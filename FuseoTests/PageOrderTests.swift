import CoreGraphics
import XCTest
import MaskingCore
@testable import Fuseo

/// 層1(Mac): 0.2.3 ページの削除・並べ替え。選択の移り方・Undo/Redo・最後の 1 ページを消さないこと、
/// そして**保存して開き直しても各ページが自分の画像のまま**であること（保存のページ画像は位置で名前が決まるため）。
@MainActor
final class PageOrderTests: XCTestCase {

    private var root: URL!

    override func setUp() {
        super.setUp()
        root = FileManager.default.temporaryDirectory
            .appendingPathComponent("fuseo-pageorder-\(UUID().uuidString)", isDirectory: true)
    }

    override func tearDown() {
        try? FileManager.default.removeItem(at: root)
        super.tearDown()
    }

    /// 取り込み順に幅 41, 42, 43… の画像を返す（ページの見分けに使う）。
    private func makeState(library: WorkLibrary? = nil) -> AppState {
        var width = 40
        let fake = FakeAnalysis { _ in
            width += 1
            let base = TestFixtures.analyzedPage()
            return AnalyzedPage(page: PageImage(cgImage: TestFixtures.cgImage(width: width, height: 24),
                                                sourceURL: nil, rectified: true, quadConfidence: 0.5),
                                ocr: [], classification: base.classification, preset: base.preset, candidates: [])
        }
        let settings = SettingsStore(defaults: UserDefaults(suiteName: "fuseo.order.test.\(UUID().uuidString)")!)
        let state = AppState(analysis: fake, settings: settings, library: library)
        state.autosaveDebounce = .zero
        return state
    }

    private func importPages(_ state: AppState, _ n: Int) async {
        await state.processFiles((1...n).map { URL(fileURLWithPath: "/tmp/p\($0).jpg") })
    }

    private func widths(_ state: AppState) -> [Int] { state.pages.map { $0.analyzed.page.cgImage.width } }

    func test_removePage_selectsSamePosition_undoRestores_redoRemovesAgain() async {
        let state = makeState()
        await importPages(state, 3)
        let undo = UndoManager()
        state.currentPageIndex = 1

        state.removePage(at: 1, undo: undo)
        XCTAssertEqual(widths(state), [41, 43])
        XCTAssertEqual(state.currentPageIndex, 1, "同じ位置（次のページ）を選ぶ")

        undo.undo()
        XCTAssertEqual(widths(state), [41, 42, 43], "同じ位置に戻る")
        XCTAssertEqual(state.currentPageIndex, 1, "戻したページを選ぶ")

        undo.redo()
        XCTAssertEqual(widths(state), [41, 43])
    }

    func test_removeLastPositionAndEarlierPage_adjustSelection() async {
        let state = makeState()
        await importPages(state, 3)
        state.currentPageIndex = 2
        state.removePage(at: 2, undo: nil)
        XCTAssertEqual(state.currentPageIndex, 1, "末尾を消したら一つ前")

        state.currentPageIndex = 1
        state.removePage(at: 0, undo: nil)
        XCTAssertEqual(widths(state), [42])
        XCTAssertEqual(state.currentPageIndex, 0, "前のページを消したら選択も一つ前へずれる（同じページを選んだまま）")
    }

    func test_lastRemainingPage_cannotBeRemoved() async {
        let state = makeState()
        await importPages(state, 1)
        XCTAssertFalse(state.canRemovePage)
        state.removePage(at: 0, undo: nil)
        XCTAssertEqual(state.pages.count, 1)
    }

    func test_movePage_keepsItSelected_andUndoes() async {
        let state = makeState()
        await importPages(state, 3)
        let undo = UndoManager()
        XCTAssertFalse(state.canMovePage(at: 0, by: -1))
        XCTAssertFalse(state.canMovePage(at: 2, by: 1))

        state.movePage(at: 0, by: 1, undo: undo)
        XCTAssertEqual(widths(state), [42, 41, 43])
        XCTAssertEqual(state.currentPageIndex, 1, "動かしたページを選んだまま")

        undo.undo()
        XCTAssertEqual(widths(state), [41, 42, 43])
        XCTAssertEqual(state.currentPageIndex, 0)
    }

    func test_removedPageEdits_comeBackWithUndo() async {
        let state = makeState()
        await importPages(state, 2)
        let undo = UndoManager()
        state.pages[1].addRect(NormRect(x: 0.2, y: 0.2, w: 0.1, h: 0.1), undo: nil)
        state.removePage(at: 1, undo: undo)
        undo.undo()
        XCTAssertEqual(state.pages[1].analyzed.manual.rects.count, 1, "消したページのマスクも一緒に戻る")
    }

    /// 保存のページ画像は位置（index）で名前が決まる。削除・並べ替えのあとで開き直しても、
    /// 各ページが自分の画像のままで、消したページの画像が残らないこと。
    func test_saveAndReopen_afterRemoveAndMove_keepsEachPagesOwnImage() async throws {
        let library = FileWorkLibrary(rootURL: root)
        let state = makeState(library: library)
        await importPages(state, 3)                       // [41, 42, 43]
        state.pages[2].addRect(NormRect(x: 0.5, y: 0.5, w: 0.1, h: 0.1), undo: nil)
        state.flushAutosave()

        state.removePage(at: 0, undo: nil)                 // [42, 43]
        state.movePage(at: 1, by: -1, undo: nil)           // [43, 42]
        state.flushAutosave()
        let id = try XCTUnwrap(state.currentWorkID)

        let (doc, dir) = try library.load(id: id)
        XCTAssertEqual(doc.pages.count, 2)
        let files = try FileManager.default.contentsOfDirectory(atPath: dir.appendingPathComponent("pages").path)
        XCTAssertEqual(files.count, 2, "消したページの画像が残っていない")

        state.startNewDocument()
        state.openWork(id: id)
        XCTAssertEqual(widths(state), [43, 42], "開き直しても各ページが自分の画像")
        XCTAssertEqual(state.pages[0].analyzed.manual.rects.count, 1, "マスクも画像と一緒に動いている")
        XCTAssertEqual(state.pages[1].analyzed.manual.rects.count, 0)
    }
}
