using MQTTnet;
using MQTTnet.Client.Connecting;
using MQTTnet.Client.Disconnecting;
using MQTTnet.Client.Options;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Protocol;
using Cysharp.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// MQTT クライアント統一管理（シングルトン）
/// メッセージハンドラーは自動的にUnityメインスレッドで実行される
/// </summary>
public sealed class MqttClientManager : IDisposable
{
    private static readonly Lazy<MqttClientManager> _instance = new Lazy<MqttClientManager>(() => new MqttClientManager());
    public static MqttClientManager Instance => _instance.Value;

    private MqttClientManager() { }

    public string BrokerIp { get; private set; } = "192.168.11.159";
    public int BrokerPort { get; private set; } = 1883;
    public bool IsConnected => _client != null && _client.IsStarted;

    private IManagedMqttClient _client;
    private bool _configured;
    private readonly object _lock = new object();
    private bool _stopping;

    private Action<string> _log = _ => { };
    private Action<string> _logError = _ => { };
    private Action<Exception, string, bool> _logException = (ex, msg, st) => { };

    private readonly Dictionary<string, Action<string, byte[]>> _topicHandlers = new Dictionary<string, Action<string, byte[]>>();
    private readonly object _handlerLock = new object();
    private Action<string, byte[]> _defaultHandler;

    private readonly HashSet<string> _subscribedTopics = new HashSet<string>();
    private readonly object _topicLock = new object();

    public void Configure(
        string brokerIp = null,
        int? brokerPort = null,
        Action<string> log = null,
        Action<string> logError = null,
        Action<Exception, string, bool> logException = null)
    {
        var hasValidIp = !string.IsNullOrWhiteSpace(brokerIp) && IPAddress.TryParse(brokerIp, out _);
        var hasValidPort = brokerPort.HasValue && brokerPort.Value > 0;

        lock (_lock)
        {
            if (_configured)
            {
                if (hasValidIp) BrokerIp = brokerIp;
                if (hasValidPort) BrokerPort = brokerPort.Value;
                return;
            }

            if (hasValidIp) BrokerIp = brokerIp;
            if (hasValidPort) BrokerPort = brokerPort.Value;

            if (log != null) _log = log;
            if (logError != null) _logError = logError;
            if (logException != null) _logException = logException;

            _configured = true;
        }
    }

    // ========= ログAPI =========
    public void Log(string message) => _log?.Invoke(message);
    public void LogError(string message) => _logError?.Invoke(message);
    public void LogException(Exception ex, string message, bool includeStackTrace = true)
        => _logException?.Invoke(ex, message, includeStackTrace);

    // ========= 接続管理 =========

    public async UniTask StartAsync()
    {
        if (_client != null && _client.IsStarted) return;

        try
        {
            var factory = new MqttFactory();
            var client = factory.CreateManagedMqttClient();

            var clientId = $"UnityMqttClient_{Guid.NewGuid():N}";
            var options = new MqttClientOptionsBuilder()
                .WithClientId(clientId)
                .WithTcpServer(BrokerIp, BrokerPort)
                .WithCleanSession()
                .Build();

            var managedOptions = new ManagedMqttClientOptionsBuilder()
                .WithClientOptions(options)
                .WithAutoReconnectDelay(TimeSpan.FromSeconds(5))
                .Build();

            client.ConnectedHandler = new MqttClientConnectedHandlerDelegate(_ =>
            {
                _log?.Invoke($"[MQTT] Connected (clientId={clientId})");
            });
            client.DisconnectedHandler = new MqttClientDisconnectedHandlerDelegate(e =>
            {
                _log?.Invoke($"[MQTT] Disconnected: Reason={e.Reason}");
                if (e.Exception != null) _logException?.Invoke(e.Exception, "Disconnected", false);
            });

            client.UseApplicationMessageReceivedHandler(e =>
            {
                // MQTTnetの別スレッドから呼ばれるため、Unityメインスレッドに切り替えて処理
                HandleMessageReceivedAsync(e).Forget();
            });

            await client.StartAsync(managedOptions).AsUniTask();
            _client = client;

            await SubscribeAllRegisteredTopicsAsync();

            _log?.Invoke($"[MQTT] Client started {BrokerIp}:{BrokerPort}");
        }
        catch (Exception ex)
        {
            _logException?.Invoke(ex, "StartAsync error", true);
            throw;
        }
    }

    /// <summary>
    /// メッセージ受信処理（自動的にメインスレッドに切り替え）
    /// </summary>
    private async UniTaskVoid HandleMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            // メインスレッドに切り替え
            await UniTask.SwitchToMainThread();

            var topic = e.ApplicationMessage?.Topic ?? string.Empty;
            var bytes = e.ApplicationMessage?.Payload ?? Array.Empty<byte>();

            if (bytes.Length == 0)
            {
                _logError?.Invoke($"MQTT payload is empty. (topic={topic})");
                return;
            }

