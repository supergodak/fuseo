import CoreGraphics
import XCTest
import MaskingCore
@testable import Fuseo

/// 層1(Mac): WP-13「作業の保存と再開」のアプリ層（docs/wp13-library-design.md §2/§5）。
/// ①橋渡しのラウンドトリップ ②復元ページが平面扱い ③自動保存のデバウンスと即時フラッシュ
/// ④状態遷移（exported→編集→inProgress）⑤「新しい書類」で一覧に残る ⑥開くで復元 ⑦削除
/// ⑧`ranking` 非依存の種別一覧 を監視する。
///
/// **iOS 版 `FuseoiOSTests/WorkLibraryAppTests.swift` と同内容**（import 行だけが違う）。
@MainActor
final class WorkLibraryAppTests: XCTestCase {

    private var root: URL!

    override func setUp() {
        super.setUp()
        root = FileManager.default.temporaryDirectory
            .appendingPathComponent("fuseo-applib-\(UUID().uuidString)", isDirectory: true)
    }

    override func tearDown() {
        try? FileManager.default.removeItem(at: root)
        super.tearDown()
    }

    // MARK: - ヘルパ

    private func makeSettings() -> SettingsStore {
        SettingsStore(defaults: UserDefaults(suiteName: "fuseo.lib.test.\(UUID().uuidString)")!)
    }

