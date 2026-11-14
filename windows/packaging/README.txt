Fuseo for Windows {VERSION}
================================

（English follows the Japanese.）

本人確認書類や控除証明書 PDF の、見せなくてよい部分を自分で塗って、
復元できない形の提出用コピーにするツールです。無料・オープンソース（MIT）。
書類はこのパソコンの中だけで扱い、どこにも送信しません（通信機能がありません）。

■ 起動のしかた
1. zip を右クリック →「すべて展開」で、好きなフォルダに展開します。
2. 展開したフォルダの Fuseo.exe をダブルクリックします。
3. 「Windows によって PC が保護されました」と表示されたら、
   「詳細情報」→「実行」を押してください。
   電子署名（有料）を付けていない無料ソフトのため、この表示が出ます。
   ※ ダウンロードしたファイルが本物か確かめたい場合は、下の「改ざんの確認」をご覧ください。
4. 初回の起動は数秒かかることがあります（プログラムの部品を一時フォルダに展開するため）。

■ 使い方（短く）
- 画像（JPEG / PNG / BMP / TIFF）や PDF をウィンドウにドラッグするか、「ファイルを開く」で選びます。
  フォルダをドラッグするか「フォルダを開く」で選ぶと、フォルダの中の書類をまとめて取り込みます（合計 50 ページまで）。
- 取り込むと、Windows 標準の文字認識（このパソコンの中だけで動きます）で書類の種類と向きを判定し、
  マイナンバー・運転免許証番号・保険証の記号番号・在留カード番号・QR コードなどを「自動検出の候補」に出します。
  判定や検出は外れることがあります。違うときは「書類の種類」を選び直し、足りない所は矩形かブラシで塗ってください。
- 「書類の種類」を選ぶと、その書類で普通伏せる欄の一覧が出ます。
- 斜めに撮った写真は「切り抜き」で四隅を合わせると、まっすぐに直せます。
- 申込書・申告書などに文字を打ち込めます（0.3〜）。道具の「文字」を選び、書きたい欄の上をドラッグして枠を作り
  （クリックだけなら標準の大きさの枠）、下に出る入力欄に打って Enter で確定します。改行は Shift+Enter、やめるときは Esc。
  どこに・どの大きさで打つかは自分で決めます（欄やマスを自動で探す機能はありません）。
  右側の「文字」で、書体（ゴシック・明朝）・サイズ（pt）・行間・字間・横と縦の揃え・マス数（個人番号の 12 マスなど）・
  色（6 色か「その他」）・位置と大きさ（mm）を変えられます。「枠の高さに合わせる」で文字の大きさを 1 回だけ計算して入れます。
  枠はドラッグで移動、つまみで大きさ変更、矢印キーで 1 ピクセル（Shift で 10）、Ctrl+D で複製、Delete で削除、ダブルクリックで打ち直し。
  一度変えた設定は次の枠にも引き継がれます（マス数を除く）。文字が枠からはみ出すと枠が赤い点線になります。
  打った文字も画像に焼き込まれ、黒塗りは文字より上に塗られます（伏せた所に文字は残りません）。
  切り抜き・回転をするとそのページの文字は消えます（先に向きを整えてから記入してください）。
  文字を入れた作業は 0.2 以前の Fuseo では開けません。書き出したファイルは後から文字だけ直せないので、
  直すときは「作業中の書類」から開き直してください。
- 「書き出し」で仕上がりを確認してから保存します。塗った部分は画素そのものが黒になり、
  元に戻せません。撮影日時などの情報（メタデータ）も書き出したファイルには入りません。
- 「一覧」でページ全体を見渡せます。マスクが 1 つもないページには赤い「マスクなし」が付きます。
- 複数の書類を開いているときは、「書類ごとに分ける」で書類ごとに「元の名前-masked」として保存できます
  （同じ名前のファイルがあれば -2, -3 … を付け、既存のファイルは上書きしません）。
- 氏名・住所などは自動では塗られません。自動検出も完全ではありません。
  保存する前に、必ず仕上がりをご自身で確認してください。
- 番号の自動検出には「日本語の文字認識」が必要です（日本語版 Windows には通常入っています）。
  入っていない場合は、設定 › 時刻と言語 › 言語 › 日本語 › 言語のオプション で追加できます（追加しなくても手で塗れます）。
- 原本の保存が必要な書類は、黒塗りしたコピーで代わりにできません。
  マイナンバーカードの裏面は、そもそも提出できない場面が多い書類です。提出先の指示をご確認ください。
- HEIC（iPhone の写真）は、Microsoft Store の「HEIF 画像拡張機能」が入っている場合だけ開けます。
  開けない場合は、iPhone 側で JPEG にしてから取り込んでください。

■ 保存される場所
- 作業中の書類（塗る前の画像を含みます）: %LOCALAPPDATA%\Fuseo\Library
  途中で閉じても続きから再開できるよう、自動で保存します。このパソコンの中だけで、
  あなたが削除するまで残ります。アプリの一覧から 1 件ずつ、または「設定」からまとめて削除できます。
