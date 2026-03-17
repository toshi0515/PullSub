using System;
using System.Security.Authentication;
using System.Text;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;

namespace UnityMqtt.V2.Core
{
    internal static class MqttV2RuntimeClientOptionsFactory
    {
        public static ManagedMqttClientOptions Build(
            MqttV2ClientProfile profile,
            MqttV2ConnectionOptions connectionOptions,
            string clientId,
            Action<string> logWarning = null)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            if (connectionOptions == null)
                throw new ArgumentNullException(nameof(connectionOptions));

            if (string.IsNullOrWhiteSpace(clientId))
                throw new ArgumentException("clientId is required.", nameof(clientId));

            var transport = connectionOptions.Transport;

            WarnIfTransportTlsMismatch(transport, connectionOptions.Tls, logWarning);

            var mqttFactory = new MqttFactory();
            var clientOptionsBuilder = mqttFactory.CreateClientOptionsBuilder()
                .WithClientId(clientId)
                .WithCleanSession(connectionOptions.UseCleanSession);

            clientOptionsBuilder = ApplyTransportOptions(clientOptionsBuilder, profile, connectionOptions);

            clientOptionsBuilder = ApplyKeepAliveOptions(clientOptionsBuilder, connectionOptions.KeepAlive);

            if (connectionOptions.Credentials.HasCredentials)
            {
                clientOptionsBuilder = clientOptionsBuilder.WithCredentials(
                    connectionOptions.Credentials.Username,
                    connectionOptions.Credentials.Password ?? string.Empty);
            }

            clientOptionsBuilder = ApplyTlsOptions(clientOptionsBuilder, transport, connectionOptions.Tls, logWarning);

            clientOptionsBuilder = ApplyWillOptions(clientOptionsBuilder, connectionOptions.Will);

            return new ManagedMqttClientOptionsBuilder()
                .WithClientOptions(clientOptionsBuilder.Build())
                .WithAutoReconnectDelay(TimeSpan.FromSeconds(connectionOptions.ReconnectDelaySeconds))
                .Build();
        }

        private static MqttClientOptionsBuilder ApplyTransportOptions(
            MqttClientOptionsBuilder clientOptionsBuilder,
            MqttV2ClientProfile profile,
            MqttV2ConnectionOptions connectionOptions)
        {
            var transport = connectionOptions.Transport;

            switch (transport.Kind)
            {
                case MqttV2TransportKind.Tcp:
                    return clientOptionsBuilder.WithTcpServer(profile.BrokerHost, profile.BrokerPort);

                case MqttV2TransportKind.Ws:
                    return clientOptionsBuilder.WithWebSocketServer((MqttClientWebSocketOptionsBuilder b) => b
                        .WithUri($"ws://{profile.BrokerHost}:{profile.BrokerPort}{transport.WebSocketPath}")
                        .WithSubProtocols(new[] { transport.WebSocketSubprotocol }));

                case MqttV2TransportKind.Wss:
                    return clientOptionsBuilder.WithWebSocketServer((MqttClientWebSocketOptionsBuilder b) => b
                        .WithUri($"wss://{profile.BrokerHost}:{profile.BrokerPort}{transport.WebSocketPath}")
                        .WithSubProtocols(new[] { transport.WebSocketSubprotocol }));

                default:
                    throw new ArgumentOutOfRangeException(nameof(transport.Kind), transport.Kind, "Unknown transport kind.");
            }
        }

        private static MqttClientOptionsBuilder ApplyTlsOptions(
            MqttClientOptionsBuilder clientOptionsBuilder,
            MqttV2TransportOptions transport,
            MqttV2TlsOptions tls,
            Action<string> logWarning)
        {
            switch (transport.Kind)
            {
                case MqttV2TransportKind.Tcp:
                    if (!tls.Enabled)
                        return clientOptionsBuilder;
                    return ApplyTcpTlsOptions(clientOptionsBuilder, tls);

                case MqttV2TransportKind.Ws:
                    // Ws では TLS は使用しない（WarnIfTransportTlsMismatch で警告済み）
                    return clientOptionsBuilder;

                case MqttV2TransportKind.Wss:
                    // Wss: 証明書系オプション + TargetHost を適用。
                    // SslProtocols は WebSocket 経路では適用できないため無視する
                    // （MQTTnet が WebSocket の TLS ネゴシエーションを管理するため）。
                    return ApplyWssTlsOptions(clientOptionsBuilder, tls, logWarning);

                default:
                    throw new ArgumentOutOfRangeException(nameof(transport.Kind), transport.Kind, "Unknown transport kind.");
            }
        }

