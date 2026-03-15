using System;
using System.Security.Authentication;
using MQTTnet.Protocol;
using UnityEngine;
using UnityEngine.Serialization;
using UnityMqtt.V2.Core;

namespace UnityMqtt.V2.Bridge
{
    [Serializable]
    public sealed class MqttV2BridgeTlsSettings
    {
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

        public MqttV2TlsOptions ToCoreOptions()
        {
            if (!_enabled)
                return MqttV2TlsOptions.Disabled;

            return new MqttV2TlsOptions(
                enabled: true,
                allowUntrustedCertificates: _allowUntrustedCertificates,
                ignoreCertificateChainErrors: _ignoreCertificateChainErrors,
                ignoreCertificateRevocationErrors: _ignoreCertificateRevocationErrors,
                targetHost: string.IsNullOrEmpty(_targetHost) ? null : _targetHost,
                sslProtocols: _sslProtocols);
        }
    }

    [Serializable]
    public sealed class MqttV2BridgeKeepAliveSettings
    {
        [Tooltip("Enable explicit keep alive period. Disable to use MQTTnet default behavior.")]
        [SerializeField] private bool _enabled = true;

        [Tooltip("MQTT keep alive period in seconds. Allowed range: 1-3600.")]
        [SerializeField] private int _seconds = MqttV2KeepAliveOptions.DefaultSeconds;

        public MqttV2KeepAliveOptions ToCoreOptions()
        {
            if (!_enabled)
                return MqttV2KeepAliveOptions.Disabled;

            return new MqttV2KeepAliveOptions(enabled: true, seconds: _seconds);
        }

        internal void LoadLegacyEnabledSeconds(int seconds)
        {
            _enabled = true;
            _seconds = seconds;
        }
    }

    [Serializable]
    public sealed class MqttV2BridgeWillSettings
    {
        [Tooltip("Enable Last Will and Testament message on unexpected disconnect.")]
        [SerializeField] private bool _enabled;

        [Tooltip("Will topic (exact-match only). Wildcards are not supported.")]
        [SerializeField] private string _topic = "status/offline";

        [Tooltip("Will payload encoded as UTF-8 text.")]
        [SerializeField] private string _payloadUtf8 = "{\"status\":\"offline\"}";

        [SerializeField] private MqttQualityOfServiceLevel _qos = MqttQualityOfServiceLevel.AtMostOnce;
        [SerializeField] private bool _retain;

        public MqttV2WillOptions ToCoreOptions()
        {
            if (!_enabled)
                return MqttV2WillOptions.Disabled;

            return new MqttV2WillOptions(
                enabled: true,
                topic: _topic,
                payloadUtf8: _payloadUtf8,
                qos: _qos,
                retain: _retain);
        }
    }

    [Serializable]
    public sealed class MqttV2BridgeSubscriptionSettings
    {
        [Tooltip("QoS level used for all subscribe requests.")]
        [SerializeField] private MqttQualityOfServiceLevel _subscribeQos = MqttQualityOfServiceLevel.AtMostOnce;

        public MqttV2SubscriptionDefaults ToCoreOptions()
        {
            return new MqttV2SubscriptionDefaults(_subscribeQos);
        }
    }

    [Serializable]
    public sealed class MqttV2BridgeConnectionSettings
    {
        [Header("Broker")]
        [SerializeField] private string _brokerHost = "127.0.0.1";
        [SerializeField] private int _brokerPort = 1883;
        [SerializeField] private string _username = "";
        [SerializeField] private string _password = "";

        [Header("Client")]
        [SerializeField] private MqttV2ClientIdPolicy _clientIdPolicy = MqttV2ClientIdPolicy.RandomPerStart;
        [SerializeField] private string _fixedClientId = "";

        [Header("Connection")]
        [Tooltip("Delay before auto reconnect after disconnect (seconds). Keep this lower than Keep Alive for faster recovery.")]
        [SerializeField] private int _reconnectDelaySeconds = 5;
        [SerializeField] private bool _useCleanSession = true;

        [Header("Keep Alive")]
        [SerializeField] private MqttV2BridgeKeepAliveSettings _keepAliveSettings = new MqttV2BridgeKeepAliveSettings();

        [Header("Will (LWT)")]
        [SerializeField] private MqttV2BridgeWillSettings _willSettings = new MqttV2BridgeWillSettings();

        [Header("Subscriptions")]
        [SerializeField] private MqttV2BridgeSubscriptionSettings _subscriptionSettings = new MqttV2BridgeSubscriptionSettings();

        [FormerlySerializedAs("_keepAliveSeconds")]
        [SerializeField] [HideInInspector] private int _legacyKeepAliveSeconds = MqttV2KeepAliveOptions.DefaultSeconds;
        [SerializeField] [HideInInspector] private bool _legacyKeepAliveMigrated;

        [Header("TLS")]
        [SerializeField] private MqttV2BridgeTlsSettings _tlsSettings = new MqttV2BridgeTlsSettings();

        public string BrokerHost => _brokerHost;
        public int BrokerPort => _brokerPort;
        public MqttV2ClientIdPolicy ClientIdPolicy => _clientIdPolicy;
        public string FixedClientId => _fixedClientId;

        public MqttV2ConnectionOptions ToConnectionOptions()
        {
            var credentials = string.IsNullOrWhiteSpace(_username)
                ? MqttV2Credentials.Anonymous
                : new MqttV2Credentials(_username, _password);

            var tlsOptions = _tlsSettings != null
                ? _tlsSettings.ToCoreOptions()
                : MqttV2TlsOptions.Disabled;

            var keepAliveOptions = _keepAliveSettings != null
                ? _keepAliveSettings.ToCoreOptions()
                : MqttV2KeepAliveOptions.Default;

            var willOptions = _willSettings != null
                ? _willSettings.ToCoreOptions()
                : MqttV2WillOptions.Disabled;

            var subscriptionDefaults = _subscriptionSettings != null
                ? _subscriptionSettings.ToCoreOptions()
                : MqttV2SubscriptionDefaults.Default;

            return new MqttV2ConnectionOptions(
                credentials: credentials,
                keepAlive: keepAliveOptions,
                reconnectDelaySeconds: _reconnectDelaySeconds,
                useCleanSession: _useCleanSession,
                tls: tlsOptions,
                will: willOptions,
                subscriptionDefaults: subscriptionDefaults);
        }

        internal void MigrateLegacyKeepAliveIfNeeded()
        {
            EnsureKeepAliveSettings();

            if (_legacyKeepAliveMigrated)
                return;

            if (_legacyKeepAliveSeconds >= MqttV2KeepAliveOptions.MinSeconds
                && _legacyKeepAliveSeconds <= MqttV2KeepAliveOptions.MaxSeconds)
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
            MqttV2ClientIdPolicy clientIdPolicy,
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
                _keepAliveSettings = new MqttV2BridgeKeepAliveSettings();
        }
    }
}