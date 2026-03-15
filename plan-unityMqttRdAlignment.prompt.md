## Plan: Unity MQTT R&D Alignment

現行コードはすでに R3 非依存化されており、今後の主軸は 1) ドキュメントと実装の整合化、2) シリアライザ抽象化、3) Core/Unity 分離と Task 化、4) MQTTnet v4.3.7 への移行、5) 研究概念 A/C の実装基盤整備、という順序で進めるのが最も安全です。研究概念 B はライブラリ外の評価層として分離を維持します。

**Steps**
1. Phase 1: 現状整合化とベースライン固定。R3 削除済みを前提に、README と導入説明から R3 依存記述を除去し、現行アーキテクチャを最新化する。あわせて既知制約として wildcard 非対応、Start/Dispose 競合、handler 登録の 2-phase 挙動を明文化する。
2. Phase 1: PublishDataAsync 群は維持対象として固定し、今後の変更でシグネチャ互換を崩さない方針を明示する。依存注入やオプション追加は既存オーバーロードを包む形で行う。
3. Phase 2: シリアライザ抽象化。IMqttSerializer を新設し、MqttDTO の JSON 直結ロジックをデフォルト実装へ分離する。MqttSubscribeExtensions と Publish 系拡張の Newtonsoft.Json 直結箇所を serializer 経由へ置換する。*depends on 1*
4. Phase 2: DTO モデルと serializer 責務を分離する。MqttDataEnvelope / MqttDataItem はデータ契約として維持し、SerializeEnvelope / TryDeserializeData は serializer 実装へ移す。System.Text.Json と MessagePack を差し込み可能な構成にする。*depends on 3*
5. Phase 3: .NET 汎用化。Core から UniTask 依存を外し、Task / ValueTask ベースへ移行する。MqttClientManager と Core 拡張メソッドは純 .NET ライブラリに残し、MqttBridge と Unity ログ・ライフサイクルだけを Unity 層へ隔離する。*depends on 3-4*
6. Phase 3: アセンブリとパッケージ境界を再設計する。MqttClient.Core は MQTTnet と serializer abstraction だけに依存させ、MqttClient.Unity は MonoBehaviour、Unity logging、必要なら Unity 向け補助 API を提供する。README とサンプルは新構成に合わせて更新する。*depends on 5*
7. Phase 4: MQTTnet v4.3.7 への移行。MqttClientManager の factory、handler 登録、managed client options、Start/Stop/Subscribe/Publish 呼び出しを v4 API に合わせて差し替える。必要に応じて IMqttTransport を導入し、将来の MQTTnet v5 / 別 transport への差し替え余地を確保する。*depends on 5-6*
8. Phase 4: MQTT v5 活用の拡張点を追加する。Message Expiry Interval、User Properties、Payload Format Indicator はオプション API として導入し、既存 publish API の簡潔性は維持する。*depends on 7*
9. Phase 5: 研究概念 A をライブラリ機能として追加する。MqttDataRepository に鮮度判定と AoDT 算出 API を追加し、補間が必要なら履歴保持機構を opt-in で実装する。履歴は既定で無効にして、常用時のメモリ増加を避ける。*depends on 3-4, parallel with 8 after serializer design is fixed*
10. Phase 5: 研究概念 C の評価基盤を追加する。MessagePack serializer 実装、JSON vs MessagePack の allocation/FPS 比較、User Properties への timestamp 移送案を検証する。Unity 側ではメインスレッドが snapshot 読み取りだけを行う現在設計を維持し、計測コードはサンプルまたは別評価プロジェクトに隔離する。*depends on 4, 6, 8*
11. Phase 5: 研究概念 B はライブラリ外で継続する。PublishDataAsync を呼ぶ external filtering layer として Builder 実装または R3 実装を比較し、本体 package には依存を戻さない。*independent after 2*

