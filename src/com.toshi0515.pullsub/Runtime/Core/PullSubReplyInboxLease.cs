using System;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    internal sealed class PullSubReplyInboxLease : IAsyncDisposable
    {
        private readonly PullSubRuntime _runtime;
        private readonly IPullSubTopic<byte[]> _replyTopic;
        private readonly PullSubPendingRequestStore _pendingStore;
        private readonly TimeSpan _idleTimeout;
        private readonly PullSubQueueOptions _replyQueueOptions;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly Action<Exception> _logException;
        private readonly PullSubRuntimeOptions _runtimeOptions;
        private readonly Action<string, int> _onInboundOversizeDrop;

        private readonly object _timerGate = new object();
        private CancellationTokenSource _idleTimerCts;

        private PullSubQueueSubscription _subscription;
        private int _disposed;

        public PullSubReplyInboxLease(
            PullSubRuntime runtime,
            string replyTopic,
            PullSubPendingRequestStore pendingStore,
            TimeSpan idleTimeout,
            PullSubQueueOptions replyQueueOptions,
            Action<Exception> logException,
            PullSubRuntimeOptions runtimeOptions,
            Action<string, int> onInboundOversizeDrop)
        {
            if (runtime == null)
                throw new ArgumentNullException(nameof(runtime));

            if (string.IsNullOrWhiteSpace(replyTopic))
                throw new ArgumentException("replyTopic is required.", nameof(replyTopic));

            if (pendingStore == null)
                throw new ArgumentNullException(nameof(pendingStore));

            if (idleTimeout < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(idleTimeout), "idleTimeout must be zero or greater.");

            if (replyQueueOptions == null)
                throw new ArgumentNullException(nameof(replyQueueOptions));

            if (runtimeOptions == null)
                throw new ArgumentNullException(nameof(runtimeOptions));

            if (onInboundOversizeDrop == null)
                throw new ArgumentNullException(nameof(onInboundOversizeDrop));

            _runtime = runtime;
            _pendingStore = pendingStore;
            _idleTimeout = idleTimeout;
            _replyQueueOptions = replyQueueOptions;
            _logException = logException ?? (_ => { });
            _runtimeOptions = runtimeOptions;
            _onInboundOversizeDrop = onInboundOversizeDrop;
            _replyTopic = PullSubTopic.Create(
                replyTopic,
                PullSubRawBinaryPayloadCodec.Default);

            _pendingStore.OnPendingCountChanged = HandlePendingCountChanged;
        }

        public string Topic => _replyTopic.TopicName;
        public bool IsSubscribed => _subscription != null;

        public async Task EnsureSubscribedAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            CancelIdleTimer();

            if (_subscription != null)
                return;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();

                if (_subscription != null)
                    return;

                _subscription = await _runtime.SubscribeQueueAsync(
                        _replyTopic,
                    _replyQueueOptions,
                        HandleReplyAsync,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            CancelIdleTimer();
            _pendingStore.OnPendingCountChanged = null;

            await _gate.WaitAsync().ConfigureAwait(false);
            PullSubQueueSubscription subscription;
            try
            {
                subscription = _subscription;
                _subscription = null;
            }
            finally
            {
                _gate.Release();
            }

            if (subscription != null)
            {
                try
                {
                    await subscription.UnsubscribeAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logException(ex);
                }
            }

            _gate.Dispose();
        }

        private ValueTask HandleReplyAsync(byte[] responseEnvelopePayload, CancellationToken cancellationToken)
        {
            // Defense in depth: dispatcher should already drop oversize payloads,
            // but keep this local guard in case future routing paths bypass dispatcher checks.
            if (responseEnvelopePayload != null && responseEnvelopePayload.Length > _runtimeOptions.MaxInboundPayloadBytes)
            {
                _onInboundOversizeDrop(Topic, responseEnvelopePayload.Length);
                return default;
            }

            if (!PullSubResponseEnvelopeCodec<byte[]>.TryReadCorrelationId(responseEnvelopePayload, out var correlationId, out _))
                return default;

            _pendingStore.TryComplete(correlationId, responseEnvelopePayload);
            return default;
        }

        private void HandlePendingCountChanged(int pendingCount)
        {
            if (_idleTimeout == TimeSpan.Zero)
                return;

            if (pendingCount > 0)
            {
                CancelIdleTimer();
                return;
            }

            StartIdleTimer();
        }

        private void StartIdleTimer()
        {
            CancellationTokenSource idleCts;
            lock (_timerGate)
            {
                _idleTimerCts?.Cancel();
                _idleTimerCts?.Dispose();
                _idleTimerCts = new CancellationTokenSource();
                idleCts = _idleTimerCts;
            }

            _ = RunIdleReleaseAsync(idleCts);
        }

        private async Task RunIdleReleaseAsync(CancellationTokenSource idleCts)
        {
            try
            {
                await Task.Delay(_idleTimeout, idleCts.Token).ConfigureAwait(false);
                await TryUnsubscribeForIdleAsync(idleCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logException(ex);
            }
        }

        private async Task TryUnsubscribeForIdleAsync(CancellationToken cancellationToken)
        {
            if (_pendingStore.PendingCount > 0)
                return;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            PullSubQueueSubscription subscription;
            try
            {
                if (_pendingStore.PendingCount > 0)
                    return;

                subscription = _subscription;
                _subscription = null;
            }
            finally
            {
                _gate.Release();
            }

            if (subscription == null)
                return;

            try
            {
                await subscription.UnsubscribeAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logException(ex);
            }
        }

        private void CancelIdleTimer()
        {
            lock (_timerGate)
            {
                _idleTimerCts?.Cancel();
                _idleTimerCts?.Dispose();
                _idleTimerCts = null;
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(PullSubReplyInboxLease));
        }
    }
}
