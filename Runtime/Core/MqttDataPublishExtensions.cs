using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;

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

        var json = MqttDTO.SerializeEnvelope(envelope);
        var bytes = Encoding.UTF8.GetBytes(json);
        return manager.PublishAsync(topic, bytes, ct: ct);
    }

    /// <summary>
    /// MqttDataItem のリストから Envelope を自動生成して Publish する。
    /// timestamp は現在時刻（UTC / ISO8601）が自動設定される。
    /// </summary>
    public static UniTask PublishDataAsync(
        this MqttClientManager manager,
        string topic,
        IEnumerable<MqttDataItem> items,
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

        return PublishDataAsync(manager, topic, envelope, ct);
    }

    /// <summary>
    /// name/value ペアの辞書から Envelope を自動生成して Publish する。
    /// MqttDataRepository.GetAllDataSnapshot() の戻り値をそのまま渡せる。
    /// timestamp は現在時刻（UTC / ISO8601）が自動設定される。
    /// </summary>
    public static UniTask PublishDataAsync(
        this MqttClientManager manager,
        string topic,
        IReadOnlyDictionary<string, object> data,
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

        return PublishDataAsync(manager, topic, envelope, ct);
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

        return PublishDataAsync(manager, topic, envelope, ct);
    }
}
