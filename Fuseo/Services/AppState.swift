import Foundation
import Observation
import MaskingCore
import os

/// UI層のロガー（コアの MaskingLog と同じ subsystem）。
enum UILog {
    static let review = Logger(subsystem: "jp.co.ati-mirai.fuseo", category: "ui.review")
}

/// 画面フローの状態（wp5 §1）。単一ウィンドウ・3状態。
enum Stage: Equatable {
    case empty
    case processing(done: Int, total: Int)
    case review
}

/// 確認画面のツール（toolbar セグメント・wp5 §2.1）。
enum ReviewTool: String, CaseIterable {
    case select, rect, brush
}

/// 確認画面のレイアウト（v1.2 一括処理）。grid=一覧レビュー（サムネイル格子）。
enum ReviewLayout: String {
    case single, grid
}

/// 1ページ分の編集状態（wp5 §5）。isOn・手動マスクはここで編集する。
@MainActor
@Observable
final class PageState: Identifiable {
    let id = UUID()
    let sourceURL: URL
    /// 解析結果スナップショット。candidates[].isOn / box / manual を編集する。
    var analyzed: AnalyzedPage
    /// リスト⇔キャンバスのハイライト双方向同期用（wp5 §2.1）。手動矩形の選択と排他。
    var selectedCandidateID: MaskCandidate.ID? {
        didSet { if selectedCandidateID != nil { selectedManualRectIndex = nil } }
    }
    /// キャンバスで選択中の手動矩形の添字（候補選択と排他）。
    var selectedManualRectIndex: Int? {
        didSet { if selectedManualRectIndex != nil { selectedCandidateID = nil } }
    }
    /// 表示ズーム（1..4×・wp5 §2）。フィット倍率として使う（`pixelAccurate` が false のとき有効）。
    var zoom: CGFloat = 1.0
    /// 100%（1:1 ピクセル）表示か。true のとき zoom を無視して原寸表示（スクロールでパン）。
    var pixelAccurate = false
    /// 候補 isOn を1つでも編集したか（種別変更時の確認アラート条件・wp5 §2.1）。
    var candidatesEdited = false
    /// ユーザーが手動指定した切り抜き四隅（wp5 §9.5）。再解析（種別変更）にも必ず引き継ぐ。
    var manualQuad: Quad?
    /// ユーザーが種別を手動選択済みか（切り抜き再解析時に自動判定へ戻さないため）。
    var forcedType: DocumentType?
    /// 手動回転（時計回り90°×n・0..3）。自動正立化が外れた場合の救済。再解析に必ず引き継ぐ。
    var manualRotation: Int = 0

    init(sourceURL: URL, analyzed: AnalyzedPage) {
        self.sourceURL = sourceURL
        self.analyzed = analyzed
    }

    /// ユーザー編集（候補チェック変更・手動マスク）があるか。切り抜き変更の確認アラート条件。
    var hasUserEdits: Bool { candidatesEdited || manualMaskCount > 0 }

    // MARK: - 派生値（書き出しサマリ・ゼロマスク判定）

    /// 適用される自動マスク数（isOn の候補）。
    var autoMaskCount: Int { analyzed.candidates.filter(\.isOn).count }
    /// 手動マスク数（矩形＋ブラシ）。
    var manualMaskCount: Int { analyzed.manual.rects.count + analyzed.manual.strokes.count }
    /// このページに適用されるマスクの総数。
    var totalMaskCount: Int { autoMaskCount + manualMaskCount }
    /// マスクが1つも無いページか（無確認保存を防ぐための赤字警告条件・wp5 §3-1）。
    var hasZeroMask: Bool { totalMaskCount == 0 }

    // MARK: - 候補トグル

    func toggleCandidate(_ id: MaskCandidate.ID) {
        guard let idx = analyzed.candidates.firstIndex(where: { $0.id == id }) else { return }
        analyzed.candidates[idx].isOn.toggle()
        candidatesEdited = true
    }

    // MARK: - 手動マスク（スナップショット方式で Undo 対応・wp5 §2.1）

