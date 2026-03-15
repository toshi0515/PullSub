namespace UnityMqtt.V2.Core
{
    public enum MqttV2ClientState
    {
        NotStarted = 0,
        Starting = 1,
        Ready = 2,
        Disconnected = 3,
        Reconnecting = 4,
        ResubscribePending = 5,
        Stopped = 6,
        Disposed = 7
    }
}
