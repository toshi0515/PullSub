using System;

namespace PullSub.Core
{
    public class PullSubException : Exception
    {
        public PullSubException(string message) : base(message)
        {
        }

        public PullSubException(string message, Exception innerException) : base(message, innerException)
        {
        }
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
}