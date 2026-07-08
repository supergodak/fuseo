# コア設計 — MaskingCore（WP-1成果物・確定版）

**版**: v1.0（2026-07-07・Fableで確定）
**位置づけ**: WP-2以降（Opus 4.8）の実装はこの文書と `Sources/MaskingCore/{Model,Support,Pipeline}` の型定義に**従う**。
設計判断が必要になったら実装せず Fable にエスカレーション（設計書§0）。

---

## 1. 座標系規約（最重要・バグ源の封じ込め）

### 1.1 内部の唯一の座標系 = 「基準画像の正規化座標・左下原点」
- **基準画像（base image）** = 書類検出→台形補正→**正立化**まで済んだ CGImage。パイプライン後段（OCR・分類・
  ルール・候補・描画・書き出し）は全てこの画像を対象とする。
- 内部の矩形は全て `NormRect`（0..1 正規化・**左下原点**）。Vision の boundingBox・CIImage と同じ向きなので
  変換なしで受け渡せる。
- 変換が必要なのは次の2箇所**だけ**。必ず `CoordinateSpace` を通す（各所での手変換は禁止）:
  - 描画（CGContext・左下原点）: `CoordinateSpace.pixelRect(_:in:)` — 単純スケール
  - SwiftUI表示（左上原点）: `CoordinateSpace.viewRect(_:in:)` — Yフリップ＋スケール

### 1.2 プリセットJSONの固定領域は「上端基準 yTop」で書く（人間向け例外）
- WP-3 でオーバレイPNGを見ながら領域を測る人間（およびプレビュー系ツール）は**左上原点**で考えるため、
  JSON 上の固定領域だけは `{"x", "yTop", "w", "h"}`（左上原点・正規化）で記述する。
- ローダ（`MaskRule.Region.normRect`）が読み込み時に左下原点 `NormRect` へ変換する。**内部に yTop を持ち込まない**。
- 変換式: `y = 1 − yTop − h`（`CoordinateSpace.fromTopOrigin` に実装済み）。

### 1.3 正立化（WP-0知見の正式化）
- **Vision OCR は 180°逆さの文字を完全に読める**ため、認識可否では向きを判別できない。
- 向き決定は「正立スコア」= Σ(conf × 有意文字数)（対象は `upright == true` の観測のみ）で行う。
  `upright` はテキスト観測の四隅がテキスト自身の上下基準で付くことを利用し `topLeft.y > bottomLeft.y` で判定。
- 補正後が縦長なら 90°CW/CCW の2択、横長なら 0/180°の2択に絞る（カード=ID-1横長前提。A4書類は縦長のまま
  縦横判定をスキップし 0/180 のみ比較）。正立スコア全ゼロ時は総量スコアにフォールバック。

---

## 2. データモデル（`Sources/MaskingCore/Model/` が正）

### 2.1 書類種別
```swift
enum DocumentType: String, Codable   // JSON の documentType と同じ raw value
// hokensho / shikakuKakuninsho / juminhyoMyNumber /
// menkyoshoFront / menkyoshoBack / myNumberCardFront / myNumberCardBack / generic
```
- PoC の `DocumentClass` は WP-2 で本型に置換して廃止する。
- `.generic` = 種別不明時のフォールバック（固定領域なし・動的検出＋手動マスクのみ）。
- `.myNumberCardBack` はプリセットの `warnings` に「この面は提出できない場面が多い（番号法）」を持つ。

### 2.2 プリセット（データ駆動・バンドルJSON）
```
Sources/MaskingCore/Resources/Presets/<documentType>.json   ※SPM resources: .process
```
スキーマ（v1）:
```json
{
  "schemaVersion": 1,
  "documentType": "menkyoshoFront",
  "displayName": "運転免許証（表）",
  "enhance": false,
  "classification": { "keywords": [ { "text": "運転免許証", "weight": 6 } ] },
  "warnings": [],
  "rules": [
    { "id": "menkyo.number", "label": "免許証番号",
      "kind": "dynamic", "detector": "licenseNumber12",
      "defaultOn": true, "padding": 0.012,
      "basis": "提出先により秘匿を求められる（自治体入札実務等）" },
    { "id": "menkyo.honseki", "label": "本籍（旧様式のみ）",
      "kind": "fixed", "region": { "x": 0.05, "yTop": 0.30, "w": 0.60, "h": 0.06 },
      "defaultOn": false,
      "basis": "取得制限情報（docomoローンFAQ等の提出実務）" }
  ]
}
```
規約:
- `id` は `<種別略称>.<欄名>` で一意。v2 の提出先テンプレートは `{ ruleStates: {"menkyo.number": true, ...} }`
  の**選択セット**としてこの id を参照する（前方互換の要）。
