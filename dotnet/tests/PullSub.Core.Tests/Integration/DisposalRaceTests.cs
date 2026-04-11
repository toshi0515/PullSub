using System;
using System.Buffers;
using System.Threading.Tasks;
using PullSub.Core;
using PullSub.Core.Tests.TestScenarios.Fixtures;

namespace PullSub.Core.Tests.Integration
{
    [Collection("PullSubRuntimeSerial")]
    public sealed class DisposalRaceTests
    {
        [Fact]
        public async Task ReceiveQueueAsync_WaitingThenDispose_CancelsWait()
        {
            var transport = new TestTransport();
            var runtime = new PullSubRuntime(transport);
            const string topic = "test/dispose/wait";

            try
            {
                await runtime.StartAsync();
                var subscriberId = await runtime.SubscribeQueueAsync(topic, new QueueOptions(8));

                using var waitCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
                var waiting = runtime.ReceiveQueueAsync(topic, subscriberId, waitCts.Token);
                await Task.Delay(20);

                var disposeTask = runtime.DisposeAsync().AsTask();
                var completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(2)));
                Assert.Same(disposeTask, completed);
                await disposeTask;

                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
            }
            finally
            {
                await runtime.DisposeAsync();
            }
        }

        [Fact]
        public async Task EmitLoop_WhileDisposing_DoesNotThrow()
        {
            var transport = new TestTransport();
            await using var runtime = new PullSubRuntime(transport);
            var codec = new SampleClassCodec();
            const string topic = "test/dispose/race";

            await runtime.StartAsync();
            await runtime.SubscribeDataAsync(topic, codec);

            var emitter = Task.Run(async () =>
            {
                for (var i = 0; i < 200; i++)
                {
                    var payload = Encode(codec, new SampleClassPayload { Value = i, Counter = i + 1 });
                    await transport.EmitMessageAsync(topic, payload);
                }
            });

            await runtime.DisposeAsync();
            await emitter;
        }

        private static byte[] Encode(IPayloadCodec<SampleClassPayload> codec, SampleClassPayload payload)
        {
            var buffer = new ArrayBufferWriter<byte>(16);
            codec.Encode(DateTime.UtcNow, payload, buffer);
            return buffer.WrittenMemory.ToArray();
        }
    }
}