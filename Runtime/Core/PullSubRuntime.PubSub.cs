using System;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    public sealed partial class PullSubRuntime
    {
        public Task PublishRawAsync(
            string topic,
            byte[] payload,
            MqttQualityOfServiceLevel qos = 0,
            bool retain = false,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            PullSubSubscriptionRegistry.ValidateExactMatchTopic(topic);

            if (payload == null)
                throw new ArgumentNullException(nameof(payload));

            EnsureStarted();

            return PullSubAsyncUtils.AwaitWithCancellation(
                _transport.EnqueueAsync(topic, payload, qos, retain),
                cancellationToken,
                _disposeCts.Token);
        }

        public Task PublishDataAsync<T>(
            string topic,
            T value,
            IPayloadCodec<T> codec,
            MqttQualityOfServiceLevel qos = 0,
            bool retain = false,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            PullSubSubscriptionRegistry.ValidateExactMatchTopic(topic);

            if (codec == null)
                throw new ArgumentNullException(nameof(codec));

            var bytes = codec.Encode(DateTime.UtcNow, value);
            if (bytes == null)
                throw new InvalidOperationException("Payload codec returned null bytes.");

            return PublishRawAsync(topic, bytes, qos, retain, cancellationToken);
        }

        public Task SubscribeQueueAsync(
            string topic,
            PullSubQueueOptions options,
            CancellationToken cancellationToken = default)
        {
            var subscribeQos = Profile.ConnectionOptions.SubscriptionDefaults.SubscribeQos;
            return SubscribeQueueAsync(topic, options, subscribeQos, cancellationToken);
        }

        public async Task SubscribeQueueAsync(
            string topic,
            PullSubQueueOptions options,
            MqttQualityOfServiceLevel subscribeQos,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            PullSubSubscriptionRegistry.ValidateExactMatchTopic(topic);
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

            if (_subscriptions.IsRawRegistered(topic))
            {
                _subscriptionGate.Release();
                linkedCts?.Dispose();
                throw new InvalidOperationException(
                    $"Topic '{topic}' is already subscribed as Queue. Call UnsubscribeQueueAsync first.");
            }

            var shouldNetworkSubscribe = _subscriptions.RegisterRaw(topic, subscribeQos);
            try
            {
                try
                {
                    _rawInbox.ConfigureTopic(topic, options);
                }
                catch
                {
                    _subscriptions.UnregisterRaw(topic);
                    throw;
                }

                if (shouldNetworkSubscribe && _transport.IsConnected)
                {
                    try
                    {
                        await SubscribeNetworkAsync(topic, subscribeQos, operationToken);
                    }
                    catch
                    {
                        _subscriptions.UnregisterRaw(topic);

                        if (!_subscriptions.IsRawRegistered(topic))
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

        /// <summary>
        /// 型付きデータ購読を開始します。同一トピックへの複数回呼び出しは refcount でまとめられます。
        /// 同一トピックへ再登録する場合、Codec は同値（<see cref="object.Equals(object)"/>）である必要があります。
        /// 同一トピックへ再登録する場合、Subscribe QoS も同一である必要があります。
        /// 解除するには <see cref="UnsubscribeDataAsync"/> を呼んでください。
        /// </summary>
        public Task SubscribeDataAsync<T>(
            string topic,
            IPayloadCodec<T> codec,
            CancellationToken cancellationToken = default)
        {
            var subscribeQos = Profile.ConnectionOptions.SubscriptionDefaults.SubscribeQos;
            return SubscribeDataAsync(topic, codec, subscribeQos, cancellationToken);
        }

        public async Task SubscribeDataAsync<T>(
            string topic,
            IPayloadCodec<T> codec,
            MqttQualityOfServiceLevel subscribeQos,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            PullSubSubscriptionRegistry.ValidateExactMatchTopic(topic);

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
                    networkRegistered = _subscriptions.RegisterData(topic, subscribeQos);
                }
                catch
                {
                    if (typedRegistered)
                        _typedDataRegistry.Unregister(topic);
                    throw;
                }

                if (networkRegistered && _transport.IsConnected)
                {
                    try
                    {
                        await SubscribeNetworkAsync(topic, subscribeQos, operationToken);
                    }
                    catch
                    {
                        _typedDataRegistry.Unregister(topic);
                        _subscriptions.UnregisterData(topic);

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
            PullSubSubscriptionRegistry.ValidateExactMatchTopic(topic);
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

            var shouldNetworkUnsubscribe = _subscriptions.UnregisterRaw(topic);
            try
            {
                if (!_subscriptions.IsRawRegistered(topic))
                    _rawInbox.RemoveTopic(topic);

                if (shouldNetworkUnsubscribe && _transport.IsConnected)
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
            PullSubSubscriptionRegistry.ValidateExactMatchTopic(topic);
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
            var shouldNetworkUnsubscribe = _subscriptions.UnregisterData(topic);
            try
            {
                if (shouldNetworkUnsubscribe && _transport.IsConnected)
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
