# PullSub
![Unity 2022.3+](https://img.shields.io/badge/Unity-2022.3%2B-black)
![.NET Standard 2.1](https://img.shields.io/badge/.NET-Standard%202.1-blue)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Test](https://github.com/toshi0515/PullSub/actions/workflows/test.yml/badge.svg)](https://github.com/toshi0515/PullSub/actions/workflows/test.yml)

Pull-style typed Pub/Sub for Unity and .NET - MQTT built-in, transport-agnostic design

- For real-time messaging: Get the latest data efficiently — best for digital twin, robotics, smart home, etc.
- Easy and type-safe access from the Unity main thread while transport/serialization layers run in other threads
- Customizable codec and serializer — can customize payload structure to match specific formats such as OPC UA, and can optimize GC by swapping serializers (e.g. MessagePack)
- Transport-agnostic design — swap MQTT for UDP or any custom protocol by implementing `ITransport` interface
- Share code between Unity and edge devices such as Raspberry Pi — the Core layer has no Unity dependency

---

## What PullSub provides

For example, when visualizing real-time robot positions in Unity from JSON messages over MQTT, managing thread safety and deserialization for every update is repetitive and error-prone. PullSub replaces this manual overhead with a clean, type-safe pull API.

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
    public static readonly ITopic<Position> Position
        = PullSubTopic.Create<Position>("robot/position");
}
```

**Unity**
```csharp
public class RobotController : MonoBehaviour
{
    [SerializeField] private PullSubMqttClient _client;

    private DataSubscription<Position> _positionSubscription;

    private async void Start()
    {
        _positionSubscription = await _client.Runtime.SubscribeDataAsync(Topics.Position);
        _positionSubscription?.AddTo(this); // Automatic disposal on OnDestroy
    }

    private void Update()
    {
        // Access the latest value
        if (_positionSubscription?.TryGet(out var pos) == true)
        {
            transform.position = new Vector3(pos.X, pos.Y, pos.Z);
        }
    }
}
```

**Why PullSub?** 
- Because callback-driven MQTT libraries force you to manage thread safety, decoding, and data lifetime yourself. PullSub handles all of that — just define your type and pull the latest value whenever you need it.

**Why pull, not push?** 
- Unity's game loop polls state every frame — transform.position, Input.GetAxis, Physics — PullSub fits this model naturally. No callbacks, no queues to drain manually, no lock.

**When PullSub is not a good fit** 
- Wildcard Topics: PullSub does not support wildcard subscriptions (+ or #). If you need wildcards, use MQTTnet directly.
- MQTT v5 Features: PullSub focuses on the MQTT v3.1.1 baseline. If you need advanced MQTT v5 features, use MQTTnet directly.

---

## Two APIs for Two Different Problems

**Data API keeps the latest value. Queue API keeps every message**

### Data API — for *state*

Use this for data where only the latest value matters, such as IoT sensor readings, digital twin state, or robot pose.

```csharp
// Poll in Update()
    private void Update()
    {
        // Access the latest value
        if (_positionSubscription?.TryGet(out var pos) == true)
        {
            transform.position = new Vector3(pos.X, pos.Y, pos.Z);
        }
    }
```

### Queue API — for *events*

Register synchronous or asynchronous handlers to process every incoming message in order. This event-driven API ensures sequential execution without missing any data — perfect for commands, logs, or transactions.

```csharp
// Handle commands one by one
var queueSubscription = await runtime.SubscribeQueueAsync(
    Topics.Command,
    async (command, ct) => await robot.ExecuteAsync(command, ct));
```

---

## Getting Started — Unity

### Install UniTask

PullSub requires [UniTask](https://github.com/Cysharp/UniTask) (2.x) as a Unity package.

### Install DLLs

Installing DLLs via [NuGet for Unity](https://github.com/GlitchEnzo/NuGetForUnity) is recommended, or place them manually in your `Assets/Plugins` folder.

<img width="571" height="556" alt="image" src="https://github.com/user-attachments/assets/7fa6bc3a-574e-46c7-aa81-c3b1129852a1" />

**Core**
- `System.Text.Json.dll` (8.0.x)
- `System.Text.Encodings.Web.dll` (8.0.x)
- `Microsoft.Bcl.AsyncInterfaces.dll` (8.0.x)

**Mqtt**
- `MQTTnet.dll` (4.3.x)

### Install PullSub

Install via UPM with a git URL:
```
https://github.com/toshi0515/PullSub.git?path=src/com.toshi0515.pullsub
```

## Getting Started — .NET (NuGet)

**Requirements:** .NET 6.0 or later

Install the core package:

```
dotnet add package PullSub.Core
```

To use the built-in MQTT transport, also install:

```
dotnet add package PullSub.Mqtt
```

**Note:** `PullSub.Core` targets `netstandard2.1` and is compatible with
.NET 6, 7, 8, and later. `PullSub.Mqtt` depends on MQTTnet 4.3.x, which is automatically installed as a dependency.

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
    public static readonly ITopic<Position> Position
        = PullSubTopic.Create<Position>("robot/position");
}
```

### 3. Subscribe and Access Data

```csharp
using UnityEngine;
using PullSub.Core;
using PullSub.Bridge;

public class RobotController : MonoBehaviour
{
    [SerializeField] private PullSubMqttClient _client;

    private SubscriptionGroup _group; // SubscriptionGroup manages multiple subscriptions lifecycle
    private DataSubscription<Position> _positionSubscription;

    private async void Start()
    {
        // Create a subscription group (manages multiple subscriptions)
        _group = _client.Runtime.CreateGroup()
            .AddTo(this);  // Automatic cleanup on OnDestroy

        // Subscribe data: returns DataSubscription<T>
        _positionSubscription = await _group.SubscribeDataAsync(Topics.Position);
    }

    private void Update()
    {
        if (_positionSubscription?.TryGet(out var pos) != true) return;
        transform.position = new Vector3(pos.X, pos.Y, pos.Z);
    }
    // SubscriptionGroup automatically unsubscribes on OnDestroy via AddTo(this)
}
```

**Subscription Group:**
- `SubscriptionGroup` groups multiple subscriptions for batch cleanup
- Built-in duplicate-topic prevention within a group
- Automatic lifecycle binding with `AddTo(this)`
- Safe for concurrent subscription requests

### 4. Register Handler Using the Queue API with Subscription Group

```csharp
public class CommandReceiver : MonoBehaviour
{
    [SerializeField] private PullSubMqttClient _client;
    private SubscriptionGroup _group;

    private async void Start()
    {
        // Create a group and bind to MonoBehaviour lifecycle
        _group = _client.Runtime.CreateGroup()
            .AddTo(this);  // Automatic cleanup on OnDestroy

        // Register handler through group (tracked and auto-unsubscribed)
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

        // Or use the extension method for Unity main thread
        await _group.SubscribeQueueOnMainThreadAsync(
            Topics.Command,
            (command) =>
            {
                robot.Execute(command);
            });

        // Later, if needed, stop just this handler while keeping other subscriptions active
        // await group.UnsubscribeQueueAsync(Topics.Command.TopicName, cancellationToken);

        // SubscriptionGroup automatically cleans up all handlers and subscriptions on OnDestroy
    }
}
```

**Or use Runtime directly**
```csharp
private QueueSubscription _sub;

private async void Start()
{
    _sub = await _client.Runtime.SubscribeQueueAsync(
        Topics.Command,
        async (command, ct) =>
        {
            await robot.ExecuteAsync(command, ct);
        },
        destroyCancellationToken);

    _sub?.AddTo(this); // Automatic cleanup on OnDestroy
}
```

### 5. Publish Data

```csharp
var position = new Position
{
    X = transform.position.x,
    Y = transform.position.y,
    Z = transform.position.z
};

await _client.Runtime.PublishDataAsync(Topics.Position, position);
```

### 6. Publish from IObservable<T> (Publisher-only helper)

Use `ToPublisher()` to connect any `IObservable<T>` stream to PullSub publish.
You can throttle or filter the stream before publishing. This can prevent excessive network traffic.

This helper uses `IObservable<T>` / `IObserver<T>` from BCL, so it works with
R3, UniRx, or System.Reactive streams. Use your favorite reactive stream library.

```csharp
sensorStream
    .ThrottleFirst(TimeSpan.FromMilliseconds(100))
    .Subscribe(_client.Runtime.ToPublisher(
    Topics.Position,
    onError: ex => Debug.LogError($"Publish failed: {ex.Message}")));
```

Notes:

- `ToPublisher()` serializes publish calls so that only one publish runs at a time.
- After source `OnCompleted` / `OnError`, subsequent `OnNext` values are ignored.
- If `onError` is omitted, publish failures are forwarded to PullSubRuntime logging callbacks.
- Runtime disposal does not invoke `onError` (treated as graceful termination).
- If source `OnNext` rate exceeds publish completion rate, pending work can accumulate.
- Receive-side `ToObservable()` is intentionally not provided by the library.
    For receive reactive flows, compose from polling (`Update` / `EveryUpdate`) or use Queue API.

### 7. Request-Reply (experimental)

Use this when you need one response per request, such as command execution acknowledgement,
query-response operations, or remote validation.

Request-Reply in current PullSub is designed for small/medium RPC payloads with safety guards.
If you need large blob transfer or stream semantics, prefer Queue/Data APIs or a dedicated file/stream transport.

Define a request topic once, then use `RequestAsync` from a requester and `RespondAsync` from a responder.

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

// Requester
var sum = await _client.Runtime.RequestAsync(
    RequestTopics.Add,
    new AddRequest { A = 20, B = 22 },
    timeout: TimeSpan.FromSeconds(1));

// Responder (runtime or group)
await _client.Runtime.RespondAsync(
    RequestTopics.Add,
    async (req, ct) =>
    {
        await Task.Yield();
        return req.A + req.B;
    });

// Note:
// Under QoS 1, a broker can redeliver the same request message.
// RequestAsync completes once per correlationId, but responder handlers can run multiple times.
// Keep responder side effects idempotent when using AtLeastOnce.

// Security model (current behavior):
// - Responder validates replyTo strictly against its own replyTopicPrefix.
// - Allowed format is "{replyTopicPrefix}/{32-char lowercase hex}" only.
// - Invalid request envelope / invalid replyTo are dropped without faulting responder subscription.
// - Remote error text returned to requester is fixed as "Remote handler failed.".
```

### 8. Runtime Monitoring (Editor)

Use the monitor window to inspect all `PullSubMqttClient` instances in open scenes:

- Open: `Tools > PullSub > Debug Monitor`

**Sections:**

- **Overview**: Scene name, broker address, connection state (Connected/Disconnected), Runtime state, queue handler diagnostics status
- **Connection**: Client ID and policy, reconnect attempts, current delay, next retry time, last failure reason
- **Request/Reply**: Pending request count, timeout/failure counters, reply inbox subscription status
- **Topics**: Active topic list with subscriber counts (Data/Queue), message receive rate, data latency, QoS level
- **Groups**: Associated `SubscriptionGroup` instances and their subscriptions
- **Recently Removed**: Topics unsubscribed in the last 10 seconds

**Warning Color (Orange) Indicates:**

- Queue handler diagnostics are disabled
- Connection is disconnected or reconnecting
- Request/Reply failures detected (timeout, publish failed, setup failed, invalid reply-to, connection lost)
- Data latency ≥ 1000ms (real-time performance degradation)
- Inbound message drops due to oversized payloads

**Notes:**

- The monitor focuses on operational state, not payload contents.
- Handler method names are intentionally not displayed.
- Refresh interval: 0.3 seconds for near real-time updates.


---

## Quick Start — .NET

Instantiate `PullSubRuntime` directly.

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

// Create a group and subscribe
await using var group = runtime.CreateGroup();
var subscription = await group.SubscribeDataAsync(
    Topics.Position,
    cancellationToken: cts.Token);

// 100 Hz loop
while (!cts.IsCancellationRequested)
{
    if (subscription.TryGet(out var pos))
    {
        var command = controller.Calculate(pos);
        await robot.SendCommandAsync(command, cts.Token);
    }
    await Task.Delay(10, cts.Token);
}
// group and subscription automatically disposed via await using
```

**Subscription Group:**
- `SubscriptionGroup` groups multiple subscriptions for atomic cleanup
- `await using` ensures async disposal completes (Network unsubscribe waits)
- Type-safe via generic `SubscribeDataAsync<T>`
- Duplicate topics in same group are prevented

### Diagnostics (.NET)

Use `GetDiagnostics()` to snapshot runtime/topic state without Unity Editor:

```csharp
using PullSub.Core;
using System;

var diagnostics = runtime.GetDiagnostics();
var snapshot = diagnostics.GetSnapshot(maxTopics: 128);

Console.WriteLine(
    $"State={snapshot.State} Connected={snapshot.IsConnected} Topics={snapshot.Topics.Length}");

foreach (var topic in snapshot.Topics)
{
    Console.WriteLine(
        $"{topic.Topic} HasValue={topic.HasValue} Receive={topic.DataReceiveCount} Dropped={topic.QueueDroppedCount}");
}

// Optional helper
runtime.LogSnapshot(Console.WriteLine, maxTopics: 128);
```

Notes:

- Queue handler fault/active metrics may be unavailable outside Unity Editor. Check `snapshot.HasQueueHandlerDiagnostics`.
- Snapshot polling is not free. For production, start with low frequency such as 1 Hz.
- OpenTelemetry / HTTP endpoint integration is intentionally out of scope for this library. Build it on top of snapshot data.

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
    public static readonly ITopic<RobotCommand> RobotCommand
        = PullSubTopic.Create<RobotCommand>("robot/command");
}
```

### Use Source Generation for IL2CPP and Performance (Best Practice)

For Unity IL2CPP or high-performance .NET applications, use the System.Text.Json Source Generator. This builds a reflection-free codec that prevents code stripping without requiring a `link.xml` file.

```csharp
// 1. Define your SerializerContext (AOT-safe)
[JsonSerializable(typeof(RobotCommand))]
public partial class AppJsonContext : JsonSerializerContext { }

