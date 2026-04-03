# PullSub
![Unity 2022.3+](https://img.shields.io/badge/Unity-2022.3%2B-black)
![.NET Standard 2.1](https://img.shields.io/badge/.NET-Standard%202.1-blue)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

Pull-style typed Pub/Sub runtime for Unity and .NET. Supports MQTTnet v4.3.x and MQTTnet.Extensions.ManagedClient v4.3.x.

- For real-time IoT sensor messaging: Get the latest IoT sensor data efficiently — best for digital twin, robotics, smart home, etc.
- Easy and type-safe access from the Unity main thread while transport/serialization layers run in other threads
- Customizable codec and serializer — can customize payload structure to match specific formats such as OPC UA, and can optimize GC by swapping serializers
- Transport-agnostic design — swap MQTT for UDP or any custom protocol by implementing `ITransport` interface
- Share code between Unity and edge devices such as Raspberry Pi — the Core layer has no Unity dependency

---

## What PullSub provides

```csharp
// Raw MQTTnet — thread safety, decoding, and lifetime management are your responsibility
client.ApplicationMessageReceivedAsync += e =>
{
    var payload = e.ApplicationMessage.Payload;
    var position = JsonSerializer.Deserialize<Position>(payload);
    lock (_gate) { _position = position; }
};

// PullSub — define your type and topic once, then just pull
public sealed class Position
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

public static class Topics
{
    public static readonly IPullSubTopic<Position> Position
        = PullSubTopic.Create<Position>("robot/position");
}
```

**Unity**
```csharp
public class RobotController : MonoBehaviour
{
    [SerializeField] private PullSubMqttClient _client;

    private PullSubDataHandle<Position> _positionHandle;
    private CancellationTokenSource _cts;

    private async void Start()
    {
        _cts = new CancellationTokenSource();

        await _client.Runtime.SubscribeDataAsync(Topics.Position, _cts.Token);

        _positionHandle = _client.Runtime.GetDataHandle(Topics.Position);
    }

    private void Update()
    {
        if (_positionHandle == null) return;
        if (!_positionHandle.HasValue) return;
        // Access the latest value just like transform.position
        var pos = _positionHandle.Value;
        transform.position = new Vector3(pos.X, pos.Y, pos.Z);
    }

    private void OnDestroy()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
```

**Why PullSub?** Because callback-driven MQTT libraries force you to manage thread safety, decoding, and data lifetime yourself. PullSub handles all of that — just define your type and pull the latest value whenever you need it.

**Why pull, not push?** Unity's game loop polls state every frame — transform.position, 
Input.GetAxis, Physics — PullSub fits this model naturally. 
No callbacks, no queues to drain manually, no lock.

PullSub abstracts transport protocols behind `ITransport`. You can use protocols other than MQTT, such as UDP, by implementing `ITransport`.

---

## When PullSub is not the right fit

- **You need callbacks:** Use the Queue API's `RegisterHandlerLeaseAsync` instead.
  It provides callback-style message handling with async support, backpressure,
  and drop detection — everything a raw callback pattern lacks.
```csharp
  _registration = await runtime.RegisterHandlerLeaseAsync(
      Topics.Position,
      async (pos, ct) =>
      {
          await UniTask.SwitchToMainThread();
          transform.position = pos.ToVector3();
      });
```
- **You need wildcard topics:** Wildcard topics (`sensors/+/temperature`, `devices/#`) are not supported. PullSub requires exact-match topic strings.

---

## Two APIs for Two Different Problems

**Data API keeps the latest value. Queue API keeps every message.**

### Data API — for *state*

Use this for data where only the latest value matters, such as IoT sensor readings, digital twin state, or robot pose.

Data API is optimized for in-place latest-state reads:

- Read `Value` and consume it in the same frame/tick.
- Do not keep `Value` references for history or delayed processing.
- Use Queue API when you need ordered history or event replay.

```csharp
// Poll in Update() — just like transform.position
private void Update()
{
    var pos = _positionHandle.Value;
    transform.position = new Vector3(pos.X, pos.Y, pos.Z);
}
```

### Queue API — for *events*

Use this for data where every message must be processed in order, such as commands, logs, or transactions.

```csharp
// Handle commands one by one
var registration = await runtime.RegisterHandlerLeaseAsync(
    Topics.Command,
    async (command, ct) => await robot.ExecuteAsync(command, ct));
```

---

## Getting Started (Unity)

### Install DLLs

Installing DLLs via [NuGet for Unity](https://github.com/GlitchEnzo/NuGetForUnity) is recommended, or place them manually in your `Assets/Plugins` folder.

<img width="571" height="556" alt="image" src="https://github.com/user-attachments/assets/7fa6bc3a-574e-46c7-aa81-c3b1129852a1" />

**Core**
- `System.Text.Json.dll` (8.0.x)
- `System.Text.Encodings.Web.dll` (8.0.x)
- `Microsoft.Bcl.AsyncInterfaces.dll` (8.0.x)

**Mqtt**
- `MQTTnet.dll` (4.3.x)
- `MQTTnet.Extensions.ManagedClient.dll` (4.3.x)

### Unity (UPM)

Install via UPM with a git URL:
```
https://github.com/toshi0515/PullSub.git?path=src/com.toshi0515.pullsub
```

### .NET (NuGet)

```
dotnet add package PullSub.Core
dotnet add package PullSub.Mqtt
```

---

## Quick Start — Unity

### 1. Add Component

Add `PullSubMqttClient` to a GameObject and configure the broker settings in the Inspector.

### 2. Define Topic and Type

Define your data type and topic in a single file.

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
    public static readonly IPullSubTopic<Position> Position
        = PullSubTopic.Create<Position>("robot/position");
}
```

### 3. Recommended: Use PullSubDataCache (non-blocking)

`PullSubDataCache` can subscribe multiple topics in parallel and publish latest values without blocking on first-arrival waits.

Define topics by convention (`*Topics` classes) or opt-in with attributes:

```csharp
using PullSub.Bridge;
using PullSub.Core;

public static class RobotTopics
{
    // Convention-based discovery: class name ends with "Topics"
    public static readonly IPullSubTopic<Position> Position
        = PullSubTopic.Create<Position>("robot/position");

    // Optional explicit key name for inspector
    [PullSubTopicKeyAlias("RobotStatus")]
    public static readonly IPullSubTopic<RobotStatus> Status
        = PullSubTopic.Create<RobotStatus>("robot/status");
}
```

Topic keys are generated automatically after script recompilation.
You can also run it manually from `Tools > PullSub > Generate Topic Catalog`.

Add `PullSubDataCache` to a GameObject, assign `PullSubMqttClient`, and choose topic keys + per-topic QoS in the Inspector.
`PullSubTopicKey` is only for Inspector setup; runtime access uses `IPullSubTopic<T>` (same style as Core APIs).

Consume values with `HasValue`/`TryGet`:

```csharp
using PullSub.Bridge;
using PullSub.Core;
using UnityEngine;

public sealed class RobotControllerByTopic : MonoBehaviour
{
    [SerializeField] private PullSubDataCache _cache;

    private void Update()
    {
        if (!_cache.TryGet(RobotTopics.Position, out Position pos))
            return;

        transform.position = new Vector3(pos.X, pos.Y, pos.Z);
    }
}
```

And you can use a DataHandle-like API from `PullSubDataCache`:

```csharp
using PullSub.Bridge;
using PullSub.Core;
using UnityEngine;

public sealed class RobotControllerByHandle : MonoBehaviour
{
    [SerializeField] private PullSubDataCache _cache;

    private PullSubDataHandle<Position> _positionHandle;

    private void Awake()
    {
        _positionHandle = _cache.GetDataHandle(RobotTopics.Position);
    }

    private void Update()
    {
        if (!_positionHandle.HasValue) return;

        var pos = _positionHandle.Value;
        transform.position = new Vector3(pos.X, pos.Y, pos.Z);
    }
}
```

### 4. Direct Runtime Access (advanced)

```csharp
using System.Threading;
using UnityEngine;
using PullSub.Core;
using PullSub.Bridge;

public class RobotController : MonoBehaviour
{
    [SerializeField] private PullSubMqttClient _client;

    private PullSubDataHandle<Position> _positionHandle;
    private CancellationTokenSource _cts;

    private async void Start()
    {
        _cts = new CancellationTokenSource();

        await _client.Runtime.StartAsync(_cts.Token);
        await _client.Runtime.WaitUntilConnectedAsync(_cts.Token);

        // Subscribe
        await _client.Runtime.SubscribeDataAsync(Topics.Position, _cts.Token);

        // Obtain a handle once and reuse it every frame
        _positionHandle = _client.Runtime.GetDataHandle(Topics.Position);
    }

    private void Update()
    {
        if (_positionHandle == null) return;
        if (!_positionHandle.HasValue) return;
        var pos = _positionHandle.Value;
        transform.position = new Vector3(pos.X, pos.Y, pos.Z);
    }

    private void OnDestroy()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
```

Use `WaitForFirstDataAsync` only when you explicitly need strict first-arrival gating.

### 5. Handle Commands Using the Queue API

```csharp
public class CommandReceiver : MonoBehaviour
{
    [SerializeField] private PullSubMqttClient _client;

    private PullSubQueueHandlerRegistration _registration;

    private async void Start()
    {
        _registration = await _client.Runtime.RegisterHandlerLeaseAsync(
            Topics.Command,
            async (command, ct) =>
            {
                await robot.ExecuteAsync(command, ct);
            });
    }

    private async void OnDestroy()
    {
        if (_registration != null)
            await _registration.DisposeAsync();
    }
}
```

### 6. Publish Data

```csharp
var position = new Position
{
    X = transform.position.x,
    Y = transform.position.y,
    Z = transform.position.z
};

await _client.Runtime.PublishDataAsync(Topics.Position, position, cancellationToken: _cts.Token);
```

---

## Quick Start — .NET

Instantiate `PullSubRuntime` directly without the Unity Bridge. This lets you share code between Unity and edge devices such as Raspberry Pi.

```csharp
using PullSub.Core;
using PullSub.Mqtt;

var connectionOptions = new MqttConnectionOptions();

var profile = new MqttClientProfile(
    brokerHost: "127.0.0.1",
    brokerPort: 1883,
    clientIdPolicy: MqttClientIdPolicy.RandomPerStart,
    connectionOptions: connectionOptions);

var transport = new MqttTransport(profile, connectionOptions);

await using var runtime = new PullSubRuntime(transport);

using var cts = new CancellationTokenSource();

await runtime.StartAsync(cts.Token);
await runtime.WaitUntilConnectedAsync(cts.Token);

// Subscribe
await runtime.SubscribeDataAsync(Topics.Position, cts.Token);
await runtime.WaitForFirstDataAsync(Topics.Position, cts.Token);

var handle = runtime.GetDataHandle(Topics.Position);

// 100 Hz loop
while (!cts.IsCancellationRequested)
{
    var pos = handle.Value;
    var command = controller.Calculate(pos);
    await robot.SendCommandAsync(command, cts.Token);
    await Task.Delay(10, cts.Token);
}
```

---

## Topic Definition — Best Practices

### Define Topic and Type in the same file

```csharp
// Types.cs
public sealed class RobotCommand
{
    public string Type { get; set; }
    public string Payload { get; set; }
}

// Topics.cs
public static class Topics
{
    public static readonly IPullSubTopic<RobotCommand> RobotCommand
        = PullSubTopic.Create<RobotCommand>("robot/command");
}
```

### Share Topic and Type definitions between Publisher and Subscriber

Referencing the same definition files on both sides prevents codec mismatches.

```
SharedClass/
  ├── Types.cs       ← Position type + RobotCommand type (Define Types)
  └── Topics.cs   ← Position topic + RobotCommand topic (Define Topics)

Publisher (.NET / Raspberry Pi)  ← references SharedClass
Subscriber (Unity)               ← references SharedClass
```

---

## Serialization

PullSub uses a JSON envelope format by default. You can change the serializer and message structure by implementing `IPayloadCodec<T>`.

```csharp
// Default — JSON envelope: { "timestamp": "...", "data": { ... } }
PullSubTopic.Create<Position>("robot/position");

// Flat JSON — { "timestamp": "...", ...Position properties... }
PullSubTopic.Create<Position>("robot/position",
    PullSubFlatJsonPayloadCodec<Position>.Default);

// Custom codec (e.g. MemoryPack)
PullSubTopic.Create<Position>("robot/position",
    new PullSubMemoryPackCodec<Position>());
```

Always share the same codec between Publisher and Subscriber. Defining the codec inside `IPullSubTopic<T>` is the recommended way to enforce this.

For class payloads used in Data API, codecs must support in-place decode (`IPayloadInPlaceCodec<T>`).
Built-in JSON codecs already support this contract.

### Notes on Missing Members

- By default, PullSub uses System.Text.Json behavior that serializes/deserializes public properties.
- If a payload is missing a member, that member is deserialized as its default value.
    - `int` -> `0`, `float` -> `0`, `bool` -> `false`, reference type -> `null`
- PullSub Data API does not merge missing members with previous cached values.
    A successfully decoded payload replaces the cached value for the topic.

To avoid accidental defaults caused by schema drift, share the same DTO and topic definitions between Publisher and Subscriber.

---

## Connection Options

Connection options are configured via `MqttConnectionOptions` and passed to `MqttClientProfile`. In Unity, these can also be set through the `PullSubMqttClient` Inspector.

### Credentials

```csharp
var connectionOptions = new MqttConnectionOptions(
    credentials: new MqttCredentials("username", "password"));
```

### TLS

```csharp
var connectionOptions = new MqttConnectionOptions(
    tls: new MqttTlsOptions(
        enabled: true,
        allowUntrustedCertificates: false,
        targetHost: "mqtt.example.com"));

var profile = new MqttClientProfile(
    brokerHost: "mqtt.example.com",
    brokerPort: 8883,
    clientIdPolicy: MqttClientIdPolicy.RandomPerStart,
    connectionOptions: connectionOptions);
```

### WebSocket

```csharp
var connectionOptions = new MqttConnectionOptions(
    transport: new MqttTransportOptions(
        kind: MqttTransportKind.Wss,
        webSocketPath: "/mqtt"));
```

### Last Will and Testament (LWT)

```csharp
var connectionOptions = new MqttConnectionOptions(
    will: new MqttWillOptions(
        enabled: true,
        topic: "robot/status",
        payloadUtf8: "{\"status\":\"offline\"}",
        qos: PullSubQualityOfServiceLevel.AtLeastOnce,
        retain: true));
```

### Subscribe QoS

The default subscribe QoS is `AtLeastOnce` (QoS 1). The actual delivered QoS is `min(publish QoS, subscribe QoS)`, so using QoS 0 would silently discard delivery guarantees regardless of what the publisher sends. You can override the QoS per subscription when needed.

```csharp
// Default — AtLeastOnce
await runtime.SubscribeDataAsync(Topics.Position, cancellationToken: ct);

// Explicit QoS
await runtime.SubscribeDataAsync(Topics.Position,
    PullSubQualityOfServiceLevel.ExactlyOnce, ct);
```

---

## API Reference

### Lifecycle

```csharp
Task StartAsync(CancellationToken ct = default)
Task WaitUntilConnectedAsync(CancellationToken ct = default)
Task DisconnectAsync(CancellationToken ct = default)
Task ShutdownAsync(CancellationToken ct = default)
ValueTask DisposeAsync()

PullSubState State { get; }   // NotStarted | Starting | Ready | Reconnecting | Stopped | Disposed
bool IsStarted { get; }
bool IsConnected { get; }
bool IsReady { get; }
```

### Data API

```csharp
// Subscribe / Unsubscribe
Task SubscribeDataAsync<T>(IPullSubTopic<T> topic,
    CancellationToken ct = default)
Task SubscribeDataAsync<T>(IPullSubTopic<T> topic,
    PullSubQualityOfServiceLevel subscribeQos,
    CancellationToken ct = default)
Task UnsubscribeDataAsync<T>(IPullSubTopic<T> topic,
    CancellationToken ct = default)

// Access the latest value
PullSubDataHandle<T> GetDataHandle<T>(IPullSubTopic<T> topic)
T GetData<T>(IPullSubTopic<T> topic, T defaultValue = default)
bool TryGetData<T>(string topic, out T value)
bool TryGetData<T>(string topic, out T value, out DateTime timestampUtc)

// Wait for the first message to arrive
Task<T> WaitForFirstDataAsync<T>(IPullSubTopic<T> topic,
    CancellationToken ct = default)
```

### Queue API

```csharp
// Subscribe / Unsubscribe manually
Task SubscribeQueueAsync(string topic, PullSubQueueOptions options,
    PullSubQualityOfServiceLevel subscribeQos = AtLeastOnce,
    CancellationToken ct = default)
Task UnsubscribeQueueAsync(string topic, CancellationToken ct = default)

// Receive messages
Task<PullSubQueueMessage> ReceiveQueueAsync(string topic,
    CancellationToken ct = default)
bool TryDequeue(string topic, out PullSubQueueMessage message)
bool TryGetDroppedCount(string topic, out long droppedCount)

// Register a handler loop (subscribe + receive loop + unsubscribe on dispose)
Task<PullSubQueueHandlerRegistration> RegisterHandlerLeaseAsync<T>(
    IPullSubTopic<T> topic,
    Func<T, CancellationToken, Task> handler,
    CancellationToken ct = default)
Task<PullSubQueueHandlerRegistration> RegisterHandlerLeaseAsync<T>(
    IPullSubTopic<T> topic,
    PullSubQueueOptions options,
    Func<T, CancellationToken, Task> handler,
    CancellationToken ct = default)
```

### Publish API

```csharp
Task PublishDataAsync<T>(IPullSubTopic<T> topic, T value,
    PullSubQualityOfServiceLevel qos = AtMostOnce,
    bool retain = false,
    CancellationToken ct = default)

Task PublishRawAsync(string topic, byte[] payload,
    PullSubQualityOfServiceLevel qos = AtMostOnce,
    bool retain = false,
    CancellationToken ct = default)
```

### PullSubDataHandle\<T\>

```csharp
T Value { get; }                    // Returns default(T) when HasValue is false
bool HasValue { get; }              // True if at least one message has been received
bool IsValid { get; }               // True when subscribed and HasValue
DateTime TimestampUtc { get; }      // Timestamp from payload, or receive time if absent
DateTime TimestampLocal { get; }    // TimestampUtc converted to local time
T GetValueOrDefault(T fallback)     // Returns fallback instead of default(T) when no data
string Topic { get; }               // The subscribed topic name
```

Data handle is intended for latest-state pull in game loops.
Avoid keeping `Value` references across frames; use Queue API or explicit snapshots for history.

### Data Arrival and Default Semantics

Before the first message arrives for a topic:

- `HasValue` is `false`
- `Value` returns `default(T)`
- `TimestampUtc` returns `default(DateTime)`

Practical implications:

- If `T` is a class, `Value` may be `null` before first arrival.
- If `T` is a struct, each member is the type default until first arrival.
- Preferred non-blocking flow: use `HasValue` (or `TryGet` in `PullSubDataCache`) before reading `Value`.
- In `PullSubDataCache`, `PullSubTopicKey` is for Inspector setup only; runtime access uses `IPullSubTopic<T>` and `GetDataHandle`.
- Optional strict flow: call `WaitForFirstDataAsync` when your app requires first-arrival gating.

### PullSubQueueMessage

```csharp
string Topic { get; }
byte[] Payload { get; }
DateTime ReceivedUtc { get; }
```

### PullSubQueueHandlerRegistration

```csharp
Task Completion { get; }    // Completes when the handler loop exits; faults if the handler throws
ValueTask DisposeAsync()    // Cancels the loop and waits for it to exit
```

---

## IL2CPP — Preventing Code Stripping

When building with IL2CPP, add the types used with `SubscribeDataAsync<T>` and `PublishDataAsync<T>` to `link.xml` to prevent them from being stripped.

```xml
<linker>
  <assembly fullname="Assembly-CSharp">
    <type fullname="YourNamespace.Position" preserve="all" />
    <type fullname="YourNamespace.RobotCommand" preserve="all" />
  </assembly>
</linker>
```
---

## License

[MIT License](LICENSE)
