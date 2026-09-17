import CoreGraphics
import CoreText
import Foundation
import ImageIO
import PDFKit
import UniformTypeIdentifiers
import XCTest
@testable import MaskingCore

/// WP-10 §2.5 層1: PDF 入力（ラスタライズ）。
///
/// フィクスチャは**テスト内で合成**する（fixtures-private の実物書類は使わない）。
final class PDFRasterizerTests: XCTestCase {

    // MARK: - フィクスチャ生成

    /// A4 相当（595×842pt）の PDF を合成する。
    /// - Parameters:
    ///   - pages: 各ページに描く文字列。
    ///   - rotation: 全ページに設定する `/Rotate`（0/90/180/270）。CGPDFContext には /Rotate を
    ///     書くキーが無いため、生成後に PDFKit でページ回転を設定して書き戻す。
    ///   - userPassword: 指定すると暗号化 PDF になる。
    private func makePDF(pages: [String], size: CGSize = CGSize(width: 595, height: 842),
                         rotation: Int = 0, userPassword: String? = nil) throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("pdfrast-\(UUID().uuidString).pdf")
        var mediaBox = CGRect(origin: .zero, size: size)

        var auxiliary: [String: Any] = [:]
        if let userPassword {
            auxiliary[kCGPDFContextUserPassword as String] = userPassword
            // オーナーパスワード未指定だと解錠可否が環境依存になるため明示する。
            auxiliary[kCGPDFContextOwnerPassword as String] = userPassword
        }

        guard let ctx = CGContext(url as CFURL, mediaBox: &mediaBox,
                                  auxiliary.isEmpty ? nil : auxiliary as CFDictionary) else {
            throw XCTSkip("PDF コンテキストを作成できない環境")
        }

        for text in pages {
            ctx.beginPDFPage(nil)

            // 白地に黒文字。OCR が読める程度の大きさで描く。
            ctx.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
            ctx.fill(mediaBox)
            draw(text: text, in: ctx, at: CGPoint(x: 60, y: size.height - 140), fontSize: 36)

            ctx.endPDFPage()
        }
        ctx.closePDF()

