using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    public sealed partial class PullSubRuntime
    {
        public bool TryDequeue(string topic, out PullSubQueueMessage message)
        {
            return _rawInbox.TryDequeue(topic, out message);
        }

        public bool TryGetDroppedCount(string topic, out long droppedCount)
        {
            return _rawInbox.TryGetDroppedCount(topic, out droppedCount);
        }

        public async Task<PullSubQueueMessage> ReceiveQueueAsync(string topic, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            PullSubSubscriptionRegistry.ValidateExactMatchTopic(topic);
            var operationToken = CreateOperationToken(cancellationToken, out var linkedCts);

            try
            {
                return await _rawInbox.DequeueAsync(topic, operationToken);
            }
            finally
            {
                linkedCts?.Dispose();
            }
        }

        /// <summary>
        /// 最後に受信したデータを取得します。SubscribeDataAsync&lt;T&gt; で購読済みであることが前提です。
        /// </summary>
        public bool TryGetData<T>(string topic, out T value)
        {
            return TryGetData<T>(topic, out value, out _);
        }

        /// <summary>
        /// 最後に受信したデータを、受信発生時刻(またはペイロード時刻)と共に取得します。
        /// SubscribeDataAsync&lt;T&gt; で購読済みであることが前提です。
        /// </summary>
        public bool TryGetData<T>(string topic, out T value, out System.DateTime timestampUtc)
        {
            if (_typedDataRegistry.TryGetCache<T>(topic, out var cache))
                return cache.TryGet(out value, out timestampUtc);

            value = default;
            timestampUtc = default;
            return false;
        }

        /// <summary>
        /// 最初のデータが到着するまで非同期に待機します。SubscribeDataAsync&lt;T&gt; で購読済みであることが前提です。
        /// </summary>
        public async Task<T> WaitForFirstDataAsync<T>(string topic, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            PullSubSubscriptionRegistry.ValidateExactMatchTopic(topic);

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
        /// トピックのデータハンドルを取得します。
        /// SubscribeDataAsync&lt;T&gt; で購読済みであることが前提です。
        /// </summary>
        public PullSubDataHandle<T> GetDataHandle<T>(string topic)
        {
            ThrowIfDisposed();
            PullSubSubscriptionRegistry.ValidateExactMatchTopic(topic);
            return new PullSubDataHandle<T>(this, topic);
        }

        public T GetData<T>(string topic, T defaultValue = default)
        {
            return TryGetData<T>(topic, out var value) ? value : defaultValue;
        }
    }
}
