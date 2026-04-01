using System;
using System.Security.Authentication;
using UnityEngine;
using UnityEngine.Serialization;
using PullSub.Core;

namespace PullSub.Bridge
{
    [Serializable]
    public sealed class PullSubClientTlsSettings
    {
        [Tooltip("TLS オプションを有効化します。\n" +
            "Wss トランスポートを使用する場合: Enabled を ON にすることで AllowUntrustedCertificates などの証明書オプションが適用されます。" +
            " Enabled = OFF のままでも wss:// 接続は TLS で行われますが証明書オプションはすべて無視されます（OS デフォルトの厳格な証明書検証が適用されます）。")]
        [SerializeField] private bool _enabled;

        [Tooltip("For development/testing only. Do not enable in production unless you fully understand the risk.")]
        [SerializeField] private bool _allowUntrustedCertificates;

        [Tooltip("For development/testing only. Accepts invalid certificate chains.")]
        [SerializeField] private bool _ignoreCertificateChainErrors;

        [Tooltip("For development/testing only. Skips revocation checks.")]
        [SerializeField] private bool _ignoreCertificateRevocationErrors;

        [Tooltip("Optional TLS target host (SNI). Leave empty to use broker host.")]
        [SerializeField] private string _targetHost = "";

        [Header("TLS Advanced")]
        [Tooltip("Default is None (OS chooses the best protocol).")]
        [SerializeField] private SslProtocols _sslProtocols = SslProtocols.None;

        public MqttTlsOptions ToCoreOptions()
        {
            if (!_enabled)
                return MqttTlsOptions.Disabled;

            return new MqttTlsOptions(
                enabled: true,
                allowUntrustedCertificates: _allowUntrustedCertificates,
                ignoreCertificateChainErrors: _ignoreCertificateChainErrors,
                ignoreCertificateRevocationErrors: _ignoreCertificateRevocationErrors,
                targetHost: string.IsNullOrEmpty(_targetHost) ? null : _targetHost,
                sslProtocols: _sslProtocols);
        }
    }

    [Serializable]
    public sealed class PullSubClientKeepAliveSettings
    {
        [Tooltip("Enable explicit keep alive period. Disable to use MQTTnet default behavior.")]
        [SerializeField] private bool _enabled = true;

        [Tooltip("MQTT keep alive period in seconds. Allowed range: 1-3600.")]
        [SerializeField] private int _seconds = MqttKeepAliveOptions.DefaultSeconds;

        public MqttKeepAliveOptions ToCoreOptions()
        {
            if (!_enabled)
                return MqttKeepAliveOptions.Disabled;

            return new MqttKeepAliveOptions(enabled: true, seconds: _seconds);
        }

        internal void LoadLegacyEnabledSeconds(int seconds)
        {
            _enabled = true;
            _seconds = seconds;
        }
    }

    [Serializable]
    public sealed class PullSubClientWillSettings
    {
        [Tooltip("Enable Last Will and Testament message on unexpected disconnect.")]
        [SerializeField] private bool _enabled;

        [Tooltip("Will topic (exact-match only). Wildcards are not supported.")]
        [SerializeField] private string _topic = "status/offline";

        [Tooltip("Will payload encoded as UTF-8 text.")]
        [SerializeField] private string _payloadUtf8 = "{\"status\":\"offline\"}";

        [SerializeField] private PullSubQualityOfServiceLevel _qos = PullSubQualityOfServiceLevel.AtMostOnce;
        [SerializeField] private bool _retain;

        public MqttWillOptions ToCoreOptions()
        {
            if (!_enabled)
                return MqttWillOptions.Disabled;

            return new MqttWillOptions(
                enabled: true,
                topic: _topic,
                payloadUtf8: _payloadUtf8,
                qos: _qos,
                retain: _retain);
        }
    }

    [Serializable]
    public sealed class PullSubClientTransportSettings
    {
        [Tooltip("接続トランスポートプロトコルを選択します。\nTcp: MQTT over TCP（既定）\nWs: MQTT over WebSocket（ws://）\nWss: MQTT over Secure WebSocket（wss://）")]
        [SerializeField] private MqttTransportKind _kind = MqttTransportKind.Tcp;

        [Tooltip("WebSocket のパス（Ws/Wss のみ）。省略すると '/' が使われます。ブローカーに合わせて設定してください（例: /mqtt）。")]
        [SerializeField] private string _webSocketPath = "";

        [Tooltip("WebSocket のサブプロトコル（Ws/Wss のみ）。省略すると 'mqtt' が使われます。")]
        [SerializeField] private string _webSocketSubprotocol = "";

