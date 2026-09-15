import SwiftUI
import AppKit
import UniformTypeIdentifiers
import MaskingCore

/// 書き出しシート（wp5 §3）。形式・検索可能PDF・品質を確認し、NSSavePanel 経由で保存する。
/// 確認画面（Review）を経てここに来る。ここを飛ばして保存する経路は存在しない（絶対条件）。
struct ExportSheet: View {
    @Environment(AppState.self) private var appState
    @Environment(\.dismiss) private var dismiss
    /// 複数書類のとき: false=1ファイルにまとめる / true=書類ごとに個別ファイル（一括処理 v1.2）
    @State private var separateFiles = false

    var body: some View {
        @Bindable var appState = appState
        let summary = appState.exportSummary
        let zeroMask = appState.hasZeroMaskPage

        VStack(alignment: .leading, spacing: 16) {
            Text("書き出し").font(.title2).bold()

            Picker("形式", selection: $appState.exportOptions.format) {
                Text("PDF").tag(ExportOptions.Format.pdf)
                Text("JPEG").tag(ExportOptions.Format.jpeg)
                Text("PNG").tag(ExportOptions.Format.png)
            }
            .pickerStyle(.segmented)
            .accessibilityIdentifier("export.format")

            Picker("カラー", selection: $appState.exportOptions.colorMode) {
                Text("カラー").tag(ExportOptions.ColorMode.color)
                Text("グレー").tag(ExportOptions.ColorMode.grayscale)
                Text("白黒（文書）").tag(ExportOptions.ColorMode.blackWhite)
            }
            .pickerStyle(.segmented)
            .accessibilityIdentifier("export.colorMode")
            .help("グレー/白黒は書類の見た目を整える出力フィルタです。黒塗りには影響しません")

            if appState.exportOptions.format == .pdf {
                Toggle("検索可能PDF（テキスト層を埋め込む）", isOn: $appState.exportOptions.searchableText)
                    .accessibilityIdentifier("export.searchable")
            }
            if appState.exportOptions.format == .jpeg {
                VStack(alignment: .leading) {
                    Text("JPEG品質: \(Int(appState.exportOptions.jpegQuality * 100))%")
                    Slider(value: $appState.exportOptions.jpegQuality, in: 0.1...1.0)
                        .accessibilityIdentifier("export.quality")
                }
            }

            if appState.pages.count > 1 {
                Picker("出力", selection: $separateFiles) {
                    Text(appState.exportOptions.format == .pdf ? "1つのPDFにまとめる" : "ページ番号つき1セット").tag(false)
                    Text("書類ごとに個別ファイル").tag(true)
                }
                .pickerStyle(.segmented)
                .accessibilityIdentifier("export.separate")
                if separateFiles {
                    Text("保存先フォルダを選ぶと、各書類を「元のファイル名-masked」で書き出します。")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }

            Divider()

            Text("マスク \(summary.total)箇所（自動 \(summary.auto)・手動 \(summary.manual)）／ \(summary.pageCount)ページ")
                .font(.callout)

            if appState.hasPDFPages {
                Text("PDFは各ページを画像として処理します。書き出したPDFのテキストは選択・検索できません（OCRテキスト層をオンにすると検索は可能。マスク箇所の文字は含まれません）。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier("export.pdfNotice")
            }

            // 検索可能PDFをオンにしても、文字認識していないページにはテキスト層が付かない（WP-10b・C）。
            if appState.exportOptions.format == .pdf && appState.exportOptions.searchableText
                && appState.hasPagesWithoutText {
                Text("文字認識していないページには検索テキストが付きません。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier("export.noTextNotice")
            }

            if !appState.allWarnings.isEmpty {
                VStack(alignment: .leading, spacing: 4) {
                    ForEach(appState.allWarnings, id: \.self) { w in
                        HStack(alignment: .top, spacing: 6) {
                            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                            Text(w).font(.caption)
                        }
                    }
                }
            }

            if zeroMask {
                Text("マスクが1つも適用されていないページがあります。このまま書き出すと元の情報が残ります。")
                    .font(.callout)
                    .foregroundStyle(.red)
                    .accessibilityIdentifier("export.zeroMaskWarning")
            }

            HStack {
                Spacer()
                Button("キャンセル") { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button(zeroMask ? "マスクなしで書き出す" : "保存…") { save() }
                    .keyboardShortcut(.defaultAction)
                    .accessibilityIdentifier("export.confirmButton")
            }
        }
        .padding(20)
        .frame(width: 420)
    }

    private func save() {
        if separateFiles && appState.pages.count > 1 {
            saveSeparately()
            return
        }
        let panel = NSSavePanel()
        panel.nameFieldStringValue = appState.defaultExportFileName
        panel.allowedContentTypes = [contentType(for: appState.exportOptions.format)]
        panel.canCreateDirectories = true
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try appState.export(to: url)
            appState.markExported(name: url.lastPathComponent, url: url)
            dismiss()
        } catch {
            appState.errorMessage = "書き出しに失敗しました: \(error)"
            appState.showingError = true
        }
    }

    /// 一括書き出し（v1.2）: 保存先フォルダを選び、1書類=1ファイルで書き出す。
    private func saveSeparately() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        panel.prompt = "このフォルダに書き出す"
        guard panel.runModal() == .OK, let dir = panel.url else { return }
        do {
            let written = try appState.exportSeparately(to: dir)
            let first = written.first ?? dir
            appState.markExported(name: first.lastPathComponent, url: first)
            dismiss()
        } catch {
            appState.errorMessage = "書き出しに失敗しました: \(error)"
            appState.showingError = true
        }
    }

    private func contentType(for format: ExportOptions.Format) -> UTType {
        switch format {
        case .pdf: return .pdf
        case .jpeg: return .jpeg
        case .png: return .png
        }
    }
}
