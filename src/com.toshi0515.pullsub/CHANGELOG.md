# Changelog

## [Unreleased]

## [0.1.0] - 2026-04-12

### Added
- Initial release.
- Data API for polling the latest typed value (`SubscribeDataAsync`).
- Queue API for ordered message handling (`SubscribeQueueAsync`).
- JSON and flat JSON payload codecs out of the box.
- MQTT transport via MQTTnet v4.3.x (`PullSub.Mqtt`).
- Custom transport support via `ITransport`.
- Unity Bridge (`PullSubMqttClient` MonoBehaviour).