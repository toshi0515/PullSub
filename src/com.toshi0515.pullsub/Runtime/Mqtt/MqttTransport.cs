using System;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;
using PullSub.Core;

namespace PullSub.Mqtt
{
    public sealed class MqttTransport : ITransport
    {
        private readonly IMqttClient _client;
        private readonly MqttClientProfile _profile;
        private readonly MqttConnectionOptions _connectionOptions;
        private int _callbacksSet;

        private Func<Task> _onConnected = () => Task.CompletedTask;
        private Func<string, Task> _onDisconnected = _ => Task.CompletedTask;
        private Func<string, ReadOnlyMemory<byte>, Task> _onMessageReceived = (_, __) => Task.CompletedTask;

        public bool IsConnected => _client.IsConnected;
        public PullSubReconnectOptions ReconnectOptions => _connectionOptions.ReconnectOptions;

        public MqttTransport(
            string brokerHost,
            int brokerPort,
            MqttConnectionOptions connectionOptions,
            MqttClientIdPolicy clientIdPolicy = MqttClientIdPolicy.RandomPerStart,
            string fixedClientId = null)
            : this(
                new MqttClientProfile(
                    brokerHost,
                    brokerPort,
                    clientIdPolicy,
                    fixedClientId,
                    connectionOptions),
                connectionOptions)
        {
        }

        internal MqttTransport(
            MqttClientProfile profile,
            MqttConnectionOptions connectionOptions)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _connectionOptions = connectionOptions ?? throw new ArgumentNullException(nameof(connectionOptions));

            var factory = new MqttFactory();
            _client = factory.CreateMqttClient();

            // MQTTnet の args 型はここで吸収し、interface のデリゲートに変換する
            _client.ConnectedAsync += _ =>
                _onConnected();

            _client.DisconnectedAsync += e =>
                _onDisconnected(e.Reason.ToString());

            _client.ApplicationMessageReceivedAsync += e =>
            {
                var msg = e.ApplicationMessage;
                if (msg == null)
                    return Task.CompletedTask;

                var payload = GetPayloadMemory(msg.PayloadSegment);
                return _onMessageReceived(msg.Topic, payload);
            };
        }

        public void SetCallbacks(
            Func<Task> onConnected,
            Func<string, Task> onDisconnected,
            Func<string, ReadOnlyMemory<byte>, Task> onMessageReceived)
        {
            if (onConnected == null)
                throw new ArgumentNullException(nameof(onConnected));

            if (onDisconnected == null)
                throw new ArgumentNullException(nameof(onDisconnected));

            if (onMessageReceived == null)
                throw new ArgumentNullException(nameof(onMessageReceived));

            if (Interlocked.Exchange(ref _callbacksSet, 1) == 1)
                throw new InvalidOperationException("Transport callbacks are already set.");

            _onConnected = onConnected;
            _onDisconnected = onDisconnected;
            _onMessageReceived = onMessageReceived;
        }

        internal static MQTTnet.Protocol.MqttQualityOfServiceLevel ToMqttNet(PullSubQualityOfServiceLevel qos)
            => (MQTTnet.Protocol.MqttQualityOfServiceLevel)(int)qos;

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            var clientId = _profile.ClientIdPolicy == MqttClientIdPolicy.Fixed
                ? _profile.FixedClientId
                : $"PullSub_{Guid.NewGuid():N}";

            var options = MqttOptionsFactory.Build(
                _profile,
                _connectionOptions,
                clientId);

            await _client.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
        }

        public async Task DisconnectAsync(bool cleanDisconnect, CancellationToken cancellationToken)
        {
            if (!_client.IsConnected)
                return;

            await _client.DisconnectAsync(new MqttClientDisconnectOptions(), cancellationToken).ConfigureAwait(false);
        }

        public Task PublishAsync(
            string topic,
            byte[] payload,
            PullSubQualityOfServiceLevel qos,
            bool retain,
            CancellationToken cancellationToken)
        {
            if (!_client.IsConnected)
                throw new InvalidOperationException("Transport is not connected.");

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(ToMqttNet(qos))
                .WithRetainFlag(retain)
                .Build();

            return _client.PublishAsync(message, cancellationToken);
        }

        public Task SubscribeAsync(string topic, PullSubQualityOfServiceLevel qos, CancellationToken cancellationToken)
        {
            if (!_client.IsConnected)
                throw new InvalidOperationException("Transport is not connected.");

            return _client.SubscribeAsync(topic, ToMqttNet(qos), cancellationToken);
        }

        public Task UnsubscribeAsync(string topic, CancellationToken cancellationToken)
        {
            if (!_client.IsConnected)
                throw new InvalidOperationException("Transport is not connected.");

            return _client.UnsubscribeAsync(topic, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            (_client as IDisposable)?.Dispose();
            return default;
        }

        private static ReadOnlyMemory<byte> GetPayloadMemory(ArraySegment<byte> payload)
        {
            if (payload.Array == null || payload.Count == 0)
                return ReadOnlyMemory<byte>.Empty;

            return new ReadOnlyMemory<byte>(payload.Array, payload.Offset, payload.Count);
        }
    }
}