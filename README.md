# unity-mqtt

Unity と .NET 向けの MQTT クライアントライブラリです。
[MQTTnet v4](https://github.com/dotnet/MQTTnet) をベースに、カスタマイズ可能なペイロード形式での送受信と Pull 型データアクセスを提供します。

![Unity 2022.3 LTS+](https://img.shields.io/badge/Unity-2022.3%20LTS%2B-black) ![.NET Standard 2.1](https://img.shields.io/badge/.NET-Standard%202.1-blue) [![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

> **要件**
> - System.Text.Json 8.0.6（`netstandard2.0`）を同梱し、Unity の `.NET Standard 2.1` 互換で動作するため、対象 Unity は **2022.3 LTS 以降** です。
> - Data API は型付きです: `SubscribeDataAsync<T>()` / `TryGetData<T>()` / `WaitForFirstDataAsync<T>()` / `PublishDataAsync<T>()`
> - 同一 topic の再購読時、Codec は `Equals` で同値である必要があります。

---

## 特徴

- **リアルタイムセンサー値取得**: IoT センサーのような「最新値」を効率的に取得できる **Data 系 API** — スマートホーム、ロボット制御、ITS などの用途に最適
- **メッセージストリーミング**: すべてのメッセージを逐次処理する **Raw 系 API** — ログ記録、リアルタイム分析、イベントトレーシング用途に最適
- ManagedClient による自動再接続・再購読
- `CancellationToken` による待機キャンセル
- 完全一致トピックのサブスクリプション管理

---

## このライブラリを選ぶ理由

### Pull 型データアクセス

MQTT の多くのクライアントはコールバック・イベント駆動ですが、センサーの最新値を `Update()` 内で参照するにはスレッド間の安全な橋渡しが必要です。unity-mqtt は **Pull 型 API** を採用し、`TryGetData<T>()` を単純に呼び出すだけで型安全な最新値が取得できます。受信スレッドとメインスレッドの調整コードが不要です。

### Unity 依存なし、どこでも動作

`MqttV2ClientRuntime` は **純粋な .NET 実装** で、Unity に依存しません。Raspberry Pi などの Linux エッジデバイスでも同じコードが動作するため、送信側と受信側で同じプロジェクト・同じ型定義を使い回せます。

### ペイロードコーデックの型安全性

新しい `IMqttV2PayloadCodec<T>` は汎用となり、各トピックの型は明示的に指定できます。`MqttV2JsonPayloadCodec<T>` 、`MqttV2FlatJsonPayloadCodec<T>` が標準提供されています。独自形式が必要な場合は `MqttV2TextPayloadCodec<T>` を継承して 2 メソッドを実装。実行中の Codec 差し替えはできませんが、トピックごとに異なる Codec 型を使用できます。

受信時の GC 最適化として、**Data 系のみ購読しているトピックでは payload の追加クローンを行わず** decode します。**Raw 系を購読しているトピックではキュー保持のため payload をクローン**します（Raw + Data 同時購読時は同一クローンを再利用し、コピー回数は 1 回です）。

---

## セットアップ

Unity Package Manager でリポジトリの Git URL を追加します。

```
https://github.com/Toshi-0515/unity-mqtt.git
```

---

## クイックスタート — Unity（Bridge 経由）

Inspector で設定する最短パターンです。

### 1. シーン配置

1. 任意の GameObject に `MqttV2Bridge` コンポーネントを追加します。
2. Inspector で **Broker Host / Port** を設定します。
3. **Connection > Transport** でプロトコルを選択（既定: TCP。WebSocket・Secure WebSocket も対応）。
4. Raw メッセージをハンドラーで処理する場合は、スクリプトから `RegisterRawHandlerLeaseAsync()` で登録します。

### 2. スクリプトからデータを取得する（型付きデータ購読）

位置情報を専用クラスとして定義し、型安全に取得します。

```csharp
using System.Threading;
using UnityEngine;
using UnityMqtt.V2.Bridge;
using UnityMqtt.V2.Core;

[System.Serializable]
public class Position
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

public class SphereController : MonoBehaviour
{
    [SerializeField] private MqttV2Bridge _bridge;

    private CancellationTokenSource _cts;

    private async void Start()
    {
        _cts = new CancellationTokenSource();

        // ブローカーと通信できる状態になるまで待機
        await _bridge.Runtime.StartAsync(_cts.Token);
        await _bridge.Runtime.WaitUntilConnectedAsync(_cts.Token);

        // ロボット位置情報を購読開始（型付き）。Codec を明示的に指定
        var codec = MqttV2JsonPayloadCodec<Position>.Default;
        await _bridge.Runtime.SubscribeDataAsync<Position>("robot/position", codec, _cts.Token);

        // 最初のデータが到着するまで待機（ネットワーク遅延を吸収）
        await _bridge.Runtime.WaitForFirstDataAsync<Position>("robot/position", _cts.Token);
        ApplyPosition();
    }

    private void Update()
    {
        ApplyPosition();
    }

    private void ApplyPosition()
    {
        if (_bridge.Runtime.TryGetData<Position>("robot/position", out var position))
        {
            transform.position = new Vector3(position.X, position.Y, position.Z);
        }
    }

    private void OnDestroy()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
```

### 3. データを送信する（型付き）

```csharp
var position = new Position
{
    X = transform.position.x,
    Y = transform.position.y,
    Z = transform.position.z
};

var codec = MqttV2JsonPayloadCodec<Position>.Default;
await _bridge.Runtime.PublishDataAsync<Position>("robot/position", position, codec, _cts.Token);
```

### 4. Raw メッセージをハンドラーで処理する（Bridge）

`MqttV2Bridge.RegisterRawHandlerLeaseAsync()` を使うと、`ReceiveRawAsync()` の明示ループだけでなく
`CancellationTokenSource` と完了待機 `Task` の管理も不要になります。

```csharp
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityMqtt.V2.Bridge;
using UnityMqtt.V2.Core;

public sealed class RawHandlerExample : MonoBehaviour
{
    [SerializeField] private MqttV2Bridge _bridge;

    private MqttV2RawHandlerRegistration _registration;

    private const string TopicName = "test/hello";

    private async void OnEnable()
    {
        try
        {
            _registration = await _bridge.RegisterRawHandlerLeaseAsync(
                TopicName,
                new MqttV2RawSubscriptionOptions(256),
                message =>
                {
                    Debug.Log($"[{message.Topic}] {Encoding.UTF8.GetString(message.Payload)}");
                });
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
    }

    private async void OnDisable()
    {
        if (_registration == null)
            return;

        try
        {
            await _registration.DisposeAsync();
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
        finally
        {
            _registration = null;
        }
    }
}
```

契約:

- この API は Unity メインスレッドから呼び出してください。
- この API は `OnEnable` 後（Bridge が enabled の間）に呼び出してください。
- `RegisterRawHandlerLeaseAsync` の戻り値は `Task<MqttV2RawHandlerRegistration>` です。`await` して購読成功を確認してください。
- `MqttV2RawHandlerRegistration.Completion` を `await` すると handler loop の最終完了/失敗を観測できます。
- `MqttV2RawHandlerRegistration.DisposeAsync()` は cleanup と完了待機を行います。`OperationCanceledException` 以外の handler 例外はこのメソッドから再送出されます。
- 例外観測は `Completion` か `DisposeAsync()` のどちらか一方を主経路にしてください。両方を同時利用すると同じ例外を二重に観測する場合があります。
- `OnDisable` / `OnDestroy` で Bridge lifecycle token がキャンセルされると loop は停止します。
- 再 `OnEnable` 時は呼び出し側が再登録してください（自動再登録はしません）。
- 同一 topic の二重登録は `InvalidOperationException` になります。
- handler 処理が到着レートに追いつかない場合、`DropOldest` が発生します。必要なら `TryGetRawDroppedCount()` を監視してください。

---

## クイックスタート — Unity / .NET（Runtime 直接利用）

Bridge を使わずに `MqttV2ClientRuntime` を直接生成します。
Unity 以外の .NET 環境でも同じコードが動作します。

```csharp
using System;
using System.Threading;
using UnityMqtt.V2.Core;

[System.Serializable]
public class ImuData
{
    public double Roll { get; set; }
    public double Pitch { get; set; }
    public double Yaw { get; set; }
}

var profile = new MqttV2ClientProfile(
    brokerHost: "127.0.0.1",
    brokerPort: 1883,
    clientIdPolicy: MqttV2ClientIdPolicy.RandomPerStart);

var imuCodec = MqttV2JsonPayloadCodec<ImuData>.Default;

await using var runtime = new MqttV2ClientRuntime(profile);

using var cts = new CancellationTokenSource();

// ブローカーと通信できるまで起動して接続待機
await runtime.StartAsync(cts.Token);
await runtime.WaitUntilConnectedAsync(cts.Token);

// --- Data 系 API: 最新センサー値の取得 ---
// IMU センサーデータを型付きで購読（最新値が自動更新される）
await runtime.SubscribeDataAsync<ImuData>("sensor/imu", imuCodec, cts.Token);

// データを送信（型付き）
var imuData = new ImuData { Roll = 0.1, Pitch = 0.2, Yaw = 0.3 };
await runtime.PublishDataAsync<ImuData>("sensor/imu", imuData, imuCodec, cts.Token);

// 最新値を取得（データが既に到着していれば即座に返却）
if (runtime.TryGetData<ImuData>("sensor/imu", out var latest))
    Console.WriteLine($"roll = {latest.Roll}");

// --- Raw 系 API: すべてのメッセージを逐次処理 ---
// ログ記録など、到着した全メッセージが必要な場合
await runtime.SubscribeRawAsync(
    "raw/logs",
    new MqttV2RawSubscriptionOptions(maxQueueDepth: 256),
    cts.Token);

await runtime.PublishRawAsync("raw/logs", System.Text.Encoding.UTF8.GetBytes("[INFO] System started"), cancellationToken: cts.Token);

// 次のメッセージが到着するまで待機
var raw = await runtime.ReceiveRawAsync("raw/logs", cts.Token);

Console.WriteLine(System.Text.Encoding.UTF8.GetString(raw.Payload));

// --- Raw lease API（.NET / Runtime 直接利用） ---
var registration = await runtime.RegisterRawHandlerLeaseAsync(
    "command/start",
    new MqttV2RawSubscriptionOptions(32),
    async (message, ct) =>
    {
        Console.WriteLine($"[{message.Topic}] {System.Text.Encoding.UTF8.GetString(message.Payload)}");
        await Task.Yield();
    },
    cts.Token);

// ...

await registration.DisposeAsync();
```

---

## Data payload 形式

型付き Data API では、以下の JSON 構造が自動的に生成されます。ユーザーは型定義を用意するだけで OK です。

### JSON Envelope 形式（MqttV2JsonPayloadCodec<T>）

```json
{
  "timestamp": "2026-03-14T12:34:56.789Z",
  "data": {
    "roll": 0.1,
    "pitch": 0.2,
    "yaw": 0.3
  }
}
```

型 `ImuData` を定義し、Codec で自動エンコード/デコードされます。

### Codec の型安全性と再登録

最新の API では `IMqttV2PayloadCodec<T>` は汎用となり、型安全なデータアクセスを提供します。

- 各トピックは 1 つの型 `T` にロックされます（型変更は例外）
- 同一トピックへの再購読時、Codec は値同値 (`Equals`）である必要があります
  - 例: `MqttV2JsonPayloadCodec<ImuData>` 2 つが異なるインスタンスでも、内部設定（`JsonSerializerOptions`）が同参照なら同値と判定
  - 設定の異なる新しい Codec 逆登録 → `InvalidOperationException`
- **推奨**: Codec は `Default` シングルトン を使用するか、`OnEnable` で毎回同じ設定で `.new` してください

| Codec | 型パラメータ | 向いている場面 |
|---|---|---|
| `MqttV2JsonPayloadCodec<T>` | 必須 | ライブラリ型安全通信、struct/class で構造化 |
| `MqttV2FlatJsonPayloadCodec<T>` | 必須 | ESP32・既存デバイス連携（タイムスタンプをフラット JSON に埋め込み） |

### Codec の切り替え

同一アプリで複数 Codec が必要な場合、Runtime インスタンスは 1 つのまま topic ごとに Codec を指定できます。
同一 topic は最初の `SubscribeDataAsync<T>()` で型と Codec が固定され、購読中に別 Codec へ差し替えることはできません。
変更する場合は `UnsubscribeDataAsync(topic)` で解除後に、希望する Codec で再度 `SubscribeDataAsync<T>()` を呼び出してください。

#### JSON Envelope (既定)

### カスタム Codec の実装

独自フォーマットが必要な場合は `MqttV2TextPayloadCodec<T>` を継承します。UTF-8 変換・null ガード・エラーハンドリング・timestamp 正規化はライブラリ側で処理されるため、実装が必要なのは **2 メソッドのみ**です。

`MqttV2TextPayloadCodec<T>` を使わずに `IMqttV2PayloadCodec<T>` を直接実装する場合、`TryDecode` の入力は `byte[]` ではなく `ReadOnlySpan<byte>` です。

#### 例: フラット CSV 形式 Codec<T>

```csharp
using System;
using System.Text;
using UnityMqtt.V2.Core;

[System.Serializable]
public class SensorData
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

public sealed class FlatCsvCodec : MqttV2TextPayloadCodec<SensorData>
{
    // 送信: "2026-03-14T12:34:56.789Z,1.23,4.56,7.89" の形式に変換
    protected override string FormatPayload(DateTime timestampUtc, SensorData value)
    {
        return $"{timestampUtc:O},{value.X},{value.Y},{value.Z}";
    }

    // 受信: "2026-03-14T12:34:56.789Z,1.23,4.56,7.89" を SensorData に変換
    protected override bool TryParsePayload(
        string text,
        out SensorData value,
        out DateTime timestampUtc,
        out string error)
    {
        value = null;
        timestampUtc = default;
        error = null;

        try
        {
            var parts = text.Split(',');
            if (parts.Length != 4)
            {
                error = "CSV format requires 4 fields (timestamp,x,y,z).";
                return false;
            }

            timestampUtc = ParseTimestampOrNow(parts[0]);

            value = new SensorData
            {
                X = double.Parse(parts[1]),
                Y = double.Parse(parts[2]),
                Z = double.Parse(parts[3])
            };
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
```

使い方は標準 Codec と同じです。

```csharp
var codec = new FlatCsvCodec();
var profile = new MqttV2ClientProfile(
    brokerHost: "127.0.0.1",
    brokerPort: 1883,
    clientIdPolicy: MqttV2ClientIdPolicy.RandomPerStart);

await using var runtime = new MqttV2ClientRuntime(profile);

// Publish: "2026-03-14T12:34:56.789Z,1.23,4.56,7.89" で送信
var data = new SensorData { X = 1.23, Y = 4.56, Z = 7.89 };
await runtime.PublishDataAsync<SensorData>("sensor/data", data, codec, cts.Token);

// 受信: 型安全に SensorData として取得
if (runtime.TryGetData<SensorData>("sensor/data", out var latest))
    Console.WriteLine($"X={latest.X}, Y={latest.Y}, Z={latest.Z}");
```

#### 例: JSON 形式カスタマイズ Codec<T>

既存デバイスが異なる JSON 形式を使う場合、`FormatPayload`/`TryParsePayload` をカスタマイズします。

```csharp
using System;
using System.Text.Json;
using UnityMqtt.V2.Core;

public sealed class CustomJsonCodec<T> : MqttV2TextPayloadCodec<T>
{
    private readonly JsonSerializerOptions _options;

    public CustomJsonCodec(JsonSerializerOptions options = null)
    {
        _options = options ?? new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    }

    protected override string FormatPayload(DateTime timestampUtc, T value)
    {
        var json = JsonSerializer.Serialize(value, _options);
        return json;  // JSON のみ（timestamp はカスタム形式では非包含）
    }

    protected override bool TryParsePayload(
        string text,
        out T value,
        out DateTime timestampUtc,
        out string error)
    {
        value = default;
        timestampUtc = ParseTimestampOrNow(null);  // 既存デバイスが t imestamp を送信しない場合
        error = null;

        try
        {
            value = JsonSerializer.Deserialize<T>(text, _options);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
```

---

## Transport

接続プロトコルを TCP / WebSocket / Secure WebSocket から選択できます。既定は TCP です。

| Transport | URI 形式 | 典型用途 |
|---|---|---|
| **Tcp**（既定） | `mqtt://host:1883` | 標準的な接続（組み込み・エッジデバイス） |
| **Ws** | `ws://host:port/path` | WebSocket ブローカー（HiveMQ、EMQX、Mosquitto WebSocket）、ブラウザ/Web 環境と共有 |
| **Wss** | `wss://host:port/path` | Secure WebSocket。TLS オプションを併用して証明書検証を制御 |

### Bridge で設定する

`MqttV2Bridge` Inspector の **Connection > Transport** セクションで設定します。

| 項目 | 説明 |
|---|---|
| **Kind** | `Tcp` / `Ws` / `Wss` から選択。既定は `Tcp` |
| **Web Socket Path** | WebSocket のパス（Ws/Wss のみ）。空白時は `/` を使用。ブローカーに合わせて設定（例: `/mqtt`） |
| **Web Socket Subprotocol** | WebSocket サブプロトコル（Ws/Wss のみ）。空白時は `mqtt` を使用 |

> **Ws と Wss の選択**:
> - 暗号化が不要な場合または WebSocket ブローカーが TLS をプロキシ側で終端する場合は `Ws` 。
> - クライアントからブローカーまで暗号化が必要な場合は `Wss` 。**TLS** セクションもあわせて有効化してください。

### Runtime 直接利用で設定する

```csharp
// WebSocket （非暗号化）
var profile = new MqttV2ClientProfile(
    brokerHost: "broker.example.com",
    brokerPort: 8083,
    clientIdPolicy: MqttV2ClientIdPolicy.RandomPerStart,
    connectionOptions: new MqttV2ConnectionOptions(
        transport: new MqttV2TransportOptions(
            kind: MqttV2TransportKind.Ws,
            webSocketPath: "/mqtt")));

await using var runtime = new MqttV2ClientRuntime(profile);

// WebSocket （TLS 暗号化 = wss://）
var tlsOptions = new MqttV2TlsOptions(enabled: true);

var profileWss = new MqttV2ClientProfile(
    brokerHost: "broker.example.com",
    brokerPort: 8084,
    clientIdPolicy: MqttV2ClientIdPolicy.RandomPerStart,
    connectionOptions: new MqttV2ConnectionOptions(
        tls: tlsOptions,
        transport: new MqttV2TransportOptions(
            kind: MqttV2TransportKind.Wss,
            webSocketPath: "/mqtt")));
```

> **Ws で TLS を有効にした場合**: `Transport=Ws` かつ `Tls.Enabled=true` の組み合わせは矛盾設定です。TLS 設定は無視され Runtime 起動時に警告が出力されます。暗号化が必要な場合は `Transport=Wss` を使用してください。

---

## TLS / SSL

ブローカーへの接続を暗号化する場合は TLS を有効にします。

### Bridge で設定する

`MqttV2Bridge` Inspector の **Connection > TLS** セクションで設定します。

#### 基本設定

| 項目 | 説明 |
|---|---|
| **Enabled** | TLS 接続を有効化します。既定は OFF |
| **Target Host (SNI)** | Server Name Indication (SNI) ホスト名。未指定の場合は BrokerHost を使用 |

#### TLS Advanced

| 項目 | 説明 | 用途 |
|---|---|---|
| **Ssl Protocols** | 使用する SSL プロトコルバージョン。既定は None（OS が最適選択） | 特定プロトコルの強制が必要な場合のみ |
| **Allow Untrusted Certificates** | 自署証明書を許可。**開発・検証のみ** | テスト環境での一時的な検証回避 |
| **Ignore Certificate Chain Errors** | 証明書チェーンエラーを無視。**開発・検証のみ** | テスト環境での検証回避 |
| **Ignore Certificate Revocation Errors** | 失効チェックスキップ。**開発・検証のみ** | テスト環境での検証回避 |

> **警告**: `AllowUntrustedCertificates`、`IgnoreCertificateChainErrors`、`IgnoreCertificateRevocationErrors` は **本番環境では使用しないでください**。MqttV2Bridge は自動的にこれらの設定をコンソール警告で通知します。

#### WebSocket (Wss) での TLS 適用制約

`Transport=Wss` では以下のオプションが有効です。

| オプション | Wss での扱い |
|---|---|
| `AllowUntrustedCertificates` | 適用されます |
| `IgnoreCertificateChainErrors` | 適用されます |
| `IgnoreCertificateRevocationErrors` | 適用されます |
| `TargetHost` | 適用されます（SNI） |
| `SslProtocols` | **適用されません**（理由: MQTTnet が WebSocket TLS ネゴシエーションを内部管理するため API レイヤーが異なる） |

`SslProtocols` に `None` 以外を設定した場合、Runtime 起動時に警告が出力されます（値は無視されます）。不確定な場合は `SslProtocols = None`（OS 選択）のままで使います。

> **Wss で証明書オプションを適用するには `Enabled = true` が必要です**:  
> `Transport=Wss` かつ `Tls.Enabled=false` の場合、`wss://` URI によって TLS 接続は行われますが  
> `AllowUntrustedCertificates` などの証明書オプションは **すべて無視されます**（OS デフォルトの厳格な証明書検証が適用されます）。  
> 開発環境で自己署名証明書を使用する場合は `Tls.Enabled=true` にした上で各オプションを設定してください。

### Runtime 直接利用で設定する

```csharp
var tlsOptions = new MqttV2TlsOptions(
    enabled: true,
    allowUntrustedCertificates: false,           // 本番では false
    ignoreCertificateChainErrors: false,         // 本番では false
    ignoreCertificateRevocationErrors: false,    // 本番では false
    targetHost: "mqtt.example.com",              // SNI ホスト名（オプション）
    sslProtocols: System.Security.Authentication.SslProtocols.None); // OS 選択

var profile = new MqttV2ClientProfile(
    brokerHost: "example.com",
    brokerPort: 8883,  // TLS ポート（既定 8883）
    clientIdPolicy: MqttV2ClientIdPolicy.RandomPerStart,
    connectionOptions: new MqttV2ConnectionOptions(tls: tlsOptions));

await using var runtime = new MqttV2ClientRuntime(profile);
```

---

## KeepAlive

KeepAlive は接続をアクティブに保ち、予期しない切断を検出するために使用されます。

### Bridge で設定する

`MqttV2Bridge` Inspector の **Connection > Keep Alive** セクションで設定します。

| 項目 | 説明 |
|---|---|
| **Enabled** | KeepAlive を有効化。既定は ON |
| **Seconds** | KeepAlive ping 間隔（秒）。範囲: 1-3600 秒。既定: 15 秒 |

> **推奨**: KeepAlive 間隔は **Reconnect Delay より小さい値** に設定してください。KeepAlive でブローカーが切断を検知し、その後 Reconnect Delay で再接続するという流れになるため、KeepAlive < Reconnect Delay の関係が正しいです。例：KeepAlive=15秒、Reconnect Delay=30秒

### Runtime 直接利用で設定する

```csharp
var keepAliveOptions = new MqttV2KeepAliveOptions(
    enabled: true,
    seconds: 30);  // 30 秒ごとに ping

var profile = new MqttV2ClientProfile(
    brokerHost: "127.0.0.1",
    brokerPort: 1883,
    clientIdPolicy: MqttV2ClientIdPolicy.RandomPerStart,
    connectionOptions: new MqttV2ConnectionOptions(keepAlive: keepAliveOptions));

await using var runtime = new MqttV2ClientRuntime(profile);
```

---

## Last Will and Testament (LWT)

Last Will and Testament は、クライアントが予期しない方法で切断された場合に、ブローカーが自動的にメッセージを発行する機能です。

**用途例**:
- デバイスのオンライン状態をトピック `devices/{id}/status` で監視
- IoT センサーが予期しず切断された場合に `{"status":"offline"}` を自動発行
- ロボット制御システムでアノーマリー検知

### Bridge で設定する

`MqttV2Bridge` Inspector の **Connection > Will (LWT)** セクションで設定します。

| 項目 | 説明 |
|---|---|
| **Enabled** | LWT 機能を有効化。既定は OFF |
| **Will Topic** | LWT メッセージを送信するトピック（完全一致のみ、ワイルドカード非対応） |
| **Will Payload** | UTF-8 テキストペイロード。JSON フォーマットも可能 |
| **Qos** | 配信 QoS レベル（既定: QoS 0）|
| **Retain** | LWT メッセージをサーバーに保持（既定: OFF）|

#### 設定例

```
Enabled:         ✓ (チェック)
Will Topic:      robot/status
Will Payload:    {"status":"offline","reason":"unexpected_disconnect"}
Qos:             AtLeastOnce
Retain:          ✓ (チェック、オプション)
```

### Runtime 直接利用で設定する

```csharp
var willOptions = new MqttV2WillOptions(
    enabled: true,
    topic: "robot/status",
    payloadUtf8: "{\"status\":\"offline\",\"reason\":\"unexpected_disconnect\"}",
    qos: MqttV2QualityOfServiceLevel.AtLeastOnce,
    retain: true);  // 最後の LWT を保持して新規購読者に通知

var profile = new MqttV2ClientProfile(
    brokerHost: "127.0.0.1",
    brokerPort: 1883,
    clientIdPolicy: MqttV2ClientIdPolicy.RandomPerStart,
    connectionOptions: new MqttV2ConnectionOptions(will: willOptions));

await using var runtime = new MqttV2ClientRuntime(profile);
```

### LWT の検証

LWT が正しく設定されているかを検証するには：

1. Bridge または Runtime を起動
2. LWT Topic を購読
3. `WaitUntilConnectedAsync()` で接続確認
4. 別のプロセスでクライアントを kill（予期しない切断を発生させる）
5. LWT Payload が自動発行されることを確認

**注意**: LWT が発火するのは **クライアントが予期しない切断をした場合**のみです。MQTT ブローカーを強制終了しても LWT は発行されません。クライアント側が突然切断されるシナリオをシミュレートしてください。

```csharp
// 受信側スクリプト（別クライアント）
await runtime.SubscribeRawAsync(
    "robot/status",
    new MqttV2RawSubscriptionOptions(64),
    cts.Token);
// ... client を kill する ...
// ReceiveRawAsync で LWT message が到着することを確認
var willMessage = await runtime.ReceiveRawAsync("robot/status", cts.Token);
Console.WriteLine(Encoding.UTF8.GetString(willMessage.Payload));
// 出力: {"status":"offline","reason":"unexpected_disconnect"}
```

### LWT の制約

- **有効化が必須**: クライアント起動前に必ず値を設定。実行中の差し替えはできません
- **Topic は完全一致のみ**: ワイルドカード（`+` / `#`）は使用不可
- **接続が失敗した場合**: LWT は発火しません（接続成功後の予期しない切断のみ）
- **Graceful なシャットダウン**: `DisconnectAsync()` / `ShutdownAsync()` で正常切断された場合、LWT は発火しません

---

## Subscribe QoS

Subscribe の QoS は `MqttV2SubscriptionDefaults` で制御されます。既定値は `AtMostOnce`（QoS 0）です。

### Bridge で設定する

`MqttV2Bridge` Inspector の **Connection > Subscriptions > Subscribe Qos** で設定します。

| QoS レベル | 説明 |
|---|---|
| **AtMostOnce（QoS 0）** | メッセージが最大 1 回配信されます（配信保証なし）。低遅延で軽量 |
| **AtLeastOnce（QoS 1）** | メッセージが最低 1 回配信されます。再送で到達率を上げます |
| **ExactlyOnce（QoS 2）** | メッセージが正確に 1 回だけ配信されます。処理コストが最も高い |

> **選択ガイド**:
> - **QoS 0**: IoT センサーの「最新値」（位置、温度など）— 新しい値が頻繁に到着する場合
> - **QoS 1**: 重要なイベント（アラート、制御コマンド）— 確実に 1 回は必要だが、重複も許容
> - **QoS 2**: 決済・在庫管理など — 紛失・重複が許されない場合

### Runtime 直接利用で設定する

```csharp
var profile = new MqttV2ClientProfile(
    brokerHost: "127.0.0.1",
    brokerPort: 1883,
    clientIdPolicy: MqttV2ClientIdPolicy.RandomPerStart,
    connectionOptions: new MqttV2ConnectionOptions(
        subscriptionDefaults: new MqttV2SubscriptionDefaults(MqttV2QualityOfServiceLevel.AtLeastOnce)));

await using var runtime = new MqttV2ClientRuntime(profile);

// ConnectionOptions の SubscribeQos（ここでは AtLeastOnce）が既定値として使われます
await runtime.SubscribeDataAsync<ImuData>("sensor/critical", MqttV2JsonPayloadCodec<ImuData>.Default, cts.Token);

// トピック単位で QoS を指定した場合は、指定値が優先されます
await runtime.SubscribeDataAsync<ImuData>(
    "sensor/critical-high",
    MqttV2JsonPayloadCodec<ImuData>.Default,
    MqttV2QualityOfServiceLevel.ExactlyOnce,
    cts.Token);
```

> **注意**: ブローカーは要求 QoS を調停できるため、実際の配信 QoS は要求値と異なる場合があります。実際の QoS はブローカーの設定に依存します。

---

## ライフサイクル API

| メソッド | 説明 |
|---|---|
| `StartAsync(ct)` | クライアントを起動します。ブローカーへの接続は この時点では保証しません |
| `WaitUntilConnectedAsync(ct)` | ブローカーと通信できる状態になり、全ての購読が完了するまで待機します |
| `DisconnectAsync(ct)` | ブローカーとの通信を一時的に切断します |
| `ShutdownAsync(ct)` | タイムアウト付きで完全に停止します（既定 3 秒） |
| `DisposeAsync()` | すべてのリソースを解放して終了します |

**使用フロー**: `StartAsync` でクライアントを起動した後、`WaitUntilConnectedAsync` で接続完了と購読完了を待機してから通信を開始してください。`WaitUntilConnectedAsync` は **ブローカーとの実際の通信が確立されることを保証する** ため、その後に `PublishDataAsync` などを呼ぶと確実にメッセージが送信できます。例：

---

## データアクセス API

本ライブラリは用途に応じた 2 種類のデータ取得方法を提供します：

### スナップショット形式（Data 系 API）

温度・湿度センサーなど **最新値が必要な用途** に最適です。受信したデータの最新スナップショットを型安全に保持するため、UI 更新時に現在値を即座に取得できます。ネットワーク遅延に強く、スマートホーム、ロボット制御、ITS などのリアルタイム用途に適しています。

| メソッド | 説明 |
|---|---|
| `SubscribeDataAsync<T>(topic, codec, ct)` | 型付きデータを購読開始します。QoS は `ConnectionOptions.SubscriptionDefaults.SubscribeQos` を使います |
| `SubscribeDataAsync<T>(topic, codec, subscribeQos, ct)` | 型付きデータを購読開始します。トピック単位の `subscribeQos` を優先します |
| `WaitForFirstDataAsync<T>(topic, ct)` | トピックの最新データが届くまで待機します。型付きで返却 |
| `TryGetData<T>(topic, out value)` | 現在のスナップショットをすぐに取得します。型付き。データがまだ到着していなければ失敗します |
| `PublishDataAsync<T>(topic, value, codec, ct)` | 型付きデータを送信します。Codec でエンコードされます |

### メッセージストリーム形式（Raw 系 API）

**すべてのメッセージを逐次処理したい場合**（ログ記録、リアルタイム分析、イベントトレーシングなど）に最適です。到着順にメッセージを取得できるため、メッセージの順序や全数が重要な用途に向いています。

| メソッド | 説明 |
|---|---|
| `SubscribeRawAsync(topic, options, ct)` | Raw キュー設定を指定して購読します。QoS は `ConnectionOptions.SubscriptionDefaults.SubscribeQos` を使います |
| `SubscribeRawAsync(topic, options, subscribeQos, ct)` | Raw キュー設定を指定して購読します。トピック単位の `subscribeQos` を優先します |
| `ReceiveRawAsync(topic, ct)` | トピックから次のメッセージを受け取ります。メッセージが到着するまで待機します |
| `TryDequeueRaw(topic, out message)` | メッセージをすぐに取得します。メッセージがなければ失敗します |
| `TryGetRawDroppedCount(topic, out droppedCount)` | 溢れたために破棄したメッセージ数を取得します |
| `RegisterRawHandlerAsync(topic, options, handler, ct)` | Core 拡張 API（Runtime 直接利用可）。受信待機 loop と解除処理を内部で実行します。戻り値 `Task` は loop の完了を表します |
| `RegisterRawHandlerLeaseAsync(topic, options, handler, ct)` | Core 拡張 API（Runtime 直接利用可）。購読成功後に `MqttV2RawHandlerRegistration` を返します。`DisposeAsync()` で loop 停止と完了待機を実行します |

#### Raw キュー上限

Raw 受信キューはトピックごとに上限付きです。`SubscribeRawAsync()` の `MqttV2RawSubscriptionOptions` で次を指定します。

- `maxQueueDepth`: キュー上限（1 以上）

上限超過時は常に `DropOldest`（最古破棄・新着保持）で動作します。監視には `TryGetRawDroppedCount()` を使ってください。

`RegisterRawHandlerLeaseAsync()` は `RegisterRawHandlerAsync()` と同じ loop を内部で利用し、cleanup を `MqttV2RawHandlerRegistration.DisposeAsync()` に集約します。

ドロップ監視は自動 warning ではなく、`TryGetRawDroppedCount()` を呼び出し側で定期確認する運用を推奨します。

この方針は Runtime 直接利用と Bridge 利用の両方で共通です（Bridge でも自動 warning は出力しません）。

---

## このライブラリが向かない用途

このライブラリはシンプルで直感的ですが、以下の要件には適していません：

- **ワイルドカードトピックの監視** — `sensors/+/temperature` や `devices/#` のワイルドカード契約はサポートしていません。複数トピックの購読が必要な場合は、各トピックを個別に登録してください
- **同一 topic 購読中の Codec 差し替え** — 購読中の topic に対して Codec をそのまま差し替えることはできません。`UnsubscribeDataAsync(topic)` 後に再購読してください
- **複数ブローカーの同時接続** — 複数のブローカーと同時に通信することはサポートしていません。フェイルオーバーやブローカースイッチングが必要な場合は複数の Runtime インスタンスを時系列で管理してください
- **バイナリ効率最優先** — JSON 形式ベースのため、極限の通信効率（バイナリプロトコル化など）が必要な場合は MQTTnet を直接使用してください

---

## ライセンス

[MIT License](LICENSE)