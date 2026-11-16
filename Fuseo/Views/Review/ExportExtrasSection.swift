import SwiftUI
import MaskingCore

/// 書き出しシートの「提出用の設定」（0.4.0）: 透かし・ファイルの大きさの上限・PDF のパスワード。
struct ExportExtrasSection: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        @Bindable var appState = appState
        let format = appState.exportOptions.format

        GroupBox("提出用の設定") {
            VStack(alignment: .leading, spacing: 10) {
                // 透かし
                Toggle("透かしを入れる", isOn: $appState.watermarkEnabled)
                    .accessibilityIdentifier("export.watermark")
                if appState.watermarkEnabled {
                    HStack {
                        TextField("例: 〇〇株式会社 提出用", text: $appState.watermarkText)
                            .textFieldStyle(.roundedBorder)
                            .accessibilityIdentifier("export.watermarkText")
                        Button("日付を足す") { appState.appendDateToWatermark() }
                            .accessibilityIdentifier("export.watermarkDate")
                    }
                    Picker("配置", selection: $appState.watermarkLayout) {
                        Text("全面に敷き詰める").tag(Watermark.Layout.tiled)
                        Text("中央に1つ").tag(Watermark.Layout.diagonal)
                    }
                    .pickerStyle(.segmented)
                    .accessibilityIdentifier("export.watermarkLayout")
                    HStack {
                        Text("濃さ")
                        Slider(value: $appState.watermarkOpacity, in: Watermark.opacityRange)
                            .accessibilityIdentifier("export.watermarkOpacity")
                    }
                    if let preview = appState.watermarkPreview() {
                        Image(preview, scale: 1, label: Text("透かしの見本"))
                            .resizable()
                            .interpolation(.medium)
                            .scaledToFit()
                            .frame(maxHeight: 150)
                            .border(Color.secondary.opacity(0.3))
                            .accessibilityIdentifier("export.watermarkPreview")
                        Text("見本（いまのページ。黒塗りは省略）。すべてのページに入り、文字としては残りません。")
                            .font(.caption).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    } else {
                        Text("入れる文言を入力してください。")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }

                Divider()

                // ファイルの大きさの上限（PNG は無劣化の形式なので出さない）
                if format != .png {
                    Picker("ファイルの大きさ", selection: $appState.exportOptions.maxBytes) {
                        Text("上限なし").tag(Int?.none)
                        ForEach(SettingsStore.sizeLimitChoices, id: \.self) { limit in
                            Text("\(ByteSize.label(limit)) 以内").tag(Int?.some(limit))
                        }
                    }
                    .accessibilityIdentifier("export.sizeLimit")
                    if appState.exportOptions.maxBytes != nil {
                        Text(appState.pages.count > 1 && format != .pdf
                             ? "1 ファイルごとに収まるよう、画質と解像度を自動で下げます。"
                             : "収まるよう、画質と解像度を自動で下げます（文字が読める大きさより下げません）。")
                            .font(.caption).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }

                // PDF のパスワード
                if format == .pdf {
                    Divider()
                    Toggle("PDFにパスワードをかける", isOn: $appState.pdfPasswordEnabled)
                        .accessibilityIdentifier("export.password")
                    if appState.pdfPasswordEnabled {
                        SecureField("パスワード", text: $appState.pdfPassword)
                            .textFieldStyle(.roundedBorder)
                            .accessibilityIdentifier("export.passwordField")
                        SecureField("もう一度", text: $appState.pdfPasswordConfirm)
                            .textFieldStyle(.roundedBorder)
                            .accessibilityIdentifier("export.passwordConfirm")
                        switch appState.pdfPasswordState {
                        case .mismatch:
                            Text("2 つのパスワードが違います。")
                                .font(.caption).foregroundStyle(.red)
                                .accessibilityIdentifier("export.passwordMismatch")
                        default:
                            EmptyView()
                        }
                        Text("パスワードは保存しません。忘れると開けなくなります。相手には、ファイルとは別の方法（電話など）で伝えてください。")
                            .font(.caption).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
            .padding(.vertical, 4)
        }
    }
}
