import CoreGraphics
import ImageIO
import PDFKit
import UniformTypeIdentifiers
import XCTest
import MaskingCore
@testable import Fuseo

/// 層1(Mac): PDF 取り込み（WP-10 §2.4/§2.5）。受理タイプ・ページ命名・パスワード/エラー分岐。
/// フィクスチャは**テスト内で合成**する（fixtures-private は使わない）。
@MainActor
final class PDFIntakeTests: XCTestCase {

    // MARK: - フィクスチャ

    private var trash: [URL] = []

    override func tearDown() {
        trash.forEach { try? FileManager.default.removeItem(at: $0) }
        trash = []
        super.tearDown()
    }

    /// 指定ページ数の PDF を合成する（必要ならユーザーパスワード付き）。
    private func makePDF(pageCount: Int, name: String = "statement",
                         size: CGSize = CGSize(width: 300, height: 400),
                         userPassword: String? = nil) throws -> URL {
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("fuseo-pdfintake-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        trash.append(dir)

        let url = dir.appendingPathComponent("\(name).pdf")
        var mediaBox = CGRect(origin: .zero, size: size)
        var auxiliary: [String: Any] = [:]
        if let userPassword {
            auxiliary[kCGPDFContextUserPassword as String] = userPassword
            auxiliary[kCGPDFContextOwnerPassword as String] = userPassword
        }
        guard let ctx = CGContext(url as CFURL, mediaBox: &mediaBox,
                                  auxiliary.isEmpty ? nil : auxiliary as CFDictionary) else {
            throw XCTSkip("PDF コンテキストを作成できない環境")
        }
        for _ in 0..<pageCount {
            ctx.beginPDFPage(nil)
            ctx.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
            ctx.fill(mediaBox)
            ctx.setFillColor(CGColor(red: 0, green: 0, blue: 0, alpha: 1))
            ctx.fill(CGRect(x: 20, y: 20, width: 60, height: 20))
            ctx.endPDFPage()
        }
        ctx.closePDF()
        return url
    }

    /// テスト用の書き出し先（呼ばれた baseName を記録する）。
    private final class RecordingWriter {
        let dir: URL
        private(set) var baseNames: [String] = []
        init() {
            dir = FileManager.default.temporaryDirectory
                .appendingPathComponent("fuseo-pdfpages-\(UUID().uuidString)", isDirectory: true)
            try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        }
        @MainActor
        func write(_ image: CGImage, _ baseName: String) throws -> URL {
            baseNames.append(baseName)
            let url = dir.appendingPathComponent("\(baseName).png")
            let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil)!
            CGImageDestinationAddImage(dest, image, nil)
            XCTAssertTrue(CGImageDestinationFinalize(dest))
            return url
        }
        func cleanUp() { try? FileManager.default.removeItem(at: dir) }
    }

    /// パスワードを順に返すフェイク入力 UI。`nil` を返すとキャンセル。
    private final class FakePassword {
        var answers: [String?]
        private(set) var retryFlags: [Bool] = []
        private(set) var fileNames: [String] = []
        init(_ answers: [String?]) { self.answers = answers }
        @MainActor
        func provide(_ fileName: String, _ retry: Bool) async -> String? {
            fileNames.append(fileName)
            retryFlags.append(retry)
            return answers.isEmpty ? nil : answers.removeFirst()
        }
    }

    // MARK: - 受理タイプ（層1 §2.5）

    func test_isAccepted_acceptsPDF() {
        XCTAssertTrue(FileIntake.isAccepted(URL(fileURLWithPath: "/tmp/a.pdf")))
        XCTAssertTrue(FileIntake.isAccepted(URL(fileURLWithPath: "/tmp/A.PDF")))
    }

    func test_isAccepted_stillAcceptsImagesAndRejectsOthers() {
        XCTAssertTrue(FileIntake.isAccepted(URL(fileURLWithPath: "/tmp/a.jpeg")))
        XCTAssertTrue(FileIntake.isAccepted(URL(fileURLWithPath: "/tmp/a.heic")))
        XCTAssertFalse(FileIntake.isAccepted(URL(fileURLWithPath: "/tmp/a.txt")))
        XCTAssertFalse(FileIntake.isAccepted(URL(fileURLWithPath: "/tmp/a.docx")))
    }

