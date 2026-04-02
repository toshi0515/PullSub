using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using PullSub.Core;
using PullSub.Core.Tests.TestScenarios.Fixtures;

namespace PullSub.Core.Tests.Integration
{
    [Collection("PullSubRuntimeSerial")]
    public sealed class QueueStressTests
    {
        [Fact]
        public async Task QueueReceivesAllMessages_UnderBurstLoad()
        {
            var transport = new TestTransport();
            await using var runtime = new PullSubRuntime(transport);
            const string topic = "test/queue/stress";
            const int total = 1000;

            await runtime.StartAsync();
            await runtime.SubscribeQueueAsync(topic, new PullSubQueueOptions(total + 16));

            var publishTask = Task.Run(async () =>
            {
                for (var i = 0; i < total; i++)
                    await transport.EmitMessageAsync(topic, BuildPayload(i));
            });

            var received = 0;
            var lastSequence = -1;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            while (received < total)
            {
                var message = await runtime.ReceiveQueueAsync(topic, cts.Token);
                var sequence = BinaryPrimitives.ReadInt32LittleEndian(message.Payload.AsSpan(0, 4));
                Assert.True(sequence > lastSequence, $"sequence must increase. seq={sequence}, last={lastSequence}");
                lastSequence = sequence;
                received++;
            }

            await publishTask;

            Assert.Equal(total, received);
            Assert.True(runtime.TryGetDroppedCount(topic, out var dropped));
            Assert.Equal(0, dropped);
        }

        private static byte[] BuildPayload(int sequence)
        {
            var payload = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), sequence);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), sequence ^ 0x5A5A5A5A);
            return payload;
        }
    }
}