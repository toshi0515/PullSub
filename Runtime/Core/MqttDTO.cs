using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;

[Serializable]
public sealed class MqttDataEnvelope
{
    // ISO8601(UTC)の文字列。必要があれば呼び出し側で DateTimeOffset.Parse してください。
    [JsonProperty("timestamp")]
    public string Timestamp { get; set; }

    [JsonProperty("items")]
    public List<MqttDataItem> Items { get; set; }
}

[Serializable]
public sealed class MqttDataItem
{
    [JsonProperty("name")]
    public string Name { get; set; }

    // 既定 serializer（Newtonsoft）では、数値は long/double、真偽値は bool、文字列は string になります
    [JsonProperty("value")]
    public object Value { get; set; }
}

public interface IMqttSerializer
{
    string SerializeEnvelope(MqttDataEnvelope envelope);
    bool TryDeserializeData(string payload, out MqttDataEnvelope envelope, out string error);
    string SerializeObject<T>(T payload);
}

public sealed class NewtonsoftMqttSerializer : IMqttSerializer
{
    public string SerializeEnvelope(MqttDataEnvelope envelope)
    {
        if (envelope == null) throw new ArgumentNullException(nameof(envelope));
        return JsonConvert.SerializeObject(envelope);
    }

    public bool TryDeserializeData(string payload, out MqttDataEnvelope envelope, out string error)
    {
        envelope = null;
        error = null;
        if (string.IsNullOrWhiteSpace(payload))
        {
            error = "Empty payload.";
            return false;
        }

        try
        {
            envelope = JsonConvert.DeserializeObject<MqttDataEnvelope>(payload);
            if (envelope == null)
            {
                error = "Deserialize returned null.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public string SerializeObject<T>(T payload) => JsonConvert.SerializeObject(payload);
}

public static class MqttSerializerDefaults
{
    private static IMqttSerializer _default = new NewtonsoftMqttSerializer();

    public static IMqttSerializer Default => Volatile.Read(ref _default);

    public static void SetDefault(IMqttSerializer serializer)
    {
        if (serializer == null) throw new ArgumentNullException(nameof(serializer));
        Interlocked.Exchange(ref _default, serializer);
    }
}

public static class MqttDTO
{
    // MqttDataEnvelope を JSON 文字列にシリアライズ（Publish 用）
    public static string SerializeEnvelope(MqttDataEnvelope envelope)
    {
        return MqttSerializerDefaults.Default.SerializeEnvelope(envelope);
    }

    // plc/data ペイロードをDTOへデシリアライズ
    public static bool TryDeserializeData(string json, out MqttDataEnvelope envelope, out string error)
    {
        return MqttSerializerDefaults.Default.TryDeserializeData(json, out envelope, out error);
    }
}