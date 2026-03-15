using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Protocol;

namespace UnityMqtt.V2.Core
{
    public sealed partial class MqttV2ClientRuntime
    {
        public async Task PublishRawAsync(
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

            var operationToken = CreateOperationToken(cancellationToken, out var linkedCts);
            try
            {
                await MqttV2AsyncUtils.AwaitWithCancellation(_client.EnqueueAsync(message), operationToken);
            }
            finally
            {
                linkedCts?.Dispose();
            }
        }

        public Task PublishDataAsync(
            string topic,
            IReadOnlyDictionary<string, object> values,
            MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce,
            bool retain = false,
            CancellationToken cancellationToken = default)
        {
            return PublishDataAsync(topic, DateTime.UtcNow, values, qos, retain, cancellationToken);
        }

        public async Task PublishDataAsync(
            string topic,
            DateTime timestampUtc,
            IReadOnlyDictionary<string, object> values,
            MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce,
            bool retain = false,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            MqttV2SubscriptionRegistry.ValidateExactMatchTopic(topic);

            if (values == null)
                throw new ArgumentNullException(nameof(values));

            var bytes = _payloadCodec.Encode(timestampUtc, values);
            if (bytes == null)
                throw new InvalidOperationException("Payload codec returned null bytes.");

            await PublishRawAsync(topic, bytes, qos, retain, cancellationToken);
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

                        // Another caller may have registered the same topic concurrently.
                        // If a registration remains, attempt a best-effort network subscribe repair.
                        if (_subscriptions.IsRawRegistered(topic) || _subscriptions.IsDataRegistered(topic))
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

        public async Task SubscribeDataAsync(
            string topic,
            MqttV2TopicUpdateMode mode = MqttV2TopicUpdateMode.Differential,
            CancellationToken cancellationToken = default)
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

            _updateModeStore.SetMode(topic, mode);

            var shouldNetworkSubscribe = _subscriptions.RegisterData(topic);
            try
            {
                if (shouldNetworkSubscribe && _client.IsConnected)
                {
                    try
                    {
                        await SubscribeNetworkAsync(topic, operationToken);
                    }
                    catch
                    {
                        _subscriptions.UnregisterData(topic);

                        // Another caller may have registered the same topic concurrently.
                        // If a registration remains, attempt a best-effort network subscribe repair.
                        if (_subscriptions.IsRawRegistered(topic) || _subscriptions.IsDataRegistered(topic))
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

                if (shouldNetworkUnsubscribe && _client.IsConnected)
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

            var shouldNetworkUnsubscribe = _subscriptions.UnregisterData(topic);
            try
            {
                if (shouldNetworkUnsubscribe && _client.IsConnected)
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
