using System;
using System.Threading;
using PullSub.Core;
using UnityEngine;

namespace PullSub.Bridge
{
    public static class PullSubContextUnityExtensions
    {
        public static PullSubContext AddTo(
            this PullSubContext context,
            MonoBehaviour behaviour)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            if (behaviour == null)
                throw new ArgumentNullException(nameof(behaviour));

            behaviour.destroyCancellationToken.Register(context.Dispose);
            return context;
        }

        public static PullSubContext AddTo(
            this PullSubContext context,
            CancellationToken cancellationToken)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            cancellationToken.Register(context.Dispose);
            return context;
        }

        public static PullSubSubscription<T> AddTo<T>(
            this PullSubSubscription<T> subscription,
            MonoBehaviour behaviour)
        {
            if (subscription == null)
                throw new ArgumentNullException(nameof(subscription));

            if (behaviour == null)
                throw new ArgumentNullException(nameof(behaviour));

            behaviour.destroyCancellationToken.Register(subscription.Dispose);
            return subscription;
        }

        public static PullSubSubscription<T> AddTo<T>(
            this PullSubSubscription<T> subscription,
            CancellationToken cancellationToken)
        {
            if (subscription == null)
                throw new ArgumentNullException(nameof(subscription));

            cancellationToken.Register(subscription.Dispose);
            return subscription;
        }

        public static PullSubQueueHandlerRegistration AddTo(
            this PullSubQueueHandlerRegistration registration,
            MonoBehaviour behaviour)
        {
            if (registration == null)
                throw new ArgumentNullException(nameof(registration));

            if (behaviour == null)
                throw new ArgumentNullException(nameof(behaviour));

            behaviour.destroyCancellationToken.Register(registration.Dispose);
            return registration;
        }

        public static PullSubQueueHandlerRegistration AddTo(
            this PullSubQueueHandlerRegistration registration,
            CancellationToken cancellationToken)
        {
            if (registration == null)
                throw new ArgumentNullException(nameof(registration));

            cancellationToken.Register(registration.Dispose);
            return registration;
        }
    }
}