using PullSub.Mqtt;

namespace PullSub.Core.IntegrationTests
{
    public sealed class MqttConnectionOptionsSecurityTests
    {
        [Fact]
        public void WsWithTlsEnabled_ThrowsArgumentException()
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                new MqttConnectionOptions(
                    tls: new MqttTlsOptions(enabled: true),
                    transport: new MqttTransportOptions(MqttTransportKind.Ws)));

            Assert.Contains("Transport=Ws", ex.Message);
        }

        [Fact]
        public void WssWithTlsEnabled_DoesNotThrow()
        {
            var options = new MqttConnectionOptions(
                tls: new MqttTlsOptions(enabled: true),
                transport: new MqttTransportOptions(MqttTransportKind.Wss));

            Assert.True(options.Tls.Enabled);
            Assert.Equal(MqttTransportKind.Wss, options.Transport.Kind);
        }

        [Fact]
        public void WsWithTlsDisabled_DoesNotThrow()
        {
            var options = new MqttConnectionOptions(
                tls: MqttTlsOptions.Disabled,
                transport: new MqttTransportOptions(MqttTransportKind.Ws));

            Assert.False(options.Tls.Enabled);
            Assert.Equal(MqttTransportKind.Ws, options.Transport.Kind);
        }
    }
}
