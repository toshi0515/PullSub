using System;
using System.Collections.Generic;

namespace UnityMqtt.V2.Core
{
    internal sealed class MqttV2SubscriptionRegistry
    {
        private sealed class TopicCounter
        {
            public int RawCount;
            public int DataCount;

            public int TotalCount => RawCount + DataCount;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, TopicCounter> _topics
            = new Dictionary<string, TopicCounter>(StringComparer.Ordinal);

        public bool RegisterRaw(string topic)
        {
            ValidateExactMatchTopic(topic);

            lock (_gate)
            {
                if (!_topics.TryGetValue(topic, out var counter))
                {
                    counter = new TopicCounter();
                    _topics[topic] = counter;
                }

                var before = counter.TotalCount;
                counter.RawCount++;
                return before == 0;
            }
        }

        public bool RegisterData(string topic)
        {
            ValidateExactMatchTopic(topic);

            lock (_gate)
            {
                if (!_topics.TryGetValue(topic, out var counter))
                {
                    counter = new TopicCounter();
                    _topics[topic] = counter;
                }

                var before = counter.TotalCount;
                counter.DataCount++;
                return before == 0;
            }
        }

        public bool UnregisterRaw(string topic)
        {
            ValidateExactMatchTopic(topic);

            lock (_gate)
            {
                if (!_topics.TryGetValue(topic, out var counter) || counter.RawCount <= 0)
                    return false;

                counter.RawCount--;
                return TryCleanupAndReturnShouldUnsubscribe(topic, counter);
            }
        }

        public bool UnregisterData(string topic)
        {
            ValidateExactMatchTopic(topic);

            lock (_gate)
            {
                if (!_topics.TryGetValue(topic, out var counter) || counter.DataCount <= 0)
                    return false;

                counter.DataCount--;
                return TryCleanupAndReturnShouldUnsubscribe(topic, counter);
            }
        }

        public bool IsRawRegistered(string topic)
        {
            lock (_gate)
            {
                return _topics.TryGetValue(topic, out var counter) && counter.RawCount > 0;
            }
        }

        public bool IsDataRegistered(string topic)
        {
            lock (_gate)
            {
                return _topics.TryGetValue(topic, out var counter) && counter.DataCount > 0;
            }
        }

        public string[] SnapshotTopics()
        {
            lock (_gate)
            {
                var result = new string[_topics.Count];
                _topics.Keys.CopyTo(result, 0);
                return result;
            }
        }

        public static void ValidateExactMatchTopic(string topic)
        {
            MqttV2TopicValidator.ValidateExactMatchTopic(topic);
        }

        private bool TryCleanupAndReturnShouldUnsubscribe(string topic, TopicCounter counter)
        {
            if (counter.TotalCount > 0)
                return false;

            _topics.Remove(topic);
            return true;
        }
    }
}
