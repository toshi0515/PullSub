using System;

namespace PullSub.Core
{
    public enum PullSubRequestFailureKind
    {
        Timeout = 0,
        ConnectionLost = 1,
        RuntimeDisposed = 2,
        SetupFailed = 3,
        PublishFailed = 4,
        PayloadDecodeFailed = 5,
        RemoteError = 6,
    }

    public class PullSubException : Exception
    {
        public PullSubException(string message) : base(message)
        {
        }

        public PullSubException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    public sealed class PullSubConnectionStateException : PullSubException
    {
        public PullSubConnectionStateException(string operation)
            : base($"Operation '{operation}' requires an active transport connection.")
        {
            Operation = operation;
        }

        public string Operation { get; }
    }

    public sealed class PullSubPayloadDecodeException : PullSubException
    {
        public PullSubPayloadDecodeException(string topic, string decodeError, Exception innerException = null)
            : base($"Payload decode failed. topic={topic} error={decodeError}", innerException)
        {
            Topic = topic;
            DecodeError = decodeError;
        }

        public string Topic { get; }
        public string DecodeError { get; }
    }

    public sealed class PullSubWildcardTopicNotSupportedException : PullSubException
    {
        public PullSubWildcardTopicNotSupportedException(string topic)
            : base($"Wildcard topics are not supported in PullSub. Use exact-match topics only: {topic}")
        {
            Topic = topic;
        }

        public string Topic { get; }
    }

    public class PullSubRequestException : PullSubException
    {
        public PullSubRequestException(
            PullSubRequestFailureKind failureKind,
            string message,
            string correlationId = null,
            Exception innerException = null)
            : base(message, innerException)
        {
            FailureKind = failureKind;
            CorrelationId = correlationId;
        }

        public PullSubRequestFailureKind FailureKind { get; }
        public string CorrelationId { get; }
    }

    public sealed class PullSubTooManyPendingRequestsException : PullSubRequestException
    {
        public PullSubTooManyPendingRequestsException(int maxPendingRequests)
            : base(
                PullSubRequestFailureKind.SetupFailed,
                $"Too many pending requests. MaxPendingRequests={maxPendingRequests}.")
        {
            MaxPendingRequests = maxPendingRequests;
        }

        public int MaxPendingRequests { get; }
    }
}