using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using PullSub.Core;
using UnityEngine;

namespace PullSub.Bridge
{
    [Serializable]
    public sealed class PullSubDataCacheTopicBinding
    {
        // PullSubTopicKey is used only for Inspector configuration.
        public PullSubTopicKey key;
        public PullSubQualityOfServiceLevel subscribeQos = PullSubQualityOfServiceLevel.AtLeastOnce;
    }

    [Serializable]
    public sealed class PullSubDataCacheTopicStatus
    {
        public PullSubTopicKey key;
        public bool isSubscribed;
        public string error;
    }

    public sealed class PullSubDataCache : MonoBehaviour
    {
        [SerializeField] private PullSubMqttClient _client;
        [SerializeField] private List<PullSubDataCacheTopicBinding> _topics = new List<PullSubDataCacheTopicBinding>();

        private readonly object _gate = new object();
        private readonly Dictionary<PullSubTopicKey, IPullSubSubscriptionLease> _subscriptions =
            new Dictionary<PullSubTopicKey, IPullSubSubscriptionLease>();

        private readonly Dictionary<PullSubTopicKey, PullSubDataCacheTopicStatus> _statuses =
            new Dictionary<PullSubTopicKey, PullSubDataCacheTopicStatus>();

        private PullSubContext _context;
        private CancellationTokenSource _lifecycleCts;

        public IReadOnlyCollection<PullSubDataCacheTopicStatus> TopicStatuses
        {
            get
            {
                lock (_gate)
                {
                    return _statuses.Values.ToArray();
                }
            }
        }

        private void Start()
        {
            _lifecycleCts = new CancellationTokenSource();
            StartAsync(_lifecycleCts.Token).Forget();
        }

        private async UniTaskVoid StartAsync(CancellationToken cancellationToken)
        {
            if (_client == null)
            {
                Debug.LogError("[PullSubDataCache] PullSubMqttClient is not assigned.");
                return;
            }

            try
            {
                await _client.Runtime.StartAsync(cancellationToken);
                await _client.Runtime.WaitUntilConnectedAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PullSubDataCache] Runtime startup failed: {ex.Message}");
                return;
            }

            lock (_gate)
            {
                _context?.Dispose();
                _context = _client.Runtime.CreateContext();
            }

            var tasks = new List<UniTask>();
            foreach (var binding in _topics)
            {
                tasks.Add(SubscribeSingleAsync(binding, cancellationToken));
            }

            await UniTask.WhenAll(tasks);
        }

        private async UniTask SubscribeSingleAsync(PullSubDataCacheTopicBinding binding, CancellationToken cancellationToken)
        {
            var key = binding.key;

            if (key.IsEmpty)
            {
                SetStatus(key, isSubscribed: false, error: "Topic key is empty.");
                return;
            }

            if (!TryReserveSubscriptionSlot(key))
            {
                SetStatus(key, isSubscribed: false, error: "Duplicate topic key in PullSubDataCache settings.");
                return;
            }

            if (!PullSubTopicCatalog.TryGet(key, out var registration))
            {
                ClearSubscriptionSlot(key);
                SetStatus(key, isSubscribed: false, error: $"Topic key '{key}' is not registered in PullSubTopicCatalog.");
                return;
            }

            PullSubContext context;
            lock (_gate)
            {
                context = _context;
            }

            if (context == null)
            {
                ClearSubscriptionSlot(key);
                SetStatus(key, isSubscribed: false, error: "PullSubContext is not available.");
                return;
            }

            try
            {
                var subscription = await registration.SubscribeAsync(context, binding.subscribeQos, cancellationToken);
                CommitSubscriptionSlot(key, subscription);
                SetStatus(key, isSubscribed: true, error: null);
            }
            catch (OperationCanceledException)
            {
                ClearSubscriptionSlot(key);
                SetStatus(key, isSubscribed: false, error: "Canceled.");
            }
            catch (Exception ex)
            {
                ClearSubscriptionSlot(key);
                SetStatus(key, isSubscribed: false, error: ex.Message);
            }
        }

        public bool HasValue<T>(IPullSubTopic<T> topic)
        {
            if (!TryGetDataHandleInternal(topic, out var handle))
                return false;

            return handle.HasValue;
        }

        public bool TryGet<T>(IPullSubTopic<T> topic, out T value)
        {
            value = default;
            if (!TryGetDataHandleInternal(topic, out var handle) || !handle.HasValue)
            {
                return false;
            }

            value = handle.Value;
            return true;
        }

