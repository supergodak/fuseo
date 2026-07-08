import SwiftUI
import UniformTypeIdentifiers

/// 受理する画像タイプ（wp5 §1・jpeg / png / heic / tiff）。PDF等は受理しない。
enum FileIntake {
    static let acceptedTypes: [UTType] = [.jpeg, .png, .heic, .tiff]

    static func isAccepted(_ url: URL) -> Bool {
        guard let type = UTType(filenameExtension: url.pathExtension.lowercased()) else { return false }
        return acceptedTypes.contains { type.conforms(to: $0) }
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
    }
}
