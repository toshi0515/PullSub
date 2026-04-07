using System;

namespace PullSub.Core
{
    public sealed class ReconnectOptions : IEquatable<ReconnectOptions>
    {
        public static ReconnectOptions Default { get; }
            = new ReconnectOptions(
                initialDelay: TimeSpan.FromSeconds(1),
                maxDelay: TimeSpan.FromSeconds(60),
                multiplier: 2.0,
                jitterFactor: 0.2);

        public ReconnectOptions(
            TimeSpan initialDelay,
            TimeSpan maxDelay,
            double multiplier,
            double jitterFactor)
        {
            if (initialDelay <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(initialDelay), "initialDelay must be greater than 0.");

            if (maxDelay <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(maxDelay), "maxDelay must be greater than 0.");

            if (maxDelay < initialDelay)
                throw new ArgumentOutOfRangeException(nameof(maxDelay), "maxDelay must be greater than or equal to initialDelay.");

            if (multiplier < 1.0)
                throw new ArgumentOutOfRangeException(nameof(multiplier), "multiplier must be greater than or equal to 1.0.");

            if (jitterFactor < 0.0 || jitterFactor > 1.0)
                throw new ArgumentOutOfRangeException(nameof(jitterFactor), "jitterFactor must be in range [0.0, 1.0].");

            InitialDelay = initialDelay;
            MaxDelay = maxDelay;
            Multiplier = multiplier;
            JitterFactor = jitterFactor;
        }

        public TimeSpan InitialDelay { get; }
        public TimeSpan MaxDelay { get; }
        public double Multiplier { get; }
        public double JitterFactor { get; }

        public static ReconnectOptions FromInitialDelaySeconds(int reconnectDelaySeconds)
        {
            if (reconnectDelaySeconds < 1)
                throw new ArgumentOutOfRangeException(nameof(reconnectDelaySeconds), "reconnectDelaySeconds must be greater than 0.");

            var initial = TimeSpan.FromSeconds(reconnectDelaySeconds);
            var max = TimeSpan.FromSeconds(Math.Max(reconnectDelaySeconds, 60));
            return new ReconnectOptions(initial, max, multiplier: 2.0, jitterFactor: 0.2);
        }

        public bool Equals(ReconnectOptions other)
        {
            if (ReferenceEquals(null, other))
                return false;

            if (ReferenceEquals(this, other))
                return true;

            return InitialDelay.Equals(other.InitialDelay)
                && MaxDelay.Equals(other.MaxDelay)
                && Multiplier.Equals(other.Multiplier)
                && JitterFactor.Equals(other.JitterFactor);
        }

        public override bool Equals(object obj) => Equals(obj as ReconnectOptions);

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = InitialDelay.GetHashCode();
                hashCode = (hashCode * 397) ^ MaxDelay.GetHashCode();
                hashCode = (hashCode * 397) ^ Multiplier.GetHashCode();
                hashCode = (hashCode * 397) ^ JitterFactor.GetHashCode();
                return hashCode;
            }
        }
    }
}