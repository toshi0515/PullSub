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
/// public コンストラクタでインスタンスを生成し、StartAsync で接続する。
/// メッセージハンドラーは MQTTnet のバックグラウンドスレッドで実行される。
/// Unity オブジェクトへのアクセスが必要な場合は、呼び出し側で UniTask.Post() 等を用い
/// メインスレッドに切り替えてください。
/// </summary>
/// <remarks>
/// ライフサイクルは以下の4状態で管理される:
/// <code>
/// NotStarted ──StartAsync──▶ Running ──StopAsync──▶ Stopped
///                               ▲                       │
///                               └──────StartAsync───────┘
///                                    （再起動可能）
///
/// いずれの状態からも Dispose/DisposeAsync → Disposed（終端・復帰不可）
/// </code>
/// Running 状態でのネットワーク切断は ManagedMqttClient が自動再接続する。
/// Stopped / Disposed 状態での Publish/Subscribe は例外をスローする。
/// </remarks>
public sealed class MqttClientManager : IDisposable, IAsyncDisposable
{
    // ========= ライフサイクル状態 =========

    private enum ClientState { NotStarted, Running, Stopped, Disposed }

    private readonly struct RunningOperationContext
    {
        public RunningOperationContext(IManagedMqttClient client, CancellationToken lifetimeToken)
        {
            Client = client;
            LifetimeToken = lifetimeToken;
        }

        public IManagedMqttClient Client { get; }
        public CancellationToken LifetimeToken { get; }
    }

    private ClientState _state = ClientState.NotStarted;
    private readonly object _stateLock = new object();

    /// <summary>
    /// HandleMessageReceived からスレッドセーフに参照するための volatile フラグ。
    /// lock なしで安全に読み取れるよう、_state の変更と同時に更新する。
    /// </summary>
    private volatile bool _isRunning = false;

    /// <summary>
    /// StartAsync / StopAsync の多重実行を防ぐセマフォ。
    /// </summary>
    private readonly SemaphoreSlim _stateSemaphore = new SemaphoreSlim(1, 1);

    /// <summary>
    /// Dispose の多重実行を防ぐラッチ（0: 未実行, 1: 実行済み）。
    /// Interlocked.Exchange でアトミックに操作する。
    /// </summary>
    private int _disposeGuard = 0;

    // ========= プロパティ =========

    public string BrokerIp { get; private set; }
    public int BrokerPort { get; private set; }

    /// <summary>ブローカーへの物理的な TCP 接続状態。</summary>
    public bool IsConnected => _client?.IsConnected == true;

    /// <summary>クライアントが Running 状態かどうか。</summary>
    public bool IsStarted { get { lock (_stateLock) return _state == ClientState.Running; } }

    // ========= 内部フィールド =========

    private IManagedMqttClient _client;

