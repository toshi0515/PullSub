using System;
using UnityEngine;

namespace PullSub.Bridge
{
    [Serializable]
    public struct PullSubTopicKey : IEquatable<PullSubTopicKey>
    {
        [SerializeField] private string _value;

        public PullSubTopicKey(string value)
        {
            _value = value ?? string.Empty;
        }

        public string Value => _value ?? string.Empty;

        public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

        public static PullSubTopicKey From(string value)
        {
            return new PullSubTopicKey(value);
        }

        public bool Equals(PullSubTopicKey other)
        {
            return string.Equals(Value, other.Value, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is PullSubTopicKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return StringComparer.Ordinal.GetHashCode(Value);
        }

        public override string ToString()
        {
            return Value;
        }

        public static bool operator ==(PullSubTopicKey left, PullSubTopicKey right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(PullSubTopicKey left, PullSubTopicKey right)
        {
            return !left.Equals(right);
        }
    }
}