        private static MqttClientOptionsBuilder ApplyTcpTlsOptions(
            MqttClientOptionsBuilder clientOptionsBuilder,
            MqttV2TlsOptions tls)
        {
            return clientOptionsBuilder.WithTlsOptions(builder =>
            {
                builder
                    .UseTls(true)
                    .WithAllowUntrustedCertificates(tls.AllowUntrustedCertificates)
                    .WithIgnoreCertificateChainErrors(tls.IgnoreCertificateChainErrors)
                    .WithIgnoreCertificateRevocationErrors(tls.IgnoreCertificateRevocationErrors);

                if (tls.SslProtocols != SslProtocols.None)
                    builder.WithSslProtocols(tls.SslProtocols);

                // When TargetHost is omitted, MQTTnet falls back to broker host automatically.
                if (!string.IsNullOrEmpty(tls.TargetHost))
                    builder.WithTargetHost(tls.TargetHost);
            });
        }

        private static MqttClientOptionsBuilder ApplyWssTlsOptions(
            MqttClientOptionsBuilder clientOptionsBuilder,
            MqttV2TlsOptions tls,
            Action<string> logWarning)
        {
            if (!tls.Enabled)
                return clientOptionsBuilder;

            if (tls.SslProtocols != SslProtocols.None)
            {
                logWarning?.Invoke(
                    "[MQTT-V2] SslProtocols は WebSocket トランスポートでは適用されません（MQTTnet が TLS ネゴシエーションを管理するため）。" +
                    " SslProtocols=None の設定を推奨します。");
            }

            return clientOptionsBuilder.WithTlsOptions(builder =>
            {
                builder
                    .UseTls(true)
                    .WithAllowUntrustedCertificates(tls.AllowUntrustedCertificates)
                    .WithIgnoreCertificateChainErrors(tls.IgnoreCertificateChainErrors)
                    .WithIgnoreCertificateRevocationErrors(tls.IgnoreCertificateRevocationErrors);

                // TargetHost (SNI) は WebSocket でも有効
                if (!string.IsNullOrEmpty(tls.TargetHost))
                    builder.WithTargetHost(tls.TargetHost);

                // SslProtocols は WebSocket TLS 経路では適用しない
            });
        }

        private static void WarnIfTransportTlsMismatch(
            MqttV2TransportOptions transport,
            MqttV2TlsOptions tls,
            Action<string> logWarning)
        {
            if (transport.Kind == MqttV2TransportKind.Ws && tls.Enabled)
            {
                logWarning?.Invoke(
                    "[MQTT-V2] Transport=Ws で TLS が有効になっていますが、Ws は非暗号化接続（ws://）です。" +
                    " TLS 設定は無視されます。暗号化が必要な場合は Transport=Wss を使用してください。");
            }
        }

        private static MqttClientOptionsBuilder ApplyKeepAliveOptions(
            MqttClientOptionsBuilder clientOptionsBuilder,
            MqttV2KeepAliveOptions keepAlive)
        {
            if (keepAlive == null || !keepAlive.Enabled)
                return clientOptionsBuilder.WithNoKeepAlive();

            return clientOptionsBuilder.WithKeepAlivePeriod(TimeSpan.FromSeconds(keepAlive.Seconds));
        }

        private static MqttClientOptionsBuilder ApplyWillOptions(
            MqttClientOptionsBuilder clientOptionsBuilder,
            MqttV2WillOptions will)
        {
            if (will == null || !will.Enabled)
                return clientOptionsBuilder;

            var payload = Encoding.UTF8.GetBytes(will.PayloadUtf8 ?? string.Empty);

            return clientOptionsBuilder
                .WithWillTopic(will.Topic)
                .WithWillPayload(payload)
                .WithWillQualityOfServiceLevel(will.Qos)
                .WithWillRetain(will.Retain);
        }
    }
}
