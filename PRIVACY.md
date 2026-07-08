# Privacy Policy / プライバシーポリシー

_Fuseo — macOS ID-document redaction tool by ATI Inc. (ATI株式会社)_

**Last updated: 2026-07-08**

> **Fuseo processes your documents entirely on your Mac. Nothing is ever sent off your device.**
> **Fuseo は書類の処理をすべてあなたの Mac の中だけで行います。外部へ送信することは一切ありません。**

---

## English

### 1. Summary

Fuseo is a local-only tool for redacting ID documents. It opens your document photos, finds and blacks out sensitive areas, and exports a clean copy — all on your own Mac. It never transmits your documents, images, or detection results anywhere. There are **no servers, no accounts, no analytics, no telemetry, and no crash reporting**. ATI Inc. cannot see your documents, because they never leave your device.

### 2. What data Fuseo handles

- **Document images you open** — the photos you drag in or choose (JPEG / PNG / HEIC / TIFF). These are read into memory to be cropped, deskewed, classified, and redacted.
- **Detection results** — the redaction candidates found in your document (their positions and labels), used only to build the on-screen review.
- **Exported files** — the redacted PDF / JPEG / PNG that you choose to save, written only to the location you pick.
- **Settings** — your preferences (default export format, searchable-PDF, JPEG quality, scan-finish strength, whether to mask faces by default).

### 3. Where it is processed and stored

All processing happens **in memory on your Mac**. Fuseo does not keep a hidden copy of your documents: the only files it writes are the exports you explicitly save and your own settings. Your original photo files are left untouched. Nothing is synced to iCloud, uploaded to any server, or shared with ATI Inc. or any third party.

### 4. Redaction is irreversible by design

Masks are **burned into the pixels** (the area is replaced with solid black), not drawn as a removable annotation or layer. Exported files are written **without metadata** (EXIF, GPS, thumbnails are not carried over), and for searchable PDFs the text under a mask is excluded from the text layer. This is a deliberate requirement: what you redact cannot be recovered from the exported file.

### 5. No collection, no tracking

Fuseo contains **no analytics, telemetry, advertising, or identifiers**. It does not phone home, does not count launches, and does not report crashes to us.

### 6. Network access

Fuseo's document processing works **fully offline**. The only network activity (when enabled in a future release) is an optional **automatic update check** via Sparkle: the app contacts ATI's update feed to see whether a newer version exists. As with any download, this reveals your IP address and the app version to the update server — but **no document data, image data, personal data, or identifiers are ever sent**. You can disable automatic update checks.

### 7. Sensitive documents — your responsibility

Fuseo is a tool that helps you redact, but it does not decide what is legal to submit. Automatic detection proposes candidates only; **you review and confirm every redaction before saving.** Some documents (for example those you must retain as originals) cannot be replaced by a redacted copy, and some — such as the back of a My Number Card — often cannot be submitted at all. Please confirm the requirements before you submit. See the [User Manual](docs/user-manual.md).

### 8. Data retention and deletion

Fuseo keeps no library of your documents. Once you close a document or quit the app, the in-memory copy is gone; only the exports you saved and your settings remain, both under your control. Removing the app deletes its settings.

### 9. Children

Fuseo is a general-purpose utility, not directed at children, and collects no personal information from anyone.

### 10. Open source

Fuseo's core is open source (MIT). You can verify exactly what it does — including that it makes no network calls to process your documents — in its public source code.

### 11. Changes

Updated versions of this policy are published with each release and dated above.

### 12. Contact

ATI Inc. (ATI株式会社) — https://fuseo.ati-mirai.co.jp · security & privacy contact: fuseo@ati-mirai.co.jp

---

## 日本語

### 1. 概要

