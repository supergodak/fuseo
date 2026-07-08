import SwiftUI
import AppKit
import UniformTypeIdentifiers

/// Empty 状態のドロップゾーン（wp5 §1）。点線枠＋「ファイルを選択…」。
/// 受理タイプ以外（PDF等）はその場でアラートし、読み込まない。
struct DropView: View {
    @Environment(AppState.self) private var appState
    @State private var isTargeted = false
    @State private var showingUnsupported = false

    var body: some View {
        VStack(spacing: 20) {
            Image(systemName: "doc.viewfinder")
                .font(.system(size: 56, weight: .light))
                .foregroundStyle(.secondary)
            Text("本人確認書類の写真をここにドロップ")
                .font(.title3)
            Text("対応形式: JPEG / PNG / HEIC / TIFF")
                .font(.callout)
                .foregroundStyle(.secondary)
            Button("ファイルを選択…") { openPanel() }
                .accessibilityIdentifier("drop.openButton")
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(40)
        .background(
            RoundedRectangle(cornerRadius: 16)
                .strokeBorder(style: StrokeStyle(lineWidth: 2, dash: [8, 6]))
                .foregroundStyle(isTargeted ? Color.accentColor : Color.secondary.opacity(0.5))
                .padding(24)
        )
        .contentShape(Rectangle())
        .accessibilityIdentifier("drop.zone")
        .dropDestination(for: URL.self) { urls, _ in
            handleDropped(urls)
            return true
        } isTargeted: { isTargeted = $0 }
        .alert("対応していない形式です", isPresented: $showingUnsupported) {
            Button("OK", role: .cancel) {}
        } message: {
            Text("JPEG / PNG / HEIC / TIFF の画像を選んでください（PDF等は非対応です）。")
        }
    }

    private func handleDropped(_ urls: [URL]) {
        let accepted = urls.filter(FileIntake.isAccepted)
        guard !accepted.isEmpty else { showingUnsupported = true; return }
        if accepted.count < urls.count { showingUnsupported = true }
        Task { await appState.processFiles(accepted) }
    }

    private func openPanel() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = false
        panel.canChooseFiles = true
        panel.allowedContentTypes = FileIntake.acceptedTypes
        if panel.runModal() == .OK {
            let accepted = panel.urls.filter(FileIntake.isAccepted)
            guard !accepted.isEmpty else { showingUnsupported = true; return }
            Task { await appState.processFiles(accepted) }
        }
    }
}