// 2. Pass JsonTypeInfo to the Create method
public static class Topics
{
    public static readonly ITopic<RobotCommand> RobotCommand
        = PullSubTopic.Create("robot/command", AppJsonContext.Default.RobotCommand);
    
    // Also supports Flat JSON
    // = PullSubTopic.CreateFlat("robot/command", AppJsonContext.Default.RobotCommand);
}

```

> [!NOTE]
> **Enabling Source Generators in Unity:**
> To activate the `System.Text.Json` Source Generator, you must manually mark the DLL as a Roslyn Analyzer:
> 1. Search for `System.Text.Json.SourceGeneration` in your Project window.
> 2. In the Inspector, click the **Asset Labels** (tag icon) at the bottom right and add the **RoslynAnalyzer** label.
> 3. In the **Select platforms for plugin** section, **uncheck all platforms**. 
>
> This ensures that the generator runs during the compilation process but is not included as a redundant DLL in your final application bundle.


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
    FlatJsonPayloadCodec<Position>.Default);

// Custom codec (e.g. MemoryPack)
PullSubTopic.Create<Position>("robot/position",
    new MemoryPackPayloadCodec<Position>());
```

Always share the same codec between Publisher and Subscriber. Defining the codec inside `ITopic<T>` is the recommended way to enforce this.

