using System;
using System.Collections.Generic;

namespace PullSub.Core
{
    /// <summary>
    /// トピックごとの型付きデータ購読登録を管理します。
    /// 1 トピック 1 型の制約を強制し、Codec の decode クロージャと
    /// <see cref="TypedTopicCache{T}"/> を紐付けます。
    /// </summary>
    internal sealed class TypedDataRegistry
    {
        private abstract class TopicEntry
        {
            public Type ValueType { get; }

            /// <summary>object の実体は TypedTopicCache&lt;T&gt;</summary>
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
            /// 既存 Codec と新規 Codec の互換性を検証します。
            /// 同値と判定できる場合のみ true を返します。
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
        /// トピックを型 T / Codec で登録します。
        /// 同一トピックに異なる型が登録されていた場合は <see cref="InvalidOperationException"/> を送出します。
        /// 同一トピック・同一型でも Codec が同値でない場合は例外を送出します。
        /// </summary>
        /// <returns>true のとき、このトピックの最初の登録（ネットワーク購読を開始すべき）</returns>
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
        /// 参照カウントを 1 減らします。
        /// </summary>
        /// <returns>true のとき、最後の参照が解放された（ネットワーク購読解除すべき）</returns>
        public bool Unregister(string topic)
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue(topic, out var entry))
                    return false;

                entry.RefCount--;
                if (entry.RefCount <= 0)
                {
                    _entries.Remove(topic);
                    return true;
                }
                return false;
            }
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

        /// <summary>全キャッシュの待機タスクをキャンセルし、登録を消去します。</summary>
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
                cache.Cancel();
        }
    }
}
