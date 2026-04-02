using System;
using System.Buffers;
using System.Threading.Tasks;
using PullSub.Core;
using PullSub.Core.Tests.TestScenarios.Fixtures;

namespace PullSub.Core.Tests.Integration
{
    [Collection("PullSubRuntimeSerial")]
    public sealed class HandleLifecycleTests
    {
        [Fact]
        public async Task DataHandle_BecomesInvalidAfterUnsubscribe()
        {
            var transport = new TestTransport();
            await using var runtime = new PullSubRuntime(transport);
            var codec = new SampleClassCodec();
            const string topic = "test/handle/lifecycle";

            await runtime.StartAsync();
            await runtime.SubscribeDataAsync(topic, codec);

            var buffer = new ArrayBufferWriter<byte>(16);
            codec.Encode(DateTime.UtcNow, new SampleClassPayload { Value = 5, Counter = 1 }, buffer);
            await transport.EmitMessageAsync(topic, buffer.WrittenMemory.ToArray());

            var handle = runtime.GetDataHandle<SampleClassPayload>(topic);
            Assert.True(handle.IsValid);
            Assert.True(handle.HasValue);

            await runtime.UnsubscribeDataAsync(topic);

            Assert.False(handle.IsValid);
            Assert.False(handle.HasValue);
            Assert.Null(handle.Value);
        }
    }
}
