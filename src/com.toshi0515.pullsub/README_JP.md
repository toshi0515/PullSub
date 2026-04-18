# PullSub
![Unity 2022.3+](https://img.shields.io/badge/Unity-2022.3%2B-black)
![.NET Standard 2.1](https://img.shields.io/badge/.NET-Standard%202.1-blue)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Test](https://github.com/toshi0515/PullSub/actions/workflows/test.yml/badge.svg)](https://github.com/toshi0515/PullSub/actions/workflows/test.yml)
[![NuGet PullSub.Core](https://img.shields.io/nuget/v/PullSub.Core?label=PullSub.Core)](https://www.nuget.org/packages/PullSub.Core/)
[![NuGet PullSub.Mqtt](https://img.shields.io/nuget/v/PullSub.Mqtt?label=PullSub.Mqtt)](https://www.nuget.org/packages/PullSub.Mqtt/)

Unityと.NETのためのPull型・型安全Pub/Subライブラリ — MQTT組み込み、かつトランスポート非依存設計

- リアルタイムメッセージング向け: 常に最新のデータを効率的に取得 — デジタルツイン、ロボティクス、スマートホームなどに最適
- トランスポートやシリアライズ処理が別スレッドで実行される一方、Unityメインスレッドから簡単かつ型安全にアクセス可能
- カスタマイズ可能なコーデックとシリアライザ — ペイロード構造を特定のフォーマット(OPC UAなど)に合わせてカスタマイズでき、シリアライザ(例: MessagePack)を差し替えることでGC最適化が可能
- トランスポート非依存設計 — `ITransport`インターフェースを実装することで、MQTTをUDPや任意のカスタムプロトコルに置換可能
- UnityとRaspberry Piなどのエッジデバイス間でコードを共有 — CoreレイヤーはUnityへの依存なし


[English](README.md) | 日本語

---

## PullSubが提供するもの

例えば、MQTT経由のJSONメッセージからロボットのリアルタイム位置をUnity上で可視化する場合、更新のたびにスレッドセーフの管理とデシリアライズを手動で行うのは反復的でエラーを起こしやすい処理です。PullSubはこの手動制御によるオーバーヘッドをクリーンで型安全なAPIに置き換えます。

### 生のMQTTnet — スレッド安全性、デコーディング、ライフサイクル管理が自己責任

```csharp
client.ApplicationMessageReceivedAsync += e =>
{
    var payload = e.ApplicationMessage.Payload;
    var position = JsonSerializer.Deserialize<Position>(payload);
    lock (_gate) { _position = position; }
};
```

### PullSub — 型とトピックを一度定義し、あとは引っ張る(値を取得する)だけ

```csharp
// 型を定義
public sealed class Position
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

// トピックを定義
public static class Topics
{
    public static readonly ITopic<Position> Position
        = PullSubTopic.Create<Position>("robot/position");
}
```

#### エッジデバイス (.NET / Raspberry Pi) — センサーデータのPublish
```csharp
// 1行でシリアライズしてパブリッシュ — 手動でのJSON作成は不要
await runtime.PublishDataAsync(Topics.Position, new Position
{
    X = sensor.ReadX(),
    Y = sensor.ReadY(),
    Z = sensor.ReadZ()
});
```

#### Unity — サブスクライブと可視化
```csharp
public class RobotController : MonoBehaviour
{
    [SerializeField] private PullSubMqttClient _client;

    private DataSubscription<Position> _sub;

    private async void Start()
    {
        _sub = await _client.Runtime.SubscribeDataAsync(Topics.Position);
        _sub?.AddTo(this); // OnDestroyで自動的に破棄
    }

    private void Update()
    {
        // 最新の値にアクセス
        if (_sub?.TryGet(out var pos) == true)
        {
            transform.position = new Vector3(pos.X, pos.Y, pos.Z);
        }
    }
}
```

### ペイロードフォーマット

PullSubはJSONのシリアライズおよびデシリアライズを自動的に処理します。
C#の型とトピックを一度定義すれば、送信時のエンコードと受信時のデコードは内部で処理されます。

#### デフォルト (JSONエンベロープ)

```json
{
  "timestamp": "2026-04-13T12:00:00.000Z",
  "data": {
    "x": 1.0,
    "y": 2.0,
    "z": 3.0
  }
}
```

#### フラットJSON

```json
{
  "timestamp": "2026-04-13T12:00:00.000Z",
  "x": 1.0,
  "y": 2.0,
  "z": 3.0
}
```

`timestamp`フィールドはパブリッシュ時に自動的に書き込まれ、受信時に自動的に読み込まれます。この値は`DataSubscription<T>.TimestampUtc`経由でアクセスできます。

既存のIoTデバイスや他のMQTTクライアントなど、外部システムと連携するときなど必要な場合はフラットJSON形式を使用してください。

```csharp
// JSONエンベロープ (デフォルト)
PullSubTopic.Create<Position>("robot/position");

// フラットJSON
PullSubTopic.CreateFlat<Position>("robot/position");
```

> **Note:** `timestamp`というプロパティ名はフラットJSON形式で予約されています。C#のクラス内(DTO)でプロパティ名として使用しないでください。

#### カスタムコーデック

PullSubのペイロードフォーマットは`IPayloadCodec<T>`を通じて完全に置き換え可能です。
System.Text.JsonをMessagePackやMemoryPack、任意のバイナリフォーマットに差し替えることで、パフォーマンス要件を満たしたり既存システムと統合することができます。

```csharp
// カスタムコーデック (例: MemoryPack)
PullSubTopic.Create<Position>("robot/position",
    new MemoryPackPayloadCodec<Position>());
```

組み込みのJSONコーデックはエンベロープ構造（`timestamp` + `data`）を定義しています。カスタムコーデックでは任意のワイヤーフォーマットを利用でき、構造は完全に自由です。

**なぜPullSubなのか？** 
- コールバック駆動のMQTTライブラリでは、スレッド処理・デコード・ライフサイクル管理を全て自前で行う必要があるためです。PullSubはそれらを全て内部で処理し、型を定義して必要な時に最新の値をプルするだけの状態にします。

**なぜPushではなくPullなのか？** 
- Unityのゲームループは毎フレーム状態をポーリングします（transform.position, Input.GetAxis, Physicsなど）。PullSubはこのモデルに自然にフィットします。コールバックも、手動でキューを空にする必要も、ロックの管理もありません。

**PullSubが適さないケース** 
- ワイルドカードトピック: PullSubはワイルドカード（+ や #）によるサブスクライブをサポートしていません。必要な場合はMQTTnetを直接使用してください。
- MQTT v5機能: PullSubはMQTT v3.1.1をベースとしています。MQTT v5機能が必要な場合はMQTTnetを直接使用してください。

---

## 2つの異なる課題に向けた2つのAPI

**Data APIは最新の値を保持し、Queue APIはすべてのメッセージを保持する**

### Data API — *状態(State)* のため

IoTセンサーの値、デジタルツインのステータス、ロボットの姿勢など、「最新の値」のみが重要となるデータに対して使用します。

```csharp
// Update()でポーリング
    private void Update()
    {
        // 最新の値にアクセス
        if (_positionSubscription?.TryGet(out var pos) == true)
        {
            transform.position = new Vector3(pos.X, pos.Y, pos.Z);
        }
    }
```

### Queue API — *イベント* のため

同期または非同期のハンドラーを登録し、入ってくるメッセージを順序通りにすべて処理します。このイベント駆動APIは、コマンド、ログ、トランザクションなどに最適で、データを取りこぼすことなく順次実行することを保証します。(Queueの深さやハンドラーによっては取りこぼす可能性があります)

```csharp
// コマンドを1つずつ処理
var queueSubscription = await runtime.SubscribeQueueAsync(
    Topics.Command,
    async (command, ct) => await robot.ExecuteAsync(command, ct));
```

---

## はじめに — Unity

### UniTaskのインストール

PullSubは拡張パッケージとして[UniTask](https://github.com/Cysharp/UniTask) (2.x) を必要とします。

### DLLのインストール

[NuGet for Unity](https://github.com/GlitchEnzo/NuGetForUnity) を使用したインストールを推奨します。あるいは手動で `Assets/Plugins` フォルダに配置してください。

**Core**
- `System.Text.Json.dll` (8.0.x)
- `System.Text.Encodings.Web.dll` (8.0.x)
- `Microsoft.Bcl.AsyncInterfaces.dll` (8.0.x)

**Mqtt**
- `MQTTnet.dll` (4.3.x)

### PullSubのインストール

git URLを使用してUPM経由でインストールします：
```
https://github.com/toshi0515/PullSub.git?path=src/com.toshi0515.pullsub
```

## はじめに — .NET (NuGet)

**Requirements:** .NET Standard 2.1 compatible environment (.NET 6.0+ recommended)

Coreパッケージのインストール ([NuGet](https://www.nuget.org/packages/PullSub.Core/)):

```
dotnet add package PullSub.Core
```

組み込みのMQTTトランスポートを使用する場合は以下もインストールします ([NuGet](https://www.nuget.org/packages/PullSub.Mqtt/)):

```
dotnet add package PullSub.Mqtt
```

**Note:** `PullSub.Core`は`netstandard2.1`をターゲットにしており、.NET 6以降と互換性があります。`PullSub.Mqtt`はMQTTnet 4.3.xに依存しており、自動的にインストールされます。

---

## クイックスタート — Unity

### 1. コンポーネントの追加

GameObjectに `PullSubMqttClient` をアタッチし、Inspectorでブローカーの接続設定を行います。

### 2. トピックと型の定義

データ型とトピックを同じファイル等に定義します。

```csharp
// Types.cs
public sealed class Position
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

// Topics.cs
public static class Topics
{
    public static readonly ITopic<Position> Position
        = PullSubTopic.Create<Position>("robot/position");
}
```

### 3. サブスクライブとデータアクセス

```csharp
using UnityEngine;
using PullSub.Core;
using PullSub.Bridge;

public class RobotController : MonoBehaviour
{
    [SerializeField] private PullSubMqttClient _client;

    private SubscriptionGroup _group; // 複数のサブスクリプションを管理
    private DataSubscription<Position> _positionSubscription;

    private async void Start()
    {
        // グループを作成
        _group = _client.Runtime.CreateGroup()
            .AddTo(this);  // OnDestroyで自動クリーンアップ

        // データをサブスクライブ: DataSubscription<T> を返す
        _positionSubscription = await _group.SubscribeDataAsync(Topics.Position);
    }

    private void Update()
    {
        if (_positionSubscription?.TryGet(out var pos) != true) return;
        transform.position = new Vector3(pos.X, pos.Y, pos.Z);
    }
}
```

**Subscription Group:**
- `SubscriptionGroup`は一括クリーンアップのために複数のサブスクリプションを束ねます
- グループ内でのトピック重複防止機能を内蔵
- `AddTo(this)`による自動ライフサイクル・バインディング
- 同時サブスクリプション要求に対して安全

### 4. Queue API を使用したハンドラーの登録

```csharp
public class CommandReceiver : MonoBehaviour
{
    [SerializeField] private PullSubMqttClient _client;
    private SubscriptionGroup _group;

    private async void Start()
    {
        _group = _client.Runtime.CreateGroup()
            .AddTo(this);

        // Queueハンドラーの登録 (管理および自動解除対象)
        await _group.SubscribeQueueAsync(
            Topics.Command,
            async (command, ct) =>
            {
                await robot.ExecuteAsync(command, ct);
            })
            .ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully)
                    Debug.LogError($"Handler registration failed: {t.Exception}");
            });

        // Unityメインスレッド用拡張メソッドの利用も可能
        await _group.SubscribeQueueOnMainThreadAsync(
            Topics.Command,
            (command) =>
            {
                robot.Execute(command);
            });
    }
}
```

### 5. データPublish

```csharp
var position = new Position
{
    X = transform.position.x,
    Y = transform.position.y,
    Z = transform.position.z
};

await _client.Runtime.PublishDataAsync(Topics.Position, position);
```

### 6. IObservable<T> からPublish (オプション)

`ToPublisher()`を使用して、任意の `IObservable<T>` ストリームをパブリッシュに接続できます。

```csharp
sensorStream
    .ThrottleFirst(TimeSpan.FromMilliseconds(100))
    .Subscribe(_client.Runtime.ToPublisher(
    Topics.Position,
    onError: ex => Debug.LogError($"Publish failed: {ex.Message}")));
```

### 7. Request-Reply (experimental)

1つのリクエストに対して1つのレスポンスが必要な場面（コマンドの実行確認など）で使用します。

```csharp
public sealed class AddRequest
{
    public int A { get; set; }
    public int B { get; set; }
}

public static class RequestTopics
{
    public static readonly IRequestTopic<AddRequest, int> Add
        = RequestTopic.Create<AddRequest, int>("math/add/request");
}

// リクエスター
var sum = await _client.Runtime.RequestAsync(
    RequestTopics.Add,
    new AddRequest { A = 20, B = 22 },
    timeout: TimeSpan.FromSeconds(1));

// レスポンダー
await _client.Runtime.RespondAsync(
    RequestTopics.Add,
    async (req, ct) =>
    {
        await Task.Yield();
        return req.A + req.B;
    });
```

### 8. ランタイムモニタリング (Editor)

`Tools > PullSub > Debug Monitor` から接続状態や各トピックの受信レートなどを監視できます。

---

## クイックスタート — .NET

`PullSubRuntime` を直接インスタンス化して使用します。

```csharp
using PullSub.Core;
using PullSub.Mqtt;
using System.Threading;
using System.Threading.Tasks;

var connectionOptions = new MqttConnectionOptions();

var transport = new MqttTransport(
    brokerHost: "127.0.0.1",
    brokerPort: 1883,
    connectionOptions: connectionOptions,
    clientIdPolicy: MqttClientIdPolicy.RandomPerStart);

await using var runtime = new PullSubRuntime(transport);
using var cts = new CancellationTokenSource();

await runtime.StartAsync(cts.Token);
await runtime.WaitUntilConnectedAsync(cts.Token);

// グループ作成とサブスクライブ
await using var group = runtime.CreateGroup();
var subscription = await group.SubscribeDataAsync(
    Topics.Position,
    cancellationToken: cts.Token);

// メインループ
while (!cts.IsCancellationRequested)
{
    if (subscription.TryGet(out var pos))
    {
        var command = controller.Calculate(pos);
        await robot.SendCommandAsync(command, cts.Token);
    }
    await Task.Delay(10, cts.Token);
}
```

---

## トピック定義 — ベストプラクティス

**Source Generation の使用**: IL2CPPや性能要求が高い場合は `System.Text.Json` Source Generator を使用し、Code Strippingを防いでください。

```csharp
[JsonSerializable(typeof(RobotCommand))]
public partial class AppJsonContext : JsonSerializerContext { }

public static class Topics
{
    public static readonly ITopic<RobotCommand> RobotCommand
        = PullSubTopic.Create("robot/command", AppJsonContext.Default.RobotCommand);
}
```

**パブリッシャーとサブスクライバー間での共有**: これらの型/トピック定義ファイルを含めた共通アセンブリまたはコードファイルを用意し、不一致を防ぎます。

---

## Serialization / シリアライズ

デフォルトではJSONエンベロープを使用しますが、`IPayloadCodec<T>` で任意に入れ替えが可能です。

欠損しているプロパティデータを受信した場合、そのプロパティは型デフォルト値（`0`, `false`, `null`）となります。Data APIでは欠損値と以前のキャッシュ状態のマージは行われません。

---

## API Reference / API リファレンス

(詳細なAPIリファレンスについては、[英語版のREADME](README.md)をご参照ください)

---

## サードパーティーライセンス

PullSubは以下のOSSを利用しています。

- [MQTTnet](https://github.com/dotnet/MQTTnet) - MIT License
- [UniTask](https://github.com/Cysharp/UniTask) - MIT License
- [System.Text.Json](https://github.com/dotnet/runtime) - MIT License

## License

[MIT License](LICENSE)
