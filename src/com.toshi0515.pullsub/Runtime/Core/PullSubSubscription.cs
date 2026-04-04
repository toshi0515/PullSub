using System;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    public sealed class PullSubSubscription<T> : IPullSubSubscriptionLease
    {
        private readonly PullSubRuntime _runtime;
        private int _unsubscribeRequested;

        internal PullSubSubscription(
            PullSubRuntime runtime,
            IPullSubTopic<T> topic,
            PullSubDataHandle<T> handle)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            if (topic == null)
                throw new ArgumentNullException(nameof(topic));

            Handle = handle ?? throw new ArgumentNullException(nameof(handle));
            Topic = topic.TopicName;
        }

        public PullSubDataHandle<T> Handle { get; }

        public string Topic { get; }

        public T Value => Handle.Value;
        public bool HasValue => Handle.HasValue;
        public bool IsValid => Handle.IsValid;
        public DateTime TimestampUtc => Handle.TimestampUtc;
        public DateTime TimestampLocal => Handle.TimestampLocal;

        public T GetValueOrDefault(T fallback)
        {
            return Handle.GetValueOrDefault(fallback);
        }

        public bool TryGet(out T value)
        {
            return Handle.TryGet(out value);
        }

        public bool TryGet(out T value, out DateTime timestampUtc)
        {
            return Handle.TryGet(out value, out timestampUtc);
        }

        public async Task<PullSubUnsubscribeResult> UnsubscribeAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _unsubscribeRequested, 1) != 0)
                return PullSubUnsubscribeResult.AlreadyCanceled;

            try
            {
                await _runtime.UnsubscribeDataAsync(Topic, cancellationToken).ConfigureAwait(false);
                return PullSubUnsubscribeResult.Success;
            }
            catch (ObjectDisposedException)
            {
                return PullSubUnsubscribeResult.AlreadyCanceled;
            }
            catch (OperationCanceledException) when (_runtime.IsDisposeRequested)
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