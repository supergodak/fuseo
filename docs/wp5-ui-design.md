# WP-5 UI設計 — 確認画面ワイヤ＋アプリシェル実装契約（Fable確定版）

**版**: v1.0（2026-07-07・Fable）
**位置づけ**: WP-5（Opus実装）の単一の契約文書。design.md §1.5（UX原則・絶対条件）と
core-design.md（座標規約・型）に**従属**する。矛盾があれば実装せず Fable へエスカレーション。

---

## 1. 画面フロー（1ウィンドウ・3状態）

```
[Empty/Drop] --(drop/open)--> [Processing n/m] --(done)--> [Review] --(export sheet)--> save
     ^                                                        |
     +----------------------(new document)--------------------+
```

- 単一 `WindowGroup`（最小 1000×680）。状態は `enum Stage { empty, processing(done: Int, total: Int), review }`。
- **Empty**: 中央に点線ドロップゾーン「本人確認書類の写真をここにドロップ」＋「ファイルを選択…」ボタン。
  受理 UTType: jpeg / png / heic / tiff（複数可）。PDF等の非対応はその場でアラート（読み込まない）。
- **Processing**: `ProgressView`＋「解析中… (n/m)」。複数ファイルは**直列**で `analyze`（Vision が内部並列のため）。
  1枚でも成功すれば Review へ。全滅なら Empty へ戻しエラーアラート。
- **Review**: 本体（§2）。**保存前確認UIそのもの**。ここを経ずに書き出す経路を作らない（design.md §1.5 絶対条件）。

## 2. 確認画面（Review）ワイヤ

```
+----------------------------------------------------------------------+
| toolbar: [+Add] [Sel|Rect|Brush] [width ----] [Fit][100%][zoom -+]   |
|          ....................................... [Preview] [Export..]|
+------+------------------------------------------------+-------------+
|pages |  (banner: rectified=false notice)              | inspector   |
| [p1] |                                                | DocType     |
| [p2] |          canvas                                |  picker     |
|      |   base image fit-to-window                     | warnings    |
|      |   - ON candidate: 70% black + solid frame      |-------------|
|      |   - OFF candidate: gray dashed frame           | candidates  |
|      |   - manual mask: 70% black + accent frame      |  [x] label  |
|      |   - selected: accent thick frame               |      basis  |
|      |                                                |  [ ] label  |
|      |                                                |      basis  |
|      |                                                |-------------|
|      |                                                | manual list |
+------+------------------------------------------------+-------------+
```

- **pages レール**（左・60pt）: 複数ページ時のみ表示。サムネイル＋選択状態。ページごとに独立した編集状態を持つ。
- **canvas**（中央）: 基準画像（`AnalyzedPage.page.cgImage`）をフィット表示。ズーム 1–4×スライダ＋Fit/100%、パンはスクロール。
  表示座標変換は **`CoordinateSpace.viewRect(_:in:)`／逆変換は `normRect(fromViewRect:in:)`・
  `normPoint(fromViewPoint:in:)` のみ**を使う（手変換禁止・core-design.md §1.1。逆変換は 0..1 クランプ済み）。
  ※ `in size:` に渡すのは**画像の表示領域サイズ**（レターボックス余白を含むビュー全体ではない）。
- **inspector**（右・320pt固定）: 上から順に
  1. **種別**: `Picker`。選択肢 = `classification.ranking` の各 `preset.displayName`（score併記・スコア0も含む全種別）。
  2. **warnings バナー**（黄）: `preset.warnings` を全件表示（例: マイナ裏「この面は提出できない場面が多い」）。
  3. **候補リスト**: 各行 = チェックボックス（`isOn`）＋ `label` ＋ 信頼度%（あれば）＋ **`basis`（必ず常時表示・キャプション灰色）**。
     固定領域は〈固定〉、検出子は〈自動〉のソースバッジ。
  4. **手動マスクリスト**: 矩形/ブラシの追加分。各行に削除ボタン。
- **候補ゼロ時**: リスト位置に「マスク候補が見つかりませんでした。手動ツールで塗ってください」を表示。

### 2.1 インタラクション仕様

- リスト行ホバー ↔ キャンバス該当矩形ハイライトを**双方向同期**（`selectedCandidateID`）。
- キャンバスの候補クリック = 選択。**ダブルクリック = isOn トグル**。リストのチェックボックスでもトグル。
- **選択マスクの直接編集（v1.0.3・2026-07-07 dogfood起点）**: 選択ツールで選択中のマスク
  （自動候補・手動矩形とも）に四隅ハンドルを表示。枠内ドラッグ=移動／ハンドル=リサイズ／⌘Z対応
  （1ドラッグ=1操作）。Deleteキー: 自動候補=チェックOFF・手動矩形=削除。
  自動候補の矩形編集は candidatesEdited 扱い（種別変更・切り抜き変更の確認対象）。
  候補と手動矩形の選択は排他（`selectedCandidateID` / `selectedManualRectIndex`）。
