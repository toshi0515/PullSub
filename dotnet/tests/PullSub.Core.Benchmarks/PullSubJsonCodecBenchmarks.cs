using System;
using System.Buffers;
using BenchmarkDotNet.Attributes;
using PullSub.Tests.PerfShared;

namespace PullSub.Core.Benchmarks
{
    [MemoryDiagnoser]
    public class PullSubJsonCodecBenchmarks
    {
        private readonly global::PullSub.Core.JsonPayloadCodec<Phase0PerfDataPayload> _jsonCodec = global::PullSub.Core.JsonPayloadCodec<Phase0PerfDataPayload>.Default;
        private readonly global::PullSub.Core.FlatJsonPayloadCodec<Phase0PerfDataPayload> _flatCodec = global::PullSub.Core.FlatJsonPayloadCodec<Phase0PerfDataPayload>.Default;

        private Phase0PerfDataPayload _payload;
        private byte[] _jsonEncoded = Array.Empty<byte>();
        private byte[] _flatEncoded = Array.Empty<byte>();

        [GlobalSetup]
        public void Setup()
        {
            _payload = new Phase0PerfDataPayload
            {
                Sequence = 42,
                SentUtcTicks = DateTime.UtcNow.Ticks,
                X = 1.25f,
                Y = -2.5f,
                Z = 3.75f,
            };

            _jsonEncoded = EncodeToArray(_jsonCodec, _payload);
            _flatEncoded = EncodeToArray(_flatCodec, _payload);
        }

        [Benchmark]
        public int JsonEncodeBytes()
        {
            return EncodeToArray(_jsonCodec, _payload).Length;
        }

        [Benchmark]
        public int FlatJsonEncodeBytes()
        {
            return EncodeToArray(_flatCodec, _payload).Length;
        }

        [Benchmark]
        public Phase0PerfDataPayload JsonDecode()
        {
            _jsonCodec.TryDecode(_jsonEncoded, out var value, out _, out _);
            return value;
        }

        [Benchmark]
        public Phase0PerfDataPayload FlatJsonDecode()
        {
            _flatCodec.TryDecode(_flatEncoded, out var value, out _, out _);
            return value;
        }

        private static byte[] EncodeToArray<T>(global::PullSub.Core.IPayloadCodec<T> codec, T value)
        {
            var buffer = new ArrayBufferWriter<byte>(256);
            codec.Encode(DateTime.UtcNow, value, buffer);
            return buffer.WrittenMemory.ToArray();
        }
    }
}
