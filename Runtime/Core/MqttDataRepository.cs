using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// データリポジトリの共通インターフェース
/// </summary>
public interface IDataRepository
{
    DateTime LastUpdateUtc { get; }
    void UpdateFromEnvelope(MqttDataEnvelope envelope);
    void Clear();
    bool TryGet<T>(string name, out T value);
}

/// <summary>
/// スレッドセーフな MQTT PLC データリポジトリ（シングルトン）
/// MQTTペイロードの name - value ペアを内部辞書で管理し、型変換も試みる
/// </summary>
public sealed class MqttDataRepository: IDataRepository
{
    private readonly object _lock = new object();
    private readonly Dictionary<string, object> _values = new Dictionary<string, object>();
    private DateTime _lastUpdateUtc = DateTime.MinValue;

    public static MqttDataRepository Instance { get; } = new MqttDataRepository();

    private MqttDataRepository() { }

    /// <summary>
    /// 辞書を MQTT データエンベロープの内容で更新する
    /// </summary>
    /// <param name="envelope"></param>
    public void UpdateFromEnvelope(MqttDataEnvelope envelope)
    {
        if (envelope == null || envelope.Items == null) return;

        lock (_lock)
        {
            foreach (var item in envelope.Items)
            {
                if (item == null) continue;
                if (string.IsNullOrWhiteSpace(item.Name)) continue;

                object coerced;
                if (!TryCoerceValue(item.Type, item.Value, out coerced))
                {
                    // 変換に失敗した場合はそのまま格納（診断時に参照できるように）
                    coerced = item.Value;
                }

                _values[item.Name] = coerced;
            }

            _lastUpdateUtc = DateTime.UtcNow;
        }
    }

    public DateTime LastUpdateUtc
    {
        get { lock (_lock) return _lastUpdateUtc; }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _values.Clear();
            _lastUpdateUtc = DateTime.MinValue;
        }
    }

    /// <summary>
    /// 型ヒントに基づいて値を変換を試みる
    /// BIT -> bool, WORD/DINT -> int, REAL -> float
    /// </summary>
    /// <param name="typeHint"></param>
    /// <param name="raw"></param>
    /// <param name="result"></param>
    /// <returns></returns>
    private static bool TryCoerceValue(string typeHint, object raw, out object result)
    {
        result = raw;
        var hint = (typeHint ?? string.Empty).Trim().ToUpperInvariant();

        try
        {
            switch (hint)
            {
                case "BIT":
                    {
                        if (raw is bool b) { result = b; return true; }
                        if (raw is long l) { result = l != 0; return true; }
                        if (raw is double d) { result = Math.Abs(d) > double.Epsilon; return true; }
                        bool parsed;
                        if (raw is string s && bool.TryParse(s, out parsed)) { result = parsed; return true; }
                        return false;
                    }

                case "WORD":
                case "DINT":
                    {
                        if (raw is long l) { result = unchecked((int)l); return true; }
                        if (raw is double d) { result = unchecked((int)d); return true; }
                        if (raw is bool b) { result = b ? 1 : 0; return true; }
                        int parsed;
                        if (raw is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                        {
                            result = parsed; return true;
                        }
                        return false;
                    }

                case "REAL":
                    {
                        if (raw is double dd) { result = (float)dd; return true; }
                        if (raw is long l) { result = (float)l; return true; }
                        float parsed;
                        if (raw is string s && float.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out parsed))
                        {
                            result = parsed; return true;
                        }
                        return false;
                    }

                default:
                    // 型ヒント不明の場合はそのまま
                    return true;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 辞書から指定型で値を取得を試みる
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="name">MQTTペイロードのname</param>
    /// <param name="value">MQTTペイロードのvalue</param>
    /// <returns></returns>
    public bool TryGet<T>(string name, out T value)
    {
        lock (_lock)
        {
            object obj;
            if (_values.TryGetValue(name, out obj) && obj is T t)
            {
                value = t;
                return true;
            }
        }
        value = default(T);
        return false;
    }
}