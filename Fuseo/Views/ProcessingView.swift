import SwiftUI

/// Processing 状態（wp5 §1）。複数ファイルは直列 analyze。進捗 (n/m) を表示する。
struct ProcessingView: View {
    let done: Int
    let total: Int

    var body: some View {
        VStack(spacing: 16) {
            ProgressView()
                .controlSize(.large)
            Text("解析中… (\(done)/\(total))")
                .font(.title3)
                .monospacedDigit()
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .accessibilityIdentifier("processing.view")
    }
}