- ツール（toolbar セグメント）:
  - **選択**（既定）: 上記クリック操作。
  - **矩形**: ドラッグで `ManualMask.rects` に追加（正規化・左下原点へ変換して保持）。
  - **ブラシ**: ドラッグで `BrushStroke`（点列＋幅）。幅スライダ = 正規化 0.01–0.10（既定 0.03・画像短辺比）。
- **Undo（⌘Z）**: 手動マスクの追加/削除のみ `UndoManager` で対応。候補トグルは対象外（チェックで即戻せるため）。
- **仕上がりプレビュー** トグル: ON = ON候補＋手動を**完全不透明の黒**で描画（burnIn と同じ見え方）。編集は選択ツールのみ可。
- **種別変更**: `MaskingPipeline.analyze(url:forcedType:)` を再実行（sourceURL 必須・enhance差があるため再解析が正）。
  候補の isOn 編集は破棄・**手動マスクは保持**。isOn を1つでも編集済みなら確認アラート（「候補の編集内容は失われます」）。
- **rectified == false**: キャンバス上部にバナー「書類の輪郭を検出できませんでした。画像全体をそのまま使用しています」。

## 3. 書き出しフロー

1. toolbar「書き出す…」→ **書き出しシート**:
   - 形式 Picker（PDF / JPEG / PNG。初期値 = 設定の既定値）
   - 検索可能PDF トグル（PDF選択時のみ有効）／ JPEG品質スライダ（JPEG時のみ）
   - サマリ行: 「マスク 5箇所（自動 3・手動 2）／ 2ページ」
   - `preset.warnings` の再掲（該当ページがあれば）
   - **適用マスク0箇所のページがある場合**: 赤字警告を表示し、ボタンラベルを「マスクなしで書き出す」に変える（無確認保存はしない）
2. 「保存…」→ `NSSavePanel`。既定名 = `<先頭ファイル名>-masked.<ext>`。
   - PDF: 全ページ1ファイル。JPEG/PNG: 複数ページは `<名前>-1.jpeg, -2.jpeg…` に分割保存。
3. 実処理: ページごとに `MaskRendering.burnIn(page:masks:strokes:)`（masks = `effectiveMaskRects`）→
   `RenderedPage(image:ocrItems:maskRects:)` → `Exporting.export(_:options:to:)`。**UI側で描画・メタデータ処理を自前実装しない**。
4. 完了: トースト＋「Finderで表示」。toolbar「新しい書類」で Empty へ（セッション破棄）。

## 4. 設定画面（Tameo型: TabView・SettingsStore）

「一般」1タブのみ（MVP）。`SettingsStore`（`@Observable`・UserDefaults、**テスト用に suite 注入可能**＝Tameo型）:

| キー | 型 | 既定値 | 用途 |
|---|---|---|---|
| defaultExportFormat | pdf/jpeg/png | pdf | 書き出しシート初期値 |
| searchablePDF | Bool | true | 同上 |
| jpegQuality | Double | 0.9 | 同上 |
| enhancerAmount | Float | 1.0 | `PipelineTuning(enhancerAmount:)` へ注入（紙書類のみ効く旨をキャプション表示） |
| faceMaskDefaultOn | Bool | false | analyze 後、`source == .detector(.face)` の候補の isOn を true に上書き |

## 5. 構成・状態モデル

```
Fuseo/                     ← アプリターゲット（XcodeGen）
  FuseoApp.swift           --uitest / --uitest-fixture <path> フック（Tameo型）
  Info.plist  Assets.xcassets
  Services/ AppState.swift（@Observable・Stage＋pages）
            AnalysisService.swift（MaskingPipeline保持・直列実行・MainActor外）
            ExportService.swift（burnIn＋export の束ね）
            SettingsStore.swift
  Views/    DropView / ProcessingView
            Review/ ReviewView / CanvasView / CandidateListView / PageRailView / ExportSheet
            Settings/ SettingsView / GeneralSettingsTab
FuseoTests/   層1（ロジック。@testable import Fuseo）
FuseoUITests/ 層2（XCUITest）
```

- `PageState`: `sourceURL` / `analyzed: AnalyzedPage`（isOn・manual をここで編集）/ `selectedCandidateID` / ズーム等の表示状態。
- パイプライン実行は必ずバックグラウンド（UI凍結禁止）。`MaskingPipeline` は `AnalysisService` が1個保持して直列に使う。
- **MaskingCore のコードは変更禁止**（必要を感じたら中断して Fable へ）。

## 6. project.yml（Tameo流用の差分だけ）

- name/target: Fuseo、`PRODUCT_BUNDLE_IDENTIFIER: jp.co.ati-mirai.fuseo`、TEAM `8NY87P5TYV`、macOS 14.0、
  `MARKETING_VERSION: 0.1.0`、Hardened Runtime YES。
