namespace PullSub.Core
{
    public sealed class PullSubDataHandle<T>
    {
        private readonly PullSubRuntime _runtime;
        private readonly string _topic;
        private readonly TypedTopicCache<T> _cache;

        internal PullSubDataHandle(PullSubRuntime runtime, string topic, TypedTopicCache<T> cache)
        {
            _runtime = runtime ?? throw new System.ArgumentNullException(nameof(runtime));
            _topic = topic ?? throw new System.ArgumentNullException(nameof(topic));
            _cache = cache ?? throw new System.ArgumentNullException(nameof(cache));
        }

        /// <summary>
        /// 最新値を返します。データ未到着の場合は default(T) を返します。
        /// Data API は in-place 更新前提のため、参照型 T の場合は
        /// 取得した参照をフレーム間で保持しないでください。
        /// </summary>
        public T Value => _cache.TryGet(out var v, out _) ? v : default;

        /// <summary>
        /// 最新データの発生時刻（世界標準時）を返します。
        /// JSON ペイロードに timestamp が含まれていればそれを、なければ受信時刻を返します。
        /// データ未到着の場合は default(DateTime) を返します。
        /// </summary>
        public System.DateTime TimestampUtc => _cache.TryGet(out _, out var ts) ? ts : default;

        /// <summary>
        /// 端末のローカル時間に変換された発生時刻を返します。
        /// </summary>
        public System.DateTime TimestampLocal => TimestampUtc.ToLocalTime();

        /// <summary>
        /// データが一度でも到着しているかどうか。
        /// </summary>
        public bool HasValue => _cache.TryGet(out _, out _);

        /// <summary>
        /// 最新値を取得します。未到着の場合は false を返します。
        /// </summary>
        public bool TryGet(out T value)
        {
            return _cache.TryGet(out value, out _);
        }

        /// <summary>
        /// 最新値と発生時刻（UTC）を取得します。未到着の場合は false を返します。
        /// </summary>
        public bool TryGet(out T value, out System.DateTime timestampUtc)
        {
            return _cache.TryGet(out value, out timestampUtc);
        }

        /// <summary>
        /// データ未到着のときに返す値を明示したい場合。
        /// </summary>
        public T GetValueOrDefault(T fallback) =>
            _cache.TryGet(out var v, out _) ? v : fallback;

        /// <summary>
        /// 購読中のトピック名。
        /// </summary>
        public string Topic => _topic;

        /// <summary>
        /// 購読が有効でデータが到着済みの場合 true。
        /// UnsubscribeDataAsync または DisposeAsync の後は false になります。
        /// </summary>
        public bool IsValid => !_runtime.IsDisposeRequested
            && _cache.TryGet(out _, out _);
    }
}