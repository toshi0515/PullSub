using System;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet.Protocol;

namespace UnityMqtt.V2.Core
{
    public static class MqttV2ClientRuntimeExtensions
    {
        public static Task RegisterRawHandlerAsync(
            this MqttV2ClientRuntime runtime,
            string topic,
            MqttV2RawSubscriptionOptions options,
            Func<MqttV2RawMessage, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
        {
            ValidateRawHandlerArguments(runtime, topic, options, handler);
            return RunRawHandlerLoopAsync(
                runtime,
                topic,
                options,
                subscribeQos: null,
                handler,
                cancellationToken,
                startedSignal: null);
        }

        public static Task RegisterRawHandlerAsync(
            this MqttV2ClientRuntime runtime,
            string topic,
            MqttV2RawSubscriptionOptions options,
            MqttQualityOfServiceLevel subscribeQos,
            Func<MqttV2RawMessage, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
        {
            ValidateRawHandlerArguments(runtime, topic, options, handler);
            return RunRawHandlerLoopAsync(
                runtime,
                topic,
                options,
                subscribeQos,
                handler,
                cancellationToken,
                startedSignal: null);
        }

        public static async Task<MqttV2RawHandlerRegistration> RegisterRawHandlerLeaseAsync(
            this MqttV2ClientRuntime runtime,
            string topic,
            MqttV2RawSubscriptionOptions options,
            Func<MqttV2RawMessage, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
        {
            ValidateRawHandlerArguments(runtime, topic, options, handler);
            return await RegisterRawHandlerLeaseAsync(runtime, topic, options, subscribeQos: null, handler, cancellationToken);
        }

        public static async Task<MqttV2RawHandlerRegistration> RegisterRawHandlerLeaseAsync(
            this MqttV2ClientRuntime runtime,
            string topic,
            MqttV2RawSubscriptionOptions options,
            MqttQualityOfServiceLevel subscribeQos,
            Func<MqttV2RawMessage, CancellationToken, Task> handler,
            CancellationToken cancellationToken = default)
        {
            ValidateRawHandlerArguments(runtime, topic, options, handler);

            return await RegisterRawHandlerLeaseAsync(runtime, topic, options, (MqttQualityOfServiceLevel?)subscribeQos, handler, cancellationToken);
        }

        private static async Task<MqttV2RawHandlerRegistration> RegisterRawHandlerLeaseAsync(
            MqttV2ClientRuntime runtime,
            string topic,
            MqttV2RawSubscriptionOptions options,
            MqttQualityOfServiceLevel? subscribeQos,
            Func<MqttV2RawMessage, CancellationToken, Task> handler,
            CancellationToken cancellationToken)
        {
            ValidateRawHandlerArguments(runtime, topic, options, handler);

            var registrationCts = new CancellationTokenSource();
            var leaseToken = CreateLinkedOperationToken(cancellationToken, registrationCts.Token, out var registrationLinkedCts);
            var startedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var loopTask = RunRawHandlerLoopAsync(runtime, topic, options, subscribeQos, handler, leaseToken, startedSignal);

            try
            {
                var completed = await Task.WhenAny(startedSignal.Task, loopTask);
                if (ReferenceEquals(completed, loopTask))
                {
                    await loopTask;
                    throw new InvalidOperationException("Raw handler loop exited before subscription completed.");
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

            return new MqttV2RawHandlerRegistration(registrationCts, registrationLinkedCts, loopTask);
        }

        public static Task<MqttV2RawHandlerRegistration> RegisterRawHandlerLeaseAsync(
            this MqttV2ClientRuntime runtime,
            string topic,
            MqttV2RawSubscriptionOptions options,
            Action<MqttV2RawMessage> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return runtime.RegisterRawHandlerLeaseAsync(
                topic,
                options,
                (message, _) =>
                {
                    handler(message);
                    return Task.CompletedTask;
                },
                cancellationToken);
        }

        public static Task<MqttV2RawHandlerRegistration> RegisterRawHandlerLeaseAsync(
            this MqttV2ClientRuntime runtime,
            string topic,
            MqttV2RawSubscriptionOptions options,
            MqttQualityOfServiceLevel subscribeQos,
            Action<MqttV2RawMessage> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return runtime.RegisterRawHandlerLeaseAsync(
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
            MqttV2ClientRuntime runtime,
            string topic,
            MqttV2RawSubscriptionOptions options,
            Func<MqttV2RawMessage, CancellationToken, Task> handler)
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

        private static async Task RunRawHandlerLoopAsync(
            MqttV2ClientRuntime runtime,
            string topic,
            MqttV2RawSubscriptionOptions options,
            MqttQualityOfServiceLevel? subscribeQos,
            Func<MqttV2RawMessage, CancellationToken, Task> handler,
            CancellationToken cancellationToken,
            TaskCompletionSource<bool> startedSignal)
        {
            try
            {
                if (subscribeQos.HasValue)
                    await runtime.SubscribeRawAsync(topic, options, subscribeQos.Value, cancellationToken);
                else
                    await runtime.SubscribeRawAsync(topic, options, cancellationToken);

                startedSignal?.TrySetResult(true);

                while (true)
                {
                    var message = await runtime.ReceiveRawAsync(topic, cancellationToken);
                    await handler(message, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || runtime.State == MqttV2ClientState.Disposed)
            {
                startedSignal?.TrySetCanceled();
            }
            catch (ObjectDisposedException) when (runtime.State == MqttV2ClientState.Disposed)
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
                    await runtime.UnsubscribeRawAsync(topic, bestEffortCts.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }
}