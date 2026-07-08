import SwiftUI
import MaskingCore

/// 切り抜きの手動調整シート（wp5 §9.5）。自動の書類検出が変な台形を拾ったときのフォールバック。
/// 元写真の上で四隅ハンドルをドラッグし、「適用」で再解析する。
///
/// 座標変換は `CoordinateSpace.viewPoint` / `normPoint(fromViewPoint:)` のみ（手変換禁止）。
struct CropAdjustSheet: View {
    @Environment(AppState.self) private var appState
    @Environment(\.dismiss) private var dismiss
    let page: PageState

    @State private var preview: VisionRectifier.CropPreview?
    @State private var quad: Quad = .fullImage
    @State private var loadFailed = false
    @State private var confirmingApply = false
    @State private var applying = false

    var body: some View {
        VStack(spacing: 12) {
            Text("切り抜きを調整").font(.title3).bold()
            Text("書類の四隅にハンドルを合わせてください。うまく検出できない写真でも、手動で正しい範囲を指定できます。")
                .font(.callout)
                .foregroundStyle(.secondary)

            editorCanvas
                .frame(maxWidth: .infinity, maxHeight: .infinity)

            footer
        }
        .padding(16)
        .frame(minWidth: 720, minHeight: 540)
        .task { await load() }
        .alert("切り抜きを変更しますか？", isPresented: $confirmingApply) {
            Button("変更する", role: .destructive) { Task { await apply() } }
            Button("キャンセル", role: .cancel) {}
        } message: {
            Text("切り抜きを変えると、このページの手動マスクと候補の編集内容は失われます。")
        }
        // 注意: ルートに accessibilityIdentifier を付けると子要素全部に伝播して
        // ボタン個別のID（crop.full 等）を上書きするため付けない（実測で判明）。
    }

    // MARK: - キャンバス（元画像＋四隅ハンドル）

    @ViewBuilder
    private var editorCanvas: some View {
        if let preview {
            GeometryReader { geo in
                let disp = fitSize(image: preview.originalImage, in: geo.size)
                ZStack(alignment: .topLeading) {
                    Image(decorative: preview.originalImage, scale: 1.0)
                        .resizable()
                        .frame(width: disp.width, height: disp.height)

                    quadOverlay(disp)
                    ForEach(Corner.allCases, id: \.self) { corner in
                        handle(corner, disp: disp)
                    }
                }
                .coordinateSpace(name: "cropCanvas")
                // キャンバスをGeometryReader内で中央寄せ
                .frame(width: disp.width, height: disp.height)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        } else if loadFailed {
            ContentUnavailableView("画像を読み込めませんでした", systemImage: "exclamationmark.triangle")
        } else {
            ProgressView("読み込み中…")
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    /// 四辺の線＋外側の減光（even-odd で四角形をくり抜く）。
    private func quadOverlay(_ disp: CGSize) -> some View {
        let pts = Corner.allCases.map { CoordinateSpace.viewPoint(quad[$0], in: disp) }
        return ZStack {
            Path { p in
                p.addRect(CGRect(origin: .zero, size: CGSize(width: disp.width, height: disp.height)))
                p.move(to: pts[0])
                for pt in pts.dropFirst() { p.addLine(to: pt) }
                p.closeSubpath()
            }
            .fill(Color.black.opacity(0.45), style: FillStyle(eoFill: true))

            Path { p in
                p.move(to: pts[0])
                for pt in pts.dropFirst() { p.addLine(to: pt) }
                p.closeSubpath()
            }
            .stroke(quad.isConvex ? Color.accentColor : Color.red, lineWidth: 2)
        }
        .allowsHitTesting(false)
    }

    private func handle(_ corner: Corner, disp: CGSize) -> some View {
        let pt = CoordinateSpace.viewPoint(quad[corner], in: disp)
        return Circle()
            .fill(Color.accentColor)
            .overlay(Circle().strokeBorder(Color.white, lineWidth: 2))
            .frame(width: 16, height: 16)
            .padding(14)                       // 当たり判定 44pt
            .contentShape(Circle())
            .position(pt)
            .gesture(
                DragGesture(minimumDistance: 0, coordinateSpace: .named("cropCanvas"))
                    .onChanged { g in
                        // キャンバス座標のポインタ位置へハンドル中心を追従（normPoint が 0..1 にクランプ）
                        quad[corner] = CoordinateSpace.normPoint(fromViewPoint: g.location, in: disp)
                    }
            )
            .accessibilityIdentifier("crop.handle.\(corner.rawValue)")
    }

    // MARK: - フッター

    private var footer: some View {
        HStack {
            Button("自動検出に戻す") { if let q = preview?.detectedQuad { quad = q } }
                .disabled(preview?.detectedQuad == nil)
                .accessibilityIdentifier("crop.reset")
            Button("全体を使う") { quad = .fullImage }
                .accessibilityIdentifier("crop.full")
            if !quad.isConvex {
                Text("四隅が交差しています").font(.callout).foregroundStyle(.red)
            }
            Spacer()
            Button("キャンセル") { dismiss() }
                .keyboardShortcut(.cancelAction)
                .accessibilityIdentifier("crop.cancel")
            Button("適用") {
                if page.hasUserEdits { confirmingApply = true } else { Task { await apply() } }
            }
            .keyboardShortcut(.defaultAction)
            .disabled(preview == nil || !quad.isConvex || applying)
            .accessibilityIdentifier("crop.apply")
        }
        .overlay(alignment: .center) { if applying { ProgressView().controlSize(.small) } }
    }

    // MARK: - 処理

    private func load() async {
        do {
            preview = try await appState.loadCropPreview(for: page)
            if preview == nil { loadFailed = true }
            quad = page.manualQuad ?? preview?.detectedQuad ?? .fullImage
        } catch {
            loadFailed = true
        }
    }

    private func apply() async {
        applying = true
        await appState.applyCrop(page: page, quad: quad)
        applying = false
        dismiss()
    }

    private func fitSize(image: CGImage, in available: CGSize) -> CGSize {
        let w = CGFloat(image.width), h = CGFloat(image.height)
        guard w > 0, h > 0, available.width > 0, available.height > 0 else { return available }
        let scale = min(available.width / w, available.height / h)
        return CGSize(width: w * scale, height: h * scale)
    }
}

/// 四隅の識別（表示・a11y・添字アクセス用）。
private enum Corner: String, CaseIterable {
    case tl, tr, br, bl
}

private extension Quad {
    subscript(_ corner: Corner) -> CGPoint {
        get {
            switch corner {
            case .tl: return topLeft
            case .tr: return topRight
            case .br: return bottomRight
            case .bl: return bottomLeft
            }
        }
        set {
            switch corner {
            case .tl: topLeft = newValue
            case .tr: topRight = newValue
            case .br: bottomRight = newValue
            case .bl: bottomLeft = newValue
            }
        }
    }
}
