using System;
using System.Collections.Generic;
using PullSub.Bridge;
using PullSub.Core;
using UnityEditor;
using UnityEngine;

namespace PullSub.Editor
{
    public sealed class PullSubDebugMonitorWindow : EditorWindow
    {
        private const double RefreshIntervalSeconds = 0.3;
        private const double RemovedTopicRetentionSeconds = 10.0;
        private const double LatencyWarningThresholdMs = 1000.0;

        private static readonly Color WarningColor = new Color(1f, 0.72f, 0.28f);

        private readonly struct RemovedTopicEvent
        {
            public RemovedTopicEvent(string topic, DateTime removedAtUtc)
            {
                Topic = topic;
                RemovedAtUtc = removedAtUtc;
            }

            public string Topic { get; }
            public DateTime RemovedAtUtc { get; }
        }

        private sealed class TopicRateState
        {
            public bool IsInitialized;
            public long LastReceiveCount;
            public double LastSampleTime;
            public double RatePerSecond;
        }

        private readonly struct ContextDebugView
        {
            public ContextDebugView(
                PullSubContext context,
                PullSubContext.ContextDebugSnapshot snapshot,
                MonoBehaviour owner)
            {
                Context = context;
                Snapshot = snapshot;
                Owner = owner;
            }

            public PullSubContext Context { get; }
            public PullSubContext.ContextDebugSnapshot Snapshot { get; }
            public MonoBehaviour Owner { get; }
        }

        private readonly struct ClientDebugView
        {
            public ClientDebugView(
                PullSubMqttClient client,
                PullSubRuntime runtime,
                PullSubRuntimeDebugSnapshot snapshot,
                PullSubRuntimeTopicDebugSnapshot[] topics,
                ContextDebugView[] contexts)
            {
                Client = client;
                Runtime = runtime;
                Snapshot = snapshot;
                Topics = topics;
                Contexts = contexts;
            }

            public PullSubMqttClient Client { get; }
            public PullSubRuntime Runtime { get; }
            public PullSubRuntimeDebugSnapshot Snapshot { get; }
            public PullSubRuntimeTopicDebugSnapshot[] Topics { get; }
            public ContextDebugView[] Contexts { get; }
        }

        private readonly List<ClientDebugView> _views = new List<ClientDebugView>();
        private readonly Dictionary<int, HashSet<string>> _previousTopicsByClientId =
            new Dictionary<int, HashSet<string>>();

        private readonly Dictionary<int, HashSet<string>> _topicScratchByClientId =
            new Dictionary<int, HashSet<string>>();

        private readonly Dictionary<int, List<RemovedTopicEvent>> _recentlyRemovedByClientId =
            new Dictionary<int, List<RemovedTopicEvent>>();

        private readonly Dictionary<int, Dictionary<string, TopicRateState>> _rateStateByClientId =
            new Dictionary<int, Dictionary<string, TopicRateState>>();

        private readonly List<string> _topicKeyRemovalScratch = new List<string>();

        private Vector2 _scroll;
        private double _lastRefreshAt;
        private bool _isDirty = true;

        [MenuItem("Tools/PullSub/Debug Monitor")]
        public static void Open()
        {
            var window = GetWindow<PullSubDebugMonitorWindow>("PullSub Debug Monitor");
            window.minSize = new Vector2(640f, 300f);
            window.Show();
        }

