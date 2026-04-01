using System;

namespace PullSub.Core
{
    /// <summary>
    /// 型付きペイロード Codec の標準インターフェース。
    /// Codec を書く = ペイロード構造と期待する型 T を両方知っている という責務を持ちます。
    /// </summary>
    public interface IPayloadCodec<T>
    {
        byte[] Encode(DateTime timestampUtc, T value);
        bool TryDecode(ReadOnlySpan<byte> payload, out T value, out DateTime timestampUtc, out string error);
    }
}
