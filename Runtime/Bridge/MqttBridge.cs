using System;
using UnityEngine;
using Cysharp.Threading.Tasks;
using System.Threading;

/// <summary>
/// MQTT 通信の Unity エントリポイント。
/// 接続・購読・送信バインドのライフサイクルを一元管理する。
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>受信: <see cref="Store"/> を通じて型安全なデータ取得が可能</item>
///   <item>接続管理: OnEnable で接続開始。OnApplicationQuit で DisposeAsync を await して
///         DISCONNECT パケットの送信を保証する。</item>
/// </list>
/// </remarks>
public class MqttBridge : MonoBehaviour
{
    [Header("MQTT Settings")]
    [SerializeField] private string _brokerIp = "127.0.0.1";
    [SerializeField] private int _brokerPort = 1883;
    [SerializeField] private string[] _topics = { "test" };

    private CancellationTokenSource _cts;
    private bool _isQuitting;
    private MqttClientManager _manager;
    private readonly MqttDataStore _store = new MqttDataStore();

    /// <summary>MQTT クライアントマネージャー</summary>
    public MqttClientManager Manager => _manager;

    /// <summary>トピックごとのデータを保持するデータストア</summary>
    public MqttDataStore Store => _store;

    public static MqttBridge Instance { get; private set; }

    private void Awake()
    {
        if (Instance != null)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnEnable()
    {
        // 重複インスタンスは Destroy 待ちのため、Instance チェックで弾く
        if (Instance != this) return;

        _cts = new CancellationTokenSource();
        InitializeAsync(_cts.Token).Forget();
    }

    private async UniTaskVoid InitializeAsync(CancellationToken ct)
    {
        try
        {
            _manager ??= new MqttClientManager(
                brokerIp: _brokerIp,
                brokerPort: _brokerPort,
                log: msg => Debug.Log(msg),
                logError: err => Debug.LogError(err),
                logException: (ex, message, includeStackTrace) =>
                {
                    Debug.LogError($"{message}: {ex.Message}");
                    if (includeStackTrace) Debug.LogException(ex);
                }
            );

            await _manager.StartAsync(ct);

            if (_topics != null)
            {
                foreach (var topic in _topics)
                {
                    if (string.IsNullOrWhiteSpace(topic)) continue;
                    await _manager.SubscribeDataTopicAsync(topic, _store, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected（OnDisable または OnApplicationQuit によるキャンセル）
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MqttBridge] Initialization failed: {ex.Message}");
            Debug.LogException(ex);
        }
    }

    /// <summary>
    /// OnDisable では接続を維持する（再 Enable 時に再利用）。
    /// 初期化のキャンセルトークンのみ破棄し、Manager は停止しない。
    /// </summary>
    private void OnDisable()
    {
        if (_isQuitting) return;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private void OnApplicationQuit()
    {
        _isQuitting = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        // DisposeAsync を非同期で起動し、DISCONNECT パケットの送信を試みる。
        // ManagedMqttClient.StopAsync には 1500ms のタイムアウトが設定されており、
        // アプリ終了前に完了する可能性が高い。
        ShutdownAsync().Forget();
    }

    private async UniTaskVoid ShutdownAsync()
    {
        if (_manager != null)
            await _manager.DisposeAsync();
    }

    private void OnDestroy()
    {
        if (!_isQuitting)
        {
            // OnApplicationQuit が呼ばれずに OnDestroy が来る場合のフォールバック。
            // fire-and-forget になるため DISCONNECT パケットの送信は保証されない。
            _manager?.Dispose();
        }

        if (Instance == this)
            Instance = null;
    }
}