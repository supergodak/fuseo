import XCTest
import MaskingCore
@testable import Fuseo

/// 層1: AppState の状態遷移・faceMaskDefaultOn 上書き・ゼロマスク判定・書き出しサマリ。
@MainActor
final class AppStateTests: XCTestCase {

    private func makeSettings(faceDefault: Bool = false) -> SettingsStore {
        let s = SettingsStore(defaults: UserDefaults(suiteName: "fuseo.test.\(UUID().uuidString)")!)
        s.faceMaskDefaultOn = faceDefault
        return s
    }

    private func makeState(analysis: Analyzing, settings: SettingsStore) -> AppState {
        AppState(analysis: analysis, settings: settings)
    }

    func test_processFiles_success_movesToReview() async {
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage() }
        let state = makeState(analysis: fake, settings: makeSettings())
        XCTAssertEqual(state.stage, .empty)
        await state.processFiles([URL(fileURLWithPath: "/tmp/a.jpg")])
        XCTAssertEqual(state.stage, .review)
        XCTAssertEqual(state.pages.count, 1)
    }

    func test_processFiles_allFail_returnsToEmpty() async {
        final class FailAnalysis: Analyzing {
            func analyze(url: URL, forcedType: DocumentType?, manualQuad: Quad?, manualRotation: Int,
                         options: AnalysisOptions) async throws -> AnalyzedPage {
                throw MaskingError.loadFailed("x")
            }
            func cropPreview(url: URL) async throws -> VisionRectifier.CropPreview? { nil }
            func displayName(for type: DocumentType) -> String { type.rawValue }
        }
        let state = makeState(analysis: FailAnalysis(), settings: makeSettings())
        await state.processFiles([URL(fileURLWithPath: "/tmp/a.jpg")])
        XCTAssertEqual(state.stage, .empty)
        XCTAssertTrue(state.showingError)
    }

    func test_processFiles_multiple_buildsPagesInOrder() async {
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage() }
        let state = makeState(analysis: fake, settings: makeSettings())
        await state.processFiles([URL(fileURLWithPath: "/tmp/a.jpg"),
                                  URL(fileURLWithPath: "/tmp/b.jpg")])
        XCTAssertEqual(state.pages.count, 2)
        XCTAssertEqual(fake.analyzeCallCount, 2)
    }

    func test_faceMaskDefaultOn_overridesFaceCandidate() async {
        let faceCand = TestFixtures.candidate(ruleID: "x.face", source: .detector(.face), isOn: false)
        let otherCand = TestFixtures.candidate(ruleID: "x.num", source: .detector(.myNumber12), isOn: false)
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage(candidates: [faceCand, otherCand]) }

        // 既定OFF: 顔候補は false のまま。
        let stateOff = makeState(analysis: fake, settings: makeSettings(faceDefault: false))
        await stateOff.processFiles([URL(fileURLWithPath: "/tmp/a.jpg")])
        let faceOff = stateOff.pages[0].analyzed.candidates.first { $0.ruleID == "x.face" }!
        XCTAssertFalse(faceOff.isOn)

        // 既定ON: 顔候補だけ true に上書き、他は据え置き。
        let stateOn = makeState(analysis: fake, settings: makeSettings(faceDefault: true))
        await stateOn.processFiles([URL(fileURLWithPath: "/tmp/a.jpg")])
        let faceOn = stateOn.pages[0].analyzed.candidates.first { $0.ruleID == "x.face" }!
        let otherOn = stateOn.pages[0].analyzed.candidates.first { $0.ruleID == "x.num" }!
        XCTAssertTrue(faceOn.isOn)
        XCTAssertFalse(otherOn.isOn)
    }

    func test_zeroMaskJudgment_perPageAndOverall() async {
        // ページ1: ON候補あり / ページ2: 候補ゼロ
        let onCand = TestFixtures.candidate(ruleID: "x.num", source: .detector(.myNumber12), isOn: true)
        var call = 0
        let fake = FakeAnalysis { _ in
            call += 1
            return call == 1
                ? TestFixtures.analyzedPage(candidates: [onCand])
                : TestFixtures.analyzedPage(candidates: [])
        }
        let state = makeState(analysis: fake, settings: makeSettings())
        await state.processFiles([URL(fileURLWithPath: "/tmp/a.jpg"),
                                  URL(fileURLWithPath: "/tmp/b.jpg")])
        XCTAssertFalse(state.pages[0].hasZeroMask)
        XCTAssertTrue(state.pages[1].hasZeroMask)
        XCTAssertTrue(state.hasZeroMaskPage)
    }

    func test_exportSummary_countsAutoAndManual() async {
        let onCand = TestFixtures.candidate(ruleID: "x.num", source: .detector(.myNumber12), isOn: true)
        let offCand = TestFixtures.candidate(ruleID: "x.face", source: .detector(.face), isOn: false)
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage(candidates: [onCand, offCand]) }
        let state = makeState(analysis: fake, settings: makeSettings())
        await state.processFiles([URL(fileURLWithPath: "/tmp/a.jpg")])
        // 手動矩形を1つ足す
        state.pages[0].addRect(NormRect(x: 0.4, y: 0.4, w: 0.1, h: 0.1), undo: nil)

        let summary = state.exportSummary
        XCTAssertEqual(summary.auto, 1)     // ON候補のみ
        XCTAssertEqual(summary.manual, 1)   // 追加した矩形
        XCTAssertEqual(summary.total, 2)
        XCTAssertEqual(summary.pageCount, 1)
    }

    func test_defaultExportFileName_usesFirstSource() async {
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage() }
        let settings = makeSettings()
        settings.defaultExportFormat = .pdf
        let state = AppState(analysis: fake, settings: settings)
        await state.processFiles([URL(fileURLWithPath: "/tmp/license-front.jpeg")])
        state.exportOptions.format = .pdf
        XCTAssertEqual(state.defaultExportFileName, "license-front-masked.pdf")
    }

    func test_typeChange_withEdits_requiresConfirmation() async {
        let cand = TestFixtures.candidate(ruleID: "x.num", source: .detector(.myNumber12), isOn: true)
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage(candidates: [cand]) }
        let state = makeState(analysis: fake, settings: makeSettings())
        await state.processFiles([URL(fileURLWithPath: "/tmp/a.jpg")])
        let page = state.pages[0]

        // 未編集なら確認不要（即再解析）。
        state.requestTypeChange(page: page, to: .menkyoshoFront)
        XCTAssertNil(state.pendingTypeChange)

        // 候補を編集すると確認待ちになる。
        page.toggleCandidate(cand.id)
        XCTAssertTrue(page.candidatesEdited)
        state.requestTypeChange(page: page, to: .hokensho)
        XCTAssertNotNil(state.pendingTypeChange)
    }

    // MARK: - 再解析の連打ガード

    func test_reanalysisGuard_ignoresReentrantCalls() async {
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage() }
        let state = makeState(analysis: fake, settings: makeSettings())
        await state.processFiles([URL(fileURLWithPath: "/tmp/a.jpg")])
        let page = state.pages[0]
        let baseline = fake.analyzeCallCount

        // 再解析中フラグが立っている間は、回転・種別変更・切り抜きとも黙って無視される
        state.reanalyzing = true
        await state.performRotate(page: page)
        await state.performTypeChange(page: page, to: .menkyoshoFront)
        await state.applyCrop(page: page, quad: .fullImage)
        XCTAssertEqual(fake.analyzeCallCount, baseline, "実行中の再入は解析を走らせない")
        XCTAssertEqual(page.manualRotation, 0, "状態も変えない")

        // フラグが下りれば通常どおり動く（実行後は自動で false に戻る）
        state.reanalyzing = false
        await state.performRotate(page: page)
        XCTAssertEqual(fake.analyzeCallCount, baseline + 1)
        XCTAssertEqual(page.manualRotation, 1)
        XCTAssertFalse(state.reanalyzing)
    }

    // MARK: - 「新しい書類」の誤操作防止

    func test_requestReset_confirmsWhenDocumentsAreOpen() async {
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage() }
        let state = makeState(analysis: fake, settings: makeSettings())

        // 何も開いていない → 即リセット（確認なし）
        state.requestReset()
        XCTAssertFalse(state.confirmingReset)
        XCTAssertEqual(state.stage, .empty)

        // 書類を開いている → 確認待ちになり、作業内容は無傷
        await state.processFiles([URL(fileURLWithPath: "/tmp/a.jpg")])
        state.requestReset()
        XCTAssertTrue(state.confirmingReset)
        XCTAssertEqual(state.pages.count, 1, "確認前に破棄しない")
        XCTAssertEqual(state.stage, .review)

        // キャンセル → 何も失われない
        state.confirmingReset = false
        XCTAssertEqual(state.pages.count, 1)

        // 確認 → 破棄して新規へ
        state.requestReset()
        state.confirmReset()
        XCTAssertTrue(state.pages.isEmpty)
        XCTAssertEqual(state.stage, .empty)
    }

    // MARK: - 一括処理（v1.2）

    func test_processFiles_fiveOrMore_startsInGridLayout() async {
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage() }
        let state = makeState(analysis: fake, settings: makeSettings())
        await state.processFiles((1...5).map { URL(fileURLWithPath: "/tmp/p\($0).jpg") })
        XCTAssertEqual(state.reviewLayout, .grid, "5枚以上は一覧レビューから始まる")

        let state2 = makeState(analysis: fake, settings: makeSettings())
        await state2.processFiles([URL(fileURLWithPath: "/tmp/a.jpg")])
        XCTAssertEqual(state2.reviewLayout, .single)
    }

    func test_exportSeparately_writesOneFilePerDocumentWithSourceNames() async throws {
        let cand = TestFixtures.candidate(ruleID: "x.num", source: .detector(.myNumber12), isOn: true)
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage(candidates: [cand]) }
        let state = makeState(analysis: fake, settings: makeSettings())
        await state.processFiles([URL(fileURLWithPath: "/tmp/hoken-a.jpg"),
                                  URL(fileURLWithPath: "/tmp/menkyo-b.jpg")])
        state.exportOptions = ExportOptions(format: .png)

        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("fuseo-batch-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        let written = try state.exportSeparately(to: dir)
        XCTAssertEqual(written.map(\.lastPathComponent), ["hoken-a-masked.png", "menkyo-b-masked.png"])
        for url in written {
            XCTAssertTrue(FileManager.default.fileExists(atPath: url.path))
        }

        // 同じフォルダへもう一度 → 重複回避の -2 が付く
        let again = try state.exportSeparately(to: dir)
        XCTAssertEqual(again.map(\.lastPathComponent), ["hoken-a-masked-2.png", "menkyo-b-masked-2.png"])
    }

    func test_fileIntake_expandsFoldersOneLevelDeep() throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("fuseo-intake-\(UUID().uuidString)")
        let sub = root.appendingPathComponent("sub")
        try FileManager.default.createDirectory(at: sub, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        for name in ["b.jpeg", "a.png", "skip.txt"] {
            FileManager.default.createFile(atPath: root.appendingPathComponent(name).path, contents: Data())
        }
        FileManager.default.createFile(atPath: sub.appendingPathComponent("c.heic").path, contents: Data())

        let expanded = FileIntake.expand([root])
        XCTAssertEqual(expanded.map(\.lastPathComponent), ["a.png", "b.jpeg", "c.heic"],
                       "名前順・非対応拡張子は除外・1階層下まで展開")
        // ファイル直接指定はそのまま
        let single = FileIntake.expand([root.appendingPathComponent("a.png")])
        XCTAssertEqual(single.map(\.lastPathComponent), ["a.png"])
    }

    // MARK: - 手動回転

    func test_rotate_incrementsAndCarriesThroughReanalysis() async {
        let cand = TestFixtures.candidate(ruleID: "x.num", source: .detector(.myNumber12), isOn: true)
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage(candidates: [cand]) }
        let state = makeState(analysis: fake, settings: makeSettings())
        await state.processFiles([URL(fileURLWithPath: "/tmp/a.jpg")])
        let page = state.pages[0]

        // 未編集 → 即回転（確認なし）。90°×1 が渡り、状態も更新される。
        state.requestRotate(page: page)
        try? await Task.sleep(nanoseconds: 200_000_000)
        XCTAssertEqual(fake.lastManualRotation, 1)
        XCTAssertEqual(page.manualRotation, 1)

        // 編集あり → 確認待ちになり、確認で実行される（2回目=90°×2）。
        page.toggleCandidate(page.analyzed.candidates[0].id)
        state.requestRotate(page: page)
        XCTAssertNotNil(state.pendingRotationPageID)
        state.confirmPendingRotation()
        try? await Task.sleep(nanoseconds: 200_000_000)
        XCTAssertEqual(page.manualRotation, 2)
        XCTAssertFalse(page.candidatesEdited, "回転で候補編集は破棄")

        // 種別変更・切り抜きにも回転が引き継がれる。
        await state.performTypeChange(page: page, to: .menkyoshoFront)
        XCTAssertEqual(fake.lastManualRotation, 2)
        await state.applyCrop(page: page, quad: .fullImage)
        XCTAssertEqual(fake.lastManualRotation, 2)
    }

    // MARK: - 切り抜きの手動調整（wp5 §9.5）

    func test_applyCrop_passesQuadAndDiscardsEdits() async {
        let cand = TestFixtures.candidate(ruleID: "x.num", source: .detector(.myNumber12), isOn: true)
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage(candidates: [cand]) }
        let state = makeState(analysis: fake, settings: makeSettings())
        await state.processFiles([URL(fileURLWithPath: "/tmp/a.jpg")])
        let page = state.pages[0]
        page.addRect(NormRect(x: 0.2, y: 0.2, w: 0.1, h: 0.1), undo: nil)
        page.toggleCandidate(page.analyzed.candidates[0].id)
        XCTAssertTrue(page.hasUserEdits)

        let quad = Quad(topLeft: CGPoint(x: 0.1, y: 0.9), topRight: CGPoint(x: 0.9, y: 0.9),
                        bottomRight: CGPoint(x: 0.9, y: 0.1), bottomLeft: CGPoint(x: 0.1, y: 0.1))
        await state.applyCrop(page: page, quad: quad)

        XCTAssertEqual(fake.lastManualQuad, quad, "再解析に手動四隅が渡ること")
        XCTAssertEqual(page.manualQuad, quad)
        XCTAssertFalse(page.candidatesEdited, "候補編集は破棄")
        XCTAssertEqual(page.manualMaskCount, 0, "手動マスクは破棄（座標系が変わるため）")
    }

    func test_typeChange_carriesManualQuad() async {
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage() }
        let state = makeState(analysis: fake, settings: makeSettings())
        await state.processFiles([URL(fileURLWithPath: "/tmp/a.jpg")])
        let page = state.pages[0]
        page.manualQuad = .fullImage

        await state.performTypeChange(page: page, to: .menkyoshoFront)
        XCTAssertEqual(fake.lastManualQuad, .fullImage, "種別変更の再解析に手動切り抜きを引き継ぐこと")
        XCTAssertEqual(page.forcedType, .menkyoshoFront)

        // 切り抜き適用時は選択済み種別を維持する
        await state.applyCrop(page: page, quad: .fullImage)
        XCTAssertEqual(fake.lastForcedType, .menkyoshoFront)
    }

    // MARK: - 選択マスクの矩形編集（移動・リサイズ・削除）

    func test_selectedCandidateBoxEdit_commitsWithUndoRedoAndMarksEdited() async {
        let cand = TestFixtures.candidate(ruleID: "x.num", source: .detector(.myNumber12), isOn: true)
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage(candidates: [cand]) }
        let state = makeState(analysis: fake, settings: makeSettings())
        await state.processFiles([URL(fileURLWithPath: "/tmp/a.jpg")])
        let page = state.pages[0]
        page.selectedCandidateID = page.analyzed.candidates[0].id
        let old = page.selectedBox!
        let moved = NormRect(x: 0.5, y: 0.5, w: old.w, h: old.h)
        let undo = UndoManager()

        page.setSelectedBox(moved)                       // ドラッグ中のライブ更新
        page.commitSelectedBoxEdit(from: old, undo: undo)
        XCTAssertEqual(page.analyzed.candidates[0].box, moved)
        XCTAssertTrue(page.candidatesEdited, "候補矩形の編集は「編集あり」扱い")

        undo.undo()
        XCTAssertEqual(page.analyzed.candidates[0].box, old)
        undo.redo()
        XCTAssertEqual(page.analyzed.candidates[0].box, moved)
    }

    func test_selectedManualRectEdit_andSelectionExclusivity() async {
        let cand = TestFixtures.candidate(ruleID: "x.num", source: .detector(.myNumber12), isOn: true)
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage(candidates: [cand]) }
        let state = makeState(analysis: fake, settings: makeSettings())
        await state.processFiles([URL(fileURLWithPath: "/tmp/a.jpg")])
        let page = state.pages[0]
        let old = NormRect(x: 0.2, y: 0.2, w: 0.1, h: 0.1)
        page.addRect(old, undo: nil)

        // 排他選択: 手動を選ぶと候補選択が外れる（逆も）
        page.selectedCandidateID = page.analyzed.candidates[0].id
        page.selectedManualRectIndex = 0
        XCTAssertNil(page.selectedCandidateID)
        page.selectedCandidateID = page.analyzed.candidates[0].id
        XCTAssertNil(page.selectedManualRectIndex)

        // 手動矩形の移動＋undo
        page.selectedManualRectIndex = 0
        let moved = NormRect(x: 0.6, y: 0.6, w: 0.1, h: 0.1)
        let undo = UndoManager()
        page.setSelectedBox(moved)
        page.commitSelectedBoxEdit(from: old, undo: undo)
        XCTAssertEqual(page.analyzed.manual.rects[0], moved)
        undo.undo()
        XCTAssertEqual(page.analyzed.manual.rects[0], old)
    }

    func test_deleteSelection_candidateTurnsOff_manualRemoves() async {
        let cand = TestFixtures.candidate(ruleID: "x.num", source: .detector(.myNumber12), isOn: true)
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage(candidates: [cand]) }
        let state = makeState(analysis: fake, settings: makeSettings())
        await state.processFiles([URL(fileURLWithPath: "/tmp/a.jpg")])
        let page = state.pages[0]

        // 自動候補: Delete = チェックOFF（リストからは消えない）
        page.selectedCandidateID = page.analyzed.candidates[0].id
        page.deleteSelection(undo: nil)
        XCTAssertEqual(page.analyzed.candidates.count, 1)
        XCTAssertFalse(page.analyzed.candidates[0].isOn)

        // 手動矩形: Delete = 削除
        page.addRect(NormRect(x: 0.2, y: 0.2, w: 0.1, h: 0.1), undo: nil)
        page.selectedManualRectIndex = 0
        page.deleteSelection(undo: nil)
        XCTAssertTrue(page.analyzed.manual.rects.isEmpty)
        XCTAssertNil(page.selectedManualRectIndex)
    }

    func test_manualMask_undo_removesAddedRect() async {
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage() }
        let state = makeState(analysis: fake, settings: makeSettings())
        await state.processFiles([URL(fileURLWithPath: "/tmp/a.jpg")])
        let page = state.pages[0]
        let undo = UndoManager()
        page.addRect(NormRect(x: 0.2, y: 0.2, w: 0.1, h: 0.1), undo: undo)
        XCTAssertEqual(page.analyzed.manual.rects.count, 1)
        undo.undo()
        XCTAssertEqual(page.analyzed.manual.rects.count, 0)
    }
}
