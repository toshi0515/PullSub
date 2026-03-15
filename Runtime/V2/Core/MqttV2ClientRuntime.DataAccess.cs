using System.Collections.Generic;
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

        public bool TryGetDataSnapshot(string topic, out IReadOnlyDictionary<string, MqttV2DataEntry> snapshot)
        {
            return _dataCache.TryGetSnapshot(topic, out snapshot);
        }

        public async Task<IReadOnlyDictionary<string, MqttV2DataEntry>> WaitForFirstDataAsync(
            string topic,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            MqttV2SubscriptionRegistry.ValidateExactMatchTopic(topic);
            var operationToken = CreateOperationToken(cancellationToken, out var linkedCts);

            try
            {
                return await _dataCache.WaitForSnapshotAsync(topic, operationToken);
            }
            finally
            {
                linkedCts?.Dispose();
            }
        }

        public bool TryGetDataValue<T>(string topic, string key, out T value)
        {
            return _dataCache.TryGetValue(topic, key, out value);
        }

        public void SetDataUpdateMode(string topic, MqttV2TopicUpdateMode mode)
        {
            ThrowIfDisposed();
            _updateModeStore.SetMode(topic, mode);
        }
    }
}
