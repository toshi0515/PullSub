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

**Unity (using Context API)**
```csharp
public class RobotController : MonoBehaviour
{
    [SerializeField] private PullSubMqttClient _client;

    private PullSubDataHandle<Position> _positionHandle;

    private async void Start()
    {
        // PullSubMqttClient starts Runtime automatically on OnEnable by default.
        // Create context and bind to MonoBehaviour lifecycle
        var context = _client.Runtime.CreateContext()
            .AddTo(this);  // Automatic disposal on OnDestroy

        // Subscribe and get handle in one step
        var subscription = await context.SubscribeDataAsync(Topics.Position);

        _positionHandle = subscription.Handle;
    }

    private void Update()
    {
        if (_positionHandle?.TryGet(out var pos) != true) return;
        // Access the latest value just like transform.position
        transform.position = new Vector3(pos.X, pos.Y, pos.Z);
    }
    // OnDestroy is handled automatically by AddTo(this)
}
```

**Why PullSub?** Because callback-driven MQTT libraries force you to manage thread safety, decoding, and data lifetime yourself. PullSub handles all of that — just define your type and pull the latest value whenever you need it.

**Why pull, not push?** Unity's game loop polls state every frame — transform.position, 
Input.GetAxis, Physics — PullSub fits this model naturally. 
No callbacks, no queues to drain manually, no lock.

