# Unity MQTT Client

Unity Package Manager 対応の最小限 MQTT 送受信パッケージ。  
[MQTTnet](https://github.com/dotnet/MQTTnet) v3.1.2 を使用し、Unity メインスレッドへの自動ディスパッチに対応。

## 特徴

- **型安全なデータアクセス**: `MqttTopicDefinition` でトピックのデータスキーマを定義し、IDE 補完と型チェックを享受
- **マルチトピック対応**: トピックごとに独立した `MqttDataRepository` を生成
- **高精度タイムスタンプ**: 各データアイテムごとに「送信元生成時刻」と「Unity 受信時刻」を保持
- **柔軟な Publish**: 汎用 JSON、PLC コマンド、および Subscribe 互換形式でのデータ送信に対応
- **スレッド安全**: メッセージ受信は自動的に Unity メインスレッドにディスパッチ

## 前提条件

### UniTask

このパッケージは [UniTask](https://github.com/Cysharp/UniTask) に依存しています。  
パッケージ導入前に、`Packages/manifest.json` の `dependencies` に以下を追加してください:

```json
"com.cysharp.unitask": "https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask"
```

## 導入方法

### Package Manager から導入

1. Unity エディタで **Window → Package Manager** を開く
2. **+ → Add package from git URL...** を選択
3. 以下の URL を入力:

```
https://github.com/Toshi-0515/unity-mqtt.git
```

## 使い方

### 1. トピック定義を作成する

受信したいトピックのデータスキーマを `MqttTopicDefinition` を継承して定義します。  
`MqttField<T>` のフィールド名は、受信する JSON ペイロードの `"name"` と一致させてください。

```csharp
public class PlcDevice1Topic : MqttTopicDefinition
{
    public override string TopicPath => "plc/device1";

    public readonly MqttField<int>   Rotation  = new("Rotation");
    public readonly MqttField<float> Speed     = new("Speed");
    public readonly MqttField<bool>  IsRunning = new("IsRunning");
}
```

### 2. 受信のセットアップ

`MqttSubscriberBridge` コンポーネントをアタッチし、インスペクターでブローカー IP と購読したいトピックのリスト（`Topics`）を設定します。

トピック文字列（例: `"plc/device1"`）は、定義クラスの `TopicPath` と一致させてください。

### 3. データ取得

`store.GetTopic<T>()` で型安全なトピックインスタンスを取得し、フィールドにアクセスします。

```csharp
var store = MqttSubscriberBridge.Instance.Store;
var topic = store.GetTopic<PlcDevice1Topic>();

// 値のみを取得
int rot = topic.Rotation.Value;

// TryGet パターン
if (topic.Rotation.TryGetValue(out var rotation))
{
    Debug.Log($"Rotation: {rotation}");
}

// タイムスタンプ付き（デジタルツイン・死活監視用）
var entry = topic.Speed.Entry;
if (entry != null)
{
    Debug.Log($"Speed: {entry.Value}");
    Debug.Log($"ソース時刻: {entry.SourceTimestampUtc}");
    Debug.Log($"Unity受信時刻: {entry.ReceivedUtc}");
    Debug.Log($"データの鮮度 (秒): {entry.Age.TotalSeconds}");
}
```

### 4. データ送信（DataPublish）

Subscribe 側と同じ形式（`MqttDataEnvelope`）でデータを送信します。

```csharp
var manager = MqttSubscriberBridge.Instance.Manager;

// 単一値の送信
await manager.PublishDataAsync("unity/data", "Status", "Running");

// 特定のトピックの内容をそのままミラー送信
var topic = store.GetTopic<PlcDevice1Topic>();
await manager.PublishDataAsync(topic.Repository, targetTopic: "cloud/mirror");
```

### 5. PLC コマンド送信

```csharp
await manager.PublishRunAsync("motor/cmd", frequency: 60, direction: 0);
await manager.PublishStopAsync("motor/cmd");
```

## ライセンス

MIT License
