using System;

namespace PullSub.Core
{
    public readonly struct PullSubRequestDiagnostics
    {
        public PullSubRequestDiagnostics(
            int pendingCount,
            bool isReplyInboxSubscribed,
            long timeoutCount,
            long duplicateDiscardCount,
            long setupFailureCount,
            long publishFailureCount,
            long invalidReplyToDropCount,
            long connectionLostFailureCount,
            long runtimeDisposedFailureCount)
        {
            PendingCount = pendingCount;
            IsReplyInboxSubscribed = isReplyInboxSubscribed;
            TimeoutCount = timeoutCount;
            DuplicateDiscardCount = duplicateDiscardCount;
            SetupFailureCount = setupFailureCount;
            PublishFailureCount = publishFailureCount;
            InvalidReplyToDropCount = invalidReplyToDropCount;
            ConnectionLostFailureCount = connectionLostFailureCount;
            RuntimeDisposedFailureCount = runtimeDisposedFailureCount;
        }

        public int PendingCount { get; }
        public bool IsReplyInboxSubscribed { get; }
        public long TimeoutCount { get; }
        public long DuplicateDiscardCount { get; }
        public long SetupFailureCount { get; }
        public long PublishFailureCount { get; }
        public long InvalidReplyToDropCount { get; }
        public long ConnectionLostFailureCount { get; }
        public long RuntimeDisposedFailureCount { get; }
    }

    public readonly struct PullSubReconnectDiagnostics
    {
        public PullSubReconnectDiagnostics(
            int attemptCount,
            TimeSpan currentDelay,
            DateTime nextRetryAtUtc,
            string lastFailureReason)
        {
            AttemptCount = attemptCount;
            CurrentDelay = currentDelay;
            NextRetryAtUtc = nextRetryAtUtc;
            LastFailureReason = lastFailureReason ?? string.Empty;
        }

        public int AttemptCount { get; }
        public TimeSpan CurrentDelay { get; }
        public DateTime NextRetryAtUtc { get; }
        public string LastFailureReason { get; }
    }

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
            long inboundOversizeDropCount,
            PullSubReconnectDiagnostics reconnect,
            PullSubRequestDiagnostics request,
            PullSubTopicDiagnostics[] topics)
        {
            State = state;
            IsStarted = isStarted;
            IsConnected = isConnected;
            IsReady = isReady;
            CapturedAtUtc = capturedAtUtc;
            HasQueueHandlerDiagnostics = hasQueueHandlerDiagnostics;
            InboundOversizeDropCount = inboundOversizeDropCount;
            Reconnect = reconnect;
            Request = request;
            Topics = topics ?? Array.Empty<PullSubTopicDiagnostics>();
        }

        public PullSubState State { get; }
        public bool IsStarted { get; }
        public bool IsConnected { get; }
        public bool IsReady { get; }
        public DateTime CapturedAtUtc { get; }
        public bool HasQueueHandlerDiagnostics { get; }
        public long InboundOversizeDropCount { get; }
        public PullSubReconnectDiagnostics Reconnect { get; }
        public PullSubRequestDiagnostics Request { get; }
        public PullSubTopicDiagnostics[] Topics { get; }
    }
}
