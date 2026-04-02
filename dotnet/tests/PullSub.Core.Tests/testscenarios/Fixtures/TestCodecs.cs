using System;
using System.Buffers;
using System.Buffers.Binary;
using PullSub.Core;

#nullable disable

namespace PullSub.Core.Tests.TestScenarios.Fixtures
{
    public sealed class SampleClassPayload
    {
        public int Value { get; set; }
        public int Counter { get; set; }
    }

    internal sealed class SampleClassCodec : IPayloadCodec<SampleClassPayload>, IPayloadInPlaceCodec<SampleClassPayload>
    {
        public void Encode(DateTime timestampUtc, SampleClassPayload value, IBufferWriter<byte> bufferWriter)
        {
            var span = bufferWriter.GetSpan(8);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(0, 4), value?.Value ?? 0);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4, 4), value?.Counter ?? 0);
            bufferWriter.Advance(8);
        }

        public bool TryDecode(ReadOnlySpan<byte> payload, out SampleClassPayload value, out DateTime timestampUtc, out string error)
        {
            value = null;
            timestampUtc = DateTime.UtcNow;
            error = null;

            if (payload.Length < 8)
            {
                error = "payload too small";
                return false;
            }

            value = new SampleClassPayload
            {
                Value = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(0, 4)),
                Counter = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4)),
            };

            return true;
        }

        public bool TryDecodeInPlace(ReadOnlySpan<byte> payload, SampleClassPayload destination, out DateTime timestampUtc, out string error)
        {
            timestampUtc = DateTime.UtcNow;
            error = null;

            if (destination == null)
            {
                error = "destination is null";
                return false;
            }

            if (payload.Length < 8)
            {
                error = "payload too small";
                return false;
            }

            destination.Value = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(0, 4));
            destination.Counter = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4));
            return true;
        }
    }

    internal sealed class SampleClassCodecWithoutInPlace : IPayloadCodec<SampleClassPayload>
    {
        public void Encode(DateTime timestampUtc, SampleClassPayload value, IBufferWriter<byte> bufferWriter)
        {
            var span = bufferWriter.GetSpan(8);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(0, 4), value?.Value ?? 0);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4, 4), value?.Counter ?? 0);
            bufferWriter.Advance(8);
        }

        public bool TryDecode(ReadOnlySpan<byte> payload, out SampleClassPayload value, out DateTime timestampUtc, out string error)
        {
            value = null;
            timestampUtc = DateTime.UtcNow;
            error = null;

            if (payload.Length < 8)
            {
                error = "payload too small";
                return false;
            }

            value = new SampleClassPayload
            {
                Value = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(0, 4)),
                Counter = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4)),
            };

            return true;
        }
    }
}
