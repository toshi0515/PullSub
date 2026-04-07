using System;
using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PullSub.Core;
using PullSub.Core.Tests.TestScenarios.Fixtures;

namespace PullSub.Core.Tests.Integration
{
    [Collection("PullSubRuntimeSerial")]
    public sealed class RequestContractTests
    {
        [Fact]
        public void RequestTopic_Create_UsesTypedCodecs()
        {
            var topic = RequestTopic.Create<SampleClassPayload, int>("test/request/topic");

            Assert.Equal("test/request/topic", topic.RequestTopicName);
            Assert.IsType<JsonPayloadCodec<SampleClassPayload>>(topic.RequestCodec);
            Assert.IsType<JsonPayloadCodec<int>>(topic.ResponseCodec);
        }

        [Fact]
        public void RequestTopic_Create_CachesEnvelopeCodecAssets()
        {
            var topic = RequestTopic.Create<SampleClassPayload, int>("test/request/topic/cache");
            var internalTopic = Assert.IsAssignableFrom<IRequestTopicInternal<SampleClassPayload, int>>(topic);

            Assert.Equal(topic.RequestTopicName, internalTopic.RequestEnvelopeTopic.TopicName);
            Assert.Same(internalTopic.RequestEnvelopeCodec, internalTopic.RequestEnvelopeTopic.Codec);

            var envelope = new RequestEnvelope<SampleClassPayload>
            {
                CorrelationId = "corr-cache",
                ReplyTo = "pullsub/reply/runtime-cache",
                SentUtc = DateTime.UtcNow,
                DeadlineUtc = DateTime.UtcNow.AddSeconds(3),
                Request = new SampleClassPayload { Value = 1, Counter = 2 },
            };

            var writer = new ArrayBufferWriter<byte>(256);
            internalTopic.RequestEnvelopeCodec.Encode(envelope.SentUtc, envelope, writer);

            var ok = internalTopic.RequestEnvelopeTopic.Codec.TryDecode(writer.WrittenSpan, out var decoded, out _, out var error);
            Assert.True(ok, error);
            Assert.Equal(envelope.CorrelationId, decoded.CorrelationId);
            Assert.Equal(envelope.ReplyTo, decoded.ReplyTo);
        }

        [Fact]
        public async Task ReplySender_IsOneShot()
        {
            var sendCount = 0;
            var sender = new ReplySender<int>(
                "corr-1",
                (_, _) =>
                {
                    Interlocked.Increment(ref sendCount);
                    return Task.CompletedTask;
                });

            await sender.SendAsync(123);

            await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(456));
            Assert.Equal(1, Volatile.Read(ref sendCount));
        }

        [Fact]
        public async Task Runtime_CanReceiveRequestOptionsFromConstructor()
        {
            var transport = new TestTransport();
            var options = new RequestOptions(
                replyTopicPrefix: "custom/reply",
                inboxIdleTimeoutSeconds: 30,
                replyInboxQueueDepth: 512,
                maxPendingRequests: 42);

            await using var runtime = new PullSubRuntime(transport, requestOptions: options);

            Assert.Equal("custom/reply", runtime.RequestOptions.ReplyTopicPrefix);
            Assert.Equal(TimeSpan.FromSeconds(30), runtime.RequestOptions.InboxIdleTimeout);
            Assert.Equal(512, runtime.RequestOptions.ReplyInboxQueueDepth);
            Assert.Equal(42, runtime.RequestOptions.MaxPendingRequests);
        }

        [Fact]
        public void RequestEnvelopeCodec_RoundTrip_PreservesCoreFields()
        {
            var now = DateTime.UtcNow;
            var envelope = new RequestEnvelope<SampleClassPayload>
            {
                CorrelationId = "corr-1",
                ReplyTo = "pullsub/reply/runtime-1",
                SentUtc = now,
                DeadlineUtc = now.AddSeconds(5),
                Request = new SampleClassPayload { Value = 10, Counter = 20 },
            };

            var codec = RequestEnvelopeCodec<SampleClassPayload>.Default;
            var writer = new ArrayBufferWriter<byte>(256);
            codec.Encode(now, envelope, writer);

            var ok = codec.TryDecode(
                writer.WrittenSpan,
                out var decoded,
                out var decodedTimestamp,
                out var error);

            Assert.True(ok, error);
            Assert.Equal(envelope.CorrelationId, decoded.CorrelationId);
            Assert.Equal(envelope.ReplyTo, decoded.ReplyTo);
            Assert.Equal(envelope.Request.Value, decoded.Request.Value);
            Assert.Equal(envelope.Request.Counter, decoded.Request.Counter);
            Assert.Equal(envelope.SentUtc, decodedTimestamp);
        }

        [Fact]
        public void RequestEnvelopeCodec_DefaultJson_EncodesInlineRequestField()
        {
            var now = DateTime.UtcNow;
            var envelope = new RequestEnvelope<SampleClassPayload>
            {
                CorrelationId = "corr-inline-request",
                ReplyTo = "pullsub/reply/runtime-inline",
                SentUtc = now,
                DeadlineUtc = now.AddSeconds(3),
                Request = new SampleClassPayload { Value = 12, Counter = 34 },
            };

            var codec = RequestEnvelopeCodec<SampleClassPayload>.Default;
            var writer = new ArrayBufferWriter<byte>(256);
            codec.Encode(now, envelope, writer);

            using var document = JsonDocument.Parse(writer.WrittenMemory);
            var root = document.RootElement;

            Assert.True(root.TryGetProperty("request", out var requestElement));
            Assert.False(root.TryGetProperty("requestPayload", out _));

            var decodedRequest = requestElement.Deserialize<SampleClassPayload>();
            Assert.NotNull(decodedRequest);
            Assert.Equal(12, decodedRequest.Value);
            Assert.Equal(34, decodedRequest.Counter);
        }

