import SwiftUI

// Mac / iOS 共通の小さな UI（WP-10 §2.4）。`Fuseo/Services` は両ターゲットにコンパイルされるため、
// AppKit / UIKit に依存しないビューはここに置いて1つの実装を共有する。

/// 暗号化 PDF のパスワード入力シート。
///
/// 入力値は `AppState.submitPDFPassword` 経由でその場の取り込み処理に渡すだけで、
/// **保存しない・ログに出さない**（WP-10 §2.4）。誤りなら同じシートが「違います」付きで出直す。
struct PDFPasswordSheet: View {
    @Environment(AppState.self) private var appState
    let request: AppState.PDFPasswordRequest

    @State private var password = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("PDFのパスワード")
                .font(.headline)

            Text("「\(request.fileName)」はパスワードで保護されています。開くためのパスワードを入力してください。")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            SecureField("パスワード", text: $password)
                .textFieldStyle(.roundedBorder)
                .accessibilityIdentifier("pdfPassword.field")
                .onSubmit { submit() }

            if request.retry {
                Text("パスワードが違います。もう一度入力してください。")
                    .font(.caption)
                    .foregroundStyle(.red)
                    .accessibilityIdentifier("pdfPassword.error")
            }

            Text("入力したパスワードは端末の外に出ません。保存もしません。")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            HStack {
                Button("キャンセル", role: .cancel) { appState.cancelPDFPassword() }
                    .keyboardShortcut(.cancelAction)
                    .accessibilityIdentifier("pdfPassword.cancel")
                Spacer()
                Button("開く") { submit() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(password.isEmpty)
                    .accessibilityIdentifier("pdfPassword.submit")
            }
        }
        .padding(20)
        .frame(minWidth: 320)
        .accessibilityIdentifier("pdfPassword.sheet")
        .interactiveDismissDisabled()
    }

    private func submit() {
        guard !password.isEmpty else { return }
        appState.submitPDFPassword(password)
    }
}

/// PDF を取り込んだときの「安全側の仕様」の説明（WP-10 §2.1・必ず一度は目に入る位置に出す）。
/// 出力が画像化されるのは機能の欠落ではなく、下のテキストが復元できないようにするための仕様。
struct PDFNoticeBanner: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        if appState.hasPDFPages && !appState.pdfNoticeDismissed {
            HStack(alignment: .top, spacing: 8) {
                Image(systemName: "info.circle.fill")
                    .foregroundStyle(.blue)
                Text("PDFは各ページを画像として処理します。書き出したPDFのテキストは選択・検索できません（OCRテキスト層をオンにすると検索は可能。マスク箇所の文字は含まれません）。")
                    .font(.caption)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 0)
                Button {
                    appState.pdfNoticeDismissed = true
                } label: {
                    Image(systemName: "xmark")
                }
                .buttonStyle(.borderless)
                .accessibilityLabel("閉じる")
                .accessibilityIdentifier("review.pdfNotice.close")
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.thinMaterial)
            .accessibilityIdentifier("review.pdfNotice")
        }
    }
}