For class payloads used in Data API, codecs must support in-place decode (`IPayloadInPlaceCodec<T>`).
Built-in JSON codecs already support this contract.

Request-Reply serialization contract (v1):

- Request/Response payload codec is swappable via `IRequestTopic<TRequest, TResponse>` (`requestCodec` / `responseCodec`).
- Request-Reply envelope itself is fixed JSON in v1 (`correlationId`, `replyTo`, `sentUtc`, `deadlineUtc`, `status`, `errorMessage`, `respondedUtc`).
- For built-in JSON/FlatJson codecs, payload is inlined as `request` / `response` fields.
- For custom non-JSON codecs, payload is encoded into `requestPayload` / `responsePayload` (base64).
- Mixed codec definitions on the same request topic are not supported. Share one request topic definition between requester and responder.

### Notes on Missing Members

- By default, PullSub uses System.Text.Json behavior that serializes/deserializes public properties.
- If a payload is missing a member, that member is deserialized as its default value.
    - `int` -> `0`, `float` -> `0`, `bool` -> `false`, reference type or nullable type -> `null`
- PullSub Data API does not merge missing members with previous cached values.
    A successfully decoded payload replaces the cached value for the topic.

To avoid accidental defaults caused by schema drift, share the same DTO and topic definitions between Publisher and Subscriber.

