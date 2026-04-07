using System;

namespace PullSub.Core
{
    internal static class TopicValidator
    {
        public static void ValidateExactMatchTopic(string topic)
        {
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("topic is required.", nameof(topic));

            if (topic.IndexOf('#') >= 0 || topic.IndexOf('+') >= 0)
                throw new PullSubWildcardTopicNotSupportedException(topic);
        }
    }
}