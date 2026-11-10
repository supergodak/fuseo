import CoreGraphics
import SwiftUI
import XCTest
import MaskingCore
@testable import Fuseo

/// 層1(Mac): 文字入れ 0.3.0 のアプリ層（docs/form-fill-design.md §5/§6）。
/// 作成・移動・大きさ・文字・設定・削除・複製の Undo、設定の引き継ぎ、種別変更で保持・回転/切り抜きで破棄、
/// ページ削除/並べ替えで追従、保存→開き直し、書き出しの数え方、mm 指定、吸い付き、画面描画＝焼き込み。
@MainActor
final class TextEditingTests: XCTestCase {

    private var root: URL!

    override func setUp() {
        super.setUp()
        root = FileManager.default.temporaryDirectory
            .appendingPathComponent("fuseo-text-\(UUID().uuidString)", isDirectory: true)
    }

    override func tearDown() {
        try? FileManager.default.removeItem(at: root)
        super.tearDown()
    }

    // MARK: - ヘルパ

    /// 600×400 の合成ページ（取り込み順に幅 601, 602… でページを見分ける）。
    private func makeState(library: WorkLibrary? = nil) -> (AppState, FakeAnalysis) {
        var width = 600
        let fake = FakeAnalysis { _ in
            width += 1
            let base = TestFixtures.analyzedPage()
            return AnalyzedPage(page: PageImage(cgImage: TestFixtures.cgImage(width: width, height: 400),
                                                sourceURL: nil, rectified: true, quadConfidence: 0.5),
                                ocr: [], classification: base.classification, preset: base.preset, candidates: [])
        }
        let settings = SettingsStore(defaults: UserDefaults(suiteName: "fuseo.text.test.\(UUID().uuidString)")!)
        let state = AppState(analysis: fake, settings: settings, library: library)
        state.autosaveDebounce = .zero
        return (state, fake)
    }

    private func importPages(_ state: AppState, _ n: Int) async {
        await state.processFiles((1...n).map { URL(fileURLWithPath: "/tmp/t\($0).jpg") })
    }

    /// 文字ツールで枠を作って打って確定する（1 手）。
    @discardableResult
    private func addText(_ page: PageState, _ text: String, box: NormRect = NormRect(x: 0.1, y: 0.6, w: 0.4, h: 0.2),
                         style: TextBox.Style = .default, undo: UndoManager?) -> TextBox.ID {
        let id = page.beginNewText(box: box, style: style, undo: undo)
        page.setEditingText(text)
        if let undo { step(undo) { page.commitTextEditing(undo: undo) } } else { page.commitTextEditing(undo: nil) }
        return id
    }

    /// 1 操作 = 1 グループの UndoManager（既定の groupsByEvent だと、テストでは同じランループ内の
    /// 登録が 1 グループにまとまってしまう。アプリではイベントごとに分かれる）。
    private func makeUndo() -> UndoManager {
        let m = UndoManager()
        m.groupsByEvent = false
        return m
    }

    private func step(_ undo: UndoManager, _ body: () -> Void) {
        undo.beginUndoGrouping()
        body()
        undo.endUndoGrouping()
    }

    // MARK: - 作成・入力

    func test_createText_isOneUndoStep_andRedoBringsItBack() async {
        let (state, _) = makeState()
        await importPages(state, 1)
        let page = state.pages[0]
        let undo = makeUndo()

        let id = page.beginNewText(box: NormRect(x: 0.1, y: 0.6, w: 0.4, h: 0.1), style: .default, undo: undo)
        XCTAssertEqual(page.editingTextID, id, "作った枠はそのまま入力状態")
        XCTAssertEqual(page.selectedTextID, id)
        page.setEditingText("山田 太郎")
        XCTAssertEqual(page.analyzed.texts.first?.text, "山田 太郎", "入力中も画面（analyzed）に即反映")
        step(undo) { page.commitTextEditing(undo: undo) }
        XCTAssertNil(page.editingTextID)
        XCTAssertEqual(page.analyzed.texts.count, 1)

        undo.undo()
        XCTAssertTrue(page.analyzed.texts.isEmpty, "作成＋最初の文字が 1 手で消える")
        XCTAssertNil(page.selectedTextID, "消えた枠を選んだままにしない")
        XCTAssertFalse(undo.canUndo)

        undo.redo()
        XCTAssertEqual(page.analyzed.texts.map(\.text), ["山田 太郎"])
    }

