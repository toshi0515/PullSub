using System;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using PullSub.Core;

namespace PullSub.Bridge
{
    public static class PullSubRuntimeUnityQueueExtensions
    {
        public static Task<PullSubQueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
            this PullSubRuntime runtime,
            IPullSubTopic<T> topic,
            Func<T, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            return SubscribeQueueOnMainThreadAsync(runtime, topic, PullSubQueueOptions.Default, handler, cancellationToken);
        }

        public static Task<PullSubQueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
            this PullSubRuntime runtime,
            IPullSubTopic<T> topic,
            Func<T, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return SubscribeQueueOnMainThreadAsync(runtime, topic, PullSubQueueOptions.Default, (message, _) => handler(message), cancellationToken);
        }

        public static Task<PullSubQueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
            this PullSubRuntime runtime,
            IPullSubTopic<T> topic,
            Action<T, CancellationToken> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return SubscribeQueueOnMainThreadAsync(
                runtime,
                topic,
                PullSubQueueOptions.Default,
                (message, ct) =>
                {
                    handler(message, ct);
                    return default;
                },
                cancellationToken);
        }

        public static Task<PullSubQueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
            this PullSubRuntime runtime,
            IPullSubTopic<T> topic,
            Action<T> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return SubscribeQueueOnMainThreadAsync(
                runtime,
                topic,
                PullSubQueueOptions.Default,
                (message, _) =>
                {
                    handler(message);
                    return default;
                },
                cancellationToken);
        }

        public static Task<PullSubQueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
            this PullSubRuntime runtime,
            IPullSubTopic<T> topic,
            PullSubQueueOptions options,
            Func<T, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            if (runtime == null)
                throw new ArgumentNullException(nameof(runtime));

            if (topic == null)
                throw new ArgumentNullException(nameof(topic));

            if (options == null)
                throw new ArgumentNullException(nameof(options));

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return runtime.SubscribeQueueAsync(
                topic,
                options,
                async (message, ct) =>
                {
                    await UniTask.SwitchToMainThread(ct);
                    await handler(message, ct);
                },
                cancellationToken);
        }

        public static Task<PullSubQueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
            this PullSubRuntime runtime,
            IPullSubTopic<T> topic,
            PullSubQueueOptions options,
            Func<T, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return SubscribeQueueOnMainThreadAsync(runtime, topic, options, (message, _) => handler(message), cancellationToken);
        }

        public static Task<PullSubQueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
            this PullSubRuntime runtime,
            IPullSubTopic<T> topic,
            PullSubQueueOptions options,
            Action<T, CancellationToken> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return SubscribeQueueOnMainThreadAsync(
                runtime,
                topic,
                options,
                (message, ct) =>
                {
                    handler(message, ct);
                    return default;
                },
                cancellationToken);
        }

        public static Task<PullSubQueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
            this PullSubRuntime runtime,
            IPullSubTopic<T> topic,
            PullSubQueueOptions options,
            Action<T> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return SubscribeQueueOnMainThreadAsync(
                runtime,
                topic,
                options,
                (message, _) =>
                {
                    handler(message);
                    return default;
                },
                cancellationToken);
        }
    }
}
