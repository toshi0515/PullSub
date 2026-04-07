using System;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    /// <summary>
    /// Async-disposable registration handle for a raw message handler loop.
    /// </summary>
    public sealed class QueueSubscription
    {
        private readonly CancellationTokenSource _registrationCts;
        private readonly CancellationTokenSource _registrationLinkedCts;
        private readonly Task _completion;

        private int _unsubscribeRequested;
        private int _stopRequested;
        private int _disposed;

        internal QueueSubscription(
            string topic,
            CancellationTokenSource registrationCts,
            CancellationTokenSource registrationLinkedCts,
            Task completion)
        {
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("topic is required.", nameof(topic));

            SubscriptionRegistry.ValidateExactMatchTopic(topic);

            Topic = topic;
            _registrationCts = registrationCts ?? throw new ArgumentNullException(nameof(registrationCts));
            _registrationLinkedCts = registrationLinkedCts;
            _completion = completion ?? throw new ArgumentNullException(nameof(completion));
        }

        public string Topic { get; }

        /// <summary>
        /// Completes when the underlying handler loop exits.
        /// This task faults if the handler throws an exception.
        /// </summary>
        public Task Completion => _completion;

        public async Task<PullSubUnsubscribeResult> UnsubscribeAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _unsubscribeRequested, 1) != 0)
                return PullSubUnsubscribeResult.AlreadyCanceled;

            if (Volatile.Read(ref _stopRequested) != 0)
                return PullSubUnsubscribeResult.AlreadyCanceled;

            try
            {
                await DisposeAsync().ConfigureAwait(false);
                return PullSubUnsubscribeResult.Success;
            }
            catch (ObjectDisposedException)
            {
                return PullSubUnsubscribeResult.AlreadyCanceled;
            }
            catch
            {
                return PullSubUnsubscribeResult.Failed;
            }
        }

        /// <summary>
        /// Requests loop cancellation and waits for completion.
        /// Exceptions from the handler are propagated, except for <see cref="OperationCanceledException"/>
        /// which is treated as normal cancellation.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            RequestStop();

            try
            {
                await _completion;
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                DisposeSources();
            }
        }

        public void Dispose()
        {
            _ = DisposeAsync().AsTask().ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void RequestStop()
        {
            if (Interlocked.Exchange(ref _stopRequested, 1) == 1)
                return;

            try
            {
                if (!_registrationCts.IsCancellationRequested)
                    _registrationCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void DisposeSources()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            _registrationLinkedCts?.Dispose();
            _registrationCts.Dispose();
        }
    }
}