**Context API for Lifecycle Management:** PullSub includes a `PullSubContext` API that groups subscriptions and provides atomic cleanup, automatic duplicate prevention, and seamless MonoBehaviour lifecycle binding with `AddTo()`. See **[Context API](#context-api-recommended-for-data-api)** in the API Reference for details.

PullSub abstracts transport protocols behind `ITransport`. You can use protocols other than MQTT, such as UDP, by implementing `ITransport`.

---

## When PullSub is not the right fit

- **You need callbacks:** Use the Queue API's `SubscribeQueueAsync` instead.
  It provides callback-style message handling with async support, backpressure,
  and drop detection — everything a raw callback pattern lacks.
```csharp
  _registration = await runtime.SubscribeQueueAsync(
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
    if (!_positionHandle.TryGet(out var pos)) return;
    transform.position = new Vector3(pos.X, pos.Y, pos.Z);
}
```

### Queue API — for *events*

Use this for data where every message must be processed in order, such as commands, logs, or transactions.

```csharp
// Handle commands one by one
var registration = await runtime.SubscribeQueueAsync(
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

### 3. Direct Runtime Access with Context API (recommended)

```csharp
using UnityEngine;
using PullSub.Core;
using PullSub.Bridge;

public class RobotController : MonoBehaviour
{
    [SerializeField] private PullSubMqttClient _client;

    private PullSubDataHandle<Position> _positionHandle;

    private async void Start()
    {
        // PullSubMqttClient starts Runtime automatically on OnEnable by default.
        // If Start On Enable is disabled, call Runtime.StartAsync manually.

        // Create a subscription context (manages multiple subscriptions)
        var context = _client.Runtime.CreateContext()
            .AddTo(this);  // Automatic cleanup on OnDestroy

        // Subscribe data: returns a PullSubDataSubscription<T> that wraps the Handle
        var subscription = await context.SubscribeDataAsync(Topics.Position);

        // Obtain handle from subscription and reuse it every frame
        _positionHandle = subscription.Handle;
    }

    private void Update()
    {
        if (_positionHandle?.TryGet(out var pos) != true) return;
        transform.position = new Vector3(pos.X, pos.Y, pos.Z);
    }
    // Context automatically unsubscribes on OnDestroy via AddTo(this)
}
```

**Benefits of Context API:**
- `Context` groups multiple subscriptions for batch cleanup
- Built-in duplicate-topic prevention within a context
- Automatic lifecycle binding with `AddTo(this)` (no manual CancellationTokenSource)
- Safe for concurrent subscription requests

Use `WaitForFirstDataAsync` only when you explicitly need strict first-arrival gating.

### 4. Handle Commands Using the Queue API with Context

```csharp
public class CommandReceiver : MonoBehaviour
{
    [SerializeField] private PullSubMqttClient _client;

    private async void Start()
    {
        // Create a context and bind to MonoBehaviour lifecycle
        var context = _client.Runtime.CreateContext()
            .AddTo(this);  // Automatic cleanup on OnDestroy

        // Register Queue handler through Context (tracked and auto-unsubscribed)
        await context.SubscribeQueueAsync(
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

        // Later, if needed, stop just this handler while keeping other subscriptions active
        // await context.UnsubscribeQueueAsync(Topics.Command.TopicName, cancellationToken);

        // Context automatically cleans up all handlers and subscriptions on OnDestroy
    }
}
```

**Or use Runtime directly** (manual lifecycle management):
```csharp
private PullSubQueueSubscription _registration;

private async void Start()
{
    _registration = await _client.Runtime.SubscribeQueueAsync(
        Topics.Command,
        async (command, ct) =>
        {
            await robot.ExecuteAsync(command, ct);
        },
        destroyCancellationToken);
}

private async void OnDestroy()
{
    if (_registration != null)
        await _registration.DisposeAsync();  // Idempotent: safe to call multiple times
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

---

## Quick Start — .NET

Instantiate `PullSubRuntime` directly without the Unity Bridge. This lets you share code between Unity and edge devices such as Raspberry Pi.

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

// Create a context and subscribe
await using var context = runtime.CreateContext();
await using var subscription = await context.SubscribeDataAsync(
    Topics.Position,
    cancellationToken: cts.Token);

var handle = subscription.Handle;

// 100 Hz loop
while (!cts.IsCancellationRequested)
{
    if (handle.TryGet(out var pos))
    {
        var command = controller.Calculate(pos);
        await robot.SendCommandAsync(command, cts.Token);
    }
    await Task.Delay(10, cts.Token);
}
// context and subscription automatically disposed via await using
```

**Context API Benefits:**
- `context` groups multiple subscriptions for atomic cleanup
- `await using` ensures async disposal completes (Network unsubscribe waits)
- Type-safe via generic `SubscribeDataAsync<T>`
- Duplicate topics in same context are prevented

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

## Lifecycle Management — Best Practices

PullSub provides `AddTo()` extensions and `PullSubContext` to automatically manage subscription and handler lifecycles with MonoBehaviour and cancellation tokens, preventing resource leaks.

### Queue Lease with AddTo (Unity)

For component-owned handlers, register first and then bind the lease to the component lifecycle:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using PullSub.Core;
using PullSub.Bridge;

public sealed class CommandHandler : MonoBehaviour
{
    [SerializeField] private PullSubMqttClient _client;

    private PullSubQueueSubscription _lease;

    private async void Start()
    {
        try
        {
            _lease = await _client.Runtime.SubscribeQueueAsync(
                Topics.RobotCommand,
                ExecuteCommandAsync);

            // Auto-stop on OnDestroy.
            _lease.AddTo(this);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Queue handler registration failed: {ex}");
        }
    }

    private static async ValueTask ExecuteCommandAsync(RobotCommand command, CancellationToken ct)
    {
        switch (command.Type)
        {
            case "move":
                await Robot.MoveAsync(command.Payload, ct);
                break;
            case "rotate":
                await Robot.RotateAsync(command.Payload, ct);
                break;
        }
    }
}
```

### Unified Context for Data Handle and Queue Leases

Use one `PullSubContext` to manage Data subscriptions and Queue handlers together:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using PullSub.Core;
using PullSub.Bridge;

public sealed class RobotController : MonoBehaviour
{
    [SerializeField] private PullSubMqttClient _client;

    private PullSubContext _context;
    private PullSubDataHandle<Position> _positionHandle;

    private async void Start()
    {
        try
        {
            _context = _client.Runtime.CreateContext().AddTo(this);

            var positionSubscription = await _context.SubscribeDataAsync(Topics.Position);
            _positionHandle = positionSubscription.Handle;

            await _context.SubscribeQueueAsync(
                Topics.RobotCommand,
                HandleCommandAsync);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Context initialization failed: {ex}");
        }
    }

    private void Update()
    {
        if (_positionHandle?.TryGet(out var pos) != true)
            return;

        transform.position = new Vector3(pos.X, pos.Y, pos.Z);
    }

    private async ValueTask HandleCommandAsync(RobotCommand command, CancellationToken ct)
    {
        if (_positionHandle?.TryGet(out var currentPos) == true)
            Debug.Log($"Current position before command: {currentPos}");

        await Robot.ExecuteAsync(command, ct);
    }

    public Task<PullSubUnsubscribeResult> StopCommandHandlerAsync(CancellationToken ct = default)
    {
        if (_context == null)
            return Task.FromResult(PullSubUnsubscribeResult.AlreadyCanceled);

        return _context.UnsubscribeQueueAsync(Topics.RobotCommand.TopicName, ct);
    }
}
```

### Multiple Queue Handlers in One Context

Register multiple handlers and keep selective stop capability:

```csharp
private async Task RegisterHandlersAsync(PullSubContext context, CancellationToken ct)
{
    await context.SubscribeQueueAsync(Topics.Command, HandleCommandAsync, cancellationToken: ct);
    await context.SubscribeQueueAsync(Topics.Log, HandleLogAsync, cancellationToken: ct);
    await context.SubscribeQueueAsync(Topics.Alert, HandleAlertAsync, cancellationToken: ct);
}

private Task<PullSubUnsubscribeResult> StopOnlyCommandHandlerAsync(PullSubContext context, CancellationToken ct)
{
    return context.UnsubscribeQueueAsync(Topics.Command.TopicName, ct);
}
```

### Practical Rules

If you bind lifetime with `AddTo(this)`, omit `cancellationToken: destroyCancellationToken` in subscribe/register calls unless you need earlier cancellation than `OnDestroy`.

| Pattern | When to use | Notes |
|---------|-------------|-------|
| **`lease.AddTo(this)`** | Single Queue handler owned by one MonoBehaviour | Simplest Unity lifecycle binding |
| **`context.AddTo(this)`** | Multiple Data + Queue registrations in one component | One owner, one cleanup point |
| **`UnsubscribeQueueAsync(topic)`** | Stop only one Queue handler | Data subscriptions and other handlers keep running |
| **`DisposeAsync()`** | Need completion guarantee for cleanup | Waits until unsubscribe/stop finishes |
| **`Dispose()`** | Fire-and-forget teardown is acceptable | Fast, non-blocking, fault-observed path |

### Runtime Monitoring (Editor)

Use the monitor window to inspect all `PullSubMqttClient` instances in open scenes:

- Open: `Tools > PullSub > Debug Monitor`
- Scope: one panel per `PullSubMqttClient` (`scene`, `host:port`, `client id policy`)
- Topic state: Data / Queue kind, Data `HasValue` + latest timestamp age, Queue active/faulted handler counts, dropped message count
- Transition hint: recently removed topics are shown for the last 10 seconds

Notes:

- The monitor focuses on operational state, not payload value contents.
- Handler method names are intentionally not displayed.

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

PullSubState State { get; }   // NotStarted | Starting | Ready | Reconnecting | Stopped | Disposed
bool IsReady { get; }
```

### Context API (Recommended for Data API)

Contexts group multiple subscriptions with duplicate prevention and atomic cleanup.

```csharp
// Create a context
PullSubContext CreateContext(this PullSubRuntime runtime)

// Subscribe through context (recommended, instance methods on PullSubContext).
// Returns a Subscription<T> containing the Handle and unsubscribe capability.
Task<PullSubDataSubscription<T>> SubscribeDataAsync<T>(
    IPullSubTopic<T> topic,
    PullSubQualityOfServiceLevel subscribeQos = AtLeastOnce,
    CancellationToken ct = default)

// Unsubscribe an individual topic
Task<PullSubUnsubscribeResult> UnsubscribeDataAsync(
    string topic,
    CancellationToken ct = default)

// Clean up all subscriptions in context (async, waits for all unsubscribes)
ValueTask DisposeAsync()

// Synchronous dispose (fire-and-forget unsubscribes)
void Dispose()

// Unity: Bind context to MonoBehaviour lifecycle
PullSubContext AddTo(this PullSubContext context, MonoBehaviour behaviour)

// Unity/General: Bind context to any cancellation token lifecycle
PullSubContext AddTo(this PullSubContext context, CancellationToken cancellationToken)
```

**PullSubDataSubscription\<T\>** (returned by `SubscribeDataAsync<T>`):
```csharp
PullSubDataHandle<T> Handle { get; }  // Access latest value
string Topic { get; }

// Data access delegates to Handle
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
PullSubDataSubscription<T> AddTo<T>(this PullSubDataSubscription<T> sub, MonoBehaviour behaviour)

// Unity/General: Bind subscription to any cancellation token lifecycle
PullSubDataSubscription<T> AddTo<T>(this PullSubDataSubscription<T> sub, CancellationToken cancellationToken)
```

**PullSubUnsubscribeResult** (enum):
```csharp
Success = 0,           // Unsubscribe succeeded
AlreadyCanceled = 1,   // Already unsubscribed or disposed (idempotent)
Failed = 2,            // Unsubscribe failed
```

**Queue API through Context** (unified handler lifecycle):
```csharp
// Register a handler loop with Context management
Task<PullSubQueueSubscription> SubscribeQueueAsync<T>(
    IPullSubTopic<T> topic,
    Func<T, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)

Task<PullSubQueueSubscription> SubscribeQueueAsync<T>(
    IPullSubTopic<T> topic,
    PullSubQueueOptions options,
    Func<T, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)

// Stop a specific Queue handler (stops only the Queue, Data subscriptions unaffected)
Task<PullSubUnsubscribeResult> UnsubscribeQueueAsync(
    string topic,
    CancellationToken ct = default)

// Unity: Bind Queue handler to MonoBehaviour lifecycle
PullSubQueueSubscription AddTo(
    this PullSubQueueSubscription registration,
    MonoBehaviour behaviour)

// Unity/General: Bind Queue handler to any cancellation token lifecycle
PullSubQueueSubscription AddTo(
    this PullSubQueueSubscription registration,
    CancellationToken cancellationToken)
```

**Queue API on Unity main thread** (Bridge extensions):
```csharp
// Available on PullSubContext / PullSubRuntime
// PullSubMqttClient is lifecycle/configuration only. Use _client.Runtime.
Task<PullSubQueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
    IPullSubTopic<T> topic,
    Func<T, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)

Task<PullSubQueueSubscription> SubscribeQueueOnMainThreadAsync<T>(
    IPullSubTopic<T> topic,
    PullSubQueueOptions options,
    Func<T, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)
```

### Data API (Low-level access)

For direct Runtime access without Context grouping. Context API is recommended for most use cases.

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

// Wait for the first message to arrive
Task<T> WaitForFirstDataAsync<T>(IPullSubTopic<T> topic,
    CancellationToken ct = default)
```

### Queue API (Context-Integrated and Direct Runtime Access)

Queue API can be used through Context (recommended for lifecycle management) or directly on Runtime (low-level).

Low-level polling surface is intentionally minimal: use `ReceiveQueueAsync` for direct queue reads.

**Through Context** (see Context API section above for SubscribeQueueAsync):
```csharp
// Context automatically tracks and unsubscribes Queue handlers on disposal
await using var context = runtime.CreateContext();
var registration = await context.SubscribeQueueAsync(
    topic,
    async (message, ct) => { /* handle message */ },
    ct);

// Stop a specific Queue handler (All Data subscriptions in context remain active)
await context.UnsubscribeQueueAsync(topic, cancellationToken);

// Dispose all handlers and subscriptions in context
await context.DisposeAsync();
```

**Direct Runtime Access** (low-level, manual lifecycle):
```csharp
// Subscribe / Unsubscribe manually
Task SubscribeQueueAsync(string topic, PullSubQueueOptions options,
    PullSubQualityOfServiceLevel subscribeQos = AtLeastOnce,
    CancellationToken ct = default)
Task UnsubscribeQueueAsync(string topic, CancellationToken ct = default)

// Receive messages
Task<PullSubQueueMessage> ReceiveQueueAsync(string topic,
    CancellationToken ct = default)

// Register a handler loop (Runtime-level, not Context-tracked)
Task<PullSubQueueSubscription> SubscribeQueueAsync<T>(
    IPullSubTopic<T> topic,
    Func<T, CancellationToken, ValueTask> handler,
    CancellationToken ct = default)
Task<PullSubQueueSubscription> SubscribeQueueAsync<T>(
    IPullSubTopic<T> topic,
    PullSubQueueOptions options,
    Func<T, CancellationToken, ValueTask> handler,
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
bool TryGet(out T value)            // Returns false when no data has arrived yet
bool TryGet(out T value, out DateTime timestampUtc)
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
- Preferred non-blocking flow: use `HasValue` or `TryGet` before reading `Value`.
- Optional strict flow (low-level): call `WaitForFirstDataAsync` only when your app requires first-arrival gating.

### PullSubQueueMessage

```csharp
string Topic { get; }
byte[] Payload { get; }
DateTime ReceivedUtc { get; }
```

### PullSubQueueSubscription

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

**Idempotent Semantics:** UnsubscribeAsync / DisposeAsync can be called multiple times safely.
Subsequent calls after the first will return `AlreadyCanceled` (for UnsubscribeAsync) and complete without side-effects.

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
