using System;

namespace PullSub.Core
{
    /// <summary>
    /// Request metadata passed to responder handlers.
    /// </summary>
    public readonly struct PullSubRequestContext
    {
        public PullSubRequestContext(
            string correlationId,
            string replyTo,
            DateTime sentUtc,
            DateTime deadlineUtc)
        {
            CorrelationId = correlationId ?? string.Empty;
            ReplyTo = replyTo ?? string.Empty;
            SentUtc = sentUtc;
            DeadlineUtc = deadlineUtc;
        }

        public string CorrelationId { get; }
        public string ReplyTo { get; }
        public DateTime SentUtc { get; }
        public DateTime DeadlineUtc { get; }

        public bool IsExpired(DateTime utcNow)
        {
            if (DeadlineUtc == default)
                return false;

            return utcNow >= DeadlineUtc;
        }
    }
}