        public bool TryGetTimestampUtc<T>(IPullSubTopic<T> topic, out DateTime timestampUtc)
        {
            timestampUtc = default;
            if (!TryGetDataHandleInternal(topic, out var handle) || !handle.HasValue)
            {
                return false;
            }

            timestampUtc = handle.TimestampUtc;
            return true;
        }

        public PullSubDataHandle<T> GetDataHandle<T>(IPullSubTopic<T> topic)
        {
            if (topic == null)
                throw new ArgumentNullException(nameof(topic));

            if (_client == null || _client.Runtime == null)
                throw new InvalidOperationException("[PullSubDataCache] Runtime is not available.");

            try
            {
                return _client.Runtime.GetDataHandle<T>(topic.TopicName);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    "[PullSubDataCache] Data handle is not available yet. " +
                    "Ensure the topic is subscribed and startup has completed before calling GetDataHandle.",
                    ex);
            }
        }

        public UniTask UnsubscribeManagedTopicsAsync(CancellationToken cancellationToken = default)
        {
            return UnsubscribeAllAsync(cancellationToken);
        }

        private void OnDestroy()
        {
            _lifecycleCts?.Cancel();
            _lifecycleCts?.Dispose();
            _lifecycleCts = null;

            PullSubContext context;
            lock (_gate)
            {
                context = _context;
                _context = null;
                _subscriptions.Clear();

                foreach (var status in _statuses.Values)
                {
                    status.isSubscribed = false;
                }
            }

            context?.Dispose();
        }

        private async UniTask UnsubscribeAllAsync(CancellationToken cancellationToken)
        {
            KeyValuePair<PullSubTopicKey, IPullSubSubscriptionLease>[] subscriptions;
            PullSubContext context;
            lock (_gate)
            {
                subscriptions = _subscriptions
                    .Where(pair => pair.Value != null)
                    .ToArray();

                _subscriptions.Clear();
                context = _context;
                _context = null;
            }

            var tasks = new List<UniTask>(subscriptions.Length);
            foreach (var pair in subscriptions)
            {
                tasks.Add(UnsubscribeSingleAsync(pair.Key, pair.Value, cancellationToken));
            }

            await UniTask.WhenAll(tasks);

            if (context != null)
                await context.DisposeAsync();
        }

        private async UniTask UnsubscribeSingleAsync(
            PullSubTopicKey key,
            IPullSubSubscriptionLease subscription,
            CancellationToken cancellationToken)
        {
            try
            {
                var result = await subscription.UnsubscribeAsync(cancellationToken);
                if (result == PullSubUnsubscribeResult.Failed)
                {
                    SetStatus(key, isSubscribed: false, error: "Unsubscribe failed.");
                    return;
                }

                SetStatus(key, isSubscribed: false, error: null);
            }
            catch (OperationCanceledException)
            {
                SetStatus(key, isSubscribed: false, error: "Canceled.");
            }
            catch (Exception ex)
            {
                SetStatus(key, isSubscribed: false, error: ex.Message);
            }
        }

        private bool TryGetDataHandleInternal<T>(IPullSubTopic<T> topic, out PullSubDataHandle<T> handle)
        {
            if (topic == null)
                throw new ArgumentNullException(nameof(topic));

            handle = default;
            if (_client == null || _client.Runtime == null)
                return false;

            try
            {
                handle = _client.Runtime.GetDataHandle<T>(topic.TopicName);
                return true;
            }
            catch (InvalidOperationException)
            {
                // Topic is not subscribed yet (e.g., startup race). Treat as "no value yet".
                return false;
            }
        }

        private void SetStatus(PullSubTopicKey key, bool isSubscribed, string error)
        {
            lock (_gate)
            {
                if (!_statuses.TryGetValue(key, out var status))
                {
                    status = new PullSubDataCacheTopicStatus { key = key };
                    _statuses[key] = status;
                }

                status.isSubscribed = isSubscribed;
                status.error = error;
            }
        }

        private bool TryReserveSubscriptionSlot(PullSubTopicKey key)
        {
            lock (_gate)
            {
                if (_subscriptions.ContainsKey(key))
                    return false;

                _subscriptions[key] = null;
                return true;
            }
        }

        private void CommitSubscriptionSlot(PullSubTopicKey key, IPullSubSubscriptionLease subscription)
        {
            lock (_gate)
            {
                _subscriptions[key] = subscription;
            }
        }

        private void ClearSubscriptionSlot(PullSubTopicKey key)
        {
            lock (_gate)
            {
                _subscriptions.Remove(key);
            }
        }
    }
}