---

## MQTT Connection Options

Connection options are configured via `MqttConnectionOptions` and passed to `MqttTransport`. In Unity, these can also be set through the `PullSubMqttClient` Inspector.

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

var transport = new MqttTransport(
    brokerHost: "mqtt.example.com",
    brokerPort: 8883,
    connectionOptions: connectionOptions);
```

### WebSocket

```csharp
var connectionOptions = new MqttConnectionOptions(
    transport: new MqttTransportOptions(
        kind: MqttTransportKind.Wss,
        webSocketPath: "/mqtt"));

// Security guard:
// Transport=Ws with TLS enabled is rejected at MqttConnectionOptions construction time.
// Use Transport=Wss when TLS is required.
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

### Runtime Lifecycle

```csharp
Task StartAsync(CancellationToken ct = default)
Task WaitUntilConnectedAsync(CancellationToken ct = default)
Task DisconnectAsync(CancellationToken ct = default)
ValueTask DisposeAsync()

PullSubState State { get; }
// NotStarted | Starting | Ready | Disconnected | Reconnecting | ResubscribePending | Stopped | Disposed
bool IsReady { get; }
```

### Subscription Group Management

`SubscriptionGroup` groups multiple subscriptions with duplicate prevention and atomic cleanup.

```csharp
// Create a group
SubscriptionGroup CreateGroup(this PullSubRuntime runtime)

// Clean up all subscriptions in group (async, waits for all unsubscribes)
ValueTask DisposeAsync()

// Synchronous dispose (fire-and-forget unsubscribes)
void Dispose()

// Unity: Bind group to MonoBehaviour lifecycle
SubscriptionGroup AddTo(this SubscriptionGroup group, MonoBehaviour behaviour)

// General: Bind group to any cancellation token lifecycle
SubscriptionGroup AddTo(this SubscriptionGroup group, CancellationToken cancellationToken)
```

