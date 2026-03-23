using System;
using System.Collections.Generic;

namespace PullSub.Core
{
    /// <summary>
    /// Raw 購読のトピック参照カウントを管理します。
    /// 型付きデータ購読は <see cref="TypedDataRegistry"/> が担います。
    /// </summary>
    internal sealed class PullSubSubscriptionRegistry
    {
        private sealed class TopicCounter
        {
            public int RawCount;
            public int DataCount;
            public PullSubQualityOfServiceLevel SubscribeQos;

            public int TotalCount => RawCount + DataCount;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, TopicCounter> _topics
            = new Dictionary<string, TopicCounter>(StringComparer.Ordinal);

        /// <returns>true のとき、このトピックの最初の登録（ネットワーク購読すべき）</returns>
        public bool RegisterRaw(string topic, PullSubQualityOfServiceLevel subscribeQos)
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
                counter.RawCount++;
                return before == 0;
            }
        }

        /// <returns>true のとき、このトピックの最初の登録（ネットワーク購読すべき）</returns>
        public bool RegisterData(string topic, PullSubQualityOfServiceLevel subscribeQos)
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
                counter.DataCount++;
                return before == 0;
            }
        }

        /// <returns>true のとき、参照がなくなった（ネットワーク購読解除すべき）</returns>
        public bool UnregisterRaw(string topic)
        {
            ValidateExactMatchTopic(topic);

            lock (_gate)
            {
                if (!_topics.TryGetValue(topic, out var counter) || counter.RawCount <= 0)
                    return false;

                counter.RawCount--;
                if (counter.TotalCount <= 0)
                {
                    _topics.Remove(topic);
                    return true;
                }
                return false;
            }
        }

        /// <returns>true のとき、参照がなくなった（ネットワーク購読解除すべき）</returns>
        public bool UnregisterData(string topic)
        {
            ValidateExactMatchTopic(topic);

            lock (_gate)
            {
                if (!_topics.TryGetValue(topic, out var counter) || counter.DataCount <= 0)
                    return false;

                counter.DataCount--;
                if (counter.TotalCount <= 0)
                {
                    _topics.Remove(topic);
                    return true;
                }
                return false;
            }
        }

        public bool IsRawRegistered(string topic)
        {
            lock (_gate)
            {
                return _topics.TryGetValue(topic, out var counter) && counter.RawCount > 0;
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
