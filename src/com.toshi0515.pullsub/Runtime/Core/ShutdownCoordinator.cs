using System;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    internal sealed class ShutdownCoordinator
    {
        private readonly Action<string> _logError;

        public ShutdownCoordinator(Action<string> logError)
        {
            _logError = logError ?? (_ => { });
        }

        public async Task RunAsync(
            Func<CancellationToken, Task> disconnectAsync,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (disconnectAsync == null)
                throw new ArgumentNullException(nameof(disconnectAsync));

            if (timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout), "timeout must be greater than zero.");

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await disconnectAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logError($"[ShutdownCoordinator] Shutdown timed out after {timeout.TotalSeconds:0.###}s.");
            }
        }
    }
}
