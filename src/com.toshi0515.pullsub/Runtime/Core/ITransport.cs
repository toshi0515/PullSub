using System;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    public interface ITransport : IAsyncDisposable
    {
        bool IsConnected { get; }
        PullSubReconnectOptions ReconnectOptions { get; }

        void SetCallbacks(
            Func<Task> onConnected,
            Func<string, Task> onDisconnected,
            Func<string, ReadOnlyMemory<byte>, Task> onMessageReceived);

        Task ConnectAsync(CancellationToken cancellationToken);
        Task DisconnectAsync(bool cleanDisconnect, CancellationToken cancellationToken);
        Task PublishAsync(string topic, byte[] payload, PullSubQualityOfServiceLevel qos, bool retain, CancellationToken cancellationToken);
        Task SubscribeAsync(string topic, PullSubQualityOfServiceLevel qos, CancellationToken cancellationToken);
        Task UnsubscribeAsync(string topic, CancellationToken cancellationToken);
    }
}