- 設定: %LOCALAPPDATA%\Fuseo\settings.json
- 書き出したファイル: あなたが選んだ場所だけ。
- レジストリは使いません。

■ 削除（アンインストール）
1. Fuseo.exe（展開したフォルダごと）を削除します。
2. 作業中の書類と設定も消す場合は、%LOCALAPPDATA%\Fuseo フォルダを削除します。
3. プログラムの部品の展開先 %TEMP%\.net\Fuseo も削除してかまいません（書類は入っていません）。

■ 改ざんの確認（任意）
PowerShell で次を実行し、表示された値が配布ページの SHA-256 と一致すれば、
配布したままのファイルです（大文字・小文字の違いは無視してください）。
  Get-FileHash .\Fuseo-win-{VERSION}-x64.zip
（ARM64 版は -arm64.zip）

■ 動作環境
Windows 10（1809 以降）/ Windows 11。x64 版と ARM64 版があります。
.NET などの追加インストールは不要です。

■ ライセンス・お問い合わせ
- Fuseo: MIT License（LICENSE.txt）。同梱している部品のライセンスは THIRD-PARTY-NOTICES.txt。
  Fuseo.exe に含まれる Microsoft の部品（Windows SDK for .NET・文字認識と顔検出の呼び出しに使用）は MIT の対象外で、
  Microsoft の Windows SDK のライセンス条項に従います。Fuseo.exe を使う・配る場合はその条項にも従ってください。
- Web: https://fuseo.ati-mirai.co.jp
- 不具合の報告: https://github.com/supergodak/fuseo/issues
- ソースコード: https://github.com/supergodak/fuseo
ATI株式会社


--------------------------------------------------------------------------------
English
--------------------------------------------------------------------------------

Fuseo blacks out the parts of ID documents and PDFs that you do not need to show,
and saves a copy where the hidden parts cannot be recovered. Free and open source (MIT).
Your documents stay on this PC. Fuseo has no network features and sends nothing anywhere.

Getting started
1. Right-click the zip and choose "Extract All".
2. Double-click Fuseo.exe in the extracted folder.
3. If "Windows protected your PC" appears, click "More info", then "Run anyway".
   This appears because the app is not code-signed (signing costs money; Fuseo is free).
   To make sure your download has not been altered, see "Checking the download" below.
4. The first launch can take a few seconds while program parts are unpacked to a temporary folder.

Notes
- Drag images (JPEG / PNG / BMP / TIFF), PDFs or a folder onto the window, or use "Open files" / "Open folder"
  (up to 50 pages in total).
- Windows' built-in text recognition (runs on this PC only) detects the document type and orientation and suggests
  Japanese ID numbers and QR codes as "Auto-detected candidates". Detection can miss or be wrong; names and addresses
  are never painted for you. Always check the result on the export sheet before saving.
- Typing text onto forms (0.3+): choose the "Text" tool, drag a box over the field (a click makes a default-size box),
  type in the box below it and press Enter (Shift+Enter for a new line, Esc to cancel). You decide where and how large;
  nothing is placed automatically. The "Text" panel sets font (Gothic / Mincho), size (pt), line and letter spacing,
  alignment, cells (one character per cell, e.g. for numbers), color and the position/size in mm. Typed text is burned
  into the image and masks are always painted over it. Rotating or cropping a page removes its text.
  Work with text cannot be opened in versions before 0.3.
- With several documents open, "Separate file per document" saves each as "<original name>-masked"
  (adds -2, -3 ... instead of overwriting existing files).
- Number detection needs Japanese text recognition (Settings > Time & language > Language & region > Japanese).
- Painted areas are replaced with black pixels and cannot be recovered. Metadata is not written.
- HEIC photos open only if Microsoft's "HEIF Image Extensions" is installed.

Where data is kept
- Documents in progress (including the unpainted images): %LOCALAPPDATA%\Fuseo\Library
  Kept on this PC until you delete them, from the list in the app or all at once in Settings.
- Settings: %LOCALAPPDATA%\Fuseo\settings.json. No registry entries.

Uninstalling
Delete Fuseo.exe, then the folder %LOCALAPPDATA%\Fuseo if you also want to remove your work and settings.
The folder %TEMP%\.net\Fuseo holds unpacked program parts only and can be deleted too.

Checking the download (optional)
In PowerShell, run  Get-FileHash .\Fuseo-win-{VERSION}-x64.zip  and compare the value with the
SHA-256 on the download page (case does not matter). Use -arm64.zip for the ARM64 build.

Requirements: Windows 10 (1809 or later) / Windows 11, x64 or ARM64. Nothing else to install.

License: MIT (LICENSE.txt). Bundled components: THIRD-PARTY-NOTICES.txt.
The Microsoft components inside Fuseo.exe (Windows SDK for .NET, used to call text recognition and face detection)
are not covered by the MIT License; they are subject to Microsoft's Windows SDK license terms, which also apply
when you use or redistribute Fuseo.exe.
Web: https://fuseo.ati-mirai.co.jp  Issues: https://github.com/supergodak/fuseo/issues  ATI Inc.
