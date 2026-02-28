using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using System.Text;
using MQTTnet.Protocol;

public static class MqttPlcCommandPublishExtensions
{
    private const int MinFreq = 0;
    private const int MaxFreq = 120;

    public static UniTask PublishRunAsync(
        this MqttClientManager manager,
        string topic,
        int? frequency,
        int? direction,
        CancellationToken ct = default)
        => PublishPlcCommandAsync(manager, topic, new PlcCommandPayload { Command = "RUN", Frequency = frequency, Direction = direction }, ct);

    public static UniTask PublishStopAsync(
        this MqttClientManager manager,
        string topic,
        CancellationToken ct = default)
        => PublishPlcCommandAsync(manager, topic, new PlcCommandPayload { Command = "STOP" }, ct);

    public static UniTask PublishSetFreqAsync(
        this MqttClientManager manager,
        string topic,
        int frequency,
        CancellationToken ct = default)
        => PublishPlcCommandAsync(manager, topic, new PlcCommandPayload { Command = "SET_FREQ", Frequency = frequency }, ct);

    public static UniTask PublishSetDirAsync(
        this MqttClientManager manager,
        string topic,
        int direction,
        CancellationToken ct = default)
        => PublishPlcCommandAsync(manager, topic, new PlcCommandPayload { Command = "SET_DIR", Direction = direction }, ct);

    private static async UniTask PublishPlcCommandAsync(
        this MqttClientManager manager,
        string topic,
        PlcCommandPayload payload,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            manager.LogError("Invalid topic: topic is required.");
            return;
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.Command))
        {
            manager.LogError("Invalid payload: command is required.");
            return;
        }

        // PLC 固有のバリデーション
        if (payload.Frequency.HasValue && (payload.Frequency.Value < MinFreq || payload.Frequency.Value > MaxFreq))
        {
            manager.LogError($"Invalid freq={payload.Frequency.Value} (allowed {MinFreq}..{MaxFreq})");
            return;
        }
        if (payload.Direction.HasValue && !(payload.Direction.Value == 0 || payload.Direction.Value == 1))
        {
            manager.LogError($"Invalid dir={payload.Direction.Value} (allowed 0|1)");
            return;
        }

        var json = JsonConvert.SerializeObject(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        await manager.PublishAsync(topic, bytes, MqttQualityOfServiceLevel.AtMostOnce, false, ct);
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