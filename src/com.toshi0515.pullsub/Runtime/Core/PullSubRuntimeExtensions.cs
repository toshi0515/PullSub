using System;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    public static class PullSubRuntimeExtensions
    {
        public static PullSubContext CreateContext(this PullSubRuntime runtime)
        {
            if (runtime == null)
                throw new ArgumentNullException(nameof(runtime));

            var context = new PullSubContext(runtime);
            PullSubContextDebugTracker.Register(runtime, context);
            return context;
        }

        public static async Task<PullSubSubscription<T>> SubscribeAsync<T>(
            this PullSubRuntime runtime,
            IPullSubTopic<T> topic,
            PullSubQualityOfServiceLevel subscribeQos = PullSubQualityOfServiceLevel.AtLeastOnce,
            CancellationToken cancellationToken = default)
        {
            if (runtime == null)
                throw new ArgumentNullException(nameof(runtime));

            if (topic == null)
                throw new ArgumentNullException(nameof(topic));

            await runtime.SubscribeDataAsync(topic, subscribeQos, cancellationToken).ConfigureAwait(false);
            var handle = runtime.GetDataHandle(topic);
            return new PullSubSubscription<T>(runtime, topic, handle);
        }

        public static Task SubscribeDataAsync<T>(
            this PullSubRuntime runtime,
            string topic,
            CancellationToken cancellationToken = default)
        {
            return runtime.SubscribeDataAsync<T>(
                topic,
                PullSubJsonPayloadCodec<T>.Default,
                cancellationToken: cancellationToken);
        }

        public static Task SubscribeDataAsync<T>(
            this PullSubRuntime runtime,
            string topic,
            PullSubQualityOfServiceLevel subscribeQos,
            CancellationToken cancellationToken = default)
        {
            return runtime.SubscribeDataAsync<T>(
                topic,
                PullSubJsonPayloadCodec<T>.Default,
                subscribeQos,
                cancellationToken);
        }

        private static T DecodeOrThrow<T>(
             string topic,
             byte[] payload,
             IPayloadCodec<T> codec)
        {
            if (!codec.TryDecode(payload.AsSpan(), out var value, out _, out var error))
                throw new InvalidOperationException(
                    $"Payload decode failed. topic={topic} error={error}");

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

        public static Task<PullSubQueueHandlerRegistration> RegisterHandlerLeaseAsync<T>(
            this PullSubRuntime runtime,
            IPullSubTopic<T> topic,
            Func<T, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
        {
            return runtime.RegisterHandlerLeaseAsync(
                topic,
                PullSubQueueOptions.Default,
                handler,
                cancellationToken);
        }

        public static Task<PullSubQueueHandlerRegistration> RegisterHandlerLeaseAsync<T>(
            this PullSubRuntime runtime,
            string topic,
            PullSubQueueOptions options,
            IPayloadCodec<T> codec,
            Func<T, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
        {
            if (codec == null)
                throw new ArgumentNullException(nameof(codec));

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return runtime.RegisterHandlerLeaseAsync(
                topic,
                options,
                async (message, ct) =>
                {
                    var value = DecodeOrThrow(topic, message.Payload, codec);
                    await handler(value, ct);
                },
                cancellationToken);
        }

        public static Task RegisterHandlerAsync(
            this PullSubRuntime runtime,
            string topic,
            PullSubQueueOptions options,
            Func<PullSubQueueMessage, CancellationToken, Task> handler,
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

        public static Task RegisterHandlerAsync(
            this PullSubRuntime runtime,
            string topic,
            PullSubQueueOptions options,
            PullSubQualityOfServiceLevel subscribeQos,
            Func<PullSubQueueMessage, CancellationToken, Task> handler,
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

        public static async Task<PullSubQueueHandlerRegistration> RegisterHandlerLeaseAsync(
            this PullSubRuntime runtime,
            string topic,
            PullSubQueueOptions options,
            Func<PullSubQueueMessage, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
        {
            return await RegisterHandlerLeaseAsync(runtime, topic, options, subscribeQos: null, handler, cancellationToken);
        }

        public static async Task<PullSubQueueHandlerRegistration> RegisterHandlerLeaseAsync(
            this PullSubRuntime runtime,
            string topic,
            PullSubQueueOptions options,
            PullSubQualityOfServiceLevel subscribeQos,
            Func<PullSubQueueMessage, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
        {
            return await RegisterHandlerLeaseAsync(runtime, topic, options, (PullSubQualityOfServiceLevel?)subscribeQos, handler, cancellationToken);
        }

        private static async Task<PullSubQueueHandlerRegistration> RegisterHandlerLeaseAsync(
            PullSubRuntime runtime,
            string topic,
            PullSubQueueOptions options,
            PullSubQualityOfServiceLevel? subscribeQos,
            Func<PullSubQueueMessage, CancellationToken, Task> handler,
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
            return new PullSubQueueHandlerRegistration(topic, registrationCts, registrationLinkedCts, loopTask);
        }

        public static Task<PullSubQueueHandlerRegistration> RegisterHandlerLeaseAsync(
            this PullSubRuntime runtime,
            string topic,
            PullSubQueueOptions options,
            Action<PullSubQueueMessage> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return runtime.RegisterHandlerLeaseAsync(
                topic,
                options,
                (message, _) =>
                {
                    handler(message);
                    return Task.CompletedTask;
                },
                cancellationToken);
        }

        public static Task<PullSubQueueHandlerRegistration> RegisterHandlerLeaseAsync(
            this PullSubRuntime runtime,
            string topic,
            PullSubQueueOptions options,
            PullSubQualityOfServiceLevel subscribeQos,
            Action<PullSubQueueMessage> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return runtime.RegisterHandlerLeaseAsync(
                topic,
                options,
                subscribeQos,
                (message, _) =>
                {
                    handler(message);
                    return Task.CompletedTask;
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
            PullSubQueueOptions options,
            Func<PullSubQueueMessage, CancellationToken, Task> handler)
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
            PullSubQueueOptions options,
            PullSubQualityOfServiceLevel? subscribeQos,
            Func<PullSubQueueMessage, CancellationToken, Task> handler,
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

        public static Task SubscribeDataAsync<T>(
            this PullSubRuntime runtime,
            IPullSubTopic<T> topic,
            CancellationToken cancellationToken = default)
        {
            return runtime.SubscribeDataAsync<T>(
                topic.TopicName,
                topic.Codec,
                cancellationToken: cancellationToken);
        }

        public static Task SubscribeDataAsync<T>(
            this PullSubRuntime runtime,
            IPullSubTopic<T> topic,
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
            IPullSubTopic<T> topic,
            CancellationToken cancellationToken = default)
        {
            return runtime.UnsubscribeDataAsync(
                topic.TopicName,
                cancellationToken);
        }

        public static Task PublishDataAsync<T>(
            this PullSubRuntime runtime,
            IPullSubTopic<T> topic,
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

        public static PullSubDataHandle<T> GetDataHandle<T>(
            this PullSubRuntime runtime,
            IPullSubTopic<T> topic)
        {
            return runtime.GetDataHandle<T>(topic.TopicName);
        }

        public static Task<T> WaitForFirstDataAsync<T>(
            this PullSubRuntime runtime,
            IPullSubTopic<T> topic,
            CancellationToken cancellationToken = default)
        {
            return runtime.WaitForFirstDataAsync<T>(
                topic.TopicName,
                cancellationToken);
        }

        public static Task<T> ReceiveQueueAsync<T>(
            this PullSubRuntime runtime,
            IPullSubTopic<T> topic,
            CancellationToken cancellationToken = default)
        {
            return runtime.ReceiveQueueAsync<T>(
                topic.TopicName,
                topic.Codec,
                cancellationToken);
        }

        public static Task<PullSubQueueHandlerRegistration> RegisterHandlerLeaseAsync<T>(
            this PullSubRuntime runtime,
            IPullSubTopic<T> topic,
            PullSubQueueOptions options,
            Func<T, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
        {
            return runtime.RegisterHandlerLeaseAsync<T>(
                topic.TopicName,
                options,
                topic.Codec,
                handler,
                cancellationToken);
        }

        public static T GetData<T>(
            this PullSubRuntime runtime,
            IPullSubTopic<T> topic,
            T defaultValue = default)
        {
            return runtime.GetData<T>(topic.TopicName, defaultValue);
        }
    }
}