**Relevant files**
- c:\Users\daich\github\unity-mqtt\Runtime\Core\MqttClientManager.cs — MQTTnet v3 managed client 依存、UniTask 依存、将来の transport abstraction 導入点
- c:\Users\daich\github\unity-mqtt\Runtime\Core\MqttDTO.cs — 現在の Newtonsoft.Json 直結点、serializer 抽象化の起点
- c:\Users\daich\github\unity-mqtt\Runtime\Core\MqttSubscribeExtensions.cs — 受信時の DTO パース接点
- c:\Users\daich\github\unity-mqtt\Runtime\Core\MqttPublishExtensions.cs — 汎用 payload の JSON 送信接点
- c:\Users\daich\github\unity-mqtt\Runtime\Core\MqttDataPublishExtensions.cs — PublishDataAsync 群、互換維持の中心
- c:\Users\daich\github\unity-mqtt\Runtime\Core\MqttDataRepository.cs — AoDT / stale 判定 / 履歴保持の導入先
- c:\Users\daich\github\unity-mqtt\Runtime\Core\MqttDataEntry.cs — SourceTimestampUtc / ReceivedUtc / Age の研究基盤
- c:\Users\daich\github\unity-mqtt\Runtime\Bridge\MqttBridge.cs — Unity 層への分離対象
- c:\Users\daich\github\unity-mqtt\Runtime\com.toshi0515.unity-mqtt.asmdef — 現行 assembly 境界と Unity 参照
- c:\Users\daich\github\unity-mqtt\README.md — R3 記述が古く、現状の構成とずれている
- c:\Users\daich\github\MqttTest\Assets\MqttHelloExample.cs — raw subscribe の利用例
- c:\Users\daich\github\MqttTest\Assets\MqttSpherePosExample.cs — typed repository / PublishDataAsync の利用例
- c:\Users\daich\github\MqttTest\Assets\MqttManualTestController.cs — 手動回帰確認の主ハーネス
- c:\Users\daich\github\MqttTest\MANUAL_TESTS.md — 移行ごとの回帰確認手順

**Verification**
1. 各フェーズで Unity package のコンパイル確認を行い、MqttTest プロジェクトで Play Mode 起動時にエラーが増えていないことを確認する。
2. MqttTest の P0 手動試験を最小回帰セットとして毎回実施する。正常起動、Publish Hello、Publish Sphere、Stop/Restart、Play Mode exit cleanup を必須確認項目にする。
3. serializer 抽象化後は Newtonsoft デフォルト実装で現行 payload 互換を確認し、既存 sample が無変更または最小変更で動作することを確認する。
4. Task 化後は Core が UnityEngine と UniTask を参照しないことを assembly 依存で確認する。
5. MQTTnet v4 移行後は broker interruption / auto reconnect / delayed subscribe を重点的に再試験する。
6. 研究概念 A/C は別途計測条件を固定し、AoDT 算出精度、stale 判定、GC allocation、FPS 影響を比較記録する。

**Decisions**
- R3 削除は完了済みとして扱い、実装計画の先頭ステップからは除外し、ドキュメント整合化のみ実施対象にする。
- PublishDataAsync 群は設計良好のため維持し、破壊的変更を避ける。
- 研究概念 B のフィルタリング層はライブラリ本体へ戻さない。
- AoDT 用の履歴保持は opt-in にし、既定の lightweight repository を守る。
- MQTTnet は v4.3.7 を目標とし、v5 直行は Unity 互換性の観点から対象外とする。

**Further Considerations**
1. serializer の注入位置は MqttClientManager 単位か、MqttDataStore / topic ごとかを早期に決める必要がある。推奨は manager または options オブジェクト単位での注入。
2. Task 化で Unity 利用者の ergonomics が落ちる場合は、Unity 層だけに UniTask bridge 拡張を残す案が現実的。
3. AoDT 補間は数値型だけを対象に始めるべきで、任意 object 全般への一般化は初期スコープから外すのが安全。
