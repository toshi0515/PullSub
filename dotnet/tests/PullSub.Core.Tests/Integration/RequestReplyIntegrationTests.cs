using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PullSub.Core;
using PullSub.Core.Tests.TestScenarios.Fixtures;

namespace PullSub.Core.Tests.Integration
{
    [Collection("PullSubRuntimeSerial")]
    public sealed class RequestReplyIntegrationTests
    {
        [Fact]
        public async Task RequestAsync_Success_ReturnsResponse()
        {
            var transport = new TestTransport();
            var topic = PullSubRequestTopic.Create<int, int>("test/request-reply/success");

            transport.OnPublish = async (publishedTopic, payload, qos, retain, ct) =>
            {
                transport.RecordPublished(publishedTopic, payload, qos, retain);

                if (!string.Equals(publishedTopic, topic.RequestTopicName, StringComparison.Ordinal))
                    return;

                var requestCodec = new PullSubRequestEnvelopeCodec<int>(topic.RequestCodec);
                var ok = requestCodec.TryDecode(payload, out var requestEnvelope, out _, out var error);
                Assert.True(ok, error);

                var responseEnvelope = new PullSubResponseEnvelope<int>
                {
                    CorrelationId = requestEnvelope.CorrelationId,
                    Status = PullSubResponseEnvelopeStatus.Success,
                    RespondedUtc = DateTime.UtcNow,
                    Response = requestEnvelope.Request + 1,
                };

                var responseCodec = new PullSubResponseEnvelopeCodec<int>(topic.ResponseCodec);
                var responsePayload = Encode(responseCodec, responseEnvelope);

                await transport.EmitMessageAsync(requestEnvelope.ReplyTo, responsePayload);
            };

            await using var runtime = new PullSubRuntime(transport);
            await runtime.StartAsync();
            await runtime.WaitUntilConnectedAsync();

            var response = await runtime.RequestAsync(topic, 41, TimeSpan.FromSeconds(1));
            Assert.Equal(42, response);
        }

        [Fact]
        public async Task RequestAsync_Timeout_ThrowsRequestException()
        {
            var transport = new TestTransport();
            var topic = PullSubRequestTopic.Create<int, int>("test/request-reply/timeout");
            transport.OnPublish = (publishedTopic, payload, qos, retain, ct) =>
            {
                transport.RecordPublished(publishedTopic, payload, qos, retain);
                return Task.CompletedTask;
            };

            await using var runtime = new PullSubRuntime(transport);
            await runtime.StartAsync();
            await runtime.WaitUntilConnectedAsync();

            var ex = await Assert.ThrowsAsync<PullSubRequestException>(
                () => runtime.RequestAsync(topic, 1, TimeSpan.FromMilliseconds(50)));

            Assert.Equal(PullSubRequestFailureKind.Timeout, ex.FailureKind);
        }

        [Fact]
        public async Task RequestAsync_PublishFailure_ThrowsAndUpdatesDiagnostics()
        {
            var transport = new TestTransport();
            var topic = PullSubRequestTopic.Create<int, int>("test/request-reply/publish-failure");
            transport.OnPublish = (_, _, _, _, _) => throw new InvalidOperationException("publish failed");

            await using var runtime = new PullSubRuntime(transport);
            await runtime.StartAsync();
            await runtime.WaitUntilConnectedAsync();

            var ex = await Assert.ThrowsAsync<PullSubRequestException>(
                () => runtime.RequestAsync(topic, 1, TimeSpan.FromSeconds(1)));

            Assert.Equal(PullSubRequestFailureKind.PublishFailed, ex.FailureKind);

            var snapshot = runtime.GetDiagnostics().GetSnapshot();
            Assert.Equal(1, snapshot.Request.PublishFailureCount);
        }

