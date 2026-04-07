using System;
using System.Threading;
using PullSub.Core;
using UnityEngine;

namespace PullSub.Bridge
{
    public static class CompositeSubscriptionUnityExtensions
    {
        public static CompositeSubscription AddTo(
            this CompositeSubscription context,
            MonoBehaviour behaviour)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            if (behaviour == null)
                throw new ArgumentNullException(nameof(behaviour));

            behaviour.destroyCancellationToken.Register(context.Dispose);
#if UNITY_EDITOR
            context.SetDebugLabel(behaviour.gameObject != null ? behaviour.gameObject.name : behaviour.name);
            CompositeSubscriptionDebugRegistry.Register(context, behaviour);
            behaviour.destroyCancellationToken.Register(() => CompositeSubscriptionDebugRegistry.Unregister(context));
#endif
            return context;
        }

        public static CompositeSubscription AddTo(
            this CompositeSubscription context,
            CancellationToken cancellationToken)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            cancellationToken.Register(context.Dispose);
            return context;
        }

        public static DataSubscription<T> AddTo<T>(
            this DataSubscription<T> subscription,
            MonoBehaviour behaviour)
        {
            if (subscription == null)
                throw new ArgumentNullException(nameof(subscription));

            if (behaviour == null)
                throw new ArgumentNullException(nameof(behaviour));

            behaviour.destroyCancellationToken.Register(subscription.Dispose);
            return subscription;
        }

        public static DataSubscription<T> AddTo<T>(
            this DataSubscription<T> subscription,
            CancellationToken cancellationToken)
        {
            if (subscription == null)
                throw new ArgumentNullException(nameof(subscription));

            cancellationToken.Register(subscription.Dispose);
            return subscription;
        }

        public static QueueSubscription AddTo(
            this QueueSubscription registration,
            MonoBehaviour behaviour)
        {
            if (registration == null)
                throw new ArgumentNullException(nameof(registration));

            if (behaviour == null)
                throw new ArgumentNullException(nameof(behaviour));

            behaviour.destroyCancellationToken.Register(registration.Dispose);
            return registration;
        }

        public static QueueSubscription AddTo(
            this QueueSubscription registration,
            CancellationToken cancellationToken)
        {
            if (registration == null)
                throw new ArgumentNullException(nameof(registration));

            cancellationToken.Register(registration.Dispose);
            return registration;
        }
    }
}