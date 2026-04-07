using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    public sealed class PullSubContext : IDisposable, IAsyncDisposable
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

        internal readonly struct ContextDebugSnapshot
        {
            public ContextDebugSnapshot(string debugLabel, ContextSubscriptionDebugSnapshot[] subscriptions)
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

        private sealed class ContextSubscriptionLease : IDisposable
        {
            private readonly Func<CancellationToken, Task<PullSubUnsubscribeResult>> _unsubscribeAsync;
            private readonly Action _dispose;

            public ContextSubscriptionLease(
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
        private readonly HashSet<SubscriptionKey> _inFlightTopics = new HashSet<SubscriptionKey>();
        private readonly Dictionary<SubscriptionKey, ContextSubscriptionLease> _activeSubscriptions
            = new Dictionary<SubscriptionKey, ContextSubscriptionLease>();
        private string _debugLabel;

        private int _disposed;

        internal PullSubContext(PullSubRuntime runtime)
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

        internal ContextDebugSnapshot GetDebugSnapshot()
        {
            lock (_gate)
            {
                var subscriptions = new ContextSubscriptionDebugSnapshot[_activeSubscriptions.Count];
                var index = 0;
                foreach (var pair in _activeSubscriptions)
                {
                    subscriptions[index++] = new ContextSubscriptionDebugSnapshot(
                        pair.Key.Topic,
                        pair.Key.Kind == SubscriptionKind.Queue);
                }

                return new ContextDebugSnapshot(_debugLabel, subscriptions);
            }
        }

        public async Task<PullSubDataHandle<T>> SubscribeDataAsync<T>(
            IPullSubTopic<T> topic,
            PullSubQualityOfServiceLevel subscribeQos = PullSubQualityOfServiceLevel.AtLeastOnce,
            CancellationToken cancellationToken = default)
        {
            if (topic == null)
                throw new ArgumentNullException(nameof(topic));

            var topicName = topic.TopicName;
            PullSubSubscriptionRegistry.ValidateExactMatchTopic(topicName);
            var key = new SubscriptionKey(topicName, SubscriptionKind.Data);

            lock (_gate)
            {
                ThrowIfDisposed_NoLock();

                if (_inFlightTopics.Contains(key) || _activeSubscriptions.ContainsKey(key))
                {
                    throw new InvalidOperationException(
                        $"Topic '{topicName}' is already subscribed in this context.");
                }

                _inFlightTopics.Add(key);
            }

            PullSubDataHandle<T> handle;
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
                    _inFlightTopics.Remove(key);
                }

                throw;
            }

            var shouldCleanupImmediately = false;
            lock (_gate)
            {
                _inFlightTopics.Remove(key);

                if (IsDisposed_NoLock())
                {
                    shouldCleanupImmediately = true;
                }
                else
                {
                    _activeSubscriptions[key] = CreateLease(handle);
                }
            }

            if (shouldCleanupImmediately)
            {
                await handle.UnsubscribeAsync(CancellationToken.None).ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(PullSubContext));
            }

            return handle;
        }

        public Task<PullSubQueueSubscription> SubscribeQueueAsync<T>(
            IPullSubTopic<T> topic,
            Func<T, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            return SubscribeQueueAsync(topic, PullSubQueueOptions.Default, handler, cancellationToken);
        }

        public Task<PullSubQueueSubscription> SubscribeQueueAsync<T>(
            IPullSubTopic<T> topic,
            Func<T, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return SubscribeQueueAsync(topic, PullSubQueueOptions.Default, (message, _) => handler(message), cancellationToken);
        }

        public Task<PullSubQueueSubscription> SubscribeQueueAsync<T>(
            IPullSubTopic<T> topic,
            Action<T, CancellationToken> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return SubscribeQueueAsync(
                topic,
                PullSubQueueOptions.Default,
                (message, ct) =>
                {
                    handler(message, ct);
                    return default;
                },
                cancellationToken);
        }

        public Task<PullSubQueueSubscription> SubscribeQueueAsync<T>(
            IPullSubTopic<T> topic,
            Action<T> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return SubscribeQueueAsync(
                topic,
                PullSubQueueOptions.Default,
                (message, _) =>
                {
                    handler(message);
                    return default;
                },
                cancellationToken);
        }

        public Task<PullSubQueueSubscription> SubscribeQueueAsync<T>(
            IPullSubTopic<T> topic,
            PullSubQueueOptions options,
            Func<T, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return SubscribeQueueAsync(topic, options, (message, _) => handler(message), cancellationToken);
        }

        public Task<PullSubQueueSubscription> SubscribeQueueAsync<T>(
            IPullSubTopic<T> topic,
            PullSubQueueOptions options,
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

        public Task<PullSubQueueSubscription> SubscribeQueueAsync<T>(
            IPullSubTopic<T> topic,
            PullSubQueueOptions options,
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

        public async Task<PullSubQueueSubscription> SubscribeQueueAsync<T>(
            IPullSubTopic<T> topic,
            PullSubQueueOptions options,
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
            PullSubSubscriptionRegistry.ValidateExactMatchTopic(topicName);
            var key = new SubscriptionKey(topicName, SubscriptionKind.Queue);

            lock (_gate)
            {
                ThrowIfDisposed_NoLock();

                if (_inFlightTopics.Contains(key) || _activeSubscriptions.ContainsKey(key))
                {
                    throw new InvalidOperationException(
                        $"Topic '{topicName}' is already registered as Queue in this context.");
                }

                _inFlightTopics.Add(key);
            }

            PullSubQueueSubscription registration;
            try
            {
                registration = await _runtime.SubscribeQueueAsync(topic, options, handler, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                lock (_gate)
                {
                    _inFlightTopics.Remove(key);
                }

                throw;
            }

            var shouldCleanupImmediately = false;
            lock (_gate)
            {
                _inFlightTopics.Remove(key);

                if (IsDisposed_NoLock())
                {
                    shouldCleanupImmediately = true;
                }
                else
                {
                    _activeSubscriptions[key] = CreateLease(registration);
                }
            }

            if (shouldCleanupImmediately)
            {
                await registration.UnsubscribeAsync(CancellationToken.None).ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(PullSubContext));
            }

            return registration;
        }

        public async Task<PullSubUnsubscribeResult> UnsubscribeDataAsync(
            string topic,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("topic is required.", nameof(topic));

            PullSubSubscriptionRegistry.ValidateExactMatchTopic(topic);

            return await UnsubscribeInternalAsync(
                    new SubscriptionKey(topic, SubscriptionKind.Data),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<PullSubUnsubscribeResult> UnsubscribeQueueAsync(
            string topic,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("topic is required.", nameof(topic));

            PullSubSubscriptionRegistry.ValidateExactMatchTopic(topic);

            return await UnsubscribeInternalAsync(
                    new SubscriptionKey(topic, SubscriptionKind.Queue),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<PullSubUnsubscribeResult> UnsubscribeInternalAsync(
            SubscriptionKey key,
            CancellationToken cancellationToken)
        {
            ContextSubscriptionLease subscription;

            lock (_gate)
            {
                if (!_activeSubscriptions.TryGetValue(key, out subscription))
                    return PullSubUnsubscribeResult.AlreadyCanceled;

                _activeSubscriptions.Remove(key);
            }

            return await subscription.UnsubscribeAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            PullSubContextDebugTracker.Unregister(_runtime, this);

            ContextSubscriptionLease[] subscriptions;
            lock (_gate)
            {
                subscriptions = _activeSubscriptions.Values.ToArray();
                _activeSubscriptions.Clear();
                _inFlightTopics.Clear();
            }

            var tasks = new List<Task>(subscriptions.Length);
            foreach (var subscription in subscriptions)
            {
                tasks.Add(subscription.UnsubscribeAsync(CancellationToken.None));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            PullSubContextDebugTracker.Unregister(_runtime, this);

            ContextSubscriptionLease[] subscriptions;
            lock (_gate)
            {
                subscriptions = _activeSubscriptions.Values.ToArray();
                _activeSubscriptions.Clear();
                _inFlightTopics.Clear();
            }

            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }
        }

        private bool IsDisposed_NoLock()
        {
            return Volatile.Read(ref _disposed) != 0;
        }

        private static ContextSubscriptionLease CreateLease<T>(PullSubDataHandle<T> handle)
        {
            if (handle == null)
                throw new ArgumentNullException(nameof(handle));

            return new ContextSubscriptionLease(
                handle.Topic,
                handle.UnsubscribeAsync,
                handle.Dispose);
        }

        private static ContextSubscriptionLease CreateLease(PullSubQueueSubscription subscription)
        {
            if (subscription == null)
                throw new ArgumentNullException(nameof(subscription));

            return new ContextSubscriptionLease(
                subscription.Topic,
                subscription.UnsubscribeAsync,
                subscription.Dispose);
        }

        private void ThrowIfDisposed_NoLock()
        {
            if (IsDisposed_NoLock())
                throw new ObjectDisposedException(nameof(PullSubContext));
        }
    }
}