using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Serialization;
using UnityMqtt.V2.Core;

namespace UnityMqtt.V2.Bridge
{
    /// <summary>
    /// <see cref="MqttV2Bridge"/> Inspector での Codec 選択肢。Bridge 専用の設定型です。
    /// Runtime を直接生成する場合は <see cref="MqttV2JsonPayloadCodec"/> または
    /// <see cref="MqttV2FlatJsonPayloadCodec"/> を直接インスタンス化してください。
    /// </summary>
    public enum MqttV2BridgeCodecKind
    {
        JsonEnvelope = 0,
        FlatJson = 1,
    }

    public sealed class MqttV2Bridge : MonoBehaviour
    {
        private const string DefaultBrokerHost = "127.0.0.1";
        private const int DefaultBrokerPort = 1883;

        [Header("Connection")]
        [SerializeField] private MqttV2BridgeConnectionSettings _connectionSettings = new MqttV2BridgeConnectionSettings();

        [FormerlySerializedAs("_brokerHost")]
        [SerializeField] [HideInInspector] private string _legacyBrokerHost = DefaultBrokerHost;
        [FormerlySerializedAs("_brokerPort")]
        [SerializeField] [HideInInspector] private int _legacyBrokerPort = DefaultBrokerPort;
        [FormerlySerializedAs("_username")]
        [SerializeField] [HideInInspector] private string _legacyUsername = "";
        [FormerlySerializedAs("_password")]
        [SerializeField] [HideInInspector] private string _legacyPassword = "";
        [FormerlySerializedAs("_clientIdPolicy")]
        [SerializeField] [HideInInspector] private MqttV2ClientIdPolicy _legacyClientIdPolicy = MqttV2ClientIdPolicy.RandomPerStart;
        [FormerlySerializedAs("_fixedClientId")]
        [SerializeField] [HideInInspector] private string _legacyFixedClientId = "";
        [SerializeField] [HideInInspector] private bool _legacyConnectionMigrated;

        [Header("Payload")]
        [SerializeField] private MqttV2BridgeCodecKind _payloadCodecKind = MqttV2BridgeCodecKind.JsonEnvelope;

        [Header("Data")]
        [SerializeField] private string[] _autoDataTopics = new string[0];
        [SerializeField] private MqttV2TopicUpdateMode _defaultUpdateMode = MqttV2TopicUpdateMode.Differential;

        [Header("Lifecycle")]
        [SerializeField] private bool _startOnEnable = true;
        [SerializeField] private bool _stopOnDisable = false;

        private bool _initialized;
        private bool _destroyed;
        private CancellationTokenSource _lifecycleCts;

        public MqttV2ClientRuntime Runtime { get; private set; }

