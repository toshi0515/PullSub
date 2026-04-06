using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    public static partial class PullSubRuntimeRequestExtensions
    {
        private static PullSubRequestContext CreateRequestContext<TRequest>(PullSubRequestEnvelope<TRequest> requestEnvelope)
        {
            return new PullSubRequestContext(
                requestEnvelope.CorrelationId,
                requestEnvelope.ReplyTo,
                requestEnvelope.SentUtc,
                requestEnvelope.DeadlineUtc);
        }

        private static PullSubRequestEnvelopeCodec<TRequest> ResolveRequestEnvelopeCodec<TRequest, TResponse>(
            IPullSubRequestTopic<TRequest, TResponse> topic)
        {
            if (topic is IPullSubRequestTopicInternal<TRequest, TResponse> internalTopic)
                return internalTopic.RequestEnvelopeCodec;

            return new PullSubRequestEnvelopeCodec<TRequest>(topic.RequestCodec);
        }

        private static PullSubResponseEnvelopeCodec<TResponse> ResolveResponseEnvelopeCodec<TRequest, TResponse>(
            IPullSubRequestTopic<TRequest, TResponse> topic)
        {
            if (topic is IPullSubRequestTopicInternal<TRequest, TResponse> internalTopic)
                return internalTopic.ResponseEnvelopeCodec;

            return new PullSubResponseEnvelopeCodec<TResponse>(topic.ResponseCodec);
        }

        private static IPullSubTopic<PullSubRequestEnvelope<TRequest>> ResolveRequestEnvelopeTopic<TRequest, TResponse>(
            IPullSubRequestTopic<TRequest, TResponse> topic,
            PullSubRequestEnvelopeCodec<TRequest> requestEnvelopeCodec)
        {
            if (topic is IPullSubRequestTopicInternal<TRequest, TResponse> internalTopic)
                return internalTopic.RequestEnvelopeTopic;

            return PullSubTopic.Create(topic.RequestTopicName, requestEnvelopeCodec);
        }

        private static byte[] EncodePayload<T>(IPayloadCodec<T> codec, DateTime timestampUtc, T value)
        {
            var writer = new ArrayBufferWriter<byte>(256);
            codec.Encode(timestampUtc, value, writer);

            return writer.WrittenCount == 0
                ? Array.Empty<byte>()
                : writer.WrittenMemory.ToArray();
        }

        private static Task PublishReplyAsync<TResponse>(
            PullSubRuntime runtime,
            string replyTo,
            PullSubResponseEnvelope<TResponse> responseEnvelope,
            PullSubResponseEnvelopeCodec<TResponse> responseEnvelopeCodec,
            CancellationToken cancellationToken)
        {
            try
            {
                PullSubSubscriptionRegistry.ValidateExactMatchTopic(replyTo);
            }
            catch (PullSubWildcardTopicNotSupportedException ex)
            {
                runtime.MarkInvalidReplyToDrop();
                runtime.LogException(ex);
                return Task.CompletedTask;
            }
            catch (ArgumentException ex)
            {
                runtime.MarkInvalidReplyToDrop();
                runtime.LogException(ex);
                return Task.CompletedTask;
            }

            var payload = EncodePayload(responseEnvelopeCodec, DateTime.UtcNow, responseEnvelope);

            return runtime.PublishRawAsync(
                replyTo,
                payload,
                PullSubQualityOfServiceLevel.AtLeastOnce,
                retain: false,
                cancellationToken);
        }

        private static string FormatRemoteErrorMessage(Exception exception)
        {
            if (exception == null)
                return "Unknown remote error.";

            var typeName = exception.GetType().Name;
            return string.IsNullOrWhiteSpace(exception.Message)
                ? typeName
                : $"{typeName}: {exception.Message}";
        }

        private static Func<TRequest, PullSubRequestContext, PullSubReplySender<TResponse>, CancellationToken, ValueTask>
            WrapValueResponderHandler<TRequest, TResponse>(
                Func<TRequest, PullSubRequestContext, CancellationToken, ValueTask<TResponse>> handler)
        {
            return async (request, requestContext, sender, ct) =>
            {
                TResponse response;
                try
                {
                    response = await handler(request, requestContext, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (requestContext.IsExpired(DateTime.UtcNow))
                        return;

                    await sender.SendErrorAsync(FormatRemoteErrorMessage(ex), ct).ConfigureAwait(false);
                    return;
                }

                if (requestContext.IsExpired(DateTime.UtcNow))
                    return;

                await sender.SendAsync(response, ct).ConfigureAwait(false);
            };
        }

        private static Func<TRequest, PullSubRequestContext, PullSubReplySender<TResponse>, CancellationToken, ValueTask>
            WrapValueResponderHandler<TRequest, TResponse>(
                Func<TRequest, CancellationToken, ValueTask<TResponse>> handler)
        {
            return WrapValueResponderHandler<TRequest, TResponse>(
                (request, _, ct) => handler(request, ct));
        }
    }
}
