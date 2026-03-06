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
using System.Threading;
using UnityEngine;

/// <summary>
/// MQTT クライアント統一管理
/// public コンストラクタでインスタンスを生成し、StartAsync で接続する。
/// メッセージハンドラーは自動的に Unity メインスレッドで実行される。
/// </summary>
public sealed class MqttClientManager : IDisposable
{
    public string BrokerIp { get; private set; } = "127.0.0.1";
    public int BrokerPort { get; private set; } = 1883;

    public MqttClientManager(
        string brokerIp,
        int brokerPort,
        Action<string> log = null,
        Action<string> logError = null,
        Action<Exception, string, bool> logException = null)
    {
        var hasValidIp = !string.IsNullOrWhiteSpace(brokerIp) && IPAddress.TryParse(brokerIp, out _);
        var hasValidPort = brokerPort > 0;

        if (hasValidIp) BrokerIp = brokerIp;
        else BrokerIp = "127.0.0.1";

        if (hasValidPort) BrokerPort = brokerPort;
        else BrokerPort = 1883;

        if (log != null) _log = log;
        if (logError != null) _logError = logError;
        if (logException != null) _logException = logException;
    }

    public bool IsConnected => _client != null && _client.IsStarted;

    private IManagedMqttClient _client;
    private bool _stopping;
    private readonly SemaphoreSlim _stopSemaphore = new SemaphoreSlim(1, 1);

    // lifetime cancellation: StartAsync で生成し StopAsync/Dispose でキャンセル＆破棄
    private CancellationTokenSource _lifetimeCts;

    private Action<string> _log = _ => { };
    private Action<string> _logError = _ => { };
    private Action<Exception, string, bool> _logException = (ex, msg, st) => { };

    private readonly Dictionary<string, Action<string, byte[]>> _topicHandlers = new Dictionary<string, Action<string, byte[]>>();
    private readonly object _handlerLock = new object();
    private Action<string, byte[]> _defaultHandler;

    private readonly HashSet<string> _subscribedTopics = new HashSet<string>();
    private readonly object _topicLock = new object();


    // ========= ログAPI =========
    public void Log(string message) => _log?.Invoke(message);
    public void LogError(string message) => _logError?.Invoke(message);
    public void LogException(Exception ex, string message, bool includeStackTrace = true)
        => _logException?.Invoke(ex, message, includeStackTrace);

    // ========= 接続管理 =========

    public async UniTask StartAsync(CancellationToken ct = default)
    {
        if (_client != null && _client.IsStarted) return;

        // prepare new lifetime token; existing one (if any) should be cancelled
        _lifetimeCts?.Cancel();
        _lifetimeCts?.Dispose();
        _lifetimeCts = new CancellationTokenSource();

        // link the caller token so that the start operation itself can be aborted
        using var startLinked = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token, ct);
        var startToken = startLinked.Token;

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

            await AwaitWithCancellation(client.StartAsync(managedOptions).AsUniTask(), startToken);
            _client = client;

            await SubscribeAllRegisteredTopicsAsync(startToken);

