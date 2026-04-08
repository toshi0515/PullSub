using System;
using System.Collections.Generic;
using PullSub.Core;
using UnityEngine;

namespace PullSub.Bridge
{
    internal static class SubscriptionGroupOwnerDebugRegistry
    {
#if UNITY_EDITOR
        private static readonly object Gate = new object();
        private static readonly Dictionary<SubscriptionGroup, WeakReference<MonoBehaviour>> Owners =
            new Dictionary<SubscriptionGroup, WeakReference<MonoBehaviour>>();
#endif

        public static void Register(SubscriptionGroup group, MonoBehaviour owner)
        {
#if UNITY_EDITOR
            if (group == null || owner == null)
                return;

            lock (Gate)
            {
                Owners[group] = new WeakReference<MonoBehaviour>(owner);
            }
#endif
        }

        public static void Unregister(SubscriptionGroup group)
        {
#if UNITY_EDITOR
            if (group == null)
                return;

            lock (Gate)
            {
                Owners.Remove(group);
            }
#endif
        }

        public static bool TryGetOwner(SubscriptionGroup group, out MonoBehaviour owner)
        {
#if UNITY_EDITOR
            if (group != null)
            {
                lock (Gate)
                {
                    if (Owners.TryGetValue(group, out var weak))
                    {
                        if (weak.TryGetTarget(out owner) && owner != null)
                            return true;

                        Owners.Remove(group);
                    }
                }
            }
#endif

            owner = null;
            return false;
        }
    }
}
