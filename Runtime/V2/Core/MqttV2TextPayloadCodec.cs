using System;
using System.Collections.Generic;
using System.Text;

namespace UnityMqtt.V2.Core
{
    /// <summary>
    /// テキスト形式ペイロードの Codec 向け抽象基底クラス。
    /// UTF-8 変換・null ガード・エラーハンドリング・timestamp 正規化を提供します。
    /// 派生クラスは <see cref="TryParseFields"/> と <see cref="FormatFields"/> のみ実装してください。
    /// </summary>
    public abstract class MqttV2TextPayloadCodec : IMqttV2PayloadCodec
    {
        public abstract string Id { get; }

        /// <summary>受信テキストを fields と timestamp に変換します。</summary>
        protected abstract bool TryParseFields(
            string text,
            out IReadOnlyDictionary<string, object> fields,
            out DateTime timestampUtc,
            out string error);

        /// <summary>timestamp と fields を送信テキストに変換します。</summary>
        protected abstract string FormatFields(
            DateTime timestampUtc,
            IReadOnlyDictionary<string, object> fields);

        public byte[] Encode(DateTime timestampUtc, IReadOnlyDictionary<string, object> fields)
        {
            if (fields == null)
                throw new ArgumentNullException(nameof(fields));

            var text = FormatFields(NormalizeTimestampOrNow(timestampUtc), fields);
            return Encoding.UTF8.GetBytes(text);
        }

        public bool TryDecode(byte[] payload, out IReadOnlyDictionary<string, object> fields, out DateTime timestampUtc, out string error)
        {
            fields = null;
            timestampUtc = default(DateTime);
            error = null;

            if (payload == null)
            {
                error = "payload is null.";
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

            return TryParseFields(text, out fields, out timestampUtc, out error);
        }

        protected static DateTime NormalizeTimestampOrNow(DateTime timestampUtc)
        {
            if (timestampUtc == default(DateTime))
                return DateTime.UtcNow;

            return timestampUtc.Kind == DateTimeKind.Utc ? timestampUtc : timestampUtc.ToUniversalTime();
        }

        protected static DateTime ParseTimestampOrNow(string timestamp)
        {
            if (DateTime.TryParse(timestamp, out var parsed))
                return parsed.ToUniversalTime();

            return DateTime.UtcNow;
        }
    }
}