### Data API

Use the Data API for accessing the latest state. Subscribing through `SubscriptionGroup` is recommended for automatic cleanups.

**Subscribe / Unsubscribe:**
```csharp
// Through SubscriptionGroup (Recommended)
// Returns DataSubscription<T>
Task<DataSubscription<T>> group.SubscribeDataAsync<T>(
    ITopic<T> topic,
    PullSubQualityOfServiceLevel subscribeQos = AtLeastOnce,
    CancellationToken ct = default)

Task<PullSubUnsubscribeResult> group.UnsubscribeDataAsync(string topic, CancellationToken ct = default)

// Direct Runtime Access (Unmanaged)
Task<DataSubscription<T>> runtime.SubscribeDataAsync<T>(...)
Task runtime.UnsubscribeDataAsync<T>(...)
```

**Data Access (`DataSubscription<T>`):**
```csharp
string Topic { get; }
T Value { get; }
bool HasValue { get; }
bool IsValid { get; }
DateTime TimestampUtc { get; }
DateTime TimestampLocal { get; }

T GetValueOrDefault(T fallback)
bool TryGet(out T value)
bool TryGet(out T value, out DateTime timestampUtc)

// Unsubscribe this specific topic directly
Task<PullSubUnsubscribeResult> UnsubscribeAsync(CancellationToken ct = default)
```

### Queue API

Use the Queue API for event-driven, sequential message processing.

**Register Handlers:**
```csharp
// Through SubscriptionGroup (Recommended - tracks handler lifecycle automatically)
// Overloads available for Func<T, ValueTask>, Action<T>, and with/without CancellationToken.
Task<QueueSubscription> group.SubscribeQueueAsync<T>(
    ITopic<T> topic,
    Func<T, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)

Task<QueueSubscription> group.SubscribeQueueAsync<T>(
    ITopic<T> topic,
    QueueOptions options,
    Func<T, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)

// Unity Main Thread execution (Bridge extension)
Task<QueueSubscription> group.SubscribeQueueOnMainThreadAsync<T>(...)

// Direct Runtime Access (Runtime-level, manual lifecycle)
Task<QueueSubscription> runtime.SubscribeQueueAsync<T>(...)
```

