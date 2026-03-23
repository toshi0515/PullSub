using System;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    /// <summary>
    /// Async-disposable registration handle for a raw message handler loop.
    /// </summary>
    public sealed class PullSubQueueHandlerRegistration : IAsyncDisposable
    {
        private readonly CancellationTokenSource _registrationCts;
        private readonly CancellationTokenSource _registrationLinkedCts;
        private readonly Task _completion;

        private int _stopRequested;
        private int _disposed;

        internal PullSubQueueHandlerRegistration(
            CancellationTokenSource registrationCts,
            CancellationTokenSource registrationLinkedCts,
            Task completion)
        {
            _registrationCts = registrationCts ?? throw new ArgumentNullException(nameof(registrationCts));
            _registrationLinkedCts = registrationLinkedCts;
            _completion = completion ?? throw new ArgumentNullException(nameof(completion));
        }

        /// <summary>
        /// Completes when the underlying handler loop exits.
        /// This task faults if the handler throws an exception.
        /// </summary>
        public Task Completion => _completion;

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