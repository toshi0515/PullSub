# Changelog

## [2.1.0] - 2026-03-07

### Added
- `MqttBridge` — 従来の `MqttSubscriberBridge` と `MqttPublisherBridge` を統合し、MQTT 接続を一元管理する単一のエントリポイントコンポーネントを追加

### Changed
- **[BREAKING]** `MqttSubscriberBridge` および `MqttPublisherBridge` を廃止・削除し、`MqttBridge` に一本化
- **[BREAKING]** `MqttClientManager` の受信コールバック仕様を変更。従来は自動的に Unity メインスレッドにディスパッチしていましたが、.NET 汎用化のため MQTTnet のバックグラウンドスレッドでそのまま実行されるようになりました
- **[BREAKING]** 上記に伴い `MqttSubscribeExtensions.SubscribeDataTopicAsync` 等の購読側において、受信データから Unity オブジェクト（UIなど）を直接操作する場合は、利用者側で `UniTask.Post`等を使用し明示的にコンテキストを切り替える構成に変更

## [2.0.0] - 2026-03-06

### Added
- `MqttDataStore` — トピック（デバイス）ごとのリポジトリを管理する新クラス
- `MqttDataEntry` — 値 + ソース生成時刻 + 受信時刻を保持する高精度データモデル
- `MqttDataRepository` — `TryGetEntry` メソッドの追加
- `MqttTopicDefinition` — 型安全なトピックスキーマ定義の抽象基底クラス
- `MqttField<T>` — トピックの単一データフィールドへの型安全アクセサ
- `MqttDataStore.GetTopic<T>()` — 型安全なトピックインスタンスの取得

### Changed
- **[BREAKING]** `IDataRepository` インターフェースを廃止
- **[BREAKING]** `MqttDataRepository` シングルトンを廃止。トピックごとにインスタンス化される設計に変更
- **[BREAKING]** `MqttDataRepository` の辞書の値型を `object` から `MqttDataEntry` に変更
- **[BREAKING]** `MqttDataStore` の文字列インデクサ `store["topic"]` を廃止。`store.GetTopic<T>()` を使用
- **[BREAKING]** `MqttDataItem.Type`（PLC固有の型ヒント）を廃止。型変換は `MqttDataEntry.TryGetValue<T>` が汎用的に処理
- **[BREAKING]** `MqttDataRepository` の PLC固有型変換ロジック（`BIT`/`WORD`/`DINT`/`REAL`）を廃止。`Convert.ChangeType` による汎用変換に置換
- `MqttSubscriberBridge` — `Store` 経由でのアクセス、複数トピックの同時購読に対応

## [1.2.0] - 2026-03-06

### Added
- `MqttDataPublishExtensions` — Subscribe 側と対称な `MqttDataEnvelope` 形式でのデータ Publish 拡張メソッド（Envelope / Items / Dictionary / 単一値の 4 オーバーロード）
- `MqttDTO.SerializeEnvelope` — Envelope の JSON シリアライズヘルパー

### Changed
- `MqttClientManager` リファクタリング: doc comment 修正、未使用フィールド (`_lock`) 削除、`SemaphoreSlim` の FQN 省略

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