            Action<string, byte[]> handler = null;
            lock (_handlerLock)
            {
                if (!_topicHandlers.TryGetValue(topic, out handler))
                {
                    handler = _defaultHandler;
                }
            }

            // メインスレッドで実行されるため、ハンドラー内でスレッドセーフを気にする必要なし
            handler?.Invoke(topic, bytes);
        }
        catch (Exception ex)
        {
            _logException?.Invoke(ex, "HandleMessageReceivedAsync error", true);
        }
    }

    public async UniTask StopAsync()
    {
        if (_stopping) return;
        _stopping = true;

        try
        {
            var c = _client;
            _client = null;

            if (c != null)
            {
                if (c.IsStarted)
                {
                    var task = c.StopAsync();
                    var completedIndex = await UniTask.WhenAny(task.AsUniTask(), UniTask.Delay(1500));

                    if (completedIndex == 1)
                    {
                        _log?.Invoke("[WARN] ManagedMqttClient.StopAsync timeout. Disposing client.");
                    }
                }
                c.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logException?.Invoke(ex, "StopAsync error", true);
        }
        finally
        {
            _stopping = false;
        }
    }

    public async UniTask PublishAsync(string topic, byte[] payload, MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce, bool retain = false)
    {
        await EnsureConnectedAsync();

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(qos)
            .WithRetainFlag(retain)
            .Build();

        try
        {
            await _client.PublishAsync(message).AsUniTask();
            _log?.Invoke($"[PUB] topic={topic}, size={payload.Length}");
        }
        catch (Exception ex)
        {
            _logException?.Invoke(ex, "PublishAsync failed", true);
        }
    }

    public async UniTask SubscribeAsync(string topic, Action<string, byte[]> handler = null)
    {
        if (string.IsNullOrWhiteSpace(topic)) return;

        if (handler != null)
        {
            SetMessageHandler(topic, handler);
        }

        lock (_topicLock)
        {
            if (_subscribedTopics.Contains(topic))
            {
                _log?.Invoke($"Topic already subscribed: {topic}");
                return;
            }
            _subscribedTopics.Add(topic);
        }

        if (_client == null || !_client.IsStarted)
        {
            _log?.Invoke($"Topic registered (will subscribe on connect): {topic}");
            return;
        }

        await SubscribeToTopicAsync(topic);
    }

    public async UniTask UnsubscribeAsync(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return;

        lock (_topicLock)
        {
            if (!_subscribedTopics.Remove(topic)) return;
        }

        RemoveMessageHandler(topic);

        if (_client != null && _client.IsStarted)
        {
            try
            {
                await _client.UnsubscribeAsync(topic).AsUniTask();
                _log?.Invoke($"Unsubscribed from topic: {topic}");
            }
            catch (Exception ex)
            {
                _logException?.Invoke(ex, $"Failed to unsubscribe from {topic}", true);
            }
        }
    }

    public void SetDefaultMessageHandler(Action<string, byte[]> handler)
    {
        lock (_handlerLock)
        {
            _defaultHandler = handler;
        }
    }

    public void SetMessageHandler(string topic, Action<string, byte[]> handler)
    {
        if (string.IsNullOrWhiteSpace(topic)) return;

        lock (_handlerLock)
        {
            if (handler == null)
            {
                _topicHandlers.Remove(topic);
                _log?.Invoke($"Message handler removed for topic: {topic}");
            }
            else
            {
                _topicHandlers[topic] = handler;
                _log?.Invoke($"Message handler set for topic: {topic}");
            }
        }
    }

    public void RemoveMessageHandler(string topic)
    {
        SetMessageHandler(topic, null);
    }

    public IReadOnlyCollection<string> GetSubscribedTopics()
    {
        lock (_topicLock)
        {
            return new List<string>(_subscribedTopics).AsReadOnly();
        }
    }

    public void Dispose()
    {
        StopAsync().Forget();
    }

    private async UniTask EnsureConnectedAsync()
    {
        if (_client == null || !_client.IsStarted)
        {
            await StartAsync();
        }
    }

    private async UniTask SubscribeAllRegisteredTopicsAsync()
    {
        string[] topics;
        lock (_topicLock)
        {
            topics = new string[_subscribedTopics.Count];
            _subscribedTopics.CopyTo(topics);
        }

        foreach (var topic in topics)
        {
            await SubscribeToTopicAsync(topic);
        }
    }

    private async UniTask SubscribeToTopicAsync(string topic)
    {
        try
        {
            var topicFilter = new MqttTopicFilterBuilder()
                .WithTopic(topic)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                .Build();

            await _client.SubscribeAsync(topicFilter).AsUniTask();
            _log?.Invoke($"Subscribed to topic: {topic}");
        }
        catch (Exception ex)
        {
            _logException?.Invoke(ex, $"Failed to subscribe to {topic}", true);
        }
    }
}