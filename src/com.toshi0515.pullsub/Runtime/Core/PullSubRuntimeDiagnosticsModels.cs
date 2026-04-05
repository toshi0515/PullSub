using System;

namespace PullSub.Core
{
    public readonly struct PullSubTopicDiagnostics
    {
        public PullSubTopicDiagnostics(
            string topic,
            int dataSubscriberCount,
            int queueSubscriberCount,
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
            DataSubscriberCount = dataSubscriberCount;
            QueueSubscriberCount = queueSubscriberCount;
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
        public int DataSubscriberCount { get; }
        public int QueueSubscriberCount { get; }
        public PullSubQualityOfServiceLevel SubscribeQos { get; }
        public bool HasValue { get; }
        public DateTime LatestValueTimestampUtc { get; }
        public long DataReceiveCount { get; }
        public long QueueDroppedCount { get; }
        public int ActiveQueueHandlers { get; }
        public int FaultedQueueHandlers { get; }
        public DateTime LastQueueHandlerFaultUtc { get; }
    }

    public readonly struct PullSubRuntimeDiagnosticsSnapshot
    {
        public PullSubRuntimeDiagnosticsSnapshot(
            PullSubState state,
            bool isStarted,
            bool isConnected,
            bool isReady,
            DateTime capturedAtUtc,
            bool hasQueueHandlerDiagnostics,
            PullSubTopicDiagnostics[] topics)
        {
            State = state;
            IsStarted = isStarted;
            IsConnected = isConnected;
            IsReady = isReady;
            CapturedAtUtc = capturedAtUtc;
            HasQueueHandlerDiagnostics = hasQueueHandlerDiagnostics;
            Topics = topics ?? Array.Empty<PullSubTopicDiagnostics>();
        }

        public PullSubState State { get; }
        public bool IsStarted { get; }
        public bool IsConnected { get; }
        public bool IsReady { get; }
        public DateTime CapturedAtUtc { get; }
        public bool HasQueueHandlerDiagnostics { get; }
        public PullSubTopicDiagnostics[] Topics { get; }
    }
}