    func test_isPDF_matchesExtensionOnly() {
        XCTAssertTrue(PDFIntake.isPDF(URL(fileURLWithPath: "/tmp/a.pdf")))
        XCTAssertFalse(PDFIntake.isPDF(URL(fileURLWithPath: "/tmp/a.png")))
    }

    // MARK: - ページ命名（ExportNamingTests の流儀）

    func test_pageBaseName_zeroPaddedThreeDigits() {
        let pdf = URL(fileURLWithPath: "/tmp/生命保険料控除証明書.pdf")
        XCTAssertEqual(PDFIntake.pageBaseName(pdfURL: pdf, pageNumber: 1), "生命保険料控除証明書-p001")
        XCTAssertEqual(PDFIntake.pageBaseName(pdfURL: pdf, pageNumber: 3), "生命保険料控除証明書-p003")
        XCTAssertEqual(PDFIntake.pageBaseName(pdfURL: pdf, pageNumber: 42), "生命保険料控除証明書-p042")
    }

    func test_pageBaseName_keepsDotsInStem() {
        XCTAssertEqual(PDFIntake.pageBaseName(pdfURL: URL(fileURLWithPath: "/tmp/2026.10.hoken.pdf"),
                                              pageNumber: 1),
                       "2026.10.hoken-p001")
    }

    // MARK: - 展開

    func test_run_expandsPDFIntoFlatPagesInOrder() async throws {
        let pdf = try makePDF(pageCount: 2)
        let writer = RecordingWriter(); defer { writer.cleanUp() }

        let outcome = await PDFIntake.run(urls: [pdf], rasterizer: PDFRasterizer(dpi: 72),
                                          passwordProvider: { _, _ in nil },
                                          writer: writer.write)

        guard case .files(let files) = outcome else { return XCTFail("展開に失敗: \(outcome)") }
        XCTAssertEqual(files.count, 2)
        XCTAssertTrue(files.allSatisfy(\.isFlatPage), "PDF由来ページは平面フラグが立つ")
        XCTAssertEqual(writer.baseNames, ["statement-p001", "statement-p002"])
    }

    func test_run_keepsImagesAsNonFlatAndPreservesOrder() async throws {
        let pdf = try makePDF(pageCount: 2)
        let image = URL(fileURLWithPath: "/tmp/photo.jpeg")
        let writer = RecordingWriter(); defer { writer.cleanUp() }

        let outcome = await PDFIntake.run(urls: [image, pdf], rasterizer: PDFRasterizer(dpi: 72),
                                          passwordProvider: { _, _ in nil },
                                          writer: writer.write)

        guard case .files(let files) = outcome else { return XCTFail("展開に失敗: \(outcome)") }
        XCTAssertEqual(files.count, 3)
        XCTAssertEqual(files.map(\.isFlatPage), [false, true, true])
        XCTAssertEqual(files[0].url, image)
    }

    // MARK: - パスワード

    func test_run_lockedPDF_asksPasswordAndUnlocks() async throws {
        let pdf = try makePDF(pageCount: 1, name: "locked", userPassword: "hunter2")
        XCTAssertTrue(PDFRasterizer.isLocked(url: pdf), "合成PDFが暗号化されていない")
        let writer = RecordingWriter(); defer { writer.cleanUp() }
        let password = FakePassword(["hunter2"])

        let outcome = await PDFIntake.run(urls: [pdf], rasterizer: PDFRasterizer(dpi: 72),
                                          passwordProvider: password.provide,
                                          writer: writer.write)

        guard case .files(let files) = outcome else { return XCTFail("解錠に失敗: \(outcome)") }
        XCTAssertEqual(files.count, 1)
        XCTAssertEqual(password.retryFlags, [false], "初回は再入力フラグが立たない")
        XCTAssertEqual(password.fileNames, ["locked.pdf"], "どのファイルのパスワードか分かる")
    }

    func test_run_wrongPassword_retriesWithRetryFlag() async throws {
        let pdf = try makePDF(pageCount: 1, name: "locked", userPassword: "hunter2")
        let writer = RecordingWriter(); defer { writer.cleanUp() }
        let password = FakePassword(["wrong", "alsowrong", "hunter2"])

        let outcome = await PDFIntake.run(urls: [pdf], rasterizer: PDFRasterizer(dpi: 72),
                                          passwordProvider: password.provide,
                                          writer: writer.write)

        guard case .files(let files) = outcome else { return XCTFail("再入力で解錠できていない: \(outcome)") }
        XCTAssertEqual(files.count, 1)
        XCTAssertEqual(password.retryFlags, [false, true, true], "2回目以降は再入力として出す")
    }

