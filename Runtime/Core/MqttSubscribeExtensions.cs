using System;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;

public static class MqttSubscribeExtensions
{
    public static UniTask SubscribeDataTopicAsync(
        this MqttClientManager manager,
        string topic,
        MqttDataStore store,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            manager.LogError("SubscribeDataTopicAsync: topic is required.");
            return UniTask.CompletedTask;
        }

        if (store == null)
        {
            manager.LogError("SubscribeDataTopicAsync: store is required.");
            return UniTask.CompletedTask;
        }

        var repository = store.GetOrCreate(topic);

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