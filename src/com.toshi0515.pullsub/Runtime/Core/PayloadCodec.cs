using System;
using System.Buffers;

namespace PullSub.Core
{
    /// <summary>
    /// 型付きペイロード Codec の標準インターフェースです。
    /// エンコードは <see cref="IBufferWriter{T}"/> への書き込み契約を採用し、
    /// 送信前の中間配列生成を避けます。
    /// </summary>
    public interface IPayloadCodec<T>
    {
        void Encode(DateTime timestampUtc, T value, IBufferWriter<byte> bufferWriter);
        bool TryDecode(ReadOnlySpan<byte> payload, out T value, out DateTime timestampUtc, out string error);
    }

    /// <summary>
    /// 既存インスタンスを再利用する in-place decode 契約です。
    /// class payload を高頻度受信で使う場合の GC 抑制を目的とします。
    /// </summary>
    public interface IPayloadInPlaceCodec<T> : IPayloadCodec<T>
    {
        bool TryDecodeInPlace(ReadOnlySpan<byte> payload, T destination, out DateTime timestampUtc, out string error);
    }
}
