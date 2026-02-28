using System;
using UnityEngine;
using Cysharp.Threading.Tasks;
using System.Threading;

public class MqttSubscriberBridge : MonoBehaviour
{
    private CancellationTokenSource _cts;
    private bool _isQuitting;

    // explicit manager instance (singleton at bridge level)
    private MqttClientManager _manager;
    public MqttClientManager Manager => _manager;

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
    [SerializeField] private string _topic = "test";

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

            // create and configure manager via constructor
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

            // 購読開始 (pass repository explicitly)
            await _manager.SubscribeDataTopicAsync(_topic, MqttDataRepository.Instance, ct);
        }
        catch (OperationCanceledException)
        {
            // expected when owner disabled/destroyed; no log needed
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[MqttSubscriberBridge] Initialization failed: {ex.Message}");
            Debug.LogException(ex);
        }
    }

    private void OnDisable()
    {
        if (_isQuitting) return; // application is quitting, cleanup done in OnApplicationQuit

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
