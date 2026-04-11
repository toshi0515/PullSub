using System;
using System.Collections.Generic;

namespace PullSub.Core
{
    /// <summary>
    /// Manages typed data subscription registrations per topic.
    /// Enforces a one-topic-one-type constraint and links the codec's decode closure with <see cref="TypedTopicCache{T}"/>.
    /// </summary>
    internal sealed class TypedDataRegistry
    {
        internal readonly struct TypedDataTopicDebugSnapshot
        {
            public TypedDataTopicDebugSnapshot(string topic, bool hasValue, DateTime timestampUtc, long receiveCount)
            {
                Topic = topic;
                HasValue = hasValue;
                TimestampUtc = timestampUtc;
                ReceiveCount = receiveCount;
            }

            public string Topic { get; }
            public bool HasValue { get; }
            public DateTime TimestampUtc { get; }
            public long ReceiveCount { get; }
        }

        private abstract class TopicEntry
        {
            public Type ValueType { get; }

            /// <summary>The actual object is <see cref="TypedTopicCache{T}"/></summary>
            public ITypedTopicCache Cache { get; }

            public int RefCount;

            protected TopicEntry(Type type, ITypedTopicCache cache)
            {
                ValueType = type;
                Cache = cache;
                RefCount = 1;
            }

            public abstract bool TryDecodeAndUpdate(ReadOnlySpan<byte> payload);

            /// <summary>
            /// Validates compatibility between the existing codec and a new codec.
            /// Returns true only if they are considered equivalent.
            /// </summary>
            public abstract bool IsEquivalentCodec(object newCodec);
        }

        private sealed class TopicEntry<T> : TopicEntry
        {
            private readonly IPayloadCodec<T> _codec;
            private readonly TypedTopicCache<T> _cache;

            public TopicEntry(TypedTopicCache<T> cache, IPayloadCodec<T> codec)
                : base(typeof(T), cache)
            {
                _cache = cache;
                _codec = codec;
            }

            public override bool TryDecodeAndUpdate(ReadOnlySpan<byte> payload)
            {
                if (!_codec.TryDecode(payload, out var value, out var timestampUtc, out _))
                    return false;

                _cache.Update(value, timestampUtc);
                return true;
            }

            public override bool IsEquivalentCodec(object newCodec)
            {
                return newCodec is IPayloadCodec<T> codecAsT
                    && EqualityComparer<IPayloadCodec<T>>.Default.Equals(_codec, codecAsT);
            }
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, TopicEntry> _entries
            = new Dictionary<string, TopicEntry>(StringComparer.Ordinal);

        /// <summary>
        /// Registers a topic with type T and codec.
        /// Throws <see cref="InvalidOperationException"/> if a different type is already registered for the same topic.
        /// Throws an exception if the codec is not equivalent for the same topic and type.
        /// </summary>
        /// <returns>true if this is the first registration for the topic (network subscription should start)</returns>
        public bool Register<T>(string topic, IPayloadCodec<T> codec, out TypedTopicCache<T> cache)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(topic, out var existing))
                {
                    if (existing.ValueType != typeof(T))
                        throw new InvalidOperationException(
                            $"Topic '{topic}' is already registered with type '{existing.ValueType.Name}'. " +
                            $"Cannot register with type '{typeof(T).Name}'.");

                    if (!existing.IsEquivalentCodec(codec))
                        throw new InvalidOperationException(
                            $"Topic '{topic}' is already registered with a non-equivalent codec. " +
                            $"Each topic must use an equivalent codec for all subscriptions.");

                    existing.RefCount++;
                    cache = (TypedTopicCache<T>)existing.Cache;
                    return false; // 追加参照
                }

                var newCache = new TypedTopicCache<T>();
                _entries[topic] = new TopicEntry<T>(newCache, codec);

                cache = newCache;
                return true; // 最初の登録
            }
        }

        /// <summary>
        /// Decrements the reference count by 1.
        /// </summary>
        /// <returns>true if the last reference has been released (network subscription should be cancelled)</returns>
        public bool Unregister(string topic)
        {
            ITypedTopicCache removedCache = null;
            lock (_gate)
            {
                if (!_entries.TryGetValue(topic, out var entry))
                    return false;

                entry.RefCount--;
                if (entry.RefCount <= 0)
                {
                    removedCache = entry.Cache;
                    _entries.Remove(topic);
                }
                else
                {
                    return false;
                }
            }

            removedCache.Invalidate();
            return true;
        }

        public bool IsRegistered(string topic)
        {
            lock (_gate)
                return _entries.ContainsKey(topic);
        }

        public bool TryGetCache<T>(string topic, out TypedTopicCache<T> cache)
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue(topic, out var entry) || entry.ValueType != typeof(T))
                {
                    cache = null;
                    return false;
                }
                cache = (TypedTopicCache<T>)entry.Cache;
                return true;
            }
        }

        public bool TryDecodeAndUpdate(string topic, ReadOnlySpan<byte> payload)
        {
            TopicEntry entry;
            lock (_gate)
            {
                if (!_entries.TryGetValue(topic, out entry))
                    return false;
            }

            return entry.TryDecodeAndUpdate(payload);
        }

        public string[] SnapshotTopics()
        {
            lock (_gate)
            {
                var result = new string[_entries.Count];
                _entries.Keys.CopyTo(result, 0);
                return result;
            }
        }

        internal TypedDataTopicDebugSnapshot[] SnapshotTopicStates()
        {
            KeyValuePair<string, TopicEntry>[] entries;
            lock (_gate)
            {
                entries = new KeyValuePair<string, TopicEntry>[_entries.Count];
                var copyIndex = 0;
                foreach (var pair in _entries)
                    entries[copyIndex++] = pair;
            }

            var result = new TypedDataTopicDebugSnapshot[entries.Length];
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                entry.Value.Cache.TryGetDebugState(out var hasValue, out var timestampUtc, out var receiveCount);
                result[i] = new TypedDataTopicDebugSnapshot(entry.Key, hasValue, timestampUtc, receiveCount);
            }

            return result;
        }

        /// <summary>Cancels all wait tasks and clears registrations.</summary>
        public void CancelAll()
        {
            List<ITypedTopicCache> caches;
            lock (_gate)
            {
                caches = new List<ITypedTopicCache>(_entries.Count);
                foreach (var entry in _entries.Values)
                    caches.Add(entry.Cache);
                _entries.Clear();
            }
            foreach (var cache in caches)
            {
                cache.Invalidate();
                cache.Cancel();
            }
        }
    }
}
