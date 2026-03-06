using System;
using UnityEngine;
using Cysharp.Threading.Tasks;
using System.Threading;

/// <summary>
/// MonoBehaviour エントリポイント。
/// MQTT 接続とデータストアのライフサイクルを管理する。
/// </summary>
public class MqttSubscriberBridge : MonoBehaviour
{
    private CancellationTokenSource _cts;
    private bool _isQuitting;

    private MqttClientManager _manager;
    private MqttDataStore _store = new MqttDataStore();

    /// <summary>
    /// MQTT クライアントマネージャー
    /// </summary>
    public MqttClientManager Manager => _manager;

    /// <summary>
    /// トピックごとのデータを保持するデータストア
    /// </summary>
    public MqttDataStore Store => _store;

    public static MqttSubscriberBridge Instance { get; private set; }

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

    [Header("MQTT Settings")]
    [SerializeField] private string _brokerIp = "127.0.0.1";
    [SerializeField] private int _brokerPort = 1883;
    [SerializeField] private string[] _topics = { "test" };

    private void OnEnable()
    {
        _cts = new CancellationTokenSource();
        InitializeAsync(_cts.Token).Forget();
    }

    private async UniTaskVoid InitializeAsync(CancellationToken ct)
    {
        try
        {
#if UNITY_EDITOR
            _brokerIp = "127.0.0.1";
#endif

            _manager = new MqttClientManager(
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

            // クライアント開始
            await _manager.StartAsync(ct);

            // 全トピックの購読開始
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
            // expected
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MqttSubscriberBridge] Initialization failed: {ex.Message}");
            Debug.LogException(ex);
        }
    }

    private void OnDisable()
    {
        if (_isQuitting) return;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _manager?.StopAsync().Forget();
    }

    private void OnApplicationQuit()
    {
        _isQuitting = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _manager?.Dispose();
    }
}