    /// 手動マスクを変更し、変更前の状態を UndoManager に登録する（add/delete 両方向に対称）。
    func mutateManual(undo: UndoManager?, _ change: (inout ManualMask) -> Void) {
        let before = analyzed.manual
        change(&analyzed.manual)
        undo?.registerUndo(withTarget: self) { target in
            MainActor.assumeIsolated {
                target.mutateManual(undo: undo) { $0 = before }
            }
        }
    }

    func addRect(_ rect: NormRect, undo: UndoManager?) {
        mutateManual(undo: undo) { $0.rects.append(rect) }
    }

    func addStroke(_ stroke: BrushStroke, undo: UndoManager?) {
        mutateManual(undo: undo) { $0.strokes.append(stroke) }
    }

    // MARK: - 選択中マスクの矩形編集（移動・リサイズ。自動候補も編集可・wp5 §2.1）

    /// キャンバスで選択中のマスク矩形（自動候補 or 手動矩形）。
    var selectedBox: NormRect? {
        if let id = selectedCandidateID {
            return analyzed.candidates.first(where: { $0.id == id })?.box
        }
        if let i = selectedManualRectIndex, analyzed.manual.rects.indices.contains(i) {
            return analyzed.manual.rects[i]
        }
        return nil
    }

    /// ドラッグ中のライブ更新（undo登録なし。確定は commitSelectedBoxEdit）。
    func setSelectedBox(_ box: NormRect) {
        if let id = selectedCandidateID,
           let idx = analyzed.candidates.firstIndex(where: { $0.id == id }) {
            analyzed.candidates[idx].box = box
        } else if let i = selectedManualRectIndex, analyzed.manual.rects.indices.contains(i) {
            analyzed.manual.rects[i] = box
        }
    }

    /// ドラッグ確定: 開始時の矩形との差分を Undo に登録（1ドラッグ=1操作）。
    /// 自動候補の矩形編集は「候補を編集した」扱い（種別変更・切り抜き変更の確認条件に含める）。
    func commitSelectedBoxEdit(from old: NormRect, undo: UndoManager?) {
        if let id = selectedCandidateID,
           let idx = analyzed.candidates.firstIndex(where: { $0.id == id }) {
            let final = analyzed.candidates[idx].box
            guard final != old else { return }
            analyzed.candidates[idx].box = old   // 一旦戻してから undo 対称の setter で確定
            setCandidateBox(id, final, undo: undo)
        } else if let i = selectedManualRectIndex, analyzed.manual.rects.indices.contains(i) {
            let final = analyzed.manual.rects[i]
            guard final != old else { return }
            analyzed.manual.rects[i] = old
            mutateManual(undo: undo) { $0.rects[i] = final }
        }
    }

    /// 候補矩形の設定（undo/redo 対称・mutateManual と同じ型）。
    private func setCandidateBox(_ id: MaskCandidate.ID, _ box: NormRect, undo: UndoManager?) {
        guard let idx = analyzed.candidates.firstIndex(where: { $0.id == id }),
              analyzed.candidates[idx].box != box else { return }
        let old = analyzed.candidates[idx].box
        analyzed.candidates[idx].box = box
        candidatesEdited = true
        undo?.registerUndo(withTarget: self) { target in
            MainActor.assumeIsolated {
                target.setCandidateBox(id, old, undo: undo)
            }
        }
    }

    /// Deleteキー: 手動矩形は削除、自動候補はチェックOFF（リストからは消さない）。
    func deleteSelection(undo: UndoManager?) {
        if let id = selectedCandidateID,
           let idx = analyzed.candidates.firstIndex(where: { $0.id == id }) {
            if analyzed.candidates[idx].isOn {
                analyzed.candidates[idx].isOn = false
                candidatesEdited = true
            }
        } else if let i = selectedManualRectIndex, analyzed.manual.rects.indices.contains(i) {
            mutateManual(undo: undo) { $0.rects.remove(at: i) }
            selectedManualRectIndex = nil
        }
    }

    /// 手動リストの行（矩形→ブラシの順）を表す。
    enum ManualItem: Equatable {
        case rect(Int)      // analyzed.manual.rects の添字
        case stroke(Int)    // analyzed.manual.strokes の添字
    }