        [Fact]
        public async Task RequestAsync_DisconnectDuringWait_FailsFast()
        {
            var transport = new TestTransport();
            var topic = PullSubRequestTopic.Create<int, int>("test/request-reply/disconnect");

            transport.OnPublish = async (publishedTopic, payload, qos, retain, ct) =>
            {
                transport.RecordPublished(publishedTopic, payload, qos, retain);

                if (!string.Equals(publishedTopic, topic.RequestTopicName, StringComparison.Ordinal))
                    return;

                await transport.DisconnectAsync(cleanDisconnect: false, CancellationToken.None);
            };

            await using var runtime = new PullSubRuntime(transport);
            await runtime.StartAsync();
            await runtime.WaitUntilConnectedAsync();

            var ex = await Assert.ThrowsAsync<PullSubRequestException>(
                () => runtime.RequestAsync(topic, 1, TimeSpan.FromSeconds(2)));

            Assert.Equal(PullSubRequestFailureKind.ConnectionLost, ex.FailureKind);
        }

        [Fact]
        public async Task RespondAsync_RuntimeResponder_ProcessesRequest()
        {
            var transport = new TestTransport();
            var topic = PullSubRequestTopic.Create<int, int>("test/request-reply/respond-runtime");

            transport.OnPublish = async (publishedTopic, payload, qos, retain, ct) =>
            {
                transport.RecordPublished(publishedTopic, payload, qos, retain);
                await transport.EmitMessageAsync(publishedTopic, payload);
            };

            await using var runtime = new PullSubRuntime(transport);
            await runtime.StartAsync();
            await runtime.WaitUntilConnectedAsync();

            await using var responder = await runtime.RespondAsync(
                topic,
                async (request, ct) =>
                {
                    await Task.Delay(1, ct);
                    return request + 5;
                });

            var response = await runtime.RequestAsync(topic, 10, TimeSpan.FromSeconds(1));
            Assert.Equal(15, response);
        }

        [Fact]
        public async Task RequestAsync_IdleRelease_ThenReconnect_ResubscribesInboxOnNextRequest()
        {
            var transport = new TestTransport();
            var topic = PullSubRequestTopic.Create<int, int>("test/request-reply/idle-reconnect");

            transport.OnPublish = async (publishedTopic, payload, qos, retain, ct) =>
            {
                transport.RecordPublished(publishedTopic, payload, qos, retain);

                if (!string.Equals(publishedTopic, topic.RequestTopicName, StringComparison.Ordinal))
                    return;

                var requestCodec = new PullSubRequestEnvelopeCodec<int>(topic.RequestCodec);
                var ok = requestCodec.TryDecode(payload, out var requestEnvelope, out _, out var error);
                Assert.True(ok, error);

                var responseEnvelope = new PullSubResponseEnvelope<int>
                {
                    CorrelationId = requestEnvelope.CorrelationId,
                    Status = PullSubResponseEnvelopeStatus.Success,
                    RespondedUtc = DateTime.UtcNow,
                    Response = requestEnvelope.Request,
                };

                var responseCodec = new PullSubResponseEnvelopeCodec<int>(topic.ResponseCodec);
                var responsePayload = Encode(responseCodec, responseEnvelope);
                await transport.EmitMessageAsync(requestEnvelope.ReplyTo, responsePayload);
            };

            var requestOptions = new PullSubRequestOptions(
                replyTopicPrefix: "pullsub/reply",
                inboxIdleTimeoutSeconds: 1,
                maxPendingRequests: 64);

            await using var runtime = new PullSubRuntime(transport, requestOptions: requestOptions);
            await runtime.StartAsync();
            await runtime.WaitUntilConnectedAsync();

            var first = await runtime.RequestAsync(topic, 7, TimeSpan.FromSeconds(1));
            Assert.Equal(7, first);

            var snapshotAfterFirst = runtime.GetDiagnostics().GetSnapshot();
            Assert.True(snapshotAfterFirst.Request.IsReplyInboxSubscribed);

            await Task.Delay(1200);
            var unsubscribeAfterIdle = transport.UnsubscribeCallCount;
            Assert.True(unsubscribeAfterIdle >= 1);

            var snapshotAfterIdle = runtime.GetDiagnostics().GetSnapshot();
            Assert.False(snapshotAfterIdle.Request.IsReplyInboxSubscribed);

            await transport.DisconnectAsync(cleanDisconnect: false, CancellationToken.None);
            await runtime.WaitUntilConnectedAsync();

            var second = await runtime.RequestAsync(topic, 8, TimeSpan.FromSeconds(1));
            Assert.Equal(8, second);

            Assert.True(transport.SubscribeCallCount >= 2);
        }