    /// <summary>
    /// StartAsync で生成し StopAsync/Dispose でキャンセル・破棄する寿命トークン。
    /// Running 状態の間のみ有効。
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
        Action<Exception, string, bool> logException = null)
    {
        // IPAddress.TryParse はホスト名（"mqtt.factory.local" 等）を拒否するため、
        // 空でないかどうかだけをチェックする。
        BrokerIp = string.IsNullOrWhiteSpace(brokerIp) ? "127.0.0.1" : brokerIp;
        BrokerPort = brokerPort > 0 ? brokerPort : 1883;

        if (log != null) _log = log;
        if (logError != null) _logError = logError;
        if (logException != null) _logException = logException;
    }

    // ========= ログ API =========

    public void Log(string message) => _log?.Invoke(message);
    public void LogError(string message) => _logError?.Invoke(message);
    public void LogException(Exception ex, string message, bool includeStackTrace = true)
        => _logException?.Invoke(ex, message, includeStackTrace);

    // ========= 接続管理 =========

    /// <summary>
    /// MQTT クライアントを起動する。
    /// NotStarted および Stopped 状態から呼び出し可能。
    /// Stopped からの再起動時は既存の ManagedMqttClient インスタンスを再利用する。
    /// </summary>
    /// <exception cref="ObjectDisposedException">Disposed 状態で呼ばれた場合。</exception>
    public async UniTask StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _stateSemaphore.WaitAsync(ct);
        try
        {
            ClientState previousState;

            // セマフォ取得後に再チェック（Dispose との競合を考慮）
            lock (_stateLock)
            {
                if (_state == ClientState.Disposed)
                    throw new ObjectDisposedException(nameof(MqttClientManager));
                if (_state == ClientState.Running)
                {
                    _log?.Invoke("[MQTT] Already running.");
                    return;
                }
                // NotStarted または Stopped から進む

                previousState = _state;
            }

            _lifetimeCts?.Cancel();
            _lifetimeCts?.Dispose();
            _lifetimeCts = new CancellationTokenSource();
            var lifetimeToken = _lifetimeCts.Token;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, ct);
            var startToken = linked.Token;

            try
            {
                // 初回のみクライアントを生成。Stop → Start の再起動時は再利用する。
                // ハンドラーはクライアントインスタンスに紐づくため再登録不要。
                if (_client == null)
                {
                    var factory = new MqttFactory();
                    _client = factory.CreateManagedMqttClient();

                    _client.ConnectedHandler = new MqttClientConnectedHandlerDelegate(_ =>
                        _log?.Invoke("[MQTT] Connected."));

                    _client.DisconnectedHandler = new MqttClientDisconnectedHandlerDelegate(e =>
                    {
                        // Running 中のネットワーク切断は ManagedMqttClient が自動再接続するため、
                        // ここでは状態を変更しない（_isRunning は true のまま）。
                        _log?.Invoke($"[MQTT] Disconnected: Reason={e.Reason}");
                        if (e.Exception != null)
                            _logException?.Invoke(e.Exception, "Disconnected", false);
                    });

                    _client.UseApplicationMessageReceivedHandler(e => HandleMessageReceived(e));
                }

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

                var client = _client;
                await AwaitWithCancellation(client.StartAsync(managedOptions).AsUniTask(), startToken);
                await SubscribeAllRegisteredTopicsAsync(new RunningOperationContext(client, lifetimeToken), startToken);

                startToken.ThrowIfCancellationRequested();
                lock (_stateLock)
                {
                    if (_state == ClientState.Disposed)
                    {
                        startToken.ThrowIfCancellationRequested();
                        throw new ObjectDisposedException(nameof(MqttClientManager));
                    }

                    _state = ClientState.Running;
                    _isRunning = true;
                }

                _log?.Invoke($"[MQTT] Client started {BrokerIp}:{BrokerPort}");
            }
            catch (OperationCanceledException)
            {
                await RollbackFailedStartAsync(previousState, "StartAsync canceled");
                throw;
            }
            catch (Exception ex)
            {
                await RollbackFailedStartAsync(previousState, "StartAsync rollback failed");
                _logException?.Invoke(ex, "StartAsync error", true);
                throw;
            }
        }
        finally
        {
            _stateSemaphore.Release();
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
            // volatile フラグによるスレッドセーフな早期終了チェック。
            // _lifetimeCts への直接アクセスを避けることで、
            // StopAsync/Dispose との競合による NullReferenceException を防ぐ。
            if (!_isRunning) return;

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
    /// MQTT クライアントを一時停止する。
    /// StartAsync で再起動可能。ManagedMqttClient インスタンスは破棄せず再利用できる。
    /// </summary>
    /// <exception cref="ObjectDisposedException">Disposed 状態で呼ばれた場合。</exception>
    public async UniTask StopAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _stateSemaphore.WaitAsync(ct);
        try
        {
            IManagedMqttClient client = null;
            CancellationTokenSource lifetimeCts = null;

            lock (_stateLock)
            {
                if (_state == ClientState.Disposed)
                    throw new ObjectDisposedException(nameof(MqttClientManager));
                if (_state != ClientState.Running)
                {
                    _log?.Invoke("[MQTT] Not running, nothing to stop.");
                    return;
                }
                // Stopped へ遷移（セマフォ保持中なので競合なし）
                _state = ClientState.Stopped;
                client = _client;
                lifetimeCts = _lifetimeCts;
                _lifetimeCts = null;
            }

            // _isRunning を先に false にして HandleMessageReceived の処理を止める
            _isRunning = false;
            lifetimeCts?.Cancel();

            try
            {
                // _client は破棄しない（再起動時に再利用するため）
                await StopClientWithTimeoutAsync(client, "[WARN] StopAsync: ManagedMqttClient.StopAsync timeout.");
            }
            catch (Exception ex)
            {
                _logException?.Invoke(ex, "StopAsync error", true);
            }
            finally
            {
                lifetimeCts?.Dispose();
            }

            _log?.Invoke("[MQTT] Client stopped.");
        }
        finally
        {
            _stateSemaphore.Release();
        }
    }

    /// <summary>
    /// 指定トピックにメッセージを送信する。
    /// Running 状態でのみ動作する。ネットワーク切断中は ManagedMqttClient が内部キューに積み、
    /// 再接続後に自動送信する（ManagedMqttClient の責務）。
    /// </summary>
    /// <exception cref="InvalidOperationException">Running 以外の状態で呼ばれた場合。</exception>
    /// <exception cref="ObjectDisposedException">Disposed 状態で呼ばれた場合。</exception>
    public async UniTask PublishAsync(
        string topic,
        byte[] payload,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce,
        bool retain = false,
        CancellationToken ct = default)
    {
        // 自動再接続は ManagedMqttClient の責務のため、ここでは EnsureConnectedAsync を呼ばない。
        // 利用者が明示的に Stop した場合は例外をスローし、意図しない再接続を防ぐ。
        var context = GetRequiredRunningOperationContext("publish");

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(qos)
            .WithRetainFlag(retain)
            .Build();

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.LifetimeToken, ct);
            await AwaitWithCancellation(context.Client.PublishAsync(message).AsUniTask(), linked.Token);
            _log?.Invoke($"[PUB] topic={topic}, size={payload?.Length ?? 0}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logException?.Invoke(ex, "PublishAsync failed", true);
            throw;
        }
    }

    /// <summary>
    /// 指定トピックを購読する。
    /// <para>
    /// 【2フェーズ処理】<br/>
    /// ① ハンドラー登録（同期・即時完了）<br/>
    /// ② ネットワーク購読（非同期・Running 時のみ実行）<br/>
    /// Running 以外の状態では①のみ実行し、StartAsync 後に SubscribeAllRegisteredTopicsAsync が②を自動実行する。<br/>
    /// ①と②の間には非原子な窓が存在するが、①→②の順序により
    /// 「ハンドラーなしでメッセージを受信する」事態を防ぐ安全側の順序を保つ。
    /// </para>
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

        // ② トピック登録（新規の場合のみネットワーク購読へ進む）
        bool added;
        lock (_topicLock) { added = _subscribedTopics.Add(topic); }

        if (!added)
        {
            _log?.Invoke($"Topic already subscribed: {topic}");
            return;
        }

        if (!TryGetRunningOperationContext(out var context))
        {
            _log?.Invoke($"Topic registered (will subscribe on connect): {topic}");
            return;
        }

        // ③ ネットワーク購読（Running 時のみ）
        await SubscribeToTopicAsync(context, topic, ct);
    }

    /// <summary>
    /// 指定トピックの購読を解除する。
    /// </summary>
    /// <exception cref="ObjectDisposedException">Disposed 状態で呼ばれた場合。</exception>
    public async UniTask UnsubscribeAsync(string topic, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(topic)) return;
        ThrowIfDisposed();

        lock (_topicLock)
        {
            if (!_subscribedTopics.Remove(topic)) return;
        }

        RemoveMessageHandler(topic);

        if (!TryGetRunningOperationContext(out var context)) return;

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.LifetimeToken, ct);
            await AwaitWithCancellation(context.Client.UnsubscribeAsync(topic).AsUniTask(), linked.Token);
            _log?.Invoke($"Unsubscribed from topic: {topic}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logException?.Invoke(ex, $"Failed to unsubscribe from {topic}", true);
            throw;
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

        CancellationTokenSource lifetimeCts = null;
        lock (_stateLock)
        {
            _state = ClientState.Disposed;
            lifetimeCts = _lifetimeCts;
            _lifetimeCts = null;
        }
        // volatile フラグを即時 false にして HandleMessageReceived の処理を止める
        _isRunning = false;
        // 進行中の非同期操作をキャンセル
        lifetimeCts?.Cancel();
        lifetimeCts?.Dispose();
        return true;
    }

    private async UniTask CoreDisposeAsync()
    {
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
            _client?.Dispose();
            _client = null;
            _stateSemaphore.Dispose();
            _log?.Invoke("[MQTT] Client disposed.");
        }
    }

    private async UniTaskVoid FireAndForgetDispose() => await CoreDisposeAsync();

    // ========= Private Helpers =========

    /// <summary>Disposed 状態であれば ObjectDisposedException をスローする。</summary>
    private void ThrowIfDisposed()
    {
        // _disposeGuard は volatile 相当の保証を Interlocked が提供する
        if (_disposeGuard == 1)
            throw new ObjectDisposedException(nameof(MqttClientManager));
    }

    private RunningOperationContext GetRequiredRunningOperationContext(string operationName)
    {
        lock (_stateLock)
        {
            if (_state == ClientState.Disposed)
                throw new ObjectDisposedException(nameof(MqttClientManager));
            if (_state != ClientState.Running)
                throw new InvalidOperationException(
                    $"Cannot {operationName}: client is {_state}. Call StartAsync first.");
            if (_client == null || _lifetimeCts == null)
                throw new OperationCanceledException($"Cannot {operationName}: client is shutting down.");

            return new RunningOperationContext(_client, _lifetimeCts.Token);
        }
    }

    private bool TryGetRunningOperationContext(out RunningOperationContext context)
    {
        lock (_stateLock)
        {
            if (_state == ClientState.Disposed)
                throw new ObjectDisposedException(nameof(MqttClientManager));
            if (_state != ClientState.Running || _client == null || _lifetimeCts == null)
            {
                context = default;
                return false;
            }

            context = new RunningOperationContext(_client, _lifetimeCts.Token);
            return true;
        }
    }

    private async UniTask SubscribeAllRegisteredTopicsAsync(RunningOperationContext context, CancellationToken ct)
    {
        string[] topics;
        lock (_topicLock)
        {
            topics = new string[_subscribedTopics.Count];
            _subscribedTopics.CopyTo(topics);
        }

        foreach (var topic in topics)
            await SubscribeToTopicAsync(context, topic, ct);
    }

    private async UniTask SubscribeToTopicAsync(RunningOperationContext context, string topic, CancellationToken ct)
    {
        try
        {
            var topicFilter = new MqttTopicFilterBuilder()
                .WithTopic(topic)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                .Build();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.LifetimeToken, ct);
            await AwaitWithCancellation(context.Client.SubscribeAsync(topicFilter).AsUniTask(), linked.Token);
            _log?.Invoke($"Subscribed to topic: {topic}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logException?.Invoke(ex, $"Failed to subscribe to {topic}", true);
            throw;
        }
    }

    private async UniTask RollbackFailedStartAsync(ClientState fallbackState, string rollbackErrorMessage)
    {
        IManagedMqttClient client;
        CancellationTokenSource lifetimeCts;

        lock (_stateLock)
        {
            client = _client;
            lifetimeCts = _lifetimeCts;

            if (_state != ClientState.Disposed)
                _state = fallbackState;

            _lifetimeCts = null;
        }

        _isRunning = false;
        lifetimeCts?.Cancel();

        try
        {
            await StopClientWithTimeoutAsync(client, "[WARN] StartAsync rollback: ManagedMqttClient.StopAsync timeout.");
        }
        catch (Exception ex)
        {
            _logException?.Invoke(ex, rollbackErrorMessage, false);
        }
        finally
        {
            lifetimeCts?.Dispose();
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