    private func makeState(_ library: WorkLibrary?,
                           candidates: [MaskCandidate] = [],
                           type: DocumentType = .generic) -> AppState {
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage(type: type, candidates: candidates) }
        let state = AppState(analysis: fake, settings: makeSettings(), library: library)
        state.autosaveDebounce = .zero
        return state
    }

    /// 保存回数を数えるライブラリ（デバウンスの検証用）。任意で保存を失敗させられる。
    private final class CountingLibrary: WorkLibrary, @unchecked Sendable {
        private let inner: FileWorkLibrary
        private(set) var saveCount = 0
        var failSave = false

        init(rootURL: URL) { inner = FileWorkLibrary(rootURL: rootURL) }

        func list() throws -> [WorkDocumentSummary] { try inner.list() }
        func load(id: UUID) throws -> (document: WorkDocument, directory: URL) { try inner.load(id: id) }
        func save(_ doc: WorkDocument, pageImages: [Int: CGImage]) throws {
            saveCount += 1
            if failSave { throw WorkLibraryError.ioFailed("テスト用の失敗") }
            try inner.save(doc, pageImages: pageImages)
        }
        func delete(id: UUID) throws { try inner.delete(id: id) }
        func deleteAll() throws { try inner.deleteAll() }
    }

    private func importOnePage(_ state: AppState, name: String = "a.jpg") async {
        await state.processFiles([URL(fileURLWithPath: "/tmp/\(name)")])
    }

    // MARK: - ① 橋渡しのラウンドトリップ

    func test_bridge_roundTrip_preservesMasksRotationAndType() async throws {
        let library = FileWorkLibrary(rootURL: root)
        let on = TestFixtures.candidate(ruleID: "x.num", source: .detector(.myNumber12), isOn: true)
        let off = TestFixtures.candidate(ruleID: "x.face", source: .detector(.face), isOn: false)
        let state = makeState(library, candidates: [on, off], type: .menkyoshoFront)
        await importOnePage(state)

        let page = state.pages[0]
        page.analyzed.candidates[1].box = NormRect(x: 0.4, y: 0.4, w: 0.2, h: 0.1)
        page.addRect(NormRect(x: 0.5, y: 0.5, w: 0.1, h: 0.1), undo: nil)
        page.addStroke(BrushStroke(points: [CGPoint(x: 0.1, y: 0.1)], width: 0.04), undo: nil)
        page.manualRotation = 3
        page.forcedType = .menkyoshoFront
        state.flushAutosave()

        let id = try XCTUnwrap(state.currentWorkID)
        let (doc, dir) = try library.load(id: id)
        let restored = try WorkDocumentBridge.restore(document: doc, directory: dir,
                                                      presets: state.allPresets)

        XCTAssertEqual(restored.count, 1)
        let back = restored[0]
        XCTAssertEqual(back.analyzed.candidates.map(\.isOn), [true, false], "候補の採否が一致")
        XCTAssertEqual(back.analyzed.candidates.map(\.box), page.analyzed.candidates.map(\.box),
                       "候補の矩形が一致")
        XCTAssertEqual(back.analyzed.candidates.map(\.id), page.analyzed.candidates.map(\.id),
                       "候補 id が保持される")
        XCTAssertEqual(back.analyzed.manual.rects, page.analyzed.manual.rects, "手動矩形が一致")
        XCTAssertEqual(back.analyzed.manual.strokes.count, 1, "ブラシが一致")
        XCTAssertEqual(back.manualRotation, 3, "回転が一致")
        XCTAssertEqual(back.analyzed.preset.documentType, .menkyoshoFront, "種別が一致")
        XCTAssertEqual(back.forcedType, .menkyoshoFront, "手動指定の種別が一致")
    }

    // MARK: - ② 復元ページは「平面」扱い

    func test_restoredPage_isTreatedAsFlat() async throws {
        let library = FileWorkLibrary(rootURL: root)
        let state = makeState(library)
        await importOnePage(state)
        state.pages[0].manualRotation = 1
        state.flushAutosave()

        let id = try XCTUnwrap(state.currentWorkID)
        let (doc, dir) = try library.load(id: id)
        let back = try WorkDocumentBridge.restore(document: doc, directory: dir,
                                                  presets: state.allPresets)[0]

        XCTAssertTrue(back.isRestored)
        XCTAssertNil(back.manualQuad, "基準画像には切り抜き適用済み。二重に切り抜かない")
        XCTAssertNil(back.reanalysisQuad, "復元ページに手動切り抜きは無い"); XCTAssertEqual(back.analysisOptions.documentDetection, .off, "再解析は基準画像をそのまま（検出 off）")
        XCTAssertFalse(back.analysisOptions.detectUpright, "基準画像は正立済み（再判定させない）")
        XCTAssertEqual(back.analysisRotation, 0, "回転は基準画像に適用済み＝差分は 0")
        XCTAssertEqual(back.analyzed.page.sourceURL, back.sourceURL, "基準画像 PNG を指す")
    }

    // MARK: - ③ デバウンスと即時フラッシュ

    func test_autosave_debouncesRapidEdits() async throws {
        let library = CountingLibrary(rootURL: root)
        let cand = TestFixtures.candidate(ruleID: "x.num", source: .detector(.myNumber12), isOn: true)
        let state = makeState(library, candidates: [cand])
        state.autosaveDebounce = .milliseconds(60)
        await importOnePage(state)
        try await wait(until: { library.saveCount == 1 }, "取り込みで1回保存される")

        let page = state.pages[0]
        page.addRect(NormRect(x: 0.1, y: 0.1, w: 0.1, h: 0.1), undo: nil)
        page.addRect(NormRect(x: 0.2, y: 0.2, w: 0.1, h: 0.1), undo: nil)
        page.toggleCandidate(page.analyzed.candidates[0].id)

        try await Task.sleep(for: .milliseconds(250))
        XCTAssertEqual(library.saveCount, 2, "連続した変更はまとめて1回だけ保存する")
    }

    func test_flushAutosave_savesImmediately() async throws {
        let library = CountingLibrary(rootURL: root)
        let state = makeState(library)
        state.autosaveDebounce = .seconds(30)
        await importOnePage(state)
        XCTAssertEqual(library.saveCount, 0, "デバウンス中はまだ保存していない")

        state.flushAutosave()
        XCTAssertEqual(library.saveCount, 1, "終了・バックグラウンド移行では即時に確定する")
        XCTAssertEqual(try library.list().count, 1)
    }

    // MARK: - ④ 状態遷移（exported → 編集 → inProgress）

    func test_status_exportedThenEdit_returnsToInProgress() async throws {
        let library = FileWorkLibrary(rootURL: root)
        let state = makeState(library)
        await importOnePage(state)
        state.flushAutosave()
        XCTAssertEqual(state.workStatus, .inProgress)

        state.markExported(name: "a-masked.pdf", url: URL(fileURLWithPath: "/tmp/a-masked.pdf"))
        state.flushAutosave()
        let id = try XCTUnwrap(state.currentWorkID)
        XCTAssertEqual(try library.load(id: id).document.status, .exported)
        XCTAssertEqual(try library.load(id: id).document.lastExportedName, "a-masked.pdf")
        XCTAssertTrue(state.showingExportDone, "完了トーストを出す")

        state.pages[0].addRect(NormRect(x: 0.1, y: 0.1, w: 0.1, h: 0.1), undo: nil)
        XCTAssertEqual(state.workStatus, .inProgress, "書き出し後に編集したら「作業中」へ戻す")
        state.flushAutosave()
        XCTAssertEqual(try library.load(id: id).document.status, .inProgress)
    }

    // MARK: - ⑤「新しい書類」で一覧に残る

    func test_startNewDocument_keepsWorkInLibrary() async throws {
        let library = FileWorkLibrary(rootURL: root)
        let state = makeState(library)
        await importOnePage(state, name: "menkyo.jpg")
        state.pages[0].addRect(NormRect(x: 0.2, y: 0.2, w: 0.1, h: 0.1), undo: nil)

        state.startNewDocument()

        XCTAssertTrue(state.pages.isEmpty)
        XCTAssertEqual(state.stage, .empty)
        XCTAssertNil(state.currentWorkID, "次の取り込みは別の作業になる")
        XCTAssertEqual(state.libraryItems.count, 1, "作業は一覧に残る")
        XCTAssertEqual(state.libraryItems[0].title, "menkyo.jpg")
        XCTAssertEqual(state.libraryItems[0].status, .inProgress)
    }

    // MARK: - ⑥ 開くで復元（Vision を走らせない）

    func test_openWork_restoresMasksWithoutReanalysis() async throws {
        let library = FileWorkLibrary(rootURL: root)
        let cand = TestFixtures.candidate(ruleID: "x.num", source: .detector(.myNumber12), isOn: true)
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage(candidates: [cand]) }
        let state = AppState(analysis: fake, settings: makeSettings(), library: library)
        state.autosaveDebounce = .zero
        await importOnePage(state)
        state.pages[0].addRect(NormRect(x: 0.3, y: 0.3, w: 0.2, h: 0.2), undo: nil)
        state.pages[0].toggleCandidate(state.pages[0].analyzed.candidates[0].id)  // isOn -> false
        state.startNewDocument()
        let callsBefore = fake.analyzeCallCount
        let id = try XCTUnwrap(state.libraryItems.first?.id)

        state.openWork(id: id)

        XCTAssertEqual(state.stage, .review)
        XCTAssertEqual(state.pages.count, 1)
        XCTAssertEqual(state.pages[0].analyzed.manual.rects.count, 1, "手動マスクが残っている")
        XCTAssertEqual(state.pages[0].analyzed.candidates.map(\.isOn), [false], "候補の採否が残っている")
        XCTAssertEqual(state.currentWorkID, id, "以後の自動保存は同じ作業へ上書きする")
        XCTAssertEqual(fake.analyzeCallCount, callsBefore, "開くときに再解析しない（Vision を走らせない）")

        // 開いた作業に加えた編集は同じ id に上書きされる（作業が増えない）。
        state.pages[0].addRect(NormRect(x: 0.6, y: 0.6, w: 0.1, h: 0.1), undo: nil)
        state.flushAutosave()
        XCTAssertEqual(try library.list().count, 1)
        XCTAssertEqual(try library.load(id: id).document.pages[0].manual.rects.count, 2)
    }

    // MARK: - ⑦ 削除

    func test_deleteWork_andDeleteAll() async throws {
        let library = FileWorkLibrary(rootURL: root)
        let state = makeState(library)
        await importOnePage(state, name: "one.jpg")
        state.startNewDocument()
        await importOnePage(state, name: "two.jpg")
        state.startNewDocument()
        XCTAssertEqual(state.libraryItems.count, 2)

        state.deleteWork(id: state.libraryItems[0].id)
        XCTAssertEqual(state.libraryItems.count, 1)

        state.deleteAllWorks()
        XCTAssertTrue(state.libraryItems.isEmpty)
        XCTAssertTrue(try library.list().isEmpty)
    }

    // MARK: - ⑧ 種別一覧は `ranking` に依存しない

    func test_typeOptions_doNotDependOnRanking() async throws {
        let library = FileWorkLibrary(rootURL: root)
        let state = makeState(library, type: .menkyoshoFront)
        await importOnePage(state)
        state.flushAutosave()
        let id = try XCTUnwrap(state.currentWorkID)
        state.startNewDocument()
        state.openWork(id: id)

        let page = state.pages[0]
        XCTAssertTrue(page.analyzed.classification.ranking.isEmpty, "復元ページは ranking を持たない")

        let options = state.typeOptions(for: page)
        XCTAssertEqual(options.count, state.allPresets.count,
                       "プリセット全件から選択肢を作る: \(options.map(\.type))")
        XCTAssertTrue(options.contains { $0.type == .menkyoshoFront }, "現在の種別を含む")
        XCTAssertTrue(options.contains { $0.type == .generic })
        XCTAssertTrue(options.allSatisfy { $0.score == nil }, "スコアは分かるものだけ添える")
        XCTAssertEqual(Set(options.map(\.type)).count, options.count, "重複しない")
    }

    // MARK: - ⑨ 保存失敗は黙らない

    func test_saveFailure_showsBanner() async throws {
        let library = CountingLibrary(rootURL: root)
        library.failSave = true
        let state = makeState(library)
        await importOnePage(state)
        state.flushAutosave()

        XCTAssertNotNil(state.librarySaveError, "保存失敗はバナーで知らせる")
        XCTAssertTrue(state.librarySaveError?.contains("保存できませんでした") == true,
                      "文言: \(state.librarySaveError ?? "")")
    }

    // MARK: - ヘルパ

    private func wait(until condition: () -> Bool, _ what: String,
                      timeout: TimeInterval = 3) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition() {
            if Date() > deadline { return XCTFail("タイムアウト: \(what)") }
            try await Task.sleep(for: .milliseconds(10))
        }
    }
}
