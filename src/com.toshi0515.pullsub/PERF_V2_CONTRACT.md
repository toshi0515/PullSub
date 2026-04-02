# PullSub Perf V2 Contract (Draft)

This document freezes the performance-oriented v2 contracts before implementation.

## 1. Data API semantics (in-place only)

- Data API is for latest-state reads in game loops.
- Data API is in-place only for class payloads.
- Holding a reference from `DataHandle.Value` across frames is not supported.
- Historical processing is explicitly out of scope for Data API and must use Queue API.

### Supported pattern

- Read and consume latest value in the same frame.
- Use `WaitForFirstDataAsync` before first read if default values are ambiguous.

### Unsupported pattern

- Keeping `Value` references for historical diffs across frames.
- Treating Data API as an event log.

## 2. Receive payload ownership

- Transport receives payloads as borrowed memory and forwards them without cloning in Data-only path.
- Borrowed payload lifetime is only valid during the message callback execution.
- Any component that stores payload beyond callback scope must create an owned copy.
- Queue API stores owned payload bytes by design.

## 3. Data and Queue path split

- Data-only subscriptions: decode directly from borrowed payload, no queue copy.
- Queue subscriptions present: dispatcher creates one owned copy for queue storage.
- Data decode should still consume span/memory directly even when queue is active.

## 4. Codec V2 responsibilities

- Codec V2 provides writer-based encode and span-based decode.
- Writer-based encode exists to avoid forced per-message byte[] creation in codec internals.
- Existing byte[] encode path can remain as compatibility shim during migration.
- MemoryPack-optimized codecs should implement Codec V2 first-class.

## 5. Concurrency and consistency requirements

- Data readers must not observe partially updated in-place state.
- Runtime must guarantee coherent latest-value publication for readers.
- Queue signaling must not rely on exceptions in normal control flow.

## 6. Migration stance

- Repository is pre-release; breaking changes are allowed.
- Prioritize runtime hot-path performance over backward compatibility.
