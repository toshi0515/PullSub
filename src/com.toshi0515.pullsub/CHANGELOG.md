# Changelog

## [Unreleased]

### Changed
- Replaced MQTTnet Managed Client usage with plain `IMqttClient`-based transport implementation.
- Moved reconnect orchestration to runtime with exponential backoff + jitter and reconnect diagnostics.
- Updated Unity bridge connection settings to configure reconnect options explicitly (initial delay / max delay / multiplier / jitter).

## [1.0.0] - 2026-xx-xx

### Added
- Initial release.
- Data API for polling the latest typed value (`SubscribeDataAsync`, `GetDataHandle`).
- Queue API for ordered message handling (`SubscribeQueueAsync`, `ReceiveQueueAsync`).
- JSON and flat JSON payload codecs out of the box.
- MQTT transport via MQTTnet v4.3.x (`PullSub.Mqtt`).
- Custom transport support via `ITransport`.
- Unity Bridge (`PullSubMqttClient` MonoBehaviour).