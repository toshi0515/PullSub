using System;
using System.Collections.Generic;
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

        private readonly Dictionary<PullSubTopicKey, IPullSubTopicRegistration> _registrations =
            new Dictionary<PullSubTopicKey, IPullSubTopicRegistration>();

        private readonly Dictionary<PullSubTopicKey, PullSubDataCacheTopicStatus> _statuses =
            new Dictionary<PullSubTopicKey, PullSubDataCacheTopicStatus>();

        private CancellationTokenSource _lifecycleCts;

        public IReadOnlyCollection<PullSubDataCacheTopicStatus> TopicStatuses => _statuses.Values;

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

            if (_registrations.ContainsKey(key))
            {
                SetStatus(key, isSubscribed: false, error: "Duplicate topic key in PullSubDataCache settings.");
                return;
            }

            if (!PullSubTopicCatalog.TryGet(key, out var registration))
            {
                SetStatus(key, isSubscribed: false, error: $"Topic key '{key}' is not registered in PullSubTopicCatalog.");
                return;
            }

            try
            {
                await registration.SubscribeAsync(_client.Runtime, binding.subscribeQos, cancellationToken);
                _registrations[key] = registration;
                SetStatus(key, isSubscribed: true, error: null);
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

        public bool HasValue<T>(IPullSubTopic<T> topic)
        {
            return GetDataHandle(topic).HasValue;
        }

        public bool TryGet<T>(IPullSubTopic<T> topic, out T value)
        {
            if (topic == null)
                throw new ArgumentNullException(nameof(topic));

            var handle = GetDataHandle(topic);
            if (!handle.HasValue)
            {
                value = default;
                return false;
            }

            value = handle.Value;
            return true;
        }

        public bool TryGetTimestampUtc<T>(IPullSubTopic<T> topic, out DateTime timestampUtc)
        {
            if (topic == null)
                throw new ArgumentNullException(nameof(topic));

            var handle = GetDataHandle(topic);
            if (!handle.HasValue)
            {
                timestampUtc = default;
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

            return _client.Runtime.GetDataHandle<T>(topic.TopicName);
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

            UnsubscribeAllAsync(CancellationToken.None).Forget();
        }

        private async UniTask UnsubscribeAllAsync(CancellationToken cancellationToken)
        {
            if (_client == null || _client.Runtime == null)
                return;

            var tasks = new List<UniTask>(_registrations.Count);
            foreach (var pair in _registrations)
            {
                tasks.Add(UnsubscribeSingleAsync(pair.Key, pair.Value, cancellationToken));
            }

            await UniTask.WhenAll(tasks);
            _registrations.Clear();
        }

        private async UniTask UnsubscribeSingleAsync(
            PullSubTopicKey key,
            IPullSubTopicRegistration registration,
            CancellationToken cancellationToken)
        {
            try
            {
                await registration.UnsubscribeAsync(_client.Runtime, cancellationToken);
                SetStatus(key, isSubscribed: false, error: null);
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        private void SetStatus(PullSubTopicKey key, bool isSubscribed, string error)
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
}
