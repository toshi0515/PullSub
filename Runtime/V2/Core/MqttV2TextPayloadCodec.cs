using System;
using System.Text;

namespace UnityMqtt.V2.Core
{
    /// <summary>
    /// テキスト形式ペイロードの型付き Codec 向け抽象基底クラス。
    /// UTF-8 変換・エラーハンドリング・timestamp 正規化を提供します。
    /// 派生クラスは <see cref="TryParsePayload"/> と <see cref="FormatPayload"/> のみ実装してください。
    /// </summary>
    public abstract class MqttV2TextPayloadCodec<T> : IMqttV2PayloadCodec<T>
    {
        /// <summary>受信テキストを value と timestamp に変換します。</summary>
        protected abstract bool TryParsePayload(
            string text,
            out T value,
            out DateTime timestampUtc,
            out string error);

        /// <summary>timestamp と value を送信テキストに変換します。</summary>
        protected abstract string FormatPayload(DateTime timestampUtc, T value);

        public virtual byte[] Encode(DateTime timestampUtc, T value)
        {
            var text = FormatPayload(NormalizeTimestampOrNow(timestampUtc), value);
            return Encoding.UTF8.GetBytes(text);
        }

        public virtual bool TryDecode(ReadOnlySpan<byte> payload, out T value, out DateTime timestampUtc, out string error)
        {
            value = default;
            timestampUtc = default;
            error = null;

            if (payload.IsEmpty)
            {
                error = "payload is empty.";
                return false;
            }

            string text;
            try
            {
                text = Encoding.UTF8.GetString(payload);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                error = "payload text is empty.";
                return false;
            }

            return TryParsePayload(text, out value, out timestampUtc, out error);
        }

        protected static DateTime NormalizeTimestampOrNow(DateTime timestampUtc)
        {
            if (timestampUtc == default)
                return DateTime.UtcNow;

            return timestampUtc.Kind == DateTimeKind.Utc ? timestampUtc : timestampUtc.ToUniversalTime();
        }

        protected static DateTime ParseTimestampOrNow(string timestamp)
        {
            if (DateTime.TryParse(timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();

            return DateTime.UtcNow;
        }
    }
}