        /// <summary>
        /// Registers a raw message handler for the specified topic.
        /// This method requires the bridge to be enabled.
        /// Call this method from Unity main thread. The handler runs on the await continuation
        /// of <see cref="MqttV2ClientRuntime.ReceiveRawAsync"/>, which uses UnitySynchronizationContext.
        /// </summary>
        /// <param name="topic">Exact-match topic name.</param>
        /// <param name="options">Raw queue options. maxQueueDepth must be greater than zero.</param>
        /// <param name="handler">Async handler invoked for each received message.</param>
        /// <param name="cancellationToken">Optional caller cancellation token.</param>
        public async Task RegisterRawHandlerAsync(
            string topic,
            MqttV2RawSubscriptionOptions options,
            Func<MqttV2RawMessage, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
        {
            ValidateRawHandlerArguments(topic, options, handler);
            var runtime = EnsureRuntimeForRawHandlerRegistration();
            var operationToken = CreateLinkedOperationToken(_lifecycleCts.Token, cancellationToken, out var linkedCts);

            try
            {
                await runtime.RegisterRawHandlerAsync(topic, options, handler, operationToken);
            }
            finally
            {
                linkedCts?.Dispose();
            }
        }

        /// <summary>
        /// Registers a raw message handler and returns an async-disposable handle.
        /// The returned handle is created only after the underlying subscribe operation succeeds.
        /// </summary>
        public async Task<MqttV2RawHandlerRegistration> RegisterRawHandlerLeaseAsync(
            string topic,
            MqttV2RawSubscriptionOptions options,
            Func<MqttV2RawMessage, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
        {
            ValidateRawHandlerArguments(topic, options, handler);
            var runtime = EnsureRuntimeForRawHandlerRegistration();
            var operationToken = CreateLinkedOperationToken(_lifecycleCts.Token, cancellationToken, out var linkedCts);

            try
            {
                var registration = await runtime.RegisterRawHandlerLeaseAsync(topic, options, handler, operationToken);

                // Keep bridge-linked CTS alive while the lease is active so lifecycle/caller cancellation
                // can continue to propagate into the Core lease loop.
                if (linkedCts != null)
                {
                    var linkedToDispose = linkedCts;
                    _ = registration.Completion.ContinueWith(
                        static (_, state) => ((CancellationTokenSource)state).Dispose(),
                        linkedToDispose,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    linkedCts = null;
                }

                return registration;
            }
            finally
            {
                linkedCts?.Dispose();
            }
        }

        /// <summary>
        /// Convenience overload for synchronous message handlers.
        /// </summary>
        public Task<MqttV2RawHandlerRegistration> RegisterRawHandlerLeaseAsync(
            string topic,
            MqttV2RawSubscriptionOptions options,
            Action<MqttV2RawMessage> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return RegisterRawHandlerLeaseAsync(
                topic,
                options,
                (message, _) =>
                {
                    handler(message);
                    return Task.CompletedTask;
                },
                cancellationToken);
        }

        private void Awake()
        {
            EnsureConnectionSettings();
            MigrateLegacyConnectionSettingsIfNeeded();
            _connectionSettings.MigrateLegacyKeepAliveIfNeeded();
            EnsureRuntime();
        }

        private async void OnEnable()
        {
            ResetLifecycleCts();

            if (!_startOnEnable)
                return;

            try
            {
                EnsureRuntime();
                await Runtime.StartAsync(_lifecycleCts.Token);
                await SubscribeAutoDataTopicsAsync(_lifecycleCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MqttV2Bridge] Start failed: {ex.Message}");
                Debug.LogException(ex);
            }
        }

        private async void OnDisable()
        {
            CancelLifecycleCts();

            if (!_stopOnDisable || Runtime == null)
                return;

            try
            {
                await Runtime.DisconnectAsync();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MqttV2Bridge] Stop failed: {ex.Message}");
            }
        }

        private async void OnDestroy()
        {
            _destroyed = true;
            CancelLifecycleCts();
            await ReleaseRuntimeAsync();
        }

        private void EnsureRuntime()
        {
            if (_destroyed)
                throw new ObjectDisposedException(nameof(MqttV2Bridge), "MqttV2Bridge is already destroyed.");

            if (_initialized && Runtime != null)
                return;

            var payloadCodec = CreatePayloadCodec(_payloadCodecKind);
            var connectionOptions = _connectionSettings.ToConnectionOptions();
            WarnIfInsecureTlsOptionsEnabled(connectionOptions);

            var profile = new MqttV2ClientProfile(
                brokerHost: _connectionSettings.BrokerHost,
                brokerPort: _connectionSettings.BrokerPort,
                payloadCodecId: payloadCodec.Id,
                clientIdPolicy: _connectionSettings.ClientIdPolicy,
                fixedClientId: _connectionSettings.FixedClientId,
                connectionOptions: connectionOptions);

            Runtime = new MqttV2ClientRuntime(
                profile,
                payloadCodec: payloadCodec,
                log: message => Debug.Log(message),
                logError: message => Debug.LogError(message),
                logException: ex => Debug.LogException(ex));

            _initialized = true;
        }

        private static void WarnIfInsecureTlsOptionsEnabled(MqttV2ConnectionOptions connectionOptions)
        {
            var tls = connectionOptions.Tls;
            if (!tls.Enabled)
                return;

            if (tls.AllowUntrustedCertificates || tls.IgnoreCertificateChainErrors || tls.IgnoreCertificateRevocationErrors)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.LogWarning(
                    "[MqttV2Bridge] Insecure TLS validation options are enabled. " +
                    "This may be acceptable for local development, but do not ship these settings to production.");
#else
                Debug.LogWarning(
                    "[MqttV2Bridge] Insecure TLS validation options are enabled in a non-development build. " +
                    "Use strict certificate validation for production deployments.");
#endif
            }
        }

        private static IMqttV2PayloadCodec CreatePayloadCodec(MqttV2BridgeCodecKind kind)
        {
            switch (kind)
            {
                case MqttV2BridgeCodecKind.JsonEnvelope:
                    return new MqttV2JsonPayloadCodec();
                case MqttV2BridgeCodecKind.FlatJson:
                    return new MqttV2FlatJsonPayloadCodec();
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }

        private async Task ReleaseRuntimeAsync()
        {
            if (Runtime == null)
                return;

            var runtime = Runtime;
            _initialized = false;
            Runtime = null;

            try
            {
                await runtime.DisposeAsync();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MqttV2Bridge] Release failed: {ex.Message}");
                Debug.LogException(ex);
            }
        }

        private async Task SubscribeAutoDataTopicsAsync(CancellationToken cancellationToken)
        {
            if (_autoDataTopics == null)
                return;

            foreach (var topic in _autoDataTopics)
            {
                if (string.IsNullOrWhiteSpace(topic))
                    continue;

                await Runtime.SubscribeDataAsync(topic, _defaultUpdateMode, cancellationToken);
            }
        }

        private static CancellationToken CreateLinkedOperationToken(
            CancellationToken first,
            CancellationToken second,
            out CancellationTokenSource linkedCts)
        {
            if (!first.CanBeCanceled)
            {
                linkedCts = null;
                return second;
            }

            if (!second.CanBeCanceled)
            {
                linkedCts = null;
                return first;
            }

            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(first, second);
            return linkedCts.Token;
        }

        private static void ValidateRawHandlerArguments(
            string topic,
            MqttV2RawSubscriptionOptions options,
            Func<MqttV2RawMessage, CancellationToken, Task> handler)
        {
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("topic is required.", nameof(topic));

            if (options == null)
                throw new ArgumentNullException(nameof(options));

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
        }

        private MqttV2ClientRuntime EnsureRuntimeForRawHandlerRegistration()
        {
            EnsureRuntime();

            var runtime = Runtime;
            if (runtime == null)
                throw new InvalidOperationException("MqttV2Bridge runtime is not initialized.");

            if (_lifecycleCts == null)
            {
                throw new InvalidOperationException(
                    "MqttV2Bridge is not enabled. Call raw handler registration APIs after OnEnable.");
            }

            return runtime;
        }

        private void ResetLifecycleCts()
        {
            CancelLifecycleCts();
            _lifecycleCts = new CancellationTokenSource();
        }

        private void CancelLifecycleCts()
        {
            if (_lifecycleCts == null)
                return;

            if (!_lifecycleCts.IsCancellationRequested)
                _lifecycleCts.Cancel();

            _lifecycleCts.Dispose();
            _lifecycleCts = null;
        }

        private void MigrateLegacyConnectionSettingsIfNeeded()
        {
            if (_legacyConnectionMigrated)
                return;

            if (!HasLegacyConnectionOverrides())
            {
                _legacyConnectionMigrated = true;
                return;
            }

            _connectionSettings.LoadLegacy(
                _legacyBrokerHost,
                _legacyBrokerPort,
                _legacyUsername,
                _legacyPassword,
                _legacyClientIdPolicy,
                _legacyFixedClientId);

            _legacyConnectionMigrated = true;
        }

        private void EnsureConnectionSettings()
        {
            if (_connectionSettings == null)
                _connectionSettings = new MqttV2BridgeConnectionSettings();
        }

        private bool HasLegacyConnectionOverrides()
        {
            return !string.Equals(_legacyBrokerHost, DefaultBrokerHost, StringComparison.Ordinal)
                || _legacyBrokerPort != DefaultBrokerPort
                || !string.IsNullOrWhiteSpace(_legacyUsername)
                || !string.IsNullOrWhiteSpace(_legacyPassword)
                || _legacyClientIdPolicy != MqttV2ClientIdPolicy.RandomPerStart
                || !string.IsNullOrWhiteSpace(_legacyFixedClientId);
        }
    }
}
