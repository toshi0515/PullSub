using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    internal readonly struct PullSubPendingRequestRegistration
    {
        public PullSubPendingRequestRegistration(
            string correlationId,
            Task<byte[]> responseTask)
        {
            CorrelationId = correlationId;
            ResponseTask = responseTask;
        }

        public string CorrelationId { get; }
        public Task<byte[]> ResponseTask { get; }
    }

    internal readonly struct PullSubPendingRequestStoreDebugSnapshot
    {
        public PullSubPendingRequestStoreDebugSnapshot(
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

    internal sealed class PullSubPendingRequestStore
    {
        private sealed class PendingEntry
        {
            public PendingEntry(
                string correlationId,
                TaskCompletionSource<byte[]> completion)
            {
                CorrelationId = correlationId;
                Completion = completion;
            }

            public string CorrelationId { get; }
            public TaskCompletionSource<byte[]> Completion { get; }
            public CancellationTokenRegistration CallerRegistration;
            public CancellationTokenSource TimeoutCts;
            public CancellationTokenRegistration TimeoutRegistration;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, PendingEntry> _entries
            = new Dictionary<string, PendingEntry>(StringComparer.Ordinal);

        private readonly int _maxPendingRequests;
        private Action<int> _onPendingCountChanged;

        private long _timeoutCount;
        private long _duplicateDiscardCount;
        private long _setupFailureCount;
        private long _publishFailureCount;
        private long _invalidReplyToDropCount;
        private long _connectionLostFailureCount;
        private long _runtimeDisposedFailureCount;

        public PullSubPendingRequestStore(int maxPendingRequests, Action<int> onPendingCountChanged)
        {
            if (maxPendingRequests < 1)
                throw new ArgumentOutOfRangeException(nameof(maxPendingRequests), "maxPendingRequests must be greater than 0.");

            _maxPendingRequests = maxPendingRequests;
            _onPendingCountChanged = onPendingCountChanged ?? (_ => { });
        }

        public Action<int> OnPendingCountChanged
        {
            get => _onPendingCountChanged;
            set => _onPendingCountChanged = value ?? (_ => { });
        }

        public int PendingCount
        {
            get
            {
                lock (_gate)
                {
                    return _entries.Count;
                }
            }
        }

        public PullSubPendingRequestRegistration Register(
            string correlationId,
            DateTime deadlineUtc,
            CancellationToken callerToken)
        {
            if (string.IsNullOrWhiteSpace(correlationId))
                throw new ArgumentException("correlationId is required.", nameof(correlationId));

            var completion = new TaskCompletionSource<byte[]>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var entry = new PendingEntry(correlationId, completion);
            int pendingCountAfterRegister;

            var timeout = deadlineUtc - DateTime.UtcNow;
            if (timeout < TimeSpan.Zero)
                timeout = TimeSpan.Zero;

            lock (_gate)
            {
                if (_entries.Count >= _maxPendingRequests)
                    throw new PullSubTooManyPendingRequestsException(_maxPendingRequests);

                if (_entries.ContainsKey(correlationId))
                    throw new InvalidOperationException($"Pending request already exists. correlationId={correlationId}");

                _entries[correlationId] = entry;
                pendingCountAfterRegister = _entries.Count;
            }

            entry.TimeoutCts = new CancellationTokenSource(timeout);
            entry.TimeoutRegistration = entry.TimeoutCts.Token.Register(() =>
            {
                TryFailSingle(
                    correlationId,
                    new PullSubRequestException(
                        PullSubRequestFailureKind.Timeout,
                        $"Request timed out. correlationId={correlationId}",
                        correlationId),
                    PullSubRequestFailureKind.Timeout,
                    canceled: false,
                    cancellationToken: default);
            });

            if (callerToken.CanBeCanceled)
            {
                entry.CallerRegistration = callerToken.Register(() =>
                {
                    TryFailSingle(
                        correlationId,
                        new OperationCanceledException(callerToken),
                        failureKind: null,
                        canceled: true,
                        cancellationToken: callerToken);
                });
            }

            NotifyPendingCountChanged(pendingCountAfterRegister);
            return new PullSubPendingRequestRegistration(correlationId, completion.Task);
        }

        public bool TryComplete(string correlationId, byte[] responseEnvelopePayload)
        {
            if (string.IsNullOrWhiteSpace(correlationId))
                return false;

            PendingEntry entry;
            int pendingCountAfterRemove;
            lock (_gate)
            {
                if (!_entries.TryGetValue(correlationId, out entry))
                {
                    _duplicateDiscardCount++;
                    return false;
                }

                _entries.Remove(correlationId);
                pendingCountAfterRemove = _entries.Count;
            }

            DisposeEntry(entry);
            entry.Completion.TrySetResult(responseEnvelopePayload ?? Array.Empty<byte>());
            NotifyPendingCountChanged(pendingCountAfterRemove);
            return true;
        }

        public void MarkSetupFailure()
        {
            lock (_gate)
            {
                _setupFailureCount++;
            }
        }

        public long MarkInvalidReplyToDrop()
        {
            lock (_gate)
            {
                _invalidReplyToDropCount++;
                return _invalidReplyToDropCount;
            }
        }

        public void FailPendingPublish(
            string correlationId,
            Exception exception,
            PullSubRequestFailureKind failureKind)
        {
            if (exception == null)
                throw new ArgumentNullException(nameof(exception));

            if (failureKind != PullSubRequestFailureKind.PublishFailed
                && failureKind != PullSubRequestFailureKind.ConnectionLost
                && failureKind != PullSubRequestFailureKind.RuntimeDisposed)
            {
                throw new ArgumentOutOfRangeException(nameof(failureKind), "failureKind must be PublishFailed, ConnectionLost, or RuntimeDisposed.");
            }

            var message = failureKind == PullSubRequestFailureKind.PublishFailed
                ? $"Request publish failed. correlationId={correlationId}"
                : $"Request failed while publishing. reason={failureKind} correlationId={correlationId}";

            TryFailSingle(
                correlationId,
                new PullSubRequestException(
                    failureKind,
                    message,
                    correlationId,
                    exception),
                failureKind,
                canceled: false,
                cancellationToken: default);
        }

        public void FailAll(PullSubRequestFailureKind failureKind)
        {
            PendingEntry[] entries;
            int pendingCountAfterClear;

            lock (_gate)
            {
                entries = new PendingEntry[_entries.Count];
                var index = 0;
                foreach (var pair in _entries)
                    entries[index++] = pair.Value;

                _entries.Clear();
                pendingCountAfterClear = _entries.Count;

                if (failureKind == PullSubRequestFailureKind.ConnectionLost)
                    _connectionLostFailureCount += entries.Length;
                else if (failureKind == PullSubRequestFailureKind.RuntimeDisposed)
                    _runtimeDisposedFailureCount += entries.Length;
            }

            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                DisposeEntry(entry);

                var exception = new PullSubRequestException(
                    failureKind,
                    $"Request failed. reason={failureKind} correlationId={entry.CorrelationId}",
                    entry.CorrelationId);

                entry.Completion.TrySetException(exception);
            }

            if (entries.Length > 0)
                NotifyPendingCountChanged(pendingCountAfterClear);
        }

        public PullSubPendingRequestStoreDebugSnapshot GetDebugSnapshot(bool isReplyInboxSubscribed)
        {
            lock (_gate)
            {
                return new PullSubPendingRequestStoreDebugSnapshot(
                    pendingCount: _entries.Count,
                    isReplyInboxSubscribed: isReplyInboxSubscribed,
                    timeoutCount: _timeoutCount,
                    duplicateDiscardCount: _duplicateDiscardCount,
                    setupFailureCount: _setupFailureCount,
                    publishFailureCount: _publishFailureCount,
                    invalidReplyToDropCount: _invalidReplyToDropCount,
                    connectionLostFailureCount: _connectionLostFailureCount,
                    runtimeDisposedFailureCount: _runtimeDisposedFailureCount);
            }
        }

        private void TryFailSingle(
            string correlationId,
            Exception exception,
            PullSubRequestFailureKind? failureKind,
            bool canceled,
            CancellationToken cancellationToken)
        {
            PendingEntry entry;
            int pendingCountAfterRemove;

            lock (_gate)
            {
                if (!_entries.TryGetValue(correlationId, out entry))
                    return;

                _entries.Remove(correlationId);
                pendingCountAfterRemove = _entries.Count;

                if (failureKind.HasValue)
                {
                    if (failureKind.Value == PullSubRequestFailureKind.Timeout)
                        _timeoutCount++;
                    else if (failureKind.Value == PullSubRequestFailureKind.PublishFailed)
                        _publishFailureCount++;
                    else if (failureKind.Value == PullSubRequestFailureKind.ConnectionLost)
                        _connectionLostFailureCount++;
                    else if (failureKind.Value == PullSubRequestFailureKind.RuntimeDisposed)
                        _runtimeDisposedFailureCount++;
                }
            }

            DisposeEntry(entry);

            if (canceled)
            {
                entry.Completion.TrySetCanceled(cancellationToken);
            }
            else
            {
                entry.Completion.TrySetException(exception);
            }

            NotifyPendingCountChanged(pendingCountAfterRemove);
        }

        private static void DisposeEntry(PendingEntry entry)
        {
            entry.CallerRegistration.Dispose();
            entry.TimeoutRegistration.Dispose();
            entry.TimeoutCts?.Dispose();
        }

        private void NotifyPendingCountChanged(int count)
        {
            _onPendingCountChanged(count);
        }
    }
}
