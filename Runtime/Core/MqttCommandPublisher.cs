using MQTTnet.Protocol;
using Newtonsoft.Json;
using System.Text;
using Cysharp.Threading.Tasks;

/// <summary>
/// MQTT コマンド発行（MqttClientManager に委譲）
/// </summary>
public static class MqttCommandPublisher
{
    private const int MinFreq = 0;
    private const int MaxFreq = 120;

    // ========= 汎用 Publish API =========

    public static async UniTask PublishAsync<T>(string topic, T payload, MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce, bool retain = false)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            MqttClientManager.Instance.LogError("Invalid topic: topic is required.");
            return;
        }

        if (payload == null)
        {
            MqttClientManager.Instance.LogError("Invalid payload: payload is required.");
            return;
        }

        var json = JsonConvert.SerializeObject(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        await MqttClientManager.Instance.PublishAsync(topic, bytes, qos, retain);
    }

    public static UniTask PublishRawAsync(string topic, byte[] payload, MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce, bool retain = false)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            MqttClientManager.Instance.LogError("Invalid topic: topic is required.");
            return UniTask.CompletedTask;
        }

        return MqttClientManager.Instance.PublishAsync(topic, payload, qos, retain);
    }

    // ========= PLC コマンドAPI =========

    public static UniTask PublishRun(string topic, int? frequency, int? direction)
        => PublishPlcCommandAsync(topic, new PlcCommandPayload { Command = "RUN", Frequency = frequency, Direction = direction });

    public static UniTask PublishStop(string topic)
        => PublishPlcCommandAsync(topic, new PlcCommandPayload { Command = "STOP" });

    public static UniTask PublishSetFreq(string topic, int frequency)
        => PublishPlcCommandAsync(topic, new PlcCommandPayload { Command = "SET_FREQ", Frequency = frequency });

    public static UniTask PublishSetDir(string topic, int direction)
        => PublishPlcCommandAsync(topic, new PlcCommandPayload { Command = "SET_DIR", Direction = direction });

    // ========= PLC コマンド実処理 =========

    private static async UniTask PublishPlcCommandAsync(string topic, PlcCommandPayload payload)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            MqttClientManager.Instance.LogError("Invalid topic: topic is required.");
            return;
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.Command))
        {
            MqttClientManager.Instance.LogError("Invalid payload: command is required.");
            return;
        }

        // PLC コマンド固有のバリデーション
        if (payload.Frequency.HasValue && (payload.Frequency.Value < MinFreq || payload.Frequency.Value > MaxFreq))
        {
            MqttClientManager.Instance.LogError($"Invalid freq={payload.Frequency.Value} (allowed {MinFreq}..{MaxFreq})");
            return;
        }
        if (payload.Direction.HasValue && !(payload.Direction.Value == 0 || payload.Direction.Value == 1))
        {
            MqttClientManager.Instance.LogError($"Invalid dir={payload.Direction.Value} (allowed 0|1)");
            return;
        }

        var json = JsonConvert.SerializeObject(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        await MqttClientManager.Instance.PublishAsync(topic, bytes, MqttQualityOfServiceLevel.AtMostOnce, false);
    }

    // ========= DTO =========

    private sealed class PlcCommandPayload
    {
        [JsonProperty("command")]
        public string Command { get; set; }

        [JsonProperty("freq")]
        public int? Frequency { get; set; }

        [JsonProperty("dir")]
        public int? Direction { get; set; }
    }
}