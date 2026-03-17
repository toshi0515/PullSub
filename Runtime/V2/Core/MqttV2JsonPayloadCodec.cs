using System;
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace UnityMqtt.V2.Core
{
    /// <summary>
    /// JSON envelope 形式の標準型付き Codec。
    /// ペイロード形式は <c>{ "timestamp": "...", "data": { ...T のプロパティ... } }</c> です。
    /// </summary>
    public sealed class MqttV2JsonPayloadCodec<T> : MqttV2TextPayloadCodec<T>
    {
        private static readonly JsonSerializerOptions DefaultOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public static readonly MqttV2JsonPayloadCodec<T> Default = new MqttV2JsonPayloadCodec<T>();

        private readonly JsonSerializerOptions _options;

        public MqttV2JsonPayloadCodec(JsonSerializerOptions options = null)
        {
            _options = options ?? DefaultOptions;
        }

        public override bool Equals(object obj)
        {
            return obj is MqttV2JsonPayloadCodec<T> other
                && ReferenceEquals(_options, other._options);
        }

        public override int GetHashCode()
        {
            return _options.GetHashCode();
        }

        public override byte[] Encode(DateTime timestampUtc, T value)
        {
            var normalized = NormalizeTimestampOrNow(timestampUtc);
            var buffer = new ArrayBufferWriter<byte>(256);

            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("timestamp", normalized);
                writer.WritePropertyName("data");
                JsonSerializer.Serialize(writer, value, _options);
                writer.WriteEndObject();
            }

            return buffer.WrittenSpan.ToArray();
        }

        public override bool TryDecode(ReadOnlySpan<byte> payload, out T value, out DateTime timestampUtc, out string error)
        {
            value = default;
            timestampUtc = default;
            error = null;

            if (payload.IsEmpty)
            {
                error = "payload is empty.";
                return false;
            }

            try
            {
                var reader = new Utf8JsonReader(payload);
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                {
                    error = "JSON root must be an object.";
                    return false;
                }

                long dataStart = -1;
                long dataEnd = -1;
                bool gotTimestamp = false;

                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName)
                    {
                        error = "Unexpected token in root object.";
                        return false;
                    }

                    if (reader.ValueTextEquals("timestamp"))
                    {
                        reader.Read();
                        if (reader.TryGetDateTime(out var ts))
                            timestampUtc = ts.Kind == DateTimeKind.Utc ? ts : ts.ToUniversalTime();
                        else
                            timestampUtc = DateTime.UtcNow;
                        gotTimestamp = true;
                    }
                    else if (reader.ValueTextEquals("data"))
                    {
                        reader.Read();
                        dataStart = reader.TokenStartIndex;
                        reader.Skip();
                        dataEnd = reader.BytesConsumed;
                    }
                    else
                    {
                        reader.Read();
                        reader.Skip();
                    }
                }

                if (!gotTimestamp)
                    timestampUtc = DateTime.UtcNow;

                if (dataStart < 0)
                {
                    error = "Missing 'data' field in JSON envelope.";
                    return false;
                }

                value = JsonSerializer.Deserialize<T>(
                    payload.Slice((int)dataStart, (int)(dataEnd - dataStart)),
                    _options);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        protected override string FormatPayload(DateTime timestampUtc, T value)
        {
            return Encoding.UTF8.GetString(Encode(timestampUtc, value));
        }

        protected override bool TryParsePayload(string text, out T value, out DateTime timestampUtc, out string error)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                value = default;
                timestampUtc = default;
                error = "payload text is empty.";
                return false;
            }

            return TryDecode(Encoding.UTF8.GetBytes(text), out value, out timestampUtc, out error);
        }
    }
}