        [Fact]
        public async Task RequestAsync_ConcurrentInitialCalls_DoNotRaceInboxSetup()
        {
            var transport = new TestTransport();
            var topic = PullSubRequestTopic.Create<int, int>("test/request-reply/concurrent-init");

            transport.OnPublish = async (publishedTopic, payload, qos, retain, ct) =>
            {
                transport.RecordPublished(publishedTopic, payload, qos, retain);

                if (!string.Equals(publishedTopic, topic.RequestTopicName, StringComparison.Ordinal))
                    return;

                var requestCodec = new PullSubRequestEnvelopeCodec<int>(topic.RequestCodec);
                var ok = requestCodec.TryDecode(payload, out var requestEnvelope, out _, out var error);
                Assert.True(ok, error);

                var responseEnvelope = new PullSubResponseEnvelope<int>
                {
                    CorrelationId = requestEnvelope.CorrelationId,
                    Status = PullSubResponseEnvelopeStatus.Success,
                    RespondedUtc = DateTime.UtcNow,
                    Response = requestEnvelope.Request,
                };

                var responseCodec = new PullSubResponseEnvelopeCodec<int>(topic.ResponseCodec);
                var responsePayload = Encode(responseCodec, responseEnvelope);

                await transport.EmitMessageAsync(requestEnvelope.ReplyTo, responsePayload);
            };

            await using var runtime = new PullSubRuntime(transport);
            await runtime.StartAsync();
            await runtime.WaitUntilConnectedAsync();

            var concurrency = Math.Max(8, Environment.ProcessorCount * 2);
            var tasks = new List<Task<int>>(concurrency);
            for (var i = 0; i < concurrency; i++)
            {
                var request = i;
                tasks.Add(runtime.RequestAsync(topic, request, TimeSpan.FromSeconds(1)));
            }

            var results = await Task.WhenAll(tasks);
            Assert.Equal(concurrency, results.Length);
            for (var i = 0; i < concurrency; i++)
                Assert.Equal(i, results[i]);
        }

        [Fact]
        public async Task RespondAsync_ConvenienceHandler_PreservesReplyPublishFailureCause()
        {
            var transport = new TestTransport();
            var topic = PullSubRequestTopic.Create<int, int>("test/request-reply/reply-publish-failure-cause");

            transport.OnPublish = async (publishedTopic, payload, qos, retain, ct) =>
            {
                transport.RecordPublished(publishedTopic, payload, qos, retain);

                if (string.Equals(publishedTopic, topic.RequestTopicName, StringComparison.Ordinal))
                {
                    await transport.EmitMessageAsync(publishedTopic, payload);
                    return;
                }

                if (publishedTopic.StartsWith("pullsub/reply/", StringComparison.Ordinal))
                    throw new InvalidOperationException("reply publish failed");
            };

            await using var runtime = new PullSubRuntime(transport);
            await runtime.StartAsync();
            await runtime.WaitUntilConnectedAsync();

            var responder = await runtime.RespondAsync(
                topic,
                (request, ct) => new ValueTask<int>(request + 1));

            try
            {
                var requestException = await Assert.ThrowsAsync<PullSubRequestException>(
                    () => runtime.RequestAsync(topic, 10, TimeSpan.FromMilliseconds(150)));
                Assert.Equal(PullSubRequestFailureKind.Timeout, requestException.FailureKind);

                var completionException = await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await responder.Completion.WaitAsync(TimeSpan.FromSeconds(1)));
                Assert.Equal("reply publish failed", completionException.Message);
            }
            finally
            {
                responder.Dispose();
            }
        }

