# Fuseo for Windows

本人確認書類や控除証明書 PDF の「見せなくてよい部分」を自分で塗って、**復元できない形**で提出用コピーにする Windows 用ツールです。
完全ローカル・無料・オープンソース（MIT）。macOS / iPhone 版と同じ書類の知識（プリセット）を使います。

- 0.2: Windows 標準の文字認識（`Windows.Media.Ocr`・端末内のみ）で書類の種類・向きを判定し、マイナンバー・免許証番号・保険証の記号番号・在留カード番号・QR コード・顔写真を候補にします（初期の ON/OFF はプリセットどおり・保存前の確認は必須）。フォルダの一括取り込みと、書類ごとの書き出し（`元の名前-masked`）。
- 0.3: 書類に文字を打ち込む「文字」の道具（申込書・申告書の記入。枠の位置と大きさ・文字サイズ・行間・字間・揃え・マス数・色は手で決める・
  改行は Shift+Enter）。文字も画像に焼き込み、黒塗りは常に文字の上に塗る。文字を入れた作業は保存形式 `fuseo-win/2`（文字が無ければ `fuseo-win/1` のまま）。
- 0.3.1: ツールバーのボタンにアイコンを付けた（Windows 標準の Segoe Fluent Icons／MDL2 Assets・名前も併記）。選んでいる道具を塗りで示し、書き出しを青いボタンに。太さはブラシのときだけ表示。
- 0.3.2: 「個人番号」欄のある A4 の申告書などの PDF が、マイナンバーカード（裏）と判定されることがあった問題を修正（PDF の紙の大きさのページはカード類と判定しない・Mac 0.3.0 と同じ規則）。
- 配布は GitHub Releases（タグ `win-v*`）と窓の杜・Vector。

## 構成
- `src/Fuseo.Core` — 座標・データの型・プリセット・種別判定と番号検出・焼き込み・書き出し（UI 非依存・.NET 8）
- `src/Fuseo.Input` — 画像／PDF の取り込み・QR（ZXing.Net）・文字認識と顔検出（Windows SDK）
- `src/Fuseo.App` — WPF アプリ（`Fuseo.exe`）
- `tests/` — xUnit、画面操作の E2E（`tests/Fuseo.E2E`）、合成書類での受け入れ検査（`tests/Acceptance`）
- 技術上の約束: [docs/contract.md](docs/contract.md)

## ビルド
```
dotnet test Fuseo.Windows.sln
```

## 配布物を作る（Windows 上で）
```
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1
```
`publish\Fuseo-win-<ver>-x64.zip` / `-arm64.zip` と `SHA256SUMS.txt` ができます。exe は自己完結の単一ファイル（pdfium.dll も同梱）で、
zip の中身は `Fuseo.exe`・`README.txt`（`packaging/README.txt` に版番号を入れたもの）・`LICENSE.txt`・`THIRD-PARTY-NOTICES.txt` の 4 つだけです。
署名はしていません（初回起動時に SmartScreen の警告が出ます。通し方は README.txt に書いてあります）。
