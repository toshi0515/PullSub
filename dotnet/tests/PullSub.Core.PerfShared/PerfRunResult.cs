using System.Collections.Generic;

namespace PullSub.Tests.PerfShared
{
    public sealed class PerfRunResult
    {
        public PerfRunResult(
            PerfScenario scenario,
            int runIndex,
            int published,
            int rawReceived,
            int dataObserved,
            double elapsedSeconds,
            double throughputPerSecond,
            double p95LatencyMs,
            long allocatedBytes,
            long rawDroppedCount)
        {
            Scenario = scenario;
            RunIndex = runIndex;
            Published = published;
            RawReceived = rawReceived;
            DataObserved = dataObserved;
            ElapsedSeconds = elapsedSeconds;
            ThroughputPerSecond = throughputPerSecond;
            P95LatencyMs = p95LatencyMs;
            AllocatedBytes = allocatedBytes;
            RawDroppedCount = rawDroppedCount;
        }

        public PerfScenario Scenario { get; }
        public int RunIndex { get; }
        public int Published { get; }
        public int RawReceived { get; }
        public int DataObserved { get; }
        public double ElapsedSeconds { get; }
        public double ThroughputPerSecond { get; }
        public double P95LatencyMs { get; }
        public long AllocatedBytes { get; }
        public long RawDroppedCount { get; }

        public string ToLogString()
        {
            return $"{Scenario} run {RunIndex}: th={ThroughputPerSecond:F1} msg/s, p95={P95LatencyMs:F2} ms, alloc={AllocatedBytes} B, rawDropped={RawDroppedCount}";
        }

        public static string BuildMedianSummary(IReadOnlyList<PerfRunResult> results)
        {
            if (results == null || results.Count == 0)
                return "(no data)";

            var throughput = new List<double>(results.Count);
            var p95 = new List<double>(results.Count);
            var allocated = new List<long>(results.Count);
            long rawDroppedTotal = 0;

            for (var i = 0; i < results.Count; i++)
            {
                throughput.Add(results[i].ThroughputPerSecond);
                p95.Add(results[i].P95LatencyMs);
                allocated.Add(results[i].AllocatedBytes);
                rawDroppedTotal += results[i].RawDroppedCount;
            }

            throughput.Sort();
            p95.Sort();
            allocated.Sort();

            var mid = results.Count / 2;
            return $"throughput={throughput[mid]:F1} msg/s, p95={p95[mid]:F2} ms, allocMedian={allocated[mid]} B, rawDroppedMean={rawDroppedTotal / (double)results.Count:F1}";
        }
    }
}
