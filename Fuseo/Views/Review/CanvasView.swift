import SwiftUI
import MaskingCore

/// 確認画面の中央キャンバス（wp5 §2）。基準画像をフィット表示し、候補/手動マスクを重ねる。
///
/// **座標変換は必ず `CoordinateSpace` 経由**（手変換禁止・core-design.md §1.1）:
/// - 正規化(左下原点) → 表示(左上原点): `CoordinateSpace.viewRect(_:in:)`
/// - 表示 → 正規化: `CoordinateSpace.normRect(fromViewRect:in:)` / `normPoint(fromViewPoint:in:)`
/// - `in size:` に渡すのは**画像の表示領域サイズ**（レターボックス余白を含まない・wp5 §2）。
struct CanvasView: View {
    @Environment(AppState.self) private var appState
    @Environment(\.undoManager) private var undoManager
    let page: PageState

    // 手動ツールのドラッグ途中状態（表示座標）
    @State private var dragStart: CGPoint?
    @State private var dragCurrent: CGPoint?
    @State private var brushPoints: [CGPoint] = []
    // 選択マスクの移動/リサイズのドラッグ開始時矩形（undo用・1ドラッグ=1操作）
    @State private var editStartBox: NormRect?

    var body: some View {
        VStack(spacing: 0) {
            if !page.analyzed.page.rectified {
                banner("書類の輪郭を検出できませんでした。画像全体をそのまま使用しています。")
            }
            GeometryReader { geo in
                let disp = displaySize(in: geo.size)
                ScrollView([.horizontal, .vertical]) {
                    ZStack(alignment: .topLeading) {
                        Image(decorative: page.analyzed.page.cgImage, scale: 1.0)
                            .resizable()
                            .frame(width: disp.width, height: disp.height)

                        candidateOverlays(disp)
                        manualRectOverlays(disp)
                        strokeOverlay(disp)
                        selectionEditLayer(disp)
                        liveDragOverlay(disp)

                        // 手動ツール時はドラッグ捕捉レイヤを最前面に置く（候補タップより優先）。
                        if appState.tool != .select && !appState.previewMode {
                            Color.clear
                                .contentShape(Rectangle())
                                .frame(width: disp.width, height: disp.height)
                                .highPriorityGesture(toolDrag(disp))
                        }
                    }
                    .coordinateSpace(name: "canvas")
                    .focusable()
                    .onDeleteCommand { page.deleteSelection(undo: undoManager) }
                    .frame(width: disp.width, height: disp.height)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                }
                // 手動ツール（矩形/ブラシ）中はスクロールを止め、ドラッグを描画に使う（選択時はパン可）。
                .scrollDisabled(appState.tool != .select || appState.previewMode)
            }
        }
        .accessibilityIdentifier("review.canvas")
    }

    // MARK: - 表示サイズ（アスペクトフィット × ズーム）

    private func displaySize(in available: CGSize) -> CGSize {
        let img = page.analyzed.page.pixelSize
        guard img.width > 0, img.height > 0, available.width > 0, available.height > 0 else {
            return CGSize(width: max(1, available.width), height: max(1, available.height))
        }
        if page.pixelAccurate {
            return img   // 100% = 原寸ピクセル表示（スクロールでパン）
        }
        let fit = min(available.width / img.width, available.height / img.height)
        let scale = fit * page.zoom
        return CGSize(width: img.width * scale, height: img.height * scale)
    }

    // MARK: - 候補オーバレイ

    @ViewBuilder
    private func candidateOverlays(_ disp: CGSize) -> some View {
        ForEach(page.analyzed.candidates) { cand in
            let visible = !appState.previewMode || cand.isOn
            if visible {
                let rect = CoordinateSpace.viewRect(cand.box, in: disp)
                candidateShape(cand, rect: rect)
                    .frame(width: rect.width, height: rect.height)
                    .position(x: rect.midX, y: rect.midY)
                    .allowsHitTesting(appState.tool == .select && !appState.previewMode)
                    .onTapGesture(count: 2) { page.toggleCandidate(cand.id) }
                    .onTapGesture { page.selectedCandidateID = cand.id }
                    .accessibilityIdentifier("review.candidate.\(cand.ruleID)")
            }
        }
    }

    @ViewBuilder
    private func candidateShape(_ cand: MaskCandidate, rect: CGRect) -> some View {
        let selected = page.selectedCandidateID == cand.id
        if appState.previewMode {
            Rectangle().fill(Color.black)
        } else if cand.isOn {
            Rectangle()
                .fill(Color.black.opacity(0.7))
                .overlay(Rectangle().strokeBorder(selected ? Color.accentColor : Color.black,
                                                  lineWidth: selected ? 3 : 1.5))
        } else {
            Rectangle()
                .strokeBorder(selected ? Color.accentColor : Color.gray,
                              style: StrokeStyle(lineWidth: selected ? 3 : 1.5, dash: selected ? [] : [5, 4]))
        }
    }

