using System;
using System.Security.Authentication;

namespace PullSub.Core
{
    public enum MqttTransportKind
    {
        /// <summary>MQTT over TCP（既定）</summary>
        Tcp = 0,
        /// <summary>MQTT over WebSocket (ws://)</summary>
        Ws = 1,
        /// <summary>MQTT over Secure WebSocket (wss://)</summary>
        Wss = 2,
    }

    public sealed class MqttTransportOptions : IEquatable<MqttTransportOptions>
    {
        private const string DefaultSubprotocol = "mqtt";
        private const string DefaultPath = "/";

        public static MqttTransportOptions TcpDefault { get; } = new MqttTransportOptions(MqttTransportKind.Tcp);

        public MqttTransportOptions(
            MqttTransportKind kind,
            string webSocketPath = null,
            string webSocketSubprotocol = null)
        {
            if (!Enum.IsDefined(typeof(MqttTransportKind), kind))
                throw new ArgumentOutOfRangeException(nameof(kind));

            Kind = kind;

            if (kind == MqttTransportKind.Tcp)
            {
                WebSocketPath = DefaultPath;
                WebSocketSubprotocol = DefaultSubprotocol;
                return;
            }

            // Ws / Wss: Path
            if (string.IsNullOrEmpty(webSocketPath))
            {
                WebSocketPath = DefaultPath;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(webSocketPath))
                    throw new ArgumentException("webSocketPath cannot be whitespace-only.", nameof(webSocketPath));
                WebSocketPath = webSocketPath.StartsWith("/") ? webSocketPath : "/" + webSocketPath;
            }

            // Ws / Wss: Subprotocol
            if (string.IsNullOrEmpty(webSocketSubprotocol))
            {
                WebSocketSubprotocol = DefaultSubprotocol;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(webSocketSubprotocol))
                    throw new ArgumentException("webSocketSubprotocol cannot be whitespace-only.", nameof(webSocketSubprotocol));

