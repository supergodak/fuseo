import Foundation
import Observation
import MaskingCore
import os

/// UI層のロガー（コアの MaskingLog と同じ subsystem）。
enum UILog {
    static let review = Logger(subsystem: "jp.co.ati-mirai.fuseo", category: "ui.review")
    /// 取り込み（PDFのページ展開など）。**パスワードは絶対に記録しない**。ファイル名は `.private`。
    static let intake = Logger(subsystem: "jp.co.ati-mirai.fuseo", category: "ui.intake")
    /// 作業ライブラリの自動保存（WP-13）。**理由だけ**を出す。タイトル・ファイル名は `.private`。
    static let library = Logger(subsystem: "jp.co.ati-mirai.fuseo", category: "ui.library")
}

/// 画面フローの状態（wp5 §1）。単一ウィンドウ・3状態＋取り込み中（WP-10）。
enum Stage: Equatable {
    case empty
    /// 取り込み中（PDFのページ画像化など）。件数を数えられないので**不確定表示**にする。
    /// ラスタライズ中に無反応に見えるのを防ぐため、取り込み開始と同時にこの状態へ移す。
    case importing
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
    var manualRotation: Int = 0 { didSet { if manualRotation != oldValue { noteEdited() } } }
    /// PDF 由来のページか（WP-10 §2.3）。書類検出は `analysisOptions.documentDetection = .insetOnly`
    /// （ページの中に小さく写った書類だけ切り抜く。文字だけのページはそのまま）。バナー表示などの判定に使う。
    let isFlatSource: Bool
    /// このページの解析オプション（WP-10b）。初回解析で決まり、**種別変更・回転などの再解析でも
    /// 同じ値を渡す**（`reanalysisQuad` と同じ扱い。文字認識の有無が再解析で勝手に変わらないように）。
    let analysisOptions: AnalysisOptions
    /// ライブラリ（WP-13）から復元したページか。復元元は**解析後の基準画像**なので、
    /// 台形補正・正立化・切り抜きは適用済み（`analysisOptions.documentDetection = .off`）。
    let isRestored: Bool
    /// 基準画像に**すでに適用済み**の手動回転（復元ページ用）。`manualRotation` は
    /// 「ユーザーが今までに回した合計」を保つため、再解析へ渡す量はこの差分になる。
    let rotationBaseline: Int
    /// 編集が起きたことの通知（WP-13 の自動保存トリガ）。`AppState` が差し込む。
    var onEdit: (() -> Void)?

    /// 文字認識済みのページか（OCR なしページの注意文の表示条件）。
    var textRecognized: Bool { analysisOptions.recognizeText }

    init(sourceURL: URL, analyzed: AnalyzedPage, isFlatSource: Bool = false,
         analysisOptions: AnalysisOptions = .default,
         isRestored: Bool = false, rotationBaseline: Int = 0) {
        self.sourceURL = sourceURL
        self.analyzed = analyzed
        self.isFlatSource = isFlatSource
        self.analysisOptions = analysisOptions
        self.isRestored = isRestored
        self.rotationBaseline = rotationBaseline
    }

    /// 再解析（種別変更・回転・追加解析）に渡す切り抜き四隅。
    /// **ユーザーが「切り抜きを調整」で指定した quad が最優先**。指定が無ければ PDF 由来・復元済みは
    /// 全面固定、それ以外は nil（=自動の書類検出に任せる）。
    var reanalysisQuad: Quad? {
        // 手動切り抜きが最優先。それ以外は analysisOptions.documentDetection に任せる
        // （PDF 由来=insetOnly: ページ内の小さな書類だけ切り抜く／復元=off: 基準画像をそのまま）。
        manualQuad
    }

    /// 再解析へ渡す回転量（基準画像に対する差分・0..3）。
    /// 新規取り込みのページは `rotationBaseline == 0` なので `manualRotation` と一致する。
    var analysisRotation: Int { ((manualRotation - rotationBaseline) % 4 + 4) % 4 }

