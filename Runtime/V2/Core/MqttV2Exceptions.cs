using System;

namespace UnityMqtt.V2.Core
{
    public class MqttV2Exception : Exception
    {
        public MqttV2Exception(string message) : base(message)
        {
        }

        public MqttV2Exception(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    public sealed class MqttV2WildcardTopicNotSupportedException : MqttV2Exception
    {
        public MqttV2WildcardTopicNotSupportedException(string topic)
            : base($"Wildcard topics are not supported in v2. Use exact-match topics only: {topic}")
        {
            Topic = topic;
        }

        public string Topic { get; }
    }
}