            _log?.Invoke($"[MQTT] Client started {BrokerIp}:{BrokerPort}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // caller requested cancellation; propagate without logging
            // lifetime token is no longer needed since start was aborted.
            _lifetimeCts?.Dispose();
            _lifetimeCts = null;
            throw;
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

            // ライフタイムキャンセルが来ていたら即終了
            if (_lifetimeCts?.IsCancellationRequested == true)
            {
                return;
            }

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
        await _stopSemaphore.WaitAsync();
        try
        {
            if (_stopping) return;
            _stopping = true;

            // cancel lifetime token immediately so that any linked operations terminate
            _lifetimeCts?.Cancel();

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
                _lifetimeCts?.Dispose();
                _lifetimeCts = null;
            }
        }
        finally
        {
            _stopSemaphore.Release();
        }
    }

    public async UniTask PublishAsync(string topic, byte[] payload, MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce, bool retain = false, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);

        // EnsureConnectedAsync attempts to start the client, but in rare
        // race conditions _client may still be null/not started.  Guard
        // against misuse by throwing an explicit exception rather than
        // silently proceeding with a null reference or wrong token.
        if (_client == null || !_client.IsStarted)
        {
            throw new InvalidOperationException("MQTT client is not connected");
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(qos)
            .WithRetainFlag(retain)
            .Build();

        try
        {
            // _lifetimeCts may be null if StopAsync raced with EnsureConnectedAsync; using CancellationToken.None
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts?.Token ?? CancellationToken.None, ct);
            await AwaitWithCancellation(_client.PublishAsync(message).AsUniTask(), linked.Token);
            _log?.Invoke($"[PUB] topic={topic}, size={payload.Length}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logException?.Invoke(ex, "PublishAsync failed", true);
            throw;
        }
    }

    public async UniTask SubscribeAsync(string topic, Action<string, byte[]> handler = null, CancellationToken ct = default)
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

        await SubscribeToTopicAsync(topic, ct);
    }

    public async UniTask UnsubscribeAsync(string topic, CancellationToken ct = default)
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
                // same race note as PublishAsync
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts?.Token ?? CancellationToken.None, ct);
                await AwaitWithCancellation(_client.UnsubscribeAsync(topic).AsUniTask(), linked.Token);
                _log?.Invoke($"Unsubscribed from topic: {topic}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logException?.Invoke(ex, $"Failed to unsubscribe from {topic}", true);
                throw;
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
        // make sure shutdown completes before Dispose returns to avoid
        // orphaned background operations or unfreed resources.
        StopAsync().GetAwaiter().GetResult();
    }

    private async UniTask EnsureConnectedAsync(CancellationToken ct = default)
    {
        if (_client == null || !_client.IsStarted)
        {
            await StartAsync(ct);
        }
    }

    private async UniTask SubscribeAllRegisteredTopicsAsync(CancellationToken ct)
    {
        string[] topics;
        lock (_topicLock)
        {
            topics = new string[_subscribedTopics.Count];
            _subscribedTopics.CopyTo(topics);
        }

        foreach (var topic in topics)
        {
            await SubscribeToTopicAsync(topic, ct);
        }
    }

    private async UniTask SubscribeToTopicAsync(string topic, CancellationToken ct)
    {
        try
        {
            var topicFilter = new MqttTopicFilterBuilder()
                .WithTopic(topic)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                .Build();

            // race with StopAsync; fall back to CancellationToken.None when lifetime is cleared
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts?.Token ?? CancellationToken.None, ct);
            await AwaitWithCancellation(_client.SubscribeAsync(topicFilter).AsUniTask(), linked.Token);
            _log?.Invoke($"Subscribed to topic: {topic}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logException?.Invoke(ex, $"Failed to subscribe to {topic}", true);
            throw;
        }
    }

    // ======= internal helpers =======

    // Waits for task to finish or cancellation to trigger, raising OperationCanceledException if cancelled.
    private static async UniTask AwaitWithCancellation(UniTask task, CancellationToken ct)
    {
        // if token cannot be canceled, just await directly
        if (!ct.CanBeCanceled)
        {
            await task;
            return;
        }

        // use Never(ct) instead of Delay(-1) to avoid undefined behavior
        var index = await UniTask.WhenAny(task, UniTask.Never(ct));
        if (index == 1) throw new OperationCanceledException(ct);

        // task is already complete but we still await it to propagate any exception –
        // the second await is effectively no‑op in the success path.
        await task;
    }

    private static async UniTask<T> AwaitWithCancellation<T>(UniTask<T> task, CancellationToken ct)
    {
        if (!ct.CanBeCanceled)
        {
            return await task;
        }

        // Wrap UniTask<T> as a plain UniTask so WhenAny picks the (UniTask, UniTask) -> int overload,
        // avoiding the (UniTask<T>, UniTask<T>) -> (bool, T) tuple-returning overload.
        async UniTask WrapAsUniTask() { await task; }
        var index = await UniTask.WhenAny(WrapAsUniTask(), UniTask.Never(ct));
        if (index == 1) throw new OperationCanceledException(ct);
        // the task may have completed with an exception; await it again to
        // propagate that exception rather than returning a default value.
        return await task;
    }
}