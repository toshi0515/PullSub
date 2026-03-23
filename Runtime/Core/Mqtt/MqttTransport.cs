using System;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;

namespace PullSub.Core
{
    internal sealed class MqttTransport : ITransport, IAsyncDisposable
    {
        private readonly IManagedMqttClient _client;
        private readonly MqttClientProfile _profile;
        private readonly MqttConnectionOptions _connectionOptions;

        public Func<Task> OnConnected { get; set; }
        public Func<string, Task> OnDisconnected { get; set; }
        public Func<string, byte[], Task> OnMessageReceived { get; set; }

        public bool IsStarted => _client.IsStarted;
        public bool IsConnected => _client.IsConnected;

        public MqttTransport(
            MqttClientProfile profile,
            MqttConnectionOptions connectionOptions)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _connectionOptions = connectionOptions ?? throw new ArgumentNullException(nameof(connectionOptions));

            var factory = new MqttFactory();
            _client = factory.CreateManagedMqttClient();

            // MQTTnet の args 型はここで吸収し、interface のデリゲートに変換する
            _client.ConnectedAsync += _ =>
                OnConnected?.Invoke() ?? Task.CompletedTask;

            _client.DisconnectedAsync += e =>
                OnDisconnected?.Invoke(e.Reason.ToString()) ?? Task.CompletedTask;

            _client.ApplicationMessageReceivedAsync += e =>
            {
                var msg = e.ApplicationMessage;
                if (msg == null)
                    return Task.CompletedTask;

                var payload = ClonePayload(msg.PayloadSegment);
                return OnMessageReceived?.Invoke(msg.Topic, payload) ?? Task.CompletedTask;
            };
        }
        internal static MQTTnet.Protocol.MqttQualityOfServiceLevel ToMqttNet(MqttQualityOfServiceLevel qos)
            => (MQTTnet.Protocol.MqttQualityOfServiceLevel)(int)qos;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var clientId = _profile.ClientIdPolicy == MqttClientIdPolicy.Fixed
                ? _profile.FixedClientId
                : $"PullSub_{Guid.NewGuid():N}";

            var options = MqttOptionsFactory.Build(
                _profile,
                _connectionOptions,
                clientId);

            await PullSubAsyncUtils.AwaitWithCancellation(_client.StartAsync(options), cancellationToken);
        }

        public Task StopAsync(bool cleanDisconnect, CancellationToken cancellationToken)
        {
            return PullSubAsyncUtils.AwaitWithCancellation(
                _client.StopAsync(cleanDisconnect),
                cancellationToken);
        }

        public Task EnqueueAsync(string topic, byte[] payload, MqttQualityOfServiceLevel qos, bool retain)
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(ToMqttNet(qos))
                .WithRetainFlag(retain)
                .Build();

            return _client.EnqueueAsync(message);
        }

        public Task SubscribeAsync(string topic, MqttQualityOfServiceLevel qos, CancellationToken cancellationToken)
        {
            var filter = new MqttTopicFilterBuilder()
                .WithTopic(topic)
                .WithQualityOfServiceLevel(ToMqttNet(qos))
                .Build();

            return PullSubAsyncUtils.AwaitWithCancellation(
                _client.SubscribeAsync(new[] { filter }),
                cancellationToken);
        }

        public Task UnsubscribeAsync(string topic, CancellationToken cancellationToken)
        {
            return PullSubAsyncUtils.AwaitWithCancellation(
                _client.UnsubscribeAsync(new[] { topic }),
                cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (_client is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else
                (_client as IDisposable)?.Dispose();
        }

        private static byte[] ClonePayload(ArraySegment<byte> payload)
        {
            if (payload.Array == null || payload.Count == 0)
                return Array.Empty<byte>();

            var cloned = new byte[payload.Count];
            Buffer.BlockCopy(payload.Array, payload.Offset, cloned, 0, payload.Count);
            return cloned;
        }
    }
}