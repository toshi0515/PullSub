using System;
using System.Buffers;
using System.Text;

namespace PullSub.Core
{
    /// <summary>
    /// Base class for typed codecs for text-based payloads.
    /// Provides UTF-8 conversion, error handling, and timestamp normalization.
    /// Derived classes should implement only <see cref="TryParsePayload"/> and <see cref="FormatPayload"/>.
    /// </summary>
    public abstract class TextPayloadCodec<T> : IPayloadCodec<T>
    {
        /// <summary>Converts received text to value and timestamp.</summary>
        protected abstract bool TryParsePayload(
            string text,
            out T value,
            out DateTime timestampUtc,
            out string error);

        /// <summary>Converts timestamp and value to text for transmission.</summary>
        protected abstract string FormatPayload(DateTime timestampUtc, T value);

        public virtual void Encode(DateTime timestampUtc, T value, IBufferWriter<byte> bufferWriter)
        {
            if (bufferWriter == null)
                throw new ArgumentNullException(nameof(bufferWriter));

            var text = FormatPayload(NormalizeTimestampOrNow(timestampUtc), value);
            if (text == null)
                throw new InvalidOperationException("Codec returned null text payload.");

            var byteCount = Encoding.UTF8.GetByteCount(text);
            var span = bufferWriter.GetSpan(byteCount);
            var bytesWritten = Encoding.UTF8.GetBytes(text.AsSpan(), span);
            bufferWriter.Advance(bytesWritten);
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
            return TimestampUtility.NormalizeOrNow(timestampUtc);
        }

        protected static DateTime ParseTimestampOrNow(string timestamp)
        {
            if (DateTime.TryParse(timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();

            return DateTime.UtcNow;
        }
    }
}