    func test_cancelOrEmptyCommit_onNewBox_removesItWithoutUndoStep() async {
        let (state, _) = makeState()
        await importPages(state, 1)
        let page = state.pages[0]
        let undo = makeUndo()

        page.beginNewText(box: NormRect(x: 0.1, y: 0.1, w: 0.2, h: 0.1), style: .default, undo: undo)
        page.setEditingText("途中")
        page.cancelTextEditing()
        XCTAssertTrue(page.analyzed.texts.isEmpty, "Esc で新しい枠ごと取り消す")

        page.beginNewText(box: NormRect(x: 0.1, y: 0.1, w: 0.2, h: 0.1), style: .default, undo: undo)
        page.commitTextEditing(undo: undo)
        XCTAssertTrue(page.analyzed.texts.isEmpty, "空のまま確定した枠は残さない")
        XCTAssertFalse(undo.canUndo, "空の枠は Undo に残らない")
    }

    func test_editExistingText_cancelReverts_commitIsOneUndoStep() async {
        let (state, _) = makeState()
        await importPages(state, 1)
        let page = state.pages[0]
        let undo = makeUndo()
        let id = addText(page, "東京都", undo: undo)

        page.beginTextEditing(id, undo: undo)
        page.setEditingText("東京都千代田区")
        page.cancelTextEditing()
        XCTAssertEqual(page.text(id)?.text, "東京都", "Esc で入力前に戻る")
        XCTAssertEqual(page.selectedTextID, id, "既存の枠は選んだまま")

        page.beginTextEditing(id, undo: undo)
        page.setEditingText("東京都千代田区\n1-1")
        step(undo) { page.commitTextEditing(undo: undo) }
        XCTAssertEqual(page.text(id)?.text, "東京都千代田区\n1-1")
        undo.undo()
        XCTAssertEqual(page.text(id)?.text, "東京都")
        undo.undo()
        XCTAssertTrue(page.analyzed.texts.isEmpty)
    }

    func test_clearingTextOfExistingBox_deletesIt_undoable() async {
        let (state, _) = makeState()
        await importPages(state, 1)
        let page = state.pages[0]
        let undo = makeUndo()
        let id = addText(page, "消す", undo: undo)
        page.beginTextEditing(id, undo: undo)
        page.setEditingText("")
        step(undo) { page.commitTextEditing(undo: undo) }
        XCTAssertTrue(page.analyzed.texts.isEmpty)
        undo.undo()
        XCTAssertEqual(page.text(id)?.text, "消す")
    }

    // MARK: - 移動・大きさ・矢印キー・mm

    func test_moveAndResize_areEachOneUndoStep() async {
        let (state, _) = makeState()
        await importPages(state, 1)
        let page = state.pages[0]
        let undo = makeUndo()
        let start = NormRect(x: 0.1, y: 0.6, w: 0.4, h: 0.1)
        let id = addText(page, "枠", box: start, undo: undo)

        // ドラッグ中は何度更新しても 1 手
        page.setTextBoxLive(id, NormRect(x: 0.15, y: 0.6, w: 0.4, h: 0.1))
        page.setTextBoxLive(id, NormRect(x: 0.2, y: 0.55, w: 0.4, h: 0.1))
        step(undo) { page.commitTextBox(id, from: start, undo: undo) }
        let moved = NormRect(x: 0.2, y: 0.55, w: 0.4, h: 0.1)
        XCTAssertEqual(page.text(id)?.box, moved)

        page.setTextBoxLive(id, NormRect(x: 0.2, y: 0.55, w: 0.5, h: 0.2))
        step(undo) { page.commitTextBox(id, from: moved, undo: undo) }

        undo.undo()
        XCTAssertEqual(page.text(id)?.box, moved, "大きさ変更が 1 手で戻る")
        undo.undo()
        XCTAssertEqual(page.text(id)?.box, start, "移動が 1 手で戻る")
        undo.redo()
        XCTAssertEqual(page.text(id)?.box, moved)
    }

