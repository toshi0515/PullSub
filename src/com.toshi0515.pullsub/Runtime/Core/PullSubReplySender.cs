using System;
using System.Threading;
using System.Threading.Tasks;

namespace PullSub.Core
{
    /// <summary>
    /// One-shot reply sender used by request responder handlers.
    /// </summary>
    public sealed class PullSubReplySender<TResponse>
    {
        private readonly Func<PullSubResponseEnvelope<TResponse>, CancellationToken, Task> _sendAsync;
        private readonly string _correlationId;
        private int _sent;

        public PullSubReplySender(
            string correlationId,
            Func<PullSubResponseEnvelope<TResponse>, CancellationToken, Task> sendAsync)
        {
            if (string.IsNullOrWhiteSpace(correlationId))
                throw new ArgumentException("correlationId is required.", nameof(correlationId));

            _correlationId = correlationId;
            _sendAsync = sendAsync ?? throw new ArgumentNullException(nameof(sendAsync));
        }

        public string CorrelationId => _correlationId;

        public Task SendAsync(TResponse response, CancellationToken cancellationToken = default)
        {
            return SendCoreAsync(
                new PullSubResponseEnvelope<TResponse>
                {
                    CorrelationId = _correlationId,
                    Status = PullSubResponseEnvelopeStatus.Success,
                    ErrorMessage = null,
                    RespondedUtc = DateTime.UtcNow,
                    Response = response,
                },
                cancellationToken);
        }

        public Task SendErrorAsync(string errorMessage, CancellationToken cancellationToken = default)
        {
            return SendCoreAsync(
                new PullSubResponseEnvelope<TResponse>
                {
                    CorrelationId = _correlationId,
                    Status = PullSubResponseEnvelopeStatus.RemoteError,
                    ErrorMessage = errorMessage ?? string.Empty,
                    RespondedUtc = DateTime.UtcNow,
                    Response = default,
                },
                cancellationToken);
        }

        private Task SendCoreAsync(
            PullSubResponseEnvelope<TResponse> envelope,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _sent, 1) == 1)
                throw new InvalidOperationException("Reply is already sent. PullSubReplySender is one-shot.");

            return _sendAsync(envelope, cancellationToken);
        }
    }
}
