using System;
using System.Security.Authentication;
using System.Text;
using MQTTnet;
using MQTTnet.Client;

namespace PullSub.Mqtt
{
    internal static class MqttOptionsFactory
    {
        public static MqttClientOptions Build(
            MqttClientProfile profile,
            MqttConnectionOptions connectionOptions,
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

            return clientOptionsBuilder.Build();
        }

        private static MqttClientOptionsBuilder ApplyTransportOptions(
            MqttClientOptionsBuilder clientOptionsBuilder,
            MqttClientProfile profile,
            MqttConnectionOptions connectionOptions)
        {
            var transport = connectionOptions.Transport;

            switch (transport.Kind)
            {
                case MqttTransportKind.Tcp:
                    return clientOptionsBuilder.WithTcpServer(profile.BrokerHost, profile.BrokerPort);

                case MqttTransportKind.Ws:
                    return clientOptionsBuilder.WithWebSocketServer((MqttClientWebSocketOptionsBuilder b) => b
                        .WithUri($"ws://{profile.BrokerHost}:{profile.BrokerPort}{transport.WebSocketPath}")
                        .WithSubProtocols(new[] { transport.WebSocketSubprotocol }));

                case MqttTransportKind.Wss:
                    return clientOptionsBuilder.WithWebSocketServer((MqttClientWebSocketOptionsBuilder b) => b
                        .WithUri($"wss://{profile.BrokerHost}:{profile.BrokerPort}{transport.WebSocketPath}")
                        .WithSubProtocols(new[] { transport.WebSocketSubprotocol }));

                default:
                    throw new ArgumentOutOfRangeException(nameof(transport.Kind), transport.Kind, "Unknown transport kind.");
            }
        }

        private static MqttClientOptionsBuilder ApplyTlsOptions(
            MqttClientOptionsBuilder clientOptionsBuilder,
            MqttTransportOptions transport,
            MqttTlsOptions tls,
            Action<string> logWarning)
        {
            switch (transport.Kind)
            {
                case MqttTransportKind.Tcp:
                    if (!tls.Enabled)
                        return clientOptionsBuilder;
                    return ApplyTcpTlsOptions(clientOptionsBuilder, tls);

                case MqttTransportKind.Ws:
                    // Ws + TLS の組み合わせは MqttConnectionOptions の生成時に fail-fast される。
                    return clientOptionsBuilder;

                case MqttTransportKind.Wss:
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
            MqttTlsOptions tls)
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
            MqttTlsOptions tls,
            Action<string> logWarning)
        {
            if (!tls.Enabled)
                return clientOptionsBuilder;

            if (tls.SslProtocols != SslProtocols.None)
            {
                logWarning?.Invoke(
                    "[MqttOptionsFactory] SslProtocols は WebSocket トランスポートでは適用されません（MQTTnet が TLS ネゴシエーションを管理するため）。" +
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

        private static MqttClientOptionsBuilder ApplyKeepAliveOptions(
            MqttClientOptionsBuilder clientOptionsBuilder,
            MqttKeepAliveOptions keepAlive)
        {
            if (keepAlive == null || !keepAlive.Enabled)
                return clientOptionsBuilder.WithNoKeepAlive();

            return clientOptionsBuilder.WithKeepAlivePeriod(TimeSpan.FromSeconds(keepAlive.Seconds));
        }

        private static MqttClientOptionsBuilder ApplyWillOptions(
            MqttClientOptionsBuilder clientOptionsBuilder,
            MqttWillOptions will)
        {
            if (will == null || !will.Enabled)
                return clientOptionsBuilder;

            var payload = Encoding.UTF8.GetBytes(will.PayloadUtf8 ?? string.Empty);

            return clientOptionsBuilder
                .WithWillTopic(will.Topic)
                .WithWillPayload(payload)
                .WithWillQualityOfServiceLevel(MqttTransport.ToMqttNet(will.Qos))
                .WithWillRetain(will.Retain);
        }
    }
}
