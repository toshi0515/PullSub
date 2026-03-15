using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UnityMqtt.V2.Core
{
    public enum MqttV2TopicUpdateMode
    {
        Differential = 0,
        Replace = 1
    }

    public sealed class MqttV2DataEntry
    {
        public MqttV2DataEntry(object value, DateTime sourceTimestampUtc, DateTime receivedUtc)
        {
            Value = value;
            SourceTimestampUtc = sourceTimestampUtc;
            ReceivedUtc = receivedUtc;
        }

        public object Value { get; }
        public DateTime SourceTimestampUtc { get; }
        public DateTime ReceivedUtc { get; }

        public bool TryGetValue<T>(out T result)
        {
            return MqttV2ObjectMapper.TryConvert(Value, out result);
        }
    }

    internal sealed class MqttV2TopicUpdateModeStore
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, MqttV2TopicUpdateMode> _modes = new Dictionary<string, MqttV2TopicUpdateMode>(StringComparer.Ordinal);

        public MqttV2TopicUpdateMode DefaultMode { get; set; } = MqttV2TopicUpdateMode.Differential;

        public MqttV2TopicUpdateMode GetMode(string topic)
        {
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("topic is required.", nameof(topic));

            lock (_gate)
            {
                if (_modes.TryGetValue(topic, out var mode))
                    return mode;

                return DefaultMode;
            }
        }

        public void SetMode(string topic, MqttV2TopicUpdateMode mode)
        {
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("topic is required.", nameof(topic));

            lock (_gate)
            {
                _modes[topic] = mode;
            }
        }
    }

    public sealed class MqttV2DataCache
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, Dictionary<string, MqttV2DataEntry>> _topicMaps
            = new Dictionary<string, Dictionary<string, MqttV2DataEntry>>(StringComparer.Ordinal);
        // Waiters for "first snapshot arrival" per topic. One signal wakes all waiters.
        private readonly Dictionary<string, TaskCompletionSource<bool>> _firstDataSignals
            = new Dictionary<string, TaskCompletionSource<bool>>(StringComparer.Ordinal);

        public void ApplyFields(string topic, IReadOnlyDictionary<string, object> fields, DateTime sourceTimestampUtc, MqttV2TopicUpdateMode mode)
        {
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("topic is required.", nameof(topic));

            if (fields == null)
                throw new ArgumentNullException(nameof(fields));

            var normalizedTimestampUtc = NormalizeSourceTimestampOrNow(sourceTimestampUtc);
            var receivedUtc = DateTime.UtcNow;

            TaskCompletionSource<bool> firstDataSignal = null;
            lock (_gate)
            {
                if (mode == MqttV2TopicUpdateMode.Replace)
                    ApplyReplaceNoLock(topic, fields, normalizedTimestampUtc, receivedUtc);
                else
                    ApplyDifferentialNoLock(topic, fields, normalizedTimestampUtc, receivedUtc);

                if (HasSnapshotNoLock(topic) && _firstDataSignals.TryGetValue(topic, out firstDataSignal))
                    _firstDataSignals.Remove(topic);
            }

            firstDataSignal?.TrySetResult(true);
        }

        public bool TryGetSnapshot(string topic, out IReadOnlyDictionary<string, MqttV2DataEntry> snapshot)
        {
            snapshot = null;
            if (string.IsNullOrWhiteSpace(topic))
                return false;

            lock (_gate)
            {
                if (!_topicMaps.TryGetValue(topic, out var current))
                    return false;

                snapshot = CloneSnapshot(current);
                return true;
            }
        }

        /// <summary>
        /// 指定トピックの最初のスナップショットが届くまで待機する。
        /// 既にデータが存在する場合は即座に返す。
        /// 「次の更新」を待つ用途には使用できない。
        /// </summary>
        public async Task<IReadOnlyDictionary<string, MqttV2DataEntry>> WaitForSnapshotAsync(
            string topic,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("topic is required.", nameof(topic));

            while (true)
            {
                Task waitTask;
                lock (_gate)
                {
                    if (_topicMaps.TryGetValue(topic, out var current))
                        return CloneSnapshot(current);

                    if (!_firstDataSignals.TryGetValue(topic, out var firstDataSignal))
                    {
                        firstDataSignal = CreateFirstDataSignal();
                        _firstDataSignals[topic] = firstDataSignal;
                    }

                    waitTask = firstDataSignal.Task;
                }

                await MqttV2AsyncUtils.AwaitWithCancellation(waitTask, cancellationToken);
            }
        }

        public void Clear()
        {
            List<TaskCompletionSource<bool>> firstDataSignals;
            lock (_gate)
            {
                _topicMaps.Clear();

                firstDataSignals = new List<TaskCompletionSource<bool>>(_firstDataSignals.Values);
                _firstDataSignals.Clear();
            }

            foreach (var signal in firstDataSignals)
                signal.TrySetCanceled();
        }

        public bool TryGetValue<T>(string topic, string key, out T value)
        {
            value = default(T);
            if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(key))
                return false;

            lock (_gate)
            {
                if (!_topicMaps.TryGetValue(topic, out var current))
                    return false;

                if (!current.TryGetValue(key, out var entry) || entry == null)
                    return false;

                return entry.TryGetValue(out value);
            }
        }

        private void ApplyReplaceNoLock(
            string topic,
            IReadOnlyDictionary<string, object> fields,
            DateTime sourceTimestampUtc,
            DateTime receivedUtc)
        {
            var replaced = BuildMap(fields, sourceTimestampUtc, receivedUtc);

            if (!_topicMaps.TryGetValue(topic, out var current))
            {
                if (replaced.Count == 0)
                    return;

                _topicMaps[topic] = replaced;
                return;
            }

            var changed = current.Count != replaced.Count;
            if (!changed)
            {
                foreach (var pair in replaced)
                {
                    if (!current.TryGetValue(pair.Key, out var currentEntry) || !AreValuesEqual(currentEntry?.Value, pair.Value.Value))
                    {
                        changed = true;
                        break;
                    }
                }
            }

            if (!changed)
            {
                // Value-equivalent replace still refreshes timing metadata for delay evaluation.
                // This intentionally keeps the code simple at the cost of dictionary allocation
                // on high-frequency unchanged replace traffic.
                _topicMaps[topic] = replaced;
                return;
            }

            if (replaced.Count == 0)
                _topicMaps.Remove(topic);
            else
                _topicMaps[topic] = replaced;
        }

        private void ApplyDifferentialNoLock(
            string topic,
            IReadOnlyDictionary<string, object> fields,
            DateTime sourceTimestampUtc,
            DateTime receivedUtc)
        {
            if (!_topicMaps.TryGetValue(topic, out var current))
            {
                current = null;
            }

            foreach (var pair in fields)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                    continue;

                if (current == null)
                {
                    current = new Dictionary<string, MqttV2DataEntry>(StringComparer.Ordinal);
                    _topicMaps[topic] = current;
                }

                if (!current.TryGetValue(pair.Key, out var existing) || !AreValuesEqual(existing?.Value, pair.Value))
                {
                    current[pair.Key] = new MqttV2DataEntry(pair.Value, sourceTimestampUtc, receivedUtc);
                }
                else
                {
                    // Keep value/version unchanged, but refresh timing metadata for delay evaluation.
                    current[pair.Key] = new MqttV2DataEntry(existing.Value, sourceTimestampUtc, receivedUtc);
                }
            }
        }

        private bool HasSnapshotNoLock(string topic)
        {
            return _topicMaps.TryGetValue(topic, out var current) && current.Count > 0;
        }

        private static bool AreValuesEqual(object left, object right)
        {
            return Equals(left, right);
        }

        private static IReadOnlyDictionary<string, MqttV2DataEntry> CloneSnapshot(Dictionary<string, MqttV2DataEntry> source)
        {
            return new Dictionary<string, MqttV2DataEntry>(source, StringComparer.Ordinal);
        }

        private static TaskCompletionSource<bool> CreateFirstDataSignal()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static Dictionary<string, MqttV2DataEntry> BuildMap(
            IReadOnlyDictionary<string, object> fields,
            DateTime sourceTimestampUtc,
            DateTime receivedUtc)
        {
            var map = new Dictionary<string, MqttV2DataEntry>(StringComparer.Ordinal);
            foreach (var pair in fields)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                    continue;

                map[pair.Key] = new MqttV2DataEntry(pair.Value, sourceTimestampUtc, receivedUtc);
            }

            return map;
        }

        private static DateTime NormalizeSourceTimestampOrNow(DateTime timestampUtc)
        {
            if (timestampUtc == default(DateTime))
                return DateTime.UtcNow;

            return timestampUtc.Kind == DateTimeKind.Utc ? timestampUtc : timestampUtc.ToUniversalTime();
        }

    }
}
