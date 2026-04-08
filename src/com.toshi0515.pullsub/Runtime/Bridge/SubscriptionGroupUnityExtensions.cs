using System;
using System.Threading;
using PullSub.Core;
using UnityEngine;

namespace PullSub.Bridge
{
    public static class SubscriptionGroupUnityExtensions
    {
        public static SubscriptionGroup AddTo(
            this SubscriptionGroup group,
            MonoBehaviour behaviour)
        {
            if (group == null)
                throw new ArgumentNullException(nameof(group));

            if (behaviour == null)
                throw new ArgumentNullException(nameof(behaviour));

            behaviour.destroyCancellationToken.Register(group.Dispose);
#if UNITY_EDITOR
            group.SetDebugLabel(behaviour.gameObject != null ? behaviour.gameObject.name : behaviour.name);
            SubscriptionGroupOwnerDebugRegistry.Register(group, behaviour);
            behaviour.destroyCancellationToken.Register(() => SubscriptionGroupOwnerDebugRegistry.Unregister(group));
#endif
            return group;
        }

        public static SubscriptionGroup AddTo(
            this SubscriptionGroup group,
            CancellationToken cancellationToken)
        {
            if (group == null)
                throw new ArgumentNullException(nameof(group));

            cancellationToken.Register(group.Dispose);
            return group;
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