    func test_nudge_movesByImagePixels_shiftTen_andStaysInsidePage() async {
        let (state, _) = makeState()
        await importPages(state, 1)
        let page = state.pages[0]
        let size = page.analyzed.page.pixelSize                   // 601×400
        let id = addText(page, "a", box: CoordinateSpace.normRect(
            fromViewRect: CGRect(x: 100, y: 100, width: 200, height: 40), in: size), undo: nil)
        page.selectedTextID = id
        let undo = makeUndo()

        step(undo) { page.nudgeSelectedText(dxPx: 1, dyPx: 0, undo: undo) }
        step(undo) { page.nudgeSelectedText(dxPx: 0, dyPx: 10, undo: undo) }      // ⇧+↓ = 画面の下へ 10px
        let px = CoordinateSpace.viewRect(page.text(id)!.box, in: size)
        XCTAssertEqual(px.minX, 101, accuracy: 0.001)
        XCTAssertEqual(px.minY, 110, accuracy: 0.001, "画面の下向き（左上原点の y が増える）")
        XCTAssertEqual(px.width, 200, accuracy: 0.001)

        undo.undo()
        XCTAssertEqual(CoordinateSpace.viewRect(page.text(id)!.box, in: size).minY, 100, accuracy: 0.001,
                       "矢印 1 回 = 1 手")

        page.nudgeSelectedText(dxPx: -10_000, dyPx: -10_000, undo: nil)
        let clamped = CoordinateSpace.viewRect(page.text(id)!.box, in: size)
        XCTAssertEqual(clamped.minX, 0, accuracy: 0.001)
        XCTAssertEqual(clamped.minY, 0, accuracy: 0.001)
        XCTAssertEqual(clamped.width, 200, accuracy: 0.001, "ページ端で止まり、大きさは変わらない")
    }

    func test_frameInMillimeters_roundTripsAt300dpi() async {
        let (state, _) = makeState()
        await importPages(state, 1)
        let page = state.pages[0]
        let size = page.analyzed.page.pixelSize
        let id = addText(page, "mm", box: CoordinateSpace.normRect(
            fromViewRect: CGRect(x: 300, y: 118.110236, width: 236.220472, height: 59.055118), in: size), undo: nil)

        let f = try! XCTUnwrap(page.textFrameMM(id))
        XCTAssertEqual(f.x, 25.4, accuracy: 0.01, "300px = 1 インチ = 25.4mm")
        XCTAssertEqual(f.y, 10, accuracy: 0.01, "y はページの左上から")
        XCTAssertEqual(f.width, 20, accuracy: 0.01)
        XCTAssertEqual(f.height, 5, accuracy: 0.01)

        let undo = makeUndo()
        step(undo) { page.setTextFrameMM(id, TextGeometry.FrameMM(x: 5, y: 7.5, width: 30, height: 6), undo: undo) }
        let g = try! XCTUnwrap(page.textFrameMM(id))
        XCTAssertEqual(g.x, 5, accuracy: 0.01)
        XCTAssertEqual(g.y, 7.5, accuracy: 0.01)
        XCTAssertEqual(g.width, 30, accuracy: 0.01)
        XCTAssertEqual(g.height, 6, accuracy: 0.01)
        undo.undo()
        XCTAssertEqual(page.textFrameMM(id)!.x, 25.4, accuracy: 0.01, "数値指定も 1 手")

        // ページより大きい指定はページに収める
        page.setTextFrameMM(id, TextGeometry.FrameMM(x: 40, y: 0, width: 999, height: 5), undo: nil)
        let px = CoordinateSpace.viewRect(page.text(id)!.box, in: size)
        XCTAssertEqual(px.minX, 0, accuracy: 0.01)
        XCTAssertEqual(px.width, size.width, accuracy: 0.01)
    }

    func test_defaultBoxForClick_putsClickAtLeftEdgeAndVerticalCenter() {
        let size = CGSize(width: 2480, height: 3508)                // A4 300dpi
        let click = CoordinateSpace.normPoint(fromViewPoint: CGPoint(x: 500, y: 1000), in: size)
        let box = try! XCTUnwrap(TextGeometry.defaultBox(clickedAt: click, style: .default, imageSize: size))
        let px = CoordinateSpace.viewRect(box, in: size)
        let fontPx = 10.5 * 300 / 72
        XCTAssertEqual(px.minX, 500, accuracy: 0.5)
        XCTAssertEqual(px.midY, 1000, accuracy: 0.5)
        XCTAssertEqual(px.height, fontPx * 2, accuracy: 0.5)
        XCTAssertEqual(px.width, fontPx * 10, accuracy: 0.5)
    }

