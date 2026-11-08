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
- 「書類の種類」を選ぶと、その書類で普通伏せる欄の一覧が出ます。矩形かブラシで塗ってください。
- 斜めに撮った写真は「切り抜き」で四隅を合わせると、まっすぐに直せます。
- 「書き出し」で仕上がりを確認してから保存します。塗った部分は画素そのものが黒になり、
  元に戻せません。撮影日時などの情報（メタデータ）も書き出したファイルには入りません。
- 自動で見つけるのは QR コード・バーコードだけです。番号や氏名などは自動では塗られません。
  保存する前に、必ず仕上がりをご自身で確認してください。
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
- 不具合・セキュリティの連絡: fuseo@ati-mirai.co.jp
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
- Drag images (JPEG / PNG / BMP / TIFF) or PDFs onto the window, or use "Open files".
- Only QR codes and barcodes are found automatically. Numbers and names are not painted for you.
  Always check the result on the export sheet before saving.
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
Web: https://fuseo.ati-mirai.co.jp  Contact: fuseo@ati-mirai.co.jp  ATI Inc.
