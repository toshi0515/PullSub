using System;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    public static class PullSubRuntimeExtensions
    {
        public static SubscriptionGroup CreateGroup(this PullSubRuntime runtime)
        {
            if (runtime == null)
                throw new ArgumentNullException(nameof(runtime));

            var context = new SubscriptionGroup(runtime);
            SubscriptionGroupDebugTracker.Register(runtime, context);
            return context;
        }

        public static async Task<DataSubscription<T>> SubscribeDataAsync<T>(
            this PullSubRuntime runtime,
            ITopic<T> topic,
            PullSubQualityOfServiceLevel subscribeQos = PullSubQualityOfServiceLevel.AtLeastOnce,
            CancellationToken cancellationToken = default)
        {
            if (runtime == null)
                throw new ArgumentNullException(nameof(runtime));

            if (topic == null)
                throw new ArgumentNullException(nameof(topic));

            await runtime.SubscribeDataTopicAsync(topic, subscribeQos, cancellationToken).ConfigureAwait(false);
            return runtime.GetDataHandle(topic);
        }

        internal static Task SubscribeDataAsync<T>(
            this PullSubRuntime runtime,
            string topic,
            CancellationToken cancellationToken = default)
        {
            return runtime.SubscribeDataAsync<T>(
                topic,
                JsonPayloadCodec<T>.Default,
                cancellationToken: cancellationToken);
        }

        internal static Task SubscribeDataAsync<T>(
            this PullSubRuntime runtime,
            string topic,
            PullSubQualityOfServiceLevel subscribeQos,
            CancellationToken cancellationToken = default)
        {
            return runtime.SubscribeDataAsync<T>(
                topic,
                JsonPayloadCodec<T>.Default,
                subscribeQos,
                cancellationToken);
        }

        private static T DecodeOrThrow<T>(
             string topic,
             byte[] payload,
             IPayloadCodec<T> codec)
        {
            if (!codec.TryDecode(payload.AsSpan(), out var value, out _, out var error))
                throw new PullSubPayloadDecodeException(topic, error);

            return value;
        }

        public static async Task<T> ReceiveQueueAsync<T>(
            this PullSubRuntime runtime,
            string topic,
            IPayloadCodec<T> codec,
            CancellationToken cancellationToken = default)
        {
            var message = await runtime.ReceiveQueueAsync(topic, cancellationToken)
                .ConfigureAwait(false);

            return DecodeOrThrow(topic, message.Payload, codec);
        }

        public static Task<QueueSubscription> SubscribeQueueAsync<T>(
            this PullSubRuntime runtime,
            ITopic<T> topic,
            Func<T, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            return runtime.SubscribeQueueAsync(
                topic,
                QueueOptions.Default,
                handler,
                cancellationToken);
        }

        public static Task<QueueSubscription> SubscribeQueueAsync<T>(
            this PullSubRuntime runtime,
            ITopic<T> topic,
            Func<T, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return runtime.SubscribeQueueAsync(topic, QueueOptions.Default, (message, _) => handler(message), cancellationToken);
        }

        public static Task<QueueSubscription> SubscribeQueueAsync<T>(
            this PullSubRuntime runtime,
            ITopic<T> topic,
            Action<T, CancellationToken> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return runtime.SubscribeQueueAsync(
                topic,
                QueueOptions.Default,
                (message, ct) =>
                {
                    handler(message, ct);
                    return default;
                },
                cancellationToken);
        }

        public static Task<QueueSubscription> SubscribeQueueAsync<T>(
            this PullSubRuntime runtime,
            ITopic<T> topic,
            Action<T> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return runtime.SubscribeQueueAsync(
                topic,
                QueueOptions.Default,
                (message, _) =>
                {
                    handler(message);
                    return default;
                },
                cancellationToken);
        }

        public static Task<QueueSubscription> SubscribeQueueAsync<T>(
            this PullSubRuntime runtime,
            string topic,
            QueueOptions options,
            IPayloadCodec<T> codec,
            Func<T, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            if (codec == null)
                throw new ArgumentNullException(nameof(codec));

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return runtime.SubscribeQueueAsync(
                topic,
                options,
                async (message, ct) =>
                {
                    var value = DecodeOrThrow(topic, message.Payload, codec);
                    await handler(value, ct).ConfigureAwait(false);
                },
                cancellationToken);
        }

        public static Task<QueueSubscription> SubscribeQueueAsync<T>(
            this PullSubRuntime runtime,
            string topic,
            QueueOptions options,
            IPayloadCodec<T> codec,
            Func<T, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return runtime.SubscribeQueueAsync(topic, options, codec, (message, _) => handler(message), cancellationToken);
        }

        public static Task<QueueSubscription> SubscribeQueueAsync<T>(
            this PullSubRuntime runtime,
            string topic,
            QueueOptions options,
            IPayloadCodec<T> codec,
            Action<T, CancellationToken> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return runtime.SubscribeQueueAsync(
                topic,
                options,
                codec,
                (message, ct) =>
                {
                    handler(message, ct);
                    return default;
                },
                cancellationToken);
        }

        public static Task<QueueSubscription> SubscribeQueueAsync<T>(
            this PullSubRuntime runtime,
            string topic,
            QueueOptions options,
            IPayloadCodec<T> codec,
            Action<T> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return runtime.SubscribeQueueAsync(
                topic,
                options,
                codec,
                (message, _) =>
                {
                    handler(message);
                    return default;
                },
                cancellationToken);
        }

        internal static Task SubscribeQueueLoopAsync(
            this PullSubRuntime runtime,
            string topic,
            QueueOptions options,
            Func<QueueMessage, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            ValidateRawHandlerArguments(runtime, topic, options, handler);
            return RunQueueHandlerLoopAsync(
                runtime,
                topic,
                options,
                subscribeQos: null,
                handler,
                cancellationToken,
                startedSignal: null);
        }

        internal static Task SubscribeQueueLoopAsync(
            this PullSubRuntime runtime,
            string topic,
            QueueOptions options,
            PullSubQualityOfServiceLevel subscribeQos,
            Func<QueueMessage, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            ValidateRawHandlerArguments(runtime, topic, options, handler);
            return RunQueueHandlerLoopAsync(
                runtime,
                topic,
                options,
                subscribeQos,
                handler,
                cancellationToken,
                startedSignal: null);
        }

        internal static async Task<QueueSubscription> SubscribeQueueAsync(
            this PullSubRuntime runtime,
            string topic,
            QueueOptions options,
            Func<QueueMessage, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            return await SubscribeQueueAsync(runtime, topic, options, subscribeQos: null, handler, cancellationToken);
        }

        internal static async Task<QueueSubscription> SubscribeQueueAsync(
            this PullSubRuntime runtime,
            string topic,
            QueueOptions options,
            PullSubQualityOfServiceLevel subscribeQos,
            Func<QueueMessage, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            return await SubscribeQueueAsync(runtime, topic, options, (PullSubQualityOfServiceLevel?)subscribeQos, handler, cancellationToken);
        }

        private static async Task<QueueSubscription> SubscribeQueueAsync(
            PullSubRuntime runtime,
            string topic,
            QueueOptions options,
            PullSubQualityOfServiceLevel? subscribeQos,
            Func<QueueMessage, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken)
        {
            ValidateRawHandlerArguments(runtime, topic, options, handler);

            var registrationCts = new CancellationTokenSource();
            var leaseToken = CreateLinkedOperationToken(cancellationToken, registrationCts.Token, out var registrationLinkedCts);
            var startedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var loopTask = RunQueueHandlerLoopAsync(runtime, topic, options, subscribeQos, handler, leaseToken, startedSignal);

            try
            {
                var completed = await Task.WhenAny(startedSignal.Task, loopTask);
                if (ReferenceEquals(completed, loopTask))
                {
                    await loopTask;
                    throw new InvalidOperationException("Handler loop exited before subscription completed.");
                }

                await startedSignal.Task;
            }
            catch
            {
                if (!registrationCts.IsCancellationRequested)
                    registrationCts.Cancel();

                registrationLinkedCts?.Dispose();
                registrationCts.Dispose();

                try
                {
                    await loopTask;
                }
                catch
                {
                }

                throw;
            }

            PullSubQueueHandlerDebugTracker.Register(runtime, topic, loopTask);
            return new QueueSubscription(topic, registrationCts, registrationLinkedCts, loopTask);
        }

        internal static Task<QueueSubscription> SubscribeQueueAsync(
            this PullSubRuntime runtime,
            string topic,
            QueueOptions options,
            Func<QueueMessage, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return runtime.SubscribeQueueAsync(topic, options, (message, _) => handler(message), cancellationToken);
        }

        internal static Task<QueueSubscription> SubscribeQueueAsync(
            this PullSubRuntime runtime,
            string topic,
            QueueOptions options,
            PullSubQualityOfServiceLevel subscribeQos,
            Func<QueueMessage, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return runtime.SubscribeQueueAsync(topic, options, subscribeQos, (message, _) => handler(message), cancellationToken);
        }

        internal static Task<QueueSubscription> SubscribeQueueAsync(
            this PullSubRuntime runtime,
            string topic,
            QueueOptions options,
            Action<QueueMessage, CancellationToken> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return runtime.SubscribeQueueAsync(
                topic,
                options,
                (message, ct) =>
                {
                    handler(message, ct);
                    return default;
                },
                cancellationToken);
        }

        internal static Task<QueueSubscription> SubscribeQueueAsync(
            this PullSubRuntime runtime,
            string topic,
            QueueOptions options,
            PullSubQualityOfServiceLevel subscribeQos,
            Action<QueueMessage, CancellationToken> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return runtime.SubscribeQueueAsync(
                topic,
                options,
                subscribeQos,
                (message, ct) =>
                {
                    handler(message, ct);
                    return default;
                },
                cancellationToken);
        }

        internal static Task<QueueSubscription> SubscribeQueueAsync(
            this PullSubRuntime runtime,
            string topic,
            QueueOptions options,
            Action<QueueMessage> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return runtime.SubscribeQueueAsync(
                topic,
                options,
                (message, _) =>
                {
                    handler(message);
                    return default;
                },
                cancellationToken);
        }

        internal static Task<QueueSubscription> SubscribeQueueAsync(
            this PullSubRuntime runtime,
            string topic,
            QueueOptions options,
            PullSubQualityOfServiceLevel subscribeQos,
            Action<QueueMessage> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return runtime.SubscribeQueueAsync(
                topic,
                options,
                subscribeQos,
                (message, _) =>
                {
                    handler(message);
                    return default;
                },
                cancellationToken);
        }

        private static CancellationToken CreateLinkedOperationToken(
            CancellationToken first,
            CancellationToken second,
            out CancellationTokenSource linkedCts)
        {
            if (!first.CanBeCanceled)
            {
                linkedCts = null;
                return second;
            }

            if (!second.CanBeCanceled)
            {
                linkedCts = null;
                return first;
            }

            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(first, second);
            return linkedCts.Token;
        }

        private static void ValidateRawHandlerArguments(
            PullSubRuntime runtime,
            string topic,
            QueueOptions options,
            Func<QueueMessage, CancellationToken, ValueTask> handler)
        {
            if (runtime == null)
                throw new ArgumentNullException(nameof(runtime));

            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("topic is required.", nameof(topic));

            if (options == null)
                throw new ArgumentNullException(nameof(options));

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
        }

        private static async Task RunQueueHandlerLoopAsync(
            PullSubRuntime runtime,
            string topic,
            QueueOptions options,
            PullSubQualityOfServiceLevel? subscribeQos,
            Func<QueueMessage, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken,
            TaskCompletionSource<bool> startedSignal)
        {
            try
            {
                if (subscribeQos.HasValue)
                    await runtime.SubscribeQueueAsync(topic, options, subscribeQos.Value, cancellationToken);
                else
                    await runtime.SubscribeQueueAsync(topic, options, cancellationToken: cancellationToken);

                startedSignal?.TrySetResult(true);

                while (true)
                {
                    var message = await runtime.ReceiveQueueAsync(topic, cancellationToken);
                    await handler(message, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || runtime.State == PullSubState.Disposed)
            {
                startedSignal?.TrySetCanceled();
            }
            catch (ObjectDisposedException) when (runtime.State == PullSubState.Disposed)
            {
                startedSignal?.TrySetCanceled();
            }
            catch (Exception ex)
            {
                startedSignal?.TrySetException(ex);
                throw;
            }
            finally
            {
                try
                {
                    using var bestEffortCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await runtime.UnsubscribeQueueAsync(topic, bestEffortCts.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        internal static Task SubscribeDataTopicAsync<T>(
            this PullSubRuntime runtime,
            ITopic<T> topic,
            CancellationToken cancellationToken = default)
        {
            return runtime.SubscribeDataAsync<T>(
                topic.TopicName,
                topic.Codec,
                cancellationToken: cancellationToken);
        }

        internal static Task SubscribeDataTopicAsync<T>(
            this PullSubRuntime runtime,
            ITopic<T> topic,
            PullSubQualityOfServiceLevel subscribeQos,
            CancellationToken cancellationToken = default)
        {
            return runtime.SubscribeDataAsync<T>(
                topic.TopicName,
                topic.Codec,
                subscribeQos,
                cancellationToken);
        }

        public static Task UnsubscribeDataAsync<T>(
            this PullSubRuntime runtime,
            ITopic<T> topic,
            CancellationToken cancellationToken = default)
        {
            return runtime.UnsubscribeDataAsync(
                topic.TopicName,
                cancellationToken);
        }

        public static Task PublishDataAsync<T>(
            this PullSubRuntime runtime,
            ITopic<T> topic,
            T value,
            PullSubQualityOfServiceLevel qos = 0,
            bool retain = false,
            CancellationToken cancellationToken = default)
        {
            return runtime.PublishDataAsync(
                topic.TopicName,
                value,
                topic.Codec,
                qos,
                retain,
                cancellationToken);
        }

        /// <summary>
        /// Creates an <see cref="IObserver{T}"/> that publishes each OnNext value through PullSubRuntime.
        ///
        /// The observer serializes publish calls so that only one publish runs at a time.
        /// After OnCompleted/OnError, subsequent OnNext calls are ignored.
        /// </summary>
        /// <remarks>
        /// If <paramref name="onError"/> is null, publish failures are reported to PullSubRuntime log callbacks.
        ///
        /// OnNext can be called concurrently from multiple threads; calls are serialized by the observer.
        /// The relative order of truly concurrent producer calls depends on scheduling.
        /// If OnNext input rate exceeds publish completion rate, pending OnNext work items can accumulate.
        ///
        /// Runtime disposal is treated as graceful termination for this observer.
        /// ObjectDisposedException and runtime-dispose OperationCanceledException do not trigger <paramref name="onError"/>.
        /// </remarks>
        /// <param name="runtime">Target runtime used for publishing.</param>
        /// <param name="topic">Typed topic used for encoding and topic name binding.</param>
        /// <param name="qos">Publish QoS.</param>
        /// <param name="retain">MQTT retain flag.</param>
        /// <param name="onError">Optional callback for non-dispose publish failures.</param>
        /// <returns>An observer that publishes incoming values.</returns>
        public static IObserver<T> ToPublisher<T>(
            this PullSubRuntime runtime,
            ITopic<T> topic,
            PullSubQualityOfServiceLevel qos = 0,
            bool retain = false,
            Action<Exception> onError = null)
        {
            if (runtime == null)
                throw new ArgumentNullException(nameof(runtime));

            if (topic == null)
                throw new ArgumentNullException(nameof(topic));

            return new PublishObserver<T>(runtime, topic, qos, retain, onError);
        }

        public static DataSubscription<T> GetDataHandle<T>(
            this PullSubRuntime runtime,
            ITopic<T> topic)
        {
            return runtime.GetDataHandle<T>(topic.TopicName);
        }

        public static Task<T> WaitForFirstDataAsync<T>(
            this PullSubRuntime runtime,
            ITopic<T> topic,
            CancellationToken cancellationToken = default)
        {
            return runtime.WaitForFirstDataAsync<T>(
                topic.TopicName,
                cancellationToken);
        }

        public static Task<T> ReceiveQueueAsync<T>(
            this PullSubRuntime runtime,
            ITopic<T> topic,
            CancellationToken cancellationToken = default)
        {
            return runtime.ReceiveQueueAsync<T>(
                topic.TopicName,
                topic.Codec,
                cancellationToken);
        }

        public static Task<QueueSubscription> SubscribeQueueAsync<T>(
            this PullSubRuntime runtime,
            ITopic<T> topic,
            QueueOptions options,
            Func<T, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            return runtime.SubscribeQueueAsync<T>(
                topic.TopicName,
                options,
                topic.Codec,
                handler,
                cancellationToken);
        }

        public static Task<QueueSubscription> SubscribeQueueAsync<T>(
            this PullSubRuntime runtime,
            ITopic<T> topic,
            QueueOptions options,
            Func<T, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            return runtime.SubscribeQueueAsync<T>(
                topic.TopicName,
                options,
                topic.Codec,
                handler,
                cancellationToken);
        }

        public static Task<QueueSubscription> SubscribeQueueAsync<T>(
            this PullSubRuntime runtime,
            ITopic<T> topic,
            QueueOptions options,
            Action<T, CancellationToken> handler,
            CancellationToken cancellationToken = default)
        {
            return runtime.SubscribeQueueAsync<T>(
                topic.TopicName,
                options,
                topic.Codec,
                handler,
                cancellationToken);
        }

        public static Task<QueueSubscription> SubscribeQueueAsync<T>(
            this PullSubRuntime runtime,
            ITopic<T> topic,
            QueueOptions options,
            Action<T> handler,
            CancellationToken cancellationToken = default)
        {
            return runtime.SubscribeQueueAsync<T>(
                topic.TopicName,
                options,
                topic.Codec,
                handler,
                cancellationToken);
        }

        internal static T GetData<T>(
            this PullSubRuntime runtime,
            ITopic<T> topic,
            T defaultValue = default)
        {
            return runtime.GetData<T>(topic.TopicName, defaultValue);
        }
    }
}
