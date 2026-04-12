# Contributing to PullSub

Thank you for your interest in contributing!

## Reporting Bugs

Please open an issue on GitHub with the following information:
- PullSub version
- Unity version (if applicable) / .NET version
- Steps to reproduce
- Expected behavior and actual behavior

## Suggesting Features

Open an issue with a description of the feature and your use case.

## Pull Requests

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/your-feature`)
3. Make your changes
4. Run tests (`dotnet test`)
5. Open a Pull Request against `main`

Please keep PRs focused on a single change.
Large changes should be discussed in an issue first.

## Development Setup

### Prerequisites
- .NET 8.0 SDK
- Unity 2022.3 or later (for Unity Bridge)
- UniTask 2.x

### Build

```bash
cd dotnet/PullSub.Core
dotnet build
```

### Test

```bash
dotnet test dotnet/tests/PullSub.Core.Tests/PullSub.Core.Tests.csproj
```

## Release Process

1. Update `<Version>` in `PullSub.Core.csproj` and `PullSub.Mqtt.csproj`
2. Update `CHANGELOG.md`
3. Push to `main`
4. Create a GitHub Release with tag `v0.1.0`
5. CI automatically publishes to NuGet.org

## Notes

This project is maintained by a student developer on a best-effort basis.
Response times may vary.