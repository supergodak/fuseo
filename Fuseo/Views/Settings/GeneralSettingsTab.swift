import SwiftUI
import MaskingCore

/// 一般設定（wp5 §4）。書き出し既定値・スキャン仕上げ強度・顔マスク既定。
struct GeneralSettingsTab: View {
    @Environment(SettingsStore.self) private var settings

    var body: some View {
        @Bindable var settings = settings
        Form {
            Section {
                Picker("既定の形式", selection: $settings.defaultExportFormat) {
                    Text("PDF").tag(ExportOptions.Format.pdf)
                    Text("JPEG").tag(ExportOptions.Format.jpeg)
                    Text("PNG").tag(ExportOptions.Format.png)
                }
                .accessibilityIdentifier("settings.general.format")

                Toggle("検索可能PDFを既定にする", isOn: $settings.searchablePDF)
                    .accessibilityIdentifier("settings.general.searchable")

                VStack(alignment: .leading) {
                    Text("JPEG品質: \(Int(settings.jpegQuality * 100))%")
                    Slider(value: $settings.jpegQuality, in: 0.1...1.0)
                        .accessibilityIdentifier("settings.general.quality")
                }
            } header: {
                Text("書き出しの既定値")
            } footer: {
                Text("書き出しシートの初期値になります。あとから個別に変更できます。")
            }

            Section {
                VStack(alignment: .leading) {
                    Text("スキャン風仕上げ強度: \(String(format: "%.1f", settings.enhancerAmount))")
                    Slider(value: $settings.enhancerAmount, in: 0.0...1.0)
                        .accessibilityIdentifier("settings.general.enhance")
                }
            } header: {
                Text("画質")
            } footer: {
                Text("紙書類（住民票など）のみ効きます。カード類（免許証・マイナンバーカード）には適用されません。")
            }

            Section {
                Toggle("顔写真を既定でマスクする", isOn: $settings.faceMaskDefaultOn)
                    .accessibilityIdentifier("settings.general.faceDefault")
            } header: {
                Text("マスクの既定")
            } footer: {
                Text("解析後、検出された顔写真を自動でマスク対象にします（あとから個別に外せます）。")
            }
        }
        .formStyle(.grouped)
    }
}
