import SwiftUI
import AppKit

/// 確認画面（wp5 §2）。保存前確認UIそのもの。ここを経ずに書き出す経路は作らない（絶対条件）。
struct ReviewView: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        @Bindable var appState = appState
        Group {
            if appState.reviewLayout == .grid && appState.pages.count > 1 {
                BatchGridView()
            } else {
                HStack(spacing: 0) {
                    if appState.pages.count > 1 {
                        PageRailView()
                        Divider()
                    }
                    if let page = appState.currentPage {
                        CanvasView(page: page)
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                        Divider()
                        CandidateListView(page: page)
                    }
                }
            }
        }
        .toolbar { toolbarContent }
        .sheet(isPresented: $appState.showingExportSheet) { ExportSheet() }
        // 同一ビューへの .sheet 二重付けは片方が無効化されるため、別ノード（background）に載せる
        .background(
            Color.clear
                .sheet(isPresented: $appState.showingCropSheet) {
                    if let page = appState.cropSheetPage {
                        CropAdjustSheet(page: page)
                    }
                }
        )
        .alert("種別を変更しますか？", isPresented: pendingTypeChangeBinding) {
            Button("変更する", role: .destructive) { appState.confirmPendingTypeChange() }
            Button("キャンセル", role: .cancel) { appState.cancelPendingTypeChange() }
        } message: {
            Text("候補の編集内容（チェックの変更）は失われます。手動マスクは保持されます。")
        }
        .alert("新しい書類を開きますか？", isPresented: confirmingResetBinding) {
            Button("破棄して新規", role: .destructive) { appState.confirmReset() }
            Button("キャンセル", role: .cancel) { appState.confirmingReset = false }
        } message: {
            Text("現在の書類・マスクの編集内容は破棄されます。書き出していない内容は元に戻せません。")
        }
        .alert("回転しますか？", isPresented: pendingRotationBinding) {
            Button("回転する", role: .destructive) { appState.confirmPendingRotation() }
            Button("キャンセル", role: .cancel) { appState.cancelPendingRotation() }
        } message: {
            Text("回転すると座標が変わるため、このページの手動マスクと候補の編集内容は失われます。")
        }
        .overlay(alignment: .bottom) { exportDoneToast }
    }

    private var pendingTypeChangeBinding: Binding<Bool> {
        Binding(get: { appState.pendingTypeChange != nil },
                set: { if !$0 { appState.cancelPendingTypeChange() } })
    }

    private var pendingRotationBinding: Binding<Bool> {
        Binding(get: { appState.pendingRotationPageID != nil },
                set: { if !$0 { appState.cancelPendingRotation() } })
    }

    private var confirmingResetBinding: Binding<Bool> {
        Binding(get: { appState.confirmingReset },
                set: { appState.confirmingReset = $0 })
    }

    // MARK: - ツールバー

    @ToolbarContentBuilder
    private var toolbarContent: some ToolbarContent {
        ToolbarItemGroup(placement: .navigation) {
            Button { addFiles() } label: { Label("追加", systemImage: "plus") }
                .accessibilityIdentifier("review.addButton")
        }
        ToolbarItemGroup(placement: .principal) {
            if appState.pages.count > 1 {
                Button {
                    appState.reviewLayout = (appState.reviewLayout == .grid) ? .single : .grid
                } label: {
                    Label(appState.reviewLayout == .grid ? "個別表示" : "一覧表示",
                          systemImage: appState.reviewLayout == .grid ? "rectangle" : "square.grid.2x2")
                }
                .help("一覧（サムネイル）と個別の確認画面を切り替えます")
                .accessibilityIdentifier("review.layoutToggle")
            }
            if appState.reviewLayout == .single {
                toolButton(.select, "選択", "cursorarrow")
                toolButton(.rect, "矩形", "rectangle.dashed")
                toolButton(.brush, "ブラシ", "paintbrush")

                if appState.tool == .brush {
                    Slider(value: brushWidthBinding, in: 0.01...0.10) { Text("幅") }
                        .frame(width: 90)
                        .help("ブラシ幅")
                }

                Button("全体") { fit() }
                Button("100%") { pixelAccurate() }
                if let page = appState.currentPage {
                    Button { appState.requestRotate(page: page) } label: {
                        Label("回転", systemImage: "rotate.right")
                    }
                    .help("時計回りに90°回転して解析し直します（自動の向き判定が外れたとき用）")
                    .disabled(appState.reanalyzing)
                    .accessibilityIdentifier("review.rotateButton")
                }
                if let page = appState.currentPage {
                    Slider(value: zoomBinding(page), in: 1.0...4.0) { Text("ズーム") }
                        .frame(width: 90)
                }
            }
        }
        ToolbarItemGroup(placement: .primaryAction) {
            Toggle(isOn: previewBinding) { Label("仕上がりプレビュー", systemImage: "eye") }
                .accessibilityIdentifier("review.previewToggle")
            Button { appState.showingExportSheet = true } label: { Label("書き出す…", systemImage: "square.and.arrow.up") }
                .accessibilityIdentifier("review.exportButton")
            Button { appState.requestReset() } label: { Label("新しい書類", systemImage: "doc.badge.plus") }
                .accessibilityIdentifier("review.newDocButton")
        }
    }

    private var previewBinding: Binding<Bool> {
        Binding(
            get: { appState.previewMode },
            set: { on in
                appState.previewMode = on
                if on { appState.tool = .select }   // プレビュー中は選択ツールのみ（wp5 §2.1）
            }
        )
    }

    private func toolButton(_ tool: ReviewTool, _ title: String, _ symbol: String) -> some View {
        Button {
            appState.tool = tool
            if tool != .select { appState.previewMode = false }
        } label: {
            Label(title, systemImage: symbol)
        }
        .background(appState.tool == tool ? Color.accentColor.opacity(0.25) : Color.clear)
        .accessibilityIdentifier("review.tool.\(tool.rawValue)")
    }

    private var brushWidthBinding: Binding<Double> {
        Binding(get: { appState.brushWidth }, set: { appState.brushWidth = $0 })
    }

    private func zoomBinding(_ page: PageState) -> Binding<CGFloat> {
        Binding(get: { page.zoom }, set: { page.zoom = $0; page.pixelAccurate = false })
    }

    private func fit() { appState.currentPage.map { $0.zoom = 1.0; $0.pixelAccurate = false } }
    private func pixelAccurate() { appState.currentPage?.pixelAccurate = true }

    // MARK: - 追加ファイル

    private func addFiles() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = false
        panel.allowedContentTypes = FileIntake.acceptedTypes
        if panel.runModal() == .OK {
            let accepted = panel.urls.filter(FileIntake.isAccepted)
            Task { await appState.appendFiles(accepted) }
        }
    }

    // MARK: - 完了トースト

    @ViewBuilder
    private var exportDoneToast: some View {
        if appState.showingExportDone, let url = appState.lastExportedURL {
            HStack(spacing: 12) {
                Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
                Text("書き出しました: \(url.lastPathComponent)")
                Button("Finderで表示") { NSWorkspace.shared.activateFileViewerSelecting([url]) }
                Button {
                    appState.showingExportDone = false
                } label: { Image(systemName: "xmark") }
                    .buttonStyle(.borderless)
            }
            .padding(12)
            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 10))
            .padding(.bottom, 20)
            .accessibilityIdentifier("review.exportDoneToast")
        }
    }
}