        [Fact]
        public async Task RequestAsync_DisconnectBeforePublishFailure_ClassifiesAsConnectionLost()
        {
            var transport = new TestTransport();
            var topic = PullSubRequestTopic.Create<int, int>("test/request-reply/disconnect-before-publish-failure");

            transport.OnPublish = async (publishedTopic, payload, qos, retain, ct) =>
            {
                transport.RecordPublished(publishedTopic, payload, qos, retain);

                if (!string.Equals(publishedTopic, topic.RequestTopicName, StringComparison.Ordinal))
                    return;

                await transport.DisconnectAsync(cleanDisconnect: false, CancellationToken.None);
                throw new InvalidOperationException("publish failed after disconnect");
            };

            await using var runtime = new PullSubRuntime(transport);
            await runtime.StartAsync();
            await runtime.WaitUntilConnectedAsync();

            var ex = await Assert.ThrowsAsync<PullSubRequestException>(
                () => runtime.RequestAsync(topic, 1, TimeSpan.FromSeconds(1)));

            Assert.Equal(PullSubRequestFailureKind.ConnectionLost, ex.FailureKind);

            var snapshot = runtime.GetDiagnostics().GetSnapshot();
            Assert.Equal(0, snapshot.Request.PublishFailureCount);
            Assert.Equal(1, snapshot.Request.ConnectionLostFailureCount);
        }

        [Fact]
        public async Task RespondAsync_WithRequestContext_ExposesDeadline()
        {
            var transport = new TestTransport();
            var topic = PullSubRequestTopic.Create<int, int>("test/request-reply/request-context-deadline");

            transport.OnPublish = async (publishedTopic, payload, qos, retain, ct) =>
            {
                transport.RecordPublished(publishedTopic, payload, qos, retain);
                await transport.EmitMessageAsync(publishedTopic, payload);
            };

            await using var runtime = new PullSubRuntime(transport);
            await runtime.StartAsync();
            await runtime.WaitUntilConnectedAsync();

            var sawValidDeadline = 0;
            await using var responder = await runtime.RespondAsync(
                topic,
                (request, requestContext, ct) =>
                {
                    if (requestContext.DeadlineUtc > requestContext.SentUtc
                        && !requestContext.IsExpired(DateTime.UtcNow))
                    {
                        Interlocked.Exchange(ref sawValidDeadline, 1);
                    }

                    return new ValueTask<int>(request + 1);
                });

            var response = await runtime.RequestAsync(topic, 10, TimeSpan.FromSeconds(1));
            Assert.Equal(11, response);
            Assert.Equal(1, Volatile.Read(ref sawValidDeadline));
        }

        [Fact]
        public async Task RespondAsync_ConvenienceHandler_ExpiredRequest_SkipsReplyPublish()
        {
            var transport = new TestTransport();
            var topic = PullSubRequestTopic.Create<int, int>("test/request-reply/expired-request-skip-reply");
            var replyPublishCount = 0;

            transport.OnPublish = async (publishedTopic, payload, qos, retain, ct) =>
            {
                transport.RecordPublished(publishedTopic, payload, qos, retain);

                if (string.Equals(publishedTopic, topic.RequestTopicName, StringComparison.Ordinal))
                {
                    await transport.EmitMessageAsync(publishedTopic, payload);
                    return;
                }

                if (publishedTopic.StartsWith("pullsub/reply/", StringComparison.Ordinal))
                    Interlocked.Increment(ref replyPublishCount);
            };

            await using var runtime = new PullSubRuntime(transport);
            await runtime.StartAsync();
            await runtime.WaitUntilConnectedAsync();

            await using var responder = await runtime.RespondAsync(
                topic,
                async (request, requestContext, ct) =>
                {
                    await Task.Delay(120, ct);
                    return request + 1;
                });

            var ex = await Assert.ThrowsAsync<PullSubRequestException>(
                () => runtime.RequestAsync(topic, 1, TimeSpan.FromMilliseconds(40)));
            Assert.Equal(PullSubRequestFailureKind.Timeout, ex.FailureKind);

            await Task.Delay(180);
            Assert.Equal(0, Volatile.Read(ref replyPublishCount));
        }

