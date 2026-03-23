using System;
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
        private readonly PullSubSubscriptionRegistry _subscriptions = new PullSubSubscriptionRegistry();
        private readonly TypedDataRegistry _typedDataRegistry = new TypedDataRegistry();
        private readonly PullSubQueueInbox _rawInbox = new PullSubQueueInbox();
        private readonly CancellationTokenSource _disposeCts = new CancellationTokenSource();

        private readonly PullSubMessageDispatcher _messageDispatcher;
        private readonly PullSubShutdownCoordinator _shutdownCoordinator;

        private readonly Action<string> _log;
        private readonly Action<string> _logWarning;
        private readonly Action<string> _logError;
        private readonly Action<Exception> _logException;
        private TaskCompletionSource<bool> _readySignal = CreateReadySignal();

        private int _disposeGuard;
        private volatile bool _suppressConnectedResubscribe;
        private PullSubState _state = PullSubState.NotStarted;

        public PullSubRuntime(
            ITransport transport,
            MqttClientProfile profile,
            Action<string> log = null,
            Action<string> logWarning = null,
            Action<string> logError = null,
            Action<Exception> logException = null)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _log = log ?? (_ => { });
            _logWarning = logWarning ?? (_ => { });
            _logError = logError ?? (_ => { });
            _logException = logException ?? (_ => { });
            _shutdownCoordinator = new PullSubShutdownCoordinator(_logError);
            _messageDispatcher = new PullSubMessageDispatcher(
                _subscriptions,
                _typedDataRegistry,
                _rawInbox,
                _logError,
                _logException);

            _transport.OnConnected = HandleConnectedAsync;
            _transport.OnDisconnected = HandleDisconnectedAsync;
            _transport.OnMessageReceived = HandleMessageReceivedAsync;
        }

        public MqttClientProfile Profile { get; }

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

        public bool IsStarted => !IsDisposeRequested && _transport.IsStarted;
        public bool IsConnected => !IsDisposeRequested && _transport.IsConnected;
        public bool IsReady => !IsDisposeRequested && State == PullSubState.Ready;

        /// <summary>
        /// Ensures the managed client has been started.
        /// This method guarantees runtime start, but does not guarantee broker connection completion.
        /// Use WaitUntilConnectedAsync when the caller needs Ready state.
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            var operationToken = CreateOperationToken(cancellationToken, out var linkedCts);
            var reuseStartedClient = false;
            var reuseConnectedClient = false;
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
                lock (_stateGate)
                {
                    if (_state == PullSubState.Starting)
                        return;

                    if (_transport.IsStarted)
                    {
                        reuseStartedClient = true;
                        reuseConnectedClient = _transport.IsConnected;
                    }
                    else
                    {
                        _state = PullSubState.Starting;
                    }
                }

                if (reuseStartedClient)
                {
                    if (reuseConnectedClient)
                    {
                        await ResubscribeAllTopicsAsync(operationToken);
                        SetState(PullSubState.Ready);
                    }
                    else
                    {
                        // StartAsync contract is "managed client started" (not "already connected").
                        // If the client is already started but transport is reconnecting, returning is expected.
                        SetState(PullSubState.Reconnecting);
                    }

                    return;
                }

                _suppressConnectedResubscribe = true;
                try
                {
                    var transport = Profile.ConnectionOptions.Transport;
                    _log($"[PullSubRuntime] Starting. broker={Profile.BrokerHost}:{Profile.BrokerPort}");

                    await _transport.StartAsync(operationToken);
                    await ResubscribeAllTopicsAsync(operationToken);
                    SetState(PullSubState.Ready);
                }
                finally
                {
                    _suppressConnectedResubscribe = false;
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

                    await PullSubAsyncUtils.AwaitWithCancellation(waitTask, operationToken);
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

        /// <summary>
        /// Requests bounded disconnect with the default timeout (3 seconds).
        /// Usually callers do not need this when using DisposeAsync, because DisposeAsync already performs bounded shutdown internally.
        /// </summary>
        public Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            return ShutdownAsync(TimeSpan.FromSeconds(3), cancellationToken);
        }

        /// <summary>
        /// Requests bounded disconnect with a custom timeout.
        /// Usually callers do not need this when using DisposeAsync, because DisposeAsync already performs bounded shutdown internally.
        /// </summary>
        public async Task ShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (IsDisposeRequested)
                return;

            await _shutdownCoordinator.RunAsync(DisconnectAsync, timeout, cancellationToken);
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
            readySignalSnapshot?.TrySetCanceled();

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

            if (_transport is IAsyncDisposable asyncTransport)
                await asyncTransport.DisposeAsync();
            else
                (_transport as IDisposable)?.Dispose();

            _disposeCts.Dispose();
            _subscriptionGate.Dispose();
            _resubscribeGate.Dispose();
            _startStopGate.Dispose();
        }

        private async Task DisconnectCoreAsync(CancellationToken cancellationToken, bool allowWhenDisposed = false)
        {
            if (IsDisposeRequested && !allowWhenDisposed)
                return;

            if (!_transport.IsStarted)
            {
                SetState(PullSubState.Stopped);
                return;
            }

            // Set Stopped before StopAsync so that the DisconnectedAsync event fired by MQTTnet
            // during the clean disconnect is treated as intentional and suppressed in OnDisconnected.
            SetState(PullSubState.Stopped);
            await _transport.StopAsync(true, cancellationToken);
        }

        private async Task ResubscribeAllTopicsAsync(CancellationToken cancellationToken)
        {
            var gateAcquired = false;
            try
            {
                await _resubscribeGate.WaitAsync(cancellationToken);
                gateAcquired = true;

                if (IsDisposeRequested)
                    return;

                SetState(PullSubState.ResubscribePending);
                foreach (var topic in _subscriptions.SnapshotTopics())
                {
                    if (!_subscriptions.TryGetSubscribeQos(topic, out var subscribeQos))
                        continue;

                    await SubscribeNetworkAsync(topic, subscribeQos, cancellationToken);
                }

                SetState(PullSubState.Ready);
            }
            finally
            {
                if (gateAcquired)
                    _resubscribeGate.Release();
            }
        }

        private async Task SubscribeNetworkAsync(
            string topic,
            MqttQualityOfServiceLevel subscribeQos,
            CancellationToken cancellationToken)
        {
            await _transport.SubscribeAsync(topic, subscribeQos, cancellationToken);
        }

        private async Task UnsubscribeNetworkAsync(string topic, CancellationToken cancellationToken)
        {
            await _transport.UnsubscribeAsync(topic, cancellationToken);
        }

        private void EnsureStarted()
        {
            if (!_transport.IsStarted)
                throw new InvalidOperationException("Client is not started. Call StartAsync first.");
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
            MqttQualityOfServiceLevel subscribeQos)
        {
            try
            {
                if (IsDisposeRequested || !_transport.IsConnected)
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

        internal bool IsDisposeRequested => Volatile.Read(ref _disposeGuard) != 0;
    }
}
