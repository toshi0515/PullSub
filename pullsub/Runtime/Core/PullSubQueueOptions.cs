using System;

namespace PullSub.Core
{
    public sealed class PullSubQueueOptions : IEquatable<PullSubQueueOptions>
    {
        public const int DefaultMaxQueueDepth = 256;

        public static PullSubQueueOptions Default { get; }
            = new PullSubQueueOptions(DefaultMaxQueueDepth);

        public PullSubQueueOptions(int maxQueueDepth)
        {
            if (maxQueueDepth < 1)
                throw new ArgumentOutOfRangeException(nameof(maxQueueDepth), "maxQueueDepth must be greater than 0.");

            MaxQueueDepth = maxQueueDepth;
        }

        public int MaxQueueDepth { get; }

        public bool Equals(PullSubQueueOptions other)
        {
            if (ReferenceEquals(null, other))
                return false;

            if (ReferenceEquals(this, other))
                return true;

            return MaxQueueDepth == other.MaxQueueDepth;
        }

        public override bool Equals(object obj) => Equals(obj as PullSubQueueOptions);

        public override int GetHashCode()
        {
            return MaxQueueDepth;
        }
    }
}