        [Fact]
        public async Task RespondAsync_InvalidReplyTo_DoesNotFaultResponderAndContinues()
        {
            var transport = new TestTransport();
            var topic = PullSubRequestTopic.Create<int, int>("test/request-reply/invalid-replyto-resilience");

            transport.OnPublish = async (publishedTopic, payload, qos, retain, ct) =>
            {
                transport.RecordPublished(publishedTopic, payload, qos, retain);

                if (string.Equals(publishedTopic, topic.RequestTopicName, StringComparison.Ordinal))
                {
                    var requestCodec = new PullSubRequestEnvelopeCodec<int>(topic.RequestCodec);
                    var ok = requestCodec.TryDecode(payload, out var requestEnvelope, out _, out var error);
                    Assert.True(ok, error);

                    if (requestEnvelope.Request == 1)
                        requestEnvelope.ReplyTo = "pullsub/reply/+";

                    var rewritten = Encode(requestCodec, requestEnvelope);
                    await transport.EmitMessageAsync(publishedTopic, rewritten);
                    return;
                }

                if (publishedTopic.StartsWith("pullsub/reply/", StringComparison.Ordinal))
                    await transport.EmitMessageAsync(publishedTopic, payload);
            };

            await using var runtime = new PullSubRuntime(transport);
            await runtime.StartAsync();
            await runtime.WaitUntilConnectedAsync();

            await using var responder = await runtime.RespondAsync(
                topic,
                (request, ct) => new ValueTask<int>(request + 1));

            var timeout = await Assert.ThrowsAsync<PullSubRequestException>(
                () => runtime.RequestAsync(topic, 1, TimeSpan.FromMilliseconds(80)));
            Assert.Equal(PullSubRequestFailureKind.Timeout, timeout.FailureKind);
            Assert.False(responder.Completion.IsFaulted);

            var snapshot = runtime.GetDiagnostics().GetSnapshot();
            Assert.Equal(1, snapshot.Request.InvalidReplyToDropCount);

            var response = await runtime.RequestAsync(topic, 2, TimeSpan.FromSeconds(1));
            Assert.Equal(3, response);
        }

        [Fact]
        public async Task RespondAsync_ReplyToPrefixMismatch_DropsReplyAndContinues()
        {
            var transport = new TestTransport();
            var topic = PullSubRequestTopic.Create<int, int>("test/request-reply/replyto-prefix-mismatch");

            transport.OnPublish = async (publishedTopic, payload, qos, retain, ct) =>
            {
                transport.RecordPublished(publishedTopic, payload, qos, retain);

                if (string.Equals(publishedTopic, topic.RequestTopicName, StringComparison.Ordinal))
                {
                    var requestCodec = new PullSubRequestEnvelopeCodec<int>(topic.RequestCodec);
                    var ok = requestCodec.TryDecode(payload, out var requestEnvelope, out _, out var error);
                    Assert.True(ok, error);

                    if (requestEnvelope.Request == 1)
                        requestEnvelope.ReplyTo = "other/reply/0123456789abcdef0123456789abcdef";

                    var rewritten = Encode(requestCodec, requestEnvelope);
                    await transport.EmitMessageAsync(publishedTopic, rewritten);
                    return;
                }

                if (publishedTopic.StartsWith("pullsub/reply/", StringComparison.Ordinal))
                    await transport.EmitMessageAsync(publishedTopic, payload);
            };

            await using var runtime = new PullSubRuntime(transport);
            await runtime.StartAsync();
            await runtime.WaitUntilConnectedAsync();

            await using var responder = await runtime.RespondAsync(
                topic,
                (request, ct) => new ValueTask<int>(request + 1));

            var timeout = await Assert.ThrowsAsync<PullSubRequestException>(
                () => runtime.RequestAsync(topic, 1, TimeSpan.FromMilliseconds(100)));
            Assert.Equal(PullSubRequestFailureKind.Timeout, timeout.FailureKind);
            Assert.False(responder.Completion.IsFaulted);

            var snapshot = runtime.GetDiagnostics().GetSnapshot();
            Assert.Equal(1, snapshot.Request.InvalidReplyToDropCount);

            var response = await runtime.RequestAsync(topic, 2, TimeSpan.FromSeconds(1));
            Assert.Equal(3, response);
        }

