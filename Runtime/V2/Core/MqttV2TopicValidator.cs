using System;

namespace UnityMqtt.V2.Core
{
    internal static class MqttV2TopicValidator
    {
        public static void ValidateExactMatchTopic(string topic)
        {
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("topic is required.", nameof(topic));

            if (topic.IndexOf('#') >= 0 || topic.IndexOf('+') >= 0)
                throw new MqttV2WildcardTopicNotSupportedException(topic);
        }
    }
}