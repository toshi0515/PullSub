using System;
using System.Buffers;
using System.Linq;
using System.Threading.Tasks;
using PullSub.Core;
using PullSub.Core.Tests.TestScenarios.Fixtures;

namespace PullSub.Core.Tests.Integration
{
    [Collection("PullSubRuntimeSerial")]
    public sealed class RuntimeDataOnlyTests
    {
        [Fact]
        public async Task SubscribeDataAsync_ClassCodecWithoutInPlace_Throws()
        {
            var transport = new TestTransport();
            await using var runtime = new PullSubRuntime(transport);

            await runtime.StartAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runtime.SubscribeDataAsync("test/data/no-inplace", new SampleClassCodecWithoutInPlace()));
        }

        [Fact]
        public async Task DataHandle_UsesInPlaceLatestReference()
        {
            var transport = new TestTransport();
            await using var runtime = new PullSubRuntime(transport);
            var codec = new SampleClassCodec();
            const string topic = "test/data/inplace";

            await runtime.StartAsync();
            await runtime.SubscribeDataAsync(topic, codec);

            var firstPayload = Encode(codec, new SampleClassPayload { Value = 10, Counter = 1 });
            await transport.EmitMessageAsync(topic, firstPayload);

            var handle = runtime.GetDataHandle<SampleClassPayload>(topic);
            var firstRef = handle.Value;

            Assert.NotNull(firstRef);
            Assert.Equal(10, firstRef.Value);
            Assert.Equal(1, firstRef.Counter);

            var secondPayload = Encode(codec, new SampleClassPayload { Value = 99, Counter = 2 });
            await transport.EmitMessageAsync(topic, secondPayload);

            var secondRef = handle.Value;
            Assert.True(object.ReferenceEquals(firstRef, secondRef));
            Assert.Equal(99, secondRef.Value);
            Assert.Equal(2, secondRef.Counter);
        }

        [Fact]
        public async Task PublishDataAsync_UsesCodecAndTransportQueue()
        {
            var transport = new TestTransport();
            await using var runtime = new PullSubRuntime(transport);
            var codec = new SampleClassCodec();
            const string topic = "test/data/publish";

            await runtime.StartAsync();
            await runtime.PublishDataAsync(topic, new SampleClassPayload { Value = 7, Counter = 3 }, codec);

            Assert.Single(transport.Published);
            var published = transport.Published.Single();
            Assert.Equal(topic, published.Topic);
            Assert.Equal(8, published.Payload.Length);
        }

        private static byte[] Encode(IPayloadCodec<SampleClassPayload> codec, SampleClassPayload payload)
        {
            var buffer = new ArrayBufferWriter<byte>(16);
            codec.Encode(DateTime.UtcNow, payload, buffer);
            return buffer.WrittenMemory.ToArray();
        }
    }
}
