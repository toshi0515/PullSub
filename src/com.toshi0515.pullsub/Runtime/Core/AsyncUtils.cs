using System;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    internal static class AsyncUtils
    {
        public static Task AwaitWithCancellation(Task task, CancellationToken cancellationToken)
        {
            return AwaitWithCancellation(task, cancellationToken, CancellationToken.None);
        }

        public static Task AwaitWithCancellation(
            Task task,
            CancellationToken cancellationToken,
            CancellationToken secondaryCancellationToken)
        {
            if (task == null)
                throw new ArgumentNullException(nameof(task));

            // Fast path for completed task.
            if (task.IsCompleted)
                return task;

            var hasPrimary = cancellationToken.CanBeCanceled;
            var hasSecondary = secondaryCancellationToken.CanBeCanceled;
            if (!hasPrimary && !hasSecondary)
                return task;

            if (hasPrimary && cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);

            if (hasSecondary && secondaryCancellationToken.IsCancellationRequested)
                return Task.FromCanceled(secondaryCancellationToken);

            return AwaitWithCancellationCore(
                task,
                cancellationToken,
                secondaryCancellationToken,
                hasPrimary,
                hasSecondary);
        }

        private static async Task AwaitWithCancellationCore(
            Task task,
            CancellationToken cancellationToken,
            CancellationToken secondaryCancellationToken,
            bool hasPrimary,
            bool hasSecondary)
        {
            // This helper stops waiting when either token is canceled, but it does not cancel the underlying task.
            // MQTTnet managed client async APIs used here do not accept CancellationToken.
            var cancellationSignal = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration primaryRegistration = default;
            CancellationTokenRegistration secondaryRegistration = default;

            try
            {
                if (hasPrimary)
                {
                    primaryRegistration = cancellationToken.Register(static state =>
                    {
                        var data = ((TaskCompletionSource<CancellationToken>, CancellationToken))state;
                        data.Item1.TrySetResult(data.Item2);
                    }, (cancellationSignal, cancellationToken));
                }

                if (hasSecondary)
                {
                    secondaryRegistration = secondaryCancellationToken.Register(static state =>
                    {
                        var data = ((TaskCompletionSource<CancellationToken>, CancellationToken))state;
                        data.Item1.TrySetResult(data.Item2);
                    }, (cancellationSignal, secondaryCancellationToken));
                }

                var completed = await Task.WhenAny(task, cancellationSignal.Task).ConfigureAwait(false);
                if (!ReferenceEquals(completed, task))
                    throw new OperationCanceledException(cancellationSignal.Task.Result);

                await task.ConfigureAwait(false);
            }
            finally
            {
                primaryRegistration.Dispose();
                secondaryRegistration.Dispose();
            }
        }
    }
}
