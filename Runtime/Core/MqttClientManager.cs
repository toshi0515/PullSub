using MQTTnet;
using MQTTnet.Client.Connecting;
using MQTTnet.Client.Disconnecting;
using MQTTnet.Client.Options;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Protocol;
using Cysharp.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// MQTT クライアント統一管理。
/// public コンストラクタでインスタンスを生成し、StartAsync で接続開始する。
/// メッセージハンドラーは MQTTnet のバックグラウンドスレッドで実行される。
/// Unity オブジェクトへのアクセスが必要な場合は、呼び出し側で UniTask.Post() 等を用い
/// メインスレッドに切り替えてください。
/// </summary>
/// <remarks>
/// 購読状態の管理は ManagedMqttClient に委譲する。
/// SubscribeAsync は StartAsync 前でも呼び出せ、ManagedMqttClient の内部キューに積まれる。
/// PublishAsync は接続開始後にのみ許可し、Dispose/DisposeAsync は進行中の操作を即座にキャンセルする。
/// </remarks>
public sealed class MqttClientManager : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// StartAsync の多重実行を直列化するセマフォ。
    /// CoreDisposeAsync は _client を破棄する前にこのセマフォを取得し、
    /// StartAsync が _client を使用中でないことを保証する。
    /// </summary>
    private readonly SemaphoreSlim _startSemaphore = new SemaphoreSlim(1, 1);

    /// <summary>
    /// Dispose の多重実行を防ぐラッチ（0: 未実行, 1: 実行済み）。
    /// Interlocked.Exchange でアトミックに操作する。
    /// </summary>
    private int _disposeGuard = 0;

    /// <summary>
    /// Dispose 済みかどうかを lock なしで参照するためのフラグ。
    /// </summary>
    private volatile bool _disposed = false;

    // ========= プロパティ =========

    public string BrokerIp { get; private set; }
    public int BrokerPort { get; private set; }

    /// <summary>ブローカーへの物理的な TCP 接続状態。</summary>
    public bool IsConnected => !_disposed && _client.IsConnected;

    /// <summary>クライアントの起動要求が完了しているかどうか。</summary>
    public bool IsStarted => !_disposed && _client.IsStarted;

    /// <summary>Publish/Subscribe 拡張で利用するシリアライザ。</summary>
    public IMqttSerializer Serializer => _serializer;

    // ========= 内部フィールド =========

    private readonly IManagedMqttClient _client;
    private readonly IMqttSerializer _serializer;

    /// <summary>
    /// StartAsync で生成し Dispose でキャンセル・破棄する寿命トークン。
    /// PublishAsync 等の進行中処理を Dispose で即座に止めるために使う。
    /// </summary>
    private CancellationTokenSource _lifetimeCts;

    private Action<string> _log = _ => { };
    private Action<string> _logError = _ => { };
    private Action<Exception, string, bool> _logException = (ex, msg, st) => { };

    private readonly Dictionary<string, Action<string, byte[]>> _topicHandlers
        = new Dictionary<string, Action<string, byte[]>>();
    private readonly object _handlerLock = new object();
    private Action<string, byte[]> _defaultHandler;

    private readonly HashSet<string> _subscribedTopics = new HashSet<string>();
    private readonly object _topicLock = new object();

    // ========= コンストラクタ =========

    public MqttClientManager(
        string brokerIp,
        int brokerPort,
        Action<string> log = null,
        Action<string> logError = null,
        Action<Exception, string, bool> logException = null,
        IMqttSerializer serializer = null)
    {
        // IPAddress.TryParse はホスト名（"mqtt.factory.local" 等）を拒否するため、
        // 空でないかどうかだけをチェックする。
        BrokerIp = string.IsNullOrWhiteSpace(brokerIp) ? "127.0.0.1" : brokerIp;
        BrokerPort = brokerPort > 0 ? brokerPort : 1883;

        if (log != null) _log = log;
        if (logError != null) _logError = logError;
        if (logException != null) _logException = logException;

        _serializer = serializer ?? MqttSerializerDefaults.Default;

        var factory = new MqttFactory();
        _client = factory.CreateManagedMqttClient();

        _client.ConnectedHandler = new MqttClientConnectedHandlerDelegate(_ =>
            _log?.Invoke("[MQTT] Connected."));

        _client.DisconnectedHandler = new MqttClientDisconnectedHandlerDelegate(e =>
        {
            _log?.Invoke($"[MQTT] Disconnected: Reason={e.Reason}");
            if (e.Exception != null)
                _logException?.Invoke(e.Exception, "Disconnected", false);
        });

        _client.UseApplicationMessageReceivedHandler(HandleMessageReceived);
    }

    // ========= ログ API =========

    public void Log(string message) => _log?.Invoke(message);
    public void LogError(string message) => _logError?.Invoke(message);
    public void LogException(Exception ex, string message, bool includeStackTrace = true)
        => _logException?.Invoke(ex, message, includeStackTrace);

    // ========= 接続管理 =========

    /// <summary>
    /// MQTT クライアントを起動する。
    /// 既に起動済みの場合の再呼び出しは no-op。
    /// </summary>
    /// <exception cref="ObjectDisposedException">Disposed 状態で呼ばれた場合。</exception>
    public async UniTask StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _startSemaphore.WaitAsync(ct);
        CancellationTokenSource lifetimeCts = null;
        try
        {
            ThrowIfDisposed();

            if (_client.IsStarted)
            {
                _log?.Invoke("[MQTT] Already running.");
                return;
            }

            lifetimeCts = new CancellationTokenSource();
            var lifetimeToken = lifetimeCts.Token;
            var previousLifetimeCts = Interlocked.Exchange(ref _lifetimeCts, lifetimeCts);
            previousLifetimeCts?.Cancel();
            previousLifetimeCts?.Dispose();

            ThrowIfDisposed();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, ct);
            var startToken = linked.Token;

            try
            {
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

                await AwaitWithCancellation(_client.StartAsync(managedOptions).AsUniTask(), startToken);

                startToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();

                _log?.Invoke($"[MQTT] Client started {BrokerIp}:{BrokerPort}");
            }
            catch (OperationCanceledException)
            {
                await CleanupFailedStartAsync(lifetimeCts);
                throw;
            }
            catch (Exception ex)
            {
                await CleanupFailedStartAsync(lifetimeCts);
                _logException?.Invoke(ex, "StartAsync error", true);
                throw;
            }
        }
        finally
        {
            _startSemaphore.Release();
        }
    }

    /// <summary>
    /// メッセージ受信処理。MQTTnet のバックグラウンドスレッドから呼ばれる。
    /// スレッド切り替えは行わず、ハンドラー呼び出しのみを担う（上位レイヤーで対応）。
    /// </summary>
    private void HandleMessageReceived(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            if (_disposed) return;

            var topic = e.ApplicationMessage?.Topic ?? string.Empty;
            var bytes = e.ApplicationMessage?.Payload ?? Array.Empty<byte>();

            if (bytes.Length == 0)
            {
                _logError?.Invoke($"MQTT payload is empty. (topic={topic})");
                return;
            }

            Action<string, byte[]> handler;
            lock (_handlerLock)
            {
                if (!_topicHandlers.TryGetValue(topic, out handler))
                    handler = _defaultHandler;
            }

            // スレッドの切り替え（UniTask.Post 等）は呼び出し側の責務
            handler?.Invoke(topic, bytes);
        }
        catch (Exception ex)
        {
            _logException?.Invoke(ex, "HandleMessageReceived error", true);
        }
    }

    /// <summary>
    /// 指定トピックにメッセージを送信する。
    /// StartAsync 完了後にのみ動作する。ネットワーク切断中は ManagedMqttClient が内部キューに積み、
    /// 再接続後に自動送信する（ManagedMqttClient の責務）。
    /// </summary>
    /// <exception cref="InvalidOperationException">StartAsync 前に呼ばれた場合。</exception>
    /// <exception cref="ObjectDisposedException">Disposed 状態で呼ばれた場合。</exception>
    public async UniTask PublishAsync(
        string topic,
        byte[] payload,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce,
        bool retain = false,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (!_client.IsStarted)
            throw new InvalidOperationException("Cannot publish: client is not started. Call StartAsync first.");

        var lifetimeCts = _lifetimeCts;
        if (lifetimeCts == null)
            throw new InvalidOperationException("Cannot publish: client is not started. Call StartAsync first.");

        CancellationToken lifetimeToken;
        try
        {
            lifetimeToken = lifetimeCts.Token;
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            throw new ObjectDisposedException(nameof(MqttClientManager));
        }
        catch (ObjectDisposedException)
        {
            // Dispose/cleanup 競合で寿命 CTS が破棄済みになったケースは
            // 想定内のライフサイクル遷移として InvalidOperation で返す。
            throw new InvalidOperationException("Cannot publish: client is not started. Call StartAsync first.");
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(qos)
            .WithRetainFlag(retain)
            .Build();

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, ct);
            await AwaitWithCancellation(_client.PublishAsync(message).AsUniTask(), linked.Token);
            _log?.Invoke($"[PUB] topic={topic}, size={payload?.Length ?? 0}");
        }
        catch (OperationCanceledException) { throw; }
        catch (ObjectDisposedException) when (_disposed)
        {
            throw new ObjectDisposedException(nameof(MqttClientManager));
        }
        catch (Exception ex)
        {
            _logException?.Invoke(ex, "PublishAsync failed", true);
            throw;
        }
    }

    /// <summary>
    /// 指定トピックを購読する。
    /// ハンドラー登録を同期的に完了させた後、購読要求を ManagedMqttClient に委譲する。
    /// StartAsync 前でも呼び出し可能で、未接続時は ManagedMqttClient の内部キューに積まれる。
    /// </summary>
    /// <exception cref="ObjectDisposedException">Disposed 状態で呼ばれた場合。</exception>
    public async UniTask SubscribeAsync(
        string topic,
        Action<string, byte[]> handler = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(topic)) return;
        ThrowIfDisposed();

        // ① ハンドラー登録（ct より先に完了）
        if (handler != null)
            SetMessageHandler(topic, handler);

        lock (_topicLock) { _subscribedTopics.Add(topic); }

        CancellationTokenSource linked = null;
        try
        {
            var operationToken = CreateOperationToken(ct, out linked);

            var topicFilter = new MqttTopicFilterBuilder()
                .WithTopic(topic)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                .Build();

            await AwaitWithCancellation(_client.SubscribeAsync(topicFilter).AsUniTask(), operationToken);
            _log?.Invoke($"Subscribed to topic: {topic}");
        }
        catch (OperationCanceledException) { throw; }
        catch (ObjectDisposedException) when (_disposed)
        {
            throw new ObjectDisposedException(nameof(MqttClientManager));
        }
        catch (Exception ex)
        {
            _logException?.Invoke(ex, $"Failed to subscribe to {topic}", true);
            throw;
        }
        finally
        {
            linked?.Dispose();
        }
    }

    /// <summary>
    /// 指定トピックの購読を解除する。
    /// </summary>
    /// <exception cref="ObjectDisposedException">Disposed 状態で呼ばれた場合。</exception>
    public async UniTask UnsubscribeAsync(string topic, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(topic)) return;
        ThrowIfDisposed();

        RemoveMessageHandler(topic);
        lock (_topicLock) { _subscribedTopics.Remove(topic); }

        CancellationTokenSource linked = null;
        try
        {
            var operationToken = CreateOperationToken(ct, out linked);
            await AwaitWithCancellation(_client.UnsubscribeAsync(topic).AsUniTask(), operationToken);
            _log?.Invoke($"Unsubscribed from topic: {topic}");
        }
        catch (OperationCanceledException) { throw; }
        catch (ObjectDisposedException) when (_disposed)
        {
            throw new ObjectDisposedException(nameof(MqttClientManager));
        }
        catch (Exception ex)
        {
            _logException?.Invoke(ex, $"Failed to unsubscribe from {topic}", true);
            throw;
        }
        finally
        {
            linked?.Dispose();
        }
    }

    public void SetDefaultMessageHandler(Action<string, byte[]> handler)
    {
        lock (_handlerLock) { _defaultHandler = handler; }
    }

    public void SetMessageHandler(string topic, Action<string, byte[]> handler)
    {
        if (string.IsNullOrWhiteSpace(topic)) return;
        lock (_handlerLock)
        {
            if (handler == null)
            {
                _topicHandlers.Remove(topic);
                _log?.Invoke($"Message handler removed: {topic}");
            }
            else
            {
                _topicHandlers[topic] = handler;
                _log?.Invoke($"Message handler set: {topic}");
            }
        }
    }

    public void RemoveMessageHandler(string topic) => SetMessageHandler(topic, null);

    public IReadOnlyCollection<string> GetSubscribedTopics()
    {
        lock (_topicLock) { return new List<string>(_subscribedTopics).AsReadOnly(); }
    }

    // ========= Dispose =========

    /// <summary>
    /// 非同期で完全停止・リソース解放する。
    /// ブローカーへの DISCONNECT パケット送信を保証するため、
    /// MqttBridge.OnApplicationQuit 等の await できる文脈での使用を推奨。
    /// </summary>
    public async UniTask DisposeAsync()
    {
        if (!TryBeginDispose()) return;
        await CoreDisposeAsync();
    }

    // IAsyncDisposable の明示的実装（await using に対応）
    async ValueTask IAsyncDisposable.DisposeAsync() => await DisposeAsync();

    /// <summary>
    /// 同期 Dispose。停止処理は fire-and-forget で実行される。
    /// DISCONNECT パケットの送信は保証されない。
    /// await できる文脈では DisposeAsync の使用を推奨。
    /// </summary>
    public void Dispose()
    {
        if (!TryBeginDispose()) return;
        FireAndForgetDispose().Forget();
    }

    /// <summary>
    /// Disposed への遷移を試みる。既に遷移済みなら false を返す（冪等）。
    /// </summary>
    private bool TryBeginDispose()
    {
        // Interlocked.Exchange でアトミックに多重実行を防ぐ
        if (Interlocked.Exchange(ref _disposeGuard, 1) == 1) return false;
        _disposed = true;
        return true;
    }

    private async UniTask CoreDisposeAsync()
    {
        // ① 進行中の操作を即座にキャンセル
        var lifetimeCts = Interlocked.Exchange(ref _lifetimeCts, null);
        lifetimeCts?.Cancel();
        lifetimeCts?.Dispose();

        // ② StartAsync が _client を使用中でないことを保証してから破棄する。
        // ①で CTS をキャンセル済みのため、StartAsync は速やかにセマフォを解放する。
        try
        {
            if (await _startSemaphore.WaitAsync(5000))
                _startSemaphore.Release();
        }
        catch { /* セマフォが既に破棄済み等 — 続行 */ }

        try
        {
            await StopClientWithTimeoutAsync(_client, "[WARN] DisposeAsync: StopAsync timeout.");
        }
        catch (Exception ex)
        {
            _logException?.Invoke(ex, "Dispose StopAsync error", false);
        }
        finally
        {
            _client.Dispose();
            _log?.Invoke("[MQTT] Client disposed.");
        }
    }

    private async UniTaskVoid FireAndForgetDispose() => await CoreDisposeAsync();

    // ========= Private Helpers =========

    /// <summary>Disposed 状態であれば ObjectDisposedException をスローする。</summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MqttClientManager));
    }

    private CancellationToken CreateOperationToken(CancellationToken ct, out CancellationTokenSource linkedCts)
    {
        var lifetimeCts = _lifetimeCts;
        if (lifetimeCts == null)
        {
            linkedCts = null;
            return ct;
        }

        try
        {
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCts.Token, ct);
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            throw new ObjectDisposedException(nameof(MqttClientManager));
        }

        return linkedCts.Token;
    }

    private async UniTask CleanupFailedStartAsync(CancellationTokenSource failedLifetimeCts)
    {
        if (failedLifetimeCts == null) return;

        var ownsLifetime = Interlocked.CompareExchange(ref _lifetimeCts, null, failedLifetimeCts) == failedLifetimeCts;
        if (ownsLifetime)
            failedLifetimeCts.Cancel();

        try
        {
            await StopClientWithTimeoutAsync(_client, "[WARN] StartAsync cleanup: ManagedMqttClient.StopAsync timeout.");
        }
        catch (Exception ex)
        {
            // Dispose との競合で _client が停止/破棄済みの可能性がある。
            // CoreDisposeAsync がクリーンアップを引き継ぐため、ここでは吸収する。
            _logException?.Invoke(ex, "StartAsync cleanup error", false);
        }
        finally
        {
            if (ownsLifetime)
                failedLifetimeCts.Dispose();
        }
    }

    private async UniTask StopClientWithTimeoutAsync(IManagedMqttClient client, string timeoutMessage)
    {
        if (client == null || !client.IsStarted) return;

        var index = await UniTask.WhenAny(
            client.StopAsync().AsUniTask(),
            UniTask.Delay(1500));
        if (index == 1)
            _log?.Invoke(timeoutMessage);
    }

    // タスクの完了またはキャンセルを待機し、キャンセル時は OperationCanceledException をスローする
    private static async UniTask AwaitWithCancellation(UniTask task, CancellationToken ct)
    {
        if (!ct.CanBeCanceled) { await task; return; }
        var index = await UniTask.WhenAny(task, UniTask.Never(ct));
        if (index == 1) throw new OperationCanceledException(ct);
        await task; // 例外伝播のために再 await
    }

    private static async UniTask<T> AwaitWithCancellation<T>(UniTask<T> task, CancellationToken ct)
    {
        if (!ct.CanBeCanceled) return await task;
        // UniTask<T> を UniTask にラップして WhenAny の int オーバーロードを使う
        async UniTask WrapAsUniTask() { await task; }
        var index = await UniTask.WhenAny(WrapAsUniTask(), UniTask.Never(ct));
        if (index == 1) throw new OperationCanceledException(ct);
        return await task; // 例外伝播のために再 await
    }
}