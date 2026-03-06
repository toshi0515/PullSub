using System;
using System.Collections.Generic;

/// <summary>
/// 複数のトピックのリポジトリを管理するレジストリ。
/// <see cref="GetTopic{T}"/> で型安全にトピックにアクセスする。
/// </summary>
public sealed class MqttDataStore
{
    private readonly object _lock = new object();
    private readonly Dictionary<string, MqttDataRepository> _repositories
        = new Dictionary<string, MqttDataRepository>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Type, MqttTopicDefinition> _topicCache
        = new Dictionary<Type, MqttTopicDefinition>();

    /// <summary>
    /// 型安全なトピック定義インスタンスを取得する。
    /// 初回呼び出し時にインスタンスを生成し内部でキャッシュする。
    /// <typeparamref name="T"/> の <see cref="MqttTopicDefinition.TopicPath"/> に対応する
    /// <see cref="MqttDataRepository"/> が自動的にバインドされる。
    /// </summary>
    public T GetTopic<T>() where T : MqttTopicDefinition, new()
    {
        lock (_lock)
        {
            if (_topicCache.TryGetValue(typeof(T), out var cached))
                return (T)cached;

            var topic = new T();
            var repo = GetOrCreateNoLock(topic.TopicPath);
            topic.Bind(repo);
            _topicCache[typeof(T)] = topic;
            return topic;
        }
    }

    /// <summary>
    /// 指定されたトピックに対応するリポジトリを取得する（Subscribe拡張メソッド用）。
    /// 存在しない場合は新しく作成される。
    /// </summary>
    public MqttDataRepository GetOrCreate(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
            throw new ArgumentException("Topic cannot be empty", nameof(topic));

        lock (_lock)
        {
            return GetOrCreateNoLock(topic);
        }
    }

    // ロックを取得済みの状態から呼ぶ内部ヘルパー。
    private MqttDataRepository GetOrCreateNoLock(string topic)
    {
        if (!_repositories.TryGetValue(topic, out var repo))
        {
            repo = new MqttDataRepository(topic);
            _repositories[topic] = repo;
        }
        return repo;
    }

    /// <summary>
    /// 登録されているすべてのリポジトリのスナップショットを取得する。
    /// </summary>
    public IReadOnlyDictionary<string, MqttDataRepository> All
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<string, MqttDataRepository>(_repositories);
            }
        }
    }

    /// <summary>
    /// すべてのリポジトリの内容をクリアする。
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var repo in _repositories.Values)
                repo.Clear();
            _repositories.Clear();
            _topicCache.Clear();
        }
    }

    /// <summary>
    /// デバイス（トピック）ごとの死活状態を取得する。
    /// </summary>
    public IEnumerable<(string topic, TimeSpan age)> GetHealthStatus()
    {
        var snapshot = All;
        foreach (var kv in snapshot)
        {
            var last = kv.Value.LastReceivedUtc;
            var age = last == DateTime.MinValue ? TimeSpan.MaxValue : DateTime.UtcNow - last;
            yield return (kv.Key, age);
        }
    }
}
