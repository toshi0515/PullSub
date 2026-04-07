using System;

namespace PullSub.Core
{
    /// <summary>
    /// Runtime-scoped guard options for transport-agnostic security hardening.
    /// </summary>
    public sealed class PullSubRuntimeOptions : IEquatable<PullSubRuntimeOptions>
    {
        public const int DefaultMaxInboundPayloadBytes = 1_048_576;
        public const int DefaultMaxReplyToLength = 512;
        public const int DefaultMaxCorrelationIdLength = 128;
        public const int DefaultInvalidReplyToAggregateThreshold = 10;
        public const int DefaultInboundOversizeAggregateThreshold = 10;

        public static readonly TimeSpan DefaultInvalidReplyToLogInterval = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan DefaultInboundOversizeLogInterval = TimeSpan.FromSeconds(60);

        public static PullSubRuntimeOptions Default { get; }
            = new PullSubRuntimeOptions();

        public PullSubRuntimeOptions(
            int maxInboundPayloadBytes = DefaultMaxInboundPayloadBytes,
            int maxReplyToLength = DefaultMaxReplyToLength,
            int maxCorrelationIdLength = DefaultMaxCorrelationIdLength,
            TimeSpan? invalidReplyToLogInterval = null,
            int invalidReplyToAggregateThreshold = DefaultInvalidReplyToAggregateThreshold,
            int inboundOversizeAggregateThreshold = DefaultInboundOversizeAggregateThreshold,
            TimeSpan? inboundOversizeLogInterval = null)
        {
            if (maxInboundPayloadBytes < 1)
                throw new ArgumentOutOfRangeException(nameof(maxInboundPayloadBytes), "maxInboundPayloadBytes must be greater than 0.");

            if (maxReplyToLength < 1)
                throw new ArgumentOutOfRangeException(nameof(maxReplyToLength), "maxReplyToLength must be greater than 0.");

            if (maxCorrelationIdLength < 1)
                throw new ArgumentOutOfRangeException(nameof(maxCorrelationIdLength), "maxCorrelationIdLength must be greater than 0.");

            var invalidReplyInterval = invalidReplyToLogInterval ?? DefaultInvalidReplyToLogInterval;
            if (invalidReplyInterval < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(invalidReplyToLogInterval),
                    "invalidReplyToLogInterval must be zero or greater.");
            }

            if (invalidReplyToAggregateThreshold < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(invalidReplyToAggregateThreshold),
                    "invalidReplyToAggregateThreshold must be greater than 0.");
            }

            if (inboundOversizeAggregateThreshold < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(inboundOversizeAggregateThreshold),
                    "inboundOversizeAggregateThreshold must be greater than 0.");
            }

            var oversizeInterval = inboundOversizeLogInterval ?? DefaultInboundOversizeLogInterval;
            if (oversizeInterval < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(inboundOversizeLogInterval),
                    "inboundOversizeLogInterval must be zero or greater.");
            }

            MaxInboundPayloadBytes = maxInboundPayloadBytes;
            MaxReplyToLength = maxReplyToLength;
            MaxCorrelationIdLength = maxCorrelationIdLength;
            InvalidReplyToLogInterval = invalidReplyInterval;
            InvalidReplyToAggregateThreshold = invalidReplyToAggregateThreshold;
            InboundOversizeAggregateThreshold = inboundOversizeAggregateThreshold;
            InboundOversizeLogInterval = oversizeInterval;
        }

        public int MaxInboundPayloadBytes { get; }
        public int MaxReplyToLength { get; }
        public int MaxCorrelationIdLength { get; }
        public TimeSpan InvalidReplyToLogInterval { get; }
        public int InvalidReplyToAggregateThreshold { get; }
        public int InboundOversizeAggregateThreshold { get; }
        public TimeSpan InboundOversizeLogInterval { get; }

        public bool Equals(PullSubRuntimeOptions other)
        {
            if (ReferenceEquals(null, other))
                return false;

            if (ReferenceEquals(this, other))
                return true;

            return MaxInboundPayloadBytes == other.MaxInboundPayloadBytes
                && MaxReplyToLength == other.MaxReplyToLength
                && MaxCorrelationIdLength == other.MaxCorrelationIdLength
                && InvalidReplyToLogInterval == other.InvalidReplyToLogInterval
                && InvalidReplyToAggregateThreshold == other.InvalidReplyToAggregateThreshold
                && InboundOversizeAggregateThreshold == other.InboundOversizeAggregateThreshold
                && InboundOversizeLogInterval == other.InboundOversizeLogInterval;
        }

        public override bool Equals(object obj) => Equals(obj as PullSubRuntimeOptions);

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = MaxInboundPayloadBytes;
                hashCode = (hashCode * 397) ^ MaxReplyToLength;
                hashCode = (hashCode * 397) ^ MaxCorrelationIdLength;
                hashCode = (hashCode * 397) ^ InvalidReplyToLogInterval.GetHashCode();
                hashCode = (hashCode * 397) ^ InvalidReplyToAggregateThreshold;
                hashCode = (hashCode * 397) ^ InboundOversizeAggregateThreshold;
                hashCode = (hashCode * 397) ^ InboundOversizeLogInterval.GetHashCode();
                return hashCode;
            }
        }
    }
}
