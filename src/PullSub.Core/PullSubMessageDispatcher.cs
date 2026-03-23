using System;

namespace PullSub.Core
{
    internal sealed class PullSubMessageDispatcher
    {
        private readonly PullSubSubscriptionRegistry _subscriptions;
        private readonly TypedDataRegistry _typedDataRegistry;
        private readonly PullSubQueueInbox _rawInbox;
        private readonly Action<string> _logError;
        private readonly Action<Exception> _logException;

        public PullSubMessageDispatcher(
            PullSubSubscriptionRegistry subscriptions,
            TypedDataRegistry typedDataRegistry,
            PullSubQueueInbox rawInbox,
            Action<string> logError,
            Action<Exception> logException)
        {
            _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
            _typedDataRegistry = typedDataRegistry ?? throw new ArgumentNullException(nameof(typedDataRegistry));
            _rawInbox = rawInbox ?? throw new ArgumentNullException(nameof(rawInbox));
            _logError = logError ?? (_ => { });
            _logException = logException ?? (_ => { });
        }

        public void Dispatch(string topic, byte[] payload)
        {
            if (string.IsNullOrWhiteSpace(topic) || payload == null)
                return;

            try
            {
                var hasRaw = _subscriptions.IsRawRegistered(topic);
                var hasData = _typedDataRegistry.IsRegistered(topic);

                if (!hasRaw && !hasData)
                    return;

                if (hasRaw)
                {
                    _rawInbox.Enqueue(topic, payload);

                    if (hasData)
                        ProcessDataPayload(topic, payload);
                }
                else
                {
                    ProcessDataPayload(topic, payload.AsSpan());
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
                _logError($"[PullSubMessageDispatcher] typed payload decode failed. topic={topic}");
            }
        }
    }
}