    func test_run_passwordCancelled_abortsWithoutImporting() async throws {
        let pdf = try makePDF(pageCount: 2, name: "locked", userPassword: "hunter2")
        let writer = RecordingWriter(); defer { writer.cleanUp() }
        let password = FakePassword([nil])

        let outcome = await PDFIntake.run(urls: [pdf], rasterizer: PDFRasterizer(dpi: 72),
                                          passwordProvider: password.provide,
                                          writer: writer.write)

        XCTAssertEqual(outcome, .cancelled)
        XCTAssertTrue(writer.baseNames.isEmpty, "キャンセル時は1ページも書き出さない")
    }

    // MARK: - 上限・エラー

    func test_run_overCombinedLimit_failsAndImportsNothing() async throws {
        let pdf = try makePDF(pageCount: 4)
        let writer = RecordingWriter(); defer { writer.cleanUp() }

        let outcome = await PDFIntake.run(urls: [URL(fileURLWithPath: "/tmp/a.jpeg"), pdf],
                                          limit: 4, rasterizer: PDFRasterizer(dpi: 72),
                                          passwordProvider: { _, _ in nil },
                                          writer: writer.write)

        guard case .failed(let message) = outcome else { return XCTFail("上限超過が検出されない: \(outcome)") }
        XCTAssertTrue(message.contains("5"), "合計ページ数を伝える文言であること: \(message)")
        XCTAssertTrue(writer.baseNames.isEmpty, "超過時は黙って切り捨てず、1ページも取り込まない")
    }

    func test_run_withinCombinedLimit_succeeds() async throws {
        let pdf = try makePDF(pageCount: 3)
        let writer = RecordingWriter(); defer { writer.cleanUp() }

        let outcome = await PDFIntake.run(urls: [URL(fileURLWithPath: "/tmp/a.jpeg"), pdf],
                                          limit: 4, rasterizer: PDFRasterizer(dpi: 72),
                                          passwordProvider: { _, _ in nil },
                                          writer: writer.write)

        guard case .files(let files) = outcome else { return XCTFail("上限内なのに失敗: \(outcome)") }
        XCTAssertEqual(files.count, 4)
    }

    func test_run_tooManyPages_failsWithPageCount() async throws {
        let pdf = try makePDF(pageCount: 6)
        let writer = RecordingWriter(); defer { writer.cleanUp() }

        let outcome = await PDFIntake.run(urls: [pdf], limit: nil,
                                          rasterizer: PDFRasterizer(dpi: 72, maxPages: 5),
                                          passwordProvider: { _, _ in nil },
                                          writer: writer.write)

        guard case .failed(let message) = outcome else { return XCTFail("ページ数上限が効いていない: \(outcome)") }
        XCTAssertTrue(message.contains("6"), "実ページ数を伝える文言であること: \(message)")
    }

