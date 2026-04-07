using System;
using System.Collections.Generic;
using PullSub.Core;
using UnityEngine;

namespace PullSub.Bridge
{
    internal static class CompositeSubscriptionDebugRegistry
    {
#if UNITY_EDITOR
        private static readonly object Gate = new object();
        private static readonly Dictionary<CompositeSubscription, WeakReference<MonoBehaviour>> Owners =
            new Dictionary<CompositeSubscription, WeakReference<MonoBehaviour>>();
#endif

        public static void Register(CompositeSubscription context, MonoBehaviour owner)
        {
#if UNITY_EDITOR
            if (context == null || owner == null)
                return;

            lock (Gate)
            {
                Owners[context] = new WeakReference<MonoBehaviour>(owner);
            }
#endif
        }

        public static void Unregister(CompositeSubscription context)
        {
#if UNITY_EDITOR
            if (context == null)
                return;

            lock (Gate)
            {
                Owners.Remove(context);
            }
#endif
        }

        public static bool TryGetOwner(CompositeSubscription context, out MonoBehaviour owner)
        {
#if UNITY_EDITOR
            if (context != null)
            {
                lock (Gate)
                {
                    if (Owners.TryGetValue(context, out var weak))
                    {
                        if (weak.TryGetTarget(out owner) && owner != null)
                            return true;

                        Owners.Remove(context);
                    }
                }
            }
#endif

            owner = null;
            return false;
        }
    }
}
