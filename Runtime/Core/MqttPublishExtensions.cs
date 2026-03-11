using MQTTnet.Protocol;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;

public static class MqttPublishExtensions
{
    public static async UniTask PublishAsync<T>(
        this MqttClientManager manager,
        string topic,
        T payload,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce,
        bool retain = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            manager.LogError("Invalid topic: topic is required.");
            return;
        }

        if (payload == null)
        {
            manager.LogError("Invalid payload: payload is required.");
            return;
        }

        var json = manager.Serializer.SerializeObject(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        await manager.PublishAsync(topic, bytes, qos, retain, ct);
    }

    public static UniTask PublishRawAsync(
        this MqttClientManager manager,
        string topic,
        byte[] payload,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce,
        bool retain = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            manager.LogError("Invalid topic: topic is required.");
            return UniTask.CompletedTask;
        }

        return manager.PublishAsync(topic, payload, qos, retain, ct);
    }
}