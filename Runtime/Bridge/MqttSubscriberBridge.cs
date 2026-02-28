using UnityEngine;
using Cysharp.Threading.Tasks;

public class MqttSubscriberBridge : MonoBehaviour
{
    [Header("MQTT Settings")]
    [SerializeField] private string _brokerIp = "192.168.11.159";
    [SerializeField] private int _brokerPort = 1883;
    [SerializeField] private string _topic = "ARMotorSystem/data";

    private async void OnEnable()
    {
#if UNITY_EDITOR
        _brokerIp = "127.0.0.1";
#endif
        
        MqttClientManager.Instance.Configure(
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

        await MqttSubscriber.SubscribeDataTopicAsync(_topic);

        // クライアント開始
        await MqttClientManager.Instance.StartAsync();
    }

    private void OnDisable()
    {
        MqttClientManager.Instance.StopAsync().Forget();
    }

    private void OnApplicationQuit()
    {
        MqttClientManager.Instance.Dispose();
    }
}
