# Security Policy / セキュリティポリシー

## Reporting a vulnerability

Fuseo handles ID-document data entirely on-device, but security reports are always welcome — especially anything that could cause a redaction to be recoverable or a document to leave the device.

- **Preferred:** open a [private security advisory](https://github.com/supergodak/fuseo/security/advisories/new) on GitHub.
- Or email: fuseo@ati-mirai.co.jp

Please **do not** open a public issue for security problems. We aim to acknowledge reports within a few days.

When reporting, include: the version (`v0.1.0 (build N)` shown in the About panel), macOS version, and steps to reproduce.

## Supported versions

Fuseo is pre-1.0; only the latest release is supported. Please update before reporting.

## Scope notes

- Fuseo does **all document processing on-device** and makes **no network calls** to handle your documents (see [PRIVACY.md](PRIVACY.md)); the only optional network activity is the Sparkle update check in a future release.
- Redaction is a **raster burn-in** (pixels replaced with black) with metadata stripped from exports. Reports showing that redacted content can be recovered from an exported file are treated as high priority.

---

## 日本語

### 脆弱性の報告

Fuseo は本人確認書類のデータを端末内のみで扱いますが、セキュリティ報告は歓迎します。特に「黒塗りが復元できてしまう」「書類が端末外に出てしまう」たぐいの問題は最優先で対応します。

- **推奨:** GitHub の [非公開セキュリティ勧告](https://github.com/supergodak/fuseo/security/advisories/new) を作成してください。
- またはメール: fuseo@ati-mirai.co.jp

セキュリティ問題は**公開 Issue にしないで**ください。数日以内の応答を目指します。報告時はバージョン（About の `v0.1.0 (build N)`）・macOS バージョン・再現手順を添えてください。

### 対応バージョン

1.0 未満のため、最新リリースのみ対応します。報告前に更新してください。

### 範囲について

- Fuseo は**書類の処理をすべて端末内で行い**、書類の処理のために**ネットワーク通信をしません**（[PRIVACY.md](PRIVACY.md)）。唯一の任意通信は将来リリースの Sparkle アップデート確認のみです。
- 黒塗りは**ラスタ焼き込み**（ピクセルを黒で置換）で、書き出しファイルからメタデータを除去します。書き出しファイルから黒塗り内容が復元できるという報告は高優先度で扱います。
