using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PullSub.Core;

namespace PullSub.Core.Tests.TestScenarios.Fixtures
{
    internal sealed class TestTransport : ITransport
    {
        private readonly ConcurrentQueue<(string Topic, byte[] Payload, PullSubQualityOfServiceLevel Qos, bool Retain)> _published
            = new ConcurrentQueue<(string Topic, byte[] Payload, PullSubQualityOfServiceLevel Qos, bool Retain)>();

        private readonly Dictionary<string, PullSubQualityOfServiceLevel> _subscriptions
            = new Dictionary<string, PullSubQualityOfServiceLevel>(StringComparer.Ordinal);

        public Func<Task>? OnConnected { get; set; }
        public Func<string, Task>? OnDisconnected { get; set; }
        public Func<string, ReadOnlyMemory<byte>, Task>? OnMessageReceived { get; set; }

        public bool IsStarted { get; private set; }
        public bool IsConnected { get; private set; }

        public IReadOnlyCollection<(string Topic, byte[] Payload, PullSubQualityOfServiceLevel Qos, bool Retain)> Published
            => _published.ToArray();

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsStarted = true;
            IsConnected = true;
            return OnConnected?.Invoke() ?? Task.CompletedTask;
        }

        public Task StopAsync(bool cleanDisconnect, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsConnected = false;
            IsStarted = false;
            return OnDisconnected?.Invoke(cleanDisconnect ? "Clean" : "Force") ?? Task.CompletedTask;
        }

        public Task EnqueueAsync(string topic, byte[] payload, PullSubQualityOfServiceLevel qos, bool retain)
        {
            _published.Enqueue((topic, payload, qos, retain));
            return Task.CompletedTask;
        }

        public Task SubscribeAsync(string topic, PullSubQualityOfServiceLevel qos, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _subscriptions[topic] = qos;
            return Task.CompletedTask;
        }

        public Task UnsubscribeAsync(string topic, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _subscriptions.Remove(topic);
            return Task.CompletedTask;
        }

        public Task EmitMessageAsync(string topic, byte[] payload)
        {
            return OnMessageReceived?.Invoke(topic, payload ?? Array.Empty<byte>()) ?? Task.CompletedTask;
        }
    }
}