    // MARK: - 設定と引き継ぎ

    func test_styleChange_onSelectedBox_isOneUndoStep_andCarriesToNextBoxExceptCells() async {
        let (state, _) = makeState()
        await importPages(state, 1)
        let page = state.pages[0]
        let undo = makeUndo()
        let id = addText(page, "123456789012", undo: undo)
        page.selectedTextID = id

        step(undo) { state.applyTextStyle(page: page, undo: undo) { $0.fontSizePt = 14 } }
        step(undo) { state.applyTextStyle(page: page, undo: undo) { $0.cells = 12 } }
        step(undo) { state.applyTextStyle(page: page, undo: undo) { $0.lineSpacing = 1.5; $0.font = .mincho; $0.color = .blue } }
        XCTAssertEqual(page.text(id)?.fontSizePt, 14)
        XCTAssertEqual(page.text(id)?.cells, 12)
        XCTAssertEqual(page.text(id)?.font, .mincho)

        XCTAssertEqual(state.nextTextStyle.fontSizePt, 14, "前に使った設定を次の枠に引き継ぐ")
        XCTAssertEqual(state.nextTextStyle.lineSpacing, 1.5)
        XCTAssertEqual(state.nextTextStyle.font, .mincho)
        XCTAssertEqual(state.nextTextStyle.color, .blue)
        XCTAssertEqual(state.nextTextStyle.cells, 0, "マス数は引き継がない（設計 §1: 書体・サイズ・行間・揃え・色）")

        undo.undo()
        XCTAssertEqual(page.text(id)?.font, .gothic, "設定の変更は 1 回 = 1 手")
        XCTAssertEqual(page.text(id)?.cells, 12)
        undo.undo()
        XCTAssertEqual(page.text(id)?.cells, 0)
        undo.undo()
        XCTAssertEqual(page.text(id)?.fontSizePt, 10.5)

        // 新しい枠は nextTextStyle で作られる（文字そのものは引き継がない）
        page.selectedTextID = nil
        let id2 = page.beginNewText(box: NormRect(x: 0.1, y: 0.1, w: 0.3, h: 0.1), style: state.nextTextStyle, undo: undo)
        XCTAssertEqual(page.text(id2)?.fontSizePt, 14)
        XCTAssertEqual(page.text(id2)?.font, .mincho)
        XCTAssertEqual(page.text(id2)?.text, "")
    }

    func test_styleChange_withoutSelection_editsNextStyle_andClamps() async {
        let (state, _) = makeState()
        await importPages(state, 1)
        let page = state.pages[0]
        let undo = makeUndo()
        state.applyTextStyle(page: page, undo: undo) { $0.fontSizePt = 200; $0.lineSpacing = 0.1; $0.cells = 7 }
        XCTAssertEqual(state.nextTextStyle.fontSizePt, 72, "範囲外は丸める")
        XCTAssertEqual(state.nextTextStyle.lineSpacing, 0.8)
        XCTAssertEqual(state.nextTextStyle.cells, 7, "選んでいないときはマス数も次の枠の設定として持つ")
        XCTAssertFalse(undo.canUndo, "次の枠の設定は書類の編集ではない")
        state.applyTextStyle(page: page, undo: undo) { $0.fontSizePt = 10.26 }
        XCTAssertEqual(state.nextTextStyle.fontSizePt, 10.5, "0.5pt 刻み")
    }

