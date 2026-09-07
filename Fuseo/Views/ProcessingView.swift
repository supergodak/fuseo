import SwiftUI

/// Processing 状態（wp5 §1）。複数ファイルは直列 analyze。進捗 (n/m) を表示する。
struct ProcessingView: View {
    /// 進捗が数えられるとき（解析中）。nil = 不確定（取り込み中のラスタライズ）。
    var done: Int?
    var total: Int?

    var body: some View {
        VStack(spacing: 16) {
            ProgressView()
                .controlSize(.large)
            if let done, let total {
                Text("解析中… (\(done)/\(total))")
                    .font(.title3)
                    .monospacedDigit()
            } else {
                Text("PDFを読み込んでいます…")
                    .font(.title3)
                    .accessibilityIdentifier("processing.importing")
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .accessibilityIdentifier("processing.view")
    }
}
