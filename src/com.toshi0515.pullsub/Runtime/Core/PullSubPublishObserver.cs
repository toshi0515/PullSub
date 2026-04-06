using System;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    internal sealed class PullSubPublishObserver<T> : IObserver<T>
    {
        private readonly PullSubRuntime _runtime;
        private readonly IPullSubTopic<T> _topic;
        private readonly PullSubQualityOfServiceLevel _qos;
        private readonly bool _retain;
        private readonly Action<Exception> _onError;
        private readonly SemaphoreSlim _publishGate = new SemaphoreSlim(1, 1);

        private int _terminated;

        public PullSubPublishObserver(
            PullSubRuntime runtime,
            IPullSubTopic<T> topic,
            PullSubQualityOfServiceLevel qos,
            bool retain,
            Action<Exception> onError)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _topic = topic ?? throw new ArgumentNullException(nameof(topic));
            _qos = qos;
            _retain = retain;
            _onError = onError;
        }

        public void OnNext(T value)
        {
            if (Volatile.Read(ref _terminated) != 0)
                return;

            _ = PublishSerializedAsync(value);
        }

        public void OnError(Exception error)
        {
            if (!TryTerminate())
                return;

            NotifyError(error ?? new InvalidOperationException("Reactive source reported an unknown error."));
        }

        public void OnCompleted()
        {
            TryTerminate();
        }

        private async Task PublishSerializedAsync(T value)
        {
            if (Volatile.Read(ref _terminated) != 0)
                return;

            await _publishGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Drop queued values after completion/error; only in-flight publish is allowed to finish.
                if (Volatile.Read(ref _terminated) != 0)
                    return;

                await _runtime.PublishDataAsync(_topic, value, _qos, _retain, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (ObjectDisposedException) when (_runtime.IsDisposeRequested)
            {
                TryTerminate();
            }
            catch (OperationCanceledException) when (_runtime.IsDisposeRequested)
            {
                TryTerminate();
            }
            catch (Exception ex)
            {
                if (TryTerminate())
                    NotifyError(ex);
            }
            finally
            {
                _publishGate.Release();
            }
        }

        private bool TryTerminate()
        {
            return Interlocked.Exchange(ref _terminated, 1) == 0;
        }

        private void NotifyError(Exception error)
        {
            if (_onError != null)
            {
                try
                {
                    _onError(error);
                    return;
                }
                catch (Exception callbackException)
                {
                    _runtime.ReportPublisherObserverError(error);
                    _runtime.ReportPublisherObserverError(callbackException);
                    return;
                }
            }

            _runtime.ReportPublisherObserverError(error);
        }
    }
}