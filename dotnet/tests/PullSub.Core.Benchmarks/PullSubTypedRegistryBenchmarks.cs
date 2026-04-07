using System;
using System.Buffers;
using BenchmarkDotNet.Attributes;
using PullSub.Tests.PerfShared;

namespace PullSub.Core.Benchmarks
{
    [MemoryDiagnoser]
    public class PullSubTypedRegistryBenchmarks
    {
        private const string Topic = "bench/registry/topic";

        private readonly global::PullSub.Core.IPayloadCodec<Phase0PerfDataPayload> _codec
            = global::PullSub.Core.JsonPayloadCodec<Phase0PerfDataPayload>.Default;

        private global::PullSub.Core.TypedDataRegistry? _registry;
        private byte[] _payload = Array.Empty<byte>();

        [GlobalSetup]
        public void Setup()
        {
            _payload = Encode(_codec, new Phase0PerfDataPayload
            {
                Sequence = 100,
                SentUtcTicks = DateTime.UtcNow.Ticks,
                X = 1,
                Y = 2,
                Z = 3,
            });

            _registry = new global::PullSub.Core.TypedDataRegistry();
            _registry.Register(Topic, _codec, out _);
            _registry.TryDecodeAndUpdate(Topic, _payload);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _registry?.CancelAll();
            _registry = null;
        }

        [Benchmark]
        public bool RegistryTryGetCache()
        {
            return _registry!.TryGetCache<Phase0PerfDataPayload>(Topic, out _);
        }

        [Benchmark]
        public bool RegistryDecodeAndUpdate()
        {
            return _registry!.TryDecodeAndUpdate(Topic, _payload);
        }

        [Benchmark]
        public bool RegisterThenUnregister()
        {
            var topic = "bench/registry/one-shot";
            _registry!.Register(topic, _codec, out _);
            return _registry.Unregister(topic);
        }

        private static byte[] Encode<T>(global::PullSub.Core.IPayloadCodec<T> codec, T value)
        {
            var buffer = new ArrayBufferWriter<byte>(256);
            codec.Encode(DateTime.UtcNow, value, buffer);
            return buffer.WrittenMemory.ToArray();
        }
    }
}