- `fallbackRegion`（dynamicルール専用・任意・yTop表記）: 検出子が0件のときに使う固定領域。
  規格で位置が固定の欄（マイナ裏の個人番号等）の「反射等でOCRが読めなくても位置で塗る」保険。
  候補ラベルに「（位置推定）」が付く。fixedルールに書くとバリデーションエラー
  （2026-07-07 dogfood起点。実測領域はクリーン/斜め両方の検出boxを包含して決める）。
- `basis`（根拠）は**全ルール必須**。確認UIにそのまま表示する。
- `enhance`: 基準画像に CIDocumentEnhancer を掛けるか。**カード類=false**（顔写真・地紋が破綻する）、
  紙書類（住民票等）=true。
- `padding`: マスク矩形の外周余白（正規化・既定 0.01）。「復元できない程度」の安全マージン。
- `kind: "fixed"` は `region`（yTop表記）必須、`kind: "dynamic"` は `detector` 必須。ローダがバリデーションする。

### 2.3 動的検出子
```swift
enum DetectorID: String, Codable
// myNumber12 / licenseNumber12 / creditCardLuhn / insurerNumber / kigoBango / face / qrBarcode
```
実装規約（WP-0知見の正式化）:
- **12桁系は「ちょうど12桁」のみ候補化**（窓スライド禁止）:
  A) 1観測内の極大数字連続（空白のみ除去後）が12桁 / B) 同一行（中心Y±2%）の数字のみ観測の合計が12桁。
  B は x昇順連結＋逆順の保険トライ（逆順不成立はログに残さない）。チェックデジットで確定する。
- 同一行判定は中心Y±2%に加えて**縦区間の重なり（低い方の文字高×35%以上）**でも成立
  （傾き残り対策・2026-07-07 dogfood）。比較は「行の末尾要素と」＝斜めの行を連鎖で追う。
- **ID-1アスペクト正規化（2パス）**: 斜め撮影では台形補正後の縦横比が実物からずれ OCR 精度が落ちる。
  種別が ID-1 確定カード（`DocumentType.isID1Card`）で長短比が 1.5858 から5%超ずれていたら、
  実物比へ再サンプルして OCR をやり直す（MaskingPipeline.analyze 内。正規化座標は水平スケール不変）。
- `insurerNumber` / `kigoBango`（保険証系）: 桁数（保険者番号=8桁または6桁）＋**近傍キーワード**
  （「保険者番号」「記号」「番号」が同一行または直上行にある）で判定。チェックデジットが無いため
  キーワード近接を必須条件にする（保険証実物のPoC測定後に閾値確定。未測定のまま実装だけ先行してよい）。
- `face`: VNDetectFaceRectanglesRequest。**既定OFF**のトグル用。
- `qrBarcode`: VNDetectBarcodesRequest（全シンボロジー）。

### 2.4 候補と確認フロー
```swift
struct MaskCandidate: Identifiable
// id: UUID / ruleID: String / label: String / box: NormRect（padding適用済み）
// source: .fixedRegion | .detector(DetectorID) / confidence: Float? / isOn: Bool（初期値=rule.defaultOn）
// basis: String
```
- 候補生成までがコア。**isOn の最終決定はユーザー**（確認UI必須・設計書§1.5）。
- 手動追加は `ManualMask`（矩形リスト＋ブラシストローク）。ブラシは点列＋線幅で持ち、描画時に不透明線として焼き込む。

---

## 3. パイプライン（`Sources/MaskingCore/Pipeline/Protocols.swift` が正）