        public MqttTransportOptions ToCoreOptions()
        {
            return new MqttTransportOptions(
                kind: _kind,
                webSocketPath: string.IsNullOrEmpty(_webSocketPath) ? null : _webSocketPath,
                webSocketSubprotocol: string.IsNullOrEmpty(_webSocketSubprotocol) ? null : _webSocketSubprotocol);
        }
    }

    [Serializable]
    public sealed class PullSubClientConnectionSettings
    {
        [Header("Broker")]
        [SerializeField] private string _brokerHost = "127.0.0.1";
        [SerializeField] private int _brokerPort = 1883;
        [SerializeField] private string _username = "";
        [SerializeField] private string _password = "";

        [Header("Client")]
        [SerializeField] private MqttClientIdPolicy _clientIdPolicy = MqttClientIdPolicy.RandomPerStart;
        [SerializeField] private string _fixedClientId = "";

        [Header("Connection")]
        [Tooltip("Delay before auto reconnect after disconnect (seconds). Keep this lower than Keep Alive for faster recovery.")]
        [SerializeField] private int _reconnectDelaySeconds = 5;
        [SerializeField] private bool _useCleanSession = true;

        [Header("Transport")]
        [SerializeField] private PullSubClientTransportSettings _transportSettings = new PullSubClientTransportSettings();

        [Header("Keep Alive")]
        [SerializeField] private PullSubClientKeepAliveSettings _keepAliveSettings = new PullSubClientKeepAliveSettings();

        [Header("Will (LWT)")]
        [SerializeField] private PullSubClientWillSettings _willSettings = new PullSubClientWillSettings();

        [FormerlySerializedAs("_keepAliveSeconds")]
        [SerializeField] [HideInInspector] private int _legacyKeepAliveSeconds = MqttKeepAliveOptions.DefaultSeconds;
        [SerializeField] [HideInInspector] private bool _legacyKeepAliveMigrated;

        [Header("TLS")]
        [SerializeField] private PullSubClientTlsSettings _tlsSettings = new PullSubClientTlsSettings();

        public string BrokerHost => _brokerHost;
        public int BrokerPort => _brokerPort;
        public MqttClientIdPolicy ClientIdPolicy => _clientIdPolicy;
        public string FixedClientId => _fixedClientId;

        public MqttConnectionOptions ToConnectionOptions()
        {
            var credentials = string.IsNullOrWhiteSpace(_username)
                ? MqttCredentials.Anonymous
                : new MqttCredentials(_username, _password);

            var tlsOptions = _tlsSettings != null
                ? _tlsSettings.ToCoreOptions()
                : MqttTlsOptions.Disabled;

            var keepAliveOptions = _keepAliveSettings != null
                ? _keepAliveSettings.ToCoreOptions()
                : MqttKeepAliveOptions.Default;

            var willOptions = _willSettings != null
                ? _willSettings.ToCoreOptions()
                : MqttWillOptions.Disabled;

            var transportOptions = _transportSettings != null
                ? _transportSettings.ToCoreOptions()
                : MqttTransportOptions.TcpDefault;

            return new MqttConnectionOptions(
                credentials: credentials,
                keepAlive: keepAliveOptions,
                reconnectDelaySeconds: _reconnectDelaySeconds,
                useCleanSession: _useCleanSession,
                tls: tlsOptions,
                will: willOptions,
                transport: transportOptions);
        }

        internal void MigrateLegacyKeepAliveIfNeeded()
        {
            EnsureKeepAliveSettings();

            if (_legacyKeepAliveMigrated)
                return;

            if (_legacyKeepAliveSeconds >= MqttKeepAliveOptions.MinSeconds
                && _legacyKeepAliveSeconds <= MqttKeepAliveOptions.MaxSeconds)
            {
                _keepAliveSettings.LoadLegacyEnabledSeconds(_legacyKeepAliveSeconds);
            }

            _legacyKeepAliveMigrated = true;
        }

        internal void LoadLegacy(
            string brokerHost,
            int brokerPort,
            string username,
            string password,
            MqttClientIdPolicy clientIdPolicy,
            string fixedClientId)
        {
            _brokerHost = string.IsNullOrWhiteSpace(brokerHost) ? "127.0.0.1" : brokerHost;
            _brokerPort = brokerPort > 0 ? brokerPort : 1883;
            _username = username ?? string.Empty;
            _password = password ?? string.Empty;
            _clientIdPolicy = clientIdPolicy;
            _fixedClientId = fixedClientId ?? string.Empty;
        }

        private void EnsureKeepAliveSettings()
        {
            if (_keepAliveSettings == null)
                _keepAliveSettings = new PullSubClientKeepAliveSettings();
        }
    }
}