    func test_fitToFrame_setsFontSizeOnce_fromRenderer() async {
        let (state, _) = makeState()
        await importPages(state, 1)
        let page = state.pages[0]
        let undo = makeUndo()
        let id = addText(page, "一行目\n二行目", box: NormRect(x: 0.1, y: 0.5, w: 0.6, h: 0.3), undo: undo)
        page.selectedTextID = id
        let expected = TextBoxRenderer.fittingFontSizePt(page.text(id)!, imageSize: page.analyzed.page.pixelSize)
        step(undo) { state.fitSelectedTextToFrame(page: page, undo: undo) }
        XCTAssertEqual(page.text(id)?.fontSizePt, expected)
        // 枠を変えても自動では追従しない
        step(undo) { page.setTextBox(id, NormRect(x: 0.1, y: 0.5, w: 0.6, h: 0.1), undo: undo) }
        XCTAssertEqual(page.text(id)?.fontSizePt, expected)
    }

    // MARK: - 選択・削除・複製

    func test_selection_isExclusiveWithCandidateAndManualRect() async {
        let (state, _) = makeState()
        await importPages(state, 1)
        let page = state.pages[0]
        page.addRect(NormRect(x: 0.5, y: 0.5, w: 0.1, h: 0.1), undo: nil)
        let id = addText(page, "a", undo: nil)

        page.selectedManualRectIndex = 0
        XCTAssertNil(page.selectedTextID)
        page.selectedTextID = id
        XCTAssertNil(page.selectedManualRectIndex)
        XCTAssertNil(page.selectedBox, "マスクの編集レイヤは出ない")
        page.selectedManualRectIndex = 0
        XCTAssertNil(page.selectedTextID)
    }

    func test_deleteAndDuplicate_areUndoable() async {
        let (state, _) = makeState()
        await importPages(state, 1)
        let page = state.pages[0]
        let size = page.analyzed.page.pixelSize
        let undo = makeUndo()
        let id = addText(page, "複製", box: CoordinateSpace.normRect(
            fromViewRect: CGRect(x: 100, y: 100, width: 120, height: 30), in: size), undo: undo)
        page.selectedTextID = id

        step(undo) { page.duplicateSelectedText(undo: undo) }
        XCTAssertEqual(page.analyzed.texts.count, 2)
        let copy = page.analyzed.texts[1]
        XCTAssertNotEqual(copy.id, id)
        XCTAssertEqual(page.selectedTextID, copy.id, "複製した枠を選ぶ")
        XCTAssertEqual(copy.text, "複製")
        let offset = TextGeometry.pixels(fromMM: 2)
        let px = CoordinateSpace.viewRect(copy.box, in: size)
        XCTAssertEqual(px.minX, 100 + offset, accuracy: 0.01, "少し右下へずらす")
        XCTAssertEqual(px.minY, 100 + offset, accuracy: 0.01)

        step(undo) { page.deleteSelection(undo: undo) }                           // Delete キー
        XCTAssertEqual(page.analyzed.texts.map(\.id), [id])
        XCTAssertNil(page.selectedTextID)

        undo.undo()
        XCTAssertEqual(page.analyzed.texts.count, 2, "削除が 1 手で戻る")
        undo.undo()
        XCTAssertEqual(page.analyzed.texts.map(\.id), [id], "複製が 1 手で戻る")
    }

    // MARK: - 再解析（種別変更で保持・回転/切り抜きで破棄）

    func test_typeChange_keepsTexts() async {
        let (state, _) = makeState()
        await importPages(state, 1)
        let page = state.pages[0]
        addText(page, "保持", undo: nil)
        await state.performTypeChange(page: page, to: .myNumberCardFront)
        XCTAssertEqual(page.analyzed.texts.map(\.text), ["保持"])
    }

    func test_rotate_discardsTexts_afterConfirmation() async {
        let (state, _) = makeState()
        await importPages(state, 1)
        let page = state.pages[0]
        let id = addText(page, "消える", undo: nil)
        page.selectedTextID = id
        XCTAssertTrue(page.hasUserEdits, "文字があれば回転・切り抜きの前に確認する")

        state.requestRotate(page: page)
        XCTAssertEqual(state.pendingRotationPageID, page.id, "確認を挟む")
        await state.performRotate(page: page)
        XCTAssertTrue(page.analyzed.texts.isEmpty, "基準画像が変わるので文字枠は破棄")
        XCTAssertNil(page.selectedTextID)
        XCTAssertNil(page.editingTextID)
    }

    func test_crop_discardsTexts() async {
        let (state, _) = makeState()
        await importPages(state, 1)
        let page = state.pages[0]
        addText(page, "消える", undo: nil)
        await state.applyCrop(page: page, quad: .fullImage)
        XCTAssertTrue(page.analyzed.texts.isEmpty)
    }