        [Fact]
        public async Task RespondAsync_ReplyToUppercaseHex_DropsReplyAndContinues()
        {
            var transport = new TestTransport();
            var topic = PullSubRequestTopic.Create<int, int>("test/request-reply/replyto-uppercase-hex");

            transport.OnPublish = async (publishedTopic, payload, qos, retain, ct) =>
            {
                transport.RecordPublished(publishedTopic, payload, qos, retain);

                if (string.Equals(publishedTopic, topic.RequestTopicName, StringComparison.Ordinal))
                {
                    var requestCodec = new PullSubRequestEnvelopeCodec<int>(topic.RequestCodec);
                    var ok = requestCodec.TryDecode(payload, out var requestEnvelope, out _, out var error);
                    Assert.True(ok, error);

                    if (requestEnvelope.Request == 1)
                        requestEnvelope.ReplyTo = "pullsub/reply/0123456789ABCDEF0123456789ABCDEF";

                    var rewritten = Encode(requestCodec, requestEnvelope);
                    await transport.EmitMessageAsync(publishedTopic, rewritten);
                    return;
                }

                if (publishedTopic.StartsWith("pullsub/reply/", StringComparison.Ordinal))
                    await transport.EmitMessageAsync(publishedTopic, payload);
            };

            await using var runtime = new PullSubRuntime(transport);
            await runtime.StartAsync();
            await runtime.WaitUntilConnectedAsync();

            await using var responder = await runtime.RespondAsync(
                topic,
                (request, ct) => new ValueTask<int>(request + 1));

            var timeout = await Assert.ThrowsAsync<PullSubRequestException>(
                () => runtime.RequestAsync(topic, 1, TimeSpan.FromMilliseconds(100)));
            Assert.Equal(PullSubRequestFailureKind.Timeout, timeout.FailureKind);
            Assert.False(responder.Completion.IsFaulted);

            var snapshot = runtime.GetDiagnostics().GetSnapshot();
            Assert.Equal(1, snapshot.Request.InvalidReplyToDropCount);

            var response = await runtime.RequestAsync(topic, 2, TimeSpan.FromSeconds(1));
            Assert.Equal(3, response);
        }

