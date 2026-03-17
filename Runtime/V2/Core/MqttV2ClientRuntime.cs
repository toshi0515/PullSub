using System;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Protocol;


namespace UnityMqtt.V2.Core
{
    public sealed partial class MqttV2ClientRuntime : IAsyncDisposable
    {
        private readonly object _stateGate = new object();
        private readonly SemaphoreSlim _startStopGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _resubscribeGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _subscriptionGate = new SemaphoreSlim(1, 1);

        private readonly IManagedMqttClient _client;
        private readonly MqttV2SubscriptionRegistry _subscriptions = new MqttV2SubscriptionRegistry();
        private readonly TypedDataRegistry _typedDataRegistry = new TypedDataRegistry();
        private readonly MqttV2RawInbox _rawInbox = new MqttV2RawInbox();
        private readonly CancellationTokenSource _disposeCts = new CancellationTokenSource();

        private readonly MqttV2MessageDispatcher _messageDispatcher;
        private readonly MqttV2ShutdownCoordinator _shutdownCoordinator;

        private readonly Action<string> _log;
        private readonly Action<string> _logWarning;
        private readonly Action<string> _logError;
        private readonly Action<Exception> _logException;
        private TaskCompletionSource<bool> _readySignal = CreateReadySignal();

        private int _disposeGuard;
        private volatile bool _suppressConnectedResubscribe;
        private MqttV2ClientState _state = MqttV2ClientState.NotStarted;

