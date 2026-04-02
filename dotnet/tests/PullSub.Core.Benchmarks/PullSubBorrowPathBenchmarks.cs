using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using PullSub.Tests.PerfShared;

namespace PullSub.Core.Benchmarks
{
    [MemoryDiagnoser]
    public class PullSubBorrowPathBenchmarks
    {
        private const string DataTopic = "bench/borrow/data";
        private const string MixedTopic = "bench/borrow/mixed";

        private readonly BenchmarkTransport _transport = new BenchmarkTransport();
        private readonly global::PullSub.Core.PullSubJsonPayloadCodec<Phase0PerfDataPayload> _codec = global::PullSub.Core.PullSubJsonPayloadCodec<Phase0PerfDataPayload>.Default;

        private global::PullSub.Core.PullSubRuntime? _runtime;
        private byte[] _encoded = Array.Empty<byte>();

        [GlobalSetup]
        public void Setup()
        {
            _runtime = new global::PullSub.Core.PullSubRuntime(_transport);
            _runtime.StartAsync().GetAwaiter().GetResult();
            _runtime.SubscribeDataAsync(DataTopic, _codec).GetAwaiter().GetResult();
            _runtime.SubscribeDataAsync(MixedTopic, _codec).GetAwaiter().GetResult();
            _runtime.SubscribeQueueAsync(MixedTopic, new global::PullSub.Core.PullSubQueueOptions(1024)).GetAwaiter().GetResult();

            var payload = new Phase0PerfDataPayload
            {
                Sequence = 1,
                SentUtcTicks = DateTime.UtcNow.Ticks,
                X = 1,
                Y = 2,
                Z = 3,
            };

            _encoded = Encode(_codec, payload);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            if (_runtime == null)
                return;

            _runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _runtime = null;
        }

        [Benchmark]
        public bool DispatchDataOnly_BorrowPath()
        {
            _transport.EmitMessage(DataTopic, _encoded);
            return _runtime!.TryGetData<Phase0PerfDataPayload>(DataTopic, out _);
        }

        [Benchmark]
        public int DispatchMixed_CopyOnQueuePath()
        {
            _transport.EmitMessage(MixedTopic, _encoded);
            _runtime!.TryDequeue(MixedTopic, out var message);
            return message?.Payload?.Length ?? 0;
        }

        private static byte[] Encode<T>(global::PullSub.Core.IPayloadCodec<T> codec, T value)
        {
            var buffer = new ArrayBufferWriter<byte>(256);
            codec.Encode(DateTime.UtcNow, value, buffer);
            return buffer.WrittenMemory.ToArray();
        }

        private sealed class BenchmarkTransport : global::PullSub.Core.ITransport
        {
            public Func<Task>? OnConnected { get; set; }
            public Func<string, Task>? OnDisconnected { get; set; }
            public Func<string, ReadOnlyMemory<byte>, Task>? OnMessageReceived { get; set; }

            public bool IsStarted { get; private set; }
            public bool IsConnected { get; private set; }

            public Task StartAsync(CancellationToken cancellationToken)
            {
                IsStarted = true;
                IsConnected = true;
                return OnConnected?.Invoke() ?? Task.CompletedTask;
            }

            public Task StopAsync(bool cleanDisconnect, CancellationToken cancellationToken)
            {
                IsConnected = false;
                IsStarted = false;
                return OnDisconnected?.Invoke(cleanDisconnect ? "Clean" : "Force") ?? Task.CompletedTask;
            }

            public Task EnqueueAsync(string topic, byte[] payload, global::PullSub.Core.PullSubQualityOfServiceLevel qos, bool retain)
            {
                return Task.CompletedTask;
            }

            public Task SubscribeAsync(string topic, global::PullSub.Core.PullSubQualityOfServiceLevel qos, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task UnsubscribeAsync(string topic, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public void EmitMessage(string topic, byte[] payload)
            {
                (OnMessageReceived?.Invoke(topic, payload) ?? Task.CompletedTask).GetAwaiter().GetResult();
            }
        }
    }
}
