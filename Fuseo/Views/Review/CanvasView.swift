import SwiftUI
import AppKit
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
    // 文字枠の移動/大きさ変更のドラッグ開始時の枠（undo用・1ドラッグ=1操作）
    @State private var textEditStartBox: NormRect?
    // 位置合わせの補助線（吸い付いたときだけ・表示座標）
    @State private var snapGuides: [TextSnap.Guide] = []
    // 矢印キー・Delete・⌘D を受けるためのキャンバスのフォーカス
    @FocusState private var canvasFocused: Bool

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
                            // ページの境界を見せる（白い余白の多い PDF ページが「小さな画像」に見えないように）
                            .overlay(Rectangle().stroke(Color.secondary.opacity(0.35), lineWidth: 1).allowsHitTesting(false))
                            .shadow(color: .black.opacity(0.12), radius: 6, y: 2)
                            // 選択ツールで何もない所をクリック → 文字枠の選択を外す
                            .onTapGesture {
                                guard appState.tool == .select else { return }
                                page.commitTextEditing(undo: undoManager)
                                page.selectedTextID = nil
                            }

                        // 描く順は書き出しと同じ: 基準画像 → 文字 → マスク（候補・手動矩形）→ ブラシ（設計 §2）
                        TextInkLayer(texts: page.analyzed.texts, imageSize: page.analyzed.page.pixelSize)
                            .frame(width: disp.width, height: disp.height)

                        // 選択ツールでは文字枠の当たり判定をマスクの下に置く（上に見えているマスクが優先）
                        if appState.tool == .select && !appState.previewMode { textHitTargets(disp) }

                        candidateOverlays(disp)
                        manualRectOverlays(disp)
                        strokeOverlay(disp)
                        textFrameOutlines(disp)
                        selectionEditLayer(disp)
                        liveDragOverlay(disp)

                        // 手動ツール時はドラッグ捕捉レイヤを最前面に置く（候補タップより優先）。
                        if appState.tool != .select && !appState.previewMode {
                            if appState.tool == .text {
                                Color.clear
                                    .contentShape(Rectangle())
                                    .frame(width: disp.width, height: disp.height)
                                    .highPriorityGesture(textToolDrag(disp))
                            } else {
                                Color.clear
                                    .contentShape(Rectangle())
                                    .frame(width: disp.width, height: disp.height)
                                    .highPriorityGesture(toolDrag(disp))
                            }
                        }
                        // 文字ツールでは既存の枠のクリックを捕捉レイヤより優先（選択・移動・ダブルクリックで入力）
                        if appState.tool == .text && !appState.previewMode { textHitTargets(disp) }

                        textSelectionLayer(disp)
                        snapGuideOverlay(disp)
                        textEditorLayer(disp)
                    }
                    .coordinateSpace(name: "canvas")
                    // 画像の表示領域そのもの（UI テストのドラッグ座標の基準。レターボックス余白を含まない）
                    .accessibilityElement(children: .contain)
                    .accessibilityIdentifier("review.page")
                    .focusable()
                    .focused($canvasFocused)
                    .onDeleteCommand { page.deleteSelection(undo: undoManager) }
                    .onKeyPress(keys: [.upArrow, .downArrow, .leftArrow, .rightArrow]) { press in
                        nudgeText(press)
                    }
                    .background { duplicateShortcut }
                    .frame(width: disp.width, height: disp.height)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                }
                // 手動ツール（矩形/ブラシ）中はスクロールを止め、ドラッグを描画に使う（選択時はパン可）。
                .scrollDisabled(appState.tool != .select || appState.previewMode)
            }
        }
        .overlay {
            if appState.reanalyzing {
                // 回転・種別変更・切り抜き適用の再解析中インジケータ（無反応に見せない・連打防止）
                VStack(spacing: 10) {
                    ProgressView()
                    Text("解析し直しています…").font(.callout)
                }
                .padding(24)
                .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(Color.black.opacity(0.08))
                .transition(.opacity)
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
        if appState.tool == .text, let s = dragStart, let c = dragCurrent,
           abs(c.x - s.x) >= 4 || abs(c.y - s.y) >= 4 {
            let r = CGRect(x: min(s.x, c.x), y: min(s.y, c.y),
                           width: abs(c.x - s.x), height: abs(c.y - s.y))
            Rectangle()
                .strokeBorder(Color.accentColor, style: StrokeStyle(lineWidth: 1.5, dash: [4, 3]))
                .frame(width: max(r.width, 1), height: max(r.height, 1))
                .position(x: r.midX, y: r.midY)
                .allowsHitTesting(false)
        }
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
                case .select, .text:   // 文字ツールは textToolDrag が受ける
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
                case .select, .text:   // 文字ツールは textToolDrag が受ける
                    break
                }
            }
    }

    // MARK: - 文字枠（文字入れ 0.3.0・docs/form-fill-design.md §5）

    /// 文字枠の当たり判定（選択・移動・ダブルクリックで入力）。選択中の枠は textSelectionLayer が受ける。
    @ViewBuilder
    private func textHitTargets(_ disp: CGSize) -> some View {
        ForEach(page.analyzed.texts) { t in
            if t.id != page.selectedTextID && t.id != page.editingTextID {
                let rect = CoordinateSpace.viewRect(t.box, in: disp)
                Rectangle()
                    .fill(Color.clear)
                    .contentShape(Rectangle())
                    .frame(width: max(rect.width, 6), height: max(rect.height, 6))
                    .position(x: rect.midX, y: rect.midY)
                    .onTapGesture(count: 2) { beginEditing(t.id) }
                    .onTapGesture { selectText(t.id) }
                    .gesture(textMoveGesture(t.id, disp))
            }
        }
    }

    private func selectText(_ id: TextBox.ID) {
        page.commitTextEditing(undo: undoManager)
        page.selectedTextID = id
        canvasFocused = true
    }

    private func beginEditing(_ id: TextBox.ID) {
        page.beginTextEditing(id, undo: undoManager)
    }

    /// 枠の線（編集中の画面だけ。仕上がりプレビューと書き出しには出さない）。
    /// はみ出している枠は赤い破線、選択中はアクセント色、それ以外は薄い点線。
    @ViewBuilder
    private func textFrameOutlines(_ disp: CGSize) -> some View {
        if !appState.previewMode {
            let imageSize = page.analyzed.page.pixelSize
            ForEach(page.analyzed.texts) { t in
                let rect = CoordinateSpace.viewRect(t.box, in: disp)
                let overflows = TextBoxRenderer.layout(t, imageSize: imageSize).overflows
                let selected = t.id == page.selectedTextID || t.id == page.editingTextID
                Rectangle()
                    .strokeBorder(overflows ? Color.red : (selected ? Color.accentColor : Color.secondary.opacity(0.7)),
                                  style: StrokeStyle(lineWidth: overflows || selected ? 1.5 : 1,
                                                     dash: overflows ? [5, 3] : (selected ? [] : [2, 3])))
                    .frame(width: max(rect.width, 1), height: max(rect.height, 1))
                    .position(x: rect.midX, y: rect.midY)
                    .allowsHitTesting(false)
                    .accessibilityHidden(true)
            }
        }
    }

    /// つまみの位置（画面向き）。
    private enum TextHandle: CaseIterable {
        case tl, t, tr, r, br, b, bl, l
        var movesLeft: Bool { self == .tl || self == .l || self == .bl }
        var movesRight: Bool { self == .tr || self == .r || self == .br }
        var movesTop: Bool { self == .tl || self == .t || self == .tr }
        var movesBottom: Bool { self == .bl || self == .b || self == .br }

        func point(in r: CGRect) -> CGPoint {
            let x = movesLeft ? r.minX : (movesRight ? r.maxX : r.midX)
            let y = movesTop ? r.minY : (movesBottom ? r.maxY : r.midY)
            return CGPoint(x: x, y: y)
        }
    }

    /// 選択中の文字枠の移動ボディと 8 か所のつまみ（入力中は出さない）。
    @ViewBuilder
    private func textSelectionLayer(_ disp: CGSize) -> some View {
        if (appState.tool == .select || appState.tool == .text), !appState.previewMode,
           let t = page.selectedText, page.editingTextID != t.id {
            let rect = CoordinateSpace.viewRect(t.box, in: disp)
            Rectangle()
                .fill(Color.clear)
                .contentShape(Rectangle())
                .frame(width: max(rect.width, 6), height: max(rect.height, 6))
                .position(x: rect.midX, y: rect.midY)
                .onTapGesture(count: 2) { beginEditing(t.id) }
                .onTapGesture { canvasFocused = true }
                .gesture(textMoveGesture(t.id, disp))

            ForEach(TextHandle.allCases, id: \.self) { handle in
                Rectangle()
                    .fill(Color.white)
                    .overlay(Rectangle().strokeBorder(Color.accentColor, lineWidth: 1.5))
                    .frame(width: 8, height: 8)
                    .padding(6)                     // 当たり判定を広げる
                    .contentShape(Rectangle())
                    .position(handle.point(in: rect))
                    .gesture(textResizeGesture(t.id, handle, disp))
            }
        }
    }

    /// ⌘ を押している間は吸い付かない。
    private var snappingDisabled: Bool { NSEvent.modifierFlags.contains(.command) }

    /// 同じページの他の文字枠（表示座標）。
    private func otherTextRects(_ id: TextBox.ID, _ disp: CGSize) -> [CGRect] {
        page.analyzed.texts.filter { $0.id != id }.map { CoordinateSpace.viewRect($0.box, in: disp) }
    }

    private func clampInside(_ r: CGRect, _ disp: CGSize) -> CGRect {
        var r = r
        r.origin.x = min(max(0, r.minX), max(0, disp.width - r.width))
        r.origin.y = min(max(0, r.minY), max(0, disp.height - r.height))
        return r
    }

    private func textMoveGesture(_ id: TextBox.ID, _ disp: CGSize) -> some Gesture {
        DragGesture(minimumDistance: 2, coordinateSpace: .named("canvas"))
            .onChanged { g in
                if textEditStartBox == nil {
                    page.commitTextEditing(undo: undoManager)
                    page.selectedTextID = id
                    canvasFocused = true
                    textEditStartBox = page.text(id)?.box
                }
                guard let start = textEditStartBox else { return }
                var r = clampInside(CoordinateSpace.viewRect(start, in: disp)
                    .offsetBy(dx: g.translation.width, dy: g.translation.height), disp)
                if snappingDisabled {
                    snapGuides = []
                } else {
                    let snapped = TextSnap.snapMove(r, others: otherTextRects(id, disp))
                    r = clampInside(snapped.rect, disp)
                    snapGuides = snapped.guides
                }
                page.setTextBoxLive(id, CoordinateSpace.normRect(fromViewRect: r, in: disp))
            }
            .onEnded { _ in
                if let old = textEditStartBox { page.commitTextBox(id, from: old, undo: undoManager) }
                textEditStartBox = nil
                snapGuides = []
            }
    }

    private func textResizeGesture(_ id: TextBox.ID, _ handle: TextHandle, _ disp: CGSize) -> some Gesture {
        DragGesture(minimumDistance: 0, coordinateSpace: .named("canvas"))
            .onChanged { g in
                if textEditStartBox == nil { textEditStartBox = page.text(id)?.box }
                guard let start = textEditStartBox else { return }
                let s = CoordinateSpace.viewRect(start, in: disp)
                let minSize: CGFloat = 4                       // つぶれ防止（表示 pt）
                var minX = s.minX, maxX = s.maxX, minY = s.minY, maxY = s.maxY
                if handle.movesLeft { minX = min(max(0, s.minX + g.translation.width), s.maxX - minSize) }
                if handle.movesRight { maxX = max(min(disp.width, s.maxX + g.translation.width), s.minX + minSize) }
                if handle.movesTop { minY = min(max(0, s.minY + g.translation.height), s.maxY - minSize) }
                if handle.movesBottom { maxY = max(min(disp.height, s.maxY + g.translation.height), s.minY + minSize) }
                var r = CGRect(x: minX, y: minY, width: maxX - minX, height: maxY - minY)
                if snappingDisabled || !(handle.movesLeft || handle.movesRight) {
                    snapGuides = []
                } else {
                    let snapped = TextSnap.snapResize(r, movingLeft: handle.movesLeft, movingRight: handle.movesRight,
                                                      others: otherTextRects(id, disp))
                    r = snapped.rect
                    snapGuides = snapped.guides
                }
                page.setTextBoxLive(id, CoordinateSpace.normRect(fromViewRect: r, in: disp))
            }
            .onEnded { _ in
                if let old = textEditStartBox { page.commitTextBox(id, from: old, undo: undoManager) }
                textEditStartBox = nil
                snapGuides = []
            }
    }

    /// 吸い付いたときの細い補助線。
    @ViewBuilder
    private func snapGuideOverlay(_ disp: CGSize) -> some View {
        if !snapGuides.isEmpty {
            Path { path in
                for g in snapGuides {
                    switch g.axis {
                    case .vertical:
                        path.move(to: CGPoint(x: g.position, y: 0))
                        path.addLine(to: CGPoint(x: g.position, y: disp.height))
                    case .horizontal:
                        path.move(to: CGPoint(x: 0, y: g.position))
                        path.addLine(to: CGPoint(x: disp.width, y: g.position))
                    }
                }
            }
            .stroke(Color.pink, lineWidth: 1)
            .frame(width: disp.width, height: disp.height)
            .allowsHitTesting(false)
            .accessibilityHidden(true)
        }
    }

    /// 入力欄（枠のすぐ下。下に余地が無ければ上）。
    @ViewBuilder
    private func textEditorLayer(_ disp: CGSize) -> some View {
        if !appState.previewMode, let id = page.editingTextID, let t = page.text(id) {
            let rect = CoordinateSpace.viewRect(t.box, in: disp)
            let width = min(max(rect.width, 240), max(240, disp.width))
            let estimatedHeight: CGFloat = 64
            let below = rect.maxY + 6 + estimatedHeight <= disp.height
            // Return/Esc で閉じたら矢印キー・Delete・⌘D がすぐ効くようにキャンバスへフォーカスを戻す
            // （パネルの数値欄などをクリックして閉じたときは戻さない＝その欄のフォーカスを奪わない）
            TextEditorOverlay(page: page, id: id, undoManager: undoManager,
                              onKeyboardFinish: { canvasFocused = true })
                .frame(width: width)
                .offset(x: min(max(0, rect.minX), max(0, disp.width - width)),
                        y: below ? rect.maxY + 6 : max(0, rect.minY - 6 - estimatedHeight))
                .id(id)
        }
    }

    /// 文字ツール: ドラッグで枠を作る／クリックで既定の大きさの枠を作る。どちらもそのまま入力に入る。
    private func textToolDrag(_ disp: CGSize) -> some Gesture {
        DragGesture(minimumDistance: 0, coordinateSpace: .local)
            .onChanged { g in
                if dragStart == nil { dragStart = g.startLocation }
                dragCurrent = g.location
            }
            .onEnded { g in
                defer { dragStart = nil; dragCurrent = nil }
                let s = g.startLocation, c = g.location
                let viewRect = CGRect(x: min(s.x, c.x), y: min(s.y, c.y),
                                      width: abs(c.x - s.x), height: abs(c.y - s.y))
                let box: NormRect?
                if viewRect.width >= 4 && viewRect.height >= 4 {
                    box = CoordinateSpace.normRect(fromViewRect: viewRect, in: disp)
                } else {
                    let p = CoordinateSpace.normPoint(fromViewPoint: s, in: disp)
                    box = TextGeometry.defaultBox(clickedAt: p, style: appState.nextTextStyle,
                                                  imageSize: page.analyzed.page.pixelSize)
                }
                guard let box, box.w > 0, box.h > 0 else { return }
                page.beginNewText(box: box, style: appState.nextTextStyle, undo: undoManager)
            }
    }

    /// 矢印キー: 選択中の文字枠を基準画像の 1px（⇧ で 10px）動かす。入力中はテキスト欄が受ける。
    private func nudgeText(_ press: KeyPress) -> KeyPress.Result {
        guard page.selectedTextID != nil, page.editingTextID == nil, !appState.previewMode else { return .ignored }
        let step: Double = press.modifiers.contains(.shift) ? 10 : 1
        switch press.key {
        case .leftArrow: page.nudgeSelectedText(dxPx: -step, dyPx: 0, undo: undoManager)
        case .rightArrow: page.nudgeSelectedText(dxPx: step, dyPx: 0, undo: undoManager)
        case .upArrow: page.nudgeSelectedText(dxPx: 0, dyPx: -step, undo: undoManager)
        case .downArrow: page.nudgeSelectedText(dxPx: 0, dyPx: step, undo: undoManager)
        default: return .ignored
        }
        return .handled
    }

    /// ⌘D（選択中の文字枠を複製）。見えないボタンでキーボードショートカットだけを受ける。
    private var duplicateShortcut: some View {
        Button("文字を複製") { page.duplicateSelectedText(undo: undoManager) }
            .keyboardShortcut("d", modifiers: .command)
            .disabled(page.selectedTextID == nil || page.editingTextID != nil || appState.previewMode)
            .opacity(0)
            .frame(width: 0, height: 0)
            .accessibilityHidden(true)
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
