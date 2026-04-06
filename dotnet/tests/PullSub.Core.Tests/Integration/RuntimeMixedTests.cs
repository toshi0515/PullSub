using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using PullSub.Core;
using PullSub.Core.Tests.TestScenarios.Fixtures;

namespace PullSub.Core.Tests.Integration
{
    [Collection("PullSubRuntimeSerial")]
    public sealed class RuntimeMixedTests
    {
        [Fact]
        public async Task MixedSubscription_DataAndQueueAreBothUpdated()
        {
            var transport = new TestTransport();
            await using var runtime = new PullSubRuntime(transport);
            var codec = new SampleClassCodec();
            const string topic = "test/mixed/data-queue";

            await runtime.StartAsync();
            await runtime.SubscribeDataAsync(topic, codec);
            await runtime.SubscribeQueueAsync(topic, new PullSubQueueOptions(8));

            var sourcePayload = Encode(codec, new SampleClassPayload { Value = 123, Counter = 7 });
            await transport.EmitMessageAsync(topic, sourcePayload);

            var handle = runtime.GetDataHandle<SampleClassPayload>(topic);
            Assert.True(handle.HasValue);
            Assert.Equal(123, handle.Value.Value);
            Assert.Equal(7, handle.Value.Counter);

            // Mutate source after emit; queue path should have copied payload already.
            Array.Clear(sourcePayload, 0, sourcePayload.Length);

            using var receiveCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var queueMessage = await runtime.ReceiveQueueAsync(topic, receiveCts.Token);
            var ok = codec.TryDecode(queueMessage.Payload, out var queued, out _, out _);

            Assert.True(ok);
            Assert.NotNull(queued);
            Assert.Equal(123, queued.Value);
            Assert.Equal(7, queued.Counter);
        }

        private static byte[] Encode(IPayloadCodec<SampleClassPayload> codec, SampleClassPayload payload)
        {
            var buffer = new ArrayBufferWriter<byte>(16);
            codec.Encode(DateTime.UtcNow, payload, buffer);
            return buffer.WrittenMemory.ToArray();
        }
    }
}