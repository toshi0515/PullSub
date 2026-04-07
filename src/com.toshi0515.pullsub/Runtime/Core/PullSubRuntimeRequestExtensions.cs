using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    public static partial class PullSubRuntimeRequestExtensions
    {
        private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(5);

        public static Task<TResponse> RequestAsync<TRequest, TResponse>(
            this PullSubRuntime runtime,
            IRequestTopic<TRequest, TResponse> topic,
            TRequest request,
            CancellationToken cancellationToken = default)
        {
            return RequestAsync(
                runtime,
                topic,
                request,
                DefaultRequestTimeout,
                PullSubQualityOfServiceLevel.AtLeastOnce,
                cancellationToken);
        }

        public static async Task<TResponse> RequestAsync<TRequest, TResponse>(
            this PullSubRuntime runtime,
            IRequestTopic<TRequest, TResponse> topic,
            TRequest request,
            TimeSpan timeout,
            PullSubQualityOfServiceLevel publishQos = PullSubQualityOfServiceLevel.AtLeastOnce,
            CancellationToken cancellationToken = default)
        {
            if (runtime == null)
                throw new ArgumentNullException(nameof(runtime));

            if (topic == null)
                throw new ArgumentNullException(nameof(topic));

            if (timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout), "timeout must be greater than zero.");

            runtime.EnsureRequestApiReady();

            var correlationId = Guid.NewGuid().ToString("N");
            var sentUtc = DateTime.UtcNow;
            var deadlineUtc = sentUtc + timeout;

            try
            {
                await runtime.EnsureReplyInboxSubscriptionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                runtime.MarkReplyInboxSetupFailure();
                throw new PullSubRequestException(
                    PullSubRequestFailureKind.SetupFailed,
                    $"Request setup failed. topic={topic.RequestTopicName}",
                    correlationId,
                    ex);
            }

            var pending = runtime.RegisterPendingRequest(correlationId, deadlineUtc, cancellationToken);
            var requestEnvelope = new RequestEnvelope<TRequest>
            {
                CorrelationId = correlationId,
                ReplyTo = runtime.ReplyInboxTopic,
                SentUtc = sentUtc,
                DeadlineUtc = deadlineUtc,
                Request = request,
            };

            var requestEnvelopeCodec = ResolveRequestEnvelopeCodec(topic);
            var requestPayload = EncodePayload(requestEnvelopeCodec, sentUtc, requestEnvelope);

            try
            {
                await runtime.PublishRawAsync(
                        topic.RequestTopicName,
                        requestPayload,
                        publishQos,
                        retain: false,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                runtime.FailRequestPublish(correlationId, ex);
                // Intentionally continue to await pending.ResponseTask.
                // Pending store decides the final failure kind under publish/disconnect races.
            }

            var responseEnvelopePayload = await pending.ResponseTask.ConfigureAwait(false);
            var responseEnvelopeCodec = ResolveResponseEnvelopeCodec(topic);
            if (!responseEnvelopeCodec.TryDecode(responseEnvelopePayload, out var responseEnvelope, out _, out var responseDecodeError))
            {
                throw new PullSubRequestException(
                    PullSubRequestFailureKind.PayloadDecodeFailed,
                    $"Response envelope decode failed. correlationId={correlationId} error={responseDecodeError}",
                    correlationId);
            }

            if (responseEnvelope.Status == ResponseEnvelopeStatus.RemoteError)
            {
                throw new PullSubRequestException(
                    PullSubRequestFailureKind.RemoteError,
                    $"Remote error response. correlationId={correlationId} error={responseEnvelope.ErrorMessage}",
                    correlationId);
            }

            return responseEnvelope.Response;
        }

        public static Task<TResponse> RequestAsync<TRequest, TResponse>(
            this CompositeSubscription context,
            IRequestTopic<TRequest, TResponse> topic,
            TRequest request,
            CancellationToken cancellationToken = default)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            return context.Runtime.RequestAsync(topic, request, cancellationToken);
        }

        public static Task<TResponse> RequestAsync<TRequest, TResponse>(
            this CompositeSubscription context,
            IRequestTopic<TRequest, TResponse> topic,
            TRequest request,
            TimeSpan timeout,
            PullSubQualityOfServiceLevel publishQos = PullSubQualityOfServiceLevel.AtLeastOnce,
            CancellationToken cancellationToken = default)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            return context.Runtime.RequestAsync(topic, request, timeout, publishQos, cancellationToken);
        }

        public static Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
            this PullSubRuntime runtime,
            IRequestTopic<TRequest, TResponse> topic,
            Func<TRequest, CancellationToken, ValueTask<TResponse>> handler,
            CancellationToken cancellationToken = default)
        {
            return RespondAsync(
                runtime,
                topic,
                QueueOptions.Default,
                handler,
                cancellationToken);
        }

        public static Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
            this PullSubRuntime runtime,
            IRequestTopic<TRequest, TResponse> topic,
            Func<TRequest, RequestContext, CancellationToken, ValueTask<TResponse>> handler,
            CancellationToken cancellationToken = default)
        {
            return RespondAsync(runtime, topic, QueueOptions.Default, handler, cancellationToken);
        }

        public static Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
            this PullSubRuntime runtime,
            IRequestTopic<TRequest, TResponse> topic,
            QueueOptions options,
            Func<TRequest, RequestContext, CancellationToken, ValueTask<TResponse>> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return RespondAsync(
                runtime,
                topic,
                options,
                WrapValueResponderHandler(handler),
                cancellationToken);
        }

        public static Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
            this PullSubRuntime runtime,
            IRequestTopic<TRequest, TResponse> topic,
            QueueOptions options,
            Func<TRequest, CancellationToken, ValueTask<TResponse>> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return RespondAsync(
                runtime,
                topic,
                options,
                WrapValueResponderHandler(handler),
                cancellationToken);
        }

        public static Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
            this PullSubRuntime runtime,
            IRequestTopic<TRequest, TResponse> topic,
            Func<TRequest, ReplySender<TResponse>, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            return RespondAsync(runtime, topic, QueueOptions.Default, handler, cancellationToken);
        }

        public static Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
            this PullSubRuntime runtime,
            IRequestTopic<TRequest, TResponse> topic,
            QueueOptions options,
            Func<TRequest, ReplySender<TResponse>, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            if (runtime == null)
                throw new ArgumentNullException(nameof(runtime));

            if (topic == null)
                throw new ArgumentNullException(nameof(topic));

            if (options == null)
                throw new ArgumentNullException(nameof(options));

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            // Sender-based overload is intentionally low-level: handler/send exceptions propagate
            // to Queue handler infrastructure and fault the subscription completion.

            return RespondAsync(
                runtime,
                topic,
                options,
                (request, requestContext, sender, ct) => handler(request, sender, ct),
                cancellationToken);
        }

        public static Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
            this PullSubRuntime runtime,
            IRequestTopic<TRequest, TResponse> topic,
            Func<TRequest, RequestContext, ReplySender<TResponse>, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            return RespondAsync(runtime, topic, QueueOptions.Default, handler, cancellationToken);
        }

        public static Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
            this PullSubRuntime runtime,
            IRequestTopic<TRequest, TResponse> topic,
            QueueOptions options,
            Func<TRequest, RequestContext, ReplySender<TResponse>, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            if (runtime == null)
                throw new ArgumentNullException(nameof(runtime));

            if (topic == null)
                throw new ArgumentNullException(nameof(topic));

            if (options == null)
                throw new ArgumentNullException(nameof(options));

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            var requestEnvelopeCodec = ResolveRequestEnvelopeCodec(topic);
            var responseEnvelopeCodec = ResolveResponseEnvelopeCodec(topic);
            var requestEnvelopeTopic = ResolveRawRequestEnvelopeTopic(topic);

            return runtime.SubscribeQueueAsync(
                requestEnvelopeTopic,
                options,
                async (requestEnvelopePayload, ct) =>
                {
                    if (!TryDecodeRequestEnvelope(runtime, requestEnvelopeCodec, requestEnvelopePayload, out var requestEnvelope))
                        return;

                    var requestContext = CreateRequestContext(requestEnvelope);
                    var sender = new ReplySender<TResponse>(
                        requestEnvelope.CorrelationId,
                        (responseEnvelope, sendCt) =>
                            PublishReplyAsync(runtime, requestEnvelope.ReplyTo, responseEnvelope, responseEnvelopeCodec, sendCt));

                    await handler(requestEnvelope.Request, requestContext, sender, ct).ConfigureAwait(false);
                },
                cancellationToken);
        }

        public static Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
            this CompositeSubscription context,
            IRequestTopic<TRequest, TResponse> topic,
            Func<TRequest, CancellationToken, ValueTask<TResponse>> handler,
            CancellationToken cancellationToken = default)
        {
            return RespondAsync(context, topic, QueueOptions.Default, handler, cancellationToken);
        }

        public static Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
            this CompositeSubscription context,
            IRequestTopic<TRequest, TResponse> topic,
            QueueOptions options,
            Func<TRequest, CancellationToken, ValueTask<TResponse>> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return RespondAsync(
                context,
                topic,
                options,
                WrapValueResponderHandler(handler),
                cancellationToken);
        }

        public static Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
            this CompositeSubscription context,
            IRequestTopic<TRequest, TResponse> topic,
            Func<TRequest, RequestContext, CancellationToken, ValueTask<TResponse>> handler,
            CancellationToken cancellationToken = default)
        {
            return RespondAsync(context, topic, QueueOptions.Default, handler, cancellationToken);
        }

        public static Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
            this CompositeSubscription context,
            IRequestTopic<TRequest, TResponse> topic,
            QueueOptions options,
            Func<TRequest, RequestContext, CancellationToken, ValueTask<TResponse>> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return RespondAsync(
                context,
                topic,
                options,
                WrapValueResponderHandler(handler),
                cancellationToken);
        }

        public static Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
            this CompositeSubscription context,
            IRequestTopic<TRequest, TResponse> topic,
            Func<TRequest, ReplySender<TResponse>, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            return RespondAsync(context, topic, QueueOptions.Default, handler, cancellationToken);
        }

        public static Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
            this CompositeSubscription context,
            IRequestTopic<TRequest, TResponse> topic,
            QueueOptions options,
            Func<TRequest, ReplySender<TResponse>, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            if (topic == null)
                throw new ArgumentNullException(nameof(topic));

            if (options == null)
                throw new ArgumentNullException(nameof(options));

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            // Sender-based overload is intentionally low-level: handler/send exceptions propagate
            // to Queue handler infrastructure and fault the subscription completion.

            return RespondAsync(
                context,
                topic,
                options,
                (request, requestContext, sender, ct) => handler(request, sender, ct),
                cancellationToken);
        }

        public static Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
            this CompositeSubscription context,
            IRequestTopic<TRequest, TResponse> topic,
            Func<TRequest, RequestContext, ReplySender<TResponse>, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            return RespondAsync(context, topic, QueueOptions.Default, handler, cancellationToken);
        }

        public static Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
            this CompositeSubscription context,
            IRequestTopic<TRequest, TResponse> topic,
            QueueOptions options,
            Func<TRequest, RequestContext, ReplySender<TResponse>, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            if (topic == null)
                throw new ArgumentNullException(nameof(topic));

            if (options == null)
                throw new ArgumentNullException(nameof(options));

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            var requestEnvelopeCodec = ResolveRequestEnvelopeCodec(topic);
            var responseEnvelopeCodec = ResolveResponseEnvelopeCodec(topic);
            var requestEnvelopeTopic = ResolveRawRequestEnvelopeTopic(topic);

            return context.SubscribeQueueAsync(
                requestEnvelopeTopic,
                options,
                async (requestEnvelopePayload, ct) =>
                {
                    if (!TryDecodeRequestEnvelope(context.Runtime, requestEnvelopeCodec, requestEnvelopePayload, out var requestEnvelope))
                        return;

                    var requestContext = CreateRequestContext(requestEnvelope);
                    var sender = new ReplySender<TResponse>(
                        requestEnvelope.CorrelationId,
                        (responseEnvelope, sendCt) =>
                            PublishReplyAsync(context.Runtime, requestEnvelope.ReplyTo, responseEnvelope, responseEnvelopeCodec, sendCt));

                    await handler(requestEnvelope.Request, requestContext, sender, ct).ConfigureAwait(false);
                },
                cancellationToken);
        }

    }
}
