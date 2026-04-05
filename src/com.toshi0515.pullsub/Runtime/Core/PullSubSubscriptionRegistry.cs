using System;
using System.Collections.Generic;

namespace PullSub.Core
{
    internal sealed class PullSubSubscriptionRegistry
    {
        internal readonly struct TopicDebugSnapshot
        {
            public TopicDebugSnapshot(
                string topic,
                int dataSubCount,
                int queueSubCount,
                PullSubQualityOfServiceLevel subscribeQos)
            {
                Topic = topic;
                DataSubCount = dataSubCount;
                QueueSubCount = queueSubCount;
                SubscribeQos = subscribeQos;
            }

            public string Topic { get; }
            public int DataSubCount { get; }
            public int QueueSubCount { get; }
            public PullSubQualityOfServiceLevel SubscribeQos { get; }
        }

        private sealed class TopicCounter
        {
            public int QueueSubCount;
            public int DataSubCount;
            public PullSubQualityOfServiceLevel SubscribeQos;

            public int TotalCount => QueueSubCount + DataSubCount;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, TopicCounter> _topics
            = new Dictionary<string, TopicCounter>(StringComparer.Ordinal);

        public bool RegisterQueueSub(string topic, PullSubQualityOfServiceLevel subscribeQos)
        {
            ValidateExactMatchTopic(topic);
            ValidateSubscribeQos(subscribeQos);

            lock (_gate)
            {
                if (!_topics.TryGetValue(topic, out var counter))
                {
                    counter = new TopicCounter();
                    counter.SubscribeQos = subscribeQos;
                    _topics[topic] = counter;
                }
                else
                {
                    EnsureSubscribeQosCompatible(topic, counter, subscribeQos);
                }

                var before = counter.TotalCount;
                counter.QueueSubCount++;
                return before == 0;
            }
        }

        public bool RegisterDataSub(string topic, PullSubQualityOfServiceLevel subscribeQos)
        {
            ValidateExactMatchTopic(topic);
            ValidateSubscribeQos(subscribeQos);

            lock (_gate)
            {
                if (!_topics.TryGetValue(topic, out var counter))
                {
                    counter = new TopicCounter();
                    counter.SubscribeQos = subscribeQos;
                    _topics[topic] = counter;
                }
                else
                {
                    EnsureSubscribeQosCompatible(topic, counter, subscribeQos);
                }

                var before = counter.TotalCount;
                counter.DataSubCount++;
                return before == 0;
            }
        }

        public bool UnregisterQueueSub(string topic)
        {
            ValidateExactMatchTopic(topic);

            lock (_gate)
            {
                if (!_topics.TryGetValue(topic, out var counter) || counter.QueueSubCount <= 0)
                    return false;

                counter.QueueSubCount--;
                if (counter.TotalCount <= 0)
                {
                    _topics.Remove(topic);
                    return true;
                }
                return false;
            }
        }

        public bool UnregisterDataSub(string topic)
        {
            ValidateExactMatchTopic(topic);

            lock (_gate)
            {
                if (!_topics.TryGetValue(topic, out var counter) || counter.DataSubCount <= 0)
                    return false;

                counter.DataSubCount--;
                if (counter.TotalCount <= 0)
                {
                    _topics.Remove(topic);
                    return true;
                }
                return false;
            }
        }

        public bool IsQueueSubRegistered(string topic)
        {
            lock (_gate)
            {
                return _topics.TryGetValue(topic, out var counter) && counter.QueueSubCount > 0;
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

        internal TopicDebugSnapshot[] SnapshotTopicDetails()
        {
            lock (_gate)
            {
                var result = new TopicDebugSnapshot[_topics.Count];
                var index = 0;
                foreach (var pair in _topics)
                {
                    var counter = pair.Value;
                    result[index++] = new TopicDebugSnapshot(
                        pair.Key,
                        counter.DataSubCount,
                        counter.QueueSubCount,
                        counter.SubscribeQos);
                }

                return result;
            }
        }

        public bool TryGetSubscribeQos(string topic, out PullSubQualityOfServiceLevel subscribeQos)
        {
            lock (_gate)
            {
                if (_topics.TryGetValue(topic, out var counter) && counter.TotalCount > 0)
                {
                    subscribeQos = counter.SubscribeQos;
                    return true;
                }

                subscribeQos = default;
                return false;
            }
        }

        private static void ValidateSubscribeQos(PullSubQualityOfServiceLevel subscribeQos)
        {
            if (!Enum.IsDefined(typeof(PullSubQualityOfServiceLevel), subscribeQos))
                throw new ArgumentOutOfRangeException(nameof(subscribeQos));
        }

        private static void EnsureSubscribeQosCompatible(
            string topic,
            TopicCounter counter,
            PullSubQualityOfServiceLevel subscribeQos)
        {
            if (counter.TotalCount > 0 && counter.SubscribeQos != subscribeQos)
            {
                throw new InvalidOperationException(
                    $"Topic '{topic}' is already registered with subscribe QoS '{counter.SubscribeQos}'. " +
                    $"Cannot register with different subscribe QoS '{subscribeQos}'.");
            }
        }

        public static void ValidateExactMatchTopic(string topic)
        {
            PullSubTopicValidator.ValidateExactMatchTopic(topic);
        }
    }
}
