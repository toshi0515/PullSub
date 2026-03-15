using System;
using System.Threading.Tasks;
using MQTTnet.Client;

namespace UnityMqtt.V2.Core
{
    public sealed partial class MqttV2ClientRuntime
    {
        private async Task HandleConnectedAsync(MqttClientConnectedEventArgs args)
        {
            if (IsDisposeRequested)
                return;

            _log("[MQTT-V2] Connected.");

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
                SetState(MqttV2ClientState.Disconnected);
                _logError("[MQTT-V2] Resubscribe failed after connect. Waiting for the next reconnect cycle.");
                _logException(ex);
            }
        }

        private Task HandleDisconnectedAsync(MqttClientDisconnectedEventArgs e)
        {
            if (IsDisposeRequested)
                return Task.CompletedTask;

            if (State == MqttV2ClientState.Stopped || State == MqttV2ClientState.Disposed)
                return Task.CompletedTask;

            SetState(MqttV2ClientState.Reconnecting);

            _logError($"[MQTT-V2] Disconnected. Reason={e.Reason}");
            if (e.Exception != null)
                _logException(e.Exception);

            return Task.CompletedTask;
        }

        private Task HandleApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
        {
            if (IsDisposeRequested)
                return Task.CompletedTask;

            _messageDispatcher.Dispatch(e?.ApplicationMessage);
            return Task.CompletedTask;
        }
    }
}
