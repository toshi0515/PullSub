using System;
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace PullSub.Core
{
    /// <summary>
    /// フラット JSON 形式の標準型付き Codec。
    /// ペイロード形式は <c>{ "timestamp": "...", ...T のプロパティ... }</c> です。
    /// <c>timestamp</c> キーは予約済みです。T のプロパティに同名のフィールドを使用しないでください。
    /// T は JSON オブジェクトとしてシリアライズできる型（クラスまたは struct）である必要があります。
    /// </summary>
    public sealed class PullSubFlatJsonPayloadCodec<T> : PullSubTextPayloadCodec<T>
    {
        private static readonly JsonSerializerOptions DefaultOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public static readonly PullSubFlatJsonPayloadCodec<T> Default = new PullSubFlatJsonPayloadCodec<T>();

        private readonly JsonSerializerOptions _options;

        public PullSubFlatJsonPayloadCodec(JsonSerializerOptions options = null)
        {
            _options = options ?? DefaultOptions;
        }

        public override bool Equals(object obj)
        {
            return obj is PullSubFlatJsonPayloadCodec<T> other
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

                var tBytes = JsonSerializer.SerializeToUtf8Bytes(value, _options);
                using var tDoc = JsonDocument.Parse(tBytes);
                if (tDoc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("PullSubFlatJsonPayloadCodec<T> requires T to serialize as a JSON object.");

                foreach (var prop in tDoc.RootElement.EnumerateObject())
                {
                    if (prop.Name.Equals("timestamp", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Property name 'timestamp' is reserved by PullSubFlatJsonPayloadCodec<T>. Use a different property name.");
                    }

                    prop.WriteTo(writer);
                }

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
                using var doc = JsonDocument.ParseValue(ref reader);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    error = "Flat JSON payload root must be a JSON object.";
                    return false;
                }

                if (root.TryGetProperty("timestamp", out var tsProp) && tsProp.TryGetDateTime(out var ts))
                    timestampUtc = ts.Kind == DateTimeKind.Utc ? ts : ts.ToUniversalTime();
                else
                    timestampUtc = DateTime.UtcNow;

                // Reconstruct T from all properties except "timestamp".
                var buffer = new ArrayBufferWriter<byte>(payload.Length);
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (!prop.Name.Equals("timestamp", StringComparison.Ordinal))
                            prop.WriteTo(writer);
                    }
                    writer.WriteEndObject();
                }

                value = JsonSerializer.Deserialize<T>(buffer.WrittenSpan, _options);
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
