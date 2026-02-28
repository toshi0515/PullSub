using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// データリポジトリの共通インターフェース
/// </summary>
public interface IDataRepository
{
    /// <summary>
    /// 送信元デバイスが付与したデータ生成時刻（UTC）。
    /// JSONに "timestamp" が存在しない・パース失敗・異常値の場合は null。
    /// </summary>
    DateTime? SourceTimestampUtc { get; }

    /// <summary>
    /// Unityクライアントがメッセージを受信した時刻（UTC）。
    /// 通信死活監視（ウォッチドッグ）に使用する。
    /// 未受信の場合は DateTime.MinValue。
    /// </summary>
    DateTime LastReceivedUtc { get; }

    void UpdateFromEnvelope(MqttDataEnvelope envelope);
    void Clear();
    bool TryGet<T>(string name, out T value);

    /// <summary>
    /// 現在保持しているすべての name/value ペアのスナップショットを取得する。
    /// デバッグや全件走査用。返却された辞書は外部から変更されない (読み取り専用コピー)。
    /// </summary>
    IReadOnlyDictionary<string, object> GetAllDataSnapshot();
}

/// <summary>
/// スレッドセーフな MQTT PLC データリポジトリ（シングルトン）
/// MQTTペイロードの name - value ペアを内部辞書で管理し、型変換も試みる
/// </summary>
public sealed class MqttDataRepository: IDataRepository
{
    private readonly object _lock = new object();
    private readonly Dictionary<string, object> _values = new Dictionary<string, object>();
    private DateTime? _sourceTimestampUtc = null;
    private DateTime _lastReceivedUtc = DateTime.MinValue;

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

            // update receive time unconditionally
            _lastReceivedUtc = DateTime.UtcNow;

            // parse source timestamp from envelope
            if (string.IsNullOrWhiteSpace(envelope.Timestamp))
            {
                _sourceTimestampUtc = null;
            }
            else if (DateTimeOffset.TryParse(
                envelope.Timestamp,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsed))
            {
                // treat MinValue/MaxValue as invalid
                if (parsed.UtcDateTime > DateTime.MinValue && parsed.UtcDateTime < DateTime.MaxValue)
                {
                    _sourceTimestampUtc = parsed.UtcDateTime;
                }
                else
                {
                    _sourceTimestampUtc = null;
                }
            }
            else
            {
                _sourceTimestampUtc = null;
            }
        }
    }

    public DateTime? SourceTimestampUtc
    {
        get { lock (_lock) return _sourceTimestampUtc; }
    }

    public DateTime LastReceivedUtc
    {
        get { lock (_lock) return _lastReceivedUtc; }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _values.Clear();
            _sourceTimestampUtc = null;
            _lastReceivedUtc = DateTime.MinValue;
        }
    }

    /// <summary>
    /// 型ヒントに基づいて値を変換を試みる
    /// bool (BIT) -> bool, int (WORD/DINT) -> int, float (REAL) -> float
    /// PLC固有の型名はエイリアスとしてサポート
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
                case "BOOL":
                case "BIT":
                    {
                        if (raw is bool b) { result = b; return true; }
                        if (raw is long l) { result = l != 0; return true; }
                        if (raw is double d) { result = Math.Abs(d) > double.Epsilon; return true; }
                        bool parsed;
                        if (raw is string s && bool.TryParse(s, out parsed)) { result = parsed; return true; }
                        return false;
                    }

                case "INT":
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

                case "FLOAT":
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

    public IReadOnlyDictionary<string, object> GetAllDataSnapshot()
    {
        lock (_lock)
        {
            // コピーを作成して返すことで、呼び出し側での変更を防ぐ
            return new Dictionary<string, object>(_values);
        }
    }
}