using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PullSub.Core;
using PullSub.Core.Tests.TestScenarios.Fixtures;

namespace PullSub.Core.Tests.Integration
{
    [Collection("PullSubRuntimeSerial")]
    public sealed class PublisherObserverTests
    {
        [Fact]
        public async Task ToPublisher_PreservesPublishOrder_InSerializedMode()
        {
            var transport = new TestTransport();
            await using var runtime = new PullSubRuntime(transport);
            var topic = PullSubTopic.Create<int>("test/publisher/order");

            transport.OnPublish = async (topicName, payload, qos, retain, _) =>
            {
                DecodeOrThrow(topic, payload, out var value);

                // Intentionally invert per-item transport latency to verify observer-level serialization.
                await Task.Delay(value == 1 ? 80 : 5);
                transport.RecordPublished(topicName, payload, qos, retain);
            };

            await runtime.StartAsync();
            var observer = runtime.ToPublisher(topic);

            observer.OnNext(1);
            observer.OnNext(2);

            await WaitUntilAsync(() => transport.Published.Count >= 2);

            var publishedValues = transport.Published
                .Select(x => DecodeOrThrow(topic, x.Payload, out var value) ? value : 0)
                .ToArray();

            Assert.Equal(new[] { 1, 2 }, publishedValues);
        }

        [Fact]
        public async Task ToPublisher_OnCompleted_DropsPendingOnNext()
        {
            var transport = new TestTransport();
            await using var runtime = new PullSubRuntime(transport);
            var topic = PullSubTopic.Create<int>("test/publisher/completion");
            var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var enqueueCount = 0;

            transport.OnPublish = async (topicName, payload, qos, retain, _) =>
            {
                var index = Interlocked.Increment(ref enqueueCount);
                if (index == 1)
                {
                    firstStarted.TrySetResult(true);
                    await releaseFirst.Task;
                }

                transport.RecordPublished(topicName, payload, qos, retain);
            };

            await runtime.StartAsync();
            var observer = runtime.ToPublisher(topic);

            observer.OnNext(10);
            await firstStarted.Task;

            observer.OnNext(20);
            observer.OnCompleted();

            releaseFirst.TrySetResult(true);

            await WaitUntilAsync(() => transport.Published.Count >= 1);
            await Task.Delay(30);

            Assert.Single(transport.Published);
            Assert.True(DecodeOrThrow(topic, transport.Published.Single().Payload, out var onlyValue));
            Assert.Equal(10, onlyValue);
        }

        [Fact]
        public async Task ToPublisher_PublishFailure_NotifiesOnErrorOnce_AndStops()
        {
            var transport = new TestTransport();
            await using var runtime = new PullSubRuntime(transport);
            var topic = PullSubTopic.Create<int>("test/publisher/error");
            var errors = new List<Exception>();

            transport.OnPublish = (_, _, _, _, _) => throw new InvalidOperationException("publish failed");

            await runtime.StartAsync();
            var observer = runtime.ToPublisher(topic, onError: ex => errors.Add(ex));

            observer.OnNext(1);
            observer.OnNext(2);

            await WaitUntilAsync(() => errors.Count >= 1);

            observer.OnNext(3);
            observer.OnError(new Exception("source failure"));
            await Task.Delay(30);

            Assert.Single(errors);
            Assert.IsType<InvalidOperationException>(errors[0]);
        }

        [Fact]
        public async Task ToPublisher_PublishFailure_WithoutOnError_ReportsToRuntimeLogger()
        {
            var transport = new TestTransport();
            var loggedErrors = new List<Exception>();
            await using var runtime = new PullSubRuntime(
                transport,
                logException: ex => loggedErrors.Add(ex));

            var topic = PullSubTopic.Create<int>("test/publisher/error/logging");
            transport.OnPublish = (_, _, _, _, _) => throw new InvalidOperationException("publish failed");

            await runtime.StartAsync();
            var observer = runtime.ToPublisher(topic);

            observer.OnNext(1);
            observer.OnNext(2);

            await WaitUntilAsync(() => loggedErrors.Count >= 1);

            Assert.Single(loggedErrors);
            Assert.IsType<InvalidOperationException>(loggedErrors[0]);
        }

        [Fact]
        public async Task ToPublisher_OnErrorCallbackThrows_ReportsBothOriginalAndCallbackErrors()
        {
            var transport = new TestTransport();
            var loggedErrors = new List<Exception>();
            await using var runtime = new PullSubRuntime(
                transport,
                logException: ex => loggedErrors.Add(ex));

            var topic = PullSubTopic.Create<int>("test/publisher/error/callback-throws");
            transport.OnPublish = (_, _, _, _, _) => throw new InvalidOperationException("publish failed");

            await runtime.StartAsync();
            var observer = runtime.ToPublisher(
                topic,
                onError: _ => throw new ApplicationException("callback failed"));

            observer.OnNext(1);

            await WaitUntilAsync(() => loggedErrors.Count >= 2);

            Assert.Contains(loggedErrors, ex => ex is InvalidOperationException);
            Assert.Contains(loggedErrors, ex => ex is ApplicationException);
        }

        [Fact]
        public async Task ToPublisher_RuntimeDisposed_DoesNotNotifyOnError()
        {
            var transport = new TestTransport();
            var runtime = new PullSubRuntime(transport);
            var topic = PullSubTopic.Create<int>("test/publisher/disposed");
            var errorCount = 0;

            await runtime.StartAsync();
            var observer = runtime.ToPublisher(topic, onError: _ => Interlocked.Increment(ref errorCount));

            await runtime.DisposeAsync();

            observer.OnNext(100);
            await Task.Delay(30);

            Assert.Equal(0, Volatile.Read(ref errorCount));
            Assert.Empty(transport.Published);
        }

        private static bool DecodeOrThrow(IPullSubTopic<int> topic, byte[] payload, out int value)
        {
            if (!topic.Codec.TryDecode(payload, out value, out _, out var error))
                throw new InvalidOperationException($"Failed to decode payload: {error}");

            return true;
        }

        private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
        {
            var start = Environment.TickCount;
            while (!condition())
            {
                if (Environment.TickCount - start > timeoutMs)
                    throw new TimeoutException("Condition was not satisfied within the timeout.");

                await Task.Delay(10);
            }
        }
    }
}