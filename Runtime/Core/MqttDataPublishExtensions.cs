using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using MQTTnet.Protocol;

/// <summary>
/// MqttClientManager の拡張メソッド — MqttDataEnvelope 形式でデータを Publish する。
/// Subscribe 側（MqttSubscribeExtensions）と対称な JSON フォーマットを生成する。
/// </summary>
public static class MqttDataPublishExtensions
{
    /// <summary>
    /// MqttDataEnvelope をそのまま JSON シリアライズして Publish する。
    /// timestamp やアイテムを自由に構成したい場合に使用する。
    /// </summary>
    public static UniTask PublishDataAsync(
        this MqttClientManager manager,
        string topic,
        MqttDataEnvelope envelope,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce,
        bool retain = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            manager.LogError("PublishDataAsync: topic is required.");
            return UniTask.CompletedTask;
        }

        if (envelope == null)
        {
            manager.LogError("PublishDataAsync: envelope is required.");
            return UniTask.CompletedTask;
        }

        var json = manager.Serializer.SerializeEnvelope(envelope);
        var bytes = Encoding.UTF8.GetBytes(json);
        return manager.PublishAsync(topic, bytes, qos, retain, ct);
    }

    /// <summary>
    /// MqttDataItem のリストから Envelope を自動生成して Publish する。
    /// timestamp は現在時刻（UTC / ISO8601）が自動設定される。
    /// </summary>
    public static UniTask PublishDataAsync(
        this MqttClientManager manager,
        string topic,
        IEnumerable<MqttDataItem> items,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce,
        bool retain = false,
        CancellationToken ct = default)
    {
        if (items == null)
        {
            manager.LogError("PublishDataAsync: items is required.");
            return UniTask.CompletedTask;
        }

        var envelope = new MqttDataEnvelope
        {
            Timestamp = DateTime.UtcNow.ToString("o"),
            Items = new List<MqttDataItem>(items)
        };

        return PublishDataAsync(manager, topic, envelope, qos, retain, ct);
    }

    /// <summary>
    /// MqttDataRepository からスナップショットを取得して Publish する。
    /// 指定されたトピック、またはリポジトリ自身のトピックに送信する。
    /// </summary>
    public static UniTask PublishDataAsync(
        this MqttClientManager manager,
        MqttDataRepository repository,
        string topic = null,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce,
        bool retain = false,
        CancellationToken ct = default)
    {
        if (repository == null)
        {
            manager.LogError("PublishDataAsync: repository is required.");
            return UniTask.CompletedTask;
        }

        var targetTopic = topic ?? repository.Topic;
        return PublishDataAsync(manager, targetTopic, repository.GetSnapshot(), qos, retain, ct);
    }

    /// <summary>
    /// name/MqttDataEntry ペアの辞書から Envelope を自動生成して Publish する。
    /// timestamp は最新のエントリ、または現在時刻（UTC / ISO8601）が使用される。
    /// </summary>
    public static UniTask PublishDataAsync(
        this MqttClientManager manager,
        string topic,
        IReadOnlyDictionary<string, MqttDataEntry> data,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce,
        bool retain = false,
        CancellationToken ct = default)
    {
        if (data == null)
        {
            manager.LogError("PublishDataAsync: data is required.");
            return UniTask.CompletedTask;
        }

        var items = new List<MqttDataItem>(data.Count);
        DateTime? maxSourceTime = null;

        foreach (var kv in data)
        {
            items.Add(new MqttDataItem { Name = kv.Key, Value = kv.Value.Value });
            if (kv.Value.SourceTimestampUtc.HasValue)
            {
                if (!maxSourceTime.HasValue || kv.Value.SourceTimestampUtc > maxSourceTime)
                    maxSourceTime = kv.Value.SourceTimestampUtc;
            }
        }

        var envelope = new MqttDataEnvelope
        {
            Timestamp = maxSourceTime?.ToString("o") ?? DateTime.UtcNow.ToString("o"),
            Items = items
        };

        return PublishDataAsync(manager, topic, envelope, qos, retain, ct);
    }

    /// <summary>
    /// name/value ペアの辞書から Envelope を自動生成して Publish する。
    /// timestamp は現在時刻（UTC / ISO8601）が自動設定される。
    /// </summary>
    public static UniTask PublishDataAsync(
        this MqttClientManager manager,
        string topic,
        IReadOnlyDictionary<string, object> data,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce,
        bool retain = false,
        CancellationToken ct = default)
    {
        if (data == null)
        {
            manager.LogError("PublishDataAsync: data is required.");
            return UniTask.CompletedTask;
        }

        var items = new List<MqttDataItem>(data.Count);
        foreach (var kv in data)
        {
            items.Add(new MqttDataItem { Name = kv.Key, Value = kv.Value });
        }

        var envelope = new MqttDataEnvelope
        {
            Timestamp = DateTime.UtcNow.ToString("o"),
            Items = items
        };

        return PublishDataAsync(manager, topic, envelope, qos, retain, ct);
    }

    /// <summary>
    /// 単一の name/value ペアを Publish するショートカット。
    /// 1 つの値だけ素早く送りたい場合に使用する。
    /// </summary>
    public static UniTask PublishDataAsync(
        this MqttClientManager manager,
        string topic,
        string name,
        object value,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce,
        bool retain = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            manager.LogError("PublishDataAsync: name is required.");
            return UniTask.CompletedTask;
        }

        var envelope = new MqttDataEnvelope
        {
            Timestamp = DateTime.UtcNow.ToString("o"),
            Items = new List<MqttDataItem>
            {
                new MqttDataItem { Name = name, Value = value }
            }
        };

        return PublishDataAsync(manager, topic, envelope, qos, retain, ct);
    }
}
