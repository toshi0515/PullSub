using System;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    /// <summary>
    /// <see cref="TypedTopicCache{T}"/> の型消去インターフェース。Dispose 時のキャンセルに使用します。
    /// </summary>
    internal interface ITypedTopicCache
    {
        void Cancel();
        void Invalidate();
        bool TryGetDebugState(out bool hasValue, out DateTime timestampUtc, out long receiveCount);
    }

    /// <summary>
    /// 1 トピック 1 型のデータキャッシュ。最新値と初回到着待機シグナルを型安全に管理します。
    /// </summary>
    internal sealed class TypedTopicCache<T> : ITypedTopicCache
    {
        private readonly object _gate = new object();
        private T _latest;
        private DateTime _latestTimestampUtc;
        private bool _hasValue;
        private bool _isActive = true;
        private long _receiveCount;
        private TaskCompletionSource<T> _firstValueSignal;

        public void Update(T value, DateTime timestampUtc)
        {
            TaskCompletionSource<T> signalToComplete = null;
            lock (_gate)
            {
                if (!_isActive)
                    return;

                _latest = value;
                _latestTimestampUtc = timestampUtc;
                _receiveCount++;
                if (!_hasValue)
                {
                    _hasValue = true;
                    signalToComplete = _firstValueSignal;
                    _firstValueSignal = null;
                }
            }
            signalToComplete?.TrySetResult(value);
        }

        public bool TryGet(out T value, out DateTime timestampUtc)
        {
            lock (_gate)
            {
                if (_isActive && _hasValue)
                {
                    value = _latest;
                    timestampUtc = _latestTimestampUtc;
                    return true;
                }
                value = default;
                timestampUtc = default;
                return false;
            }
        }

        public async Task<T> WaitForFirstAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                Task<T> waitTask;
                lock (_gate)
                {
                    if (!_isActive)
                        throw new InvalidOperationException("Topic cache is not active.");

                    if (_hasValue)
                        return _latest;

                    if (_firstValueSignal == null)
                        _firstValueSignal = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

                    waitTask = _firstValueSignal.Task;
                }

                await AsyncUtils.AwaitWithCancellation(waitTask, cancellationToken);
            }
        }

        public void Cancel()
        {
            TaskCompletionSource<T> signal;
            lock (_gate)
            {
                signal = _firstValueSignal;
                _firstValueSignal = null;
            }
            signal?.TrySetCanceled();
        }

        public void Invalidate()
        {
            TaskCompletionSource<T> signal;
            lock (_gate)
            {
                _isActive = false;
                _hasValue = false;
                _latest = default;
                _latestTimestampUtc = default;
                signal = _firstValueSignal;
                _firstValueSignal = null;
            }

            signal?.TrySetCanceled();
        }

        public bool TryGetDebugState(out bool hasValue, out DateTime timestampUtc, out long receiveCount)
        {
            lock (_gate)
            {
                hasValue = _isActive && _hasValue;
                timestampUtc = hasValue ? _latestTimestampUtc : default;
                receiveCount = _receiveCount;
                return hasValue;
            }
        }

        void ITypedTopicCache.Cancel() => Cancel();
        void ITypedTopicCache.Invalidate() => Invalidate();
        bool ITypedTopicCache.TryGetDebugState(out bool hasValue, out DateTime timestampUtc, out long receiveCount)
            => TryGetDebugState(out hasValue, out timestampUtc, out receiveCount);
    }
}