    // MARK: - ページの削除・並べ替え

    func test_pageRemoveAndMove_carryTexts() async {
        let (state, _) = makeState()
        await importPages(state, 3)                                 // 幅 601, 602, 603
        addText(state.pages[1], "二枚目", undo: nil)
        addText(state.pages[2], "三枚目", undo: nil)
        let undo = makeUndo()

        step(undo) { state.movePage(at: 2, by: -1, undo: undo) }                   // [601, 603, 602]
        XCTAssertEqual(state.pages.map { $0.analyzed.texts.first?.text }, [nil, "三枚目", "二枚目"])

        step(undo) { state.removePage(at: 1, undo: undo) }                          // [601, 602]
        XCTAssertEqual(state.pages.map { $0.analyzed.texts.first?.text }, [nil, "二枚目"])
        undo.undo()
        XCTAssertEqual(state.pages[1].analyzed.texts.first?.text, "三枚目", "消したページの文字も一緒に戻る")
    }

    // MARK: - 保存→開き直し

    func test_saveAndReopen_keepsTexts_andWritesSchemaV2() async throws {
        let library = FileWorkLibrary(rootURL: root)
        let (state, _) = makeState(library: library)
        await importPages(state, 2)
        var style = TextBox.Style(fontSizePt: 12, lineSpacing: 1.5, font: .mincho, color: .blue,
                                  hAlign: .center, vAlign: .top, cells: 0)
        addText(state.pages[0], "氏名\n二行目", box: NormRect(x: 0.2, y: 0.7, w: 0.5, h: 0.15), style: style, undo: nil)
        style.cells = 12
        addText(state.pages[1], "123456789012", box: NormRect(x: 0.1, y: 0.2, w: 0.8, h: 0.08), style: style, undo: nil)
        let before = state.pages.map(\.analyzed.texts)
        state.flushAutosave()
        let id = try XCTUnwrap(state.currentWorkID)

        let (doc, _) = try library.load(id: id)
        XCTAssertEqual(doc.schemaVersion, 2, "文字があるときだけ v2 で書く")

        state.startNewDocument()
        state.openWork(id: id)
        XCTAssertEqual(state.pages.map(\.analyzed.texts), before, "位置・文字・設定がすべて同じ")
    }

    // MARK: - 書き出しの確認

    func test_exportTextCount_countsNonEmptyBoxes_andOverflow() async {
        let (state, _) = makeState()
        await importPages(state, 2)
        XCTAssertEqual(state.exportTextCount, 0)
        addText(state.pages[0], "a", undo: nil)
        addText(state.pages[1], "b", undo: nil)
        // 小さな枠に大きな文字 → はみ出し
        addText(state.pages[1], "はみ出す長い文字", box: NormRect(x: 0.1, y: 0.1, w: 0.05, h: 0.02),
                style: TextBox.Style(fontSizePt: 40), undo: nil)
        // 入力中の空の枠は数えない
        state.pages[0].beginNewText(box: NormRect(x: 0.5, y: 0.5, w: 0.2, h: 0.1), style: .default, undo: nil)
        XCTAssertEqual(state.exportTextCount, 3)
        XCTAssertEqual(state.exportOverflowingTextCount, 1)
        XCTAssertEqual(state.exportSummary.total, 0, "文字はマスクの数に入れない")
        XCTAssertTrue(state.hasZeroMaskPage, "文字だけのページは「マスクなし」のまま（設計 §3）")
    }

    // MARK: - 吸い付き

    func test_snapMove_alignsLeftRightAndRowCenter_withinThreshold() {
        let other = CGRect(x: 100, y: 200, width: 80, height: 20)    // midY = 210
        let r = CGRect(x: 103, y: 197, width: 50, height: 20)        // 左端 3pt・中央 3pt ずれ
        let res = TextSnap.snapMove(r, others: [other])
        XCTAssertEqual(res.rect.minX, 100)
        XCTAssertEqual(res.rect.midY, 210)
        XCTAssertEqual(Set(res.guides), [TextSnap.Guide(axis: .vertical, position: 100),
                                         TextSnap.Guide(axis: .horizontal, position: 210)])

        let right = CGRect(x: 128, y: 300, width: 50, height: 20)    // 右端 178 → 180 へ
        XCTAssertEqual(TextSnap.snapMove(right, others: [other]).rect.maxX, 180)

        let far = CGRect(x: 110, y: 300, width: 50, height: 20)
        let none = TextSnap.snapMove(far, others: [other])
        XCTAssertEqual(none.rect, far, "離れていれば吸い付かない")
        XCTAssertTrue(none.guides.isEmpty)
    }

