namespace PullSub.Core
{
    public sealed class PullSubDataHandle<T>
    {
        private readonly PullSubRuntime _runtime;
        private readonly string _topic;

        internal PullSubDataHandle(PullSubRuntime runtime, string topic)
        {
            _runtime = runtime;
            _topic = topic;
        }

        /// <summary>
        /// 最新値を返します。データ未到着の場合は default(T) を返します。
        /// </summary>
        public T Value => _runtime.TryGetData<T>(_topic, out var v) ? v : default;

        /// <summary>
        /// データが一度でも到着しているかどうか。
        /// </summary>
        public bool HasValue => _runtime.TryGetData<T>(_topic, out _);

        /// <summary>
        /// データ未到着のときに返す値を明示したい場合。
        /// </summary>
        public T GetValueOrDefault(T fallback) =>
            _runtime.TryGetData<T>(_topic, out var v) ? v : fallback;

        /// <summary>
        /// 購読中のトピック名。
        /// </summary>
        public string Topic => _topic;

        /// <summary>
        /// 購読が有効でデータが到着済みの場合 true。
        /// UnsubscribeDataAsync または DisposeAsync の後は false になります。
        /// </summary>
        public bool IsValid => !_runtime.IsDisposeRequested
            && _runtime.TryGetData<T>(_topic, out _);
    }
}