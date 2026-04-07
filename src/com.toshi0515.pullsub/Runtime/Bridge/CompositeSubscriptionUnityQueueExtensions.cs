using System;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using PullSub.Core;

namespace PullSub.Bridge
{
    public static class CompositeSubscriptionUnityQueueExtensions
    {
        public static Task<QueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
            this CompositeSubscription context,
            ITopic<T> topic,
            Func<T, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            return SubscribeQueueOnMainThreadAsync(context, topic, QueueOptions.Default, handler, cancellationToken);
        }

        public static Task<QueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
            this CompositeSubscription context,
            ITopic<T> topic,
            Func<T, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return SubscribeQueueOnMainThreadAsync(context, topic, QueueOptions.Default, (message, _) => handler(message), cancellationToken);
        }

        public static Task<QueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
            this CompositeSubscription context,
            ITopic<T> topic,
            Action<T, CancellationToken> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return SubscribeQueueOnMainThreadAsync(
                context,
                topic,
                QueueOptions.Default,
                (message, ct) =>
                {
                    handler(message, ct);
                    return default;
                },
                cancellationToken);
        }

        public static Task<QueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
            this CompositeSubscription context,
            ITopic<T> topic,
            Action<T> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return SubscribeQueueOnMainThreadAsync(
                context,
                topic,
                QueueOptions.Default,
                (message, _) =>
                {
                    handler(message);
                    return default;
                },
                cancellationToken);
        }

        public static Task<QueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
            this CompositeSubscription context,
            ITopic<T> topic,
            QueueOptions options,
            Func<T, CancellationToken, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            if (topic == null)
                throw new ArgumentNullException(nameof(topic));

            if (options == null)
                throw new ArgumentNullException(nameof(options));

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return context.SubscribeQueueAsync(
                topic,
                options,
                async (message, ct) =>
                {
                    await UniTask.SwitchToMainThread(ct);
                    await handler(message, ct);
                },
                cancellationToken);
        }

        public static Task<QueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
            this CompositeSubscription context,
            ITopic<T> topic,
            QueueOptions options,
            Func<T, ValueTask> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return SubscribeQueueOnMainThreadAsync(context, topic, options, (message, _) => handler(message), cancellationToken);
        }

        public static Task<QueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
            this CompositeSubscription context,
            ITopic<T> topic,
            QueueOptions options,
            Action<T, CancellationToken> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return SubscribeQueueOnMainThreadAsync(
                context,
                topic,
                options,
                (message, ct) =>
                {
                    handler(message, ct);
                    return default;
                },
                cancellationToken);
        }

        public static Task<QueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
            this CompositeSubscription context,
            ITopic<T> topic,
            QueueOptions options,
            Action<T> handler,
            CancellationToken cancellationToken = default)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return SubscribeQueueOnMainThreadAsync(
                context,
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
