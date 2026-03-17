using System.Threading;
using System.Threading.Tasks;

namespace UnityMqtt.V2.Core
{
    public sealed partial class MqttV2ClientRuntime
    {
        public bool TryDequeueRaw(string topic, out MqttV2RawMessage message)
        {
            return _rawInbox.TryDequeue(topic, out message);
        }

        public bool TryGetRawDroppedCount(string topic, out long droppedCount)
        {
            return _rawInbox.TryGetDroppedCount(topic, out droppedCount);
        }

        public async Task<MqttV2RawMessage> ReceiveRawAsync(string topic, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            MqttV2SubscriptionRegistry.ValidateExactMatchTopic(topic);
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
            if (_typedDataRegistry.TryGetCache<T>(topic, out var cache))
                return cache.TryGet(out value);

            value = default;
            return false;
        }

        /// <summary>
        /// 最初のデータが到着するまで非同期に待機します。SubscribeDataAsync&lt;T&gt; で購読済みであることが前提です。
        /// </summary>
        public async Task<T> WaitForFirstDataAsync<T>(string topic, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            MqttV2SubscriptionRegistry.ValidateExactMatchTopic(topic);

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
    }
}
