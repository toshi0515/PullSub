using System;
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace PullSub.Core
{
    // Request-Reply wire contract: envelope is JSON; payload shape depends on request/response codec kind.
    public enum ResponseEnvelopeStatus
    {
        Success = 0,
        RemoteError = 1,
    }

    public struct RequestEnvelope<TRequest>
    {
        public string CorrelationId { get; set; }
        public string ReplyTo { get; set; }
        public DateTime SentUtc { get; set; }
        public DateTime DeadlineUtc { get; set; }
        public TRequest Request { get; set; }
    }

    public struct ResponseEnvelope<TResponse>
    {
        public string CorrelationId { get; set; }
        public ResponseEnvelopeStatus Status { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime RespondedUtc { get; set; }
        public TResponse Response { get; set; }
    }

    public sealed class RequestEnvelopeCodec<TRequest> : IPayloadCodec<RequestEnvelope<TRequest>>
    {
        private static readonly JsonSerializerOptions DefaultOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        public static readonly RequestEnvelopeCodec<TRequest> Default
            = new RequestEnvelopeCodec<TRequest>();

        private readonly JsonSerializerOptions _options;
        private readonly IPayloadCodec<TRequest> _requestCodec;

        public RequestEnvelopeCodec(
            IPayloadCodec<TRequest> requestCodec = null,
            JsonSerializerOptions options = null)
        {
            _requestCodec = requestCodec ?? JsonPayloadCodec<TRequest>.Default;
            _options = options ?? DefaultOptions;
        }

        public override bool Equals(object obj)
        {
            return obj is RequestEnvelopeCodec<TRequest> other
                && ReferenceEquals(_options, other._options)
                && Equals(_requestCodec, other._requestCodec);
        }

        public override int GetHashCode()
        {
            return _options.GetHashCode();
        }

        public void Encode(
            DateTime timestampUtc,
            RequestEnvelope<TRequest> value,
            IBufferWriter<byte> bufferWriter)
        {
            if (bufferWriter == null)
                throw new ArgumentNullException(nameof(bufferWriter));

            if (string.IsNullOrWhiteSpace(value.CorrelationId))
                throw new ArgumentException("CorrelationId is required.", nameof(value));

            if (string.IsNullOrWhiteSpace(value.ReplyTo))
                throw new ArgumentException("ReplyTo is required.", nameof(value));

            var normalized = TimestampUtility.NormalizeOrNow(timestampUtc);
            var envelope = value;

            if (envelope.SentUtc == default)
                envelope.SentUtc = normalized;

            if (envelope.DeadlineUtc == default)
                envelope.DeadlineUtc = envelope.SentUtc;

            using var writer = new Utf8JsonWriter(bufferWriter);
            writer.WriteStartObject();
            writer.WriteString("correlationId", envelope.CorrelationId);
            writer.WriteString("replyTo", envelope.ReplyTo);
            writer.WriteString("sentUtc", envelope.SentUtc);
            writer.WriteString("deadlineUtc", envelope.DeadlineUtc);

            if (UseInlineJsonRequest)
            {
                writer.WritePropertyName("request");
                JsonSerializer.Serialize(writer, envelope.Request, _options);
            }
            else
            {
                var requestWriter = new ArrayBufferWriter<byte>(256);
                _requestCodec.Encode(envelope.SentUtc, envelope.Request, requestWriter);
                var requestBytes = requestWriter.WrittenCount == 0
                    ? Array.Empty<byte>()
                    : requestWriter.WrittenMemory.ToArray();

                writer.WriteString("requestPayload", Convert.ToBase64String(requestBytes));
            }

            writer.WriteEndObject();
        }

        public bool TryDecode(
            ReadOnlySpan<byte> payload,
            out RequestEnvelope<TRequest> value,
            out DateTime timestampUtc,
            out string error)
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
                using var document = JsonDocument.Parse(payload.ToArray());
                var root = document.RootElement;

                var correlationId = GetString(root, "correlationId");
                if (string.IsNullOrWhiteSpace(correlationId))
                {
                    error = "CorrelationId is required.";
                    return false;
                }

                var replyTo = GetString(root, "replyTo");
                if (string.IsNullOrWhiteSpace(replyTo))
                {
                    error = "ReplyTo is required.";
                    return false;
                }

                var sentUtc = GetDateTimeOrDefault(root, "sentUtc");
                var deadlineUtc = GetDateTimeOrDefault(root, "deadlineUtc");
                if (!TryDecodeRequest(root, out var request, out var requestTimestampUtc, out var decodeError))
                {
                    error = decodeError;
                    return false;
                }

                timestampUtc = sentUtc == default
                    ? TimestampUtility.NormalizeOrNow(requestTimestampUtc)
                    : TimestampUtility.NormalizeOrNow(sentUtc);

                if (sentUtc == default)
                    sentUtc = timestampUtc;

                value = new RequestEnvelope<TRequest>
                {
                    CorrelationId = correlationId,
                    ReplyTo = replyTo,
                    SentUtc = sentUtc,
                    DeadlineUtc = deadlineUtc,
                    Request = request,
                };

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private bool UseInlineJsonRequest
            => _requestCodec is JsonPayloadCodec<TRequest>
            || _requestCodec is FlatJsonPayloadCodec<TRequest>;

        private bool TryDecodeRequest(
            JsonElement root,
            out TRequest request,
            out DateTime requestTimestampUtc,
            out string error)
        {
            request = default;
            requestTimestampUtc = default;
            error = null;

            if (root.TryGetProperty("request", out var requestElement))
            {
                try
                {
                    request = requestElement.Deserialize<TRequest>(_options);
                    return true;
                }
                catch (Exception ex)
                {
                    error = $"request decode failed. {ex.Message}";
                    return false;
                }
            }

            if (UseInlineJsonRequest)
            {
                error = "request is required.";
                return false;
            }

            var payloadBase64 = GetString(root, "requestPayload");
            if (payloadBase64 == null)
            {
                error = "requestPayload is required.";
                return false;
            }

            byte[] requestBytes;
            try
            {
                requestBytes = payloadBase64.Length == 0
                    ? Array.Empty<byte>()
                    : Convert.FromBase64String(payloadBase64);
            }
            catch (FormatException)
            {
                error = "requestPayload is not valid base64.";
                return false;
            }

            if (!_requestCodec.TryDecode(requestBytes, out request, out requestTimestampUtc, out var decodeError))
            {
                error = $"requestPayload decode failed. {decodeError}";
                return false;
            }

            return true;
        }

        private static string GetString(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var element))
                return null;

            return element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : element.ToString();
        }

        private static DateTime GetDateTimeOrDefault(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var element))
                return default;

            if (element.ValueKind == JsonValueKind.String && element.TryGetDateTime(out var value))
                return value;

            return default;
        }
    }

    public sealed class ResponseEnvelopeCodec<TResponse> : IPayloadCodec<ResponseEnvelope<TResponse>>
    {
        private static readonly JsonSerializerOptions DefaultOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        public static readonly ResponseEnvelopeCodec<TResponse> Default
            = new ResponseEnvelopeCodec<TResponse>();

        private readonly JsonSerializerOptions _options;
        private readonly IPayloadCodec<TResponse> _responseCodec;

        public ResponseEnvelopeCodec(
            IPayloadCodec<TResponse> responseCodec = null,
            JsonSerializerOptions options = null)
        {
            _responseCodec = responseCodec ?? JsonPayloadCodec<TResponse>.Default;
            _options = options ?? DefaultOptions;
        }

        public override bool Equals(object obj)
        {
            return obj is ResponseEnvelopeCodec<TResponse> other
                && ReferenceEquals(_options, other._options)
                && Equals(_responseCodec, other._responseCodec);
        }

        public override int GetHashCode()
        {
            return _options.GetHashCode();
        }

        public void Encode(
            DateTime timestampUtc,
            ResponseEnvelope<TResponse> value,
            IBufferWriter<byte> bufferWriter)
        {
            if (bufferWriter == null)
                throw new ArgumentNullException(nameof(bufferWriter));

            if (string.IsNullOrWhiteSpace(value.CorrelationId))
                throw new ArgumentException("CorrelationId is required.", nameof(value));

            var normalized = TimestampUtility.NormalizeOrNow(timestampUtc);
            var envelope = value;

            if (envelope.RespondedUtc == default)
                envelope.RespondedUtc = normalized;

            if (!UseInlineJsonResponse)
            {
                var responseWriter = new ArrayBufferWriter<byte>(256);
                _responseCodec.Encode(envelope.RespondedUtc, envelope.Response, responseWriter);
                var responseBytes = responseWriter.WrittenCount == 0
                    ? Array.Empty<byte>()
                    : responseWriter.WrittenMemory.ToArray();

                using var payloadWriter = new Utf8JsonWriter(bufferWriter);
                payloadWriter.WriteStartObject();
                payloadWriter.WriteString("correlationId", envelope.CorrelationId);
                payloadWriter.WriteNumber("status", (int)envelope.Status);
                payloadWriter.WriteString("errorMessage", envelope.ErrorMessage ?? string.Empty);
                payloadWriter.WriteString("respondedUtc", envelope.RespondedUtc);
                payloadWriter.WriteString("responsePayload", Convert.ToBase64String(responseBytes));
                payloadWriter.WriteEndObject();
                return;
            }

            using var writer = new Utf8JsonWriter(bufferWriter);
            writer.WriteStartObject();
            writer.WriteString("correlationId", envelope.CorrelationId);
            writer.WriteNumber("status", (int)envelope.Status);
            writer.WriteString("errorMessage", envelope.ErrorMessage ?? string.Empty);
            writer.WriteString("respondedUtc", envelope.RespondedUtc);
            writer.WritePropertyName("response");
            JsonSerializer.Serialize(writer, envelope.Response, _options);
            writer.WriteEndObject();
        }

        public bool TryDecode(
            ReadOnlySpan<byte> payload,
            out ResponseEnvelope<TResponse> value,
            out DateTime timestampUtc,
            out string error)
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
                using var document = JsonDocument.Parse(payload.ToArray());
                var root = document.RootElement;

                var correlationId = GetString(root, "correlationId");
                if (string.IsNullOrWhiteSpace(correlationId))
                {
                    error = "CorrelationId is required.";
                    return false;
                }

                if (!TryReadStatus(root, out var status))
                {
                    error = "status is required.";
                    return false;
                }

                var errorMessage = GetString(root, "errorMessage") ?? string.Empty;
                var respondedUtc = GetDateTimeOrDefault(root, "respondedUtc");
                if (!TryDecodeResponse(root, out var response, out var responseTimestampUtc, out var decodeError))
                {
                    error = decodeError;
                    return false;
                }

                timestampUtc = respondedUtc == default
                    ? TimestampUtility.NormalizeOrNow(responseTimestampUtc)
                    : TimestampUtility.NormalizeOrNow(respondedUtc);

                if (respondedUtc == default)
                    respondedUtc = timestampUtc;

                value = new ResponseEnvelope<TResponse>
                {
                    CorrelationId = correlationId,
                    Status = status,
                    ErrorMessage = errorMessage,
                    RespondedUtc = respondedUtc,
                    Response = response,
                };

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private bool UseInlineJsonResponse
            => _responseCodec is JsonPayloadCodec<TResponse>
            || _responseCodec is FlatJsonPayloadCodec<TResponse>;

        private bool TryDecodeResponse(
            JsonElement root,
            out TResponse response,
            out DateTime responseTimestampUtc,
            out string error)
        {
            response = default;
            responseTimestampUtc = default;
            error = null;

            if (root.TryGetProperty("response", out var responseElement))
            {
                try
                {
                    response = responseElement.Deserialize<TResponse>(_options);
                    return true;
                }
                catch (Exception ex)
                {
                    error = $"response decode failed. {ex.Message}";
                    return false;
                }
            }

            if (UseInlineJsonResponse)
            {
                error = "response is required.";
                return false;
            }

            var payloadBase64 = GetString(root, "responsePayload");
            if (payloadBase64 == null)
            {
                error = "responsePayload is required.";
                return false;
            }

            byte[] responseBytes;
            try
            {
                responseBytes = payloadBase64.Length == 0
                    ? Array.Empty<byte>()
                    : Convert.FromBase64String(payloadBase64);
            }
            catch (FormatException)
            {
                error = "responsePayload is not valid base64.";
                return false;
            }

            if (!_responseCodec.TryDecode(responseBytes, out response, out responseTimestampUtc, out var decodeError))
            {
                error = $"responsePayload decode failed. {decodeError}";
                return false;
            }

            return true;
        }

        internal static bool TryReadCorrelationId(ReadOnlySpan<byte> payload, out string correlationId, out string error)
        {
            correlationId = null;
            error = null;

            if (payload.IsEmpty)
            {
                error = "payload is empty.";
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(payload.ToArray());
                var root = document.RootElement;
                var value = GetString(root, "correlationId");
                if (string.IsNullOrWhiteSpace(value))
                {
                    error = "CorrelationId is required.";
                    return false;
                }

                correlationId = value;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryReadStatus(JsonElement root, out ResponseEnvelopeStatus status)
        {
            status = ResponseEnvelopeStatus.Success;

            if (!root.TryGetProperty("status", out var element))
                return false;

            if (element.ValueKind == JsonValueKind.Number)
            {
                if (element.TryGetInt32(out var intValue)
                    && Enum.IsDefined(typeof(ResponseEnvelopeStatus), intValue))
                {
                    status = (ResponseEnvelopeStatus)intValue;
                    return true;
                }

                return false;
            }

            if (element.ValueKind == JsonValueKind.String)
            {
                var str = element.GetString();
                return Enum.TryParse(str, ignoreCase: true, out status);
            }

            return false;
        }

        private static string GetString(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var element))
                return null;

            return element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : element.ToString();
        }

        private static DateTime GetDateTimeOrDefault(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var element))
                return default;

            if (element.ValueKind == JsonValueKind.String && element.TryGetDateTime(out var value))
                return value;

            return default;
        }
    }

    internal sealed class RawBinaryPayloadCodec : IPayloadCodec<byte[]>
    {
        public static readonly RawBinaryPayloadCodec Default = new RawBinaryPayloadCodec();

        public void Encode(DateTime timestampUtc, byte[] value, IBufferWriter<byte> bufferWriter)
        {
            if (bufferWriter == null)
                throw new ArgumentNullException(nameof(bufferWriter));

            var source = value ?? Array.Empty<byte>();
            if (source.Length == 0)
                return;

            var span = bufferWriter.GetSpan(source.Length);
            source.AsSpan().CopyTo(span);
            bufferWriter.Advance(source.Length);
        }

        public bool TryDecode(ReadOnlySpan<byte> payload, out byte[] value, out DateTime timestampUtc, out string error)
        {
            value = payload.Length == 0 ? Array.Empty<byte>() : payload.ToArray();
            timestampUtc = DateTime.UtcNow;
            error = null;
            return true;
        }
    }
}
