using System;
using System.Collections.Generic;

namespace PullSub.Core
{
    internal readonly struct PullSubRuntimeTopicDebugSnapshot
    {
        public PullSubRuntimeTopicDebugSnapshot(
            string topic,
            int dataSubCount,
            int queueSubCount,
            PullSubQualityOfServiceLevel subscribeQos,
            bool hasValue,
            DateTime latestValueTimestampUtc,
            long dataReceiveCount,
            long queueDroppedCount,
            int activeQueueHandlers,
            int faultedQueueHandlers,
            DateTime lastQueueHandlerFaultUtc)
        {
            Topic = topic;
            DataSubCount = dataSubCount;
            QueueSubCount = queueSubCount;
            SubscribeQos = subscribeQos;
            HasValue = hasValue;
            LatestValueTimestampUtc = latestValueTimestampUtc;
            DataReceiveCount = dataReceiveCount;
            QueueDroppedCount = queueDroppedCount;
            ActiveQueueHandlers = activeQueueHandlers;
            FaultedQueueHandlers = faultedQueueHandlers;
            LastQueueHandlerFaultUtc = lastQueueHandlerFaultUtc;
        }

        public string Topic { get; }
        public int DataSubCount { get; }
        public int QueueSubCount { get; }
        public PullSubQualityOfServiceLevel SubscribeQos { get; }
        public bool HasValue { get; }
        public DateTime LatestValueTimestampUtc { get; }
        public long DataReceiveCount { get; }
        public long QueueDroppedCount { get; }
        public int ActiveQueueHandlers { get; }
        public int FaultedQueueHandlers { get; }
        public DateTime LastQueueHandlerFaultUtc { get; }
    }

    internal readonly struct PullSubRuntimeDebugSnapshot
    {
        public PullSubRuntimeDebugSnapshot(
            PullSubState state,
            bool isStarted,
            bool isConnected,
            bool isReady,
            DateTime capturedAtUtc,
            bool hasQueueHandlerDiagnostics,
            int reconnectAttemptCount,
            TimeSpan reconnectCurrentDelay,
            DateTime reconnectNextRetryAtUtc,
            string reconnectLastFailureReason,
            PullSubRuntimeTopicDebugSnapshot[] topics)
        {
            State = state;
            IsStarted = isStarted;
            IsConnected = isConnected;
            IsReady = isReady;
            CapturedAtUtc = capturedAtUtc;
            HasQueueHandlerDiagnostics = hasQueueHandlerDiagnostics;
            ReconnectAttemptCount = reconnectAttemptCount;
            ReconnectCurrentDelay = reconnectCurrentDelay;
            ReconnectNextRetryAtUtc = reconnectNextRetryAtUtc;
            ReconnectLastFailureReason = reconnectLastFailureReason;
            Topics = topics;
        }

        public PullSubState State { get; }
        public bool IsStarted { get; }
        public bool IsConnected { get; }
        public bool IsReady { get; }
        public DateTime CapturedAtUtc { get; }
        public bool HasQueueHandlerDiagnostics { get; }
        public int ReconnectAttemptCount { get; }
        public TimeSpan ReconnectCurrentDelay { get; }
        public DateTime ReconnectNextRetryAtUtc { get; }
        public string ReconnectLastFailureReason { get; }
        public PullSubRuntimeTopicDebugSnapshot[] Topics { get; }
    }

    public sealed partial class PullSubRuntime
    {
        internal PullSubRuntimeDebugSnapshot GetDebugSnapshot()
        {
            var topicDetails = _subscriptions.SnapshotTopicDetails();
            var typedStates = _typedDataRegistry.SnapshotTopicStates();

            var typedByTopic =
                new Dictionary<string, TypedDataRegistry.TypedDataTopicDebugSnapshot>(typedStates.Length, StringComparer.Ordinal);
            foreach (var typed in typedStates)
            {
                typedByTopic[typed.Topic] = typed;
            }

            var topics = new PullSubRuntimeTopicDebugSnapshot[topicDetails.Length];
            for (var i = 0; i < topicDetails.Length; i++)
            {
                var topic = topicDetails[i];

                var hasValue = false;
                var latestValueTimestampUtc = default(DateTime);
                var dataReceiveCount = 0L;
                if (typedByTopic.TryGetValue(topic.Topic, out var typed))
                {
                    hasValue = typed.HasValue;
                    latestValueTimestampUtc = typed.TimestampUtc;
                    dataReceiveCount = typed.ReceiveCount;
                }

                var droppedCount = 0L;
                if (topic.QueueSubCount > 0 && _rawInbox.TryGetDroppedCount(topic.Topic, out var dropped))
                    droppedCount = dropped;

                var queueHandlerState = PullSubQueueHandlerDebugTracker.GetSnapshot(this, topic.Topic);

                topics[i] = new PullSubRuntimeTopicDebugSnapshot(
                    topic.Topic,
                    topic.DataSubCount,
                    topic.QueueSubCount,
                    topic.SubscribeQos,
                    hasValue,
                    latestValueTimestampUtc,
                    dataReceiveCount,
                    droppedCount,
                    queueHandlerState.ActiveCount,
                    queueHandlerState.FaultedCount,
                    queueHandlerState.LastFaultUtc);
            }

            var state = State;
            var isStarted = state != PullSubState.NotStarted
                && state != PullSubState.Stopped
                && state != PullSubState.Disposed;

            GetReconnectStateSnapshot(
                out var reconnectAttemptCount,
                out var reconnectCurrentDelay,
                out var reconnectNextRetryAtUtc,
                out var reconnectLastFailureReason);

            return new PullSubRuntimeDebugSnapshot(
                state,
                isStarted,
                _transport.IsConnected,
                IsReady,
                DateTime.UtcNow,
                PullSubQueueHandlerDebugTracker.IsTrackingEnabled,
                reconnectAttemptCount,
                reconnectCurrentDelay,
                reconnectNextRetryAtUtc,
                reconnectLastFailureReason,
                topics);
        }
    }
}