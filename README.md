# Unity MQTT Client

Unity Package Manager 対応の最小限 MQTT 送受信パッケージ。  
[MQTTnet](https://github.com/dotnet/MQTTnet) v3.1.2 を使用し、Unity メインスレッドへの自動ディスパッチに対応。

## 機能

- **接続管理**: `MqttClientManager`（public コンストラクタ、シングルトン廃止）による自動再接続付き MQTT クライアント
- **受信（Subscribe）**: トピック購読 → JSON デシリアライズ → `MqttDataRepository` へ格納
- **送信（Publish）**: 拡張メソッド（`MqttPublishExtensions` / `MqttSubscribeExtensions` / `MqttPlcCommandPublishExtensions`）による汎用 JSON 送信 / PLC コマンド送信（RUN, STOP, SET_FREQ, SET_DIR）
- **スレッド安全**: 受信メッセージは自動的に Unity メインスレッドにディスパッチ
- **MonoBehaviour エントリポイント**: `MqttSubscriberBridge` がシングルトンとして `MqttClientManager` インスタンスを保持

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

> **Note**: Private リポジトリの場合、Personal Access Token 付き URL を使用:
> ```
> https://YOUR_PAT@github.com/Toshi-0515/unity-mqtt.git
> ```
> または SSH:
> ```
> git@github.com:Toshi-0515/unity-mqtt.git
> ```

## 使い方

### 1. 受信のセットアップ

`MqttSubscriberBridge` コンポーネントを任意の GameObject にアタッチし、Inspector でブローカー IP・ポート・トピックを設定するだけで動作します。

受信データは `MqttDataRepository.Instance` から取得:

```csharp
if (MqttDataRepository.Instance.TryGet<int>("Rotation", out var rotation))
{
    Debug.Log($"Rotation: {rotation}");
}
```

### 2. コマンド送信

シングルトンを廃止したため、まずは `MqttClientManager` のインスタンスを取得します。
`MqttSubscriberBridge` 経由でも、あるいは自分で生成して構いません。

非同期操作には `CancellationToken` を渡せるようになりました。通常は不要ですが、
たとえばボタン操作に紐付けて途中キャンセルしたい場合に使用します。

```csharp
var cts = new CancellationTokenSource();

// ブリッジ経由で取得する例
var manager = MqttSubscriberBridge.Instance.Manager;

await manager.PublishRunAsync("ARMotorSystem/cmd", frequency: 30, direction: 0, ct: cts.Token);

// キャンセル
cts.Cancel();

await manager.PublishStopAsync("ARMotorSystem/cmd");
await manager.PublishAsync("my/topic", new { message = "hello" });
```

```csharp
// 自前で生成する場合
var manager = new MqttClientManager("127.0.0.1", 1883);
await manager.StartAsync();
await manager.PublishAsync("foo", new { bar = 123 });
```

ライフタイムに沿った初期化処理にもトークンが渡せます。非アクティブ化時に自動キャンセルされます。

### 3. データ送信（DataPublish）

Subscribe 側と同じ `MqttDataEnvelope` 形式で Publish するための拡張メソッドです。

```csharp
var manager = MqttSubscriberBridge.Instance.Manager;

// 単一値の送信
await manager.PublishDataAsync("unity/data", "Rotation", 1500);

// 複数値を Dictionary で送信
await manager.PublishDataAsync("unity/data", new Dictionary<string, object>
{
    ["Rotation"] = 1500,
    ["MotorRunning"] = true,
});

// Repository のスナップショットをそのまま転送（ミラー・中継用途）
var snapshot = MqttDataRepository.Instance.GetAllDataSnapshot();
await manager.PublishDataAsync("plc/mirror", snapshot);
```

## 受信 JSON フォーマット

```json
{
  "timestamp": "2026-02-28T05:00:00Z",
  "items": [
    { "name": "Rotation",        "type": "WORD", "value": 1000 },
    { "name": "ElectricCurrent", "type": "WORD", "value": 500  },
    { "name": "MotorRunning",    "type": "BIT",  "value": true }
  ]
}
```

型ヒント (`type`) に基づく自動変換: `BIT` → `bool`, `WORD`/`DINT` → `int`, `REAL` → `float`

## 同梱ライブラリ

| ライブラリ | バージョン | ライセンス |
|---|---|---|
| MQTTnet | 3.1.2 | MIT |
| MQTTnet.Extensions.ManagedClient | 3.1.2 | MIT |

## ライセンス

MIT License
