# unity-mqtt

Unity と .NET 向けの MQTT クライアントライブラリです。
[MQTTnet v4](https://github.com/dotnet/MQTTnet) をベースに、カスタマイズ可能なペイロード形式での送受信と Pull 型データアクセスを提供します。

![Unity 2019.1+](https://img.shields.io/badge/Unity-2019.1%2B-black) ![.NET Standard 2.1](https://img.shields.io/badge/.NET-Standard%202.1-blue) [![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

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

MQTT の多くのクライアントはコールバック・イベント駆動ですが、センサーの最新値を `Update()` 内で参照するにはスレッド間の安全な橋渡しが必要です。unity-mqtt は **Pull 型 API** を採用し、`TryGetDataValue<T>()` を単純に呼び出すだけで最新値が取得できます。受信スレッドとメインスレッドの調整コードが不要です。

### Unity 依存なし、どこでも動作

`MqttV2ClientRuntime` は **純粋な .NET 実装** で、Unity に依存しません。Raspberry Pi などの Linux エッジデバイスでも同じコードが動作するため、送信側と受信側で同じプロジェクト・同じ型定義を使い回せます。

### ペイロードコーデックの自由度

ライブラリ標準として `json-envelope-v2`（JSON envelope 形式）と `flat-json-v1`（フラット JSON 形式）の 2 種類の Codec を提供しています。`MqttV2Bridge` の Inspector から選択するだけで切り替えられます。独自形式が必要な場合は `MqttV2TextPayloadCodec` を継承して 2 メソッドを実装します（接続後の実行中差し替えはできません）。複合値は `TryGetDataValue<T>()` で JsonConvert ベースの変換を試み、変換できない場合は `false` を返します。

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
2. Inspector で Broker Host / Port を設定します。
3. 常時受信したいトピックは `Auto Data Topics` に追加します。Raw 購読はスクリプトから明示的に行います。
4. **Payload Codec** で `JsonEnvelope`（既定）か `FlatJson` を選択します。既存デバイスがフラット JSON を送信している場合は `FlatJson` を選択します。

### 2. スクリプトからデータを取得する

```csharp
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityMqtt.V2.Bridge;
using UnityMqtt.V2.Core;

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

        // ロボット位置情報を購読開始（最新値を自動更新）
        await _bridge.Runtime.SubscribeDataAsync("robot/position", cancellationToken: _cts.Token);

        // 最初のデータが到着するまで待機（ネットワーク遅延を吸収）
        await _bridge.Runtime.WaitForFirstDataAsync("robot/position", _cts.Token);
        ApplyPosition();
    }

    private void Update()
    {
        ApplyPosition();
    }

    private void ApplyPosition()
    {
        if (_bridge.Runtime.TryGetDataValue<float>("robot/position", "X", out var x) &&
            _bridge.Runtime.TryGetDataValue<float>("robot/position", "Y", out var y) &&
            _bridge.Runtime.TryGetDataValue<float>("robot/position", "Z", out var z))
        {
            transform.position = new Vector3(x, y, z);
        }
    }

    private void OnDestroy()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
```

### 3. データを送信する

```csharp
await _bridge.Runtime.PublishDataAsync("robot/position", new Dictionary<string, object>
{
    { "X", transform.position.x },
    { "Y", transform.position.y },
    { "Z", transform.position.z },
}, cancellationToken: _cts.Token);
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
using System.Collections.Generic;
using System.Threading;
using UnityMqtt.V2.Core;

var profile = new MqttV2ClientProfile(
    brokerHost: "127.0.0.1",
    brokerPort: 1883,
    payloadCodecId: "json-envelope-v2",
    clientIdPolicy: MqttV2ClientIdPolicy.RandomPerStart);

await using var runtime = new MqttV2ClientRuntime(
    profile,
    payloadCodec: new MqttV2JsonPayloadCodec());

using var cts = new CancellationTokenSource();

// ブローカーと通信できるまで起動して接続待機
await runtime.StartAsync(cts.Token);
await runtime.WaitUntilConnectedAsync(cts.Token);

// --- Data 系 API: 最新センサー値の取得 ---
// IMU センサーデータを購読（最新値が自動更新される）
await runtime.SubscribeDataAsync("sensor/imu", cancellationToken: cts.Token);

// データを送信
await runtime.PublishDataAsync("sensor/imu", new Dictionary<string, object>
{
    { "roll",  0.1 },
    { "pitch", 0.2 },
    { "yaw",   0.3 },
}, cancellationToken: cts.Token);

// 最新值を取得（データが既に到着していれば即座に返却）
var snapshot = await runtime.WaitForFirstDataAsync("sensor/imu", cts.Token);
if (snapshot.TryGetValue("roll", out var entry) && entry.TryGetValue<double>(out var roll))
    Console.WriteLine($"roll = {roll}");

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

既定の `MqttV2JsonPayloadCodec` では、Data 系 API（`PublishDataAsync` / `SubscribeDataAsync`）は以下の JSON envelope を使用します。

```json
{
  "timestamp": "2026-03-14T12:34:56.789Z",
  "items": [
    { "name": "X", "value": 1.23 },
    { "name": "Y", "value": 4.56 },
    { "name": "Z", "value": 7.89 }
  ]
}
```

生バイト列を直接扱う場合は `PublishRawAsync` / `SubscribeRawAsync` / `ReceiveRawAsync` を使用します。

### ペイロード Codec の選択

ライブラリは以下の 2 種類の Codec を標準提供しています。用途に応じて選択します。

| Codec クラス | Id | 向いている場面 |
|---|---|---|
| `MqttV2JsonPayloadCodec`（既定） | `json-envelope-v2` | ライブラリ間の通信、型安全が必要な場面 |
| `MqttV2FlatJsonPayloadCodec` | `flat-json-v1` | ESP32・Raspberry Pi などの既存デバイスとの連携 |

#### Bridge で切り替える（コードなし）

Inspector の **Payload Codec** フィールドを `FlatJson` に設定するだけで切り替わります。

#### Runtime 直接利用で切り替える

```csharp
var codec = new MqttV2FlatJsonPayloadCodec();

var profile = new MqttV2ClientProfile(
    brokerHost: "127.0.0.1",
    brokerPort: 1883,
    payloadCodecId: codec.Id,           // "flat-json-v1"
    clientIdPolicy: MqttV2ClientIdPolicy.RandomPerStart);

await using var runtime = new MqttV2ClientRuntime(profile, payloadCodec: codec);
```

フラット JSON のペイロードは次のような見た目です。

```json
{
  "timestamp": "2026-03-14T12:34:56.789Z",
  "X": 1.23,
  "Y": 4.56,
  "Z": 7.89
}
```

Publish / receive は通常の Data 系 API と同じです。

```csharp
// Publish: { "timestamp": "...", "X": 1.23, "Y": 4.56, "Z": 7.89 } で送信される
await runtime.PublishDataAsync(
    "robot/position",
    DateTime.UtcNow,
    new Dictionary<string, object> { { "X", 1.23 }, { "Y", 4.56 }, { "Z", 7.89 } },
    cancellationToken: cts.Token);

// 受信値の取り出し（通常の Data 系 API と同じ）
if (runtime.TryGetDataValue<double>("robot/position", "X", out var x))
    Console.WriteLine($"X = {x}");
```

> **注意**: `flat-json-v1` では `timestamp` はペイロード内の予約キーです。同名のフィールドをデータとして使用しないでください。

### カスタム Codec の実装

独自フォーマットが必要な場合は `MqttV2TextPayloadCodec` を継承します。UTF-8 変換・null ガード・エラーハンドリング・timestamp 正規化はライブラリ側で処理されるため、実装が必要なのは **2 メソッドのみ**です。

#### 例: CSV 形式 Codec

次のような 1 行 CSV でデータをやり取りしたい場合を考えます。

```
2026-03-14T12:34:56.789Z,X=1.23,Y=4.56,Z=7.89
```

```csharp
using System;
using System.Collections.Generic;
using UnityMqtt.V2.Core;

public sealed class CsvPayloadCodec : MqttV2TextPayloadCodec
{
    public override string Id => "csv-v1";

    // 送信: "timestamp,Key=Value,Key=Value,..." の形式に変換
    protected override string FormatFields(DateTime timestampUtc, IReadOnlyDictionary<string, object> fields)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(timestampUtc.ToString("o"));

        foreach (var pair in fields)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                continue;

            sb.Append(',');
            sb.Append(pair.Key);
            sb.Append('=');
            sb.Append(pair.Value);
        }

        return sb.ToString();
    }

    // 受信: "timestamp,Key=Value,Key=Value,..." を fields に変換
    protected override bool TryParseFields(
        string text,
        out IReadOnlyDictionary<string, object> fields,
        out DateTime timestampUtc,
        out string error)
    {
        fields = null;
        timestampUtc = default;
        error = null;

        try
        {
            var parts = text.Split(',');
            timestampUtc = ParseTimestampOrNow(parts[0]);

            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            for (var i = 1; i < parts.Length; i++)
            {
                var sep = parts[i].IndexOf('=');
                if (sep < 1)
                    continue;

                var key = parts[i].Substring(0, sep);
                var val = parts[i].Substring(sep + 1);

                // 数値として解釈できる場合は double に変換
                map[key] = double.TryParse(val, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) ? (object)d : val;
            }

            fields = map;
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
var codec = new CsvPayloadCodec();

var profile = new MqttV2ClientProfile(
    brokerHost: "127.0.0.1",
    brokerPort: 1883,
    payloadCodecId: codec.Id,           // "csv-v1"
    clientIdPolicy: MqttV2ClientIdPolicy.RandomPerStart);

await using var runtime = new MqttV2ClientRuntime(profile, payloadCodec: codec);

// Publish: "2026-03-14T12:34:56.789Z,X=1.23,Y=4.56,Z=7.89" の見た目で送信される
await runtime.PublishDataAsync(
    "robot/position",
    DateTime.UtcNow,
    new Dictionary<string, object> { { "X", 1.23 }, { "Y", 4.56 }, { "Z", 7.89 } },
    cancellationToken: cts.Token);

// 受信値の取り出しは通常の Data 系 API と同じ
if (runtime.TryGetDataValue<double>("robot/position", "X", out var x))
    Console.WriteLine($"X = {x}");
```

#### 例: OPC UA JSON Codec（既存 PLC・センサーとの接続）

OPC UA Pub/Sub（MQTT transport）で流れる典型的な JSON ペイロードは次の構造を持ちます。

```json
{
    "MessageType": "ua-data",
    "Messages": [
        {
            "Timestamp": "2026-03-14T12:34:56.789Z",
            "Payload": {
                "Temperature": { "Value": 23.5 },
                "Pressure":    { "Value": 101.3 }
            }
        }
    ]
}
```

このような「形式が決まっている既存システム」に対しては、`TryParseFields` でペイロードの取り出し方を記述するだけです。

```csharp
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityMqtt.V2.Core;

public sealed class OpcUaJsonCodec : MqttV2TextPayloadCodec
{
    public override string Id => "opcua-json-v1";

    protected override bool TryParseFields(
        string text,
        out IReadOnlyDictionary<string, object> fields,
        out DateTime timestampUtc,
        out string error)
    {
        fields = null;
        timestampUtc = default;
        error = null;

        try
        {
            var root = JObject.Parse(text);
            var msg = root["Messages"]?[0];
            if (msg == null)
            {
                error = "Messages[0] not found.";
                return false;
            }

            timestampUtc = ParseTimestampOrNow(msg.Value<string>("Timestamp"));

            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            var payload = msg["Payload"] as JObject;
            if (payload != null)
            {
                foreach (var prop in payload.Properties())
                    map[prop.Name] = prop.Value["Value"];  // Value サブフィールドを展開
            }

            fields = map;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // このシステムへの Publish は想定しないため最小実装
    protected override string FormatFields(DateTime timestampUtc, IReadOnlyDictionary<string, object> fields)
        => throw new NotSupportedException("Publishing in OPC UA format is not supported by this codec.");
}
```

受信側の取り出しは通常の Data 系 API と同じです。

```csharp
var codec = new OpcUaJsonCodec();

var profile = new MqttV2ClientProfile(
    brokerHost: "127.0.0.1",
    brokerPort: 1883,
    payloadCodecId: codec.Id,
    clientIdPolicy: MqttV2ClientIdPolicy.RandomPerStart);

await using var runtime = new MqttV2ClientRuntime(profile, payloadCodec: codec);
await runtime.StartAsync(cts.Token);
await runtime.SubscribeDataAsync("factory/plc1", cancellationToken: cts.Token);

if (runtime.TryGetDataValue<double>("factory/plc1", "Temperature", out var temp))
    Console.WriteLine($"Temperature = {temp}");
```

バイナリ形式（MessagePack など byte[] を直接扱う場合）は `IMqttV2PayloadCodec` を直接実装します。

このように「見た目」は自由ですが、ライブラリ内部では `timestamp + fields` に正規化して Data API に渡されます。

---

## 更新モード

`SubscribeDataAsync` の引数でトピックごとに更新モードを選択します。

| モード | 動作 |
|---|---|
| `Differential`（既定） | 受信したフィールドのキーだけ上書き。受信しないキーは保持 |
| `Replace` | 受信したフィールド全体で再構築。受信しないキーは削除 |

```csharp
// トピックごとに Replace モードを指定する例
await runtime.SubscribeDataAsync("sensor/full", MqttV2TopicUpdateMode.Replace, cts.Token);
```

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
    payloadCodecId: "json-envelope-v2",
    clientIdPolicy: MqttV2ClientIdPolicy.RandomPerStart,
    connectionOptions: new MqttV2ConnectionOptions(tls: tlsOptions));

await using var runtime = new MqttV2ClientRuntime(profile, payloadCodec: /* codec */);
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
    payloadCodecId: "json-envelope-v2",
    clientIdPolicy: MqttV2ClientIdPolicy.RandomPerStart,
    connectionOptions: new MqttV2ConnectionOptions(keepAlive: keepAliveOptions));

await using var runtime = new MqttV2ClientRuntime(profile, payloadCodec: /* codec */);
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
    qos: MqttQualityOfServiceLevel.AtLeastOnce,
    retain: true);  // 最後の LWT を保持して新規購読者に通知

var profile = new MqttV2ClientProfile(
    brokerHost: "127.0.0.1",
    brokerPort: 1883,
    payloadCodecId: "json-envelope-v2",
    clientIdPolicy: MqttV2ClientIdPolicy.RandomPerStart,
    connectionOptions: new MqttV2ConnectionOptions(will: willOptions));

await using var runtime = new MqttV2ClientRuntime(profile, payloadCodec: /* codec */);
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
    payloadCodecId: "json-envelope-v2",
    clientIdPolicy: MqttV2ClientIdPolicy.RandomPerStart,
    connectionOptions: new MqttV2ConnectionOptions(
        subscriptionDefaults: new MqttV2SubscriptionDefaults(MqttQualityOfServiceLevel.AtLeastOnce)));

await using var runtime = new MqttV2ClientRuntime(profile, payloadCodec: /* codec */);

// すべてのトピックに同じ QoS が適用されます（トピック単位での QoS override はサポートしていません）
await runtime.SubscribeDataAsync("sensor/critical", MqttV2TopicUpdateMode.Differential, cts.Token);
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

温度・湿度センサーなど **最新値が必要な用途** に最適です。受信したデータの最新スナップショットをキーバリュー形式で保持するため、UI 更新時に現在値を即座に取得できます。ネットワーク遅延に強く、スマートホーム、ロボット制御、ITS などのリアルタイム用途に適しています。

| メソッド | 説明 |
|---|---|
| `WaitForFirstDataAsync(topic, ct)` | トピックの最新データが届くまで待機します。到着済みなら即座に返します |
| `TryGetDataSnapshot(topic, out snapshot)` | 現在のスナップショットをすぐに取得します。データがまだ到着していなければ失敗します |
| `TryGetDataValue<T>(topic, key, out value)` | スナップショット内の特定のキー値をすぐに取得します |

> **更新セマンティクス**: 同じ値のメッセージを受信した場合でも、対象フィールドの `SourceTimestampUtc` / `ReceivedUtc` は最新受信で更新されます。

### メッセージストリーム形式（Raw 系 API）

**すべてのメッセージを逐次処理したい場合**（ログ記録、リアルタイム分析、イベントトレーシングなど）に最適です。到着順にメッセージを取得できるため、メッセージの順序や全数が重要な用途に向いています。

| メソッド | 説明 |
|---|---|
| `SubscribeRawAsync(topic, options, ct)` | Raw キュー設定を指定して購読します。`maxQueueDepth` は 1 以上が必須です |
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
- **実行中の Codec 差し替え** — 起動後に JSON から MessagePack に切り替えるなど、PayloadCodec の実行中差し替えはできません。再初期化が必要です
- **複数ブローカーの同時接続** — 複数のブローカーと同時に通信することはサポートしていません。フェイルオーバーやブローカースイッチングが必要な場合は複数の Runtime インスタンスを時系列で管理してください
- **バイナリ効率最優先** — JSON 形式ベースのため、極限の通信効率（バイナリプロトコル化など）が必要な場合は MQTTnet を直接使用してください

---

## ライセンス

[MIT License](LICENSE)