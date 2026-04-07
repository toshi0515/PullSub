using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using PullSub.Core;
using PullSub.Core.Tests.TestScenarios.Fixtures;

namespace PullSub.Core.Tests.Integration
{
    [Collection("PullSubRuntimeSerial")]
    public sealed class RuntimeQueueOnlyTests
    {
        [Fact]
        public async Task QueueReceivesInOrder_AndTracksDroppedCount()
        {
            var transport = new TestTransport();
            await using var runtime = new PullSubRuntime(transport);
            const string topic = "test/queue/order";

            await runtime.StartAsync();
            await runtime.SubscribeQueueAsync(topic, new QueueOptions(1));

            await transport.EmitMessageAsync(topic, BuildPayload(1));
            await transport.EmitMessageAsync(topic, BuildPayload(2));

            using var receiveCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var msg = await runtime.ReceiveQueueAsync(topic, receiveCts.Token);
            var sequence = BinaryPrimitives.ReadInt32LittleEndian(msg.Payload.AsSpan(0, 4));

            Assert.Equal(2, sequence);
            Assert.True(runtime.TryGetDroppedCount(topic, out var dropped));
            Assert.Equal(1, dropped);
        }

        private static byte[] BuildPayload(int sequence)
        {
            var payload = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), sequence);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), sequence * 10);
            return payload;
        }
    }
}
