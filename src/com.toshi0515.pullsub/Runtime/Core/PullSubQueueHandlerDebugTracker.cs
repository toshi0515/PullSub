using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    internal readonly struct PullSubQueueHandlerDebugSnapshot
    {
        public PullSubQueueHandlerDebugSnapshot(int activeCount, int faultedCount, DateTime lastFaultUtc)
        {
            ActiveCount = activeCount;
            FaultedCount = faultedCount;
            LastFaultUtc = lastFaultUtc;
        }

        public int ActiveCount { get; }
        public int FaultedCount { get; }
        public DateTime LastFaultUtc { get; }
    }

    internal static class PullSubQueueHandlerDebugTracker
    {
        public static bool IsTrackingEnabled
        {
            get
            {
#if UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }

#if UNITY_EDITOR
        private sealed class TopicState
        {
            public int ActiveCount;
            public int FaultedCount;
            public DateTime LastFaultUtc;
        }

        private sealed class ContinuationState
        {
            public ContinuationState(PullSubRuntime runtime, string topic)
            {
                Runtime = runtime;
                Topic = topic;
            }

            public PullSubRuntime Runtime { get; }
            public string Topic { get; }
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<PullSubRuntime, Dictionary<string, TopicState>> RuntimeStates
            = new Dictionary<PullSubRuntime, Dictionary<string, TopicState>>();
#endif

        public static void Register(PullSubRuntime runtime, string topic, Task completion)
        {
#if UNITY_EDITOR
            if (runtime == null || string.IsNullOrWhiteSpace(topic) || completion == null)
                return;

            lock (Gate)
            {
                if (!RuntimeStates.TryGetValue(runtime, out var topics))
                {
                    topics = new Dictionary<string, TopicState>(StringComparer.Ordinal);
                    RuntimeStates[runtime] = topics;
                }

                if (!topics.TryGetValue(topic, out var state))
                {
                    state = new TopicState();
                    topics[topic] = state;
                }

                state.ActiveCount++;
            }

            _ = completion.ContinueWith(
                static (task, boxedState) =>
                {
                    var state = (ContinuationState)boxedState;
                    OnCompletion(state.Runtime, state.Topic, task);
                },
                new ContinuationState(runtime, topic),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
#endif
        }

        public static PullSubQueueHandlerDebugSnapshot GetSnapshot(PullSubRuntime runtime, string topic)
        {
#if UNITY_EDITOR
            if (runtime == null || string.IsNullOrWhiteSpace(topic))
                return default;

            lock (Gate)
            {
                if (!RuntimeStates.TryGetValue(runtime, out var topics))
                    return default;

                if (!topics.TryGetValue(topic, out var state))
                    return default;

                return new PullSubQueueHandlerDebugSnapshot(
                    state.ActiveCount,
                    state.FaultedCount,
                    state.LastFaultUtc);
            }
#else
            return default;
#endif
        }

        public static void OnRuntimeDisposed(PullSubRuntime runtime)
        {
#if UNITY_EDITOR
            if (runtime == null)
                return;

            lock (Gate)
            {
                RuntimeStates.Remove(runtime);
            }
#endif
        }

#if UNITY_EDITOR
        private static void OnCompletion(PullSubRuntime runtime, string topic, Task completion)
        {
            lock (Gate)
            {
                if (!RuntimeStates.TryGetValue(runtime, out var topics))
                    return;

                if (!topics.TryGetValue(topic, out var state))
                    return;

                if (state.ActiveCount > 0)
                    state.ActiveCount--;

                if (completion.IsFaulted)
                {
                    state.FaultedCount++;
                    state.LastFaultUtc = DateTime.UtcNow;
                }
            }
        }
#endif
    }
}