Fuseo は完全ローカルの本人確認書類・黒塗りツールです。書類の写真を読み込み、見せる必要のない部分を見つけて黒く塗りつぶし、提出用のきれいなコピーを書き出します。これらの処理はすべてあなたの Mac の中だけで行われ、書類・画像・検出結果をどこにも送信しません。**サーバー・アカウント・解析・テレメトリ・クラッシュレポートは一切ありません。** データは端末から出ないため、ATI株式会社があなたの書類を見ることはできません。

### 2. Fuseo が扱うデータ

- **読み込んだ書類の画像** — ドラッグまたは選択した写真（JPEG／PNG／HEIC／TIFF）。切り抜き・傾き補正・種別判定・黒塗りのためにメモリ上に読み込みます。
- **検出結果** — 書類内で見つかった黒塗り候補（位置・ラベル）。画面での確認表示にのみ使います。
- **書き出したファイル** — あなたが保存を選んだ黒塗り済みの PDF／JPEG／PNG。指定した保存先にのみ書き込みます。
- **設定** — 既定の書き出し形式・検索可能PDF・JPEG品質・スキャン風仕上げ強度・顔写真を既定でマスクするか、などの環境設定。

### 3. 処理・保存の場所

処理はすべて**あなたの Mac のメモリ上**で行います。Fuseo が書類の隠しコピーを持つことはありません。書き込むファイルは、あなたが明示的に保存した書き出しファイルと、あなた自身の設定だけです。元の写真ファイルはそのまま残します。iCloud 同期も、サーバーへのアップロードも、ATI株式会社や第三者への共有も行いません。

### 4. 黒塗りは設計上、復元できません

マスクは**ピクセルへの焼き込み**（対象領域を不透明な黒で置換）であり、あとから剥がせる注釈やレイヤーではありません。書き出したファイルは**メタデータなし**（EXIF・GPS・サムネイルを引き継がない）で生成し、検索可能PDFではマスク下の文字をテキスト層から除外します。これは意図的な要件で、黒塗りした内容は書き出しファイルから復元できません。

### 5. 収集なし・追跡なし

Fuseo には**解析・テレメトリ・広告・識別子は一切含まれません**。外部へ通信して起動回数を数えたり、クラッシュを私たちに報告したりすることはありません。

### 6. ネットワーク

書類の処理は**完全オフライン**で動作します。将来のリリースで有効化される唯一の通信は、任意の**自動アップデート確認**（Sparkle）です：ATI のアップデートフィードへ新バージョンの有無を確認します。一般的なダウンロードと同様 IP とバージョンが伝わりますが、**書類データ・画像データ・個人情報・識別子は一切送信しません**。自動確認は無効化できます。

### 7. 機微な書類 — ご自身の責任

Fuseo は黒塗りを手伝う道具であり、何を提出してよいかを判断するものではありません。自動検出はあくまで候補の提示で、**保存前にすべての黒塗りをあなたが確認・確定します。** 原本の保存が義務づけられた書類は黒塗りコピーで代替できず、マイナンバーカードの裏面のようにそもそも提出できない場面が多い書類もあります。提出前に要件をご確認ください（[使い方ガイド](docs/user-manual.md)）。

### 8. 保持と削除

Fuseo はあなたの書類をため込みません。書類を閉じるかアプリを終了すると、メモリ上のコピーは失われ、残るのは保存した書き出しファイルと設定だけで、どちらもあなたの管理下にあります。アプリを削除すると設定も削除されます。

### 9. 子どもについて

Fuseo は一般的なユーティリティであり、子どもを対象とせず、誰からも個人情報を収集しません。

### 10. オープンソース

Fuseo のコアはオープンソース（MIT）です。書類の処理にネットワーク通信を行わないことを含め、公開ソースコードで確認できます。

### 11. 変更

本ポリシーの更新版はリリースとともに公開し、冒頭に日付を記します。

### 12. 連絡先

ATI株式会社 — https://fuseo.ati-mirai.co.jp ・セキュリティ／プライバシー連絡先：fuseo@ati-mirai.co.jp
