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
            string clientId)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            if (connectionOptions == null)
                throw new ArgumentNullException(nameof(connectionOptions));

            if (string.IsNullOrWhiteSpace(clientId))
                throw new ArgumentException("clientId is required.", nameof(clientId));

            var mqttFactory = new MqttFactory();
            var clientOptionsBuilder = mqttFactory.CreateClientOptionsBuilder()
                .WithClientId(clientId)
                .WithTcpServer(profile.BrokerHost, profile.BrokerPort)
                .WithCleanSession(connectionOptions.UseCleanSession);

            clientOptionsBuilder = ApplyKeepAliveOptions(clientOptionsBuilder, connectionOptions.KeepAlive);

            if (connectionOptions.Credentials.HasCredentials)
            {
                clientOptionsBuilder = clientOptionsBuilder.WithCredentials(
                    connectionOptions.Credentials.Username,
                    connectionOptions.Credentials.Password ?? string.Empty);
            }

            if (connectionOptions.Tls.Enabled)
                clientOptionsBuilder = ApplyTlsOptions(clientOptionsBuilder, connectionOptions.Tls);

            clientOptionsBuilder = ApplyWillOptions(clientOptionsBuilder, connectionOptions.Will);

            return new ManagedMqttClientOptionsBuilder()
                .WithClientOptions(clientOptionsBuilder.Build())
                .WithAutoReconnectDelay(TimeSpan.FromSeconds(connectionOptions.ReconnectDelaySeconds))
                .Build();
        }

        private static MqttClientOptionsBuilder ApplyTlsOptions(
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