# Unity MQTT Client

Unity Package Manager 対応の最小限 MQTT 送受信パッケージ。  
[MQTTnet](https://github.com/dotnet/MQTTnet) v3.1.2 を使用し、Unity メインスレッドへの自動ディスパッチに対応。

## 機能

- **接続管理**: シングルトン `MqttClientManager` による自動再接続付き MQTT クライアント
- **受信（Subscribe）**: トピック購読 → JSON デシリアライズ → `MqttDataRepository` へ格納
- **送信（Publish）**: 汎用 JSON 送信 / PLC コマンド送信（RUN, STOP, SET_FREQ, SET_DIR）
- **スレッド安全**: 受信メッセージは自動的に Unity メインスレッドにディスパッチ

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

```csharp
// モーター起動
await MqttCommandPublisher.PublishRun("ARMotorSystem/cmd", frequency: 30, direction: 0);

// モーター停止
await MqttCommandPublisher.PublishStop("ARMotorSystem/cmd");

// 汎用 JSON 送信
await MqttCommandPublisher.PublishAsync("my/topic", new { message = "hello" });
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
