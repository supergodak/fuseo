import SwiftUI
import MaskingCore

/// 文字枠の描画レイヤ（文字入れ 0.3.0）。**書き出しの焼き込みと同じ `TextBoxRenderer.draw`** で描くので、
/// 画面の見た目＝出力（docs/form-fill-design.md §4）。確認キャンバス・一覧のサムネイル・仕上がりプレビューで使う。
///
/// SwiftUI の Canvas は左上原点・下向きの y なので、「表示サイズ / 画像サイズ」の縮尺と上下反転を掛けて
/// 基準画像のピクセル・左下原点（TextBoxRenderer の規約）に合わせてから描く。
struct TextInkLayer: View {
    let texts: [TextBox]
    let imageSize: CGSize

    var body: some View {
        Canvas { ctx, size in
            guard imageSize.width > 0, imageSize.height > 0, !texts.isEmpty else { return }
            ctx.withCGContext { cg in
                cg.translateBy(x: 0, y: size.height)
                cg.scaleBy(x: size.width / imageSize.width, y: -size.height / imageSize.height)
                TextBoxRenderer.draw(texts, in: cg, imageSize: imageSize)
            }
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }
}

/// 文字の入力欄（枠のすぐ下に出す）。入力した文字は枠の中に焼き込みと同じ描画で即反映される。
/// Return で確定・⌥Return で改行・Esc で取り消し。フォーカスが外れたら確定する。
struct TextEditorOverlay: View {
    let page: PageState
    let id: TextBox.ID
    let undoManager: UndoManager?
    /// Return・Esc で閉じたとき（キャンバスへフォーカスを戻す）。
    var onKeyboardFinish: () -> Void = {}
    @FocusState private var focused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            TextField("文字を入力", text: textBinding, axis: .vertical)
                .textFieldStyle(.roundedBorder)
                .lineLimit(1...6)
                .focused($focused)
                .onSubmit {
                    page.commitTextEditing(undo: undoManager)
                    onKeyboardFinish()
                }
                .onExitCommand {
                    page.cancelTextEditing()
                    onKeyboardFinish()
                }
                .accessibilityIdentifier("review.textEditor")
            Text("Return で確定・⌥Return で改行・Esc で取り消し")
                .font(.caption2)
                .foregroundStyle(.secondary)
        }
        .padding(6)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 6))
        .shadow(color: .black.opacity(0.15), radius: 3, y: 1)
        .onAppear { DispatchQueue.main.async { focused = true } }
        .onChange(of: focused) { _, isFocused in
            if !isFocused, page.editingTextID == id { page.commitTextEditing(undo: undoManager) }
        }
        .onDisappear {
            if page.editingTextID == id { page.commitTextEditing(undo: undoManager) }
        }
    }

    private var textBinding: Binding<String> {
        Binding(get: { page.text(id)?.text ?? "" },
                set: { page.setEditingText($0) })
    }
}
