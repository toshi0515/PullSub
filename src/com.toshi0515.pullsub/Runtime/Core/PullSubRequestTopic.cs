using System;

namespace PullSub.Core
{
    /// <summary>
    /// Typed request topic definition.
    /// </summary>
    public interface IPullSubRequestTopic<TRequest, TResponse>
    {
        string RequestTopicName { get; }
        IPayloadCodec<TRequest> RequestCodec { get; }
        IPayloadCodec<TResponse> ResponseCodec { get; }
    }

    internal interface IPullSubRequestTopicInternal<TRequest, TResponse> : IPullSubRequestTopic<TRequest, TResponse>
    {
        PullSubRequestEnvelopeCodec<TRequest> RequestEnvelopeCodec { get; }
        PullSubResponseEnvelopeCodec<TResponse> ResponseEnvelopeCodec { get; }
        IPullSubTopic<PullSubRequestEnvelope<TRequest>> RequestEnvelopeTopic { get; }
    }

    /// <summary>
    /// Factory methods for request topic definitions.
    /// </summary>
    public static class PullSubRequestTopic
    {
        public static IPullSubRequestTopic<TRequest, TResponse> Create<TRequest, TResponse>(string requestTopicName)
        {
            return Create(
                requestTopicName,
                PullSubJsonPayloadCodec<TRequest>.Default,
                PullSubJsonPayloadCodec<TResponse>.Default);
        }

        public static IPullSubRequestTopic<TRequest, TResponse> Create<TRequest, TResponse>(
            string requestTopicName,
            IPayloadCodec<TRequest> requestCodec,
            IPayloadCodec<TResponse> responseCodec)
        {
            PullSubTopicValidator.ValidateExactMatchTopic(requestTopicName);

            if (requestCodec == null)
                throw new ArgumentNullException(nameof(requestCodec));

            if (responseCodec == null)
                throw new ArgumentNullException(nameof(responseCodec));

            return new RequestTopicDefinition<TRequest, TResponse>(
                requestTopicName,
                requestCodec,
                responseCodec);
        }

        private sealed class RequestTopicDefinition<TRequest, TResponse> : IPullSubRequestTopicInternal<TRequest, TResponse>
        {
            public RequestTopicDefinition(
                string requestTopicName,
                IPayloadCodec<TRequest> requestCodec,
                IPayloadCodec<TResponse> responseCodec)
            {
                RequestTopicName = requestTopicName;
                RequestCodec = requestCodec;
                ResponseCodec = responseCodec;
                RequestEnvelopeCodec = new PullSubRequestEnvelopeCodec<TRequest>(requestCodec);
                ResponseEnvelopeCodec = new PullSubResponseEnvelopeCodec<TResponse>(responseCodec);
                RequestEnvelopeTopic = PullSubTopic.Create(requestTopicName, RequestEnvelopeCodec);
            }

            public string RequestTopicName { get; }
            public IPayloadCodec<TRequest> RequestCodec { get; }
            public IPayloadCodec<TResponse> ResponseCodec { get; }
            public PullSubRequestEnvelopeCodec<TRequest> RequestEnvelopeCodec { get; }
            public PullSubResponseEnvelopeCodec<TResponse> ResponseEnvelopeCodec { get; }
            public IPullSubTopic<PullSubRequestEnvelope<TRequest>> RequestEnvelopeTopic { get; }
        }
    }
}
