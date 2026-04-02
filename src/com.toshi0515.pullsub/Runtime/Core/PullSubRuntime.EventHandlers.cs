using System;
using System.Threading.Tasks;

namespace PullSub.Core
{
    public sealed partial class PullSubRuntime
    {
        private async Task HandleConnectedAsync()
        {
            if (IsDisposeRequested)
                return;

            _log("[PullSubRuntime] Connected.");

            if (_suppressConnectedResubscribe)
                return;

            try
            {
                if (IsDisposeRequested)
                    return;

                await ResubscribeAllTopicsAsync(_disposeCts.Token);
            }
            catch (OperationCanceledException) when (IsDisposeRequested || _disposeCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                SetState(PullSubState.Disconnected);
                _logError("[PullSubRuntime] Resubscribe failed after connect. Waiting for the next reconnect cycle.");
                _logException(ex);
            }
        }
        private Task HandleDisconnectedAsync(string reason)
        {
            if (IsDisposeRequested)
                return Task.CompletedTask;

            if (State == PullSubState.Stopped || State == PullSubState.Disposed)
                return Task.CompletedTask;

            SetState(PullSubState.Reconnecting);

            _logWarning($"[PullSubRuntime] Disconnected. Reason={reason}");
            return Task.CompletedTask;
        }

        private Task HandleMessageReceivedAsync(string topic, ReadOnlyMemory<byte> payload)
        {
            if (IsDisposeRequested)
                return Task.CompletedTask;

            _messageDispatcher.Dispatch(topic, payload);
            return Task.CompletedTask;
        }
    }
}
