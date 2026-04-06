using System;
using System.Threading;

namespace PullSub.Core
{
    public sealed class PullSubRuntimeDiagnostics
    {
        private readonly PullSubRuntime _runtime;

        internal PullSubRuntimeDiagnostics(PullSubRuntime runtime)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        }

        public PullSubRuntimeDiagnosticsSnapshot GetSnapshot(int maxTopics = int.MaxValue)
        {
            if (maxTopics < 0)
                throw new ArgumentOutOfRangeException(nameof(maxTopics));

            if (_runtime.State == PullSubState.Disposed)
                throw new ObjectDisposedException(nameof(PullSubRuntime));

            var snapshot = _runtime.GetDebugSnapshot();
            var sourceTopics = snapshot.Topics;
            var topicCount = sourceTopics.Length;
            if (maxTopics < topicCount)
                topicCount = maxTopics;

            var topics = new PullSubTopicDiagnostics[topicCount];
            for (var i = 0; i < topicCount; i++)
            {
                var topic = sourceTopics[i];
                topics[i] = new PullSubTopicDiagnostics(
                    topic: topic.Topic,
                    dataSubscriberCount: topic.DataSubCount,
                    queueSubscriberCount: topic.QueueSubCount,
                    subscribeQos: topic.SubscribeQos,
                    hasValue: topic.HasValue,
                    latestValueTimestampUtc: topic.LatestValueTimestampUtc,
                    dataReceiveCount: topic.DataReceiveCount,
                    queueDroppedCount: topic.QueueDroppedCount,
                    activeQueueHandlers: topic.ActiveQueueHandlers,
                    faultedQueueHandlers: topic.FaultedQueueHandlers,
                    lastQueueHandlerFaultUtc: topic.LastQueueHandlerFaultUtc);
            }

            return new PullSubRuntimeDiagnosticsSnapshot(
                state: snapshot.State,
                isStarted: snapshot.IsStarted,
                isConnected: snapshot.IsConnected,
                isReady: snapshot.IsReady,
                capturedAtUtc: snapshot.CapturedAtUtc,
                hasQueueHandlerDiagnostics: snapshot.HasQueueHandlerDiagnostics,
                reconnect: new PullSubReconnectDiagnostics(
                    attemptCount: snapshot.ReconnectAttemptCount,
                    currentDelay: snapshot.ReconnectCurrentDelay,
                    nextRetryAtUtc: snapshot.ReconnectNextRetryAtUtc,
                    lastFailureReason: snapshot.ReconnectLastFailureReason),
                topics: topics);
        }
    }

    public sealed partial class PullSubRuntime
    {
        private PullSubRuntimeDiagnostics _diagnostics;

        public PullSubRuntimeDiagnostics GetDiagnostics()
        {
            ThrowIfDisposed();

            var existing = Volatile.Read(ref _diagnostics);
            if (existing != null)
                return existing;

            var created = new PullSubRuntimeDiagnostics(this);
            var original = Interlocked.CompareExchange(ref _diagnostics, created, null);
            return original ?? created;
        }
    }
}
