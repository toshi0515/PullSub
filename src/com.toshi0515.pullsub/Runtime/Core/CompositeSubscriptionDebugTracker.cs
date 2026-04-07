using System;
using System.Collections.Generic;

namespace PullSub.Core
{
    internal static class CompositeSubscriptionDebugTracker
    {
#if UNITY_EDITOR
        private static readonly object Gate = new object();
        private static readonly Dictionary<PullSubRuntime, HashSet<CompositeSubscription>> RuntimeContexts =
            new Dictionary<PullSubRuntime, HashSet<CompositeSubscription>>();
#endif

        public static void Register(PullSubRuntime runtime, CompositeSubscription sub)
        {
#if UNITY_EDITOR
            if (runtime == null || sub == null)
                return;

            lock (Gate)
            {
                if (!RuntimeContexts.TryGetValue(runtime, out var subs))
                {
                    subs = new HashSet<CompositeSubscription>();
                    RuntimeContexts[runtime] = subs;
                }

                subs.Add(sub);
            }
#endif
        }

        public static void Unregister(PullSubRuntime runtime, CompositeSubscription sub)
        {
#if UNITY_EDITOR
            if (runtime == null || sub == null)
                return;

            lock (Gate)
            {
                if (!RuntimeContexts.TryGetValue(runtime, out var subs))
                    return;

                subs.Remove(sub);
                if (subs.Count == 0)
                    RuntimeContexts.Remove(runtime);
            }
#endif
        }

        public static CompositeSubscription[] Snapshot(PullSubRuntime runtime)
        {
#if UNITY_EDITOR
            if (runtime == null)
                return Array.Empty<CompositeSubscription>();

            lock (Gate)
            {
                if (!RuntimeContexts.TryGetValue(runtime, out var subs) || subs.Count == 0)
                    return Array.Empty<CompositeSubscription>();

                var result = new CompositeSubscription[subs.Count];
                subs.CopyTo(result);
                return result;
            }
#else
            return Array.Empty<CompositeSubscription>();
#endif
        }

        public static void OnRuntimeDisposed(PullSubRuntime runtime)
        {
#if UNITY_EDITOR
            if (runtime == null)
                return;

            lock (Gate)
            {
                RuntimeContexts.Remove(runtime);
            }
#endif
        }
    }
}