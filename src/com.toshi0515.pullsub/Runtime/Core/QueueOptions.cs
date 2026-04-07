using System;

namespace PullSub.Core
{
    public sealed class QueueOptions : IEquatable<QueueOptions>
    {
        public const int DefaultMaxQueueDepth = 256;

        public static QueueOptions Default { get; }
            = new QueueOptions(DefaultMaxQueueDepth);

        public QueueOptions(int maxQueueDepth)
        {
            if (maxQueueDepth < 1)
                throw new ArgumentOutOfRangeException(nameof(maxQueueDepth), "maxQueueDepth must be greater than 0.");

            MaxQueueDepth = maxQueueDepth;
        }

        public int MaxQueueDepth { get; }

        public bool Equals(QueueOptions other)
        {
            if (ReferenceEquals(null, other))
                return false;

            if (ReferenceEquals(this, other))
                return true;

            return MaxQueueDepth == other.MaxQueueDepth;
        }

        public override bool Equals(object obj) => Equals(obj as QueueOptions);

        public override int GetHashCode()
        {
            return MaxQueueDepth;
        }
    }
}