- **外部パッケージ依存ゼロ**（Sauce/KeyboardShortcuts/Sparkle は入れない。Sparkle は WP-7 で追加）。
  ローカルパッケージ参照のみ: `packages: MaskingCore: { path: . }`。
- テスト2ターゲット（FuseoTests = TEST_HOST 方式、FuseoUITests）＋ scheme の test アクション明示は Tameo と同一。
- 既存の `Package.swift`（swift test / poc）は**そのまま生かす**。壊さないこと。

## 7. accessibilityIdentifier（層2テストの契約）

```
drop.zone / drop.openButton
review.typePicker / review.previewToggle / review.exportButton / review.newDocButton
review.tool.select / review.tool.rect / review.tool.brush / review.canvas
review.candidate.<ruleID>.toggle        （行本体は review.candidate.<ruleID>）
review.manual.row.<index> / review.manual.delete.<index>
export.format / export.searchable / export.quality / export.confirmButton
settings.general.format / settings.general.searchable / settings.general.quality /
settings.general.enhance / settings.general.faceDefault
```

## 8. テスト（Tameo型・層1/層2）

- 起動フック: `--uitest` = 自動処理等の副作用停止／`--uitest-open-settings` = 設定を開く／
  `--uitest-fixture <path>` = 指定画像を起動時に自動ロードして Review まで進める。
- **フィクスチャは合成画像のみ**（UIテスト側で CGContext 生成→一時ディレクトリへ書き出して渡す。
  文字なし単色矩形で良い＝generic 種別・候補ゼロ→手動ツール経路を試験）。**fixtures-private/ をテストから参照しない**。
- 層1（FuseoTests）: SettingsStore の既定値/永続化、AppState の状態遷移（フェイク AnalysisService）、
  faceMaskDefaultOn の isOn 上書き、書き出し既定ファイル名、ゼロマスク判定。
- 層2（FuseoUITests）: 起動→フィクスチャ自動ロード→Review 表示→矩形ツールでドラッグ→手動リストに1件→
  書き出しシート表示→「マスクなし」警告が出ない、のスモーク1本＋設定画面の開閉/値変更1本。

## 9. 完了条件（WP-5）

1. `swift test` 全緑（**29件**＋追加分）かつ poc 回帰ベースライン一致（CLAUDE.md 記載: 3/2/2/1）
2. `xcodegen generate` → `xcodebuild -scheme Fuseo test` で層1・層2緑
3. 4類型のエンドツーエンド（ドロップ→確認→書き出し）が GUI で完走（Fable が fixtures-private で目視検証する）
4. 絶対条件の目視確認: 確認画面を経ない保存経路がない／basis が全候補に表示される／warnings が表示される

## 9.5 切り抜きの手動調整（v1.0.1追加・2026-07-07 dogfoodフィードバック起点）

自動の書類検出が変な台形を拾ったときのフォールバック。**Fable実装**（幾何・座標のため）。

- コアAPI: `Quad`（正規化・左下原点の四隅）／`VisionRectifier.cropPreview(imageAt:)`
  → `(originalImage, detectedQuad?, confidence?)`／`MaskingPipeline.analyze(url:forcedType:manualQuad:)`
  （manualQuad指定時は検出をスキップしてその四隅で台形補正。正立化・OCR・分類・ルールは通常どおり）
- UI: インスペクタ種別の上に「切り抜きを調整…」ボタン（`review.cropButton`）。検出失敗バナーにも同ボタン。
  シート（`crop.sheet`）: 元写真をフィット表示＋四隅ハンドル（円・44pt当たり判定）ドラッグ。四辺の線と外側の減光。
  初期位置=前回の manualQuad → 自動検出quad → 全体、の優先順。
  ボタン: 「自動検出に戻す」（検出quadが無ければ非活性）「全体を使う」「キャンセル」「適用」（`crop.apply` 等）。
  凸でない四角形のときは適用を非活性（`Quad.isConvex`）。
- 適用時: `analyze(url:forcedType:現在の強制種別, manualQuad:)` で再解析。**手動マスク・候補isOn編集は破棄**
  （基準画像の座標系が変わるため保持不能）。編集があれば確認アラートを挟む。
- `PageState.manualQuad` に保持し、**種別変更の再解析にも必ず引き継ぐ**（切り抜きが勝手に戻る事故の防止）。
- ハンドルの座標変換も `CoordinateSpace.normPoint(fromViewPoint:)`／`viewPoint(_:in:)` のみ使用。

## 10. Opus への注意（再掲・厳守）

- 設計判断が必要になったら**その場で決めずに中断してエスカレーション報告**（本書・core-design.md にない挙動を発明しない）
- 座標変換は `CoordinateSpace` 経由のみ（逆変換 `normRect(fromViewRect:in:)`・`normPoint(fromViewPoint:in:)` は
  Fable が追加済み。これ以外の変換が必要になったら追加せず報告）
- fixtures-private/ の参照・コミット・git 操作・生番号のログ/報告記載は禁止
