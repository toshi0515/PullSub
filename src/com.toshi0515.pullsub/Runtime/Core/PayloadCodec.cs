using System;
using System.Buffers;

namespace PullSub.Core
{
    /// <summary>
    /// Standard typed payload codec interface.
    /// The encode contract uses <see cref="IBufferWriter{T}"/> to avoid intermediate array allocation before sending.
    /// </summary>
    public interface IPayloadCodec<T>
    {
        void Encode(DateTime timestampUtc, T value, IBufferWriter<byte> bufferWriter);
        bool TryDecode(ReadOnlySpan<byte> payload, out T value, out DateTime timestampUtc, out string error);
    }

    /// <summary>
    /// [Experimental] In-place decode contract that reuses existing instances.
    /// Designed to reduce GC pressure when using class payloads with high-frequency reception.
    /// </summary>
    public interface IPayloadInPlaceCodec<T> : IPayloadCodec<T>
    {
        bool TryDecodeInPlace(ReadOnlySpan<byte> payload, T destination, out DateTime timestampUtc, out string error);
    }
}
