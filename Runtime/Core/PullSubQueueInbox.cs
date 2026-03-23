using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    public sealed class PullSubQueueMessage
    {
        public PullSubQueueMessage(string topic, byte[] payload, DateTime receivedUtc)
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
        // TryDequeue and DequeueAsync can be mixed on the same topic.
        // Wakeups are not fair between sync and async consumers.
        private readonly object _gate = new object();
        private readonly Dictionary<string, Queue<PullSubQueueMessage>> _queues
            = new Dictionary<string, Queue<PullSubQueueMessage>>(StringComparer.Ordinal);
        private readonly Dictionary<string, PullSubQueueOptions> _optionsByTopic
            = new Dictionary<string, PullSubQueueOptions>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _droppedCounts
            = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, SemaphoreSlim> _signals
            = new Dictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

        public void ConfigureTopic(string topic, PullSubQueueOptions options)
        {
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("topic is required.", nameof(topic));

            if (options == null)
                throw new ArgumentNullException(nameof(options));

            lock (_gate)
            {
                if (_optionsByTopic.ContainsKey(topic))
                    throw new InvalidOperationException($"Topic '{topic}' is already configured.");

                _optionsByTopic[topic] = options;

                if (!_queues.ContainsKey(topic))
                    _queues[topic] = new Queue<PullSubQueueMessage>();

                GetOrCreateSignalNoLock(topic);
            }
        }

        public void RemoveTopic(string topic)
        {
            if (string.IsNullOrWhiteSpace(topic))
                return;

            SemaphoreSlim signal = null;
            lock (_gate)
            {
                _queues.Remove(topic);
                _optionsByTopic.Remove(topic);
                _droppedCounts.Remove(topic);

                if (_signals.TryGetValue(topic, out signal))
                    _signals.Remove(topic);
            }

            signal?.Dispose();
        }

        public void Enqueue(string topic, byte[] payload)
        {
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("topic is required.", nameof(topic));

            if (payload == null)
                throw new ArgumentNullException(nameof(payload));

            SemaphoreSlim signal;
            lock (_gate)
            {
                if (!_optionsByTopic.TryGetValue(topic, out var options))
                    return;

                if (!_queues.TryGetValue(topic, out var queue))
                {
                    queue = new Queue<PullSubQueueMessage>();
                    _queues[topic] = queue;
                }

                if (queue.Count >= options.MaxQueueDepth)
                {
                    queue.Dequeue();
                    IncrementDroppedCountNoLock(topic);
                }

                queue.Enqueue(new PullSubQueueMessage(topic, payload, DateTime.UtcNow));
                signal = GetOrCreateSignalNoLock(topic);
            }

            try
            {
                signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Signal is already pending.
            }
        }

        public bool TryDequeue(string topic, out PullSubQueueMessage message)
        {
            message = null;
            if (string.IsNullOrWhiteSpace(topic))
                return false;

            lock (_gate)
            {
                if (!_queues.TryGetValue(topic, out var queue) || queue.Count == 0)
                    return false;

                message = queue.Dequeue();
                return true;
            }
        }

        public bool TryGetDroppedCount(string topic, out long droppedCount)
        {
            droppedCount = 0;
            if (string.IsNullOrWhiteSpace(topic))
                return false;

            lock (_gate)
            {
                if (!_optionsByTopic.ContainsKey(topic))
                    return false;

                _droppedCounts.TryGetValue(topic, out droppedCount);
                return true;
            }
        }

        public async Task<PullSubQueueMessage> DequeueAsync(string topic, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("topic is required.", nameof(topic));

            while (true)
            {
                SemaphoreSlim signal;
                lock (_gate)
                {
                    if (_queues.TryGetValue(topic, out var queue) && queue.Count > 0)
                        return queue.Dequeue();

                    signal = GetOrCreateSignalNoLock(topic);
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

        public void Clear()
        {
            List<SemaphoreSlim> signals;
            lock (_gate)
            {
                _queues.Clear();
                _optionsByTopic.Clear();
                _droppedCounts.Clear();
                signals = new List<SemaphoreSlim>(_signals.Values);
                _signals.Clear();
            }

            foreach (var signal in signals)
                signal.Dispose();
        }

        private void IncrementDroppedCountNoLock(string topic)
        {
            if (_droppedCounts.TryGetValue(topic, out var current))
            {
                _droppedCounts[topic] = current + 1;
                return;
            }

            _droppedCounts[topic] = 1;
        }

        private SemaphoreSlim GetOrCreateSignalNoLock(string topic)
        {
            if (!_signals.TryGetValue(topic, out var signal))
            {
                signal = new SemaphoreSlim(0, 1);
                _signals[topic] = signal;
            }

            return signal;
        }
    }
}
