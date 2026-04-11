using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    public sealed class DataSubscription<T> : System.IDisposable, System.IAsyncDisposable
    {
        private readonly PullSubRuntime _runtime;
        private readonly string _topic;
        private readonly TypedTopicCache<T> _cache;
        private int _unsubscribeRequested;

        internal DataSubscription(PullSubRuntime runtime, string topic, TypedTopicCache<T> cache)
        {
            _runtime = runtime ?? throw new System.ArgumentNullException(nameof(runtime));
            _topic = topic ?? throw new System.ArgumentNullException(nameof(topic));
            _cache = cache ?? throw new System.ArgumentNullException(nameof(cache));
        }

        /// <summary>
        /// Returns the latest value. Returns default(T) if no data has arrived yet.
        /// </summary>
        public T Value => _cache.TryGet(out var v, out _) ? v : default;

        /// <summary>
        /// Returns the timestamp (UTC) of the latest data.
        /// Uses the timestamp field from the JSON payload if available, otherwise uses the wall clock time when the message was received.
        /// Returns default(DateTime) if no data has arrived yet.
        /// </summary>
        public System.DateTime TimestampUtc => _cache.TryGet(out _, out var ts) ? ts : default;

        /// <summary>
        /// Returns the timestamp converted to the local time of this device.
        /// </summary>
        public System.DateTime TimestampLocal => TimestampUtc.ToLocalTime();

        /// <summary>
        /// Returns true if at least one message has been received.
        /// </summary>
        public bool HasValue => _cache.TryGet(out _, out _);

        /// <summary>
        /// Tries to get the latest value. Returns false if no data has arrived yet.
        /// </summary>
        public bool TryGet(out T value)
        {
            return _cache.TryGet(out value, out _);
        }

        /// <summary>
        /// Tries to get the latest value and its timestamp (UTC). Returns false if no data has arrived yet.
        /// </summary>
        public bool TryGet(out T value, out System.DateTime timestampUtc)
        {
            return _cache.TryGet(out value, out timestampUtc);
        }

        /// <summary>
        /// Returns the latest value, or the specified fallback value if no data has arrived yet.
        /// </summary>
        public T GetValueOrDefault(T fallback) =>
            _cache.TryGet(out var v, out _) ? v : fallback;

        /// <summary>
        /// The name of the subscribed topic.
        /// </summary>
        public string Topic => _topic;

        /// <summary>
        /// Returns true if the subscription is active and data has arrived.
        /// Becomes false after calling UnsubscribeAsync or DisposeAsync.
        /// </summary>
        public bool IsValid => !_runtime.IsDisposeRequested
            && _cache.TryGet(out _, out _);

        public async Task<PullSubUnsubscribeResult> UnsubscribeAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _unsubscribeRequested, 1) != 0)
                return PullSubUnsubscribeResult.AlreadyCanceled;

            try
            {
                await _runtime.UnsubscribeDataAsync(Topic, cancellationToken).ConfigureAwait(false);
                return PullSubUnsubscribeResult.Success;
            }
            catch (System.ObjectDisposedException)
            {
                return PullSubUnsubscribeResult.AlreadyCanceled;
            }
            catch (System.OperationCanceledException) when (_runtime.IsDisposeRequested)
            {
                return PullSubUnsubscribeResult.AlreadyCanceled;
            }
            catch
            {
                return PullSubUnsubscribeResult.Failed;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await UnsubscribeAsync(CancellationToken.None).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _ = UnsubscribeAsync(CancellationToken.None);
        }
    }
}