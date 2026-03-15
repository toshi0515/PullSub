using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityMqtt.V2.Core
{
    internal static class MqttV2AsyncUtils
    {
        public static Task AwaitWithCancellation(Task task, CancellationToken cancellationToken)
        {
            if (task == null)
                throw new ArgumentNullException(nameof(task));

            // Fast path: avoid async state machine allocation when cancellation is not used.
            if (!cancellationToken.CanBeCanceled)
                return task;

            return AwaitWithCancellationCore(task, cancellationToken);
        }

        private static async Task AwaitWithCancellationCore(Task task, CancellationToken cancellationToken)
        {
            // This helper stops waiting when canceled, but it does not cancel the underlying task.
            // MQTTnet managed client async APIs used here do not accept CancellationToken.
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var completed = await Task.WhenAny(task, cancellationTask);
            if (!ReferenceEquals(completed, task))
                throw new OperationCanceledException(cancellationToken);

            await task;
        }
    }
}
