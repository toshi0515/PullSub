using System;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    public interface ITransport
    {
        bool IsStarted { get; }
        bool IsConnected { get; }

        // イベントは MQTTnet 型に依存しないデリゲートで定義
        Func<Task> OnConnected { get; set; }
        Func<string, Task> OnDisconnected { get; set; } // reason string
        // Borrowed payload memory is valid only during callback execution.
        Func<string, ReadOnlyMemory<byte>, Task> OnMessageReceived { get; set; } // topic, payload

        Task StartAsync(CancellationToken cancellationToken);
        Task StopAsync(bool cleanDisconnect, CancellationToken cancellationToken);
        Task EnqueueAsync(string topic, byte[] payload, PullSubQualityOfServiceLevel qos, bool retain);
        Task SubscribeAsync(string topic, PullSubQualityOfServiceLevel qos, CancellationToken cancellationToken);
        Task UnsubscribeAsync(string topic, CancellationToken cancellationToken);
    }
}