        public MqttV2ClientRuntime(
            MqttV2ClientProfile profile,
            Action<string> log = null,
            Action<string> logWarning = null,
            Action<string> logError = null,
            Action<Exception> logException = null)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));

            _log = log ?? (_ => { });
            _logWarning = logWarning ?? (_ => { });
            _logError = logError ?? (_ => { });
            _logException = logException ?? (_ => { });
            _shutdownCoordinator = new MqttV2ShutdownCoordinator(_logError);
            _messageDispatcher = new MqttV2MessageDispatcher(
                _subscriptions,
                _typedDataRegistry,
                _rawInbox,
                _logError,
                _logException);

            var factory = new MqttFactory();
            _client = factory.CreateManagedMqttClient();
            _client.ConnectedAsync += HandleConnectedAsync;
            _client.DisconnectedAsync += HandleDisconnectedAsync;
            _client.ApplicationMessageReceivedAsync += HandleApplicationMessageReceivedAsync;
        }

        public MqttV2ClientProfile Profile { get; }
        public MqttV2ClientState State
        {
            get
            {
                lock (_stateGate)
                {
                    return _state;
                }
            }
        }

        public bool IsStarted => !IsDisposeRequested && _client.IsStarted;
        public bool IsConnected => !IsDisposeRequested && _client.IsConnected;
        public bool IsReady => !IsDisposeRequested && State == MqttV2ClientState.Ready;

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
                    if (_state == MqttV2ClientState.Starting)
                        return;

                    if (_client.IsStarted)
                    {
                        reuseStartedClient = true;
                        reuseConnectedClient = _client.IsConnected;
                    }
                    else
                    {
                        _state = MqttV2ClientState.Starting;
                    }
                }

                if (reuseStartedClient)
                {
                    if (reuseConnectedClient)
                    {
                        await ResubscribeAllTopicsAsync(operationToken);
                        SetState(MqttV2ClientState.Ready);
                    }
                    else
                    {
                        // StartAsync contract is "managed client started" (not "already connected").
                        // If the client is already started but transport is reconnecting, returning is expected.
                        SetState(MqttV2ClientState.Reconnecting);
                    }

                    return;
                }

                _suppressConnectedResubscribe = true;
                try
                {
                    var clientId = CreateClientId();
                    var transport = Profile.ConnectionOptions.Transport;
                    _log($"[MQTT-V2] Starting. clientId={clientId} broker={Profile.BrokerHost}:{Profile.BrokerPort} transport={transport.Kind}");

                    // ManagedClient reuses the same options for automatic reconnect cycles.
                    var managedOptions = MqttV2RuntimeClientOptionsFactory.Build(
                        Profile,
                        Profile.ConnectionOptions,
                        clientId,
                        logWarning: _logWarning);

                    await MqttV2AsyncUtils.AwaitWithCancellation(_client.StartAsync(managedOptions), operationToken);
                    await ResubscribeAllTopicsAsync(operationToken);
                    SetState(MqttV2ClientState.Ready);
                }
                finally
                {
                    _suppressConnectedResubscribe = false;
                }
            }
            catch
            {
                SetState(MqttV2ClientState.Disconnected);
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
                        if (_state == MqttV2ClientState.Ready)
                            return;

                        waitTask = _readySignal.Task;
                    }

                    await MqttV2AsyncUtils.AwaitWithCancellation(waitTask, operationToken);
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
                _logError($"[MQTT-V2] Dispose shutdown failure: {ex.Message}");
                _logException(ex);
            }

            try
            {
                _rawInbox.Clear();
                _typedDataRegistry.CancelAll();
            }
            catch (Exception ex)
            {
                _logError($"[MQTT-V2] Dispose cleanup failure: {ex.Message}");
                _logException(ex);
            }

            SetState(MqttV2ClientState.Disposed);

            if (_client is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else
                (_client as IDisposable)?.Dispose();

            _disposeCts.Dispose();
            _subscriptionGate.Dispose();
            _resubscribeGate.Dispose();
            _startStopGate.Dispose();
        }

        private async Task DisconnectCoreAsync(CancellationToken cancellationToken, bool allowWhenDisposed = false)
        {
            if (IsDisposeRequested && !allowWhenDisposed)
                return;

            if (!_client.IsStarted)
            {
                SetState(MqttV2ClientState.Stopped);
                return;
            }

            // Set Stopped before StopAsync so that the DisconnectedAsync event fired by MQTTnet
            // during the clean disconnect is treated as intentional and suppressed in OnDisconnected.
            SetState(MqttV2ClientState.Stopped);
            await MqttV2AsyncUtils.AwaitWithCancellation(_client.StopAsync(true), cancellationToken);
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

                SetState(MqttV2ClientState.ResubscribePending);
                foreach (var topic in _subscriptions.SnapshotTopics())
                {
                    if (!_subscriptions.TryGetSubscribeQos(topic, out var subscribeQos))
                        continue;

                    await SubscribeNetworkAsync(topic, subscribeQos, cancellationToken);
                }

                SetState(MqttV2ClientState.Ready);
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
            var topicFilter = new MqttTopicFilterBuilder()
                .WithTopic(topic)
                .WithQualityOfServiceLevel(subscribeQos)
                .Build();

            await MqttV2AsyncUtils.AwaitWithCancellation(_client.SubscribeAsync(new[] { topicFilter }), cancellationToken);
        }

        private async Task UnsubscribeNetworkAsync(string topic, CancellationToken cancellationToken)
        {
            await MqttV2AsyncUtils.AwaitWithCancellation(_client.UnsubscribeAsync(new[] { topic }), cancellationToken);
        }

        private string CreateClientId()
        {
            if (Profile.ClientIdPolicy == MqttV2ClientIdPolicy.Fixed)
                return Profile.FixedClientId;

            return $"UnityMqttV2_{Guid.NewGuid():N}";
        }

        private void EnsureStarted()
        {
            if (!_client.IsStarted)
                throw new InvalidOperationException("Client is not started. Call StartAsync first.");
        }

        private void ThrowIfDisposed()
        {
            if (IsDisposeRequested)
                throw new ObjectDisposedException(nameof(MqttV2ClientRuntime));
        }

        private void SetState(MqttV2ClientState next)
        {
            TaskCompletionSource<bool> readySignalToSet = null;
            lock (_stateGate)
            {
                if (IsDisposeRequested && next != MqttV2ClientState.Disposed)
                    return;

                if (_state == next)
                    return;

                _state = next;

                if (next == MqttV2ClientState.Ready)
                {
                    readySignalToSet = _readySignal;
                }
                else if (next != MqttV2ClientState.Disposed && _readySignal.Task.IsCompleted)
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
                throw new ObjectDisposedException(nameof(MqttV2ClientRuntime));

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
                throw new ObjectDisposedException(nameof(MqttV2ClientRuntime));
            }

            return linkedCts.Token;
        }

        private async Task EnsureNetworkSubscriptionConsistencyAsync(
            string topic,
            MqttQualityOfServiceLevel subscribeQos)
        {
            try
            {
                if (IsDisposeRequested || !_client.IsConnected)
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

        private bool IsDisposeRequested => Volatile.Read(ref _disposeGuard) != 0;
    }
}
