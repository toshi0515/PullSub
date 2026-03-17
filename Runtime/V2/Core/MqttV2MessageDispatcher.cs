using System;
using MQTTnet;

namespace UnityMqtt.V2.Core
{
    internal sealed class MqttV2MessageDispatcher
    {
        private readonly MqttV2SubscriptionRegistry _subscriptions;
        private readonly TypedDataRegistry _typedDataRegistry;
        private readonly MqttV2RawInbox _rawInbox;
        private readonly Action<string> _logError;
        private readonly Action<Exception> _logException;

        public MqttV2MessageDispatcher(
            MqttV2SubscriptionRegistry subscriptions,
            TypedDataRegistry typedDataRegistry,
            MqttV2RawInbox rawInbox,
            Action<string> logError,
            Action<Exception> logException)
        {
            _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
            _typedDataRegistry = typedDataRegistry ?? throw new ArgumentNullException(nameof(typedDataRegistry));
            _rawInbox = rawInbox ?? throw new ArgumentNullException(nameof(rawInbox));
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
                var hasRaw = _subscriptions.IsRawRegistered(topic);
                var hasData = _typedDataRegistry.IsRegistered(topic);

                if (!hasRaw && !hasData)
                    return;

                if (hasRaw)
                {
                    // Raw queue stores payload beyond this callback, so cloning is required.
                    var payload = ClonePayload(message.PayloadSegment);
                    _rawInbox.Enqueue(topic, payload);

                    if (hasData)
                        ProcessDataPayload(topic, payload);
                }
                else
                {
                    // Data-only path can decode directly from MQTTnet payload without cloning.
                    ProcessDataPayload(topic, message.PayloadSegment.AsSpan());
                }
            }
            catch (Exception ex)
            {
                _logException(ex);
            }
        }

        private void ProcessDataPayload(string topic, ReadOnlySpan<byte> payload)
        {
            if (!_typedDataRegistry.TryDecodeAndUpdate(topic, payload))
            {
                _logError($"[MQTT-V2] typed payload decode failed. topic={topic}");
            }
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