    /// インスペクタ手動リストの表示順（矩形が先、ブラシが後）。
    var manualItems: [ManualItem] {
        analyzed.manual.rects.indices.map(ManualItem.rect)
            + analyzed.manual.strokes.indices.map(ManualItem.stroke)
    }

    func deleteManualItem(at listIndex: Int, undo: UndoManager?) {
        let items = manualItems
        guard items.indices.contains(listIndex) else { return }
        switch items[listIndex] {
        case .rect(let i):
            mutateManual(undo: undo) { if $0.rects.indices.contains(i) { $0.rects.remove(at: i) } }
        case .stroke(let i):
            mutateManual(undo: undo) { if $0.strokes.indices.contains(i) { $0.strokes.remove(at: i) } }
        }
    }
}

/// アプリ全体の状態と操作（wp5 §5）。@Observable・@MainActor。
/// 解析は `Analyzing` を注入（テストでフェイクに差し替え）。
@MainActor
@Observable
final class AppState {
    var stage: Stage = .empty
    var pages: [PageState] = []
    var currentPageIndex = 0

    // 確認画面の道具立て（toolbar は全ページ共通）
    var tool: ReviewTool = .select
    /// 一覧（グリッド）⇄ 個別の切替（v1.2）。5枚以上の取込では一覧から始める。
    var reviewLayout: ReviewLayout = .single
    /// ブラシ幅（正規化 0.01..0.10・既定 0.03＝画像短辺比・wp5 §2.1）。
    var brushWidth: Double = 0.03
    /// 仕上がりプレビュー（ON = 完全不透明の黒で描画・編集は選択ツールのみ）。
    var previewMode = false

    // 書き出しシート
    var showingExportSheet = false
    var exportOptions: ExportOptions

    // アラート
    var errorMessage: String?
    var showingError = false
    // 書き出し完了（トースト＋Finderで表示・wp5 §3-4）
    var lastExportedURL: URL?
    var showingExportDone = false
    /// 種別変更の確認待ち（isOn 編集済みで警告する場合）。
    var pendingTypeChange: (pageID: PageState.ID, type: DocumentType)?

    let analysis: Analyzing
    let settings: SettingsStore
    let exportService: ExportService

    init(analysis: Analyzing, settings: SettingsStore, exportService: ExportService = ExportService()) {
        self.analysis = analysis
        self.settings = settings
        self.exportService = exportService
        self.exportOptions = settings.initialExportOptions
    }

    var currentPage: PageState? {
        pages.indices.contains(currentPageIndex) ? pages[currentPageIndex] : nil
    }

    // MARK: - 取り込み・解析（wp5 §1 Processing）

    /// 複数ファイルを直列に解析して Review へ進む。1枚でも成功すれば Review、全滅なら Empty へ戻す。
    func processFiles(_ urls: [URL]) async {
        guard !urls.isEmpty else { return }
        stage = .processing(done: 0, total: urls.count)
        var built: [PageState] = []
        var failures = 0
        for (index, url) in urls.enumerated() {
            do {
                var analyzed = try await analysis.analyze(url: url, forcedType: nil, manualQuad: nil, manualRotation: 0)
                applyFaceDefault(to: &analyzed)
                built.append(PageState(sourceURL: url, analyzed: analyzed))
            } catch {
                failures += 1
            }
            stage = .processing(done: index + 1, total: urls.count)
        }

        if built.isEmpty {
            stage = .empty
            presentError("読み込めませんでした。対応する画像ファイル（JPEG / PNG / HEIC / TIFF）を選んでください。")
        } else {
            pages = built
            currentPageIndex = 0
            exportOptions = settings.initialExportOptions
            reviewLayout = built.count >= 5 ? .grid : .single   // 束は一覧から（v1.2）
            stage = .review
            if failures > 0 {
                presentError("\(failures)枚は読み込めませんでした。読み込めた\(built.count)枚を表示しています。")
            }
        }
    }

