using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PullSub.Core;
using PullSub.Core.Tests.TestScenarios.Fixtures;

namespace PullSub.Core.Tests.Integration
{
    [Collection("PullSubRuntimeSerial")]
    public sealed class RuntimeDiagnosticsTests
    {
        [Fact]
        public async Task GetSnapshot_ReportsDataTopicMetrics()
        {
            var transport = new TestTransport();
            await using var runtime = new PullSubRuntime(transport);
            var codec = new SampleClassCodec();
            const string topic = "test/diagnostics/data";

            await runtime.StartAsync();
            await runtime.WaitUntilConnectedAsync();
            await runtime.SubscribeDataAsync(topic, codec);

            await transport.EmitMessageAsync(topic, Encode(codec, new SampleClassPayload { Value = 1, Counter = 1 }));
            await transport.EmitMessageAsync(topic, Encode(codec, new SampleClassPayload { Value = 2, Counter = 2 }));

            var snapshot = runtime.GetDiagnostics().GetSnapshot();
            var topicMetric = Assert.Single(snapshot.Topics.Where(x => x.Topic == topic));

            Assert.Equal(PullSubState.Ready, snapshot.State);
            Assert.True(snapshot.IsStarted);
            Assert.True(snapshot.IsConnected);
            Assert.True(snapshot.IsReady);
            Assert.False(snapshot.HasQueueHandlerDiagnostics);

            Assert.Equal(1, topicMetric.DataSubscriberCount);
            Assert.Equal(0, topicMetric.QueueSubscriberCount);
            Assert.True(topicMetric.HasValue);
            Assert.True(topicMetric.DataReceiveCount >= 2);
            Assert.Equal(0, topicMetric.QueueDroppedCount);
        }

        [Fact]
        public async Task GetSnapshot_MaxTopics_LimitsResultCount()
        {
            var transport = new TestTransport();
            await using var runtime = new PullSubRuntime(transport);
            var codec = new SampleClassCodec();

            await runtime.StartAsync();
            await runtime.WaitUntilConnectedAsync();
            await runtime.SubscribeDataAsync("test/diagnostics/topic-a", codec);
            await runtime.SubscribeDataAsync("test/diagnostics/topic-b", codec);

            var snapshot = runtime.GetDiagnostics().GetSnapshot(maxTopics: 1);
            Assert.Single(snapshot.Topics);
        }

        [Fact]
        public async Task LogSnapshot_WritesHeaderAndTopicLines()
        {
            var transport = new TestTransport();
            await using var runtime = new PullSubRuntime(transport);
            var codec = new SampleClassCodec();
            var lines = new List<string>();

            await runtime.StartAsync();
            await runtime.WaitUntilConnectedAsync();
            await runtime.SubscribeDataAsync("test/diagnostics/log", codec);

            runtime.LogSnapshot(lines.Add);

            Assert.NotEmpty(lines);
            Assert.StartsWith("[PullSub.Diagnostics] State=", lines[0], StringComparison.Ordinal);
            Assert.Contains(lines, line => line.Contains("Topic=test/diagnostics/log", StringComparison.Ordinal));
        }

        [Fact]
        public async Task GetSnapshot_ReportsInboundOversizeDrops()
        {
            var transport = new TestTransport();
            await using var runtime = new PullSubRuntime(
                transport,
                runtimeOptions: new PullSubRuntimeOptions(maxInboundPayloadBytes: 8));

            const string topic = "test/diagnostics/oversize";

            await runtime.StartAsync();
            await runtime.WaitUntilConnectedAsync();
            await runtime.SubscribeQueueAsync(topic, PullSubQueueOptions.Default);

            await transport.EmitMessageAsync(topic, new byte[32]);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => runtime.ReceiveQueueAsync(topic, cts.Token));

            var snapshot = runtime.GetDiagnostics().GetSnapshot();
            Assert.True(snapshot.InboundOversizeDropCount >= 1);
        }

        private static byte[] Encode(IPayloadCodec<SampleClassPayload> codec, SampleClassPayload payload)
        {
            var buffer = new ArrayBufferWriter<byte>(16);
            codec.Encode(DateTime.UtcNow, payload, buffer);
            return buffer.WrittenMemory.ToArray();
        }
    }
}
