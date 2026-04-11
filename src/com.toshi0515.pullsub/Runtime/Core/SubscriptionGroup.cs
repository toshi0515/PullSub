using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    public sealed class SubscriptionGroup : IDisposable, IAsyncDisposable
    {
        internal readonly struct ContextSubscriptionDebugSnapshot
        {
            public ContextSubscriptionDebugSnapshot(string topic, bool isQueue)
            {
                Topic = topic;
                IsQueue = isQueue;
            }

            public string Topic { get; }
            public bool IsQueue { get; }
        }

        internal readonly struct GroupDebugSnapshot
        {
            public GroupDebugSnapshot(string debugLabel, ContextSubscriptionDebugSnapshot[] subscriptions)
            {
                DebugLabel = debugLabel;
                Subscriptions = subscriptions;
            }

            public string DebugLabel { get; }
            public ContextSubscriptionDebugSnapshot[] Subscriptions { get; }
        }

        private enum SubscriptionKind
        {
            Data = 0,
            Queue = 1,
        }

        private readonly struct SubscriptionKey : IEquatable<SubscriptionKey>
        {
            private static readonly StringComparer TopicComparer = StringComparer.Ordinal;

            public SubscriptionKey(string topic, SubscriptionKind kind)
            {
                Topic = topic;
                Kind = kind;
            }

            public string Topic { get; }
            public SubscriptionKind Kind { get; }

            public bool Equals(SubscriptionKey other)
            {
                return Kind == other.Kind && TopicComparer.Equals(Topic, other.Topic);
            }

            public override bool Equals(object obj)
            {
                return obj is SubscriptionKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = 17;
                    hash = (hash * 31) + TopicComparer.GetHashCode(Topic);
                    hash = (hash * 31) + (int)Kind;
                    return hash;
                }
            }
        }

        private sealed class SubscriptionGroupLease : IDisposable
        {
            private readonly Func<CancellationToken, Task<PullSubUnsubscribeResult>> _unsubscribeAsync;
            private readonly Action _dispose;

            public SubscriptionGroupLease(
                string topic,
                Func<CancellationToken, Task<PullSubUnsubscribeResult>> unsubscribeAsync,
                Action dispose)
            {
                Topic = topic ?? throw new ArgumentNullException(nameof(topic));
                _unsubscribeAsync = unsubscribeAsync ?? throw new ArgumentNullException(nameof(unsubscribeAsync));
                _dispose = dispose ?? throw new ArgumentNullException(nameof(dispose));
            }

            public string Topic { get; }

            public Task<PullSubUnsubscribeResult> UnsubscribeAsync(CancellationToken cancellationToken = default)
            {
                return _unsubscribeAsync(cancellationToken);
            }

            public void Dispose()
            {
                _dispose();
            }
        }

        private readonly PullSubRuntime _runtime;
        private readonly object _gate = new object();
        private readonly HashSet<SubscriptionKey> _inFlightDataTopics = new HashSet<SubscriptionKey>();
        private readonly Dictionary<SubscriptionKey, SubscriptionGroupLease> _activeDataSubscriptions
            = new Dictionary<SubscriptionKey, SubscriptionGroupLease>();
        private readonly Dictionary<string, List<SubscriptionGroupLease>> _activeQueueSubscriptions
            = new Dictionary<string, List<SubscriptionGroupLease>>(StringComparer.Ordinal);
        private string _debugLabel;

        private int _disposed;

        internal SubscriptionGroup(PullSubRuntime runtime)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        }

        internal PullSubRuntime Runtime => _runtime;

        internal void SetDebugLabel(string label)
        {
            lock (_gate)
            {
                _debugLabel = string.IsNullOrWhiteSpace(label) ? null : label;
            }
        }

        internal GroupDebugSnapshot GetDebugSnapshot()
        {
            lock (_gate)
            {
                var queueCount = 0;
                foreach (var list in _activeQueueSubscriptions.Values)
                    queueCount += list.Count;

                var snapshots = new ContextSubscriptionDebugSnapshot[_activeDataSubscriptions.Count + queueCount];
                var index = 0;

                foreach (var pair in _activeDataSubscriptions)
                {
                    snapshots[index++] = new ContextSubscriptionDebugSnapshot(pair.Key.Topic, isQueue: false);
                }

                foreach (var pair in _activeQueueSubscriptions)
                {
                    foreach (var _ in pair.Value)
                    {
                        snapshots[index++] = new ContextSubscriptionDebugSnapshot(pair.Key, isQueue: true);
                    }
                }

                return new GroupDebugSnapshot(_debugLabel, snapshots);
            }
        }

        public async Task<DataSubscription<T>> SubscribeDataAsync<T>(
            ITopic<T> topic,
            PullSubQualityOfServiceLevel subscribeQos = PullSubQualityOfServiceLevel.AtLeastOnce,
            CancellationToken cancellationToken = default)
        {
            if (topic == null)
                throw new ArgumentNullException(nameof(topic));

            var topicName = topic.TopicName;
            SubscriptionRegistry.ValidateExactMatchTopic(topicName);
            var key = new SubscriptionKey(topicName, SubscriptionKind.Data);

            lock (_gate)
            {
                ThrowIfDisposed_NoLock();

                if (_inFlightDataTopics.Contains(key) || _activeDataSubscriptions.ContainsKey(key))
                {
                    throw new InvalidOperationException(
                        $"Topic '{topicName}' is already subscribed in this context.");
                }

                _inFlightDataTopics.Add(key);
            }

            DataSubscription<T> handle;
            try
            {
                // Context-side duplicate checks must happen before Runtime subscribe.
                await _runtime.SubscribeDataTopicAsync(topic, subscribeQos, cancellationToken).ConfigureAwait(false);
                handle = _runtime.GetDataHandle(topic);
            }
            catch
            {
                lock (_gate)
                {
                    _inFlightDataTopics.Remove(key);
                }

                throw;
            }

            var shouldCleanupImmediately = false;
            lock (_gate)
            {
                _inFlightDataTopics.Remove(key);

                if (IsDisposed_NoLock())
                {
                    shouldCleanupImmediately = true;
                }
                else
                {
                    _activeDataSubscriptions[key] = CreateLease(handle);
                }
            }

            if (shouldCleanupImmediately)
            {
                await handle.UnsubscribeAsync(CancellationToken.None).ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(SubscriptionGroup));
            }

            return handle;
        }

        public Task<QueueSubscription> SubscribeQueueAsync<T>(
            ITopic<T> topic,
            Func<T, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            return SubscribeQueueAsync(topic, QueueOptions.Default, handler, cancellationToken);
        }

        public Task<QueueSubscription> SubscribeQueueAsync<T>(
            ITopic<T> topic,
            Func<T, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return SubscribeQueueAsync(topic, QueueOptions.Default, (message, _) => handler(message), cancellationToken);
        }

        public Task<QueueSubscription> SubscribeQueueAsync<T>(
            ITopic<T> topic,
            Action<T, CancellationToken> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return SubscribeQueueAsync(
                topic,
                QueueOptions.Default,
                (message, ct) =>
                {
                    handler(message, ct);
                    return default;
                },
                cancellationToken);
        }

        public Task<QueueSubscription> SubscribeQueueAsync<T>(
            ITopic<T> topic,
            Action<T> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return SubscribeQueueAsync(
                topic,
                QueueOptions.Default,
                (message, _) =>
                {
                    handler(message);
                    return default;
                },
                cancellationToken);
        }

        public Task<QueueSubscription> SubscribeQueueAsync<T>(
            ITopic<T> topic,
            QueueOptions options,
            Func<T, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return SubscribeQueueAsync(topic, options, (message, _) => handler(message), cancellationToken);
        }

        public Task<QueueSubscription> SubscribeQueueAsync<T>(
            ITopic<T> topic,
            QueueOptions options,
            Action<T, CancellationToken> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return SubscribeQueueAsync(
                topic,
                options,
                (message, ct) =>
                {
                    handler(message, ct);
                    return default;
                },
                cancellationToken);
        }

        public Task<QueueSubscription> SubscribeQueueAsync<T>(
            ITopic<T> topic,
            QueueOptions options,
            Action<T> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return SubscribeQueueAsync(
                topic,
                options,
                (message, _) =>
                {
                    handler(message);
                    return default;
                },
                cancellationToken);
        }

        public async Task<QueueSubscription> SubscribeQueueAsync<T>(
            ITopic<T> topic,
            QueueOptions options,
            Func<T, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            if (topic == null)
                throw new ArgumentNullException(nameof(topic));

            if (options == null)
                throw new ArgumentNullException(nameof(options));

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            var topicName = topic.TopicName;
            SubscriptionRegistry.ValidateExactMatchTopic(topicName);

            lock (_gate)
            {
                ThrowIfDisposed_NoLock();
            }

            var registration = await _runtime.SubscribeQueueAsync(topic, options, handler, cancellationToken)
                .ConfigureAwait(false);

            var shouldCleanupImmediately = false;
            lock (_gate)
            {
                if (IsDisposed_NoLock())
                {
                    shouldCleanupImmediately = true;
                }
                else
                {
                    if (!_activeQueueSubscriptions.TryGetValue(topicName, out var list))
                    {
                        list = new List<SubscriptionGroupLease>();
                        _activeQueueSubscriptions[topicName] = list;
                    }
                    list.Add(CreateLease(registration));
                }
            }

            if (shouldCleanupImmediately)
            {
                await registration.UnsubscribeAsync(CancellationToken.None).ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(SubscriptionGroup));
            }

            return registration;
        }

        public async Task<PullSubUnsubscribeResult> UnsubscribeDataAsync(
            string topic,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("topic is required.", nameof(topic));

            SubscriptionRegistry.ValidateExactMatchTopic(topic);

            SubscriptionGroupLease lease;
            var key = new SubscriptionKey(topic, SubscriptionKind.Data);

            lock (_gate)
            {
                if (!_activeDataSubscriptions.TryGetValue(key, out lease))
                    return PullSubUnsubscribeResult.AlreadyCanceled;

                _activeDataSubscriptions.Remove(key);
            }

            return await lease.UnsubscribeAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<PullSubUnsubscribeResult> UnsubscribeQueueAsync(
            string topic,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("topic is required.", nameof(topic));

            SubscriptionRegistry.ValidateExactMatchTopic(topic);

            List<SubscriptionGroupLease> leases;
            lock (_gate)
            {
                if (!_activeQueueSubscriptions.TryGetValue(topic, out leases) || leases.Count == 0)
                    return PullSubUnsubscribeResult.AlreadyCanceled;

                _activeQueueSubscriptions.Remove(topic);
            }

            var tasks = new Task<PullSubUnsubscribeResult>[leases.Count];
            for (var i = 0; i < leases.Count; i++)
                tasks[i] = leases[i].UnsubscribeAsync(cancellationToken);

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);

            foreach (var r in results)
                if (r == PullSubUnsubscribeResult.Success)
                    return PullSubUnsubscribeResult.Success;

            return PullSubUnsubscribeResult.AlreadyCanceled;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            SubscriptionGroupDebugTracker.Unregister(_runtime, this);

            SubscriptionGroupLease[] dataLeases;
            SubscriptionGroupLease[] queueLeases;

            lock (_gate)
            {
                dataLeases = _activeDataSubscriptions.Values.ToArray();
                _activeDataSubscriptions.Clear();
                _inFlightDataTopics.Clear();

                var allQueueLeases = new List<SubscriptionGroupLease>();
                foreach (var list in _activeQueueSubscriptions.Values)
                    allQueueLeases.AddRange(list);

                queueLeases = allQueueLeases.ToArray();
                _activeQueueSubscriptions.Clear();
            }

            var tasks = new List<Task>(dataLeases.Length + queueLeases.Length);
            foreach (var lease in dataLeases)
                tasks.Add(lease.UnsubscribeAsync(CancellationToken.None));
            foreach (var lease in queueLeases)
                tasks.Add(lease.UnsubscribeAsync(CancellationToken.None));

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            SubscriptionGroupDebugTracker.Unregister(_runtime, this);

            SubscriptionGroupLease[] dataLeases;
            SubscriptionGroupLease[] queueLeases;

            lock (_gate)
            {
                dataLeases = _activeDataSubscriptions.Values.ToArray();
                _activeDataSubscriptions.Clear();
                _inFlightDataTopics.Clear();

                var allQueueLeases = new List<SubscriptionGroupLease>();
                foreach (var list in _activeQueueSubscriptions.Values)
                    allQueueLeases.AddRange(list);

                queueLeases = allQueueLeases.ToArray();
                _activeQueueSubscriptions.Clear();
            }

            foreach (var lease in dataLeases)
                lease.Dispose();
            foreach (var lease in queueLeases)
                lease.Dispose();
        }

        private bool IsDisposed_NoLock()
        {
            return Volatile.Read(ref _disposed) != 0;
        }

        private static SubscriptionGroupLease CreateLease<T>(DataSubscription<T> handle)
        {
            if (handle == null)
                throw new ArgumentNullException(nameof(handle));

            return new SubscriptionGroupLease(
                handle.Topic,
                handle.UnsubscribeAsync,
                handle.Dispose);
        }

        private static SubscriptionGroupLease CreateLease(QueueSubscription subscription)
        {
            if (subscription == null)
                throw new ArgumentNullException(nameof(subscription));

            return new SubscriptionGroupLease(
                subscription.Topic,
                subscription.UnsubscribeAsync,
                subscription.Dispose);
        }

        private void ThrowIfDisposed_NoLock()
        {
            if (IsDisposed_NoLock())
                throw new ObjectDisposedException(nameof(SubscriptionGroup));
        }
    }
}