        private void OnEnable()
        {
            _lastRefreshAt = EditorApplication.timeSinceStartup;
            _isDirty = true;
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate()
        {
            var now = EditorApplication.timeSinceStartup;
            if (!_isDirty && now - _lastRefreshAt < RefreshIntervalSeconds)
                return;

            _lastRefreshAt = now;
            try
            {
                RefreshViews(now);
                Repaint();
            }
            catch (MissingReferenceException)
            {
                // Scene objects can be destroyed while leaving play mode; retry on next update.
                _isDirty = true;
            }
        }

        private void OnGUI()
        {
            if (_isDirty)
            {
                try
                {
                    RefreshViews(EditorApplication.timeSinceStartup);
                }
                catch (MissingReferenceException)
                {
                    _isDirty = true;
                    return;
                }
            }

            EditorGUILayout.LabelField("PullSub Debug Monitor", EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);

            if (_views.Count == 0)
            {
                EditorGUILayout.HelpBox("No PullSubMqttClient found in open scenes.", MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            try
            {
                foreach (var view in _views)
                {
                    try
                    {
                        DrawClientBlock(view);
                        EditorGUILayout.Space(8f);
                    }
                    catch (MissingReferenceException)
                    {
                        _isDirty = true;
                    }
                }
            }
            finally
            {
                EditorGUILayout.EndScrollView();
            }
        }

        private void RefreshViews(double now)
        {
            var clients = FindObjectsByType<PullSubMqttClient>(FindObjectsSortMode.None);
            Array.Sort(clients, CompareClients);

            CleanupStaleClientCaches(clients);

            _views.Clear();
            for (var i = 0; i < clients.Length; i++)
            {
                var client = clients[i];
                if (client == null)
                    continue;

                var runtime = client.Runtime;
                if (runtime == null)
                {
                    CleanupClientCaches(client.GetInstanceID());
                    _views.Add(new ClientDebugView(
                        client,
                        null,
                        default,
                        Array.Empty<PullSubRuntimeTopicDebugSnapshot>(),
                        Array.Empty<ContextDebugView>()));
                    continue;
                }

                var snapshot = runtime.GetDebugSnapshot();
                var topics = CopyAndSortTopics(snapshot.Topics);

                UpdateRemovedTopicHistory(client.GetInstanceID(), topics);
                UpdateTopicRates(client.GetInstanceID(), topics, now);

                var contexts = BuildContextViews(runtime);
                _views.Add(new ClientDebugView(client, runtime, snapshot, topics, contexts));
            }

            _isDirty = false;
        }

        private static int CompareClients(PullSubMqttClient a, PullSubMqttClient b)
        {
            if (ReferenceEquals(a, b))
                return 0;

            if (a == null)
                return 1;

            if (b == null)
                return -1;

            var aSceneName = TryGetSceneName(a);
            var bSceneName = TryGetSceneName(b);

            var sceneCompare = StringComparer.Ordinal.Compare(aSceneName, bSceneName);
            if (sceneCompare != 0)
                return sceneCompare;

            var aName = TryGetGameObjectName(a);
            var bName = TryGetGameObjectName(b);
            return StringComparer.Ordinal.Compare(aName, bName);
        }

        private static PullSubRuntimeTopicDebugSnapshot[] CopyAndSortTopics(PullSubRuntimeTopicDebugSnapshot[] source)
        {
            if (source == null || source.Length == 0)
                return Array.Empty<PullSubRuntimeTopicDebugSnapshot>();

            var topics = new PullSubRuntimeTopicDebugSnapshot[source.Length];
            Array.Copy(source, topics, source.Length);
            Array.Sort(topics, static (a, b) => StringComparer.Ordinal.Compare(a.Topic, b.Topic));
            return topics;
        }

        private static ContextDebugView[] BuildContextViews(PullSubRuntime runtime)
        {
            var contexts = PullSubContextDebugTracker.Snapshot(runtime);
            if (contexts.Length == 0)
                return Array.Empty<ContextDebugView>();

            var result = new ContextDebugView[contexts.Length];
            for (var i = 0; i < contexts.Length; i++)
            {
                var context = contexts[i];
                var snapshot = context.GetDebugSnapshot();
                PullSubContextOwnerDebugRegistry.TryGetOwner(context, out var owner);
                result[i] = new ContextDebugView(context, snapshot, owner);
            }

            Array.Sort(result, CompareContextViews);
            return result;
        }

        private static int CompareContextViews(ContextDebugView a, ContextDebugView b)
        {
            var left = string.IsNullOrWhiteSpace(a.Snapshot.DebugLabel) ? "(no label)" : a.Snapshot.DebugLabel;
            var right = string.IsNullOrWhiteSpace(b.Snapshot.DebugLabel) ? "(no label)" : b.Snapshot.DebugLabel;

            var compare = StringComparer.Ordinal.Compare(left, right);
            if (compare != 0)
                return compare;

            return a.Context.GetHashCode().CompareTo(b.Context.GetHashCode());
        }

        private void UpdateTopicRates(int clientId, PullSubRuntimeTopicDebugSnapshot[] topics, double now)
        {
            if (!_rateStateByClientId.TryGetValue(clientId, out var states))
            {
                states = new Dictionary<string, TopicRateState>(StringComparer.Ordinal);
                _rateStateByClientId[clientId] = states;
            }

            var currentTopics = GetOrCreateTopicScratch(clientId);
            currentTopics.Clear();

            for (var i = 0; i < topics.Length; i++)
            {
                var topic = topics[i];
                currentTopics.Add(topic.Topic);

                if (topic.DataSubCount <= 0)
                    continue;

                if (!states.TryGetValue(topic.Topic, out var state))
                {
                    state = new TopicRateState();
                    states[topic.Topic] = state;
                }

                if (state.IsInitialized)
                {
                    var elapsed = now - state.LastSampleTime;
                    var delta = topic.DataReceiveCount - state.LastReceiveCount;
                    if (elapsed > 0 && delta >= 0)
                        state.RatePerSecond = delta / elapsed;
                    else
                        state.RatePerSecond = 0;
                }
                else
                {
                    state.IsInitialized = true;
                    state.RatePerSecond = 0;
                }

                state.LastReceiveCount = topic.DataReceiveCount;
                state.LastSampleTime = now;
            }

            _topicKeyRemovalScratch.Clear();
            foreach (var key in states.Keys)
            {
                if (!currentTopics.Contains(key))
                    _topicKeyRemovalScratch.Add(key);
            }

            for (var i = 0; i < _topicKeyRemovalScratch.Count; i++)
                states.Remove(_topicKeyRemovalScratch[i]);
        }

        private HashSet<string> GetOrCreateTopicScratch(int clientId)
        {
            if (!_topicScratchByClientId.TryGetValue(clientId, out var topics))
            {
                topics = new HashSet<string>(StringComparer.Ordinal);
                _topicScratchByClientId[clientId] = topics;
            }

            return topics;
        }

        private void CleanupStaleClientCaches(PullSubMqttClient[] clients)
        {
            var aliveIds = new HashSet<int>();
            for (var i = 0; i < clients.Length; i++)
            {
                var client = clients[i];
                if (client != null)
                    aliveIds.Add(client.GetInstanceID());
            }

            CleanupDictionaryKeys(_previousTopicsByClientId, aliveIds);
            CleanupDictionaryKeys(_topicScratchByClientId, aliveIds);
            CleanupDictionaryKeys(_recentlyRemovedByClientId, aliveIds);
            CleanupDictionaryKeys(_rateStateByClientId, aliveIds);
        }

        private static void CleanupDictionaryKeys<TValue>(Dictionary<int, TValue> source, HashSet<int> aliveIds)
        {
            if (source.Count == 0)
                return;

            var deadKeys = new List<int>();
            foreach (var key in source.Keys)
            {
                if (!aliveIds.Contains(key))
                    deadKeys.Add(key);
            }

            for (var i = 0; i < deadKeys.Count; i++)
                source.Remove(deadKeys[i]);
        }

        private void UpdateRemovedTopicHistory(int clientId, PullSubRuntimeTopicDebugSnapshot[] topics)
        {
            if (!_previousTopicsByClientId.TryGetValue(clientId, out var previousTopics))
            {
                previousTopics = new HashSet<string>(StringComparer.Ordinal);
                _previousTopicsByClientId[clientId] = previousTopics;
            }

            var currentTopics = GetOrCreateTopicScratch(clientId);
            currentTopics.Clear();
            for (var i = 0; i < topics.Length; i++)
                currentTopics.Add(topics[i].Topic);

            var now = DateTime.UtcNow;
            foreach (var topic in previousTopics)
            {
                if (currentTopics.Contains(topic))
                    continue;

                if (!_recentlyRemovedByClientId.TryGetValue(clientId, out var events))
                {
                    events = new List<RemovedTopicEvent>();
                    _recentlyRemovedByClientId[clientId] = events;
                }

                events.Add(new RemovedTopicEvent(topic, now));
            }

            previousTopics.Clear();
            previousTopics.UnionWith(currentTopics);

            if (_recentlyRemovedByClientId.TryGetValue(clientId, out var list))
            {
                list.RemoveAll(x => (now - x.RemovedAtUtc).TotalSeconds > RemovedTopicRetentionSeconds);
                if (list.Count == 0)
                    _recentlyRemovedByClientId.Remove(clientId);
            }
        }

        private void CleanupClientCaches(int clientId)
        {
            _previousTopicsByClientId.Remove(clientId);
            _topicScratchByClientId.Remove(clientId);
            _recentlyRemovedByClientId.Remove(clientId);
            _rateStateByClientId.Remove(clientId);
        }

        private void DrawClientBlock(ClientDebugView view)
        {
            var client = view.Client;
            if (!TryGetGameObject(client, out var clientGameObject))
            {
                _isDirty = true;
                return;
            }

            var runtime = view.Runtime;
            var connection = GetConnectionSummary(client);
            var label = $"{clientGameObject.name} ({connection.Host}:{connection.Port})";

            using (new EditorGUILayout.VerticalScope("box"))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
                    if (GUILayout.Button("Select", GUILayout.Width(64f)) && clientGameObject != null)
                    {
                        Selection.activeObject = clientGameObject;
                        EditorGUIUtility.PingObject(clientGameObject);
                    }
                }

                if (runtime == null)
                {
                    EditorGUILayout.LabelField($"Scene: {clientGameObject.scene.name}");
                    EditorGUILayout.LabelField($"ClientId: {connection.ClientIdText} | Policy: {connection.ClientIdPolicy}");
                    EditorGUILayout.HelpBox("Runtime is not initialized yet.", MessageType.Warning);
                    return;
                }

                var snapshot = view.Snapshot;
                var status = snapshot.IsConnected ? "Connected" : "Disconnected";

                EditorGUILayout.LabelField("Overview", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"Scene: {clientGameObject.scene.name}");
                EditorGUILayout.LabelField($"State: {snapshot.State} | {status} | Ready: {(snapshot.IsReady ? "Yes" : "No")}");
                DrawRuntimeMetaSection(snapshot);

                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("Connection", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"ClientId: {connection.ClientIdText} | Policy: {connection.ClientIdPolicy}");
                DrawReconnectSection(snapshot);
                DrawRequestSection(snapshot.Request, snapshot.InboundOversizeDropCount);

                DrawContextSection(client.GetInstanceID(), snapshot.CapturedAtUtc, view.Topics, view.Contexts);

                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("Topics", EditorStyles.boldLabel);
                DrawTopicTable(client.GetInstanceID(), snapshot.CapturedAtUtc, view.Topics);
                DrawRemovedTopicSection(client.GetInstanceID());
            }
        }

        private static void DrawRuntimeMetaSection(PullSubRuntimeDebugSnapshot snapshot)
        {
            var snapshotAgeMs = Math.Max(0, (DateTime.UtcNow - snapshot.CapturedAtUtc).TotalMilliseconds);
            var line =
                $"Runtime: Started={(snapshot.IsStarted ? "Yes" : "No")}, CapturedAtUtc={snapshot.CapturedAtUtc:O}, SnapshotAge={snapshotAgeMs:F0}ms, QueueHandlerDiagnostics={(snapshot.HasQueueHandlerDiagnostics ? "Enabled" : "Disabled")}";

            if (!snapshot.HasQueueHandlerDiagnostics)
                DrawWarningLabel(line);
            else
                EditorGUILayout.LabelField(line);
        }

        private static void DrawReconnectSection(PullSubRuntimeDebugSnapshot snapshot)
        {
            var nextRetryInMs = snapshot.ReconnectNextRetryAtUtc == default
                ? 0
                : Math.Max(0, (snapshot.ReconnectNextRetryAtUtc - snapshot.CapturedAtUtc).TotalMilliseconds);

            var reason = string.IsNullOrWhiteSpace(snapshot.ReconnectLastFailureReason)
                ? "-"
                : snapshot.ReconnectLastFailureReason;

            var line =
                $"Reconnect: Attempts={snapshot.ReconnectAttemptCount}, CurrentDelay={snapshot.ReconnectCurrentDelay.TotalMilliseconds:F0}ms, NextRetryIn={nextRetryInMs:F0}ms";

            if (snapshot.State == PullSubState.Reconnecting || snapshot.State == PullSubState.Disconnected)
                DrawWarningLabel(line);
            else
                EditorGUILayout.LabelField(line);

            EditorGUILayout.LabelField($"  LastFailure: {reason}");
        }

        private static void DrawRequestSection(PullSubPendingRequestStoreDebugSnapshot request, long inboundOversizeDropCount)
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Request/Reply", EditorStyles.boldLabel);

            EditorGUILayout.LabelField(
                $"  Pending: {request.PendingCount} | ReplyInboxSubscribed: {(request.IsReplyInboxSubscribed ? "Yes" : "No")}");

            var failures =
                $"  Failures: Timeout={request.TimeoutCount}, Publish={request.PublishFailureCount}, Setup={request.SetupFailureCount}, InvalidReplyTo={request.InvalidReplyToDropCount}, ConnectionLost={request.ConnectionLostFailureCount}, RuntimeDisposed={request.RuntimeDisposedFailureCount}, DuplicateDiscard={request.DuplicateDiscardCount}";

            if (HasRequestWarnings(request))
                DrawWarningLabel(failures);
            else
                EditorGUILayout.LabelField(failures);

            if (inboundOversizeDropCount > 0)
                DrawWarningLabel($"  InboundOversizeDropped={inboundOversizeDropCount}");
            else
                EditorGUILayout.LabelField("  InboundOversizeDropped=0");
        }

