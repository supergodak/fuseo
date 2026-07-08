# Fuseo（ふせお）

**本人確認書類を「提出してよい状態」にする、完全ローカルの macOS アプリ。**
マイナンバーや免許証番号など、提出先に見せる必要のない部分を復元できない方式で黒塗りし、きれいなスキャン風の PDF／画像として書き出します。

Fuseo は**「提出事故を減らす道具」**です。名称は「伏せる」に由来し、姉妹アプリ [Tameo（ためお／「溜める」）](https://github.com/supergodak/tameo)と同じ命名系譜です。

🌐 Website: https://fuseo.ati-mirai.co.jp  ·  📜 [プライバシーポリシー](PRIVACY.md)  ·  ⚖️ [MIT License](LICENSE)  ·  🍎 macOS 14+

> **状態: 開発中（v0.1.0）。** Developer ID 署名＋Apple 公証の DMG で配布予定です。使い方の詳細は [docs/user-manual.md](docs/user-manual.md) を参照してください。

---

## 日本語

### Fuseo とは

役所や勤務先に本人確認書類を提出するとき、見せる必要のない番号（マイナンバー・免許証番号など）まで一緒に写ってしまうことがあります。Fuseo は、そうした部分を Mac の中だけで見つけて黒塗りし、提出用のきれいなコピーを作ります。写真がインターネットに送られることは一切なく、アカウント登録も不要です。

**自動検出は「候補」の提示です。何をどこまで塗るかは、保存前に必ずあなたが画面で確認して決めます。** 「自動で完璧」を謳うツールではありません。

### 3つの安心

1. **完全ローカル** — 書類の処理はすべてお使いの Mac の中だけで完結します。写真や検出結果が外部に送信されることはありません。アカウントもテレメトリもありません。
2. **消したところは復元できません** — 黒塗りは画像そのものを黒いピクセルで塗りつぶす「焼き込み」方式です。あとから透けて見えたり、レイヤーを剥がして復元されたりすることはありません。撮影日時・位置情報などのメタデータも引き継ぎません。
3. **勝手に保存しません** — 塗る場所は必ずあなたが確認してから書き出します。保存前の確認 UI を外すことはありません。

### 対応している書類

| 書類 | 自動で黒塗り候補になる箇所（例） |
|---|---|
| マイナンバーカード（表） | 性別欄・臓器提供意思欄 |
| マイナンバーカード（裏） | 個人番号（12桁）・QRコード |
| 運転免許証（表） | 免許証番号 |
| 運転免許証（裏） | 臓器提供意思表示欄（備考欄は残します） |
| 健康保険証・資格確認書・住民票 | 記号・番号・個人番号など（対応拡充中） |

上記以外の書類も読み込めます。自動候補が出ないことがありますが、手動の矩形・ブラシツールで自由に塗れます。各候補には「なぜここを塗るのか」の根拠が表示されます。

> ⚠️ **提出前に確認してほしいこと**
> - **原本の保存が義務づけられている書類**（扶養控除等申告書など）は、黒塗りしたコピーで代替できません。Fuseo の対象外です。
> - **マイナンバーカードの裏面は、そもそも提出できない場面が多い書類です**（番号法で複写が制限されています）。提出を求められているのが本当に裏面なのか、先に確認してください。
> - 自動検出は完璧ではありません。**塗り漏れの最終確認はご自身でお願いします。**

### インストール

- **直接ダウンロード（公証済み DMG）:** [製品サイト](https://fuseo.ati-mirai.co.jp) または [GitHub Releases](https://github.com/supergodak/fuseo/releases/latest) から最新版を入手し、**Fuseo** を `/Applications` にドラッグします。
- **Homebrew:** 準備中（`brew install --cask fuseo` を予定）。

**macOS 14（Sonoma）以降**（Apple Silicon／Intel）が必要です。Apple 純正フレームワーク（Vision・Core Image・PDFKit・SwiftUI）のみで動作し、外部依存はありません。

### 使い方（要約）

1. **起動** — アプリケーションフォルダの Fuseo をダブルクリック。
2. **読み込む** — 書類の写真をウィンドウにドラッグ＆ドロップ（JPEG／PNG／HEIC／TIFF、複数枚可）。
3. **確認する** — 切り抜き・傾き補正・種別判定・黒塗り候補の検出まで自動で進みます。候補が正しいか画面で確認します。
4. **調整する** — 塗らない候補はチェックを外す。足りないところは矩形・ブラシで塗り足す。
5. **書き出す** — PDF／JPEG／PNG を選んで保存。仕上がりプレビューで実際の塗りつぶし状態を確認できます。

詳しい操作・確認画面の見かた・書き出しオプションは **[docs/user-manual.md](docs/user-manual.md)** にまとめています。

### 動作要件

- macOS 14（Sonoma）以降 · Apple Silicon／Intel

### ライセンス

[MIT](LICENSE) © 2026 ATI株式会社。詳細は [LICENSE](LICENSE) を参照してください。

---

## English

### What is Fuseo?

**Fuseo is a completely local macOS app that gets ID documents ready for submission.** When you send an ID document to a government office or an employer, sensitive numbers you don't need to reveal (My Number, driver's license number, etc.) often end up in the photo too. Fuseo finds those parts — entirely on your Mac — and redacts them, producing a clean, scan-style PDF or image copy. Nothing is ever sent over the internet, and no account is required.

Fuseo is a **tool for reducing submission accidents**. Its automatic detection only *proposes candidates*; **you always review and decide what gets redacted before anything is saved.** It does not claim to be "automatically perfect."

### Three assurances

1. **Fully local.** All document processing happens only on your Mac. Photos and detection results never leave your device. No accounts, no telemetry.
2. **Redactions cannot be recovered.** Masks are *burned in* — the pixels themselves are painted black. Nothing shows through later, and no layer can be peeled off to reveal what was hidden. Metadata such as capture date and GPS location is not carried over.
3. **Nothing is saved without your say-so.** You always confirm the redactions on screen before exporting. The pre-save confirmation step is never removed.

### Supported documents

| Document | Auto-suggested redaction areas (examples) |
|---|---|
| My Number Card (front) | Sex field, organ-donation field |
| My Number Card (back) | Individual Number (12 digits), QR code |
| Driver's license (front) | License number |
| Driver's license (back) | Organ-donation section (the remarks field is kept) |
| Health insurance card / eligibility certificate / residence certificate | Symbol, number, individual number, etc. (expanding) |

You can open other documents too; automatic candidates may not appear, but you can freely redact with the manual rectangle and brush tools. Every candidate shows the *basis* — why that area is proposed for redaction.

> ⚠️ **Before you submit**
> - Documents you are legally required to keep as originals (e.g. certain tax withholding declarations) **cannot** be replaced by a redacted copy. They are out of scope for Fuseo.
> - **The back of a My Number Card often cannot be submitted at all** (copying is legally restricted). Confirm whether the back side is really what you were asked to provide.
> - Automatic detection is not perfect. **The final check for missed spots is up to you.**

### Install

- **Direct download (notarized DMG):** grab the latest from the [project site](https://fuseo.ati-mirai.co.jp) or [GitHub Releases](https://github.com/supergodak/fuseo/releases/latest), then drag **Fuseo** into `/Applications`.
- **Homebrew:** coming soon (`brew install --cask fuseo` planned).

Requires **macOS 14 (Sonoma) or later** (Apple Silicon or Intel). Built entirely on Apple's own frameworks (Vision, Core Image, PDFKit, SwiftUI) with no third-party dependencies.

### Usage (in brief)

1. **Launch** Fuseo from your Applications folder.
2. **Load** a document photo by dragging it into the window (JPEG / PNG / HEIC / TIFF; multiple pages OK).
3. **Review** — cropping, deskew, document-type detection, and redaction candidates run automatically. Check them on screen.
4. **Adjust** — uncheck candidates you don't want; add missing spots with the rectangle or brush tool.
5. **Export** — choose PDF / JPEG / PNG and save. A preview shows exactly how the redactions will look.

See **[docs/user-manual.md](docs/user-manual.md)** for the full guide.

### Requirements

- macOS 14 (Sonoma) or later · Apple Silicon or Intel

### License

[MIT](LICENSE) © 2026 ATI Inc. (ATI株式会社).