    /// Review 中に書類を追加する（+Add）。解析して既存ページ末尾へ追加。全滅ならエラーのみ。
    func appendFiles(_ urls: [URL]) async {
        let accepted = urls
        guard !accepted.isEmpty else { return }
        var added = 0
        for url in accepted {
            do {
                var analyzed = try await analysis.analyze(url: url, forcedType: nil, manualQuad: nil, manualRotation: 0)
                applyFaceDefault(to: &analyzed)
                pages.append(PageState(sourceURL: url, analyzed: analyzed))
                added += 1
            } catch {
                // 個別失敗はスキップ
            }
        }
        if added == 0 { presentError("追加した書類を読み込めませんでした。") }
    }

    /// 設定 `faceMaskDefaultOn` が ON なら、顔検出候補の isOn を true に上書きする（wp5 §4）。
    func applyFaceDefault(to analyzed: inout AnalyzedPage) {
        guard settings.faceMaskDefaultOn else { return }
        for i in analyzed.candidates.indices where analyzed.candidates[i].source == .detector(.face) {
            analyzed.candidates[i].isOn = true
        }
    }

    // MARK: - 種別変更（wp5 §2.1）

    /// 種別ピッカーの選択変更。isOn 編集済みなら確認、そうでなければ即再解析。
    func requestTypeChange(page: PageState, to type: DocumentType) {
        if page.candidatesEdited {
            pendingTypeChange = (page.id, type)
        } else {
            Task { await performTypeChange(page: page, to: type) }
        }
    }

    func confirmPendingTypeChange() {
        guard let pending = pendingTypeChange,
              let page = pages.first(where: { $0.id == pending.pageID }) else { return }
        let type = pending.type
        pendingTypeChange = nil
        Task { await performTypeChange(page: page, to: type) }
    }

    func cancelPendingTypeChange() { pendingTypeChange = nil }

    /// 種別を変えて再解析する。候補の isOn 編集は破棄・**手動マスクは保持**（wp5 §2.1）。
    /// 手動切り抜き（manualQuad）は必ず引き継ぐ（切り抜きが勝手に戻る事故の防止・wp5 §9.5）。
    func performTypeChange(page: PageState, to type: DocumentType) async {
        do {
            let keepManual = page.analyzed.manual
            var re = try await analysis.analyze(url: page.sourceURL, forcedType: type,
                                                manualQuad: page.manualQuad,
                                                manualRotation: page.manualRotation)
            applyFaceDefault(to: &re)
            re.manual = keepManual
            page.analyzed = re
            page.forcedType = type
            page.candidatesEdited = false
            page.selectedCandidateID = nil
        } catch {
            presentError("種別を変更した再解析に失敗しました。")
        }
    }

    // MARK: - 切り抜きの手動調整（wp5 §9.5）

    /// 切り抜き調整シートの表示フラグ（書き出しシートと同じ isPresented 方式）。
    var showingCropSheet = false
    /// シートの対象ページ。
    private(set) var cropSheetPage: PageState?

    func requestCropAdjust(page: PageState) {
        UILog.review.info("切り抜き調整シートを要求")
        cropSheetPage = page
        showingCropSheet = true
    }

    /// シート初期表示用: 元画像＋自動検出の四隅。
    func loadCropPreview(for page: PageState) async throws -> VisionRectifier.CropPreview? {
        try await analysis.cropPreview(url: page.sourceURL)
    }

    /// 四隅を確定して再解析する。基準画像の座標系が変わるため**手動マスク・候補編集は破棄**。
    /// 種別はユーザーが選択済みならそれを維持し、未選択なら自動判定に任せる。
    func applyCrop(page: PageState, quad: Quad) async {
        do {
            var re = try await analysis.analyze(url: page.sourceURL, forcedType: page.forcedType,
                                                manualQuad: quad,
                                                manualRotation: page.manualRotation)
            applyFaceDefault(to: &re)
            page.analyzed = re
            page.manualQuad = quad
            page.candidatesEdited = false
            page.selectedCandidateID = nil
        } catch {
            presentError("切り抜きを変更した再解析に失敗しました。")
        }
    }

