using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using PullSub.Core;
using PullSub.Mqtt;
using PullSub.Tests.PerfShared;

namespace PullSub.Core.IntegrationTests
{
    public sealed class RuntimeLoopbackPerfTests
    {
        private const string BrokerHost = "127.0.0.1";
        private const int BrokerPort = 1883;
        private const int RunsPerScenario = 3;
        private const int MessagesPerRun = 300;
        private const int RawQueueDepth = 512;

        [Fact]
        public async Task DataOnly_LocalBroker_Median()
        {
            await AssertBrokerAvailableAsync();
            var results = await RunScenarioSeriesAsync(PerfScenario.DataOnly, PullSubQualityOfServiceLevel.AtLeastOnce);
            AssertAllRuns(results);
        }

        [Fact]
        public async Task RawOnly_LocalBroker_Median()
        {
            await AssertBrokerAvailableAsync();
            var results = await RunScenarioSeriesAsync(PerfScenario.RawOnly, PullSubQualityOfServiceLevel.AtLeastOnce);
            AssertAllRuns(results);
        }

        [Fact]
        public async Task Mixed_LocalBroker_Median()
        {
            await AssertBrokerAvailableAsync();
            var results = await RunScenarioSeriesAsync(PerfScenario.Mixed, PullSubQualityOfServiceLevel.AtLeastOnce);
            AssertAllRuns(results);
        }

        private static void AssertAllRuns(IReadOnlyList<PerfRunResult> results)
        {
            Assert.NotEmpty(results);
            foreach (var result in results)
            {
                Assert.True(result.ThroughputPerSecond > 0);
                Assert.True(result.P95LatencyMs >= 0);

                if (result.Scenario is PerfScenario.RawOnly or PerfScenario.Mixed)
                    Assert.True(result.RawReceived > 0);

                if (result.Scenario is PerfScenario.DataOnly or PerfScenario.Mixed)
                    Assert.True(result.DataObserved > 0);
            }
        }

        private static async Task AssertBrokerAvailableAsync()
        {
            using var tcpClient = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await tcpClient.ConnectAsync(BrokerHost, BrokerPort, cts.Token);
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException($"Local MQTT broker is required at {BrokerHost}:{BrokerPort}. {ex.Message}");
            }
        }

        private async Task<List<PerfRunResult>> RunScenarioSeriesAsync(PerfScenario scenario, PullSubQualityOfServiceLevel qos)
        {
            var runs = new List<PerfRunResult>(RunsPerScenario);
            for (var run = 1; run <= RunsPerScenario; run++)
                runs.Add(await RunScenarioAsync(scenario, run, qos));

            return runs;
        }

