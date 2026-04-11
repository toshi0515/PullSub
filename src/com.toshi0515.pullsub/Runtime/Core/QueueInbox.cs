using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    public sealed class QueueMessage
    {
        public QueueMessage(string topic, byte[] payload, DateTime receivedUtc)
        {
            Topic = topic;
            Payload = payload;
            ReceivedUtc = receivedUtc;
        }

        public string Topic { get; }
        public byte[] Payload { get; }
        public DateTime ReceivedUtc { get; }
    }

    internal sealed class PullSubQueueInbox
    {
        private sealed class SubscriberState
        {
            // All fields are accessed only while holding PullSubQueueInbox._gate.
            public readonly Queue<QueueMessage> Queue = new Queue<QueueMessage>();
            public readonly QueueOptions Options;
            public long DroppedCount;
            public readonly SemaphoreSlim Signal = new SemaphoreSlim(0, 1);

            public SubscriberState(QueueOptions options)
            {
                Options = options ?? throw new ArgumentNullException(nameof(options));
            }
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, Dictionary<Guid, SubscriberState>> _topics
            = new Dictionary<string, Dictionary<Guid, SubscriberState>>(StringComparer.Ordinal);

        public Guid RegisterSubscriber(string topic, QueueOptions options)
        {
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("topic is required.", nameof(topic));

            if (options == null)
                throw new ArgumentNullException(nameof(options));

            var subscriberId = Guid.NewGuid();
            var state = new SubscriberState(options);

            lock (_gate)
            {
                if (!_topics.TryGetValue(topic, out var subscribers))
                {
                    subscribers = new Dictionary<Guid, SubscriberState>();
                    _topics[topic] = subscribers;
                }
                subscribers[subscriberId] = state;
            }

            return subscriberId;
        }

        public void UnregisterSubscriber(string topic, Guid subscriberId)
        {
            if (string.IsNullOrWhiteSpace(topic))
                return;

            SemaphoreSlim signal = null;
            lock (_gate)
            {
                if (!_topics.TryGetValue(topic, out var subscribers))
                    return;

                if (!subscribers.TryGetValue(subscriberId, out var state))
                    return;

                signal = state.Signal;
                subscribers.Remove(subscriberId);

                if (subscribers.Count == 0)
                    _topics.Remove(topic);
            }

            signal?.Dispose();
        }

        public void Enqueue(string topic, byte[] payload)
        {
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("topic is required.", nameof(topic));

            if (payload == null)
                throw new ArgumentNullException(nameof(payload));

            var message = new QueueMessage(topic, payload, DateTime.UtcNow);
            List<SemaphoreSlim> signalsToRelease = null;

            lock (_gate)
            {
                if (!_topics.TryGetValue(topic, out var subscribers))
                    return;

                foreach (var pair in subscribers)
                {
                    var state = pair.Value;

                    if (state.Queue.Count >= state.Options.MaxQueueDepth)
                    {
                        state.Queue.Dequeue();
                        state.DroppedCount++;
                    }

                    state.Queue.Enqueue(message);

                    if (state.Signal.CurrentCount == 0)
                    {
                        if (signalsToRelease == null)
                            signalsToRelease = new List<SemaphoreSlim>();
                        signalsToRelease.Add(state.Signal);
                    }
                }
            }

            if (signalsToRelease == null)
                return;

            foreach (var signal in signalsToRelease)
            {
                try
                {
                    signal.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        public bool TryDequeue(string topic, Guid subscriberId, out QueueMessage message)
        {
            message = null;
            if (string.IsNullOrWhiteSpace(topic))
                return false;

            lock (_gate)
            {
                if (!_topics.TryGetValue(topic, out var subscribers))
                    return false;

                if (!subscribers.TryGetValue(subscriberId, out var state) || state.Queue.Count == 0)
                    return false;

                message = state.Queue.Dequeue();
                return true;
            }
        }

        public async Task<QueueMessage> DequeueAsync(string topic, Guid subscriberId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("topic is required.", nameof(topic));

            while (true)
            {
                SemaphoreSlim signal;
                lock (_gate)
                {
                    if (_topics.TryGetValue(topic, out var subscribers) &&
                        subscribers.TryGetValue(subscriberId, out var state))
                    {
                        if (state.Queue.Count > 0)
                            return state.Queue.Dequeue();

                        signal = state.Signal;
                    }
                    else
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new OperationCanceledException("Subscriber was unregistered while waiting for a message.", cancellationToken);
                    }
                }

                try
                {
                    await signal.WaitAsync(cancellationToken);
                }
                catch (ObjectDisposedException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new OperationCanceledException("Queue inbox was cleared while waiting for a message.", cancellationToken);
                }
            }
        }

        public bool TryGetDroppedCount(string topic, Guid subscriberId, out long droppedCount)
        {
            droppedCount = 0;
            if (string.IsNullOrWhiteSpace(topic))
                return false;

            lock (_gate)
            {
                if (!_topics.TryGetValue(topic, out var subscribers))
                    return false;

                if (!subscribers.TryGetValue(subscriberId, out var state))
                    return false;

                droppedCount = state.DroppedCount;
                return true;
            }
        }

        public bool TryGetTotalDroppedCount(string topic, out long totalDroppedCount)
        {
            totalDroppedCount = 0;
            if (string.IsNullOrWhiteSpace(topic))
                return false;

            lock (_gate)
            {
                if (!_topics.TryGetValue(topic, out var subscribers))
                    return false;

                foreach (var state in subscribers.Values)
                {
                    totalDroppedCount += state.DroppedCount;
                }
                return true;
            }
        }

        public void Clear()
        {
            List<SemaphoreSlim> signals = new List<SemaphoreSlim>();
            lock (_gate)
            {
                foreach (var subscribers in _topics.Values)
                {
                    foreach (var state in subscribers.Values)
                    {
                        signals.Add(state.Signal);
                    }
                }
                _topics.Clear();
            }

            foreach (var signal in signals)
                signal.Dispose();
        }
    }
}