        [Fact]
        public void RequestEnvelopeCodec_DecodeWithoutDeadline_LeavesDeadlineUnset()
        {
            var now = DateTime.UtcNow;
            var json = $"{{\"correlationId\":\"corr-no-deadline\",\"replyTo\":\"pullsub/reply/runtime-1\",\"sentUtc\":\"{now:O}\",\"request\":123}}";

            var codec = RequestEnvelopeCodec<int>.Default;
            var ok = codec.TryDecode(Encoding.UTF8.GetBytes(json), out var decoded, out _, out var error);

            Assert.True(ok, error);
            Assert.Equal(default, decoded.DeadlineUtc);

            var context = new RequestContext(decoded.CorrelationId, decoded.ReplyTo, decoded.SentUtc, decoded.DeadlineUtc);
            Assert.False(context.IsExpired(DateTime.UtcNow));
        }

        [Fact]
        public void RequestEnvelopeCodec_DecodeLegacyRequestPayload_IsRejected()
        {
            var now = DateTime.UtcNow;
            var json = $"{{\"correlationId\":\"corr-legacy-request\",\"replyTo\":\"pullsub/reply/runtime-1\",\"sentUtc\":\"{now:O}\",\"requestPayload\":\"AQID\"}}";

            var codec = RequestEnvelopeCodec<int>.Default;
            var ok = codec.TryDecode(Encoding.UTF8.GetBytes(json), out _, out _, out var error);

            Assert.False(ok);
            Assert.Equal("request is required.", error);
        }

        [Fact]
        public void RequestEnvelopeCodec_CustomCodec_UsesRequestPayloadAndRoundTrips()
        {
            var now = DateTime.UtcNow;
            var envelope = new RequestEnvelope<int>
            {
                CorrelationId = "corr-custom-request",
                ReplyTo = "pullsub/reply/runtime-custom",
                SentUtc = now,
                DeadlineUtc = now.AddSeconds(5),
                Request = 321,
            };

            var codec = new RequestEnvelopeCodec<int>(new Int32BinaryPayloadCodec());
            var writer = new ArrayBufferWriter<byte>(256);
            codec.Encode(now, envelope, writer);

            using (var document = JsonDocument.Parse(writer.WrittenMemory))
            {
                var root = document.RootElement;
                Assert.False(root.TryGetProperty("request", out _));
                Assert.True(root.TryGetProperty("requestPayload", out var payloadElement));
                Assert.False(string.IsNullOrWhiteSpace(payloadElement.GetString()));
            }

            var ok = codec.TryDecode(writer.WrittenSpan, out var decoded, out var decodedTimestamp, out var error);
            Assert.True(ok, error);
            Assert.Equal(321, decoded.Request);
            Assert.Equal(now, decodedTimestamp);
        }

        [Fact]
        public void ResponseEnvelopeCodec_DefaultJson_EncodesInlineResponseField()
        {
            var now = DateTime.UtcNow;
            var envelope = new ResponseEnvelope<int>
            {
                CorrelationId = "corr-inline-response",
                Status = ResponseEnvelopeStatus.Success,
                ErrorMessage = string.Empty,
                RespondedUtc = now,
                Response = 42,
            };

            var codec = ResponseEnvelopeCodec<int>.Default;
            var writer = new ArrayBufferWriter<byte>(256);
            codec.Encode(now, envelope, writer);

            using var document = JsonDocument.Parse(writer.WrittenMemory);
            var root = document.RootElement;

            Assert.True(root.TryGetProperty("response", out var responseElement));
            Assert.False(root.TryGetProperty("responsePayload", out _));
            Assert.Equal(42, responseElement.GetInt32());
        }

        [Fact]
        public void ResponseEnvelopeCodec_DecodeLegacyPayload_IsRejected()
        {
            var now = DateTime.UtcNow;
            var json = $"{{\"correlationId\":\"corr-legacy-response\",\"status\":0,\"errorMessage\":\"\",\"respondedUtc\":\"{now:O}\",\"responsePayload\":\"AQID\"}}";

            var codec = ResponseEnvelopeCodec<int>.Default;
            var ok = codec.TryDecode(Encoding.UTF8.GetBytes(json), out _, out _, out var error);

            Assert.False(ok);
            Assert.Equal("response is required.", error);
        }

        [Fact]
        public void ResponseEnvelopeCodec_CustomCodec_UsesResponsePayloadAndRoundTrips()
        {
            var now = DateTime.UtcNow;
            var envelope = new ResponseEnvelope<int>
            {
                CorrelationId = "corr-custom-response",
                Status = ResponseEnvelopeStatus.Success,
                ErrorMessage = string.Empty,
                RespondedUtc = now,
                Response = 654,
            };

            var codec = new ResponseEnvelopeCodec<int>(new Int32BinaryPayloadCodec());
            var writer = new ArrayBufferWriter<byte>(256);
            codec.Encode(now, envelope, writer);

            using (var document = JsonDocument.Parse(writer.WrittenMemory))
            {
                var root = document.RootElement;
                Assert.False(root.TryGetProperty("response", out _));
                Assert.True(root.TryGetProperty("responsePayload", out var payloadElement));
                Assert.False(string.IsNullOrWhiteSpace(payloadElement.GetString()));
            }

            var ok = codec.TryDecode(writer.WrittenSpan, out var decoded, out var decodedTimestamp, out var error);
            Assert.True(ok, error);
            Assert.Equal(654, decoded.Response);
            Assert.Equal(now, decodedTimestamp);
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
    }
}
