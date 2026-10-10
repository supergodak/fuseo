# Fuseo for Windows

本人確認書類や控除証明書 PDF の「見せなくてよい部分」を自分で塗って、**復元できない形**で提出用コピーにする Windows 用ツールです。
完全ローカル・無料・オープンソース（MIT）。macOS / iPhone 版と同じ書類の知識（プリセット）を使います。

> 開発中（v0.1）。配布は窓の杜・Vector と GitHub Releases を予定しています。

## 構成
- `src/Fuseo.Core` — 座標・データの型・プリセット・焼き込み・書き出し（UI 非依存・.NET 8）
- `src/Fuseo.App` — WPF アプリ（`Fuseo.exe`）
- `tests/` — xUnit
- 技術上の約束: [docs/contract.md](docs/contract.md)

## ビルド
```
dotnet test Fuseo.Windows.sln
```
