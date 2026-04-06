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
            private Func<Task>? _onConnected;
            private Func<string, Task>? _onDisconnected;
            private Func<string, ReadOnlyMemory<byte>, Task>? _onMessageReceived;
            private int _callbacksSet;

            public bool IsConnected { get; private set; }
            public global::PullSub.Core.PullSubReconnectOptions ReconnectOptions { get; }
                = global::PullSub.Core.PullSubReconnectOptions.Default;

            public void SetCallbacks(
                Func<Task> onConnected,
                Func<string, Task> onDisconnected,
                Func<string, ReadOnlyMemory<byte>, Task> onMessageReceived)
            {
                if (Interlocked.Exchange(ref _callbacksSet, 1) == 1)
                    throw new InvalidOperationException("Callbacks are already set.");

                _onConnected = onConnected;
                _onDisconnected = onDisconnected;
                _onMessageReceived = onMessageReceived;
            }

            public Task ConnectAsync(CancellationToken cancellationToken)
            {
                IsConnected = true;
                return _onConnected?.Invoke() ?? Task.CompletedTask;
            }

            public Task DisconnectAsync(bool cleanDisconnect, CancellationToken cancellationToken)
            {
                IsConnected = false;
                return _onDisconnected?.Invoke(cleanDisconnect ? "Clean" : "Force") ?? Task.CompletedTask;
            }

            public Task PublishAsync(
                string topic,
                byte[] payload,
                global::PullSub.Core.PullSubQualityOfServiceLevel qos,
                bool retain,
                CancellationToken cancellationToken)
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
                (_onMessageReceived?.Invoke(topic, payload) ?? Task.CompletedTask).GetAwaiter().GetResult();
            }

            public ValueTask DisposeAsync()
            {
                IsConnected = false;
                return default;
            }
        }
    }
}
