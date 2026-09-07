import CoreGraphics
import XCTest
import MaskingCore
@testable import Fuseo

/// 層1(Mac): PDF 由来ページの再解析規約（WP-10 §2.3）。
/// PDF ページは平面・全面なので、種別変更・回転などの**再解析でも `Quad.fullImage` を維持する**。
/// ただしユーザーが「切り抜きを調整」で指定した quad が最優先。
@MainActor
final class PDFPageStateTests: XCTestCase {

    private func makeSettings() -> SettingsStore {
        SettingsStore(defaults: UserDefaults(suiteName: "fuseo.test.\(UUID().uuidString)")!)
    }

    private func makeState(_ fake: FakeAnalysis) -> AppState {
        AppState(analysis: fake, settings: makeSettings())
    }

    private let pdfPage = IntakeFile(url: URL(fileURLWithPath: "/tmp/hoken-p001.png"), isFlatPage: true)
    private let photo = IntakeFile(url: URL(fileURLWithPath: "/tmp/photo.jpg"))

    func test_processFiles_pdfPageBypassesDocumentDetection() async {
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage() }
        let state = makeState(fake)
        await state.processFiles([pdfPage])

        XCTAssertEqual(fake.lastManualQuad, .fullImage, "PDF由来ページは全面固定で解析する")
        XCTAssertTrue(state.pages[0].isFlatSource)
        XCTAssertTrue(state.hasPDFPages)
    }

    func test_processFiles_imageKeepsAutomaticDetection() async {
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage() }
        let state = makeState(fake)
        await state.processFiles([photo])

        XCTAssertNil(fake.lastManualQuad, "画像は従来どおり自動の書類検出に任せる")
        XCTAssertFalse(state.pages[0].isFlatSource)
        XCTAssertFalse(state.hasPDFPages)
    }

    func test_appendFiles_pdfPageBypassesDocumentDetection() async {
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage() }
        let state = makeState(fake)
        await state.processFiles([photo])
        await state.appendFiles([pdfPage])

        XCTAssertEqual(state.pages.count, 2)
        XCTAssertEqual(fake.lastManualQuad, .fullImage)
        XCTAssertTrue(state.pages[1].isFlatSource)
    }

    func test_typeChange_keepsFullImageQuadForPDFPage() async {
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage() }
        let state = makeState(fake)
        await state.processFiles([pdfPage])

        await state.performTypeChange(page: state.pages[0], to: .menkyoshoFront)
        XCTAssertEqual(fake.lastManualQuad, .fullImage, "種別変更の再解析でも全面固定を維持する")
    }

    func test_rotate_keepsFullImageQuadForPDFPage() async {
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage() }
        let state = makeState(fake)
        await state.processFiles([pdfPage])

        await state.performRotate(page: state.pages[0])
        XCTAssertEqual(fake.lastManualQuad, .fullImage, "回転の再解析でも全面固定を維持する")
        XCTAssertEqual(fake.lastManualRotation, 1)
    }

    func test_userQuadWinsOverFlatSource() async {
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage() }
        let state = makeState(fake)
        await state.processFiles([pdfPage])
        let page = state.pages[0]

        let quad = Quad(topLeft: CGPoint(x: 0.1, y: 0.9), topRight: CGPoint(x: 0.9, y: 0.9),
                        bottomRight: CGPoint(x: 0.9, y: 0.1), bottomLeft: CGPoint(x: 0.1, y: 0.1))
        await state.applyCrop(page: page, quad: quad)
        XCTAssertEqual(fake.lastManualQuad, quad)

        // 以降の再解析はユーザー指定の quad が優先される（全面固定に戻さない）。
        await state.performRotate(page: page)
        XCTAssertEqual(fake.lastManualQuad, quad, "ユーザーの切り抜きが最優先")
        XCTAssertEqual(page.reanalysisQuad, quad)
    }

    // MARK: - フィクスチャ

    private var trash: [URL] = []

    override func tearDown() {
        trash.forEach { try? FileManager.default.removeItem(at: $0) }
        trash = []
        super.tearDown()
    }

    /// 白紙 PDF を合成する（fixtures-private は使わない）。
    private func makePDF(name: String, pageCount: Int = 2) throws -> URL {
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("fuseo-import-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        trash.append(dir)

        let pdf = dir.appendingPathComponent("\(name).pdf")
        var mediaBox = CGRect(x: 0, y: 0, width: 200, height: 280)
        guard let ctx = CGContext(pdf as CFURL, mediaBox: &mediaBox, nil) else {
            throw XCTSkip("PDF コンテキストを作成できない環境")
        }
        for _ in 0..<pageCount {
            ctx.beginPDFPage(nil)
            ctx.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
            ctx.fill(mediaBox)
            ctx.endPDFPage()
        }
        ctx.closePDF()
        return pdf
    }

    /// `importFiles`（取り込みの唯一の入口）で PDF が2ページに展開され、Review まで進むこと。
    /// 注意文（画像化される旨）のフラグも取り込みのたびに立て直す。
    func test_importFiles_expandsPDFAndReachesReview() async throws {
        let pdf = try makePDF(name: "koujo")
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage() }
        let state = makeState(fake)
        state.pdfNoticeDismissed = true

        await state.importFiles([pdf], writer: state.makeTempPageWriter(),
                                rasterizer: PDFRasterizer(dpi: 72))

        XCTAssertEqual(state.stage, .review)
        XCTAssertEqual(state.pages.count, 2)
        XCTAssertTrue(state.pages.allSatisfy(\.isFlatSource))
        XCTAssertTrue(state.hasPDFPages)
        XCTAssertFalse(state.pdfNoticeDismissed, "PDF取り込みのたびに注意文を出し直す")
        XCTAssertEqual(state.pages.map { $0.sourceURL.deletingPathExtension().lastPathComponent },
                       ["koujo-p001", "koujo-p002"])
    }

    /// ページ画像の一時ディレクトリの寿命（本人確認書類の像を temp に残さない）。
    /// セッション中は再解析のため残し、**取り込みの置き換え**・「新しい書類」で削除する。
    func test_pageImageDirectories_areReplacedOnNewImportAndPurgedOnReset() async throws {
        let fake = FakeAnalysis { _ in TestFixtures.analyzedPage() }
        let state = makeState(fake)
        let fm = FileManager.default

        await state.importFiles([try makePDF(name: "first")], writer: state.makeTempPageWriter(),
                                rasterizer: PDFRasterizer(dpi: 72))
        let firstDirs = state.pageImageDirectories
        XCTAssertEqual(firstDirs.count, 1)
        XCTAssertTrue(fm.fileExists(atPath: firstDirs[0].path), "セッション中は残す（再解析で読み直すため）")

        // 置き換え取り込み: 前回のページ画像は消える。
        await state.importFiles([try makePDF(name: "second")], writer: state.makeTempPageWriter(),
                                rasterizer: PDFRasterizer(dpi: 72))
        XCTAssertFalse(fm.fileExists(atPath: firstDirs[0].path), "置き換え時に前の一時ディレクトリが消えていない")
        XCTAssertEqual(state.pageImageDirectories.count, 1)
        let secondDir = state.pageImageDirectories[0]
        XCTAssertTrue(fm.fileExists(atPath: secondDir.path))

        // 追加取込は同じ集合に積む（現行ページが参照しているので消さない）。
        await state.importFiles([try makePDF(name: "third")], writer: state.makeTempPageWriter(),
                                rasterizer: PDFRasterizer(dpi: 72), append: true)
        XCTAssertEqual(state.pageImageDirectories.count, 2)
        XCTAssertTrue(fm.fileExists(atPath: secondDir.path), "追加取込では前の分を消さない")
        let allDirs = state.pageImageDirectories

        // 「新しい書類」で全部消す（アプリ終了時の purge と同じ経路）。
        state.reset()
        XCTAssertTrue(state.pageImageDirectories.isEmpty)
        for dir in allDirs {
            XCTAssertFalse(fm.fileExists(atPath: dir.path), "リセット後に一時ページ画像が残っている: \(dir.lastPathComponent)")
        }
    }
}