        private static bool HasRequestWarnings(PullSubPendingRequestStoreDebugSnapshot request)
        {
            return request.TimeoutCount > 0
                || request.DuplicateDiscardCount > 0
                || request.SetupFailureCount > 0
                || request.PublishFailureCount > 0
                || request.InvalidReplyToDropCount > 0
                || request.ConnectionLostFailureCount > 0
                || request.RuntimeDisposedFailureCount > 0
                || (request.PendingCount > 0 && !request.IsReplyInboxSubscribed);
        }

        private void DrawTopicTable(int clientId, DateTime capturedAtUtc, PullSubRuntimeTopicDebugSnapshot[] topics)
        {
            if (topics.Length == 0)
            {
                EditorGUILayout.HelpBox("No active topics.", MessageType.Info);
                return;
            }

            for (var i = 0; i < topics.Length; i++)
            {
                var topic = topics[i];
                var kind = GetKindLabel(topic);
                var warnLatency = false;
                var dataState = topic.DataSubCount > 0
                    ? BuildDataState(clientId, topic, capturedAtUtc, out warnLatency)
                    : "-";

                var queueState = topic.QueueSubCount > 0
                    ? BuildQueueState(topic, capturedAtUtc)
                    : "-";

                EditorGUILayout.LabelField(
                    $"{kind} {topic.Topic} | Subs(Data={topic.DataSubCount}, Queue={topic.QueueSubCount})");
                var details =
                    $"    Data: {dataState} | Queue: {queueState} | ReceiveCount: {topic.DataReceiveCount} | QoS: {topic.SubscribeQos}";

                if (topic.DataSubCount > 0 && warnLatency)
                    DrawWarningLabel(details);
                else
                    EditorGUILayout.LabelField(details);
            }
        }

