# Changelog

## [Unreleased]

## [1.0.0] - 2026-xx-xx

### Added
- Initial release.
- Data API for polling the latest typed value (`SubscribeDataAsync`, `GetDataHandle`).
- Queue API for ordered message handling (`RegisterHandlerLeaseAsync`, `ReceiveQueueAsync`).
- JSON and flat JSON payload codecs out of the box.
- MQTT transport via MQTTnet v4.3.x (`PullSub.Mqtt`).
- Custom transport support via `ITransport`.
- Unity Bridge (`PullSubMqttClient` MonoBehaviour).