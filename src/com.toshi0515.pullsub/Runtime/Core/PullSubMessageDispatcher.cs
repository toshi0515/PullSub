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
        private readonly PullSubRuntimeOptions _runtimeOptions;
        private readonly Action<string, int> _onInboundOversizeDrop;

        public PullSubMessageDispatcher(
            PullSubSubscriptionRegistry subscriptions,
            TypedDataRegistry typedDataRegistry,
            PullSubQueueInbox rawInbox,
            Action<string> logError,
            Action<Exception> logException,
            PullSubRuntimeOptions runtimeOptions,
            Action<string, int> onInboundOversizeDrop)
        {
            _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
            _typedDataRegistry = typedDataRegistry ?? throw new ArgumentNullException(nameof(typedDataRegistry));
            _rawInbox = rawInbox ?? throw new ArgumentNullException(nameof(rawInbox));
            _logError = logError ?? (_ => { });
            _logException = logException ?? (_ => { });
            _runtimeOptions = runtimeOptions ?? throw new ArgumentNullException(nameof(runtimeOptions));
            _onInboundOversizeDrop = onInboundOversizeDrop ?? throw new ArgumentNullException(nameof(onInboundOversizeDrop));
        }

        public void Dispatch(string topic, ReadOnlyMemory<byte> payload)
        {
            if (string.IsNullOrWhiteSpace(topic))
                return;

            try
            {
                var hasRaw = _subscriptions.IsQueueSubRegistered(topic);
                var hasData = _typedDataRegistry.IsRegistered(topic);

                if (!hasRaw && !hasData)
                    return;

                if (payload.Length > _runtimeOptions.MaxInboundPayloadBytes)
                {
                    _onInboundOversizeDrop(topic, payload.Length);
                    return;
                }

                if (hasRaw)
                {
                    _rawInbox.Enqueue(topic, payload.ToArray());

                    if (hasData)
                        ProcessDataPayload(topic, payload.Span);
                }
                else
                {
                    ProcessDataPayload(topic, payload.Span);
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