import SwiftUI

/// 左のページレール（wp5 §2）。複数ページ時のみ表示。サムネイル＋選択状態。
/// 0.2.3: サムネイルの右クリックと、下のボタンで、選んだページの並べ替え（前へ・後ろへ）と削除ができる（Undo 可）。
struct PageRailView: View {
    @Environment(AppState.self) private var appState
    @Environment(\.undoManager) private var undoManager

    var body: some View {
        VStack(spacing: 0) {
            ScrollViewReader { proxy in
                ScrollView {
                    VStack(spacing: 8) {
                        ForEach(Array(appState.pages.enumerated()), id: \.element.id) { index, page in
                            Button {
                                appState.currentPageIndex = index
                            } label: {
                                Image(decorative: page.analyzed.page.cgImage, scale: 1.0)
                                    .resizable()
                                    .aspectRatio(contentMode: .fit)
                                    .frame(width: 48, height: 48)
                                    .overlay(
                                        RoundedRectangle(cornerRadius: 4)
                                            .strokeBorder(index == appState.currentPageIndex ? Color.accentColor : Color.gray.opacity(0.4),
                                                          lineWidth: index == appState.currentPageIndex ? 2.5 : 1)
                                    )
                            }
                            .buttonStyle(.plain)
                            .contextMenu { PageActionsMenu(index: index) }
                            .accessibilityIdentifier("review.page.\(index)")
                            .id(page.id)
                        }
                    }
                    .padding(6)
                }
                // 並べ替えで選択中のページが見えない位置へ動いても追いかける
                .onChange(of: appState.currentPageIndex) { _, index in
                    if appState.pages.indices.contains(index) { proxy.scrollTo(appState.pages[index].id) }
                }
            }
            Divider()
            VStack(spacing: 2) {
                railButton("chevron.up", help: "選んだページを前へ", id: "review.page.moveUp",
                           enabled: appState.canMovePage(at: appState.currentPageIndex, by: -1)) {
                    appState.movePage(at: appState.currentPageIndex, by: -1, undo: undoManager)
                }
                railButton("chevron.down", help: "選んだページを後ろへ", id: "review.page.moveDown",
                           enabled: appState.canMovePage(at: appState.currentPageIndex, by: 1)) {
                    appState.movePage(at: appState.currentPageIndex, by: 1, undo: undoManager)
                }
                railButton("trash", help: "選んだページを削除（⌘Z で戻せます）", id: "review.page.remove",
                           enabled: appState.canRemovePage) {
                    appState.removePage(at: appState.currentPageIndex, undo: undoManager)
                }
            }
            .padding(.vertical, 6)
        }
        .frame(width: 60)
    }

    private func railButton(_ symbol: String, help: LocalizedStringKey, id: String, enabled: Bool,
                            action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .frame(width: 36, height: 22)
        }
        .buttonStyle(.borderless)
        .disabled(!enabled)
        .help(help)
        .accessibilityLabel(Text(help))
        .accessibilityIdentifier(id)
    }
}

/// ページの右クリックメニュー（レールと一覧で共通）。
struct PageActionsMenu: View {
    @Environment(AppState.self) private var appState
    @Environment(\.undoManager) private var undoManager
    let index: Int

    var body: some View {
        Button("前へ移動") { appState.movePage(at: index, by: -1, undo: undoManager) }
            .disabled(!appState.canMovePage(at: index, by: -1))
        Button("後ろへ移動") { appState.movePage(at: index, by: 1, undo: undoManager) }
            .disabled(!appState.canMovePage(at: index, by: 1))
        Divider()
        Button("このページを削除", role: .destructive) { appState.removePage(at: index, undo: undoManager) }
            .disabled(!appState.canRemovePage)
    }
}