        private async Task<PerfRunResult> RunScenarioAsync(PerfScenario scenario, int runIndex, PullSubQualityOfServiceLevel qos)
        {
            var topic = $"pullsub/perf/{scenario.ToString().ToLowerInvariant()}/{Guid.NewGuid():N}";
            var codec = JsonPayloadCodec<Phase0PerfDataPayload>.Default;

            var options = MqttConnectionOptions.Default;
            await using var subscriberTransport = new MqttTransport(BrokerHost, BrokerPort, options);
            await using var publisherTransport = new MqttTransport(BrokerHost, BrokerPort, options);
            await using var subscriberRuntime = new PullSubRuntime(subscriberTransport);
            await using var publisherRuntime = new PullSubRuntime(publisherTransport);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var ct = cts.Token;

            await subscriberRuntime.StartAsync(ct);
            await publisherRuntime.StartAsync(ct);
            await subscriberRuntime.WaitUntilConnectedAsync(ct);
            await publisherRuntime.WaitUntilConnectedAsync(ct);

            if (scenario is PerfScenario.RawOnly or PerfScenario.Mixed)
                await subscriberRuntime.SubscribeQueueAsync(topic, new QueueOptions(RawQueueDepth), qos, ct);

            if (scenario is PerfScenario.DataOnly or PerfScenario.Mixed)
                await subscriberRuntime.SubscribeDataAsync(topic, codec, qos, ct);

            var latencies = new List<double>(MessagesPerRun);
            var rawReceived = 0;
            var dataObserved = 0;

            var rawTask = Task.CompletedTask;
            if (scenario is PerfScenario.RawOnly or PerfScenario.Mixed)
            {
                rawTask = Task.Run(async () =>
                {
                    for (var i = 0; i < MessagesPerRun; i++)
                    {
                        QueueMessage msg;
                        try
                        {
                            msg = await subscriberRuntime.ReceiveQueueAsync(topic, ct);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            break;
                        }

                        rawReceived++;

                        var sentTicks = ReadSentTicks(scenario, msg.Payload, codec);
                        latencies.Add(TicksToMs(DateTime.UtcNow.Ticks - sentTicks));
                    }
                }, ct);
            }

            var dataTask = Task.CompletedTask;
            if (scenario is PerfScenario.DataOnly or PerfScenario.Mixed)
            {
                dataTask = Task.Run(async () =>
                {
                    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                    var lastSequence = -1L;
                    while (DateTime.UtcNow < deadline && lastSequence < MessagesPerRun - 1)
                    {
                        if (subscriberRuntime.TryGetData<Phase0PerfDataPayload>(topic, out var value) && value.Sequence > lastSequence)
                        {
                            lastSequence = value.Sequence;
                            dataObserved++;
                            if (scenario == PerfScenario.DataOnly)
                                latencies.Add(TicksToMs(DateTime.UtcNow.Ticks - value.SentUtcTicks));
                        }

                        await Task.Yield();
                    }
                }, ct);
            }

            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            var sw = Stopwatch.StartNew();

            for (var i = 0; i < MessagesPerRun; i++)
            {
                if (scenario == PerfScenario.RawOnly)
                {
                    var payload = BuildRawPayload(i, DateTime.UtcNow.Ticks);
                    await publisherRuntime.PublishRawAsync(topic, payload, qos, false, ct);
                }
                else
                {
                    var payload = new Phase0PerfDataPayload
                    {
                        Sequence = i,
                        SentUtcTicks = DateTime.UtcNow.Ticks,
                        X = i,
                        Y = i * 2,
                        Z = i * 3,
                    };

                    await publisherRuntime.PublishDataAsync(topic, payload, codec, qos, false, ct);
                }
            }

            await rawTask;
            await dataTask;

            sw.Stop();
            var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
            subscriberRuntime.TryGetDroppedCount(topic, out var droppedCount);

            var throughput = MessagesPerRun / Math.Max(0.0001d, sw.Elapsed.TotalSeconds);
            var p95 = PerfMath.Percentile95(latencies);

            return new PerfRunResult(
                scenario,
                runIndex,
                MessagesPerRun,
                rawReceived,
                dataObserved,
                sw.Elapsed.TotalSeconds,
                throughput,
                p95,
                allocated,
                droppedCount);
        }

        private static byte[] BuildRawPayload(long sequence, long sentTicks)
        {
            var payload = new byte[16];
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0, 8), sequence);
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8, 8), sentTicks);
            return payload;
        }

        private static long ReadSentTicks(byte[] payload)
        {
            if (payload == null || payload.Length < 16)
                return DateTime.UtcNow.Ticks;

            return BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(8, 8));
        }

        private static long ReadSentTicks(
            PerfScenario scenario,
            byte[] payload,
            IPayloadCodec<Phase0PerfDataPayload> codec)
        {
            if (scenario == PerfScenario.RawOnly)
                return ReadSentTicks(payload);

            if (payload != null && codec.TryDecode(payload, out var decoded, out _, out _) && decoded.SentUtcTicks > 0)
                return decoded.SentUtcTicks;

            return DateTime.UtcNow.Ticks;
        }

        private static double TicksToMs(long ticks)
        {
            return ticks / (double)TimeSpan.TicksPerMillisecond;
        }
    }
}