    // MARK: - 手動回転（90°時計回り・自動正立化の救済）

    /// 回転の確認待ち（編集がある場合のみ確認を挟む）。
    var pendingRotationPageID: PageState.ID?

    /// 回転ボタン: 編集済みなら確認、そうでなければ即実行。
    func requestRotate(page: PageState) {
        if page.hasUserEdits {
            pendingRotationPageID = page.id
        } else {
            Task { await performRotate(page: page) }
        }
    }

    func confirmPendingRotation() {
        guard let id = pendingRotationPageID,
              let page = pages.first(where: { $0.id == id }) else { return }
        pendingRotationPageID = nil
        Task { await performRotate(page: page) }
    }

    func cancelPendingRotation() { pendingRotationPageID = nil }

    /// 時計回りに90°回して再解析する。座標系が変わるため候補編集・手動マスクは破棄。
    func performRotate(page: PageState) async {
        do {
            let next = (page.manualRotation + 1) % 4
            var re = try await analysis.analyze(url: page.sourceURL, forcedType: page.forcedType,
                                                manualQuad: page.manualQuad,
                                                manualRotation: next)
            applyFaceDefault(to: &re)
            page.analyzed = re
            page.manualRotation = next
            page.candidatesEdited = false
            page.selectedCandidateID = nil
            page.selectedManualRectIndex = nil
        } catch {
            presentError("回転後の再解析に失敗しました。")
        }
    }

    // MARK: - 書き出し（wp5 §3）

    /// 全ページの書き出しサマリ（自動/手動/ページ数）。
    var exportSummary: (total: Int, auto: Int, manual: Int, pageCount: Int) {
        let auto = pages.reduce(0) { $0 + $1.autoMaskCount }
        let manual = pages.reduce(0) { $0 + $1.manualMaskCount }
        return (auto + manual, auto, manual, pages.count)
    }

    /// マスクが1つも無いページが存在するか（赤字警告・ボタンラベル切替の条件・wp5 §3-1）。
    var hasZeroMaskPage: Bool { pages.contains(where: \.hasZeroMask) }

    /// 該当ページに warnings を持つプリセットがあるか（シート再掲用）。
    var allWarnings: [String] {
        var seen = Set<String>()
        var result: [String] = []
        for page in pages {
            for w in page.analyzed.preset.warnings where !seen.contains(w) {
                seen.insert(w); result.append(w)
            }
        }
        return result
    }

    /// 既定保存名（先頭ファイル名 -masked.<ext>）。
    var defaultExportFileName: String {
        let first = pages.first?.sourceURL.lastPathComponent ?? "document"
        return ExportNaming.defaultFileName(firstSourceName: first, format: exportOptions.format)
    }

    /// 実書き出し（burnIn → export）。UI から NSSavePanel の URL を受けて呼ぶ。
    func export(to url: URL) throws {
        try exportService.export(pages: pages.map(\.analyzed), options: exportOptions, to: url)
    }

    /// 一括書き出し（v1.2）: 1書類=1ファイルで指定フォルダへ。`<元名>-masked.<ext>`（重複は -2, -3…）。
    /// 確認画面（一覧/個別）を経てから呼ばれる。書き出したURL群を返す。
    @discardableResult
    func exportSeparately(to directory: URL) throws -> [URL] {
        var written: [URL] = []
        for page in pages {
            let name = ExportNaming.defaultFileName(
                firstSourceName: page.sourceURL.lastPathComponent, format: exportOptions.format)
            let url = ExportNaming.uniqueURL(in: directory, fileName: name)
            try exportService.export(pages: [page.analyzed], options: exportOptions, to: url)
            written.append(url)
        }
        return written
    }

    // MARK: - セッション

    func reset() {
        pages = []
        currentPageIndex = 0
        tool = .select
        reviewLayout = .single
        previewMode = false
        showingExportSheet = false
        pendingTypeChange = nil
        showingCropSheet = false
        cropSheetPage = nil
        stage = .empty
    }

    private func presentError(_ message: String) {
        errorMessage = message
        showingError = true
    }
}
