using System;
using System.Globalization;

/// <summary>
/// MQTT データの単一アイテムのエントリ。
/// 値だけでなく、ソース時刻と受信時刻を保持する不変オブジェクト。
/// </summary>
public sealed class MqttDataEntry
{
    /// <summary>
    /// 格納されている値。
    /// </summary>
    public object Value { get; }

    /// <summary>
    /// 送信元デバイスが付与したデータ生成時刻（UTC）。
    /// 不明な場合は null。
    /// </summary>
    public DateTime? SourceTimestampUtc { get; }

    /// <summary>
    /// Unity クライアントがこのメッセージを受信した時刻（UTC）。
    /// </summary>
    public DateTime ReceivedUtc { get; }

    /// <summary>
    /// 受信時刻からの経過時間。
    /// </summary>
    public TimeSpan Age => DateTime.UtcNow - ReceivedUtc;

    public MqttDataEntry(object value, DateTime? sourceTimestampUtc, DateTime receivedUtc)
    {
        Value = value;
        SourceTimestampUtc = sourceTimestampUtc;
        ReceivedUtc = receivedUtc;
    }

    /// <summary>
    /// 値を指定型で取得を試みる。
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="result"></param>
    /// <returns></returns>
    public bool TryGetValue<T>(out T result)
    {
        if (Value is T t)
        {
            result = t;
            return true;
        }

        // Newtonsoft.Json は整数を long、浮動小数点を double としてデシリアライズするため、
        // int や float で取得する際に Convert.ChangeType でキャストする。
        if (Value != null)
        {
            try
            {
                result = (T)Convert.ChangeType(Value, typeof(T), CultureInfo.InvariantCulture);
                return true;
            }
            catch { }
        }

        result = default;
        return false;
    }
}
