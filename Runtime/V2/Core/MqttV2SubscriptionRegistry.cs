using System;
using System.Collections.Generic;

namespace UnityMqtt.V2.Core
{
    /// <summary>
    /// Raw 購読のトピック参照カウントを管理します。
    /// 型付きデータ購読は <see cref="TypedDataRegistry"/> が担います。
    /// </summary>
    internal sealed class MqttV2SubscriptionRegistry
    {
        private sealed class TopicCounter
        {
            public int RawCount;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, TopicCounter> _topics
            = new Dictionary<string, TopicCounter>(StringComparer.Ordinal);

        /// <returns>true のとき、このトピックの最初の登録（ネットワーク購読すべき）</returns>
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

                var before = counter.RawCount;
                counter.RawCount++;
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
                if (counter.RawCount <= 0)
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

        public static void ValidateExactMatchTopic(string topic)
        {
            MqttV2TopicValidator.ValidateExactMatchTopic(topic);
        }
    }
}