**Unsubscribe Queue Handlers:**
```csharp
// 1. Through SubscriptionGroup: Stop ALL handlers for a specific topic in the group
await group.UnsubscribeQueueAsync(topic, cancellationToken);

// 2. Through QueueSubscription: Stop a specific handler using its returned registration object
await queueSubscription.UnsubscribeAsync(cancellationToken);

// 3. Dispose the entire group to stop everything at once
await group.DisposeAsync();
```

**Queue Handle (`QueueSubscription`):**
```csharp
string Topic { get; }
Task Completion { get; } // Completes when handler loop exits; faults if handler throws

Task<PullSubUnsubscribeResult> UnsubscribeAsync(CancellationToken ct = default)
ValueTask DisposeAsync()
void Dispose()
```

**PullSubUnsubscribeResult Enum:**
```csharp
Success = 0,           // Unsubscribe succeeded
AlreadyCanceled = 1,   // Already unsubscribed or disposed (idempotent)
Failed = 2,            // Unsubscribe failed
```

### Subscription Lifecycle Binding (`AddTo`)

You can bind the lifecycle of any subscription or group to a Unity `MonoBehaviour` or a `CancellationToken`. This works for **both** `SubscriptionGroup` and Direct Runtime objects (`DataSubscription<T>`, `QueueSubscription`).

```csharp
// 1. Bind a SubscriptionGroup
var group = runtime.CreateGroup().AddTo(this);

// 2. Bind a direct Data subscription
var data = await runtime.SubscribeDataAsync(Topics.Position);
data.AddTo(this);

// 3. Bind a direct Queue handler
var queue = await runtime.SubscribeQueueAsync(Topics.Command, handler);
queue.AddTo(destroyCancellationToken);
```


### Publish API

```csharp
Task PublishDataAsync<T>(ITopic<T> topic, T value,
    PullSubQualityOfServiceLevel qos = AtMostOnce,
    bool retain = false,
    CancellationToken ct = default)

// Publish raw bytes without codec
Task PublishRawAsync(string topic, byte[] payload,
    PullSubQualityOfServiceLevel qos = AtMostOnce,
    bool retain = false,
    CancellationToken ct = default)

// Create an observer that publishes messages
IObserver<T> ToPublisher<T>(
    this PullSubRuntime runtime,
    ITopic<T> topic,
    PullSubQualityOfServiceLevel qos = AtMostOnce,
    bool retain = false,
    Action<Exception> onError = null)
```

### Data Arrival and Default Semantics

Before the first message arrives for an active `DataSubscription<T>`:

- `HasValue` is `false`
- `Value` returns `default(T)` (which may be `null` for classes)
- `TimestampUtc` returns `default(DateTime)`

**Best Practice for non-blocking flows**: Always check `HasValue` or use `TryGet` before reading `Value`.
**Strict flow (blocking)**: Use `WaitForFirstDataAsync` only when your application requires strict initialization gating.

### Experimental APIs

**Request-Reply (RPC)**
PullSub includes an experimental Request-Reply API (`runtime.RequestAsync` and `runtime.RespondAsync`) for RPC-style communication over MQTT. As the primary focus of this library is Pub/Sub, this API is subject to change in future releases. It includes advanced guard options (`PullSubRuntimeOptions`), timeout boundaries, and diagnostic counters.
Runtime guard currently exposes `MaxInboundPayloadBytes` in inspector (`Runtime Guard`), while other guard values use library defaults.

---

## IL2CPP — Preventing Code Stripping (When not using Source Generator)

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

## Maintenance Status

This library is maintained by a student developer on a best-effort basis.
Response times may vary. Contributions and bug reports are welcome.

This project was developed as a personal learning project, with assistance
from LLM-based tools (e.g. GitHub Copilot, Claude) during development.

## License

[MIT License](LICENSE)