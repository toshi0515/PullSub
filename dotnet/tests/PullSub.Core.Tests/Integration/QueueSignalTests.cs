using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using PullSub.Core;
using PullSub.Core.Tests.TestScenarios.Fixtures;

namespace PullSub.Core.Tests.Integration
{
    [Collection("PullSubRuntimeSerial")]
    public sealed class QueueSignalTests
    {
        [Fact]
        public async Task ReceiveQueueAsync_PendingWait_ThenTwoMessages_AreBothDeliveredInOrder()
        {
            var transport = new TestTransport();
            await using var runtime = new PullSubRuntime(transport);
            const string topic = "test/queue/signal-order";

            await runtime.StartAsync();
            await runtime.SubscribeQueueAsync(topic, new QueueOptions(8));

            using var receiveCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var firstWait = runtime.ReceiveQueueAsync(topic, receiveCts.Token);
            await Task.Delay(20);

            await transport.EmitMessageAsync(topic, BuildPayload(1));
            await transport.EmitMessageAsync(topic, BuildPayload(2));

            var first = await firstWait;
            var second = await runtime.ReceiveQueueAsync(topic, receiveCts.Token);

            Assert.Equal(1, ReadSequence(first.Payload));
            Assert.Equal(2, ReadSequence(second.Payload));
        }

        [Fact]
        public async Task ReceiveQueueAsync_CancellationToken_IsObserved()
        {
            var transport = new TestTransport();
            await using var runtime = new PullSubRuntime(transport);
            const string topic = "test/queue/signal-cancel";

            await runtime.StartAsync();
            await runtime.SubscribeQueueAsync(topic, new QueueOptions(8));

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await runtime.ReceiveQueueAsync(topic, cts.Token));
        }

        private static byte[] BuildPayload(int sequence)
        {
            var payload = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), sequence);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), sequence * 10);
            return payload;
        }

        private static int ReadSequence(byte[] payload)
        {
            if (payload == null || payload.Length < 4)
                return -1;

            return BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
        }
    }
}