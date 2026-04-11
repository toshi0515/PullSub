# PullSub
![Unity 2022.3+](https://img.shields.io/badge/Unity-2022.3%2B-black)
![.NET Standard 2.1](https://img.shields.io/badge/.NET-Standard%202.1-blue)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

Pull-style typed Pub/Sub runtime for Unity and .NET. Supports MQTTnet v4.3.x.

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

**Why PullSub?** Because callback-driven MQTT libraries force you to manage thread safety, decoding, and data lifetime yourself. PullSub handles all of that — just define your type and pull the latest value whenever you need it.

**Why pull, not push?** Unity's game loop polls state every frame — transform.position, 
Input.GetAxis, Physics — PullSub fits this model naturally. 
No callbacks, no queues to drain manually, no lock.

---

## Two APIs for Two Different Problems

**Data API keeps the latest value. Queue API keeps every message**

### Data API — for *state*

Use this for data where only the latest value matters, such as IoT sensor readings, digital twin state, or robot pose.

Data API is optimized for in-place latest-state reads:

- Use Queue API when you need ordered history or event replay.

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

Use this for data where every message must be processed in order, such as commands, logs, or transactions.

```csharp
// Handle commands one by one
var queueSubscription = await runtime.SubscribeQueueAsync(
    Topics.Command,
    async (command, ct) => await robot.ExecuteAsync(command, ct));
```

Note: You can only add one handler per topic. 

### Request-Reply API — for *RPC-like workflows* (Experimental)

Use this when you need one response per request, such as command execution acknowledgement,
query-response operations, or remote validation.

Request-Reply in current PullSub is designed for small/medium RPC payloads with safety guards.
If you need large blob transfer or stream semantics, prefer Queue/Data APIs or a dedicated file/stream transport.

```csharp
public static class RequestTopics
{
    public static readonly IRequestTopic<MoveRequest, MoveResult> Move
        = RequestTopic.Create<MoveRequest, MoveResult>("robot/move/request");
}

// Requester side
var result = await runtime.RequestAsync(
    RequestTopics.Move,
    new MoveRequest { Distance = 1.25f },
    timeout: TimeSpan.FromSeconds(2));

// Responder side
await using var responder = await runtime.RespondAsync(
    RequestTopics.Move,
    async (request, ct) =>
    {
        await robot.MoveAsync(request.Distance, ct);
        return new MoveResult { Accepted = true };
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

---

## Getting Started (Unity)

### Install UniTask

Install [UniTask](https://github.com/Cysharp/UniTask) as a Unity package.

### Install DLLs

Installing DLLs via [NuGet for Unity](https://github.com/GlitchEnzo/NuGetForUnity) is recommended, or place them manually in your `Assets/Plugins` folder.

<img width="571" height="556" alt="image" src="https://github.com/user-attachments/assets/7fa6bc3a-574e-46c7-aa81-c3b1129852a1" />

**Core**
- `System.Text.Json.dll` (8.0.x)
- `System.Text.Encodings.Web.dll` (8.0.x)
- `Microsoft.Bcl.AsyncInterfaces.dll` (8.0.x)

**Mqtt**
- `MQTTnet.dll` (4.3.x)

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

### 4. Handle Commands Using the Queue API with Subscription Group

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

        // Register Queue handler through group (tracked and auto-unsubscribed)
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
```

### 8. Runtime Monitoring (Editor)

Use the monitor window to inspect all `PullSubMqttClient` instances in open scenes:

- Open: `Tools > PullSub > Debug Monitor`

**Sections:**

- **Overview**: Scene name, broker address, connection state (Connected/Disconnected), Runtime state, queue handler diagnostics status
- **Connection**: Client ID and policy, reconnect attempts, current delay, next retry time, last failure reason
- **Request/Reply**: Pending request count, timeout/failure counters, reply inbox subscription status
- **Topics**: Active topic list with subscriber counts (Data/Queue), message receive rate, data latency, QoS level
- **Contexts**: Associated `PullSubContext` instances and their subscriptions
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

Instantiate `PullSubRuntime` directly. This lets you share code between Unity and edge devices such as Raspberry Pi.

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
    - `int` -> `0`, `float` -> `0`, `bool` -> `false`, reference type -> `null`
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

### Lifecycle

```csharp
Task StartAsync(CancellationToken ct = default)
Task WaitUntilConnectedAsync(CancellationToken ct = default)
Task DisconnectAsync(CancellationToken ct = default)
ValueTask DisposeAsync()

PullSubState State { get; }
// NotStarted | Starting | Ready | Disconnected | Reconnecting | ResubscribePending | Stopped | Disposed
bool IsReady { get; }
```

### Subscription Group API

`SubscriptionGroup` groups multiple subscriptions with duplicate prevention and atomic cleanup.
`SubscriptionGroup` methods internally use Runtime operations, while adding group-scoped duplicate prevention and ownership-based cleanup.

```csharp
// Create a group
SubscriptionGroup CreateGroup(this PullSubRuntime runtime)

// Subscribe through group (recommended, instance methods on SubscriptionGroup).
// Returns DataSubscription<T> directly.
Task<DataSubscription<T>> SubscribeDataAsync<T>(
    ITopic<T> topic,
    PullSubQualityOfServiceLevel subscribeQos = AtLeastOnce,
    CancellationToken ct = default)

// Unsubscribe an individual topic
Task<PullSubUnsubscribeResult> UnsubscribeDataAsync(
    string topic,
    CancellationToken ct = default)

// Clean up all subscriptions in group (async, waits for all unsubscribes)
ValueTask DisposeAsync()

// Synchronous dispose (fire-and-forget unsubscribes)
void Dispose()

// Unity: Bind group to MonoBehaviour lifecycle
SubscriptionGroup AddTo(this SubscriptionGroup group, MonoBehaviour behaviour)

// Unity/General: Bind group to any cancellation token lifecycle
SubscriptionGroup AddTo(this SubscriptionGroup group, CancellationToken cancellationToken)
```

**DataSubscription\<T\>** (returned by `SubscribeDataAsync<T>`):
```csharp
string Topic { get; }

// Data access
T Value { get; }
bool HasValue { get; }
bool IsValid { get; }
DateTime TimestampUtc { get; }
DateTime TimestampLocal { get; }
T GetValueOrDefault(T fallback)
bool TryGet(out T value)
bool TryGet(out T value, out DateTime timestampUtc)

// Unsubscribe this topic
Task<PullSubUnsubscribeResult> UnsubscribeAsync(CancellationToken ct = default)

// Unity: Bind subscription to MonoBehaviour lifecycle
DataSubscription<T> AddTo<T>(this DataSubscription<T> subscription, MonoBehaviour behaviour)

// Unity/General: Bind subscription to any cancellation token lifecycle
DataSubscription<T> AddTo<T>(this DataSubscription<T> subscription, CancellationToken cancellationToken)
```

**PullSubUnsubscribeResult** (enum):
```csharp
Success = 0,           // Unsubscribe succeeded
AlreadyCanceled = 1,   // Already unsubscribed or disposed (idempotent)
Failed = 2,            // Unsubscribe failed
```

**Queue API through SubscriptionGroup** (unified handler lifecycle):
```csharp
// Register a handler loop with group management
Task<QueueSubscription> SubscribeQueueAsync<T>(
    ITopic<T> topic,
    Func<T, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)

Task<QueueSubscription> SubscribeQueueAsync<T>(
    ITopic<T> topic,
    Func<T, ValueTask> handler,
    CancellationToken ct = default)

Task<QueueSubscription> SubscribeQueueAsync<T>(
    ITopic<T> topic,
    Action<T, CancellationToken> handler,
    CancellationToken ct = default)

Task<QueueSubscription> SubscribeQueueAsync<T>(
    ITopic<T> topic,
    Action<T> handler,
    CancellationToken ct = default)

Task<QueueSubscription> SubscribeQueueAsync<T>(
    ITopic<T> topic,
    QueueOptions options,
    Func<T, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)

Task<QueueSubscription> SubscribeQueueAsync<T>(
    ITopic<T> topic,
    QueueOptions options,
    Func<T, ValueTask> handler,
    CancellationToken ct = default)

Task<QueueSubscription> SubscribeQueueAsync<T>(
    ITopic<T> topic,
    QueueOptions options,
    Action<T, CancellationToken> handler,
    CancellationToken ct = default)

Task<QueueSubscription> SubscribeQueueAsync<T>(
    ITopic<T> topic,
    QueueOptions options,
    Action<T> handler,
    CancellationToken ct = default)

// Stop a specific Queue handler (stops only the Queue, Data subscriptions unaffected)
Task<PullSubUnsubscribeResult> UnsubscribeQueueAsync(
    string topic,
    CancellationToken ct = default)

// Unity: Bind Queue handler to MonoBehaviour lifecycle
QueueSubscription AddTo(
    this QueueSubscription registration,
    MonoBehaviour behaviour)

// Unity/General: Bind Queue handler to any cancellation token lifecycle
QueueSubscription AddTo(
    this QueueSubscription registration,
    CancellationToken cancellationToken)
```

**Queue API on Unity main thread** (Bridge extensions):
```csharp
// Available on SubscriptionGroup / PullSubRuntime
// PullSubMqttClient is lifecycle/configuration only. Use _client.Runtime.
Task<QueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
    ITopic<T> topic,
    Func<T, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)

Task<QueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
    ITopic<T> topic,
    Func<T, ValueTask> handler,
    CancellationToken ct = default)

Task<QueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
    ITopic<T> topic,
    Action<T, CancellationToken> handler,
    CancellationToken ct = default)

Task<QueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
    ITopic<T> topic,
    Action<T> handler,
    CancellationToken ct = default)

Task<QueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
    ITopic<T> topic,
    QueueOptions options,
    Func<T, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)

Task<QueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
    ITopic<T> topic,
    QueueOptions options,
    Func<T, ValueTask> handler,
    CancellationToken ct = default)

Task<QueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
    ITopic<T> topic,
    QueueOptions options,
    Action<T, CancellationToken> handler,
    CancellationToken ct = default)

Task<QueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
    ITopic<T> topic,
    QueueOptions options,
    Action<T> handler,
    CancellationToken ct = default)
```

### Data API (Low-level access)

For direct Runtime access without group grouping. Subscription Group API is recommended for most use cases.

```csharp
// Subscribe / Unsubscribe
Task<DataSubscription<T>> SubscribeDataAsync<T>(
    ITopic<T> topic,
    PullSubQualityOfServiceLevel subscribeQos = PullSubQualityOfServiceLevel.AtLeastOnce,
    CancellationToken ct = default)
Task UnsubscribeDataAsync<T>(ITopic<T> topic,
    CancellationToken ct = default)

// Access the latest value
DataSubscription<T> GetDataHandle<T>(ITopic<T> topic)

// Wait for the first message to arrive
Task<T> WaitForFirstDataAsync<T>(ITopic<T> topic,
    CancellationToken ct = default)
```

### Queue API (SubscriptionGroup-Integrated and Direct Runtime Access)

Queue API can be used through `SubscriptionGroup` (recommended for multiple subscriptions lifecycle management) or directly on Runtime.

Low-level polling surface is intentionally minimal: use `ReceiveQueueAsync` for direct queue reads.

**Through SubscriptionGroup** (see Subscription Group API section above for SubscribeQueueAsync):
```csharp
// SubscriptionGroup automatically tracks and unsubscribes Queue handlers on disposal
await using var group = runtime.CreateGroup();
var registration = await group.SubscribeQueueAsync(
    topic,
    async (message, ct) => { /* handle message */ },
    ct);

// Stop a specific Queue handler (All Data subscriptions in group remain active)
await group.UnsubscribeQueueAsync(topic, cancellationToken);

// Dispose all handlers and subscriptions in group
await group.DisposeAsync();
```

**Direct Runtime Access** (low-level, manual lifecycle):
```csharp
// Subscribe / Unsubscribe manually
Task SubscribeQueueAsync(string topic, QueueOptions options,
    PullSubQualityOfServiceLevel subscribeQos = AtLeastOnce,
    CancellationToken ct = default)
Task UnsubscribeQueueAsync(string topic, CancellationToken ct = default)

// Receive messages
Task<QueueMessage> ReceiveQueueAsync(string topic,
    CancellationToken ct = default)
Task<T> ReceiveQueueAsync<T>(ITopic<T> topic,
    CancellationToken ct = default)

// Register a handler loop (Runtime-level, not group-tracked)
Task<QueueSubscription> SubscribeQueueAsync<T>(
    ITopic<T> topic,
    Func<T, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)

Task<QueueSubscription> SubscribeQueueAsync<T>(
    ITopic<T> topic,
    Func<T, ValueTask> handler,
    CancellationToken ct = default)

Task<QueueSubscription> SubscribeQueueAsync<T>(
    ITopic<T> topic,
    Action<T, CancellationToken> handler,
    CancellationToken ct = default)

Task<QueueSubscription> SubscribeQueueAsync<T>(
    ITopic<T> topic,
    Action<T> handler,
    CancellationToken ct = default)

Task<QueueSubscription> SubscribeQueueAsync<T>(
    ITopic<T> topic,
    QueueOptions options,
    Func<T, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)

Task<QueueSubscription> SubscribeQueueAsync<T>(
    ITopic<T> topic,
    QueueOptions options,
    Func<T, ValueTask> handler,
    CancellationToken ct = default)

Task<QueueSubscription> SubscribeQueueAsync<T>(
    ITopic<T> topic,
    QueueOptions options,
    Action<T, CancellationToken> handler,
    CancellationToken ct = default)

Task<QueueSubscription> SubscribeQueueAsync<T>(
    ITopic<T> topic,
    QueueOptions options,
    Action<T> handler,
    CancellationToken ct = default)
```

### Request-Reply API (experimental)

```csharp
// Define typed request/response topic
IRequestTopic<TRequest, TResponse> RequestTopic.Create<TRequest, TResponse>(
    string requestTopicName)

IRequestTopic<TRequest, TResponse> RequestTopic.Create<TRequest, TResponse>(
    string requestTopicName,
    IPayloadCodec<TRequest> requestCodec,
    IPayloadCodec<TResponse> responseCodec)

// Request from runtime
Task<TResponse> RequestAsync<TRequest, TResponse>(
    this PullSubRuntime runtime,
    IRequestTopic<TRequest, TResponse> topic,
    TRequest request,
    CancellationToken ct = default)

Task<TResponse> RequestAsync<TRequest, TResponse>(
    this PullSubRuntime runtime,
    IRequestTopic<TRequest, TResponse> topic,
    TRequest request,
    TimeSpan timeout,
    PullSubQualityOfServiceLevel publishQos = AtLeastOnce,
    CancellationToken ct = default)

// Request from group
Task<TResponse> RequestAsync<TRequest, TResponse>(
    this SubscriptionGroup group,
    IRequestTopic<TRequest, TResponse> topic,
    TRequest request,
    CancellationToken ct = default)

Task<TResponse> RequestAsync<TRequest, TResponse>(
    this SubscriptionGroup group,
    IRequestTopic<TRequest, TResponse> topic,
    TRequest request,
    TimeSpan timeout,
    PullSubQualityOfServiceLevel publishQos = AtLeastOnce,
    CancellationToken ct = default)

// Respond from runtime
Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
    this PullSubRuntime runtime,
    IRequestTopic<TRequest, TResponse> topic,
    Func<TRequest, CancellationToken, ValueTask<TResponse>> handler,
    CancellationToken ct = default)

Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
    this PullSubRuntime runtime,
    IRequestTopic<TRequest, TResponse> topic,
    Func<TRequest, RequestContext, CancellationToken, ValueTask<TResponse>> handler,
    CancellationToken ct = default)

Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
    this PullSubRuntime runtime,
    IRequestTopic<TRequest, TResponse> topic,
    Func<TRequest, ReplySender<TResponse>, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)

Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
    this PullSubRuntime runtime,
    IRequestTopic<TRequest, TResponse> topic,
    Func<TRequest, RequestContext, ReplySender<TResponse>, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)

Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
    this PullSubRuntime runtime,
    IRequestTopic<TRequest, TResponse> topic,
    QueueOptions options,
    Func<TRequest, CancellationToken, ValueTask<TResponse>> handler,
    CancellationToken ct = default)

Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
    this PullSubRuntime runtime,
    IRequestTopic<TRequest, TResponse> topic,
    QueueOptions options,
    Func<TRequest, RequestContext, CancellationToken, ValueTask<TResponse>> handler,
    CancellationToken ct = default)

Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
    this PullSubRuntime runtime,
    IRequestTopic<TRequest, TResponse> topic,
    QueueOptions options,
    Func<TRequest, ReplySender<TResponse>, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)

Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
    this PullSubRuntime runtime,
    IRequestTopic<TRequest, TResponse> topic,
    QueueOptions options,
    Func<TRequest, RequestContext, ReplySender<TResponse>, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)

// Respond from group
Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
    this SubscriptionGroup group,
    IRequestTopic<TRequest, TResponse> topic,
    Func<TRequest, CancellationToken, ValueTask<TResponse>> handler,
    CancellationToken ct = default)

Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
    this SubscriptionGroup group,
    IRequestTopic<TRequest, TResponse> topic,
    Func<TRequest, RequestContext, CancellationToken, ValueTask<TResponse>> handler,
    CancellationToken ct = default)

Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
    this SubscriptionGroup group,
    IRequestTopic<TRequest, TResponse> topic,
    Func<TRequest, ReplySender<TResponse>, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)

Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
    this SubscriptionGroup group,
    IRequestTopic<TRequest, TResponse> topic,
    Func<TRequest, RequestContext, ReplySender<TResponse>, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)

Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
    this SubscriptionGroup group,
    IRequestTopic<TRequest, TResponse> topic,
    QueueOptions options,
    Func<TRequest, CancellationToken, ValueTask<TResponse>> handler,
    CancellationToken ct = default)

Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
    this SubscriptionGroup group,
    IRequestTopic<TRequest, TResponse> topic,
    QueueOptions options,
    Func<TRequest, RequestContext, CancellationToken, ValueTask<TResponse>> handler,
    CancellationToken ct = default)

Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
    this SubscriptionGroup group,
    IRequestTopic<TRequest, TResponse> topic,
    QueueOptions options,
    Func<TRequest, ReplySender<TResponse>, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)

Task<QueueSubscription> RespondAsync<TRequest, TResponse>(
    this SubscriptionGroup group,
    IRequestTopic<TRequest, TResponse> topic,
    QueueOptions options,
    Func<TRequest, RequestContext, ReplySender<TResponse>, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)
```

Request-Reply notes:

- `StartAsync` must be completed before calling `RequestAsync` / `RespondAsync`.
- `timeout` is authoritative completion timing for requester.
- Responder receives `DeadlineUtc` in request envelope as advisory metadata.
- Convenience responder overloads (`Func<TRequest, ... , ValueTask<TResponse>>`) skip reply publish when the request is already expired after handler execution.
- One request completes with the first matching response (`CorrelationId`), later duplicates are discarded.
- If transport disconnects or runtime is disposed while waiting, pending requests fail fast.
- `ReplySender<TResponse>` is one-shot (`SendAsync` or `SendErrorAsync` once).
- Sender-based responder overloads are low-level and may propagate handler/send exceptions to Queue subscription completion.
- Responder validates `replyTo` against its own `replyTopicPrefix` and accepts only `{replyTopicPrefix}/{32-char lowercase hex}`.
- Uppercase hex, wildcard topics, wrong prefix, and oversized `replyTo` are dropped to keep responder subscriptions alive.
- Request envelopes are decoded from raw payload. Malformed envelopes are dropped and do not fault responder subscriptions.
- Inbound oversize payloads are dropped before decode (`MaxInboundPayloadBytes`, default 1 MB).
- Convenience responder overloads return fixed remote error text (`"Remote handler failed."`) to avoid leaking server internals.
- Use diagnostics counters (`snapshot.Request.InvalidReplyToDropCount`, `snapshot.InboundOversizeDropCount`) for abuse detection.
- Prefer `SubscriptionGroup.RespondAsync(...)` for component-scoped lifecycle management; use `PullSubRuntime.RespondAsync(...)` for low-level/manual lifecycle control.
- For one-way command/event flows (no reply expected), use Queue API with `PublishDataAsync` / `PublishRawAsync` instead of Request-Reply.
- Reply inbox topic is generated as `{replyTopicPrefix}/{runtime nonce}`. For high-frequency workloads, keep `replyTopicPrefix` short.

Request failure exception:

```csharp
PullSubRequestException.FailureKind

// enum PullSubRequestFailureKind
Timeout
ConnectionLost
RuntimeDisposed
SetupFailed
PublishFailed
PayloadDecodeFailed
RemoteError
```

Runtime request options:

```csharp
var runtime = new PullSubRuntime(
    transport,
    requestOptions: new RequestOptions(
        replyTopicPrefix: "pullsub/reply",
        inboxIdleTimeoutSeconds: 60,
        replyInboxQueueDepth: 256,
        maxPendingRequests: 1024));
```

Runtime guard options (security limits):

```csharp
var runtime = new PullSubRuntime(
    transport,
    runtimeOptions: new PullSubRuntimeOptions(
        maxInboundPayloadBytes: 1_048_576,
        maxReplyToLength: 512,
        maxCorrelationIdLength: 128,
        invalidReplyToLogInterval: TimeSpan.FromSeconds(60),
        invalidReplyToAggregateThreshold: 10,
        inboundOversizeAggregateThreshold: 10,
        inboundOversizeLogInterval: TimeSpan.FromSeconds(60)));
```

In Unity Bridge, request options are available under `Request/Reply`.
Runtime guard currently exposes `MaxInboundPayloadBytes` in inspector (`Runtime Guard`), while other guard values use library defaults.

### Publish API

```csharp
Task PublishDataAsync<T>(ITopic<T> topic, T value,
    PullSubQualityOfServiceLevel qos = AtMostOnce,
    bool retain = false,
    CancellationToken ct = default)

Task PublishRawAsync(string topic, byte[] payload,
    PullSubQualityOfServiceLevel qos = AtMostOnce,
    bool retain = false,
    CancellationToken ct = default)

IObserver<T> ToPublisher<T>(
    this PullSubRuntime runtime,
    ITopic<T> topic,
    PullSubQualityOfServiceLevel qos = AtMostOnce,
    bool retain = false,
    Action<Exception> onError = null)
```

### DataSubscription\<T\>

```csharp
T Value { get; }                    // Returns default(T) when HasValue is false
bool HasValue { get; }              // True if at least one message has been received
bool IsValid { get; }               // True when subscribed and HasValue
DateTime TimestampUtc { get; }      // Timestamp from payload, or receive time if absent
DateTime TimestampLocal { get; }    // TimestampUtc converted to local time
T GetValueOrDefault(T fallback)     // Returns fallback instead of default(T) when no data
bool TryGet(out T value)            // Returns false when no data has arrived yet
bool TryGet(out T value, out DateTime timestampUtc)
string Topic { get; }               // The subscribed topic name
```

`DataSubscription<T>` is intended for latest-state pull in game loops.
Avoid keeping `Value` references across frames; use Queue API or explicit snapshots for history.

### Data Arrival and Default Semantics

Before the first message arrives for a topic:

- `HasValue` is `false`
- `Value` returns `default(T)`
- `TimestampUtc` returns `default(DateTime)`

Practical implications:

- If `T` is a class, `Value` may be `null` before first arrival.
- If `T` is a struct, each member is the type default until first arrival.
- Preferred non-blocking flow: use `HasValue` or `TryGet` before reading `Value`.
- Optional strict flow (low-level): call `WaitForFirstDataAsync` only when your app requires first-arrival gating.

### QueueMessage

```csharp
string Topic { get; }
byte[] Payload { get; }
DateTime ReceivedUtc { get; }
```

### QueueSubscription

```csharp
// Topic being handled
string Topic { get; }

// Completes when handler loop exits; faults if handler throws an exception
Task Completion { get; }

// Stop handler and wait for loop to exit (recommended for explicit async cleanup)
Task<PullSubUnsubscribeResult> UnsubscribeAsync(CancellationToken ct = default)

// Async dispose: cancels loop and waits for exit
ValueTask DisposeAsync()

// Sync dispose: requests cancellation and returns immediately (fire-and-forget)
// Fault observer prevents orphan error logs from faults in the handler loop
void Dispose()
```

**Idempotent Semantics:** `UnsubscribeAsync` / `DisposeAsync` can be called multiple times safely.
Subsequent calls after the first will return `AlreadyCanceled` (for `UnsubscribeAsync`) and complete without side-effects.

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