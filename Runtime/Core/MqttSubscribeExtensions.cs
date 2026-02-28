using System;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;

public static class MqttSubscribeExtensions
{
    public static UniTask SubscribeDataTopicAsync(
        this MqttClientManager manager,
        string topic,
        IDataRepository repository,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            manager.LogError("SubscribeDataTopicAsync: topic is required.");
            return UniTask.CompletedTask;
        }

        if (repository == null)
        {
            manager.LogError("SubscribeDataTopicAsync: repository is required.");
            return UniTask.CompletedTask;
        }

        return manager.SubscribeAsync(topic, (t, bytes) =>
        {
            var payload = Encoding.UTF8.GetString(bytes);

            if (MqttDTO.TryDeserializeData(payload, out var env, out var err))
            {
                repository.UpdateFromEnvelope(env);
                manager.Log($"MQTT data updated: topic={t}, items={env.Items?.Count ?? 0}");
            }
            else
            {
                manager.LogError($"Payload parse error: {err}");
            }
        }, ct);
    }
}