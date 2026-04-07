using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    public static partial class PullSubRuntimeRequestExtensions
    {
        private static RequestContext CreateRequestContext<TRequest>(RequestEnvelope<TRequest> requestEnvelope)
        {
            return new RequestContext(
                requestEnvelope.CorrelationId,
                requestEnvelope.ReplyTo,
                requestEnvelope.SentUtc,
                requestEnvelope.DeadlineUtc);
        }

        private static RequestEnvelopeCodec<TRequest> ResolveRequestEnvelopeCodec<TRequest, TResponse>(
            IRequestTopic<TRequest, TResponse> topic)
        {
            if (topic is IRequestTopicInternal<TRequest, TResponse> internalTopic)
                return internalTopic.RequestEnvelopeCodec;

            return new RequestEnvelopeCodec<TRequest>(topic.RequestCodec);
        }

        private static ResponseEnvelopeCodec<TResponse> ResolveResponseEnvelopeCodec<TRequest, TResponse>(
            IRequestTopic<TRequest, TResponse> topic)
        {
            if (topic is IRequestTopicInternal<TRequest, TResponse> internalTopic)
                return internalTopic.ResponseEnvelopeCodec;

            return new ResponseEnvelopeCodec<TResponse>(topic.ResponseCodec);
        }

        private static ITopic<byte[]> ResolveRawRequestEnvelopeTopic<TRequest, TResponse>(
            IRequestTopic<TRequest, TResponse> topic)
        {
            return PullSubTopic.Create(topic.RequestTopicName, RawBinaryPayloadCodec.Default);
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
            ResponseEnvelope<TResponse> responseEnvelope,
            ResponseEnvelopeCodec<TResponse> responseEnvelopeCodec,
            CancellationToken cancellationToken)
        {
            if (!IsAllowedReplyTo(runtime, replyTo))
            {
                runtime.MarkInvalidReplyToDrop(replyTo);
                return Task.CompletedTask;
            }

            if (string.IsNullOrWhiteSpace(responseEnvelope.CorrelationId)
                || responseEnvelope.CorrelationId.Length > runtime.RuntimeOptions.MaxCorrelationIdLength)
            {
                runtime.LogWarning(
                    $"[PullSubRuntime] Dropped reply with invalid correlationId length. " +
                    $"max={runtime.RuntimeOptions.MaxCorrelationIdLength}.");
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

        private static bool TryDecodeRequestEnvelope<TRequest>(
            PullSubRuntime runtime,
            RequestEnvelopeCodec<TRequest> requestEnvelopeCodec,
            byte[] requestEnvelopePayload,
            out RequestEnvelope<TRequest> requestEnvelope)
        {
            requestEnvelope = default;

            if (!requestEnvelopeCodec.TryDecode(requestEnvelopePayload, out requestEnvelope, out _, out var decodeError))
            {
                runtime.LogWarning($"[PullSubRuntime] Dropped invalid request envelope. error={decodeError}");
                return false;
            }

            if (string.IsNullOrWhiteSpace(requestEnvelope.CorrelationId)
                || requestEnvelope.CorrelationId.Length > runtime.RuntimeOptions.MaxCorrelationIdLength)
            {
                runtime.LogWarning(
                    $"[PullSubRuntime] Dropped request with invalid correlationId length. " +
                    $"max={runtime.RuntimeOptions.MaxCorrelationIdLength}");
                return false;
            }

            if (!IsAllowedReplyTo(runtime, requestEnvelope.ReplyTo))
            {
                runtime.MarkInvalidReplyToDrop(requestEnvelope.ReplyTo);
                return false;
            }

            return true;
        }

        private static bool IsAllowedReplyTo(PullSubRuntime runtime, string replyTo)
        {
            if (runtime == null)
                return false;

            if (string.IsNullOrWhiteSpace(replyTo))
                return false;

            if (replyTo.Length > runtime.RuntimeOptions.MaxReplyToLength)
                return false;

            try
            {
                SubscriptionRegistry.ValidateExactMatchTopic(replyTo);
            }
            catch (PullSubWildcardTopicNotSupportedException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }

            var normalizedPrefix = runtime.RequestOptions.ReplyTopicPrefix.TrimEnd('/');
            if (normalizedPrefix.Length == 0)
                return false;

            var expectedPrefix = normalizedPrefix + "/";
            if (!replyTo.StartsWith(expectedPrefix, StringComparison.Ordinal))
                return false;

            var suffix = replyTo.AsSpan(expectedPrefix.Length);
            if (suffix.Length != 32)
                return false;

            for (var i = 0; i < suffix.Length; i++)
            {
                var c = suffix[i];
                var isDigit = c >= '0' && c <= '9';
                var isLowerHex = c >= 'a' && c <= 'f';
                if (!isDigit && !isLowerHex)
                    return false;
            }

            return true;
        }

        private static string FormatRemoteErrorMessage(Exception exception)
        {
            return "Remote handler failed.";
        }

        private static Func<TRequest, RequestContext, ReplySender<TResponse>, CancellationToken, ValueTask>
            WrapValueResponderHandler<TRequest, TResponse>(
                Func<TRequest, RequestContext, CancellationToken, ValueTask<TResponse>> handler)
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

        private static Func<TRequest, RequestContext, ReplySender<TResponse>, CancellationToken, ValueTask>
            WrapValueResponderHandler<TRequest, TResponse>(
                Func<TRequest, CancellationToken, ValueTask<TResponse>> handler)
        {
            return WrapValueResponderHandler<TRequest, TResponse>(
                (request, _, ct) => handler(request, ct));
        }
    }
}