        if rotation != 0 {
            // /Rotate を持つ PDF に書き換える（暗号化と回転の同時指定はテストで使わない）。
            guard let doc = PDFDocument(url: url) else { throw XCTSkip("PDFKit で再読込できない") }
            for i in 0..<doc.pageCount { doc.page(at: i)?.rotation = rotation }
            guard doc.write(to: url) else { throw XCTSkip("PDFKit で書き戻せない") }
        }
        return url
    }

    private func draw(text: String, in ctx: CGContext, at origin: CGPoint, fontSize: CGFloat) {
        let font = CTFontCreateWithName("Helvetica" as CFString, fontSize, nil)
        let attributed = NSAttributedString(string: text, attributes: [
            .font: font,
            .foregroundColor: CGColor(red: 0, green: 0, blue: 0, alpha: 1),
        ])
        let line = CTLineCreateWithAttributedString(attributed)
        ctx.textPosition = origin
        CTLineDraw(line, ctx)
    }

    private func removeFile(_ url: URL) {
        try? FileManager.default.removeItem(at: url)
    }

    // MARK: - 1. ページ数と寸法

    func test_rasterize_pageCountAndPixelSize() throws {
        let url = try makePDF(pages: ["Page one", "Page two"])
        defer { removeFile(url) }

        let rasterizer = PDFRasterizer(dpi: 300)
        var pages: [PDFRasterizer.Page] = []
        try rasterizer.rasterize(url: url) { pages.append($0) }

        XCTAssertEqual(pages.count, 2)
        XCTAssertEqual(pages.map(\.index), [0, 1])

        // 595×842pt @300dpi = 2479×3508px（四捨五入で ±1 の誤差を許容）
        let expectedW = Int((595.0 / 72.0 * 300.0).rounded())
        let expectedH = Int((842.0 / 72.0 * 300.0).rounded())
        XCTAssertEqual(pages[0].cgImage.width, expectedW, accuracy: 1)
        XCTAssertEqual(pages[0].cgImage.height, expectedH, accuracy: 1)
        XCTAssertEqual(pages[0].pointSize.width, 595, accuracy: 0.5)
        XCTAssertEqual(pages[0].pointSize.height, 842, accuracy: 0.5)
    }

    // MARK: - 2. /Rotate 90 で縦横比が入れ替わる

    func test_rasterize_appliesPageRotation() throws {
        let portrait = try makePDF(pages: ["Upright"], rotation: 0)
        let rotated = try makePDF(pages: ["Rotated"], rotation: 90)
        defer { removeFile(portrait); removeFile(rotated) }

        let rasterizer = PDFRasterizer(dpi: 72)  // 等倍で十分

        var portraitPage: PDFRasterizer.Page?
        try rasterizer.rasterize(url: portrait) { portraitPage = $0 }
        var rotatedPage: PDFRasterizer.Page?
        try rasterizer.rasterize(url: rotated) { rotatedPage = $0 }

        let p = try XCTUnwrap(portraitPage)
        let r = try XCTUnwrap(rotatedPage)

        // 元が縦長（595×842）なら、/Rotate 90 の結果は横長になる。
        XCTAssertLessThan(p.cgImage.width, p.cgImage.height, "回転なしは縦長のはず")
        XCTAssertGreaterThan(r.cgImage.width, r.cgImage.height, "/Rotate 90 は横長になるはず")

        // pointSize も入れ替わっている。
        XCTAssertEqual(r.pointSize.width, 842, accuracy: 0.5)
        XCTAssertEqual(r.pointSize.height, 595, accuracy: 0.5)
    }

    // MARK: - 2b. 内容がページ全面に描かれる（縮小・中央寄せしない）

    /// ページ端から 10pt 内側に黒い枠線を描いた PDF。ラスタライズ後、暗い画素の範囲が
    /// ほぼ全面（各辺 ≥ 0.95）に達していること。2026-09-17 のバグ（内容が約 57% に縮小して中央に
    /// 描かれる）を固定する。回転ページも同様。
    func test_rasterize_drawsContentAtFullScale_forUprightAndRotatedPages() throws {
        for rotation in [0, 90] {
            let url = try makeBorderPDF(rotation: rotation)
            defer { removeFile(url) }
            var page: PDFRasterizer.Page?
            try PDFRasterizer(dpi: 100).rasterize(url: url) { page = $0 }
            let img = try XCTUnwrap(page).cgImage
            let bbox = Self.darkBounds(of: img)
            XCTAssertLessThan(bbox.minX, 0.05, "rotation=\(rotation): 左端まで描かれていない (\(bbox))")
            XCTAssertGreaterThan(bbox.maxX, 0.95, "rotation=\(rotation): 右端まで描かれていない (\(bbox))")
            XCTAssertLessThan(bbox.minY, 0.05, "rotation=\(rotation): 上端まで描かれていない (\(bbox))")
            XCTAssertGreaterThan(bbox.maxY, 0.95, "rotation=\(rotation): 下端まで描かれていない (\(bbox))")
        }
    }

    /// 595×842pt のページに、端から 10pt 内側の黒枠（太さ 6pt）を描く。
    private func makeBorderPDF(rotation: Int) throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("pdfrast-border-\(UUID().uuidString).pdf")
        var mediaBox = CGRect(x: 0, y: 0, width: 595, height: 842)
        guard let ctx = CGContext(url as CFURL, mediaBox: &mediaBox, nil) else {
            throw XCTSkip("PDF コンテキストを作成できない環境")
        }
        ctx.beginPDFPage(nil)
        ctx.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1)); ctx.fill(mediaBox)
        ctx.setStrokeColor(CGColor(red: 0, green: 0, blue: 0, alpha: 1)); ctx.setLineWidth(6)
        ctx.stroke(mediaBox.insetBy(dx: 10, dy: 10))
        ctx.endPDFPage(); ctx.closePDF()
        if rotation != 0 {
            guard let doc = PDFDocument(url: url) else { throw XCTSkip("PDFKit で再読込できない") }
            doc.page(at: 0)?.rotation = rotation
            guard doc.write(to: url) else { throw XCTSkip("PDFKit で書き戻せない") }
        }
        return url
    }

    /// 暗い画素（RGB すべて < 128）の外接矩形を正規化（0..1・左上原点）で返す。
    private static func darkBounds(of image: CGImage) -> (minX: Double, maxX: Double, minY: Double, maxY: Double) {
        let w = image.width, h = image.height
        var data = [UInt8](repeating: 0, count: w * h * 4)
        let ctx = CGContext(data: &data, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        var minX = w, maxX = -1, minY = h, maxY = -1
        for y in 0..<h {
            for x in 0..<w {
                let i = (y * w + x) * 4
                if data[i] < 128 && data[i + 1] < 128 && data[i + 2] < 128 {
                    minX = min(minX, x); maxX = max(maxX, x); minY = min(minY, y); maxY = max(maxY, y)
                }
            }
        }
        return (Double(minX) / Double(w), Double(maxX + 1) / Double(w),
                Double(minY) / Double(h), Double(maxY + 1) / Double(h))
    }

    // MARK: - 3. パスワード

    func test_lockedPDF_requiresCorrectPassword() throws {
        let url = try makePDF(pages: ["Secret"], userPassword: "correct-horse")
        defer { removeFile(url) }

        XCTAssertTrue(PDFRasterizer.isLocked(url: url), "暗号化 PDF は isLocked=true")

        let rasterizer = PDFRasterizer(dpi: 72)

        // パスワードなし → .locked
        XCTAssertThrowsError(try rasterizer.rasterize(url: url) { _ in }) { error in
            XCTAssertEqual(error as? PDFRasterizer.Failure, .locked)
        }

        // 誤パスワード → .wrongPassword
        XCTAssertThrowsError(try rasterizer.rasterize(url: url, password: "nope") { _ in }) { error in
            XCTAssertEqual(error as? PDFRasterizer.Failure, .wrongPassword)
        }

        // 正パスワード → 解錠して 1 ページ得られる
        var count = 0
        try rasterizer.rasterize(url: url, password: "correct-horse") { _ in count += 1 }
        XCTAssertEqual(count, 1)
    }

    func test_isLocked_falseForPlainPDF() throws {
        let url = try makePDF(pages: ["Plain"])
        defer { removeFile(url) }
        XCTAssertFalse(PDFRasterizer.isLocked(url: url))
    }

    // MARK: - 4. end-to-end（PDF → 解析 → マスク → 検索テキスト層から除外）

    func test_endToEnd_myNumberInPDFIsMaskedAndExcludedFromTextLayer() throws {
        // チェックディジットが成立する 12 桁を生成する（誤検出除去の実装に合わせる）。
        let myNumber = try XCTUnwrap(Self.validMyNumber(), "検査用数字が成立する12桁を生成できなかった")
        let spaced = Self.grouped(myNumber)

        let pdf = try makePDF(pages: ["Notice", spaced])
        defer { removeFile(pdf) }

        let rasterizer = PDFRasterizer(dpi: 300)
        let pipeline = try MaskingPipeline()
        var analyzed: [AnalyzedPage] = []
        var temps: [URL] = []
        defer { temps.forEach(removeFile) }

        try rasterizer.rasterize(url: pdf) { page in
            // PDF ページは既に平面・全面なので書類検出はバイパスする（§2.3）。
            let tmp = try Self.writePNG(page.cgImage, baseName: String(format: "pdfpage-%03d", page.index + 1))
            temps.append(tmp)
            analyzed.append(try pipeline.analyze(url: tmp, manualQuad: .fullImage))
        }

        XCTAssertEqual(analyzed.count, 2)

        // 2 ページ目に個人番号がマスク候補として出る。
        let candidates = analyzed[1].candidates
        let hasMyNumber = candidates.contains { $0.source == .detector(.myNumber12) }
        XCTAssertTrue(hasMyNumber,
                      "PDF 由来ページから個人番号が検出されること（候補: \(candidates.map(\.label))）")

        // 既存の書き出し経路で PDF に戻す。マスクと交差する OCR 観測はテキスト層から除外される。
        let out = FileManager.default.temporaryDirectory
            .appendingPathComponent("pdfrast-out-\(UUID().uuidString).pdf")
        defer { removeFile(out) }
        let rendered = analyzed.map {
            RenderedPage(image: $0.page.cgImage, ocrItems: $0.ocr, maskRects: $0.effectiveMaskRects)
        }
        try FileExporter().export(rendered, options: ExportOptions(format: .pdf, searchableText: true), to: out)

        let text = try XCTUnwrap(PDFDocument(url: out)?.string)
        let digitsOnly = text.filter(\.isNumber)
        XCTAssertFalse(digitsOnly.contains(myNumber), "12桁はテキスト層に残らないこと")
        XCTAssertTrue(text.contains("Notice"), "マスク外のテキストは検索できること")
    }

    // MARK: - 4b. 権限フラグ（印刷不可・コピー不可）でも描画が空にならない

    func test_permissionRestrictedPDF_stillRasterizesContent() throws {
        // オーナーパスワードのみ（ユーザーパスワード無し）＝開けるが印刷・コピーが禁止された PDF。
        // 保険会社などの電子交付 PDF に多い形。描画には影響しないはずだが、ここで固定する。
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("pdfrast-restricted-\(UUID().uuidString).pdf")
        defer { removeFile(url) }
        var mediaBox = CGRect(x: 0, y: 0, width: 595, height: 842)
        let aux: [String: Any] = [
            kCGPDFContextOwnerPassword as String: "owner-only",
            kCGPDFContextAllowsPrinting as String: false,
            kCGPDFContextAllowsCopying as String: false,
        ]
        guard let ctx = CGContext(url as CFURL, mediaBox: &mediaBox, aux as CFDictionary) else {
            throw XCTSkip("PDF コンテキストを作成できない環境")
        }
        ctx.beginPDFPage(nil)
        ctx.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        ctx.fill(mediaBox)
        // 大きな黒い矩形＝描画されていれば必ず暗いピクセルが出る
        ctx.setFillColor(CGColor(red: 0, green: 0, blue: 0, alpha: 1))
        ctx.fill(CGRect(x: 100, y: 500, width: 300, height: 200))
        ctx.endPDFPage()
        ctx.closePDF()

        XCTAssertFalse(PDFRasterizer.isLocked(url: url), "ユーザーパスワード無しなので locked ではない")

        var page: PDFRasterizer.Page?
        try PDFRasterizer(dpi: 72).rasterize(url: url) { page = $0 }
        let image = try XCTUnwrap(page).cgImage

        // 暗いピクセルの割合を数える（黒矩形は 300×200 / 595×842 ≒ 12%）
        let w = image.width, h = image.height
        var data = [UInt8](repeating: 0, count: w * h * 4)
        let cs = CGColorSpace(name: CGColorSpace.sRGB)!
        let bmp = try XCTUnwrap(CGContext(data: &data, width: w, height: h, bitsPerComponent: 8,
                                          bytesPerRow: w * 4, space: cs,
                                          bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        bmp.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        var dark = 0
        for i in stride(from: 0, to: data.count, by: 4) where data[i] < 64 && data[i + 1] < 64 && data[i + 2] < 64 {
            dark += 1
        }
        let ratio = Double(dark) / Double(w * h)
        XCTAssertGreaterThan(ratio, 0.08, "権限フラグ付き PDF の内容が描画されていること（暗画素比 \(ratio)）")
        XCTAssertLessThan(ratio, 0.20, "全面が黒になっていないこと（白下地が効いていること）")
    }

    // MARK: - 5. 上限

    func test_tooManyPages() throws {
        let url = try makePDF(pages: Array(repeating: "x", count: 6))
        defer { removeFile(url) }

        let rasterizer = PDFRasterizer(dpi: 72, maxPages: 5)
        XCTAssertThrowsError(try rasterizer.rasterize(url: url) { _ in }) { error in
            XCTAssertEqual(error as? PDFRasterizer.Failure, .tooManyPages(6))
        }
    }

    func test_maxLongSidePx_capsHugePage() throws {
        // 2000×3000pt のポスター。300dpi なら長辺 12500px になるが上限で抑える。
        let url = try makePDF(pages: ["Poster"], size: CGSize(width: 2000, height: 3000))
        defer { removeFile(url) }

        let rasterizer = PDFRasterizer(dpi: 300, maxLongSidePx: 4000)
        var page: PDFRasterizer.Page?
        try rasterizer.rasterize(url: url) { page = $0 }

        let p = try XCTUnwrap(page)
        XCTAssertLessThanOrEqual(max(p.cgImage.width, p.cgImage.height), 4000)
        // 縦横比は保たれている。
        let ratio = Double(p.cgImage.height) / Double(p.cgImage.width)
        XCTAssertEqual(ratio, 3000.0 / 2000.0, accuracy: 0.01)
    }

    func test_unreadableFile() throws {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("not-a-pdf-\(UUID().uuidString).pdf")
        try Data("this is not a pdf".utf8).write(to: url)
        defer { removeFile(url) }

        let rasterizer = PDFRasterizer(dpi: 72)
        XCTAssertThrowsError(try rasterizer.rasterize(url: url) { _ in }) { error in
            XCTAssertEqual(error as? PDFRasterizer.Failure, .unreadable)
        }
    }

    // MARK: - ヘルパ

    /// 検査用数字が成立する 12 桁を探す（総務省令式）。
    private static func validMyNumber() -> String? {
        for seed in 100_000_000_00...100_000_010_00 {
            let body = String(format: "%011ld", seed)   // Int は 64bit なので %ld（%d だと下位32bitに切られる）
            for check in 0...9 {
                let candidate = body + String(check)
                if Checkdigits.isValidMyNumber(candidate) { return candidate }
            }
        }
        return nil
    }

    /// OCR が桁を拾いやすいよう 4 桁ずつ空ける。
    private static func grouped(_ digits: String) -> String {
        stride(from: 0, to: digits.count, by: 4).map { offset -> String in
            let start = digits.index(digits.startIndex, offsetBy: offset)
            let end = digits.index(start, offsetBy: min(4, digits.count - offset))
            return String(digits[start..<end])
        }.joined(separator: " ")
    }

    private static func writePNG(_ image: CGImage, baseName: String) throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("\(baseName)-\(UUID().uuidString).png")
        guard let dest = CGImageDestinationCreateWithURL(url as CFURL,
                                                         UTType.png.identifier as CFString, 1, nil) else {
            throw CocoaError(.fileWriteUnknown)
        }
        CGImageDestinationAddImage(dest, image, nil)
        guard CGImageDestinationFinalize(dest) else { throw CocoaError(.fileWriteUnknown) }
        return url
    }
}

// XCTAssertEqual(Int, Int, accuracy:) は無いので用意する。
private func XCTAssertEqual(_ lhs: Int, _ rhs: Int, accuracy: Int,
                            _ message: @autoclosure () -> String = "",
                            file: StaticString = #filePath, line: UInt = #line) {
    XCTAssertLessThanOrEqual(abs(lhs - rhs), accuracy,
                             message().isEmpty ? "\(lhs) と \(rhs) の差が \(accuracy) を超えた" : message(),
                             file: file, line: line)
}
