using System;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;

public static class MqttSubscribeExtensions
{
    /// <summary>
    /// 指定トピックを購読し、受信データを <see cref="MqttDataStore"/> に自動格納する。
    /// </summary>
    /// <remarks>
    /// ハンドラは MQTTnet のバックグラウンドスレッドで実行されます。
    /// <see cref="MqttDataRepository"/> は内部で lock 保護されているためスレッドセーフですが、
    /// Unity オブジェクト（TextMeshPro 等）を直接更新する場合は、
    /// 呼び出し側で <c>UniTask.Post()</c> 等を使いメインスレッドに切り替えてください。
    /// </remarks>
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

        return manager.SubscribeAsync(topic, OnMessage, ct);

        // ローカル関数：ハンドラはバックグラウンドスレッドから呼び出されるが、
        // MqttDataRepository 内部は lock で保護されているためスレッドセーフ
        void OnMessage(string t, byte[] bytes)
        {
            try
            {
                var payload = Encoding.UTF8.GetString(bytes);

                if (manager.Serializer.TryDeserializeData(payload, out var env, out var err))
                {
                    repository.UpdateFromEnvelope(env);
                    manager.Log($"MQTT data updated: topic={t}, items={env.Items?.Count ?? 0}");
                }
                else
                {
                    manager.LogError($"Payload parse error: {err}");
                }
            }
            catch (Exception ex)
            {
                manager.LogException(ex, "SubscribeDataTopicAsync error", true);
            }
        }
    }
}