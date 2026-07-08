import SwiftUI

/// 左のページレール（wp5 §2）。複数ページ時のみ表示。サムネイル＋選択状態。
struct PageRailView: View {
    @Environment(AppState.self) private var appState

    var body: some View {
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
                    .accessibilityIdentifier("review.page.\(index)")
                }
            }
            .padding(6)
        }
        .frame(width: 60)
    }
}
