using System;

namespace PullSub.Core
{
    /// <summary>
    /// Typed request topic definition.
    /// </summary>
    public interface IRequestTopic<TRequest, TResponse>
    {
        string RequestTopicName { get; }
        IPayloadCodec<TRequest> RequestCodec { get; }
        IPayloadCodec<TResponse> ResponseCodec { get; }
    }

    internal interface IRequestTopicInternal<TRequest, TResponse> : IRequestTopic<TRequest, TResponse>
    {
        RequestEnvelopeCodec<TRequest> RequestEnvelopeCodec { get; }
        ResponseEnvelopeCodec<TResponse> ResponseEnvelopeCodec { get; }
        ITopic<RequestEnvelope<TRequest>> RequestEnvelopeTopic { get; }
    }

    /// <summary>
    /// Factory methods for request topic definitions.
    /// </summary>
    public static class RequestTopic
    {
        public static IRequestTopic<TRequest, TResponse> Create<TRequest, TResponse>(string requestTopicName)
        {
            return Create(
                requestTopicName,
                JsonPayloadCodec<TRequest>.Default,
                JsonPayloadCodec<TResponse>.Default);
        }

        public static IRequestTopic<TRequest, TResponse> Create<TRequest, TResponse>(
            string requestTopicName,
            IPayloadCodec<TRequest> requestCodec,
            IPayloadCodec<TResponse> responseCodec)
        {
            TopicValidator.ValidateExactMatchTopic(requestTopicName);

            if (requestCodec == null)
                throw new ArgumentNullException(nameof(requestCodec));

            if (responseCodec == null)
                throw new ArgumentNullException(nameof(responseCodec));

            return new RequestTopicDefinition<TRequest, TResponse>(
                requestTopicName,
                requestCodec,
                responseCodec);
        }

        private sealed class RequestTopicDefinition<TRequest, TResponse> : IRequestTopicInternal<TRequest, TResponse>
        {
            public RequestTopicDefinition(
                string requestTopicName,
                IPayloadCodec<TRequest> requestCodec,
                IPayloadCodec<TResponse> responseCodec)
            {
                RequestTopicName = requestTopicName;
                RequestCodec = requestCodec;
                ResponseCodec = responseCodec;
                RequestEnvelopeCodec = new RequestEnvelopeCodec<TRequest>(requestCodec);
                ResponseEnvelopeCodec = new ResponseEnvelopeCodec<TResponse>(responseCodec);
                RequestEnvelopeTopic = PullSubTopic.Create(requestTopicName, RequestEnvelopeCodec);
            }

            public string RequestTopicName { get; }
            public IPayloadCodec<TRequest> RequestCodec { get; }
            public IPayloadCodec<TResponse> ResponseCodec { get; }
            public RequestEnvelopeCodec<TRequest> RequestEnvelopeCodec { get; }
            public ResponseEnvelopeCodec<TResponse> ResponseEnvelopeCodec { get; }
            public ITopic<RequestEnvelope<TRequest>> RequestEnvelopeTopic { get; }
        }
    }
}
