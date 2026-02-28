# Changelog

## [1.1.0] - 2026-02-28

### Changed
- シングルトン廃止: `MqttClientManager` に public コンストラクタを追加
- ファサードクラスを削除し、拡張メソッド (`MqttPublishExtensions`/`MqttSubscribeExtensions`/`MqttPlcCommandPublishExtensions`) に移行
- `MqttSubscriberBridge` が自身の `MqttClientManager` インスタンスを保持し、シングルトン化

## [1.0.0] - 2026-02-28

### Added
- `MqttClientManager` — シングルトン MQTT クライアント管理（自動再接続、メインスレッドディスパッチ）
- `MqttSubscriber` — Subscribe ファサード（データトピック購読 → リポジトリ更新）
- `MqttCommandPublisher` — Publish ファサード（汎用 JSON / PLC コマンド）
- `MqttDataRepository` — スレッドセーフなデータリポジトリ（型変換付き）
- `MqttDTO` — データエンベロープ DTO
- `MqttSubscriberBridge` — MonoBehaviour エントリポイント
- MQTTnet v3.1.2 DLL 同梱
