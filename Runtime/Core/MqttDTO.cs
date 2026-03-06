using System;
using System.Collections.Generic;
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

    // "BIT" | "WORD" | "DINT" | "REAL" など（大文字想定）
    [JsonProperty("type")]
    public string Type { get; set; }

    // Newtonsoft.Json により、数値は long/double、真偽値は bool、文字列は string になります
    [JsonProperty("value")]
    public object Value { get; set; }
}

public static class MqttDTO
{
    // MqttDataEnvelope を JSON 文字列にシリアライズ（Publish 用）
    public static string SerializeEnvelope(MqttDataEnvelope envelope)
    {
        if (envelope == null) throw new ArgumentNullException(nameof(envelope));
        return JsonConvert.SerializeObject(envelope);
    }

    // plc/data ペイロードをDTOへデシリアライズ
    public static bool TryDeserializeData(string json, out MqttDataEnvelope envelope, out string error)
    {
        envelope = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Empty payload.";
            return false;
        }

        try
        {
            envelope = JsonConvert.DeserializeObject<MqttDataEnvelope>(json);
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
}