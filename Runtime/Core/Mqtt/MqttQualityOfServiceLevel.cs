namespace PullSub.Core
{    
    public enum MqttQualityOfServiceLevel
    {
        AtMostOnce = 0,
        AtLeastOnce = 1,
        ExactlyOnce = 2
    }
}