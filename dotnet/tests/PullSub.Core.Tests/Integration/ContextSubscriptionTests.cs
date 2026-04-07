using System;
using System.Threading.Tasks;
using PullSub.Core;
using PullSub.Core.Tests.TestScenarios.Fixtures;

namespace PullSub.Core.Tests.Integration
{
    [Collection("PullSubRuntimeSerial")]
    public sealed class ContextSubscriptionTests
    {
        [Fact]
        public async Task RuntimeSubscribeAsync_ReturnsLeaseAndUnsubscribesOnDisposeAsync()
        {
            var transport = new TestTransport();
            await using var runtime = new PullSubRuntime(transport);
            await runtime.StartAsync();

            var topic = PullSubTopic.Create("test/context/runtime-subscribe", new SampleClassCodec());
            await using var handle = await runtime.SubscribeDataAsync(topic);

            Assert.NotNull(handle);
            Assert.Equal(topic.TopicName, handle.Topic);
            Assert.Equal(1, transport.SubscribeCallCount);

            await handle.DisposeAsync();

            Assert.Equal(1, transport.UnsubscribeCallCount);
        }

        [Fact]
        public async Task ContextSubscribeAsync_DuplicateTopic_DoesNotTriggerSecondNetworkSubscribe()
        {
            var transport = new TestTransport();
            await using var runtime = new PullSubRuntime(transport);
            await runtime.StartAsync();

            await using var context = runtime.CreateContext();
            var topic = PullSubTopic.Create("test/context/duplicate", new SampleClassCodec());

            var first = await context.SubscribeDataAsync(topic);
            Assert.NotNull(first);

            await Assert.ThrowsAsync<InvalidOperationException>(() => context.SubscribeDataAsync(topic));

            Assert.Equal(1, transport.SubscribeCallCount);

            await context.DisposeAsync();

            Assert.Equal(1, transport.UnsubscribeCallCount);
        }

        [Fact]
        public async Task ContextSubscribeAsync_ParallelDuplicateTopic_OnlyOneNetworkSubscribeOccurs()
        {
            var transport = new TestTransport();
            await using var runtime = new PullSubRuntime(transport);
            await runtime.StartAsync();

            await using var context = runtime.CreateContext();
            var topic = PullSubTopic.Create("test/context/parallel-duplicate", new SampleClassCodec());

            var task1 = context.SubscribeDataAsync(topic);
            var task2 = context.SubscribeDataAsync(topic);

            PullSubDataHandle<SampleClassPayload>? subscription1 = null;
            PullSubDataHandle<SampleClassPayload>? subscription2 = null;
            Exception? exception1 = null;
            Exception? exception2 = null;

            try
            {
                subscription1 = await task1;
            }
            catch (Exception ex)
            {
                exception1 = ex;
            }

            try
            {
                subscription2 = await task2;
            }
            catch (Exception ex)
            {
                exception2 = ex;
            }

            Assert.True((exception1 is InvalidOperationException) || (exception2 is InvalidOperationException));
            Assert.True(subscription1 != null || subscription2 != null);
            Assert.Equal(1, transport.SubscribeCallCount);

            await context.DisposeAsync();

            Assert.Equal(1, transport.UnsubscribeCallCount);
        }

        [Fact]
        public async Task DataHandle_UnsubscribeAsync_SecondCallReturnsAlreadyCanceled()
        {
            var transport = new TestTransport();
            await using var runtime = new PullSubRuntime(transport);
            await runtime.StartAsync();

            var topic = PullSubTopic.Create("test/context/data-idempotent-unsubscribe", new SampleClassCodec());
            var handle = await runtime.SubscribeDataAsync(topic);

            var first = await handle.UnsubscribeAsync();
            var unsubscribeCallsAfterFirst = transport.UnsubscribeCallCount;
            var second = await handle.UnsubscribeAsync();

            Assert.Equal(PullSubUnsubscribeResult.Success, first);
            Assert.Equal(PullSubUnsubscribeResult.AlreadyCanceled, second);
            Assert.Equal(unsubscribeCallsAfterFirst, transport.UnsubscribeCallCount);
        }

        [Fact]
        public async Task QueueLease_UnsubscribeAsync_SecondCallReturnsAlreadyCanceled()
        {
            var transport = new TestTransport();
            await using var runtime = new PullSubRuntime(transport);
            await runtime.StartAsync();

            var registration = await runtime.SubscribeQueueAsync(
                "test/context/queue-idempotent-unsubscribe",
                PullSubQueueOptions.Default,
                static _ => { });

            var first = await registration.UnsubscribeAsync();
            var second = await registration.UnsubscribeAsync();

            Assert.Equal(PullSubUnsubscribeResult.Success, first);
            Assert.Equal(PullSubUnsubscribeResult.AlreadyCanceled, second);
            Assert.Equal(1, transport.UnsubscribeCallCount);
        }
    }
}