using System;

namespace PullSub.Core
{
    /// <summary>
    /// Runtime-scoped configuration for request/reply behavior.
    /// </summary>
    public sealed class PullSubRequestOptions : IEquatable<PullSubRequestOptions>
    {
        public const string DefaultReplyTopicPrefix = "pullsub/reply";
        public const int DefaultInboxIdleTimeoutSeconds = 60;
        public const int DefaultReplyInboxQueueDepth = PullSubQueueOptions.DefaultMaxQueueDepth;
        public const int DefaultMaxPendingRequests = 1024;

        public static PullSubRequestOptions Default { get; }
            = new PullSubRequestOptions(
                DefaultReplyTopicPrefix,
                DefaultInboxIdleTimeoutSeconds,
                DefaultReplyInboxQueueDepth,
                DefaultMaxPendingRequests);

        public PullSubRequestOptions(
            string replyTopicPrefix,
            int inboxIdleTimeoutSeconds,
            int maxPendingRequests)
            : this(
                replyTopicPrefix,
                TimeSpan.FromSeconds(inboxIdleTimeoutSeconds),
                DefaultReplyInboxQueueDepth,
                maxPendingRequests)
        {
        }

        public PullSubRequestOptions(
            string replyTopicPrefix,
            int inboxIdleTimeoutSeconds,
            int replyInboxQueueDepth,
            int maxPendingRequests)
            : this(
                replyTopicPrefix,
                TimeSpan.FromSeconds(inboxIdleTimeoutSeconds),
                replyInboxQueueDepth,
                maxPendingRequests)
        {
        }

        public PullSubRequestOptions(
            string replyTopicPrefix = DefaultReplyTopicPrefix,
            TimeSpan? inboxIdleTimeout = null,
            int replyInboxQueueDepth = DefaultReplyInboxQueueDepth,
            int maxPendingRequests = DefaultMaxPendingRequests)
        {
            if (string.IsNullOrWhiteSpace(replyTopicPrefix))
                throw new ArgumentException("replyTopicPrefix is required.", nameof(replyTopicPrefix));

            if (replyTopicPrefix.IndexOf('#') >= 0 || replyTopicPrefix.IndexOf('+') >= 0)
                throw new PullSubWildcardTopicNotSupportedException(replyTopicPrefix);

            var idleTimeout = inboxIdleTimeout ?? TimeSpan.FromSeconds(DefaultInboxIdleTimeoutSeconds);
            if (idleTimeout < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(inboxIdleTimeout), "inboxIdleTimeout must be zero or greater.");

            if (replyInboxQueueDepth < 1)
                throw new ArgumentOutOfRangeException(nameof(replyInboxQueueDepth), "replyInboxQueueDepth must be greater than 0.");

            if (maxPendingRequests < 1)
                throw new ArgumentOutOfRangeException(nameof(maxPendingRequests), "maxPendingRequests must be greater than 0.");

            ReplyTopicPrefix = replyTopicPrefix;
            InboxIdleTimeout = idleTimeout;
            ReplyInboxQueueDepth = replyInboxQueueDepth;
            MaxPendingRequests = maxPendingRequests;
        }

        public string ReplyTopicPrefix { get; }
        public TimeSpan InboxIdleTimeout { get; }
        public int ReplyInboxQueueDepth { get; }
        public int MaxPendingRequests { get; }

        public bool Equals(PullSubRequestOptions other)
        {
            if (ReferenceEquals(null, other))
                return false;

            if (ReferenceEquals(this, other))
                return true;

            return string.Equals(ReplyTopicPrefix, other.ReplyTopicPrefix, StringComparison.Ordinal)
                && InboxIdleTimeout == other.InboxIdleTimeout
                && ReplyInboxQueueDepth == other.ReplyInboxQueueDepth
                && MaxPendingRequests == other.MaxPendingRequests;
        }

        public override bool Equals(object obj) => Equals(obj as PullSubRequestOptions);

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = StringComparer.Ordinal.GetHashCode(ReplyTopicPrefix);
                hashCode = (hashCode * 397) ^ InboxIdleTimeout.GetHashCode();
                hashCode = (hashCode * 397) ^ ReplyInboxQueueDepth;
                hashCode = (hashCode * 397) ^ MaxPendingRequests;
                return hashCode;
            }
        }
    }
}