    func test_snapResize_onlyMovesTheDraggedEdge() {
        let other = CGRect(x: 100, y: 0, width: 80, height: 20)
        let r = CGRect(x: 50, y: 100, width: 128, height: 30)       // 右端 178
        let res = TextSnap.snapResize(r, movingLeft: false, movingRight: true, others: [other])
        XCTAssertEqual(res.rect.minX, 50)
        XCTAssertEqual(res.rect.maxX, 180)
        XCTAssertEqual(res.rect.height, 30)
        let notMoving = TextSnap.snapResize(r, movingLeft: true, movingRight: false, others: [other])
        XCTAssertEqual(notMoving.rect, r, "左端は遠いので吸い付かず、右端は動かしていない")
    }

    // MARK: - 画面描画 = 焼き込み

    /// キャンバスの文字レイヤ（SwiftUI Canvas）と書き出しの焼き込みで、文字が同じ位置に出ること。
    func test_canvasTextLayer_inksSamePlaceAsBurnIn() throws {
        let size = CGSize(width: 300, height: 200)
        let page = PageImage(cgImage: whiteImage(Int(size.width), Int(size.height)), sourceURL: nil,
                             rectified: true, quadConfidence: 1)
        // 画面の上寄り・左寄りの枠（左上原点で y = 20〜80）
        let box = CoordinateSpace.normRect(fromViewRect: CGRect(x: 20, y: 20, width: 200, height: 60), in: size)
        let texts = [TextBox(box: box, text: "■■■", style: TextBox.Style(fontSizePt: 8, vAlign: .top))]

        let burned = try RasterMaskRenderer().burnIn(page: page, texts: texts, masks: [], strokes: [])
        let renderer = ImageRenderer(content: ZStack {
            Color.white
            TextInkLayer(texts: texts, imageSize: size)
        }.frame(width: size.width, height: size.height))
        renderer.scale = 1
        let shown = try XCTUnwrap(renderer.cgImage)

        let a = try XCTUnwrap(inkBounds(burned), "焼き込みに文字が出ている")
        let b = try XCTUnwrap(inkBounds(shown), "画面のレイヤに文字が出ている")
        XCTAssertLessThan(a.minY, 60, "枠の上寄り（左上原点）に出る")
        XCTAssertEqual(a.minX, b.minX, accuracy: 2)
        XCTAssertEqual(a.minY, b.minY, accuracy: 2, "上下が反転していない")
        XCTAssertEqual(a.maxX, b.maxX, accuracy: 2)
        XCTAssertEqual(a.maxY, b.maxY, accuracy: 2)
    }

    private func whiteImage(_ w: Int, _ h: Int) -> CGImage {
        let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: w, height: h))
        return ctx.makeImage()!
    }

    /// 暗い画素の外接矩形（左上原点のピクセル）。
    private func inkBounds(_ image: CGImage) -> CGRect? {
        let w = image.width, h = image.height
        var data = [UInt8](repeating: 0, count: w * h * 4)
        let ctx = CGContext(data: &data, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        var minX = Int.max, minY = Int.max, maxX = -1, maxY = -1
        for row in 0..<h {                      // メモリの先頭行 = 画像の上端
            for col in 0..<w {
                let i = (row * w + col) * 4
                if Int(data[i]) + Int(data[i + 1]) + Int(data[i + 2]) < 300 {
                    minX = min(minX, col); maxX = max(maxX, col)
                    minY = min(minY, row); maxY = max(maxY, row)
                }
            }
        }
        guard maxX >= 0 else { return nil }
        return CGRect(x: minX, y: minY, width: maxX - minX + 1, height: maxY - minY + 1)
    }
}
