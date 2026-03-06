using System;
using System.Collections.Generic;

/// <summary>
/// スレッドセーフな MQTT データリポジトリ。
/// 特定のトピックに紐づくデータを辞書で管理する。
/// </summary>
public sealed class MqttDataRepository
{
    private readonly object _lock = new object();
    private readonly Dictionary<string, MqttDataEntry> _entries = new Dictionary<string, MqttDataEntry>();
    private DateTime? _lastEnvelopeSourceTimestampUtc = null;
    private DateTime _lastReceivedUtc = DateTime.MinValue;

    /// <summary>
    /// このリポジトリが担当するトピック。
    /// </summary>
    public string Topic { get; }

    public MqttDataRepository(string topic)
    {
        Topic = topic ?? throw new ArgumentNullException(nameof(topic));
    }

    /// <summary>
    /// リポジトリの内容を MQTT データエンベロープで更新する。
    /// </summary>
    public void UpdateFromEnvelope(MqttDataEnvelope envelope)
    {
        if (envelope == null || envelope.Items == null) return;

        var receivedUtc = DateTime.UtcNow;
        DateTime? sourceTimestampUtc = null;

        if (!string.IsNullOrWhiteSpace(envelope.Timestamp) &&
            System.DateTimeOffset.TryParse(
                envelope.Timestamp,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            if (parsed.UtcDateTime > DateTime.MinValue && parsed.UtcDateTime < DateTime.MaxValue)
                sourceTimestampUtc = parsed.UtcDateTime;
        }

        lock (_lock)
        {
            _lastReceivedUtc = receivedUtc;
            _lastEnvelopeSourceTimestampUtc = sourceTimestampUtc;

            foreach (var item in envelope.Items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Name)) continue;
                _entries[item.Name] = new MqttDataEntry(item.Value, sourceTimestampUtc, receivedUtc);
            }
        }
    }

    /// <summary>
    /// 最新のエンベロープに含まれていたソース時刻（UTC）。
    /// </summary>
    public DateTime? LastEnvelopeSourceTimestampUtc
    {
        get { lock (_lock) return _lastEnvelopeSourceTimestampUtc; }
    }

    /// <summary>
    /// 最後にメッセージを受信した時刻（UTC）。
    /// </summary>
    public DateTime LastReceivedUtc
    {
        get { lock (_lock) return _lastReceivedUtc; }
    }

    /// <summary>
    /// リポジトリをクリアする。
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _lastEnvelopeSourceTimestampUtc = null;
            _lastReceivedUtc = DateTime.MinValue;
        }
    }

    /// <summary>
    /// 指定した名前の値を型指定で取得する。
    /// </summary>
    public bool TryGet<T>(string name, out T value)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(name, out var entry))
                return entry.TryGetValue(out value);
        }
        value = default;
        return false;
    }

    /// <summary>
    /// 指定した名前のエントリ（時刻情報付き）を取得する。
    /// </summary>
    public bool TryGetEntry(string name, out MqttDataEntry entry)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(name, out entry);
        }
    }

    /// <summary>
    /// 現在保持しているすべてのエントリのスナップショットを取得する。
    /// </summary>
    public IReadOnlyDictionary<string, MqttDataEntry> GetSnapshot()
    {
        lock (_lock)
        {
            return new Dictionary<string, MqttDataEntry>(_entries);
        }
    }
}