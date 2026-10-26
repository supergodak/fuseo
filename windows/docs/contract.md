# Fuseo for Windows — 技術契約（公開版）

Windows 版の実装が守る約束です。ここに書いたことはテストで固定します。

## 1. 墨消しの正しさ（最優先）
- マスクは**ラスタ焼き込み**（画素の置き換え）。注釈・レイヤー・半透明の重ね塗りはしない。
- 焼き込みは**新しい画素バッファ**に対して行い、マスク領域内は完全不透明の黒 `#000000FF`。
- 書き出しは焼き込み済みのページ（`BurnedPage`・焼き込み処理だけが作れる型）しか受け取らない。
- 書き出したファイルに**元画像のメタデータを持ち越さない**。エンコーダは自前で、書く内容を限定している。
  - JPEG: SOI・APP0（JFIF・サムネイル無し）・DQT・SOF0・DHT・SOS・EOI のみ。EXIF・XMP・ICC・サムネイル・コメントを持たない。
  - PNG: IHDR・IDAT・IEND のみ。`tEXt` / `iTXt` / `zTXt` / `eXIf` / `tIME` / `iCCP` 等を持たない。
  - PDF（1.4）: 各ページは画像 1 枚と、それを置く命令 1 行だけ。`/Info` は `/Producer (Fuseo for Windows)` のみ。
    テキスト・フォント・注釈・XMP・アクション・`/ID` は入れない。元 PDF のページは捨てる。
  - 出力は不透明 RGB（透明部分は白に合成）。
- これらはファイルのバイト列を許可リストで検査するテストで固定している（`tests/Fuseo.Core.Tests/ExportTests.cs`）。
- 書き出しの前には必ず仕上がりの確認画面を通す。自動検出は候補であり、最終判断は利用者が行う。
- 自動検出（v0.1 は QR コード・バーコードのみ・ZXing.Net）の候補の箱は、記号の外枠より広めに取る（安全側）。
  プリセットにその検出のルールがある書類でだけ候補を出し、ON/OFF の初期値はルールの `defaultOn` に従う。

## 2. 通信しない
- アプリは通信機能を持たない（更新確認もしない）。通信系アセンブリを参照しないことをテストで確認する。

## 3. 座標
- 内部の座標は**基準ビットマップの正規化座標（0..1）・左上原点**ひとつだけ。
- 変換は `CoordinateSpace` だけを通す（`ToPixel` は外側へ切り上げ＝1px も欠けない）。
- 回転は取り込み時に画素へ焼き込む。以後のマスク座標は表示向きの画像上で持つ。
- 編集中の 90° 回転は、画像とマスク座標（手動・候補）を**必ず同じ回数だけ一緒に**回す（`QuarterTurn`）。取り込んだ画像は書き換えず、回転は Undo で戻せる。
- プリセット JSON の固定領域 `{x, yTop, w, h}` は左上原点で、そのまま読む。

## 4. 書類の知識
- プリセットは macOS / iPhone 版と同じ JSON（`Sources/MaskingCore/Resources/Presets`）を共有する。
- 全ルールに根拠（basis）が必須。

## 5. 入力
- 画像（JPEG / PNG / BMP / TIFF、HEIC は Windows の HEIF 拡張がある場合）と PDF。
- PDF は各ページを 300dpi・長辺 4000px 以内で画像化（白下地）。パスワードはメモリ内でのみ扱う。
- 開いているページと合わせて合計 50 ページまで（画像だけの取り込みも含む）。画像も長辺 4000px に縮小する（2026-10-26 ユーザー決定）。

## 6. 作業の自動保存（ライブラリ・`fuseo-win/1`）
- 保存先は**このパソコンの中だけ**: `%LOCALAPPDATA%\Fuseo\Library\<作業id>\`。通信・同期はしない。
  保存されるのは**黒塗り前の画像**を含むデータで、利用者が削除するまで残る（自動削除なし）。空の画面の一覧にその旨を出す。
- 1 作業 = 画面に開いているページ一式。中身は `document.json`・`pages/<ページid>.png`（取り込んだままの元画像・回転 0・可逆）・
  `thumbs/<ページid>.jpg`（一覧用・長辺 320px）。
- `document.json` の項目名は macOS 版の WorkDocument と同じ（`schemaVersion`=`"fuseo-win/1"`・`id`・`title`・`createdAt`・`updatedAt`・
  `status`（`inProgress`/`exported`）・`lastExportedName`・`pages[]{index, imageFile, thumbnailFile, sourceName, documentType,
  manualRotation, candidates, manual, pixelWidth, pixelHeight}`）。Windows で足す項目: `"origin": "topLeft"`、各ページの
  `"coordinateFrame": "rotatedImage"`・`fromPdf`・`sourcePageIndex`・`rawDetections`（padding 前・回転 0 基準）・`rawDetector`・
  `checkedRuleIds`。macOS 互換用の `ocr`/`manualQuad`/`analysisOptions` は空（`[]`/`null`）。
  - マスク・候補の座標は `manualRotation`（時計回り 90° の回数・0..3）で回した画像の上の正規化座標（左上原点）。`pixelWidth/Height` は元画像の寸法。
  - 候補は macOS と同じ形（`id`・`ruleID`・`label`・`box{x,y,w,h}`・`source{kind, detector}`・`confidence`・`isOn`・`basis`）、ブラシの点は `[x, y]`。
  - 列挙は camelCase の文字列、日時は ISO 8601・UTC・ミリ秒、JSON は UTF-8（BOM なし）・インデント付き。Undo 履歴は保存しない。
  - 形はゴールデンテストで固定（`tests/Fuseo.Core.Tests/WorkDocumentJsonTests.cs`）。
- **壊れない保存**: ページ画像とサムネイルは初めて保存するときに 1 回だけ、一時ファイル → 名前の変更で書く。`document.json` は毎回
  同じフォルダの一時ファイルに書いてから置き換える。途中で失敗しても既存の `document.json` は壊れず、一時ファイルは消す（残ったものは
  一覧を読むときに掃除する）。JSON から消えたページの画像は保存の後に消す。
- **読み込みの安全**: `imageFile`/`thumbnailFile` は**その作業フォルダ内の相対パスだけ**（英数字と `. _ -` の要素を `/` でつないだもの）。
  絶対パス・ドライブ指定・`..`・`\`・`:`・予約名は拒否し、解決後の完全パスが作業フォルダの中かも確かめる。
  版が違う・JSON が壊れている・値が範囲外（未知の列挙・回転が 0..3 以外など）・画像が無い作業は「読めない作業」として一覧に出し、
  削除だけできる（アプリは落ちない）。
- **自動保存**: 取り込み・手動マスク・候補の ON/OFF・回転・種別・チェックリストの印・ページの並べ替え/削除・Undo/Redo・書き出し完了の
  たびに、1 秒のデバウンスでバックグラウンド保存（中身は UI スレッドで写し取ってから別スレッドで書く）。失敗はステータスバーに出す。
  終了時は保留中の保存を確定してから閉じる。書き出し完了で `exported`、その後に編集したら `inProgress` に戻す。
- 「新しい書類」は今の作業をライブラリに残したまま（確認なし）空にする。開いた作業は元画像と状態をそのまま戻し、検出は走らせない。
- 削除（1 件・すべて）は確認ダイアログを通す。
