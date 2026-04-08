using System;
using System.Collections.Generic;

namespace PullSub.Core
{
    internal static class SubscriptionGroupDebugTracker
    {
#if UNITY_EDITOR
        private static readonly object Gate = new object();
        private static readonly Dictionary<PullSubRuntime, HashSet<SubscriptionGroup>> RuntimeGroups =
            new Dictionary<PullSubRuntime, HashSet<SubscriptionGroup>>();
#endif

        public static void Register(PullSubRuntime runtime, SubscriptionGroup sub)
        {
#if UNITY_EDITOR
            if (runtime == null || sub == null)
                return;

            lock (Gate)
            {
                if (!RuntimeGroups.TryGetValue(runtime, out var subs))
                {
                    subs = new HashSet<SubscriptionGroup>();
                    RuntimeGroups[runtime] = subs;
                }

                subs.Add(sub);
            }
#endif
        }

        public static void Unregister(PullSubRuntime runtime, SubscriptionGroup sub)
        {
#if UNITY_EDITOR
            if (runtime == null || sub == null)
                return;

            lock (Gate)
            {
                if (!RuntimeGroups.TryGetValue(runtime, out var subs))
                    return;

                subs.Remove(sub);
                if (subs.Count == 0)
                    RuntimeGroups.Remove(runtime);
            }
#endif
        }

        public static SubscriptionGroup[] Snapshot(PullSubRuntime runtime)
        {
#if UNITY_EDITOR
            if (runtime == null)
                return Array.Empty<SubscriptionGroup>();

            lock (Gate)
            {
                if (!RuntimeGroups.TryGetValue(runtime, out var subs) || subs.Count == 0)
                    return Array.Empty<SubscriptionGroup>();

                var result = new SubscriptionGroup[subs.Count];
                subs.CopyTo(result);
                return result;
            }
#else
            return Array.Empty<SubscriptionGroup>();
#endif
        }

        public static void OnRuntimeDisposed(PullSubRuntime runtime)
        {
#if UNITY_EDITOR
            if (runtime == null)
                return;

            lock (Gate)
            {
                RuntimeGroups.Remove(runtime);
            }
#endif
        }
    }
}