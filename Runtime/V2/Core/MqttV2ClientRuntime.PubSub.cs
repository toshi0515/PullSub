using System;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Protocol;

namespace UnityMqtt.V2.Core
{
    public sealed partial class MqttV2ClientRuntime
    {
        public Task PublishRawAsync(
            string topic,
            byte[] payload,
            MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce,
            bool retain = false,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            MqttV2SubscriptionRegistry.ValidateExactMatchTopic(topic);

            if (payload == null)
                throw new ArgumentNullException(nameof(payload));

            EnsureStarted();

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(qos)
                .WithRetainFlag(retain)
                .Build();

            return MqttV2AsyncUtils.AwaitWithCancellation(
                _client.EnqueueAsync(message),
                cancellationToken,
                _disposeCts.Token);
        }

        public Task PublishDataAsync<T>(
            string topic,
            T value,
            IMqttV2PayloadCodec<T> codec,
            MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce,
            bool retain = false,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            MqttV2SubscriptionRegistry.ValidateExactMatchTopic(topic);

            if (codec == null)
                throw new ArgumentNullException(nameof(codec));

            var bytes = codec.Encode(DateTime.UtcNow, value);
            if (bytes == null)
                throw new InvalidOperationException("Payload codec returned null bytes.");

            return PublishRawAsync(topic, bytes, qos, retain, cancellationToken);
        }

        public async Task SubscribeRawAsync(
            string topic,
            MqttV2RawSubscriptionOptions options,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            MqttV2SubscriptionRegistry.ValidateExactMatchTopic(topic);
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
                    $"Topic '{topic}' is already subscribed as Raw. Call UnsubscribeRawAsync first.");
            }

            var shouldNetworkSubscribe = _subscriptions.RegisterRaw(topic);
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

                if (shouldNetworkSubscribe && _client.IsConnected)
                {
                    try
                    {
                        await SubscribeNetworkAsync(topic, operationToken);
                    }
                    catch
                    {
                        _subscriptions.UnregisterRaw(topic);

                        if (!_subscriptions.IsRawRegistered(topic))
                            _rawInbox.RemoveTopic(topic);

                        if (_subscriptions.IsRawRegistered(topic) || _typedDataRegistry.IsRegistered(topic))
                            _ = EnsureNetworkSubscriptionConsistencyAsync(topic);

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
        /// 解除するには <see cref="UnsubscribeDataAsync"/> を呼んでください。
        /// </summary>
        public async Task SubscribeDataAsync<T>(
            string topic,
            IMqttV2PayloadCodec<T> codec,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            MqttV2SubscriptionRegistry.ValidateExactMatchTopic(topic);

            if (codec == null)
                throw new ArgumentNullException(nameof(codec));

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
            bool isFirst;
            try
            {
                isFirst = _typedDataRegistry.Register<T>(topic, codec, out _);
            }
            catch
            {
                _subscriptionGate.Release();
                linkedCts?.Dispose();
                throw;
            }

            try
            {
                if (isFirst && !_subscriptions.IsRawRegistered(topic) && _client.IsConnected)
                {
                    try
                    {
                        await SubscribeNetworkAsync(topic, operationToken);
                    }
                    catch
                    {
                        _typedDataRegistry.Unregister(topic);

                        if (_subscriptions.IsRawRegistered(topic) || _typedDataRegistry.IsRegistered(topic))
                            _ = EnsureNetworkSubscriptionConsistencyAsync(topic);

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

        public async Task UnsubscribeRawAsync(string topic, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            MqttV2SubscriptionRegistry.ValidateExactMatchTopic(topic);
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

                if (shouldNetworkUnsubscribe && !_typedDataRegistry.IsRegistered(topic) && _client.IsConnected)
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
            MqttV2SubscriptionRegistry.ValidateExactMatchTopic(topic);
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

            var isLast = _typedDataRegistry.Unregister(topic);
            try
            {
                if (isLast && !_subscriptions.IsRawRegistered(topic) && _client.IsConnected)
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
