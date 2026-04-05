using System;

namespace PullSub.Mqtt
{
    internal sealed class MqttClientProfile : IEquatable<MqttClientProfile>
    {
        public MqttClientProfile(
            string brokerHost,
            int brokerPort,
            MqttClientIdPolicy clientIdPolicy,
            string fixedClientId = null,
            MqttConnectionOptions connectionOptions = null)
        {
            BrokerHost = string.IsNullOrWhiteSpace(brokerHost) ? throw new ArgumentException("brokerHost is required.", nameof(brokerHost)) : brokerHost;
            BrokerPort = brokerPort > 0 ? brokerPort : throw new ArgumentOutOfRangeException(nameof(brokerPort));
            ClientIdPolicy = clientIdPolicy;
            FixedClientId = fixedClientId;
            ConnectionOptions = connectionOptions ?? MqttConnectionOptions.Default;

            if (ClientIdPolicy == MqttClientIdPolicy.Fixed && string.IsNullOrWhiteSpace(FixedClientId))
                throw new ArgumentException("fixedClientId is required when clientIdPolicy is Fixed.", nameof(fixedClientId));
        }

        public string BrokerHost { get; }
        public int BrokerPort { get; }
        public MqttClientIdPolicy ClientIdPolicy { get; }
        public string FixedClientId { get; }
        public MqttConnectionOptions ConnectionOptions { get; }

        public bool Equals(MqttClientProfile other)
        {
            if (ReferenceEquals(null, other))
                return false;

            if (ReferenceEquals(this, other))
                return true;

            return string.Equals(BrokerHost, other.BrokerHost, StringComparison.Ordinal)
                && BrokerPort == other.BrokerPort
                && ClientIdPolicy == other.ClientIdPolicy
                && string.Equals(FixedClientId, other.FixedClientId, StringComparison.Ordinal)
                && Equals(ConnectionOptions, other.ConnectionOptions);
        }

        public override bool Equals(object obj) => Equals(obj as MqttClientProfile);

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = (BrokerHost != null ? StringComparer.Ordinal.GetHashCode(BrokerHost) : 0);
                hashCode = (hashCode * 397) ^ BrokerPort;
                hashCode = (hashCode * 397) ^ (int)ClientIdPolicy;
                hashCode = (hashCode * 397) ^ (FixedClientId != null ? StringComparer.Ordinal.GetHashCode(FixedClientId) : 0);
                hashCode = (hashCode * 397) ^ (ConnectionOptions != null ? ConnectionOptions.GetHashCode() : 0);
                return hashCode;
            }
        }
    }
}
