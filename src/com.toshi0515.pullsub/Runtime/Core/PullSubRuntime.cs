using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    public sealed partial class PullSubRuntime : IAsyncDisposable
    {
        private readonly object _stateGate = new object();
        private readonly SemaphoreSlim _startStopGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _resubscribeGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _subscriptionGate = new SemaphoreSlim(1, 1);
        private readonly ITransport _transport;
        private readonly SubscriptionRegistry _subscriptions = new SubscriptionRegistry();
        private readonly TypedDataRegistry _typedDataRegistry = new TypedDataRegistry();
        private readonly PullSubQueueInbox _rawInbox = new PullSubQueueInbox();
        private readonly CancellationTokenSource _disposeCts = new CancellationTokenSource();

        private readonly MessageDispatcher _messageDispatcher;
        private readonly ShutdownCoordinator _shutdownCoordinator;
        private readonly HashSet<string> _networkSubscribedTopics = new HashSet<string>(StringComparer.Ordinal);
        private readonly RequestOptions _requestOptions;
        private readonly PullSubRuntimeOptions _runtimeOptions;
        private readonly string _requestRuntimeNonce = Guid.NewGuid().ToString("N");
        private readonly PendingRequestStore _pendingRequestStore;
        private readonly ReplyInboxLease _replyInboxLease;
        private readonly object _securityLogGate = new object();

        private long _inboundOversizeDropCount;
        private long _inboundOversizeDropBurstCount;
        private DateTime _inboundOversizeLastLogUtc;
        private long _invalidReplyToDropBurstCount;
        private DateTime _invalidReplyToLastLogUtc;

        private readonly Action<string> _log;
        private readonly Action<string> _logWarning;
        private readonly Action<string> _logError;
        private readonly Action<Exception> _logException;
        private TaskCompletionSource<bool> _readySignal = CreateReadySignal();
        private TaskCompletionSource<bool> _disconnectSignal = CreateDisconnectedSignal();
        private readonly Random _reconnectJitterRandom = new Random();

        private CancellationTokenSource _connectionLoopCts;
        private Task _connectionLoopTask;

        private int _disposeGuard;
        private int _reconnectAttemptCount;
        private TimeSpan _reconnectCurrentDelay = ReconnectOptions.Default.InitialDelay;
        private DateTime _reconnectNextRetryAtUtc;
        private string _reconnectLastFailureReason = string.Empty;
        private PullSubState _state = PullSubState.NotStarted;

        public PullSubRuntime(
            ITransport transport,
            Action<string> log = null,
            Action<string> logWarning = null,
            Action<string> logError = null,
            Action<Exception> logException = null,
            RequestOptions requestOptions = null,
            PullSubRuntimeOptions runtimeOptions = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _log = log ?? (_ => { });
            _logWarning = logWarning ?? (_ => { });
            _logError = logError ?? (_ => { });
            _logException = logException ?? (_ => { });
            _requestOptions = requestOptions ?? RequestOptions.Default;
            _runtimeOptions = runtimeOptions ?? PullSubRuntimeOptions.Default;
            _pendingRequestStore = new PendingRequestStore(
                _requestOptions.MaxPendingRequests,
                _ => { });
            _replyInboxLease = new ReplyInboxLease(
                this,
                BuildReplyInboxTopic(_requestOptions.ReplyTopicPrefix, _requestRuntimeNonce),
                _pendingRequestStore,
                _requestOptions.InboxIdleTimeout,
                new QueueOptions(_requestOptions.ReplyInboxQueueDepth),
                _logException,
                _runtimeOptions,
                MarkInboundOversizeDrop);
            _shutdownCoordinator = new ShutdownCoordinator(_logError);
            _messageDispatcher = new MessageDispatcher(
                _subscriptions,
                _typedDataRegistry,
                _rawInbox,
                _logError,
                _logException,
                _runtimeOptions,
                MarkInboundOversizeDrop);

            _transport.SetCallbacks(
                HandleConnectedAsync,
                HandleDisconnectedAsync,
                HandleMessageReceivedAsync);
        }

        public RequestOptions RequestOptions => _requestOptions;
        public PullSubRuntimeOptions RuntimeOptions => _runtimeOptions;
        internal string ReplyInboxTopic => _replyInboxLease.Topic;
        internal bool IsReplyInboxSubscribed => _replyInboxLease.IsSubscribed;

        public PullSubState State
        {
            get
            {
                lock (_stateGate)
                {
                    return _state;
                }
            }
        }

        public bool IsReady => !IsDisposeRequested && State == PullSubState.Ready;

        internal void EnsureRequestApiReady()
        {
            ThrowIfDisposed();
            EnsureStarted();
        }

        internal Task EnsureReplyInboxSubscriptionAsync(CancellationToken cancellationToken)
        {
            return _replyInboxLease.EnsureSubscribedAsync(cancellationToken);
        }

        internal PendingRequestRegistration RegisterPendingRequest(
            string correlationId,
            DateTime deadlineUtc,
            CancellationToken cancellationToken)
        {
            return _pendingRequestStore.Register(correlationId, deadlineUtc, cancellationToken);
        }

        internal bool TryCompletePendingRequest(string correlationId, byte[] responseEnvelopePayload)
        {
            return _pendingRequestStore.TryComplete(correlationId, responseEnvelopePayload);
        }

        internal void MarkReplyInboxSetupFailure()
        {
            _pendingRequestStore.MarkSetupFailure();
        }

        internal void MarkInvalidReplyToDrop(string replyTo = null)
        {
            var totalDropCount = _pendingRequestStore.MarkInvalidReplyToDrop();
            var now = DateTime.UtcNow;

            if (totalDropCount == 1)
            {
                lock (_securityLogGate)
                {
                    _invalidReplyToLastLogUtc = now;
                    Interlocked.Exchange(ref _invalidReplyToDropBurstCount, 0);
                }

                _logWarning(
                    $"[PullSubRuntime] Dropped invalid replyTo. replyTo='{replyTo ?? string.Empty}' " +
                    $"expectedPrefix='{_requestOptions.ReplyTopicPrefix}'.");
                return;
            }

            Interlocked.Increment(ref _invalidReplyToDropBurstCount);

            if (_runtimeOptions.InvalidReplyToLogInterval <= TimeSpan.Zero)
                return;

            if (Volatile.Read(ref _invalidReplyToDropBurstCount) < _runtimeOptions.InvalidReplyToAggregateThreshold)
                return;

            long burstToReport;
            lock (_securityLogGate)
            {
                if (now - _invalidReplyToLastLogUtc < _runtimeOptions.InvalidReplyToLogInterval)
                    return;

                burstToReport = Interlocked.Exchange(ref _invalidReplyToDropBurstCount, 0);
                _invalidReplyToLastLogUtc = now;
            }

            if (burstToReport > 0)
            {
                _logWarning(
                    $"[PullSubRuntime] Dropped invalid replyTo in aggregate. " +
                    $"count={burstToReport} total={totalDropCount} expectedPrefix='{_requestOptions.ReplyTopicPrefix}'.");
            }
        }

        internal void MarkInboundOversizeDrop(string topic, int payloadLength)
        {
            var totalDropCount = Interlocked.Increment(ref _inboundOversizeDropCount);
            var now = DateTime.UtcNow;

            if (totalDropCount == 1)
            {
                lock (_securityLogGate)
                {
                    _inboundOversizeLastLogUtc = now;
                    Interlocked.Exchange(ref _inboundOversizeDropBurstCount, 0);
                }

                _logWarning(
                    $"[PullSubRuntime] Dropped inbound oversize payload. topic='{topic}' " +
                    $"sizeBytes={payloadLength} limitBytes={_runtimeOptions.MaxInboundPayloadBytes}.");
                return;
            }

            Interlocked.Increment(ref _inboundOversizeDropBurstCount);

            if (_runtimeOptions.InboundOversizeLogInterval <= TimeSpan.Zero)
                return;

            if (Volatile.Read(ref _inboundOversizeDropBurstCount) < _runtimeOptions.InboundOversizeAggregateThreshold)
                return;

            long burstToReport;
            lock (_securityLogGate)
            {
                if (now - _inboundOversizeLastLogUtc < _runtimeOptions.InboundOversizeLogInterval)
                    return;

                burstToReport = Interlocked.Exchange(ref _inboundOversizeDropBurstCount, 0);
                _inboundOversizeLastLogUtc = now;
            }

            if (burstToReport > 0)
            {
                _logWarning(
                    $"[PullSubRuntime] Dropped inbound oversize payloads in aggregate. " +
                    $"count={burstToReport} total={totalDropCount} limitBytes={_runtimeOptions.MaxInboundPayloadBytes}.");
            }
        }

        internal long GetInboundOversizeDropCount()
        {
            return Interlocked.Read(ref _inboundOversizeDropCount);
        }

        internal void FailRequestPublish(string correlationId, Exception exception)
        {
            _pendingRequestStore.FailPendingPublish(
                correlationId,
                exception,
                ClassifyPublishFailure());
        }

        internal void FailAllPendingRequests(PullSubRequestFailureKind failureKind)
        {
            _pendingRequestStore.FailAll(failureKind);
        }

        internal PendingRequestStoreDebugSnapshot GetPendingRequestStoreSnapshot()
        {
            return _pendingRequestStore.GetDebugSnapshot(_replyInboxLease.IsSubscribed);
        }

        internal void LogException(Exception exception)
        {
            if (exception == null)
                return;

            _logException(exception);
        }

        internal void LogWarning(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            _logWarning(message);
        }

        internal PullSubRequestFailureKind ClassifyPublishFailure()
        {
            if (IsDisposeRequested)
                return PullSubRequestFailureKind.RuntimeDisposed;

            if (!_transport.IsConnected)
                return PullSubRequestFailureKind.ConnectionLost;

            var state = State;
            if (state == PullSubState.Disconnected
                || state == PullSubState.Reconnecting
                || state == PullSubState.Stopped)
            {
                return PullSubRequestFailureKind.ConnectionLost;
            }

            return PullSubRequestFailureKind.PublishFailed;
        }

        /// <summary>
        /// Ensures the managed client has been started.
        /// This method guarantees runtime start, but does not guarantee broker connection completion.
        /// Use WaitUntilConnectedAsync when the caller needs Ready state.
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            var operationToken = CreateOperationToken(cancellationToken, out var linkedCts);
            try
            {
                await _startStopGate.WaitAsync(operationToken);
            }
            catch
            {
                linkedCts?.Dispose();
                throw;
            }

            try
            {
                var shouldStartLoop = false;
                lock (_stateGate)
                {
                    if (IsStartedStateNoLock() && IsConnectionLoopRunningNoLock())
                    {
                        return;
                    }

                    _state = PullSubState.Starting;
                    if (_readySignal.Task.IsCompleted)
                        _readySignal = CreateReadySignal();

                    InitializeReconnectStateNoLock(_transport.ReconnectOptions);
                    ClearNetworkSubscriptionCacheNoLock();
                    shouldStartLoop = true;
                }

                if (shouldStartLoop)
                {
                    _log("[PullSubRuntime] Starting.");
                    StartConnectionLoop();
                }
            }
            catch
            {
                SetState(PullSubState.Disconnected);
                throw;
            }
            finally
            {
                _startStopGate.Release();
                linkedCts?.Dispose();
            }
        }

        public async Task WaitUntilConnectedAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            var operationToken = CreateOperationToken(cancellationToken, out var linkedCts);

            try
            {
                while (true)
                {
                    Task waitTask;
                    lock (_stateGate)
                    {
                        if (_state == PullSubState.Ready)
                            return;

                        waitTask = _readySignal.Task;
                    }

                    await AsyncUtils.AwaitWithCancellation(waitTask, operationToken);
                }
            }
            finally
            {
                linkedCts?.Dispose();
            }
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            var operationToken = CreateOperationToken(cancellationToken, out var linkedCts);
            try
            {
                await _startStopGate.WaitAsync(operationToken);
            }
            catch
            {
                linkedCts?.Dispose();
                throw;
            }

            try
            {
                await DisconnectCoreAsync(operationToken);
            }
            finally
            {
                _startStopGate.Release();
                linkedCts?.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeGuard, 1) == 1)
                return;

            TaskCompletionSource<bool> readySignalSnapshot;
            lock (_stateGate)
            {
                readySignalSnapshot = _readySignal;
            }

            _disposeCts.Cancel();
            FailAllPendingRequests(PullSubRequestFailureKind.RuntimeDisposed);
            readySignalSnapshot?.TrySetCanceled();

            try
            {
                await _replyInboxLease.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logError($"[PullSubRuntime] Reply inbox dispose failure: {ex.Message}");
                _logException(ex);
            }

            try
            {
                await _shutdownCoordinator.RunAsync(DisconnectForDisposeAsync, TimeSpan.FromSeconds(3), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logError($"[PullSubRuntime] Dispose shutdown failure: {ex.Message}");
                _logException(ex);
            }

            try
            {
                _rawInbox.Clear();
                _typedDataRegistry.CancelAll();
            }
            catch (Exception ex)
            {
                _logError($"[PullSubRuntime] Dispose cleanup failure: {ex.Message}");
                _logException(ex);
            }

            SetState(PullSubState.Disposed);

            await _transport.DisposeAsync();

            _disposeCts.Dispose();
            _subscriptionGate.Dispose();
            _resubscribeGate.Dispose();
            _startStopGate.Dispose();
            PullSubQueueHandlerDebugTracker.OnRuntimeDisposed(this);
            CompositeSubscriptionDebugTracker.OnRuntimeDisposed(this);
        }

        private async Task DisconnectCoreAsync(CancellationToken cancellationToken, bool allowWhenDisposed = false)
        {
            if (IsDisposeRequested && !allowWhenDisposed)
                return;

            if (!IsStartedState() && !_transport.IsConnected)
            {
                SetState(PullSubState.Stopped);
                return;
            }

            _log("[PullSubRuntime] Disconnecting...");

            // Move to Stopped before disconnecting so OnDisconnected can be treated as intentional.
            SetState(PullSubState.Stopped);

            await StopConnectionLoopAsync(cancellationToken);

            if (_transport.IsConnected)
                await _transport.DisconnectAsync(true, cancellationToken);

            lock (_stateGate)
            {
                InitializeReconnectStateNoLock(_transport.ReconnectOptions);
                ClearNetworkSubscriptionCacheNoLock();
            }

            _log("[PullSubRuntime] Disconnected.");
        }

        private async Task ResubscribeAllTopicsAsync(CancellationToken cancellationToken)
        {
            var gateAcquired = false;
            var subscriptionGateAcquired = false;
            try
            {
                await _resubscribeGate.WaitAsync(cancellationToken);
                gateAcquired = true;

                await _subscriptionGate.WaitAsync(cancellationToken);
                subscriptionGateAcquired = true;

                if (IsDisposeRequested)
                    return;

                var topics = _subscriptions.SnapshotTopics();
                if (topics.Length > 0)
                    _log($"[PullSubRuntime] Resubscribing {topics.Length} topic(s).");


                SetState(PullSubState.ResubscribePending);
                foreach (var topic in topics)
                {
                    if (!_subscriptions.TryGetSubscribeQos(topic, out var subscribeQos))
                        continue;

                    if (IsNetworkSubscribedTopic(topic))
                        continue;

                    await SubscribeNetworkAsync(topic, subscribeQos, cancellationToken);
                }

                SetState(PullSubState.Ready);

                if (topics.Length > 0)
                    _log("[PullSubRuntime] Resubscribe completed.");
            }
            finally
            {
                if (subscriptionGateAcquired)
                    _subscriptionGate.Release();

                if (gateAcquired)
                    _resubscribeGate.Release();
            }
        }

        private async Task SubscribeNetworkAsync(
            string topic,
            PullSubQualityOfServiceLevel subscribeQos,
            CancellationToken cancellationToken)
        {
            await _transport.SubscribeAsync(topic, subscribeQos, cancellationToken);
            MarkNetworkSubscribedTopic(topic);
        }

        private async Task UnsubscribeNetworkAsync(string topic, CancellationToken cancellationToken)
        {
            await _transport.UnsubscribeAsync(topic, cancellationToken);
            MarkNetworkUnsubscribedTopic(topic);
        }

        private void EnsureStarted()
        {
            if (!IsStartedState())
                throw new InvalidOperationException("Client is not started. Call StartAsync first.");
        }

        private static string BuildReplyInboxTopic(string prefix, string runtimeNonce)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                throw new ArgumentException("prefix is required.", nameof(prefix));

            if (string.IsNullOrWhiteSpace(runtimeNonce))
                throw new ArgumentException("runtimeNonce is required.", nameof(runtimeNonce));

            var normalizedPrefix = prefix.TrimEnd('/');
            var topic = $"{normalizedPrefix}/{runtimeNonce}";
            SubscriptionRegistry.ValidateExactMatchTopic(topic);
            return topic;
        }

        private void ThrowIfDisposed()
        {
            if (IsDisposeRequested)
                throw new ObjectDisposedException(nameof(PullSubRuntime));
        }

        private void SetState(PullSubState next)
        {
            TaskCompletionSource<bool> readySignalToSet = null;
            lock (_stateGate)
            {
                if (IsDisposeRequested && next != PullSubState.Disposed)
                    return;

                if (_state == next)
                    return;

                _state = next;

                if (next == PullSubState.Ready)
                {
                    readySignalToSet = _readySignal;
                }
                else if (next != PullSubState.Disposed && _readySignal.Task.IsCompleted)
                {
                    _readySignal = CreateReadySignal();
                }
            }

            readySignalToSet?.TrySetResult(true);
        }

        private CancellationToken CreateOperationToken(
            CancellationToken cancellationToken,
            out CancellationTokenSource linkedCts)
        {
            if (IsDisposeRequested)
                throw new ObjectDisposedException(nameof(PullSubRuntime));

            if (!cancellationToken.CanBeCanceled)
            {
                linkedCts = null;
                return _disposeCts.Token;
            }

            if (cancellationToken == _disposeCts.Token || cancellationToken.IsCancellationRequested)
            {
                linkedCts = null;
                return cancellationToken;
            }

            try
            {
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token, cancellationToken);
            }
            catch (ObjectDisposedException) when (IsDisposeRequested)
            {
                throw new ObjectDisposedException(nameof(PullSubRuntime));
            }

            return linkedCts.Token;
        }

        private async Task EnsureNetworkSubscriptionConsistencyAsync(
            string topic,
            PullSubQualityOfServiceLevel subscribeQos)
        {
            try
            {
                if (IsDisposeRequested || !_transport.IsConnected || IsNetworkSubscribedTopic(topic))
                    return;

                await SubscribeNetworkAsync(topic, subscribeQos, _disposeCts.Token);
            }
            catch (OperationCanceledException) when (IsDisposeRequested || _disposeCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logException(ex);
            }
        }

        private async Task DisconnectForDisposeAsync(CancellationToken cancellationToken)
        {
            var gateAcquired = false;
            try
            {
                await _startStopGate.WaitAsync(cancellationToken);
                gateAcquired = true;

                await DisconnectCoreAsync(cancellationToken, allowWhenDisposed: true);
            }
            finally
            {
                if (gateAcquired)
                    _startStopGate.Release();
            }
        }

        private static TaskCompletionSource<bool> CreateReadySignal()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static TaskCompletionSource<bool> CreateDisconnectedSignal()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private void StartConnectionLoop()
        {
            CancellationToken loopToken;
            lock (_stateGate)
            {
                if (IsConnectionLoopRunningNoLock())
                    return;

                _connectionLoopCts?.Dispose();
                _connectionLoopCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
                loopToken = _connectionLoopCts.Token;
            }

            _connectionLoopTask = Task.Run(() => RunConnectionLoopAsync(loopToken), CancellationToken.None);
        }

        private async Task StopConnectionLoopAsync(CancellationToken cancellationToken)
        {
            Task loopTask;
            CancellationTokenSource loopCts;

            lock (_stateGate)
            {
                _disconnectSignal?.TrySetResult(true);

                loopTask = _connectionLoopTask;
                loopCts = _connectionLoopCts;

                _connectionLoopTask = null;
                _connectionLoopCts = null;
            }

            if (loopCts != null)
            {
                try
                {
                    loopCts.Cancel();
                }
                finally
                {
                    loopCts.Dispose();
                }
            }

            if (loopTask == null)
                return;

            try
            {
                await AsyncUtils.AwaitWithCancellation(loopTask, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private async Task RunConnectionLoopAsync(CancellationToken cancellationToken)
        {
            var reconnectOptions = _transport.ReconnectOptions ?? ReconnectOptions.Default;
            var nextDelay = reconnectOptions.InitialDelay;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    TaskCompletionSource<bool> disconnectSignal;
                    lock (_stateGate)
                    {
                        disconnectSignal = CreateDisconnectedSignal();
                        _disconnectSignal = disconnectSignal;
                    }

                    try
                    {
                        await _transport.ConnectAsync(cancellationToken);
                        nextDelay = reconnectOptions.InitialDelay;

                        lock (_stateGate)
                        {
                            InitializeReconnectStateNoLock(reconnectOptions);
                        }

                        await AsyncUtils.AwaitWithCancellation(disconnectSignal.Task, cancellationToken);

                        if (cancellationToken.IsCancellationRequested)
                            break;

                        if (!ShouldContinueReconnectLoop())
                            break;

                        SetState(PullSubState.Reconnecting);
                        var disconnectedDelay = ApplyJitter(nextDelay, reconnectOptions.JitterFactor);
                        RecordReconnectRetry(disconnectedDelay, "Disconnected");
                        await Task.Delay(disconnectedDelay, cancellationToken);
                        nextDelay = ComputeNextDelay(nextDelay, reconnectOptions);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (!ShouldContinueReconnectLoop())
                            break;

                        SetState(PullSubState.Reconnecting);
                        var retryDelay = ApplyJitter(nextDelay, reconnectOptions.JitterFactor);
                        RecordReconnectRetry(retryDelay, ex.Message);

                        await Task.Delay(retryDelay, cancellationToken);
                        nextDelay = ComputeNextDelay(nextDelay, reconnectOptions);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logError($"[PullSubRuntime] Connection loop terminated unexpectedly: {ex.Message}");
                _logException(ex);
                SetState(PullSubState.Disconnected);
            }
        }

        private TimeSpan ApplyJitter(TimeSpan delay, double jitterFactor)
        {
            if (jitterFactor <= 0.0)
                return delay;

            var rangeMs = delay.TotalMilliseconds * jitterFactor;
            var offsetMs = (_reconnectJitterRandom.NextDouble() * 2.0 - 1.0) * rangeMs;
            var jitteredMs = Math.Max(1.0, delay.TotalMilliseconds + offsetMs);
            return TimeSpan.FromMilliseconds(jitteredMs);
        }

        private static TimeSpan ComputeNextDelay(TimeSpan currentDelay, ReconnectOptions options)
        {
            var scaledTicks = (long)(currentDelay.Ticks * options.Multiplier);
            if (scaledTicks < options.InitialDelay.Ticks)
                scaledTicks = options.InitialDelay.Ticks;

            if (scaledTicks > options.MaxDelay.Ticks)
                scaledTicks = options.MaxDelay.Ticks;

            return TimeSpan.FromTicks(scaledTicks);
        }

        private void InitializeReconnectStateNoLock(ReconnectOptions options)
        {
            var resolved = options ?? ReconnectOptions.Default;
            _reconnectAttemptCount = 0;
            _reconnectCurrentDelay = resolved.InitialDelay;
            _reconnectNextRetryAtUtc = default;
            _reconnectLastFailureReason = string.Empty;
        }

        private void RecordReconnectRetry(TimeSpan delay, string reason)
        {
            lock (_stateGate)
            {
                _reconnectAttemptCount++;
                _reconnectCurrentDelay = delay;
                _reconnectNextRetryAtUtc = DateTime.UtcNow.Add(delay);
                _reconnectLastFailureReason = reason ?? string.Empty;
            }
        }

        private bool IsConnectionLoopRunningNoLock()
        {
            return _connectionLoopTask != null && !_connectionLoopTask.IsCompleted;
        }

        private bool ShouldContinueReconnectLoop()
        {
            lock (_stateGate)
            {
                return _state != PullSubState.Stopped
                    && _state != PullSubState.Disposed;
            }
        }

        private bool IsStartedState()
        {
            lock (_stateGate)
            {
                return IsStartedState(_state);
            }
        }

        private bool IsStartedStateNoLock()
        {
            return IsStartedState(_state);
        }

        private static bool IsStartedState(PullSubState state)
        {
            return state != PullSubState.NotStarted
                && state != PullSubState.Stopped
                && state != PullSubState.Disposed;
        }

        private void SignalDisconnected()
        {
            TaskCompletionSource<bool> disconnectSignal;
            lock (_stateGate)
            {
                disconnectSignal = _disconnectSignal;
            }

            disconnectSignal?.TrySetResult(true);
        }

        private bool IsNetworkSubscribedTopic(string topic)
        {
            lock (_stateGate)
            {
                return _networkSubscribedTopics.Contains(topic);
            }
        }

        private void MarkNetworkSubscribedTopic(string topic)
        {
            lock (_stateGate)
            {
                _networkSubscribedTopics.Add(topic);
            }
        }

        private void MarkNetworkUnsubscribedTopic(string topic)
        {
            lock (_stateGate)
            {
                _networkSubscribedTopics.Remove(topic);
            }
        }

        private void ClearNetworkSubscriptionCache()
        {
            lock (_stateGate)
            {
                _networkSubscribedTopics.Clear();
            }
        }

        private void ClearNetworkSubscriptionCacheNoLock()
        {
            _networkSubscribedTopics.Clear();
        }

        internal void GetReconnectStateSnapshot(
            out int attemptCount,
            out TimeSpan currentDelay,
            out DateTime nextRetryAtUtc,
            out string lastFailureReason)
        {
            lock (_stateGate)
            {
                attemptCount = _reconnectAttemptCount;
                currentDelay = _reconnectCurrentDelay;
                nextRetryAtUtc = _reconnectNextRetryAtUtc;
                lastFailureReason = _reconnectLastFailureReason;
            }
        }

        internal void ReportPublisherObserverError(Exception exception)
        {
            if (exception == null)
                return;

            _logError($"[PublishObserver] Publish failed: {exception.Message}");
            _logException(exception);
        }

        internal bool IsDisposeRequested => Volatile.Read(ref _disposeGuard) != 0;
    }
}
