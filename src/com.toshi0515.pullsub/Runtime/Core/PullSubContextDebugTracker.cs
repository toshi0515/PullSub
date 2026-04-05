using System;
using System.Collections.Generic;

namespace PullSub.Core
{
    internal static class PullSubContextDebugTracker
    {
#if UNITY_EDITOR
        private static readonly object Gate = new object();
        private static readonly Dictionary<PullSubRuntime, HashSet<PullSubContext>> RuntimeContexts =
            new Dictionary<PullSubRuntime, HashSet<PullSubContext>>();
#endif

        public static void Register(PullSubRuntime runtime, PullSubContext context)
        {
#if UNITY_EDITOR
            if (runtime == null || context == null)
                return;

            lock (Gate)
            {
                if (!RuntimeContexts.TryGetValue(runtime, out var contexts))
                {
                    contexts = new HashSet<PullSubContext>();
                    RuntimeContexts[runtime] = contexts;
                }

                contexts.Add(context);
            }
#endif
        }

        public static void Unregister(PullSubRuntime runtime, PullSubContext context)
        {
#if UNITY_EDITOR
            if (runtime == null || context == null)
                return;

            lock (Gate)
            {
                if (!RuntimeContexts.TryGetValue(runtime, out var contexts))
                    return;

                contexts.Remove(context);
                if (contexts.Count == 0)
                    RuntimeContexts.Remove(runtime);
            }
#endif
        }

        public static PullSubContext[] Snapshot(PullSubRuntime runtime)
        {
#if UNITY_EDITOR
            if (runtime == null)
                return Array.Empty<PullSubContext>();

            lock (Gate)
            {
                if (!RuntimeContexts.TryGetValue(runtime, out var contexts) || contexts.Count == 0)
                    return Array.Empty<PullSubContext>();

                var result = new PullSubContext[contexts.Count];
                contexts.CopyTo(result);
                return result;
            }
#else
            return Array.Empty<PullSubContext>();
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
