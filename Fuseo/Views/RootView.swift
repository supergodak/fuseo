import SwiftUI
import UniformTypeIdentifiers

/// 受理するタイプ（wp5 §1・jpeg / png / heic / tiff ＋ WP-10 で pdf を追加）。
/// PDF は `PDFIntake` が各ページを画像へ展開してからパイプラインへ渡す。
enum FileIntake {
    static let acceptedTypes: [UTType] = [.jpeg, .png, .heic, .tiff, .pdf]

    static func isAccepted(_ url: URL) -> Bool {
        guard let type = UTType(filenameExtension: url.pathExtension.lowercased()) else { return false }
        return acceptedTypes.contains { type.conforms(to: $0) }
    }

    /// 一括処理（v1.2）: フォルダを含むURL群を「受理できる画像ファイルの一覧」に展開する。
    /// フォルダは直下＋1階層のみ走査（深い再帰はしない・隠しファイル除外）。名前順。
    static func expand(_ urls: [URL]) -> [URL] {
        var out: [URL] = []
        let fm = FileManager.default
        for url in urls {
            var isDir: ObjCBool = false
            if fm.fileExists(atPath: url.path, isDirectory: &isDir), isDir.boolValue {
                let children = (try? fm.contentsOfDirectory(
                    at: url, includingPropertiesForKeys: [.isDirectoryKey],
                    options: [.skipsHiddenFiles])) ?? []
                for child in children.sorted(by: { $0.lastPathComponent < $1.lastPathComponent }) {
                    var childIsDir: ObjCBool = false
                    if fm.fileExists(atPath: child.path, isDirectory: &childIsDir), childIsDir.boolValue {
                        let grand = (try? fm.contentsOfDirectory(
                            at: child, includingPropertiesForKeys: nil,
                            options: [.skipsHiddenFiles])) ?? []
                        out += grand.sorted(by: { $0.lastPathComponent < $1.lastPathComponent })
                            .filter(isAccepted)
                    } else if isAccepted(child) {
                        out.append(child)
                    }
                }
            } else if isAccepted(url) {
                out.append(url)
            }
        }
        return out
    }
}

/// ルートビュー。3状態（Empty / Processing / Review）を切り替える（wp5 §1）。
struct RootView: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        @Bindable var appState = appState
        Group {
            switch appState.stage {
            case .empty:
                DropView()
            case .importing:
                ProcessingView()          // 取り込み中（PDFのページ画像化・不確定表示）
            case .processing(let done, let total):
                ProcessingView(done: done, total: total)
            case .review:
                ReviewView()
            }
        }
        .alert("読み込みエラー", isPresented: $appState.showingError) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(appState.errorMessage ?? "")
        }
        // 暗号化 PDF のパスワード入力（WP-10 §2.4）。どの状態からの取り込みでも出せるようここに置く。
        .sheet(item: $appState.pdfPasswordRequest) { request in
            PDFPasswordSheet(request: request)
                .environment(appState)
        }
    }
}
