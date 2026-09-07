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
}