        private void DrawContextSection(
            int clientId,
            DateTime capturedAtUtc,
            PullSubRuntimeTopicDebugSnapshot[] topics,
            ContextDebugView[] contexts)
        {
            if (contexts.Length == 0)
                return;

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Contexts", EditorStyles.boldLabel);

            for (var i = 0; i < contexts.Length; i++)
            {
                var contextView = contexts[i];
                var label = string.IsNullOrWhiteSpace(contextView.Snapshot.DebugLabel)
                    ? "(no label)"
                    : contextView.Snapshot.DebugLabel;

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(label, EditorStyles.linkLabel))
                    {
                        if (TryGetGameObject(contextView.Owner, out var ownerGameObject))
                        {
                            Selection.activeObject = ownerGameObject;
                            EditorGUIUtility.PingObject(ownerGameObject);
                        }
                    }

                    EditorGUILayout.LabelField(
                        $"Subscriptions: {contextView.Snapshot.Subscriptions.Length}",
                        GUILayout.Width(160f));
                }

                for (var subIndex = 0; subIndex < contextView.Snapshot.Subscriptions.Length; subIndex++)
                {
                    var sub = contextView.Snapshot.Subscriptions[subIndex];
                    var topicStateText = "(inactive)";
                    var warnLatency = false;

                    if (TryFindTopic(topics, sub.Topic, out var topicState))
                    {
                        if (sub.IsQueue)
                        {
                            topicStateText = BuildQueueState(topicState, capturedAtUtc);
                        }
                        else
                        {
                            topicStateText = BuildDataState(clientId, topicState, capturedAtUtc, out warnLatency);
                        }
                    }

                    var line = sub.IsQueue
                        ? $"    [Queue] {sub.Topic} | {topicStateText}"
                        : $"    [Data] {sub.Topic} | {topicStateText}";

                    if (!sub.IsQueue && warnLatency)
                        DrawWarningLabel(line);
                    else
                        EditorGUILayout.LabelField(line);
                }
            }
        }

        private static bool TryFindTopic(
            PullSubRuntimeTopicDebugSnapshot[] topics,
            string topic,
            out PullSubRuntimeTopicDebugSnapshot topicState)
        {
            for (var i = 0; i < topics.Length; i++)
            {
                if (StringComparer.Ordinal.Equals(topics[i].Topic, topic))
                {
                    topicState = topics[i];
                    return true;
                }
            }

            topicState = default;
            return false;
        }

        private void DrawRemovedTopicSection(int clientId)
        {
            if (!_recentlyRemovedByClientId.TryGetValue(clientId, out var events) || events.Count == 0)
                return;

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Recently Removed (last 10s)", EditorStyles.boldLabel);

            var now = DateTime.UtcNow;
            for (var i = 0; i < events.Count; i++)
            {
                var ageMs = Math.Max(0, (now - events[i].RemovedAtUtc).TotalMilliseconds);
                EditorGUILayout.LabelField($"  {events[i].Topic} (removed {ageMs:F0}ms ago)");
            }
        }

        private string BuildDataState(
            int clientId,
            PullSubRuntimeTopicDebugSnapshot topic,
            DateTime capturedAtUtc,
            out bool warnLatency)
        {
            if (!TryGetTopicRate(clientId, topic.Topic, out var ratePerSecond))
                ratePerSecond = 0;

            if (!topic.HasValue)
            {
                warnLatency = false;
                return $"HasValue: No (waiting), {ratePerSecond:F1} msg/s";
            }

            var latencyMs = Math.Max(0, (capturedAtUtc - topic.LatestValueTimestampUtc).TotalMilliseconds);
            warnLatency = latencyMs >= LatencyWarningThresholdMs;

            return $"HasValue: Yes, Last: {latencyMs:F0}ms ago, Latency: {latencyMs:F0}ms, {ratePerSecond:F1} msg/s";
        }

        private static string BuildQueueState(PullSubRuntimeTopicDebugSnapshot topic, DateTime capturedAtUtc)
        {
            var status = $"Dropped: {topic.QueueDroppedCount}, ActiveHandlers: {topic.ActiveQueueHandlers}";
            if (topic.FaultedQueueHandlers <= 0)
                return status;

            var ageMs = Math.Max(0, (capturedAtUtc - topic.LastQueueHandlerFaultUtc).TotalMilliseconds);
            return $"{status}, FaultedHandlers: {topic.FaultedQueueHandlers}, LastFault: {ageMs:F0}ms ago";
        }

        private bool TryGetTopicRate(int clientId, string topic, out double ratePerSecond)
        {
            if (_rateStateByClientId.TryGetValue(clientId, out var rates)
                && rates.TryGetValue(topic, out var state))
            {
                ratePerSecond = state.RatePerSecond;
                return true;
            }

            ratePerSecond = 0;
            return false;
        }

        private static string GetKindLabel(PullSubRuntimeTopicDebugSnapshot topic)
        {
            if (topic.DataSubCount > 0 && topic.QueueSubCount > 0)
                return "[Data+Queue]";

            if (topic.DataSubCount > 0)
                return "[Data]";

            return "[Queue]";
        }

        private static void DrawWarningLabel(string text)
        {
            var previousColor = GUI.contentColor;
            GUI.contentColor = WarningColor;
            EditorGUILayout.LabelField(text);
            GUI.contentColor = previousColor;
        }

        private static (string Host, int Port, string ClientIdPolicy, string ClientIdText) GetConnectionSummary(
            PullSubMqttClient client)
        {
            if (client == null)
                return ("unknown", 0, "Unknown", "(auto)");

            try
            {
                return client.GetConnectionSummaryForDebugMonitor();
            }
            catch (MissingReferenceException)
            {
                return ("unknown", 0, "Unknown", "(auto)");
            }
        }

        private static string TryGetSceneName(PullSubMqttClient client)
        {
            return TryGetGameObject(client, out var gameObject)
                ? gameObject.scene.name
                : string.Empty;
        }

        private static string TryGetGameObjectName(PullSubMqttClient client)
        {
            return TryGetGameObject(client, out var gameObject)
                ? gameObject.name
                : string.Empty;
        }

        private static bool TryGetGameObject(Component component, out GameObject gameObject)
        {
            gameObject = null;
            if (component == null)
                return false;

            try
            {
                gameObject = component.gameObject;
                return gameObject != null;
            }
            catch (MissingReferenceException)
            {
                return false;
            }
        }
    }
}