                WebSocketSubprotocol = webSocketSubprotocol;
            }
        }

        public MqttTransportKind Kind { get; }

        /// <summary>
        /// WebSocket のパス。Kind=Tcp では未使用。
        /// Kind=Ws/Wss で未指定の場合は "/" に正規化されます。
        /// </summary>
        public string WebSocketPath { get; }

        /// <summary>
        /// WebSocket のサブプロトコル。Kind=Tcp では未使用。
        /// Kind=Ws/Wss で未指定の場合は "mqtt" が使われます。
        /// </summary>
        public string WebSocketSubprotocol { get; }

        public bool Equals(MqttTransportOptions other)
        {
            if (ReferenceEquals(null, other))
                return false;

            if (ReferenceEquals(this, other))
                return true;

            return Kind == other.Kind
                && string.Equals(WebSocketPath, other.WebSocketPath, StringComparison.Ordinal)
                && string.Equals(WebSocketSubprotocol, other.WebSocketSubprotocol, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) => Equals(obj as MqttTransportOptions);

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = (int)Kind;
                hashCode = (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(WebSocketPath);
                hashCode = (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(WebSocketSubprotocol);
                return hashCode;
            }
        }
    }


    public sealed class MqttCredentials : IEquatable<MqttCredentials>
    {
        public static MqttCredentials Anonymous { get; } = new MqttCredentials();

        public MqttCredentials(string username = null, string password = null)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                if (!string.IsNullOrEmpty(password))
                    throw new ArgumentException(
                        "username is required when password is provided. Whitespace-only username is not allowed.",
                        nameof(password));

                Username = null;
                Password = null;
                return;
            }

            Username = username;
            Password = password ?? string.Empty;
        }

        public string Username { get; }
        public string Password { get; }
        public bool HasCredentials => !string.IsNullOrWhiteSpace(Username);

        public bool Equals(MqttCredentials other)
        {
            if (ReferenceEquals(null, other))
                return false;

            if (ReferenceEquals(this, other))
                return true;

            return string.Equals(Username, other.Username, StringComparison.Ordinal)
                && string.Equals(Password, other.Password, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) => Equals(obj as MqttCredentials);

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = Username != null ? StringComparer.Ordinal.GetHashCode(Username) : 0;
                hashCode = (hashCode * 397) ^ (Password != null ? StringComparer.Ordinal.GetHashCode(Password) : 0);
                return hashCode;
            }
        }
    }

    public sealed class MqttTlsOptions : IEquatable<MqttTlsOptions>
    {
        public static MqttTlsOptions Disabled { get; } = new MqttTlsOptions(false);

        public MqttTlsOptions(
            bool enabled,
            bool allowUntrustedCertificates = false,
            bool ignoreCertificateChainErrors = false,
            bool ignoreCertificateRevocationErrors = false,
            string targetHost = null,
            SslProtocols sslProtocols = SslProtocols.None)
        {
            if (!enabled)
            {
                if (allowUntrustedCertificates)
                    throw new ArgumentException(
                        "allowUntrustedCertificates is only valid when TLS is enabled.",
                        nameof(allowUntrustedCertificates));

                if (ignoreCertificateChainErrors)
                    throw new ArgumentException(
                        "ignoreCertificateChainErrors is only valid when TLS is enabled.",
                        nameof(ignoreCertificateChainErrors));

                if (ignoreCertificateRevocationErrors)
                    throw new ArgumentException(
                        "ignoreCertificateRevocationErrors is only valid when TLS is enabled.",
                        nameof(ignoreCertificateRevocationErrors));

                if (!string.IsNullOrEmpty(targetHost))
                    throw new ArgumentException("targetHost is only valid when TLS is enabled.", nameof(targetHost));

                if (sslProtocols != SslProtocols.None)
                    throw new ArgumentException("sslProtocols is only valid when TLS is enabled.", nameof(sslProtocols));
            }

            if (enabled && targetHost != null && string.IsNullOrWhiteSpace(targetHost))
                throw new ArgumentException("targetHost cannot be whitespace-only.", nameof(targetHost));

            Enabled = enabled;
            AllowUntrustedCertificates = allowUntrustedCertificates;
            IgnoreCertificateChainErrors = ignoreCertificateChainErrors;
            IgnoreCertificateRevocationErrors = ignoreCertificateRevocationErrors;
            TargetHost = enabled && !string.IsNullOrEmpty(targetHost) ? targetHost : null;
            SslProtocols = enabled ? sslProtocols : SslProtocols.None;
        }

        public bool Enabled { get; }
        public bool AllowUntrustedCertificates { get; }
        public bool IgnoreCertificateChainErrors { get; }
        public bool IgnoreCertificateRevocationErrors { get; }
        public string TargetHost { get; }
        public SslProtocols SslProtocols { get; }

        public bool Equals(MqttTlsOptions other)
        {
            if (ReferenceEquals(null, other))
                return false;

            if (ReferenceEquals(this, other))
                return true;

            return Enabled == other.Enabled
                && AllowUntrustedCertificates == other.AllowUntrustedCertificates
                && IgnoreCertificateChainErrors == other.IgnoreCertificateChainErrors
                && IgnoreCertificateRevocationErrors == other.IgnoreCertificateRevocationErrors
                && string.Equals(TargetHost, other.TargetHost, StringComparison.Ordinal)
                && SslProtocols == other.SslProtocols;
        }

        public override bool Equals(object obj) => Equals(obj as MqttTlsOptions);

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = Enabled.GetHashCode();
                hashCode = (hashCode * 397) ^ AllowUntrustedCertificates.GetHashCode();
                hashCode = (hashCode * 397) ^ IgnoreCertificateChainErrors.GetHashCode();
                hashCode = (hashCode * 397) ^ IgnoreCertificateRevocationErrors.GetHashCode();
                hashCode = (hashCode * 397) ^ (TargetHost != null ? StringComparer.Ordinal.GetHashCode(TargetHost) : 0);
                hashCode = (hashCode * 397) ^ (int)SslProtocols;
                return hashCode;
            }
        }
    }

    public sealed class MqttKeepAliveOptions : IEquatable<MqttKeepAliveOptions>
    {
        public const int DefaultSeconds = 15;
        public const int MinSeconds = 1;
        public const int MaxSeconds = 3600;

        public static MqttKeepAliveOptions Default { get; }
            = new MqttKeepAliveOptions(enabled: true, seconds: DefaultSeconds);

        public static MqttKeepAliveOptions Disabled { get; }
            = new MqttKeepAliveOptions(enabled: false);

        public MqttKeepAliveOptions(bool enabled, int seconds = DefaultSeconds)
        {
            if (enabled && (seconds < MinSeconds || seconds > MaxSeconds))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(seconds),
                    $"seconds must be in range [{MinSeconds}, {MaxSeconds}] when keep alive is enabled.");
            }

            if (!enabled && seconds != DefaultSeconds)
            {
                throw new ArgumentException(
                    "seconds must not be customized when keep alive is disabled.",
                    nameof(seconds));
            }

            Enabled = enabled;
            Seconds = enabled ? seconds : 0;
        }

        public bool Enabled { get; }
        public int Seconds { get; }

        public bool Equals(MqttKeepAliveOptions other)
        {
            if (ReferenceEquals(null, other))
                return false;

            if (ReferenceEquals(this, other))
                return true;

            return Enabled == other.Enabled
                && Seconds == other.Seconds;
        }

        public override bool Equals(object obj) => Equals(obj as MqttKeepAliveOptions);

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = Enabled.GetHashCode();
                hashCode = (hashCode * 397) ^ Seconds;
                return hashCode;
            }
        }
    }

    public sealed class MqttWillOptions : IEquatable<MqttWillOptions>
    {
        public static MqttWillOptions Disabled { get; } = new MqttWillOptions(false);

        public MqttWillOptions(
            bool enabled,
            string topic = null,
            string payloadUtf8 = null,
            PullSubQualityOfServiceLevel qos = PullSubQualityOfServiceLevel.AtMostOnce,
            bool retain = false)
        {
            if (enabled && !Enum.IsDefined(typeof(PullSubQualityOfServiceLevel), qos))
                throw new ArgumentOutOfRangeException(nameof(qos));

            if (enabled)
                PullSubTopicValidator.ValidateExactMatchTopic(topic);

            Enabled = enabled;
            Topic = enabled ? topic : null;
            PayloadUtf8 = enabled ? (payloadUtf8 ?? string.Empty) : null;
            Qos = enabled ? qos : PullSubQualityOfServiceLevel.AtMostOnce;
            Retain = enabled && retain;
        }

        public bool Enabled { get; }
        public string Topic { get; }
        public string PayloadUtf8 { get; }
        public PullSubQualityOfServiceLevel Qos { get; }
        public bool Retain { get; }

        public bool Equals(MqttWillOptions other)
        {
            if (ReferenceEquals(null, other))
                return false;

            if (ReferenceEquals(this, other))
                return true;

            return Enabled == other.Enabled
                && string.Equals(Topic, other.Topic, StringComparison.Ordinal)
                && string.Equals(PayloadUtf8, other.PayloadUtf8, StringComparison.Ordinal)
                && Qos == other.Qos
                && Retain == other.Retain;
        }

        public override bool Equals(object obj) => Equals(obj as MqttWillOptions);

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = Enabled.GetHashCode();
                hashCode = (hashCode * 397) ^ (Topic != null ? StringComparer.Ordinal.GetHashCode(Topic) : 0);
                hashCode = (hashCode * 397) ^ (PayloadUtf8 != null ? StringComparer.Ordinal.GetHashCode(PayloadUtf8) : 0);
                hashCode = (hashCode * 397) ^ (int)Qos;
                hashCode = (hashCode * 397) ^ Retain.GetHashCode();
                return hashCode;
            }
        }
    }

    public sealed class MqttSubscriptionDefaults : IEquatable<MqttSubscriptionDefaults>
    {
        public static MqttSubscriptionDefaults Default { get; }
            = new MqttSubscriptionDefaults(PullSubQualityOfServiceLevel.AtMostOnce);

        public MqttSubscriptionDefaults(PullSubQualityOfServiceLevel subscribeQos)
        {
            if (!Enum.IsDefined(typeof(PullSubQualityOfServiceLevel), subscribeQos))
                throw new ArgumentOutOfRangeException(nameof(subscribeQos));

            SubscribeQos = subscribeQos;
        }

        public PullSubQualityOfServiceLevel SubscribeQos { get; }

        public bool Equals(MqttSubscriptionDefaults other)
        {
            if (ReferenceEquals(null, other))
                return false;

            if (ReferenceEquals(this, other))
                return true;

            return SubscribeQos == other.SubscribeQos;
        }

        public override bool Equals(object obj) => Equals(obj as MqttSubscriptionDefaults);

        public override int GetHashCode() => (int)SubscribeQos;
    }

    public sealed class MqttConnectionOptions : IEquatable<MqttConnectionOptions>
    {
        public static MqttConnectionOptions Default { get; } = new MqttConnectionOptions();

        public MqttConnectionOptions(
            MqttCredentials credentials = null,
            int keepAliveSeconds = 15,
            int reconnectDelaySeconds = 5,
            bool useCleanSession = true,
            MqttTlsOptions tls = null,
            MqttWillOptions will = null,
            MqttSubscriptionDefaults subscriptionDefaults = null,
            MqttTransportOptions transport = null)
            : this(
                credentials,
                new MqttKeepAliveOptions(enabled: true, seconds: keepAliveSeconds),
                reconnectDelaySeconds,
                useCleanSession,
                tls,
                will,
                subscriptionDefaults,
                transport)
        {
        }

        public MqttConnectionOptions(
            MqttCredentials credentials,
            MqttKeepAliveOptions keepAlive,
            int reconnectDelaySeconds = 5,
            bool useCleanSession = true,
            MqttTlsOptions tls = null,
            MqttWillOptions will = null,
            MqttSubscriptionDefaults subscriptionDefaults = null,
            MqttTransportOptions transport = null)
        {
            if (reconnectDelaySeconds < 1)
                throw new ArgumentOutOfRangeException(nameof(reconnectDelaySeconds), "reconnectDelaySeconds must be greater than 0.");

            Credentials = credentials ?? MqttCredentials.Anonymous;
            KeepAlive = keepAlive ?? MqttKeepAliveOptions.Default;
            ReconnectDelaySeconds = reconnectDelaySeconds;
            UseCleanSession = useCleanSession;
            Tls = tls ?? MqttTlsOptions.Disabled;
            Will = will ?? MqttWillOptions.Disabled;
            SubscriptionDefaults = subscriptionDefaults ?? MqttSubscriptionDefaults.Default;
            Transport = transport ?? MqttTransportOptions.TcpDefault;
        }

        public MqttCredentials Credentials { get; }
        public MqttKeepAliveOptions KeepAlive { get; }
        public int KeepAliveSeconds => KeepAlive.Enabled ? KeepAlive.Seconds : 0;
        public int ReconnectDelaySeconds { get; }
        public bool UseCleanSession { get; }
        public MqttTlsOptions Tls { get; }
        public MqttWillOptions Will { get; }
        public MqttSubscriptionDefaults SubscriptionDefaults { get; }
        public MqttTransportOptions Transport { get; }

        public bool Equals(MqttConnectionOptions other)
        {
            if (ReferenceEquals(null, other))
                return false;

            if (ReferenceEquals(this, other))
                return true;

            return Equals(Credentials, other.Credentials)
                && Equals(KeepAlive, other.KeepAlive)
                && ReconnectDelaySeconds == other.ReconnectDelaySeconds
                && UseCleanSession == other.UseCleanSession
                && Equals(Tls, other.Tls)
                && Equals(Will, other.Will)
                && Equals(SubscriptionDefaults, other.SubscriptionDefaults)
                && Equals(Transport, other.Transport);
        }

        public override bool Equals(object obj) => Equals(obj as MqttConnectionOptions);

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = Credentials != null ? Credentials.GetHashCode() : 0;
                hashCode = (hashCode * 397) ^ (KeepAlive != null ? KeepAlive.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ ReconnectDelaySeconds;
                hashCode = (hashCode * 397) ^ UseCleanSession.GetHashCode();
                hashCode = (hashCode * 397) ^ (Tls != null ? Tls.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ (Will != null ? Will.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ (SubscriptionDefaults != null ? SubscriptionDefaults.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ (Transport != null ? Transport.GetHashCode() : 0);
                return hashCode;
            }
        }
    }
}