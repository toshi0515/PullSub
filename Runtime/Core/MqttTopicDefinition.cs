using System.Reflection;

/// <summary>
/// MQTTトピックのデータスキーマを定義する抽象基底クラス。
/// 継承クラスで <see cref="TopicPath"/> とデータフィールド（<see cref="MqttField{T}"/>）を定義する。
/// </summary>
/// <remarks>
/// <see cref="MqttField{T}"/> は必ず <b>public readonly フィールド</b>として定義してください。
/// プロパティとして定義した場合、リフレクションによるバインド処理がスキップされ、
/// データの受け取りに失敗します（コンパイルエラーにはなりません）。
/// <code>
/// // OK
/// public readonly MqttField&lt;float&gt; Speed = new("Speed");
///
/// // NG — 無言でバインドされずデータが取得できない
/// public MqttField&lt;float&gt; Speed { get; } = new("Speed");
/// </code>
/// </remarks>
/// <example>
/// <code>
/// public class PlcDevice1Topic : MqttTopicDefinition
/// {
///     public override string TopicPath => "plc/device1";
///
///     public readonly MqttField&lt;int&gt;   Rotation  = new("Rotation");
///     public readonly MqttField&lt;float&gt; Speed     = new("Speed");
///     public readonly MqttField&lt;bool&gt;  IsRunning = new("IsRunning");
/// }
///
/// // Usage:
/// var topic = bridge.Store.GetTopic&lt;PlcDevice1Topic&gt;();
/// if (topic.Rotation.TryGetValue(out var rot))
///     Debug.Log($"Rotation: {rot}");
/// </code>
/// </example>
public abstract class MqttTopicDefinition
{
    /// <summary>
    /// このトピック定義が対応するMQTTトピックパス（例: <c>"plc/device1"</c>）。
    /// </summary>
    public abstract string TopicPath { get; }

    /// <summary>
    /// このトピックのデータリポジトリ。Subscribe/Publishの拡張メソッドが利用する。
    /// </summary>
    public MqttDataRepository Repository { get; private set; }

    /// <summary>
    /// 指定された <see cref="MqttDataRepository"/> をこのトピックのフィールドにバインドする。
    /// <see cref="MqttDataStore.GetTopic{T}"/> から自動的に呼ばれる。
    /// </summary>
    internal void Bind(MqttDataRepository repository)
    {
        Repository = repository;

        // public インスタンスフィールドから MqttFieldBase を収集して Repository を注入
        var fields = GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
        foreach (var fi in fields)
        {
            if (fi.GetValue(this) is MqttFieldBase field)
            {
                field.Repository = repository;
            }
        }
    }
}