```
URL ──DocumentRectifier──▶ PageImage（基準画像）
      PageImage ──TextRecognizer──▶ [OCRItem]
      ([OCRItem], presets) ──DocumentClassifier──▶ ClassificationResult
      (preset, PageImage, [OCRItem]) ──RuleEngine──▶ [MaskCandidate]
      （ユーザー確認: isOn編集＋ManualMask追加）
      (PageImage, 確定マスク) ──MaskRenderer──▶ CGImage（焼き込み済み）
      ([RenderedPage], ExportOptions) ──Exporter──▶ 出力ファイル
```
- 各段は protocol。標準実装は `Vision`/`CoreImage` 版（WP-2）。テストはフィクスチャ実装を差し込む。
- オーケストレータ `MaskingPipeline.analyze(url:) -> AnalyzedPage` が rectify→ocr→classify→candidates を一括実行。
  `AnalyzedPage` = PageImage＋OCR＋分類＋候補のスナップショット（UIはこれを表示・編集する）。
- **エラー方針**: 書類検出失敗は throw せず「全面フォールバック＋ `rectified=false`」で続行（PoC実証済み）。
  読み込み不能・OCR実行失敗のみ `MaskingError` を throw。

## 4. Render / Export 契約（WP-4実装・I/Oは今確定）

- `MaskRenderer.burnIn`: 新規ビットマップに基準画像を描き、確定マスクを**完全不透明**で塗る（黒 #000 固定）。
  α合成・注釈・レイヤーは禁止。出力は新しい CGImage（元ピクセルはマスク下に存在しない）。
- `Exporter`:
  - JPEG/PNG: `CGImageDestination` を**メタデータ辞書なし**で生成（EXIF・GPS・サムネイルを引き継がない）。
  - PDF: 焼き込み済みラスタのみを埋め込む。検索可能PDFのテキスト層は
    **マスク矩形と交差する OCRItem を除外**して合成する（マスク下の文字列をPDFに残さない）。
  - テスト（WP-4完了条件）: 出力のメタデータ走査・PDFテキスト層走査・マスク領域ピクセル検証を自動化。
- `ExportOptions { format: .pdf | .jpeg | .png, searchableText: Bool, jpegQuality: Double }`

## 5. ファイル配置と作業指示

```
Sources/MaskingCore/
  Model/        DocumentType.swift / Preset.swift / Candidate.swift     ← WP-1で確定済（変更はFable承認制）
  Support/      CoordinateSpace.swift                                    ← 同上
  Pipeline/     Protocols.swift                                          ← 同上
                VisionRectifier.swift / TextRecognition.swift /
                Classifier.swift / RuleEngine.swift / MaskingPipeline.swift   ← WP-2（PoCPipelineから移植・製品化）
  Render/       MaskRenderer.swift / Exporter.swift                      ← WP-4（Fable）
  Resources/Presets/*.json                                               ← WP-3（Opus）
  PoC.swift 等（PoCPipeline/Checkdigits/Overlay）                        ← WP-2完了時にPoCPipelineを廃止、
                                                                            Checkdigits/OverlayはPipeline/Testsへ吸収
Sources/poc/    測定CLI。WP-2完了後は MaskingPipeline を叩く回帰テストとして維持
```

### Opus への具体指示（WP-2/3で従うこと）
1. 型・スキーマ・座標規約は本書と Model/Support/Protocols のとおり。**変えたくなったら中断して Fable へ**。
2. PoCPipeline のロジック（正立スコア・ちょうど12桁・行グルーピング±2%）は**そのまま移植**する。挙動を変えない。
3. プリセットJSONの固定領域は `fixtures-private/out/*-overlay.png` を画像ビューアで開き、左上原点の比率で
   `yTop` 表記のまま記入する（内部変換はローダがやる。自分で 1−y−h 計算をしない）。
4. 実物写真（fixtures-private/）は**コミット禁止**。テストフィクスチャが要る場合は合成画像を生成する。
5. 各実装完了時に `swift run poc fixtures-private/*.jpeg` を回し、WP-0結果（docs/poc-results.md のサマリ表）から
   後退していないことを確認する。
