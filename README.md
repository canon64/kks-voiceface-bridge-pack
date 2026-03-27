# kks-voiceface-bridge-pack (Source)

このREADMEは、`kks-voiceface-bridge-pack` 向けの  
**ソース配布ルートREADME.md** として使う想定です。

主目的は、`human_2_KKS_pipeline` とKKS側の連携を成立させることです。  
特に音声連携の中核は `MainGameVoiceFaceEventBridge` です。

## セット内容（3プロジェクト）

1. `MainGameVoiceFaceEventBridge`
- 役割: 外部からの音声/コマンドを受けてKKSへ反映する中核ブリッジ
- 主な受信: NamedPipe (`kks_voice_face_events`) / 任意でHTTP
- 機能: 音声再生、表情制御、`response_text` 由来の衣装変更トリガー処理

2. `MainGameSubtitleEventBridge`
- 役割: HTTPで受けた字幕要求を `MainGameSubtitleCore` に橋渡し
- 主な受信: `http://<host>:18766/subtitle-event`（既定）
- 注意: `MainGameSubtitleCore` への HardDependency あり（Core必須）

3. `MainGameSubtitleCore`
- 役割: 字幕表示本体（キュー/表示API/入力パネル）
- `SubtitleApi` を提供し、`MainGameSubtitleEventBridge` から呼ばれる
- 入力パネルからGUI側へテキスト転送する機能も持つ

## human_2_KKS_pipeline との接続仕様

1. 音声イベント連携（主経路）
- GUI -> `send_voice_face_event.ps1` -> `MainGameVoiceFaceEventBridge`
- 既定: NamedPipe `kks_voice_face_events`
- HTTP利用時（任意）:
  - port: `18765`
  - endpoint: `/voice-face-event`

2. 字幕連携
- GUI -> HTTP POST -> `MainGameSubtitleEventBridge`
- 既定:
  - host: `127.0.0.1`
  - port: `18766`
  - endpoint: `/subtitle-event`
- EventBridge -> `SubtitleApi` -> `MainGameSubtitleCore`

3. 逆方向（任意）
- `MainGameSubtitleCore` 入力パネル -> GUI外部受信
- 既定:
  - host: `127.0.0.1`
  - port: `18767`
  - endpoint: `/manual-text`

## ビルド対象

- `MainGameVoiceFaceEventBridge/MainGameVoiceFaceEventBridge.csproj`
- `MainGameSubtitleEventBridge/MainGameSubtitleEventBridge.csproj`
- `MainGameSubtitleCore/MainGameSubtitleCore.csproj`

## DLL配置先（実行時）

`BepInEx/plugins/canon_plugins/` 配下に、各プラグインを個別フォルダで配置します。

- `MainGameVoiceFaceEventBridge/MainGameVoiceFaceEventBridge.dll`
- `MainGameSubtitleEventBridge/MainGameSubtitleEventBridge.dll`
- `MainGameSubtitleCore/MainGameSubtitleCore.dll`

## 最低限の動作確認

1. 3DLLがすべて読み込まれていることを確認
2. `human_2_KKS_pipeline` から音声イベントを送信し、音声が再生されることを確認
3. 字幕送信で `subtitle-event` が受理され、字幕表示されることを確認
4. 必要なら `MainGameSubtitleCore` 入力パネルから `manual-text` 転送を確認

## 補足

- 本セットは「音声連携中心 + 字幕連携同梱」の構成です。
- 字幕系だけ利用する場合でも、`MainGameSubtitleEventBridge` には `MainGameSubtitleCore` が必要です。