        [Fact]
        public async Task RespondAsync_InvalidRequestEnvelope_DoesNotFaultResponderAndContinues()
        {
            var transport = new TestTransport();
            var topic = PullSubRequestTopic.Create<int, int>("test/request-reply/invalid-request-envelope");
            var malformedInjected = 0;

            transport.OnPublish = async (publishedTopic, payload, qos, retain, ct) =>
            {
                transport.RecordPublished(publishedTopic, payload, qos, retain);

                if (string.Equals(publishedTopic, topic.RequestTopicName, StringComparison.Ordinal))
                {
                    if (Interlocked.CompareExchange(ref malformedInjected, 1, 0) == 0)
                    {
                        await transport.EmitMessageAsync(publishedTopic, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
                        return;
                    }

                    await transport.EmitMessageAsync(publishedTopic, payload);
                    return;
                }

                if (publishedTopic.StartsWith("pullsub/reply/", StringComparison.Ordinal))
                    await transport.EmitMessageAsync(publishedTopic, payload);
            };

            await using var runtime = new PullSubRuntime(transport);
            await runtime.StartAsync();
            await runtime.WaitUntilConnectedAsync();

            await using var responder = await runtime.RespondAsync(
                topic,
                (request, ct) => new ValueTask<int>(request + 1));

            var timeout = await Assert.ThrowsAsync<PullSubRequestException>(
                () => runtime.RequestAsync(topic, 1, TimeSpan.FromMilliseconds(120)));
            Assert.Equal(PullSubRequestFailureKind.Timeout, timeout.FailureKind);
            Assert.False(responder.Completion.IsFaulted);

            var response = await runtime.RequestAsync(topic, 2, TimeSpan.FromSeconds(1));
            Assert.Equal(3, response);
        }

        [Fact]
        public async Task RespondAsync_ConvenienceHandler_Exception_UsesFixedRemoteErrorMessage()
        {
            var transport = new TestTransport();
            var topic = PullSubRequestTopic.Create<int, int>("test/request-reply/fixed-remote-error");

            transport.OnPublish = async (publishedTopic, payload, qos, retain, ct) =>
            {
                transport.RecordPublished(publishedTopic, payload, qos, retain);
                await transport.EmitMessageAsync(publishedTopic, payload);
            };

            await using var runtime = new PullSubRuntime(transport);
            await runtime.StartAsync();
            await runtime.WaitUntilConnectedAsync();

            await using var responder = await runtime.RespondAsync(
                topic,
                (request, ct) => throw new InvalidOperationException("sensitive error detail"));

            var ex = await Assert.ThrowsAsync<PullSubRequestException>(
                () => runtime.RequestAsync(topic, 1, TimeSpan.FromSeconds(1)));

            Assert.Equal(PullSubRequestFailureKind.RemoteError, ex.FailureKind);
            Assert.Contains("Remote handler failed.", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("sensitive error detail", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task RequestAsync_CanBeCalledFromContext()
        {
            var transport = new TestTransport();
            var topic = PullSubRequestTopic.Create<int, int>("test/request-reply/context-request");

            transport.OnPublish = async (publishedTopic, payload, qos, retain, ct) =>
            {
                transport.RecordPublished(publishedTopic, payload, qos, retain);
                await transport.EmitMessageAsync(publishedTopic, payload);
            };

            await using var runtime = new PullSubRuntime(transport);
            await runtime.StartAsync();
            await runtime.WaitUntilConnectedAsync();

            await using var context = runtime.CreateContext();
            await using var responder = await context.RespondAsync(
                topic,
                (request, ct) => new ValueTask<int>(request + 2));

            var result = await context.RequestAsync(topic, 40, TimeSpan.FromSeconds(1));
            Assert.Equal(42, result);
        }

        [Fact]
        public async Task RequestAsync_WithCustomRequestTopicImplementation_RemainsCompatible()
        {
            var transport = new TestTransport();
            var topic = new CustomRequestTopic<int, int>("test/request-reply/custom-topic");

            transport.OnPublish = async (publishedTopic, payload, qos, retain, ct) =>
            {
                transport.RecordPublished(publishedTopic, payload, qos, retain);
                await transport.EmitMessageAsync(publishedTopic, payload);
            };

            await using var runtime = new PullSubRuntime(transport);
            await runtime.StartAsync();
            await runtime.WaitUntilConnectedAsync();

            await using var responder = await runtime.RespondAsync(
                topic,
                (request, ct) => new ValueTask<int>(request + 1));

            var response = await runtime.RequestAsync(topic, 10, TimeSpan.FromSeconds(1));
            Assert.Equal(11, response);
        }

        [Fact]
        public async Task RequestAsync_WithCustomBinaryCodecs_RemainsCompatible()
        {
            var transport = new TestTransport();
            var topic = PullSubRequestTopic.Create<int, int>(
                "test/request-reply/custom-binary-codecs",
                new Int32BinaryPayloadCodec(),
                new Int32BinaryPayloadCodec());

            transport.OnPublish = async (publishedTopic, payload, qos, retain, ct) =>
            {
                transport.RecordPublished(publishedTopic, payload, qos, retain);

                if (!string.Equals(publishedTopic, topic.RequestTopicName, StringComparison.Ordinal))
                    return;

                var requestCodec = new PullSubRequestEnvelopeCodec<int>(topic.RequestCodec);
                var ok = requestCodec.TryDecode(payload, out var requestEnvelope, out _, out var error);
                Assert.True(ok, error);

                var responseEnvelope = new PullSubResponseEnvelope<int>
                {
                    CorrelationId = requestEnvelope.CorrelationId,
                    Status = PullSubResponseEnvelopeStatus.Success,
                    RespondedUtc = DateTime.UtcNow,
                    Response = requestEnvelope.Request + 10,
                };

                var responseCodec = new PullSubResponseEnvelopeCodec<int>(topic.ResponseCodec);
                var responsePayload = Encode(responseCodec, responseEnvelope);
                await transport.EmitMessageAsync(requestEnvelope.ReplyTo, responsePayload);
            };

            await using var runtime = new PullSubRuntime(transport);
            await runtime.StartAsync();
            await runtime.WaitUntilConnectedAsync();

            var response = await runtime.RequestAsync(topic, 5, TimeSpan.FromSeconds(1));
            Assert.Equal(15, response);
        }

        [Fact]
        public async Task RequestAsync_ExceedingMaxPendingRequests_ThrowsTooManyPendingRequests()
        {
            var transport = new TestTransport();
            var topic = PullSubRequestTopic.Create<int, int>("test/request-reply/max-pending");

            transport.OnPublish = (publishedTopic, payload, qos, retain, ct) =>
            {
                transport.RecordPublished(publishedTopic, payload, qos, retain);
                return Task.CompletedTask;
            };

            var requestOptions = new PullSubRequestOptions(
                replyTopicPrefix: "pullsub/reply",
                inboxIdleTimeoutSeconds: 60,
                replyInboxQueueDepth: 256,
                maxPendingRequests: 1);

            await using var runtime = new PullSubRuntime(transport, requestOptions: requestOptions);
            await runtime.StartAsync();
            await runtime.WaitUntilConnectedAsync();

            using var longRequestCts = new CancellationTokenSource();
            var firstRequest = runtime.RequestAsync(topic, 1, TimeSpan.FromSeconds(10), cancellationToken: longRequestCts.Token);

            var pendingObserved = false;
            var waitDeadline = DateTime.UtcNow.AddSeconds(1);
            while (DateTime.UtcNow < waitDeadline)
            {
                if (runtime.GetDiagnostics().GetSnapshot().Request.PendingCount == 1)
                {
                    pendingObserved = true;
                    break;
                }

                await Task.Delay(10);
            }

            Assert.True(pendingObserved);

            var tooMany = await Assert.ThrowsAsync<PullSubTooManyPendingRequestsException>(
                () => runtime.RequestAsync(topic, 2, TimeSpan.FromSeconds(1)));
            Assert.Equal(PullSubRequestFailureKind.SetupFailed, tooMany.FailureKind);

            longRequestCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await firstRequest);
        }

        private static byte[] Encode<T>(IPayloadCodec<T> codec, T value)
        {
            var writer = new ArrayBufferWriter<byte>(256);
            codec.Encode(DateTime.UtcNow, value, writer);
            return writer.WrittenCount == 0
                ? Array.Empty<byte>()
                : writer.WrittenMemory.ToArray();
        }

        private sealed class Int32BinaryPayloadCodec : IPayloadCodec<int>
        {
            public void Encode(DateTime timestampUtc, int value, IBufferWriter<byte> bufferWriter)
            {
                var bytes = BitConverter.GetBytes(value);
                var span = bufferWriter.GetSpan(bytes.Length);
                bytes.AsSpan().CopyTo(span);
                bufferWriter.Advance(bytes.Length);
            }

            public bool TryDecode(ReadOnlySpan<byte> payload, out int value, out DateTime timestampUtc, out string error)
            {
                if (payload.Length != sizeof(int))
                {
                    value = default;
                    timestampUtc = default;
                    error = "payload length is invalid.";
                    return false;
                }

                value = BitConverter.ToInt32(payload.ToArray(), 0);
                timestampUtc = DateTime.UtcNow;
                error = string.Empty;
                return true;
            }
        }

        private sealed class CustomRequestTopic<TRequest, TResponse> : IPullSubRequestTopic<TRequest, TResponse>
        {
            public CustomRequestTopic(string requestTopicName)
            {
                RequestTopicName = requestTopicName;
                RequestCodec = PullSubJsonPayloadCodec<TRequest>.Default;
                ResponseCodec = PullSubJsonPayloadCodec<TResponse>.Default;
            }

            public string RequestTopicName { get; }
            public IPayloadCodec<TRequest> RequestCodec { get; }
            public IPayloadCodec<TResponse> ResponseCodec { get; }
        }
    }
}
