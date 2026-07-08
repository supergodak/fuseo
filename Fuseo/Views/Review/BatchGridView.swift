import SwiftUI
import MaskingCore

/// 一括処理の一覧レビュー（v1.2）。サムネイル格子で全書類の状態を見渡し、
/// クリックで個別確認画面へ。**ここは確認の入口であり、無確認の自動保存は存在しない**（絶対条件）。
struct BatchGridView: View {
    @Environment(AppState.self) private var appState

    private let columns = [GridItem(.adaptive(minimum: 230, maximum: 320), spacing: 18)]

    var body: some View {
        ScrollView {
            LazyVGrid(columns: columns, spacing: 18) {
                ForEach(Array(appState.pages.enumerated()), id: \.element.id) { index, page in
                    cell(index: index, page: page)
                }
            }
            .padding(20)
        }
        .accessibilityIdentifier("review.grid")
    }

    private func cell(index: Int, page: PageState) -> some View {
        Button {
            appState.currentPageIndex = index
            appState.reviewLayout = .single
        } label: {
            VStack(alignment: .leading, spacing: 8) {
                Image(decorative: page.analyzed.page.cgImage, scale: 1.0)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
                    .frame(maxWidth: .infinity)
                    .frame(height: 150)
                    .background(Color(nsColor: .windowBackgroundColor))
                    .clipShape(RoundedRectangle(cornerRadius: 6))

                Text(page.sourceURL.lastPathComponent)
                    .font(.callout.weight(.medium))
                    .lineLimit(1)
                    .truncationMode(.middle)

                HStack(spacing: 6) {
                    Text(appState.analysis.displayName(for: page.analyzed.preset.documentType))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Spacer()
                    if !page.analyzed.preset.warnings.isEmpty {
                        Image(systemName: "exclamationmark.triangle.fill")
                            .foregroundStyle(.orange)
                            .font(.caption)
                            .help(page.analyzed.preset.warnings.joined(separator: "\n"))
                    }
                    if page.hasZeroMask {
                        Text("マスクなし")
                            .font(.caption2.weight(.bold))
                            .padding(.horizontal, 6).padding(.vertical, 2)
                            .background(Color.red.opacity(0.15))
                            .foregroundStyle(.red)
                            .clipShape(Capsule())
                    } else {
                        Text("マスク \(page.totalMaskCount)")
                            .font(.caption2.weight(.semibold))
                            .padding(.horizontal, 6).padding(.vertical, 2)
                            .background(Color.accentColor.opacity(0.15))
                            .foregroundStyle(Color.accentColor)
                            .clipShape(Capsule())
                    }
                }
            }
            .padding(12)
            .background(Color(nsColor: .controlBackgroundColor))
            .clipShape(RoundedRectangle(cornerRadius: 10))
            .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(Color.secondary.opacity(0.25)))
        }
        .buttonStyle(.plain)
        .accessibilityIdentifier("review.grid.cell.\(index)")
        .help("クリックで個別に確認・調整")
    }
}
