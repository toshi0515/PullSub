using System;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    internal interface IPullSubSubscriptionLease : IDisposable, IAsyncDisposable
    {
        string Topic { get; }

        Task<PullSubUnsubscribeResult> UnsubscribeAsync(CancellationToken cancellationToken = default);
    }
}