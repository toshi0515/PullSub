
using System;
using System.Text;
using Cysharp.Threading.Tasks;

/// <summary>
/// MQTT サブスクライバー（MqttClientManager に委譲）
/// </summary>
public static class MqttSubscriber
{
    public static UniTask SubscribeAsync(string topic) => MqttClientManager.Instance.SubscribeAsync(topic);

    public static UniTask SubscribeAsync(string topic, Action<string, byte[]> handler)
        => MqttClientManager.Instance.SubscribeAsync(topic, handler);

    public static UniTask UnsubscribeAsync(string topic) => MqttClientManager.Instance.UnsubscribeAsync(topic);

    public static UniTask SubscribeDataTopicAsync(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            MqttClientManager.Instance.LogError("SubscribeDataTopicAsync: topic is required.");
            return UniTask.CompletedTask;
        }

        if (MqttDataRepository.Instance == null)
        {
            MqttClientManager.Instance.LogError("SubscribeDataTopicAsync: repository is required.");
            return UniTask.CompletedTask;
        }

        return MqttClientManager.Instance.SubscribeAsync(topic, (t, bytes) => HandleDataMessage(t, bytes));
    }

    private static void HandleDataMessage(string topic, byte[] bytes)
    {
        var payload = Encoding.UTF8.GetString(bytes);

        if (MqttDTO.TryDeserializeData(payload, out var env, out var err))
        {
            MqttDataRepository.Instance.UpdateFromEnvelope(env);
            MqttClientManager.Instance.Log($"MQTT data updated: topic={topic}, items={env.Items?.Count ?? 0}");
        }
        else
        {
            MqttClientManager.Instance.LogError($"Payload parse error: {err}");
        }
    }
}
