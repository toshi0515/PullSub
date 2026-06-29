using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Serialization;
using PullSub.Core;
using PullSub.Mqtt;

namespace PullSub.Bridge
{
    public sealed class PullSubMqttClient : MonoBehaviour
    {
        private const string DefaultBrokerHost = "127.0.0.1";
        private const int DefaultBrokerPort = 1883;

        [Header("Connection")]
        [SerializeField] private PullSubClientConnectionSettings _connectionSettings = new PullSubClientConnectionSettings();

        [FormerlySerializedAs("_brokerHost")]
        [SerializeField][HideInInspector] private string _legacyBrokerHost = DefaultBrokerHost;
        [FormerlySerializedAs("_brokerPort")]
        [SerializeField][HideInInspector] private int _legacyBrokerPort = DefaultBrokerPort;
        [FormerlySerializedAs("_username")]
        [SerializeField][HideInInspector] private string _legacyUsername = "";
        [FormerlySerializedAs("_password")]
        [SerializeField][HideInInspector] private string _legacyPassword = "";
        [FormerlySerializedAs("_clientIdPolicy")]
        [SerializeField][HideInInspector] private MqttClientIdPolicy _legacyClientIdPolicy = MqttClientIdPolicy.RandomPerStart;
        [FormerlySerializedAs("_fixedClientId")]
        [SerializeField][HideInInspector] private string _legacyFixedClientId = "";
        [SerializeField][HideInInspector] private bool _legacyConnectionMigrated;

        [Header("Debug Mode")]
        [SerializeField] private bool _debugMode;
        [SerializeField] private PullSubClientConnectionSettings _debugConnectionSettings = new PullSubClientConnectionSettings();

        [Header("Lifecycle")]
        [SerializeField] private bool _startOnEnable = true;
        [SerializeField] private bool _stopOnDisable = false;

        private bool _initialized;
        private bool _destroyed;
        private CancellationTokenSource _lifecycleCts;

        public PullSubRuntime Runtime { get; private set; }

        internal (
            string Host,
            int Port,
            string ClientIdPolicy,
            string ClientIdText,
            bool IsDebugActive,
            bool IsDebugSuppressed,
            string DefaultHost,
            string DebugHost) GetConnectionSummaryForDebugMonitor()
        {
            if (_connectionSettings == null)
                return ("unknown", 0, "Unknown", "(auto)", false, false, "unknown", "unknown");

            var isDebugActive = IsDebugModeActive();
            var isDebugSuppressed = _debugMode && !isDebugActive;
            var effective = GetEffectiveConnectionSettings();

            var host = string.IsNullOrWhiteSpace(effective.BrokerHost)
                ? "unknown"
                : effective.BrokerHost;
            var defaultHost = string.IsNullOrWhiteSpace(_connectionSettings.BrokerHost)
                ? "unknown"
                : _connectionSettings.BrokerHost;
            var debugHost = _debugConnectionSettings != null && !string.IsNullOrWhiteSpace(_debugConnectionSettings.BrokerHost)
                ? _debugConnectionSettings.BrokerHost
                : "unknown";
            var clientIdText = string.IsNullOrWhiteSpace(effective.FixedClientId)
                ? "(auto)"
                : effective.FixedClientId;

            return (
                host,
                effective.BrokerPort,
                effective.ClientIdPolicy.ToString(),
                clientIdText,
                isDebugActive,
                isDebugSuppressed,
                defaultHost,
                debugHost);
        }

        private void Awake()
        {
            EnsureConnectionSettings();
            EnsureDebugConnectionSettings();
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

            LogDebugModeSelection();

            var effective = GetEffectiveConnectionSettings();
            var connectionOptions = effective.ToConnectionOptions();
            WarnIfInsecureTlsOptionsEnabled(connectionOptions);

            Runtime = new PullSubRuntime(
                transport: new MqttTransport(
                    brokerHost: effective.BrokerHost,
                    brokerPort: effective.BrokerPort,
                    connectionOptions: connectionOptions,
                    clientIdPolicy: effective.ClientIdPolicy,
                    fixedClientId: effective.FixedClientId),
                log: message => Debug.Log(message),
                logWarning: message => Debug.LogWarning(message),
                logError: message => Debug.LogError(message),
                logException: ex => Debug.LogException(ex),
                requestOptions: effective.ToRequestOptions(),
                runtimeOptions: effective.ToRuntimeOptions());

            _initialized = true;
        }

        private static void WarnIfInsecureTlsOptionsEnabled(MqttConnectionOptions connectionOptions)
        {
            var tls = connectionOptions.Tls;
            var transport = connectionOptions.Transport;

            // Ws + TLS invalid combination is rejected by MqttConnectionOptions before runtime starts.
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

        private void LogDebugModeSelection()
        {
            if (!_debugMode)
                return;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var defaultHost = _connectionSettings?.BrokerHost ?? "(unknown)";
            var defaultPort = _connectionSettings?.BrokerPort ?? 0;
            var debugHost = _debugConnectionSettings?.BrokerHost ?? "(unknown)";
            var debugPort = _debugConnectionSettings?.BrokerPort ?? 0;
            Debug.LogWarning(
                $"[PullSubMqttClient] Debug Mode is enabled. " +
                $"Connecting to debug settings ({debugHost}:{debugPort}) " +
                $"instead of default settings ({defaultHost}:{defaultPort}).");
#else
            Debug.LogWarning(
                "[PullSubMqttClient] Debug Mode is enabled in Inspector but has been suppressed " +
                "in this non-development build. Connecting with default settings.");
#endif
        }

        private bool IsDebugModeActive()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            return _debugMode;
#else
            return false;
#endif
        }

        private PullSubClientConnectionSettings GetEffectiveConnectionSettings()
        {
            if (!IsDebugModeActive())
                return _connectionSettings;

            EnsureDebugConnectionSettings();
            return _debugConnectionSettings;
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

        private void EnsureDebugConnectionSettings()
        {
            if (_debugConnectionSettings == null)
                _debugConnectionSettings = new PullSubClientConnectionSettings();
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
