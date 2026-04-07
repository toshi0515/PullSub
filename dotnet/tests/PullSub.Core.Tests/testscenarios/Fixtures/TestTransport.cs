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

        private readonly ConcurrentDictionary<string, PullSubQualityOfServiceLevel> _subscriptions
            = new ConcurrentDictionary<string, PullSubQualityOfServiceLevel>(StringComparer.Ordinal);

        private int _subscribeCallCount;
        private int _unsubscribeCallCount;
        private int _callbacksSet;

        private Func<Task>? _onConnected;
        private Func<string, Task>? _onDisconnected;
        private Func<string, ReadOnlyMemory<byte>, Task>? _onMessageReceived;

        public Func<string, byte[], PullSubQualityOfServiceLevel, bool, CancellationToken, Task>? OnPublish { get; set; }

        public bool IsConnected { get; private set; }
        public ReconnectOptions ReconnectOptions { get; set; } = ReconnectOptions.Default;

        public IReadOnlyCollection<(string Topic, byte[] Payload, PullSubQualityOfServiceLevel Qos, bool Retain)> Published
            => _published.ToArray();

        public int SubscribeCallCount => Volatile.Read(ref _subscribeCallCount);
        public int UnsubscribeCallCount => Volatile.Read(ref _unsubscribeCallCount);

        internal void SetConnectedForTest(bool isConnected)
        {
            IsConnected = isConnected;
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
                throw new InvalidOperationException("Callbacks are already set.");

            _onConnected = onConnected;
            _onDisconnected = onDisconnected;
            _onMessageReceived = onMessageReceived;
        }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsConnected = true;
            return _onConnected?.Invoke() ?? Task.CompletedTask;
        }

        public Task DisconnectAsync(bool cleanDisconnect, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsConnected = false;
            return _onDisconnected?.Invoke(cleanDisconnect ? "Clean" : "Force") ?? Task.CompletedTask;
        }

        public Task PublishAsync(
            string topic,
            byte[] payload,
            PullSubQualityOfServiceLevel qos,
            bool retain,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (OnPublish != null)
                return OnPublish(topic, payload, qos, retain, cancellationToken);

            _published.Enqueue((topic, payload, qos, retain));
            return Task.CompletedTask;
        }

        public void RecordPublished(string topic, byte[] payload, PullSubQualityOfServiceLevel qos, bool retain)
        {
            _published.Enqueue((topic, payload, qos, retain));
        }

        public Task SubscribeAsync(string topic, PullSubQualityOfServiceLevel qos, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _subscribeCallCount);
            _subscriptions[topic] = qos;
            return Task.CompletedTask;
        }

        public Task UnsubscribeAsync(string topic, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _unsubscribeCallCount);
            _subscriptions.TryRemove(topic, out _);
            return Task.CompletedTask;
        }

        public Task EmitMessageAsync(string topic, byte[] payload)
        {
            return _onMessageReceived?.Invoke(topic, payload ?? Array.Empty<byte>()) ?? Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return default;
        }
    }
}
