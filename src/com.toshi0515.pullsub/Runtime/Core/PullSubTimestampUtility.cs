using System;

namespace PullSub.Core
{
    internal static class PullSubTimestampUtility
    {
        internal static DateTime NormalizeOrNow(DateTime timestampUtc)
        {
            if (timestampUtc == default)
                return DateTime.UtcNow;

            return timestampUtc.Kind == DateTimeKind.Utc
                ? timestampUtc
                : timestampUtc.ToUniversalTime();
        }
    }
}