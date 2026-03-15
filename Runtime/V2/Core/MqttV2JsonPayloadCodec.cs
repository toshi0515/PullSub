using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace UnityMqtt.V2.Core
{
    /// <summary>
    /// JSON envelope 形式の標準 Codec（Id: json-envelope-v2）。
    /// ペイロードは <c>{ "timestamp": "...", "items": [ { "name": "...", "value": ... } ] }</c> 形式です。
    /// </summary>
    public sealed class MqttV2JsonPayloadCodec : MqttV2TextPayloadCodec
    {
        public override string Id => "json-envelope-v2";

        protected override string FormatFields(DateTime timestampUtc, IReadOnlyDictionary<string, object> fields)
        {
            var items = new List<JsonItemContract>(fields.Count);
            foreach (var pair in fields)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                    continue;

                items.Add(new JsonItemContract
                {
                    Name = pair.Key,
                    Value = pair.Value
                });
            }

            var envelope = new JsonEnvelopeContract
            {
                Timestamp = timestampUtc.ToString("o"),
                Items = items
            };

            return JsonConvert.SerializeObject(envelope);
        }

        protected override bool TryParseFields(string text, out IReadOnlyDictionary<string, object> fields, out DateTime timestampUtc, out string error)
        {
            fields = null;
            timestampUtc = default(DateTime);
            error = null;

            try
            {
                var envelope = JsonConvert.DeserializeObject<JsonEnvelopeContract>(text);
                if (envelope == null)
                {
                    error = "Deserialize returned null envelope.";
                    return false;
                }

                var map = new Dictionary<string, object>(StringComparer.Ordinal);
                if (envelope.Items != null)
                {
                    foreach (var item in envelope.Items)
                    {
                        if (item == null || string.IsNullOrWhiteSpace(item.Name))
                            continue;

                        map[item.Name] = item.Value;
                    }
                }

                fields = map;
                timestampUtc = ParseTimestampOrNow(envelope.Timestamp);

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        [Serializable]
        private sealed class JsonEnvelopeContract
        {
            [JsonProperty("timestamp")]
            public string Timestamp { get; set; }

            [JsonProperty("items")]
            public List<JsonItemContract> Items { get; set; }
        }

        [Serializable]
        private sealed class JsonItemContract
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("value")]
            public object Value { get; set; }
        }
    }
}
