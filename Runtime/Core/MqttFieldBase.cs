using System;

/// <summary>
/// <see cref="MqttField{T}"/> の型消去された基底クラス。
/// <see cref="MqttTopicDefinition"/> がリフレクションでフィールドを収集するために使用する。
/// </summary>
public abstract class MqttFieldBase
{
    /// <summary>
    /// MQTTデータエンベロープの "name" に対応するキー。
    /// </summary>
    public string Name { get; }

    internal MqttDataRepository Repository { get; set; }

    protected MqttFieldBase(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Field name cannot be empty.", nameof(name));
        Name = name;
    }
}

/// <summary>
/// MQTTトピックの単一データフィールドへの型安全なアクセサ。
/// <see cref="MqttTopicDefinition"/> のフィールドとして定義して使用する。
/// </summary>
public sealed class MqttField<T> : MqttFieldBase
{
    /// <summary>
    /// <paramref name="name"/> に MQTTデータエンベロープの "name" を指定する。
    /// </summary>
    public MqttField(string name) : base(name) { }

    /// <summary>
    /// 値を受信済みかどうか。
    /// </summary>
    public bool HasValue => TryGetValue(out _);

    /// <summary>
    /// 最新の値。未受信の場合は <c>default(T)</c>。
    /// </summary>
    public T Value => TryGetValue(out var v) ? v : default;

    /// <summary>
    /// 値の取得を試みる。未受信の場合は false を返す。
    /// </summary>
    public bool TryGetValue(out T value)
    {
        if (Repository != null)
            return Repository.TryGet<T>(Name, out value);
        value = default;
        return false;
    }

    /// <summary>
    /// タイムスタンプなどを含む完全なエントリ。未受信の場合は null。
    /// </summary>
    public MqttDataEntry Entry
    {
        get
        {
            if (Repository != null && Repository.TryGetEntry(Name, out var e))
                return e;
            return null;
        }
    }
}
