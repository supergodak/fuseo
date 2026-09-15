import AppKit
import MaskingCore
import SwiftUI

/// WP-13: 空画面の上部に出す「作業中の書類」一覧（docs/wp13-library-design.md §2/§4）。
///
/// 保存されているのは**マスク前の書類画像**なので、削除の導線を目立つ位置に置く（行の削除＋設定の全削除）。
/// 一覧が空なら何も描かない（従来の空画面のまま）。
struct WorkLibraryView: View {
    @Environment(AppState.self) private var appState
    @State private var pendingDelete: WorkDocumentSummary?

    var body: some View {
        if !appState.libraryItems.isEmpty {
            VStack(alignment: .leading, spacing: 8) {
                Text("作業中の書類")
                    .font(.headline)
                    .padding(.horizontal, 4)

                ScrollView {
                    LazyVStack(spacing: 4) {
                        ForEach(appState.libraryItems, id: \.id) { item in
                            row(item)
                        }
                    }
                }
                .frame(maxHeight: 220)

                Text("保存先は端末内のみです（バックアップ対象外・外部に送信されません）。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .padding(.horizontal, 4)
            }
            .padding(16)
            .accessibilityElement(children: .contain)   // 行・ボタンを個別要素として露出
            .accessibilityIdentifier("library.list")
            .alert("この作業を削除しますか？", isPresented: deleteBinding) {
                Button("削除", role: .destructive) {
                    if let target = pendingDelete { appState.deleteWork(id: target.id) }
                    pendingDelete = nil
                }
                Button("キャンセル", role: .cancel) { pendingDelete = nil }
            } message: {
                Text("元に戻せません。保存された書類の画像とマスクの状態が消えます。")
            }
        }
    }

    private var deleteBinding: Binding<Bool> {
        Binding(get: { pendingDelete != nil }, set: { if !$0 { pendingDelete = nil } })
    }

    private func row(_ item: WorkDocumentSummary) -> some View {
        HStack(spacing: 12) {
            // ダブルクリックで開くのは**この情報部分だけ**に付ける。行全体に付けると
            // AppKit 側で行が1つのアクセシビリティ要素に畳まれ、右側のボタンが XCUITest から
            // 個別に見えなくなる（＝「開く」が hittable にならない）。
            HStack(spacing: 12) {
                WorkThumbnail(url: item.thumbnailURL)

                VStack(alignment: .leading, spacing: 2) {
                    Text(item.title)
                        .font(.body)
                        .lineLimit(1)
                        .accessibilityIdentifier("library.row.title")
                    HStack(spacing: 8) {
                        statusBadge(item.status)
                        Text(item.updatedAt, format: .relative(presentation: .named))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        Text("\(item.pageCount)ページ")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }

                Spacer()
            }
            .contentShape(Rectangle())
            .onTapGesture(count: 2) { appState.openWork(id: item.id) }

            Button("開く") { appState.openWork(id: item.id) }
                .buttonStyle(.bordered)
                .accessibilityIdentifier("library.open.\(item.id.uuidString)")
            Button("Finderで表示") {
                if let dir = appState.workDirectory(for: item.id) {
                    NSWorkspace.shared.activateFileViewerSelecting([dir])
                }
            }
            .buttonStyle(.bordered)
            .accessibilityIdentifier("library.reveal.\(item.id.uuidString)")
            Button(role: .destructive) { pendingDelete = item } label: {
                Image(systemName: "trash")
            }
            .buttonStyle(.bordered)
            .accessibilityIdentifier("library.delete.\(item.id.uuidString)")
        }
        .padding(8)
        .background(Color.secondary.opacity(0.08), in: RoundedRectangle(cornerRadius: 8))
        // 子（3つのボタン）を個別の要素として露出させる（行にまとめない）。
        .accessibilityElement(children: .contain)
    }

    @ViewBuilder
    private func statusBadge(_ status: WorkDocument.Status) -> some View {
        switch status {
        case .exported:
            Text("書き出し済み")
                .font(.caption2)
                .padding(.horizontal, 6).padding(.vertical, 2)
                .background(Color.green.opacity(0.22), in: Capsule())
                .accessibilityIdentifier("library.badge.exported")
        case .inProgress:
            Text("作業中")
                .font(.caption2)
                .padding(.horizontal, 6).padding(.vertical, 2)
                .background(Color.orange.opacity(0.22), in: Capsule())
                .accessibilityIdentifier("library.badge.inProgress")
        }
    }
}

/// 一覧のサムネイル（遅延読み込み・読めなければプレースホルダ）。
struct WorkThumbnail: View {
    let url: URL?
    @State private var image: NSImage?

    var body: some View {
        Group {
            if let image {
                Image(nsImage: image).resizable().aspectRatio(contentMode: .fit)
            } else {
                Image(systemName: "doc.text.image")
                    .foregroundStyle(.secondary)
            }
        }
        .frame(width: 48, height: 36)
        .clipShape(RoundedRectangle(cornerRadius: 4))
        .task(id: url) {
            guard let url else { image = nil; return }
            image = NSImage(contentsOf: url)
        }
    }
}
