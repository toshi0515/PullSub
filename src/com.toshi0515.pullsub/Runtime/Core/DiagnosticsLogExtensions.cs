using System;
using System.Globalization;

namespace PullSub.Core
{
    public static class DiagnosticsLogExtensions
    {
        public static void LogSnapshot(
            this PullSubRuntime runtime,
            Action<string> log,
            int maxTopics = 128)
        {
            if (runtime == null)
                throw new ArgumentNullException(nameof(runtime));

            runtime.GetDiagnostics().LogSnapshot(log, maxTopics);
        }

        public static void LogSnapshot(
            this PullSubRuntimeDiagnostics diagnostics,
            Action<string> log,
            int maxTopics = 128)
        {
            if (diagnostics == null)
                throw new ArgumentNullException(nameof(diagnostics));

            if (log == null)
                throw new ArgumentNullException(nameof(log));

            var snapshot = diagnostics.GetSnapshot(maxTopics);
            log(string.Format(
                CultureInfo.InvariantCulture,
                "[PullSub.Diagnostics] State={0} Started={1} Connected={2} Ready={3} CapturedAtUtc={4:O} Topics={5} QueueHandlerDiagnostics={6} InboundOversizeDropped={7} ReconnectAttempt={8} ReconnectDelayMs={9} ReconnectNextRetryAtUtc={10:O} ReconnectLastFailure={11}",
                snapshot.State,
                snapshot.IsStarted,
                snapshot.IsConnected,
                snapshot.IsReady,
                snapshot.CapturedAtUtc,
                snapshot.Topics.Length,
                snapshot.HasQueueHandlerDiagnostics,
                snapshot.InboundOversizeDropCount,
                snapshot.Reconnect.AttemptCount,
                snapshot.Reconnect.CurrentDelay.TotalMilliseconds,
                snapshot.Reconnect.NextRetryAtUtc,
                snapshot.Reconnect.LastFailureReason));

            for (var i = 0; i < snapshot.Topics.Length; i++)
            {
                var topic = snapshot.Topics[i];
                log(string.Format(
                    CultureInfo.InvariantCulture,
                    "[PullSub.Diagnostics] Topic={0} DataSubs={1} QueueSubs={2} HasValue={3} DataReceiveCount={4} QueueDropped={5} QoS={6}",
                    topic.Topic,
                    topic.DataSubscriberCount,
                    topic.QueueSubscriberCount,
                    topic.HasValue,
                    topic.DataReceiveCount,
                    topic.QueueDroppedCount,
                    topic.SubscribeQos));
            }
        }
    }
}
