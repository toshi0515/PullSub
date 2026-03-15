using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityMqtt.V2.Core
{
    /// <summary>
    /// フラット JSON 形式の標準 Codec（Id: flat-json-v1）。
    /// ペイロードは <c>timestamp</c> キーとフィールドが同一 JSON ルートに並ぶ形式です。
    /// <c>timestamp</c> キーは予約済みです。同名のフィールドを使用しないでください。
    /// </summary>
    public sealed class MqttV2FlatJsonPayloadCodec : MqttV2TextPayloadCodec
    {
        public override string Id => "flat-json-v1";

        protected override string FormatFields(DateTime timestampUtc, IReadOnlyDictionary<string, object> fields)
        {
            var root = new JObject
            {
                ["timestamp"] = timestampUtc.ToString("o")
            };

            foreach (var pair in fields)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                    continue;

                if (string.Equals(pair.Key, "timestamp", StringComparison.Ordinal))
                    throw new ArgumentException(
                        "Field key 'timestamp' is reserved by flat-json-v1 codec. Use a different key name.",
                        nameof(fields));

                root[pair.Key] = pair.Value == null ? JValue.CreateNull() : JToken.FromObject(pair.Value);
            }

            return root.ToString(Formatting.None);
        }

        protected override bool TryParseFields(string text, out IReadOnlyDictionary<string, object> fields, out DateTime timestampUtc, out string error)
        {
            fields = null;
            timestampUtc = default(DateTime);
            error = null;

            try
            {
                var root = JObject.Parse(text);
                var map = new Dictionary<string, object>(StringComparer.Ordinal);

                timestampUtc = ParseTimestampOrNow(root.Value<string>("timestamp"));

                foreach (var prop in root.Properties())
                {
                    if (string.Equals(prop.Name, "timestamp", StringComparison.Ordinal))
                        continue;

                    map[prop.Name] = prop.Value;
                }

                fields = map;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
