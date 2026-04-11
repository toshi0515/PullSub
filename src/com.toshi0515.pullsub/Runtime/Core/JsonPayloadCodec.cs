using System;
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace PullSub.Core
{
    /// <summary>
    /// JSON envelope 形式の標準型付き Codec。
    /// ペイロード形式は <c>{ "timestamp": "...", "data": { ...T のプロパティ... } }</c> です。
    /// </summary>
    public sealed class JsonPayloadCodec<T> : TextPayloadCodec<T>
    {
        private static readonly JsonSerializerOptions DefaultOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public static readonly JsonPayloadCodec<T> Default = new JsonPayloadCodec<T>();

        private readonly JsonSerializerOptions _options;

        public JsonPayloadCodec(JsonSerializerOptions options = null)
        {
            _options = options ?? DefaultOptions;
        }

        public override bool Equals(object obj)
        {
            return obj is JsonPayloadCodec<T> other
                && ReferenceEquals(_options, other._options);
        }

        public override int GetHashCode()
        {
            return _options.GetHashCode();
        }

        public override void Encode(DateTime timestampUtc, T value, IBufferWriter<byte> bufferWriter)
        {
            if (bufferWriter == null)
                throw new ArgumentNullException(nameof(bufferWriter));

            var normalized = NormalizeTimestampOrNow(timestampUtc);
            using var writer = new Utf8JsonWriter(bufferWriter);
            writer.WriteStartObject();
            writer.WriteString("timestamp", normalized);
            writer.WritePropertyName("data");
            JsonSerializer.Serialize(writer, value, _options);
            writer.WriteEndObject();
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
            var buffer = new ArrayBufferWriter<byte>(256);
            Encode(timestampUtc, value, buffer);
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
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
