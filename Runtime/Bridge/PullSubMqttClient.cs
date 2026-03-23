using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Serialization;
using PullSub.Core;

namespace PullSub.Bridge
{
    public sealed class PullSubMqttClient : MonoBehaviour
    {
        private const string DefaultBrokerHost = "127.0.0.1";
        private const int DefaultBrokerPort = 1883;

        [Header("Connection")]
        [SerializeField] private PullSubClientConnectionSettings _connectionSettings = new PullSubClientConnectionSettings();

        [FormerlySerializedAs("_brokerHost")]
        [SerializeField] [HideInInspector] private string _legacyBrokerHost = DefaultBrokerHost;
        [FormerlySerializedAs("_brokerPort")]
        [SerializeField] [HideInInspector] private int _legacyBrokerPort = DefaultBrokerPort;
        [FormerlySerializedAs("_username")]
        [SerializeField] [HideInInspector] private string _legacyUsername = "";
        [FormerlySerializedAs("_password")]
        [SerializeField] [HideInInspector] private string _legacyPassword = "";
        [FormerlySerializedAs("_clientIdPolicy")]
        [SerializeField] [HideInInspector] private MqttClientIdPolicy _legacyClientIdPolicy = MqttClientIdPolicy.RandomPerStart;
        [FormerlySerializedAs("_fixedClientId")]
        [SerializeField] [HideInInspector] private string _legacyFixedClientId = "";
        [SerializeField] [HideInInspector] private bool _legacyConnectionMigrated;

        [Header("Lifecycle")]
        [SerializeField] private bool _startOnEnable = true;
        [SerializeField] private bool _stopOnDisable = false;

        private bool _initialized;
        private bool _destroyed;
        private CancellationTokenSource _lifecycleCts;

        public PullSubRuntime Runtime { get; private set; }

        /// <summary>
        /// Registers a raw message handler for the specified topic.
        /// This method requires the client to be enabled.
        /// Call this method from Unity main thread. The handler runs on the await continuation
        /// of <see cref="PullSubRuntime.ReceiveQueueAsync"/>, which uses UnitySynchronizationContext.
        /// </summary>
        /// <param name="topic">Exact-match topic name.</param>
        /// <param name="options">Queue options. maxQueueDepth must be greater than zero.</param>
        /// <param name="handler">Async handler invoked for each received message.</param>
        /// <param name="cancellationToken">Optional caller cancellation token.</param>
        public async Task RegisterHandlerAsync(
            string topic,
            PullSubQueueOptions options,
            Func<PullSubQueueMessage, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
        {
            ValidateRawHandlerArguments(topic, options, handler);
            var runtime = EnsureRuntimeForRawHandlerRegistration();
            var operationToken = CreateLinkedOperationToken(_lifecycleCts.Token, cancellationToken, out var linkedCts);

            try
            {
                await runtime.RegisterHandlerAsync(topic, options, handler, operationToken);
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
        public async Task<PullSubQueueHandlerRegistration> RegisterHandlerLeaseAsync(
            string topic,
            PullSubQueueOptions options,
            Func<PullSubQueueMessage, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
        {
            ValidateRawHandlerArguments(topic, options, handler);
            var runtime = EnsureRuntimeForRawHandlerRegistration();
            var operationToken = CreateLinkedOperationToken(_lifecycleCts.Token, cancellationToken, out var linkedCts);

            try
            {
                var registration = await runtime.RegisterHandlerLeaseAsync(topic, options, handler, operationToken);

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
        public Task<PullSubQueueHandlerRegistration> RegisterHandlerLeaseAsync(
            string topic,
            PullSubQueueOptions options,
            Action<PullSubQueueMessage> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return RegisterHandlerLeaseAsync(
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
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PullSubMqttClient] Start failed: {ex.Message}");
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
                Debug.LogError($"[PullSubMqttClient] Stop failed: {ex.Message}");
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
                throw new ObjectDisposedException(nameof(PullSubMqttClient), "PullSubMqttClient is already destroyed.");

            if (_initialized && Runtime != null)
                return;

            var connectionOptions = _connectionSettings.ToConnectionOptions();
            WarnIfInsecureTlsOptionsEnabled(connectionOptions);

            var profile = new MqttClientProfile(
                brokerHost: _connectionSettings.BrokerHost,
                brokerPort: _connectionSettings.BrokerPort,
                clientIdPolicy: _connectionSettings.ClientIdPolicy,
                fixedClientId: _connectionSettings.FixedClientId,
                connectionOptions: connectionOptions);

            Runtime = new PullSubRuntime(
                transport: new MqttTransport(profile, connectionOptions),
                profile,
                log: message => Debug.Log(message),
                logWarning: message => Debug.LogWarning(message),
                logError: message => Debug.LogError(message),
                logException: ex => Debug.LogException(ex));

            _initialized = true;
        }

        private static void WarnIfInsecureTlsOptionsEnabled(MqttConnectionOptions connectionOptions)
        {
            var tls = connectionOptions.Tls;
            var transport = connectionOptions.Transport;

            // Ws + TLS 有効は Transport ログで警告済みのためここでは追加警告しない
            if (!tls.Enabled || transport.Kind == MqttTransportKind.Ws)
                return;

            if (tls.AllowUntrustedCertificates || tls.IgnoreCertificateChainErrors || tls.IgnoreCertificateRevocationErrors)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.LogWarning(
                    "[PullSubMqttClient] Insecure TLS validation options are enabled. " +
                    "This may be acceptable for local development, but do not ship these settings to production.");
#else
                Debug.LogWarning(
                    "[PullSubMqttClient] Insecure TLS validation options are enabled in a non-development build. " +
                    "Use strict certificate validation for production deployments.");
#endif
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
                Debug.LogError($"[PullSubMqttClient] Release failed: {ex.Message}");
                Debug.LogException(ex);
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
            PullSubQueueOptions options,
            Func<PullSubQueueMessage, CancellationToken, Task> handler)
        {
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("topic is required.", nameof(topic));

            if (options == null)
                throw new ArgumentNullException(nameof(options));

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
        }

        private PullSubRuntime EnsureRuntimeForRawHandlerRegistration()
        {
            EnsureRuntime();

            var runtime = Runtime;
            if (runtime == null)
                throw new InvalidOperationException("PullSubMqttClient runtime is not initialized.");

            if (_lifecycleCts == null)
            {
                throw new InvalidOperationException(
                    "PullSubMqttClient is not enabled. Call raw handler registration APIs after OnEnable.");
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
                _connectionSettings = new PullSubClientConnectionSettings();
        }

        private bool HasLegacyConnectionOverrides()
        {
            return !string.Equals(_legacyBrokerHost, DefaultBrokerHost, StringComparison.Ordinal)
                || _legacyBrokerPort != DefaultBrokerPort
                || !string.IsNullOrWhiteSpace(_legacyUsername)
                || !string.IsNullOrWhiteSpace(_legacyPassword)
                || _legacyClientIdPolicy != MqttClientIdPolicy.RandomPerStart
                || !string.IsNullOrWhiteSpace(_legacyFixedClientId);
        }
    }
}