    // MARK: - 手動矩形オーバレイ

    @ViewBuilder
    private func manualRectOverlays(_ disp: CGSize) -> some View {
        ForEach(Array(page.analyzed.manual.rects.enumerated()), id: \.offset) { i, r in
            let rect = CoordinateSpace.viewRect(r, in: disp)
            let selected = page.selectedManualRectIndex == i
            Group {
                if appState.previewMode {
                    Rectangle().fill(Color.black)
                } else {
                    Rectangle()
                        .fill(Color.black.opacity(0.7))
                        .overlay(Rectangle().strokeBorder(Color.accentColor,
                                                          lineWidth: selected ? 3 : 1.5))
                }
            }
            .frame(width: rect.width, height: rect.height)
            .position(x: rect.midX, y: rect.midY)
            .allowsHitTesting(appState.tool == .select && !appState.previewMode)
            .onTapGesture { page.selectedManualRectIndex = i }
        }
    }

    // MARK: - 選択マスクの編集レイヤ（移動＋四隅ハンドル・選択ツール時のみ）

    /// 画面向きの四隅（tl=画面左上）。
    private enum BoxCorner: String, CaseIterable { case tl, tr, br, bl }

    @ViewBuilder
    private func selectionEditLayer(_ disp: CGSize) -> some View {
        if appState.tool == .select, !appState.previewMode, let box = page.selectedBox {
            let rect = CoordinateSpace.viewRect(box, in: disp)
            // 移動用の透明ボディ（選択済みマスクの上に重ねる。ダブルクリックのトグルは透過）
            Rectangle()
                .fill(Color.clear)
                .contentShape(Rectangle())
                .frame(width: rect.width, height: rect.height)
                .position(x: rect.midX, y: rect.midY)
                .onTapGesture(count: 2) {
                    if let id = page.selectedCandidateID { page.toggleCandidate(id) }
                }
                .gesture(moveGesture(disp))

            ForEach(BoxCorner.allCases, id: \.self) { corner in
                let pt = handlePoint(corner, rect)
                Rectangle()
                    .fill(Color.white)
                    .overlay(Rectangle().strokeBorder(Color.accentColor, lineWidth: 1.5))
                    .frame(width: 9, height: 9)
                    .padding(8)                     // 当たり判定を広げる
                    .contentShape(Rectangle())
                    .position(pt)
                    .gesture(resizeGesture(corner, disp))
            }
        }
    }

    /// 表示矩形の四隅（画面座標）。
    private func handlePoint(_ corner: BoxCorner, _ rect: CGRect) -> CGPoint {
        switch corner {
        case .tl: return CGPoint(x: rect.minX, y: rect.minY)
        case .tr: return CGPoint(x: rect.maxX, y: rect.minY)
        case .br: return CGPoint(x: rect.maxX, y: rect.maxY)
        case .bl: return CGPoint(x: rect.minX, y: rect.maxY)
        }
    }

    /// リサイズの固定点＝ドラッグする隅の対角（正規化・左下原点）。
    private func anchorPoint(oppositeOf corner: BoxCorner, in r: NormRect) -> CGPoint {
        switch corner {
        case .tl: return CGPoint(x: r.x + r.w, y: r.y)          // 対角=画面右下
        case .tr: return CGPoint(x: r.x, y: r.y)                // 対角=画面左下
        case .br: return CGPoint(x: r.x, y: r.y + r.h)          // 対角=画面左上
        case .bl: return CGPoint(x: r.x + r.w, y: r.y + r.h)    // 対角=画面右上
        }
    }

    private func moveGesture(_ disp: CGSize) -> some Gesture {
        DragGesture(minimumDistance: 2, coordinateSpace: .named("canvas"))
            .onChanged { g in
                if editStartBox == nil { editStartBox = page.selectedBox }
                guard let start = editStartBox else { return }
                let p0 = CoordinateSpace.normPoint(fromViewPoint: g.startLocation, in: disp)
                let p1 = CoordinateSpace.normPoint(fromViewPoint: g.location, in: disp)
                let nx = min(max(0, start.x + (p1.x - p0.x)), 1 - start.w)
                let ny = min(max(0, start.y + (p1.y - p0.y)), 1 - start.h)
                page.setSelectedBox(NormRect(x: nx, y: ny, w: start.w, h: start.h))
            }
            .onEnded { _ in
                if let old = editStartBox { page.commitSelectedBoxEdit(from: old, undo: undoManager) }
                editStartBox = nil
            }
    }