    /// 編集通知（自動保存のトリガ）。
    func noteEdited() { onEdit?() }

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
        noteEdited()
    }

    // MARK: - 手動マスク（スナップショット方式で Undo 対応・wp5 §2.1）

    /// 手動マスクを変更し、変更前の状態を UndoManager に登録する（add/delete 両方向に対称）。
    func mutateManual(undo: UndoManager?, _ change: (inout ManualMask) -> Void) {
        let before = analyzed.manual
        change(&analyzed.manual)
        noteEdited()
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
        noteEdited()
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
        noteEdited()
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
                noteEdited()
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
    /// 作業ライブラリ（WP-13）。nil = 保存しない（既存の層1テスト・保存先を作れない環境）。
    let library: WorkLibrary?
    /// プリセット全件。種別ピッカーの選択肢と、ライブラリ復元のプリセット引きに使う。
    let allPresets: [DocumentPreset]

    init(analysis: Analyzing, settings: SettingsStore, exportService: ExportService = ExportService(),
         library: WorkLibrary? = nil) {
        self.analysis = analysis
        self.settings = settings
        self.exportService = exportService
        self.library = library
        self.allPresets = (try? PresetStore.loadAll()) ?? []
        self.exportOptions = settings.initialExportOptions
    }

    var currentPage: PageState? {
        pages.indices.contains(currentPageIndex) ? pages[currentPageIndex] : nil
    }

    // MARK: - 取り込み・解析（wp5 §1 Processing）

    /// 画像ファイル群を取り込む（PDF を経由しない従来経路）。
    func processFiles(_ urls: [URL]) async {
        await processFiles(urls.map { IntakeFile(url: $0) })
    }

    /// 複数ファイルを直列に解析して Review へ進む。1枚でも成功すれば Review、全滅なら Empty へ戻す。
    /// PDF 由来のページ（`isFlatPage`）は書類検出をバイパスして全面固定で解析する（WP-10 §2.3）。
    func processFiles(_ files: [IntakeFile]) async {
        guard !files.isEmpty else { return }
        stage = .processing(done: 0, total: files.count)
        var built: [PageState] = []
        var failures = 0
        for (index, file) in files.enumerated() {
            do {
                var analyzed = try await analysis.analyze(
                    url: file.url, forcedType: nil,
                    manualQuad: nil, manualRotation: 0,
                    options: file.analysisOptions)
                applyFaceDefault(to: &analyzed)
                built.append(PageState(sourceURL: file.url, analyzed: analyzed,
                                       isFlatSource: file.isFlatPage,
                                       analysisOptions: file.analysisOptions))
            } catch {
                failures += 1
            }
            stage = .processing(done: index + 1, total: files.count)
        }

        if built.isEmpty {
            stage = .empty
            presentError(String(localized: "読み込めませんでした。対応するファイル（JPEG / PNG / HEIC / TIFF / PDF）を選んでください。"))
        } else {
            // 取り込みは常に「新しい作業」。前の作業はライブラリに残ったままになる（WP-13）。
            clearWorkIdentity()
            pages = built
            attachEditObservers()
            dirtyImagePageIDs = Set(built.map(\.id))
            currentPageIndex = 0
            exportOptions = settings.initialExportOptions
            reviewLayout = built.count >= 5 ? .grid : .single   // 束は一覧から（v1.2）
            stage = .review
            scheduleAutosave()
            if failures > 0 {
                presentError(String(localized: "\(failures)枚は読み込めませんでした。読み込めた\(built.count)枚を表示しています。"))
            }
        }
    }

    /// Review 中に書類を追加する（+Add）。画像のみの従来経路。
    func appendFiles(_ urls: [URL]) async {
        await appendFiles(urls.map { IntakeFile(url: $0) })
    }

    /// Review 中に書類を追加する（+Add）。解析して既存ページ末尾へ追加。全滅ならエラーのみ。
    func appendFiles(_ files: [IntakeFile]) async {
        guard !files.isEmpty else { return }
        await withReanalysis {
        var added = 0
        for file in files {
            do {
                var analyzed = try await analysis.analyze(
                    url: file.url, forcedType: nil,
                    manualQuad: nil, manualRotation: 0,
                    options: file.analysisOptions)
                applyFaceDefault(to: &analyzed)
                let page = PageState(sourceURL: file.url, analyzed: analyzed,
                                     isFlatSource: file.isFlatPage,
                                     analysisOptions: file.analysisOptions)
                page.onEdit = { [weak self] in self?.noteEdited() }
                pages.append(page)
                dirtyImagePageIDs.insert(page.id)
                added += 1
            } catch {
                // 個別失敗はスキップ
            }
        }
        if added == 0 { presentError(String(localized: "追加した書類を読み込めませんでした。")) }
        else { noteEdited() }
        }
    }

    // MARK: - PDF 取り込み（WP-10 §2.4）

    /// PDF パスワード入力の要求（`sheet(item:)` 用）。入力値はメモリ内のみで扱い、保存もログもしない。
    struct PDFPasswordRequest: Identifiable, Equatable {
        let id = UUID()
        let fileName: String
        /// 直前の入力が誤りだった（再入力の文言を出す）。
        let retry: Bool
    }

    /// 表示中のパスワード入力要求（nil = 出さない）。
    var pdfPasswordRequest: PDFPasswordRequest?
    private var pdfPasswordContinuation: CheckedContinuation<String?, Never>?

    /// 文字認識をするかの選択要求（`sheet(item:)` 用・WP-10b）。
    struct PDFTextChoiceRequest: Identifiable, Equatable {
        let id = UUID()
        /// 取り込み全体の PDF 総ページ数。
        let totalPages: Int
        /// 文字認識の見積秒数（表示は `PDFIntake.durationText(seconds:)`）。
        let estimatedSeconds: Int
    }

    /// 表示中の文字認識選択要求（nil = 出さない）。
    var pdfTextChoiceRequest: PDFTextChoiceRequest?
    private var pdfTextChoiceContinuation: CheckedContinuation<PDFIntake.TextChoice?, Never>?

    /// 「PDFは画像として処理する」注意文を閉じたか。PDF を取り込むたびに再表示する。
    var pdfNoticeDismissed = false
    /// 「このPDFは文字認識していません」注意文を閉じたか。取り込みのたびに再表示する。
    var noTextNoticeDismissed = false
    /// PDF 由来のページを含むか（注意文の表示条件）。
    var hasPDFPages: Bool { pages.contains(where: \.isFlatSource) }
    /// 文字認識していないページを含むか（OCRなし注意文・書き出しシートの1行の表示条件）。
    var hasPagesWithoutText: Bool { pages.contains { !$0.textRecognized } }

    /// 文字認識の選択シートを出して選択を待つ。`nil` = キャンセル（取り込み中止・エラー表示なし）。
    func requestPDFTextChoice(totalPages: Int, estimatedSeconds: Int) async -> PDFIntake.TextChoice? {
        await withCheckedContinuation { continuation in
            pdfTextChoiceContinuation = continuation
            pdfTextChoiceRequest = PDFTextChoiceRequest(totalPages: totalPages, estimatedSeconds: estimatedSeconds)
        }
    }

    func submitPDFTextChoice(_ choice: PDFIntake.TextChoice) { finishPDFTextChoice(choice) }
    func cancelPDFTextChoice() { finishPDFTextChoice(nil) }

    private func finishPDFTextChoice(_ value: PDFIntake.TextChoice?) {
        pdfTextChoiceRequest = nil
        let continuation = pdfTextChoiceContinuation
        pdfTextChoiceContinuation = nil
        continuation?.resume(returning: value)
    }

    /// パスワード入力シートを出して入力を待つ。`nil` = キャンセル。
    func requestPDFPassword(fileName: String, retry: Bool) async -> String? {
        await withCheckedContinuation { continuation in
            pdfPasswordContinuation = continuation
            pdfPasswordRequest = PDFPasswordRequest(fileName: fileName, retry: retry)
        }
    }

    func submitPDFPassword(_ password: String) { finishPDFPassword(password) }
    func cancelPDFPassword() { finishPDFPassword(nil) }

    private func finishPDFPassword(_ value: String?) {
        pdfPasswordRequest = nil
        let continuation = pdfPasswordContinuation
        pdfPasswordContinuation = nil
        continuation?.resume(returning: value)
    }

    // MARK: - ページ画像の一時ディレクトリ（Mac 経路の寿命管理）

    /// 取り込み済みページ画像の一時ディレクトリ（Mac）。**セッション中は消さない**
    /// （再解析で `sourceURL` を読み直すため）。取り込みの置き換え・「新しい書類」・アプリ終了で削除する。
    private(set) var pageImageDirectories: [URL] = []
    /// 今回の取り込みで作ったが、まだ確定していないディレクトリ。
    private var stagedPageDirectories: [URL] = []

    /// Mac 用のページ画像 writer を作る。作った一時ディレクトリは AppState が寿命管理する
    /// （本人確認書類の像を temp に残さないための唯一の入口。View から直接 `PDFIntake` を呼ばない）。
    func makeTempPageWriter() -> PDFIntake.PageWriter {
        let store = PDFIntake.makeTempPageStore()
        stagedPageDirectories.append(store.directory)
        return store.writer
    }

    /// 取り込みが成立したときに寿命を更新する。`replacing` はページ集合の置き換え（=追加取込でない）。
    private func commitPageDirectories(replacing: Bool) {
        let staged = stagedPageDirectories
        stagedPageDirectories = []
        if replacing {
            removeDirectories(pageImageDirectories)
            pageImageDirectories = []
        }
        pageImageDirectories += staged
    }

    /// 取り込みが成立しなかったとき（キャンセル・失敗）は、今回作った分だけ即削除する。
    private func discardStagedPageDirectories() {
        removeDirectories(stagedPageDirectories)
        stagedPageDirectories = []
    }

    /// ページ画像の一時ディレクトリを全部削除する（「新しい書類」・アプリ終了時）。
    func purgePageImageDirectories() {
        removeDirectories(pageImageDirectories + stagedPageDirectories)
        pageImageDirectories = []
        stagedPageDirectories = []
    }

    private func removeDirectories(_ urls: [URL]) {
        for url in urls { try? FileManager.default.removeItem(at: url) }
    }

    /// 取り込みの唯一の入口。PDF はページ画像へ展開してから解析へ渡す。
    ///
    /// - Parameters:
    ///   - writer: ページ画像の一時ファイル化（iOS=`SessionFiles.importCGImage` / Mac=`makeTempPageWriter()`）。
    ///   - append: true なら Review 中の追加取込。
    func importFiles(_ urls: [URL],
                     writer: @escaping PDFIntake.PageWriter,
                     limit: Int? = PDFIntake.combinedLimit,
                     rasterizer: PDFRasterizer = PDFRasterizer(),
                     append: Bool = false) async {
        guard !urls.isEmpty else { return }

        // ラスタライズは時間がかかるので、取り込み開始と同時に不確定の処理中表示へ移す
        // （ページ進捗が出るまでの無反応区間をなくす）。
        let stageBeforeImport = stage
        if !append { stage = .importing }

        let outcome = await PDFIntake.run(
            urls: urls, limit: limit, rasterizer: rasterizer,
            passwordProvider: { [weak self] fileName, retry in
                guard let self else { return nil }
                return await self.requestPDFPassword(fileName: fileName, retry: retry)
            },
            textChoiceProvider: { [weak self] totalPages, seconds in
                guard let self else { return nil }
                return await self.requestPDFTextChoice(totalPages: totalPages, estimatedSeconds: seconds)
            },
            writer: writer)

        switch outcome {
        case .cancelled:
            // パスワード入力／文字認識の選択のキャンセル（ユーザー操作なのでエラーは出さない）。
            UILog.intake.info("ユーザーのキャンセルにより取り込みを中止")
            discardStagedPageDirectories()
            if !append { stage = stageBeforeImport }
        case .failed(let message):
            discardStagedPageDirectories()
            if !append { stage = stageBeforeImport }
            presentError(message)
        case .files(let files):
            guard !files.isEmpty else {
                discardStagedPageDirectories()
                if !append { stage = stageBeforeImport }
                return
            }
            if files.contains(where: \.isFlatPage) { pdfNoticeDismissed = false }
            if files.contains(where: { !$0.recognizeText }) { noTextNoticeDismissed = false }
            if append {
                await appendFiles(files)
            } else {
                await processFiles(files)
            }
            // 解析の成否によらず、生成済みページ画像はページの sourceURL として参照されている。
            commitPageDirectories(replacing: !append)
        }
    }

    /// 設定 `faceMaskDefaultOn` が ON なら、顔検出候補の isOn を true に上書きする（wp5 §4）。
    func applyFaceDefault(to analyzed: inout AnalyzedPage) {
        guard settings.faceMaskDefaultOn else { return }
        for i in analyzed.candidates.indices where analyzed.candidates[i].source == .detector(.face) {
            analyzed.candidates[i].isOn = true
        }
    }

    // MARK: - 再解析の可視化と連打ガード

    /// 再解析（回転・種別変更・切り抜き適用・追加取込）の実行中フラグ。
    /// UIはこれを見て「解析し直しています…」のオーバーレイを出し、該当ボタンを無効化する。
    var reanalyzing = false

    /// 再解析を1つずつ実行する（実行中の再入は黙って無視＝連打ガード）。
    private func withReanalysis(_ body: () async -> Void) async {
        guard !reanalyzing else { return }
        reanalyzing = true
        defer { reanalyzing = false }
        await body()
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
    /// PDF 由来ページは手動指定が無ければ全面固定を維持する（WP-10 §2.3・`reanalysisQuad`）。
    func performTypeChange(page: PageState, to type: DocumentType) async {
        await withReanalysis {
        do {
            let keepManual = page.analyzed.manual
            var re = try await analysis.analyze(url: page.sourceURL, forcedType: type,
                                                manualQuad: page.reanalysisQuad,
                                                manualRotation: page.analysisRotation,
                                                options: page.analysisOptions)
            applyFaceDefault(to: &re)
            re.manual = keepManual
            page.analyzed = re
            page.forcedType = type
            page.candidatesEdited = false
            page.selectedCandidateID = nil
            noteBaseImageChanged(page)
        } catch {
            presentError(String(localized: "種別を変更した再解析に失敗しました。"))
        }
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
        await withReanalysis {
        do {
            var re = try await analysis.analyze(url: page.sourceURL, forcedType: page.forcedType,
                                                manualQuad: quad,
                                                manualRotation: page.analysisRotation,
                                                options: page.analysisOptions)
            applyFaceDefault(to: &re)
            page.analyzed = re
            page.manualQuad = quad
            page.candidatesEdited = false
            page.selectedCandidateID = nil
            noteBaseImageChanged(page)
        } catch {
            presentError(String(localized: "切り抜きを変更した再解析に失敗しました。"))
        }
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
        await withReanalysis {
        do {
            let next = (page.manualRotation + 1) % 4
            var re = try await analysis.analyze(
                url: page.sourceURL, forcedType: page.forcedType,
                manualQuad: page.reanalysisQuad,
                // 基準画像に対する差分（復元ページは baseline 分がすでに適用済み）。
                manualRotation: ((next - page.rotationBaseline) % 4 + 4) % 4,
                options: page.analysisOptions)
            applyFaceDefault(to: &re)
            page.analyzed = re
            page.manualRotation = next
            page.candidatesEdited = false
            page.selectedCandidateID = nil
            page.selectedManualRectIndex = nil
            noteBaseImageChanged(page)
        } catch {
            presentError(String(localized: "回転後の再解析に失敗しました。"))
        }
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

    /// 「新しい書類」（WP-13）。**確認は挟まない**。デバウンス中の保存を確定してから空の状態へ戻す。
    /// 作業はライブラリに残るので、あとから一覧で開き直せる。
    func startNewDocument() {
        flushAutosave()
        clearWorkIdentity()
        reset()
        refreshLibrary()
    }

    func reset() {
        // 本人確認書類の像を temp に残さない（PDFページ画像の一時ディレクトリを消す）。
        purgePageImageDirectories()
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

    // MARK: - 作業ライブラリ（WP-13・docs/wp13-library-design.md §2）

    /// 一覧（更新日時の降順）。空なら一覧 UI は出さない。
    private(set) var libraryItems: [WorkDocumentSummary] = []
    /// 保存に失敗したときの文言（確認画面のバナー。黙って失敗しないための唯一の出口）。
    var librarySaveError: String?
    /// いま編集している作業の id（nil = まだ保存対象になっていない）。
    private(set) var currentWorkID: UUID?
    /// 現在の作業の状態（書き出し済みバッジの根拠）。
    private(set) var workStatus: WorkDocument.Status = .inProgress
    /// 直近に書き出したファイル名。
    private(set) var lastExportedName: String?
    /// 自動保存のデバウンス幅（テストで短くする）。
    var autosaveDebounce: Duration = .seconds(1)

    private var workCreatedAt: Date?
    /// 最後の保存以降に変更があったか（フラッシュを空振りさせないための番人）。
    private var hasUnsavedChanges = false
    /// 次の保存でページ画像を書き込む必要があるページ（新規取り込み・再解析で基準画像が変わった分）。
    private var dirtyImagePageIDs: Set<PageState.ID> = []
    private var autosaveTask: Task<Void, Never>?

    /// 種別ピッカーの選択肢（`classification.ranking` に依存しない）。
    /// 自動判定のスコアがある種別だけスコアを添える。ライブラリから復元したページは ranking が空なので、
    /// プリセット全件から一覧を作る（契約 B）。
    struct TypeOption: Identifiable, Equatable {
        let type: DocumentType
        /// 自動判定のスコア（無ければ nil = 表示しない）。
        let score: Int?
        var id: DocumentType { type }
    }

    func typeOptions(for page: PageState) -> [TypeOption] {
        var seen = Set<DocumentType>()
        var out: [TypeOption] = []
        for entry in page.analyzed.classification.ranking where seen.insert(entry.type).inserted {
            out.append(TypeOption(type: entry.type, score: entry.score))
        }
        for preset in allPresets where seen.insert(preset.documentType).inserted {
            out.append(TypeOption(type: preset.documentType, score: nil))
        }
        // いま適用されている種別は必ず選択肢に含める（プリセットが読めない環境でも空にしない）。
        let current = page.analyzed.preset.documentType
        if seen.insert(current).inserted {
            out.insert(TypeOption(type: current, score: nil), at: 0)
        }
        return out
    }

    /// ラベル（表示名＋あればスコア）。
    func typeOptionLabel(_ option: TypeOption) -> String {
        let name = analysis.displayName(for: option.type)
        guard let score = option.score else { return name }
        return String(localized: "\(name)（\(score)）")
    }

    // MARK: 一覧・削除

    func refreshLibrary() {
        guard let library else { libraryItems = []; return }
        do { libraryItems = try library.list() }
        catch {
            libraryItems = []
            UILog.library.error("一覧を読めません: \(String(describing: error), privacy: .public)")
        }
    }

    func deleteWork(id: UUID) {
        guard let library else { return }
        do { try library.delete(id: id) }
        catch { UILog.library.error("削除できません: \(String(describing: error), privacy: .public)") }
        if currentWorkID == id { clearWorkIdentity() }
        refreshLibrary()
    }

    func deleteAllWorks() {
        guard let library else { return }
        do { try library.deleteAll() }
        catch { UILog.library.error("全削除に失敗: \(String(describing: error), privacy: .public)") }
        clearWorkIdentity()
        refreshLibrary()
    }

    /// 作業ディレクトリ（Mac の「Finderで表示」用）。
    func workDirectory(for id: UUID) -> URL? {
        (try? library?.load(id: id))?.directory
    }

    // MARK: 開く

    /// 保存済みの作業を開く。**Vision は走らせない**（基準画像とマスク状態をそのまま復元する）。
    /// 開いた作業が以後の自動保存の対象（同じ id に上書き）になる。
    func openWork(id: UUID) {
        guard let library else { return }
        flushAutosave()
        do {
            let (doc, dir) = try library.load(id: id)
            let restored = try WorkDocumentBridge.restore(document: doc, directory: dir,
                                                          presets: allPresets)
            purgePageImageDirectories()   // 前の取り込みの一時領域は不要
            pages = restored
            attachEditObservers()
            currentWorkID = doc.id
            workCreatedAt = doc.createdAt
            workStatus = doc.status
            lastExportedName = doc.lastExportedName
            dirtyImagePageIDs = []        // 画像は保存済み。触るまで書き直さない
            currentPageIndex = 0
            tool = .select
            previewMode = false
            exportOptions = settings.initialExportOptions
            reviewLayout = restored.count >= 5 ? .grid : .single
            librarySaveError = nil
            stage = .review
        } catch {
            UILog.library.error("開けません: \(String(describing: error), privacy: .public)")
            presentError(String(localized: "保存した作業を開けませんでした。"))
            refreshLibrary()
        }
    }

    // MARK: 自動保存

    /// 編集が起きた（候補の採否・マスクの追加/削除/移動など）。書き出し済みなら「作業中」へ戻す。
    func noteEdited() {
        if workStatus == .exported { workStatus = .inProgress }
        scheduleAutosave()
    }

    /// 基準画像が変わった（初回解析・再解析）。次の保存でこのページの PNG を書き直す。
    func noteBaseImageChanged(_ page: PageState) {
        dirtyImagePageIDs.insert(page.id)
        noteEdited()
    }

    /// 書き出しが完了した。状態を「書き出し済み」にし、完了トーストを出す。
    func markExported(name: String, url: URL?) {
        workStatus = .exported
        lastExportedName = name
        lastExportedURL = url
        showingExportDone = true
        scheduleAutosave()
    }

    /// デバウンス付きの自動保存予約（既定1秒）。
    func scheduleAutosave() {
        guard library != nil, !pages.isEmpty else { return }
        hasUnsavedChanges = true
        autosaveTask?.cancel()
        let delay = autosaveDebounce
        autosaveTask = Task { [weak self] in
            if delay > .zero { try? await Task.sleep(for: delay) }
            guard !Task.isCancelled, let self else { return }
            self.performSave()
        }
    }

    /// 予約中の保存を即座に確定する（「新しい書類」・アプリ終了・バックグラウンド移行）。
    func flushAutosave() {
        autosaveTask?.cancel()
        autosaveTask = nil
        guard hasUnsavedChanges else { return }
        performSave()
    }

    private func performSave() {
        autosaveTask = nil
        guard let library, !pages.isEmpty else { return }
        let id = currentWorkID ?? UUID()
        let created = workCreatedAt ?? Date()
        let doc = WorkDocumentBridge.makeDocument(
            id: id,
            title: pages.first?.sourceURL.lastPathComponent ?? "document",
            createdAt: created,
            updatedAt: Date(),
            status: workStatus,
            lastExportedName: lastExportedName,
            pages: pages)
        let images = WorkDocumentBridge.pageImages(for: pages, dirty: dirtyImagePageIDs)
        do {
            try library.save(doc, pageImages: images)
            currentWorkID = id
            workCreatedAt = created
            dirtyImagePageIDs = []
            hasUnsavedChanges = false
            librarySaveError = nil
            refreshLibrary()
        } catch {
            // 黙らない: 確認画面のバナーに理由を出す。ログにもタイトル・ファイル名は出さない。
            UILog.library.error("保存できません: \(String(describing: error), privacy: .public)")
            librarySaveError = String(localized: "保存できませんでした: \(String(describing: error))")
        }
    }

    /// 全ページに編集通知を差し込む（自動保存のトリガ）。
    func attachEditObservers() {
        for page in pages {
            page.onEdit = { [weak self] in self?.noteEdited() }
        }
    }

    /// 現在の作業の同一性を手放す（次の保存は新しい id になる）。
    private func clearWorkIdentity() {
        autosaveTask?.cancel()
        autosaveTask = nil
        currentWorkID = nil
        workCreatedAt = nil
        workStatus = .inProgress
        lastExportedName = nil
        dirtyImagePageIDs = []
        hasUnsavedChanges = false
        librarySaveError = nil
    }
}