    func test_run_unreadable_failsWithReason() async throws {
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("fuseo-pdfbroken-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        trash.append(dir)
        let broken = dir.appendingPathComponent("broken.pdf")
        try Data("not a pdf".utf8).write(to: broken)
        let writer = RecordingWriter(); defer { writer.cleanUp() }

        let outcome = await PDFIntake.run(urls: [broken], rasterizer: PDFRasterizer(dpi: 72),
                                          passwordProvider: { _, _ in nil },
                                          writer: writer.write)

        guard case .failed(let message) = outcome else { return XCTFail("壊れたPDFが失敗しない: \(outcome)") }
        XCTAssertTrue(message.contains("broken.pdf"), "どのファイルか分かる文言であること: \(message)")
    }

    /// 文言に出す上限は**実際に使う rasterizer の maxPages**（既定値ではない）。
    func test_run_tooManyPages_messageUsesActualRasterizerLimit() async throws {
        let pdf = try makePDF(pageCount: 4)
        let writer = RecordingWriter(); defer { writer.cleanUp() }

        let outcome = await PDFIntake.run(urls: [pdf], limit: nil,
                                          rasterizer: PDFRasterizer(dpi: 72, maxPages: 3),
                                          passwordProvider: { _, _ in nil },
                                          writer: writer.write)

        guard case .failed(let message) = outcome else { return XCTFail("ページ数上限が効いていない: \(outcome)") }
        XCTAssertTrue(message.contains("3"), "実際の上限(3)を伝える文言であること: \(message)")
        XCTAssertFalse(message.contains("50"), "既定値(50)が漏れている: \(message)")
        XCTAssertTrue(message.contains("4"), "実ページ数(4)を伝える文言であること: \(message)")
    }

    func test_message_isDistinctForEachFailure() {
        let messages = [
            PDFIntake.message(for: .locked, fileName: "a.pdf"),
            PDFIntake.message(for: .wrongPassword, fileName: "a.pdf"),
            PDFIntake.message(for: .unreadable, fileName: "a.pdf"),
            PDFIntake.message(for: .tooManyPages(99), fileName: "a.pdf")
        ]
        XCTAssertEqual(Set(messages).count, 4, "失敗理由ごとに別々の文言であること")
        XCTAssertTrue(messages.allSatisfy { $0.contains("a.pdf") })
    }

    // MARK: - 文字認識の選択（WP-10b・B）

    /// 文字認識の選択 UI のフェイク。呼ばれた回数と引数（総ページ数・見積秒数）を記録する。
    private final class FakeTextChoice {
        let answer: PDFIntake.TextChoice?
        private(set) var calls: [(pages: Int, seconds: Int)] = []
        init(_ answer: PDFIntake.TextChoice?) { self.answer = answer }
        @MainActor
        func provide(_ totalPages: Int, _ seconds: Int) async -> PDFIntake.TextChoice? {
            calls.append((totalPages, seconds))
            return answer
        }
    }

    /// 閾値ちょうど（10ページ）では聞かない＝短いPDFの動線を1手も増やさない。
    func test_run_atThreshold_doesNotAskAboutTextRecognition() async throws {
        let pdf = try makePDF(pageCount: PDFIntake.textRecognitionPromptThreshold)
        let writer = RecordingWriter(); defer { writer.cleanUp() }
        let choice = FakeTextChoice(.skip)

        let outcome = await PDFIntake.run(urls: [pdf], limit: nil, rasterizer: PDFRasterizer(dpi: 72),
                                          passwordProvider: { _, _ in nil },
                                          textChoiceProvider: choice.provide,
                                          writer: writer.write)

        guard case .files(let files) = outcome else { return XCTFail("展開に失敗: \(outcome)") }
        XCTAssertTrue(choice.calls.isEmpty, "閾値以下では文字認識の選択を聞かない")
        XCTAssertTrue(files.allSatisfy(\.recognizeText), "既定は文字認識あり")
        XCTAssertTrue(files.allSatisfy { $0.analysisOptions == .flatPage })
    }

    /// 閾値超（11ページ）で1回だけ聞く。見積は 1ページ2秒。
    func test_run_overThreshold_asksOnceWithEstimate() async throws {
        let pdf = try makePDF(pageCount: PDFIntake.textRecognitionPromptThreshold + 1)
        let writer = RecordingWriter(); defer { writer.cleanUp() }
        let choice = FakeTextChoice(.recognize)

        let outcome = await PDFIntake.run(urls: [pdf], limit: nil, rasterizer: PDFRasterizer(dpi: 72),
                                          passwordProvider: { _, _ in nil },
                                          textChoiceProvider: choice.provide,
                                          writer: writer.write)

        guard case .files(let files) = outcome else { return XCTFail("展開に失敗: \(outcome)") }
        XCTAssertEqual(choice.calls.count, 1, "取り込み単位で1回だけ聞く")
        XCTAssertEqual(choice.calls.first?.pages, 11)
        XCTAssertEqual(choice.calls.first?.seconds, 22, "1ページ2秒の見積")
        XCTAssertTrue(files.allSatisfy(\.recognizeText))
        XCTAssertTrue(files.allSatisfy { $0.analysisOptions == .flatPage })
    }

    /// 複数PDFでも「取り込み全体のPDF総ページ数」で1回だけ聞く。
    func test_run_multiplePDFs_asksOnceForCombinedPageCount() async throws {
        let first = try makePDF(pageCount: 6, name: "first")
        let second = try makePDF(pageCount: 6, name: "second")
        let writer = RecordingWriter(); defer { writer.cleanUp() }
        let choice = FakeTextChoice(.recognize)

        let outcome = await PDFIntake.run(urls: [first, second], limit: nil, rasterizer: PDFRasterizer(dpi: 72),
                                          passwordProvider: { _, _ in nil },
                                          textChoiceProvider: choice.provide,
                                          writer: writer.write)

        guard case .files = outcome else { return XCTFail("展開に失敗: \(outcome)") }
        XCTAssertEqual(choice.calls.count, 1, "PDFごとではなく取り込み単位で聞く")
        XCTAssertEqual(choice.calls.first?.pages, 12)
    }

    /// 「文字認識せずに進む」→ PDF由来ページだけ recognizeText=false（画像は従来どおり）。
    func test_run_skipChoice_marksOnlyPDFPagesWithoutText() async throws {
        let pdf = try makePDF(pageCount: 11)
        let image = URL(fileURLWithPath: "/tmp/photo.jpeg")
        let writer = RecordingWriter(); defer { writer.cleanUp() }
        let choice = FakeTextChoice(.skip)

        let outcome = await PDFIntake.run(urls: [image, pdf], limit: nil, rasterizer: PDFRasterizer(dpi: 72),
                                          passwordProvider: { _, _ in nil },
                                          textChoiceProvider: choice.provide,
                                          writer: writer.write)

        guard case .files(let files) = outcome else { return XCTFail("展開に失敗: \(outcome)") }
        XCTAssertEqual(files.count, 12)
        XCTAssertTrue(files[0].recognizeText, "画像は文字認識の対象のまま")
        XCTAssertEqual(files[0].analysisOptions, .default)
        XCTAssertTrue(files.dropFirst().allSatisfy { !$0.recognizeText }, "PDFページは全て文字認識なし")
        XCTAssertTrue(files.dropFirst().allSatisfy { $0.analysisOptions == .flatPageWithoutText })
    }

    /// キャンセル＝取り込み中止。1ページも書き出さない（エラー表示もしない）。
    func test_run_textChoiceCancelled_abortsWithoutImporting() async throws {
        let pdf = try makePDF(pageCount: 11)
        let writer = RecordingWriter(); defer { writer.cleanUp() }
        let choice = FakeTextChoice(nil)

        let outcome = await PDFIntake.run(urls: [pdf], limit: nil, rasterizer: PDFRasterizer(dpi: 72),
                                          passwordProvider: { _, _ in nil },
                                          textChoiceProvider: choice.provide,
                                          writer: writer.write)

        XCTAssertEqual(outcome, .cancelled)
        XCTAssertTrue(writer.baseNames.isEmpty, "選択前に1ページも書き出さない")
    }

    // MARK: - 見積の文言（WP-10b・B）

    /// UI言語が日本語か（英語UIのシミュレータでも壊れないように厳密比較はjaのときだけ行う）。
    private var uiIsJapanese: Bool {
        Bundle.main.preferredLocalizations.first?.hasPrefix("ja") ?? true
    }

    func test_estimatedSeconds_isTwoSecondsPerPage() {
        XCTAssertEqual(PDFIntake.estimatedSeconds(pages: 25), 50)
        XCTAssertEqual(PDFIntake.estimatedSeconds(pages: 40), 80)
    }

    func test_estimatedDurationText_underOneMinute_isSeconds() {
        let text = PDFIntake.estimatedDurationText(pages: 25)
        XCTAssertTrue(text.contains("50"), "秒数をそのまま出す: \(text)")
        if uiIsJapanese { XCTAssertEqual(text, "約50秒") }
    }

    func test_estimatedDurationText_overOneMinute_roundsUpToMinutes() {
        let text = PDFIntake.estimatedDurationText(pages: 40)
        XCTAssertFalse(text.contains("80"), "60秒以上は分に切り上げる: \(text)")
        XCTAssertTrue(text.contains("2"), "80秒→2分（切り上げ）: \(text)")
        if uiIsJapanese { XCTAssertEqual(text, "約2分") }
    }

    // MARK: - 上限超過の案内（WP-10b・D）

    func test_limitMessage_suggestsMacAppOnlyOniOS() {
        let message = PDFIntake.limitMessage(total: 30, limit: 10)
        #if os(iOS)
        XCTAssertTrue(PDFIntake.mentionsMacApp(message), "iOSは行き止まりにせずMac版を案内する: \(message)")
        XCTAssertTrue(message.contains("50"), "Mac版のページ上限を伝える: \(message)")
        #else
        XCTAssertFalse(PDFIntake.mentionsMacApp(message), "Mac版は自分自身を案内しない")
        XCTAssertFalse(message.contains("Mac"), "Mac版の案内が漏れている: \(message)")
        #endif
    }
}
