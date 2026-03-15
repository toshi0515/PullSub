using System;
using MQTTnet;

namespace UnityMqtt.V2.Core
{
    internal sealed class MqttV2MessageDispatcher
    {
        private readonly MqttV2SubscriptionRegistry _subscriptions;
        private readonly MqttV2RawInbox _rawInbox;
        private readonly MqttV2DataCache _dataCache;
        private readonly MqttV2TopicUpdateModeStore _updateModeStore;
        private readonly IMqttV2PayloadCodec _payloadCodec;
        private readonly Action<string> _logError;
        private readonly Action<Exception> _logException;

        public MqttV2MessageDispatcher(
            MqttV2SubscriptionRegistry subscriptions,
            MqttV2RawInbox rawInbox,
            MqttV2DataCache dataCache,
            MqttV2TopicUpdateModeStore updateModeStore,
            IMqttV2PayloadCodec payloadCodec,
            Action<string> logError,
            Action<Exception> logException)
        {
            _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
            _rawInbox = rawInbox ?? throw new ArgumentNullException(nameof(rawInbox));
            _dataCache = dataCache ?? throw new ArgumentNullException(nameof(dataCache));
            _updateModeStore = updateModeStore ?? throw new ArgumentNullException(nameof(updateModeStore));
            _payloadCodec = payloadCodec ?? throw new ArgumentNullException(nameof(payloadCodec));
            _logError = logError ?? (_ => { });
            _logException = logException ?? (_ => { });
        }

        public void Dispatch(MqttApplicationMessage message)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.Topic))
                return;

            try
            {
                var topic = message.Topic;
                var hasRawSubscription = _subscriptions.IsRawRegistered(topic);
                var hasDataSubscription = _subscriptions.IsDataRegistered(topic);

                if (!hasRawSubscription && !hasDataSubscription)
                    return;

                var payload = ClonePayload(message.PayloadSegment);

                if (hasRawSubscription)
                    _rawInbox.Enqueue(topic, payload);

                if (hasDataSubscription)
                    ProcessDataPayload(topic, payload);
            }
            catch (Exception ex)
            {
                _logException(ex);
            }
        }

        private void ProcessDataPayload(string topic, byte[] payload)
        {
            if (!_payloadCodec.TryDecode(payload, out var fields, out var timestampUtc, out var codecError))
            {
                _logError($"[MQTT-V2] payload codec decode failed. topic={topic}, error={codecError}");
                return;
            }

            var mode = _updateModeStore.GetMode(topic);
            _dataCache.ApplyFields(topic, fields, timestampUtc, mode);
        }

        private static byte[] ClonePayload(ArraySegment<byte> payload)
        {
            if (payload.Array == null || payload.Count == 0)
                return Array.Empty<byte>();

            var cloned = new byte[payload.Count];
            Buffer.BlockCopy(payload.Array, payload.Offset, cloned, 0, payload.Count);
            return cloned;
        }
    }
}