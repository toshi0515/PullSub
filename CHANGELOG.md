# Changelog

## [Unreleased]

### Changed
- **[BREAKING]** `PollRawAsync` を `ReceiveRawAsync` に改名

### Removed
- **[BREAKING]** `MqttV2ClientRuntime.WaitForNextDataAsync` を削除
- **[BREAKING]** `MqttV2ClientRuntime.WatchTopicAsync` を削除（`IAsyncEnumerable` ベースの Data 系 Push API）
- **[BREAKING]** `MqttV2Bridge.WaitForTopicUpdateAsync` を削除
- **[BREAKING]** `MqttV2Bridge.WatchTopicOnMainThreadAsync` を削除
- **[BREAKING]** `com.toshi0515.unity-mqtt.asmdef` から `UniTask` 参照を削除
- `MqttV2DataCache` の changeVersion / changeSignal 管理機構を削除（Push API 内部専用実装）

## [2.2.0]

### Added
- `MqttSubscriptionLease` を追加し、購読の所有権と解除を `Dispose()` / `DisposeAsync()` に集約
- `MqttBridge` に `IsManagerStarted` / `IsManagerConnected` を追加

### Changed
- **[BREAKING]** `MqttClientManager.SubscribeAsync` を lease ベースの one-phase API に変更し、`SubscribeAsync(topic, handler, ct)` が `MqttSubscriptionLease` を返すよう変更
- **[BREAKING]** `PublishAsync` / `SubscribeAsync` は `StartAsync` 完了後のみ許可する契約に統一
- `MqttSubscribeExtensions.SubscribeDataTopicAsync(...)` は `MqttSubscriptionLease` を返すよう変更
- `MqttBridge` は topic ごとの lease を保持し、`ShutdownAsync` / `OnDestroy` で解除するよう変更
- `MqttHelloExample` / `MqttSpherePosExample` / `MqttManualTestController` を新 lease API に合わせて更新
- README / MANUAL_TESTS を lease ベースの API に合わせて更新

### Removed
- **[BREAKING]** `MqttSubscribeResult` / `MqttSubscribeFailureKind` を削除
- **[BREAKING]** `MqttUnsubscribeResult` / `MqttUnsubscribeFailureKind` / `MqttUnsubscribeMode` を削除
- **[BREAKING]** `AddMessageHandler(...)` / `SetMessageHandler(...)` / `RemoveMessageHandler(...)` / `UnsubscribeAsync(...)` を削除
- `MqttPlcCommandPublishExtensions` を削除
- `MqttDTO.SerializeEnvelope` / `MqttDTO.TryDeserializeData` の互換ヘルパーを削除

## [2.1.1]

### Added
- `IMqttSerializer` 抽象を追加し、`NewtonsoftMqttSerializer` を既定実装として導入
- `MqttClientManager` のコンストラクタに optional serializer 引数を追加（manager 単位で差し替え可能）

### Removed
- **[BREAKING]** `StopAsync()` を削除。`ManagedMqttClient` が自動再接続を担うため、Unity アプリでの Stop → Restart パターンは不要と判断
- **[BREAKING]** `ClientState.Stopped` を削除

### Changed
- `MqttClientManager` のライフサイクルを `NotStarted → Running → Disposed` の 3 フェーズに単純化。`_stateSemaphore` は `StartAsync` 多重実行防止のみに特化し、`Dispose` は `Interlocked` ラッチのみで保護
- `MqttDataPublishExtensions` / `MqttPublishExtensions` / `MqttPlcCommandPublishExtensions` の JSON 変換経路を manager serializer 経由へ統一
- `MqttSubscribeExtensions.SubscribeDataTopicAsync` の DTO 復元を manager serializer 経由へ置換
- `MqttDTO.SerializeEnvelope` / `MqttDTO.TryDeserializeData` は互換 API として維持しつつ、既定 serializer への委譲実装へ変更
- README を現行実装に合わせて更新し、ライブラリの責務範囲、設計方針、既知制約、手動検証の導線を明文化
- R3 依存削除済みの現状に合わせて導入手順を修正し、UniTask のみを前提条件として記載
- `package.json` の説明文を現行アーキテクチャに合わせて更新し、型安全なトピックスナップショットとタイムスタンプ付きデータモデルを説明に反映

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
