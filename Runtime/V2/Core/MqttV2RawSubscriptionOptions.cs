using System;

namespace UnityMqtt.V2.Core
{
    public sealed class MqttV2RawSubscriptionOptions : IEquatable<MqttV2RawSubscriptionOptions>
    {
        public const int DefaultMaxQueueDepth = 256;

        public static MqttV2RawSubscriptionOptions Default { get; }
            = new MqttV2RawSubscriptionOptions(DefaultMaxQueueDepth);

        public MqttV2RawSubscriptionOptions(int maxQueueDepth)
        {
            if (maxQueueDepth < 1)
                throw new ArgumentOutOfRangeException(nameof(maxQueueDepth), "maxQueueDepth must be greater than 0.");

            MaxQueueDepth = maxQueueDepth;
        }

        public int MaxQueueDepth { get; }

        public bool Equals(MqttV2RawSubscriptionOptions other)
        {
            if (ReferenceEquals(null, other))
                return false;

            if (ReferenceEquals(this, other))
                return true;

            return MaxQueueDepth == other.MaxQueueDepth;
        }

        public override bool Equals(object obj) => Equals(obj as MqttV2RawSubscriptionOptions);

        public override int GetHashCode()
        {
            return MaxQueueDepth;
        }
    }
}