# PullSub dotnet tests

## Scope

This folder contains three layers:

- PullSub.Core.Tests: fast local functional tests using in-memory TestTransport
- PullSub.Core.Benchmarks: BenchmarkDotNet microbenchmarks for codec/dispatch hot paths
- PullSub.Core.IntegrationTests: broker-backed loopback perf tests against local MQTT broker

## Prerequisites

- .NET SDK 8+
- For integration tests: local broker at 127.0.0.1:1883

## Run functional tests

From dotnet folder:

```powershell
 dotnet test tests/PullSub.Core.Tests/PullSub.Core.Tests.csproj -c Release
```

## Run benchmarks

From dotnet folder:

```powershell
 dotnet run -c Release --project tests/PullSub.Core.Benchmarks/PullSub.Core.Benchmarks.csproj -- --filter "*"
```

Use benchmark filters to limit runtime:

```powershell
 dotnet run -c Release --project tests/PullSub.Core.Benchmarks/PullSub.Core.Benchmarks.csproj -- --filter "*Borrow*"
```

## Run broker integration tests

Start local broker first, then run:

```powershell
 dotnet test tests/PullSub.Core.IntegrationTests/PullSub.Core.IntegrationTests.csproj -c Release
```

If broker is not reachable, tests fail fast with an explicit broker-required message.