    private func resizeGesture(_ corner: BoxCorner, _ disp: CGSize) -> some Gesture {
        DragGesture(minimumDistance: 0, coordinateSpace: .named("canvas"))
            .onChanged { g in
                if editStartBox == nil { editStartBox = page.selectedBox }
                guard let start = editStartBox else { return }
                let anchor = anchorPoint(oppositeOf: corner, in: start)
                let p = CoordinateSpace.normPoint(fromViewPoint: g.location, in: disp)
                let minSize = 0.005                            // つぶれ防止
                let x0 = min(anchor.x, p.x), x1 = max(anchor.x, p.x)
                let y0 = min(anchor.y, p.y), y1 = max(anchor.y, p.y)
                page.setSelectedBox(NormRect(x: x0, y: y0,
                                             w: max(minSize, x1 - x0), h: max(minSize, y1 - y0)))
            }
            .onEnded { _ in
                if let old = editStartBox { page.commitSelectedBoxEdit(from: old, undo: undoManager) }
                editStartBox = nil
            }
    }

    // MARK: - ブラシストローク表示

    @ViewBuilder
    private func strokeOverlay(_ disp: CGSize) -> some View {
        let lineWidth = { (w: Double) in max(1, w * min(disp.width, disp.height)) }
        ForEach(Array(page.analyzed.manual.strokes.enumerated()), id: \.offset) { _, stroke in
            Path { path in
                let pts = stroke.points.map { viewPoint($0, in: disp) }
                guard let first = pts.first else { return }
                path.move(to: first)
                for p in pts.dropFirst() { path.addLine(to: p) }
                if pts.count == 1 { path.addLine(to: CGPoint(x: first.x + 0.1, y: first.y)) }
            }
            .stroke(appState.previewMode ? Color.black : Color.black.opacity(0.7),
                    style: StrokeStyle(lineWidth: lineWidth(stroke.width), lineCap: .round, lineJoin: .round))
            .allowsHitTesting(false)
        }
    }

    // MARK: - ドラッグ途中プレビュー

    @ViewBuilder
    private func liveDragOverlay(_ disp: CGSize) -> some View {
        if appState.tool == .rect, let s = dragStart, let c = dragCurrent {
            let r = CGRect(x: min(s.x, c.x), y: min(s.y, c.y),
                           width: abs(c.x - s.x), height: abs(c.y - s.y))
            Rectangle()
                .fill(Color.black.opacity(0.4))
                .overlay(Rectangle().strokeBorder(Color.accentColor, lineWidth: 1.5))
                .frame(width: r.width, height: r.height)
                .position(x: r.midX, y: r.midY)
                .allowsHitTesting(false)
        }
        if appState.tool == .brush, !brushPoints.isEmpty {
            Path { path in
                guard let first = brushPoints.first else { return }
                path.move(to: first)
                for p in brushPoints.dropFirst() { path.addLine(to: p) }
            }
            .stroke(Color.black.opacity(0.5),
                    style: StrokeStyle(lineWidth: max(1, appState.brushWidth * min(disp.width, disp.height)),
                                       lineCap: .round, lineJoin: .round))
            .allowsHitTesting(false)
        }
    }

    // MARK: - ツールのドラッグ処理

    private func toolDrag(_ disp: CGSize) -> some Gesture {
        DragGesture(minimumDistance: 2, coordinateSpace: .local)
            .onChanged { g in
                switch appState.tool {
                case .rect:
                    if dragStart == nil { dragStart = g.startLocation }
                    dragCurrent = g.location
                case .brush:
                    brushPoints.append(g.location)
                case .select:
                    break
                }
            }
            .onEnded { _ in
                switch appState.tool {
                case .rect:
                    if let s = dragStart, let c = dragCurrent {
                        let viewRect = CGRect(x: min(s.x, c.x), y: min(s.y, c.y),
                                              width: abs(c.x - s.x), height: abs(c.y - s.y))
                        if viewRect.width > 1, viewRect.height > 1 {
                            let norm = CoordinateSpace.normRect(fromViewRect: viewRect, in: disp)
                            page.addRect(norm, undo: undoManager)
                        }
                    }
                    dragStart = nil; dragCurrent = nil
                case .brush:
                    if !brushPoints.isEmpty {
                        let norm = brushPoints.map { CoordinateSpace.normPoint(fromViewPoint: $0, in: disp) }
                        page.addStroke(BrushStroke(points: norm, width: appState.brushWidth), undo: undoManager)
                    }
                    brushPoints = []
                case .select:
                    break
                }
            }
    }

    private func viewPoint(_ p: CGPoint, in disp: CGSize) -> CGPoint {
        // 正規化(左下原点)の1点 → 表示(左上原点)。viewRect の点版（幅0矩形として変換）。
        let r = CoordinateSpace.viewRect(NormRect(x: p.x, y: p.y, w: 0, h: 0), in: disp)
        return CGPoint(x: r.minX, y: r.minY)
    }

    private func banner(_ text: String) -> some View {
        HStack(spacing: 6) {
            Image(systemName: "exclamationmark.triangle.fill")
            Text(text).font(.callout)
            Button("切り抜きを調整…") { appState.requestCropAdjust(page: page) }
                .font(.callout)
            Spacer()
        }
        .padding(8)
        .frame(maxWidth: .infinity)
        .background(Color.orange.opacity(0.2))
    }
}
