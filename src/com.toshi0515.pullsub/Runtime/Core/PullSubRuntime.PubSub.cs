using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    public sealed partial class PullSubRuntime
    {
        public async Task PublishRawAsync(
            string topic,
            byte[] payload,
            PullSubQualityOfServiceLevel qos = 0,
            bool retain = false,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            SubscriptionRegistry.ValidateExactMatchTopic(topic);

            if (payload == null)
                throw new ArgumentNullException(nameof(payload));

            EnsureStarted();

            if (!_transport.IsConnected)
                throw new PullSubConnectionStateException(nameof(PublishRawAsync));

            var operationToken = CreateOperationToken(cancellationToken, out var linkedCts);
            try
            {
                await _transport.PublishAsync(topic, payload, qos, retain, operationToken);
            }
            finally
            {
                linkedCts?.Dispose();
            }
        }

        public Task PublishDataAsync<T>(
            string topic,
            T value,
            IPayloadCodec<T> codec,
            PullSubQualityOfServiceLevel qos = 0,
            bool retain = false,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            SubscriptionRegistry.ValidateExactMatchTopic(topic);

            if (codec == null)
                throw new ArgumentNullException(nameof(codec));

            var timestampUtc = DateTime.UtcNow;
            var buffer = new ArrayBufferWriter<byte>(256);
            codec.Encode(timestampUtc, value, buffer);
            var bytes = buffer.WrittenCount == 0
                ? Array.Empty<byte>()
                : buffer.WrittenMemory.ToArray();

            if (bytes == null)
                throw new InvalidOperationException("Payload codec returned null bytes.");

            return PublishRawAsync(topic, bytes, qos, retain, cancellationToken);
        }

        public async Task SubscribeQueueAsync(
            string topic,
            QueueOptions options,
            PullSubQualityOfServiceLevel subscribeQos = PullSubQualityOfServiceLevel.AtLeastOnce,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            SubscriptionRegistry.ValidateExactMatchTopic(topic);
            var operationToken = CreateOperationToken(cancellationToken, out var linkedCts);

            try
            {
                await _subscriptionGate.WaitAsync(operationToken);
            }
            catch
            {
                linkedCts?.Dispose();
                throw;
            }

            if (_subscriptions.IsQueueSubRegistered(topic))
            {
                _subscriptionGate.Release();
                linkedCts?.Dispose();
                throw new InvalidOperationException(
                    $"Topic '{topic}' is already subscribed as Queue. Call UnsubscribeQueueAsync first.");
            }

            var shouldNetworkSubscribe = _subscriptions.RegisterQueueSub(topic, subscribeQos);
            try
            {
                try
                {
                    _rawInbox.ConfigureTopic(topic, options);
                }
                catch
                {
                    _subscriptions.UnregisterQueueSub(topic);
                    throw;
                }

                if (shouldNetworkSubscribe && _transport.IsConnected && !IsNetworkSubscribedTopic(topic))
                {
                    try
                    {
                        await SubscribeNetworkAsync(topic, subscribeQos, operationToken);
                    }
                    catch
                    {
                        _subscriptions.UnregisterQueueSub(topic);

                        if (!_subscriptions.IsQueueSubRegistered(topic))
                            _rawInbox.RemoveTopic(topic);

                        if (_subscriptions.TryGetSubscribeQos(topic, out var fallbackSubscribeQos))
                            _ = EnsureNetworkSubscriptionConsistencyAsync(topic, fallbackSubscribeQos);

                        throw;
                    }
                }
            }
            finally
            {
                _subscriptionGate.Release();
                linkedCts?.Dispose();
            }
        }

        public async Task SubscribeDataAsync<T>(
            string topic,
            IPayloadCodec<T> codec,
            PullSubQualityOfServiceLevel subscribeQos = PullSubQualityOfServiceLevel.AtLeastOnce,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            SubscriptionRegistry.ValidateExactMatchTopic(topic);

            if (codec == null)
                throw new ArgumentNullException(nameof(codec));

            var operationToken = CreateOperationToken(cancellationToken, out var linkedCts);

            var gateAcquired = false;
            try
            {
                await _subscriptionGate.WaitAsync(operationToken);
                gateAcquired = true;

                var typedRegistered = false;
                var networkRegistered = false;
                try
                {
                    _typedDataRegistry.Register<T>(topic, codec, out _);
                    typedRegistered = true;
                    networkRegistered = _subscriptions.RegisterDataSub(topic, subscribeQos);
                }
                catch
                {
                    if (typedRegistered)
                        _typedDataRegistry.Unregister(topic);
                    throw;
                }

                if (networkRegistered && _transport.IsConnected && !IsNetworkSubscribedTopic(topic))
                {
                    try
                    {
                        await SubscribeNetworkAsync(topic, subscribeQos, operationToken);
                    }
                    catch
                    {
                        _typedDataRegistry.Unregister(topic);
                        _subscriptions.UnregisterDataSub(topic);

                        if (_subscriptions.TryGetSubscribeQos(topic, out var fallbackSubscribeQos))
                            _ = EnsureNetworkSubscriptionConsistencyAsync(topic, fallbackSubscribeQos);

                        throw;
                    }
                }
            }
            finally
            {
                if (gateAcquired)
                    _subscriptionGate.Release();

                linkedCts?.Dispose();
            }
        }

        public async Task UnsubscribeQueueAsync(string topic, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            SubscriptionRegistry.ValidateExactMatchTopic(topic);
            var operationToken = CreateOperationToken(cancellationToken, out var linkedCts);

            try
            {
                await _subscriptionGate.WaitAsync(operationToken);
            }
            catch
            {
                linkedCts?.Dispose();
                throw;
            }

            var shouldNetworkUnsubscribe = _subscriptions.UnregisterQueueSub(topic);
            try
            {
                if (!_subscriptions.IsQueueSubRegistered(topic))
                    _rawInbox.RemoveTopic(topic);

                if (shouldNetworkUnsubscribe && _transport.IsConnected && IsNetworkSubscribedTopic(topic))
                {
                    await UnsubscribeNetworkAsync(topic, operationToken);
                }
            }
            finally
            {
                _subscriptionGate.Release();
                linkedCts?.Dispose();
            }
        }

        public async Task UnsubscribeDataAsync(string topic, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            SubscriptionRegistry.ValidateExactMatchTopic(topic);
            var operationToken = CreateOperationToken(cancellationToken, out var linkedCts);

            try
            {
                await _subscriptionGate.WaitAsync(operationToken);
            }
            catch
            {
                linkedCts?.Dispose();
                throw;
            }

            _typedDataRegistry.Unregister(topic);
            var shouldNetworkUnsubscribe = _subscriptions.UnregisterDataSub(topic);
            try
            {
                if (shouldNetworkUnsubscribe && _transport.IsConnected && IsNetworkSubscribedTopic(topic))
                {
                    await UnsubscribeNetworkAsync(topic, operationToken);
                }
            }
            finally
            {
                _subscriptionGate.Release();
                linkedCts?.Dispose();
            }
        }
    }
}
