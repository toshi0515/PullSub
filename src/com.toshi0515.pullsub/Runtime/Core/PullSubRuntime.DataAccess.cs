using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    public sealed partial class PullSubRuntime
    {
        internal bool TryDequeue(string topic, System.Guid subscriberId, out QueueMessage message)
        {
            return _rawInbox.TryDequeue(topic, subscriberId, out message);
        }

        internal bool TryGetTotalDroppedCount(string topic, out long droppedCount)
        {
            return _rawInbox.TryGetTotalDroppedCount(topic, out droppedCount);
        }

        internal bool TryGetDroppedCount(string topic, System.Guid subscriberId, out long droppedCount)
        {
            return _rawInbox.TryGetDroppedCount(topic, subscriberId, out droppedCount);
        }

        internal async Task<QueueMessage> ReceiveQueueAsync(
            string topic,
            System.Guid subscriberId,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            SubscriptionRegistry.ValidateExactMatchTopic(topic);
            var operationToken = CreateOperationToken(cancellationToken, out var linkedCts);

            try
            {
                return await _rawInbox.DequeueAsync(topic, subscriberId, operationToken);
            }
            finally
            {
                linkedCts?.Dispose();
            }
        }

        /// <summary>
        /// Gets the latest received data. Assumes the topic has been subscribed via SubscribeDataAsync<T>.
        /// </summary>
        internal bool TryGetData<T>(string topic, out T value)
        {
            return TryGetData<T>(topic, out value, out _);
        }

        /// <summary>
        /// Gets the latest received data along with its timestamp (received time or payload timestamp).
        /// Assumes the topic has been subscribed via SubscribeDataAsync<T>.
        /// </summary>
        internal bool TryGetData<T>(string topic, out T value, out System.DateTime timestampUtc)
        {
            if (_typedDataRegistry.TryGetCache<T>(topic, out var cache))
                return cache.TryGet(out value, out timestampUtc);

            value = default;
            timestampUtc = default;
            return false;
        }

        /// <summary>
        /// Asynchronously waits until the first message arrives. Assumes the topic has been subscribed via SubscribeDataAsync<T>.
        /// </summary>
        internal async Task<T> WaitForFirstDataAsync<T>(string topic, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            SubscriptionRegistry.ValidateExactMatchTopic(topic);

            if (!_typedDataRegistry.TryGetCache<T>(topic, out var cache))
                throw new System.InvalidOperationException(
                    $"Topic '{topic}' is not registered for type {typeof(T).Name}. Call SubscribeDataAsync<T> first.");

            var operationToken = CreateOperationToken(cancellationToken, out var linkedCts);
            try
            {
                return await cache.WaitForFirstAsync(operationToken);
            }
            finally
            {
                linkedCts?.Dispose();
            }
        }

        /// <summary>
        /// Returns a data subscription handle for the topic.
        /// Assumes the topic has been subscribed via SubscribeDataAsync<T>.
        /// </summary>
        internal DataSubscription<T> GetDataHandle<T>(string topic)
        {
            ThrowIfDisposed();
            SubscriptionRegistry.ValidateExactMatchTopic(topic);

            if (!_typedDataRegistry.TryGetCache<T>(topic, out var cache))
                throw new System.InvalidOperationException(
                    $"Topic '{topic}' is not registered for type {typeof(T).Name}. Call SubscribeDataAsync<T> first.");

            return new DataSubscription<T>(this, topic, cache);
        }

        internal T GetData<T>(string topic, T defaultValue = default)
        {
            return TryGetData<T>(topic, out var value) ? value : defaultValue;
        }
    }
}
