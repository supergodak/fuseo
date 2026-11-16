import XCTest
import CoreGraphics
import ImageIO
import PDFKit
@testable import MaskingCore

/// 0.4.0: 書き出しの透かし・大きさの上限・PDF のパスワード。
final class ExportExtrasTests: XCTestCase {

    // MARK: - 部品

    private func solid(_ w: Int, _ h: Int, gray: CGFloat = 1) -> CGImage {
        let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.setFillColor(CGColor(gray: gray, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: w, height: h))
        return ctx.makeImage()!
    }

    /// 写真のように圧縮しにくい画像（決まった乱数）。
    private func noisy(_ w: Int, _ h: Int) -> CGImage {
        let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        var state: UInt64 = 42
        func next() -> CGFloat { state = state &* 6364136223846793005 &+ 1442695040888963407; return CGFloat(state >> 40) / CGFloat(1 << 24) }
        ctx.setFillColor(CGColor(gray: 0.9, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: w, height: h))
        for _ in 0..<6000 {
            ctx.setFillColor(CGColor(red: next(), green: next(), blue: next(), alpha: 0.5))
            ctx.fill(CGRect(x: next() * CGFloat(w), y: next() * CGFloat(h), width: 6 + next() * 30, height: 4 + next() * 10))
        }
        return ctx.makeImage()!
    }

    /// 白でない画素（輝度 < limit/255）の数。区画を指定できる（左下原点の px）。
    private func darkPixels(_ image: CGImage, in rect: CGRect? = nil, limit: Int = 200) -> Int {
        let w = image.width, h = image.height
        var buf = [UInt8](repeating: 0, count: w * h * 4)
        let ctx = CGContext(data: &buf, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        let r = rect ?? CGRect(x: 0, y: 0, width: w, height: h)
        var n = 0
        for y in Int(r.minY)..<Int(r.maxY) {
            let row = (h - 1 - y) * w * 4          // バッファは上から
            for x in Int(r.minX)..<Int(r.maxX) {
                let i = row + x * 4
                if (Int(buf[i]) + Int(buf[i + 1]) + Int(buf[i + 2])) / 3 < limit { n += 1 }
            }
        }
        return n
    }

    private func tempURL(_ ext: String) -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("extras-\(UUID().uuidString).\(ext)")
        addTeardownBlock { try? FileManager.default.removeItem(at: url) }
        return url
    }

    private func page(_ image: CGImage, ocr: [OCRItem] = [], masks: [NormRect] = []) -> RenderedPage {
        RenderedPage(image: image, ocrItems: ocr, maskRects: masks)
    }

    // MARK: - 透かし

    func test_watermark_tiled_coversAllQuadrants() {
        let img = solid(1200, 800)
        let out = WatermarkRenderer.apply(Watermark(text: "テスト提出用", layout: .tiled, opacity: 0.4), to: img)
        for q in [CGRect(x: 0, y: 0, width: 600, height: 400), CGRect(x: 600, y: 0, width: 600, height: 400),
                  CGRect(x: 0, y: 400, width: 600, height: 400), CGRect(x: 600, y: 400, width: 600, height: 400)] {
            XCTAssertGreaterThan(darkPixels(out, in: q), 500, "4 分の 1 の区画 \(q) に透かしが無い")
        }
        XCTAssertEqual(darkPixels(img), 0, "入力の画像を変えてはいけない")
    }

    func test_watermark_diagonal_isCentered_andEmptyTextDoesNothing() {
        let img = solid(1200, 800)
        let out = WatermarkRenderer.apply(Watermark(text: "提出用", layout: .diagonal, opacity: 0.5), to: img)
        XCTAssertGreaterThan(darkPixels(out, in: CGRect(x: 450, y: 300, width: 300, height: 200)), 1000)
        XCTAssertEqual(darkPixels(out, in: CGRect(x: 0, y: 0, width: 120, height: 80)), 0, "隅には描かない")
        let blank = WatermarkRenderer.apply(Watermark(text: "   "), to: img)
        XCTAssertEqual(darkPixels(blank), 0)
    }

    func test_watermark_layout_tiledAngleAndDiagonalAngle() {
        let canvas = CGSize(width: 1000, height: 600)
        let d = WatermarkLayout.placements(layout: .diagonal, canvas: canvas, textSize: CGSize(width: 300, height: 50))
        XCTAssertEqual(d.count, 1)
        XCTAssertEqual(d[0].angle, atan2(600, 1000), accuracy: 1e-9)
        let t = WatermarkLayout.placements(layout: .tiled, canvas: canvas, textSize: CGSize(width: 200, height: 30))
        XCTAssertGreaterThan(t.count, 6)
        XCTAssertTrue(t.allSatisfy { abs($0.angle - WatermarkLayout.tiledAngle) < 1e-9 })
    }

    /// 透かしは画素にだけ入り、検索用のテキスト層には入らない。白黒の書き出しでも消えない。
    func test_watermark_inPDF_isPixelsOnly_andSurvivesBlackWhite() throws {
        let ocr = [OCRItem(text: "OKTEXT", box: NormRect(x: 0.1, y: 0.1, w: 0.3, h: 0.1), confidence: 1, upright: true)]
        let url = tempURL("pdf")
        try FileExporter().export([page(solid(1200, 800), ocr: ocr)],
                                  options: ExportOptions(format: .pdf, searchableText: true, colorMode: .blackWhite,
                                                         watermark: Watermark(text: "WMARKZ提出用")), to: url)
        let doc = try XCTUnwrap(PDFDocument(url: url))
        let text = doc.string ?? ""
        XCTAssertTrue(text.contains("OKTEXT"))
        XCTAssertFalse(text.contains("WMARKZ"), "透かしの文言がテキスト層に入っている")
        // PDF の画像に透かしの画素がある（白黒でも消えていない）
        let pdfPage = try XCTUnwrap(CGPDFDocument(url as CFURL)?.page(at: 1))
        let box = pdfPage.getBoxRect(.mediaBox)
        let ctx = try XCTUnwrap(CGContext(data: nil, width: Int(box.width), height: Int(box.height), bitsPerComponent: 8,
                                          bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                          bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        ctx.setFillColor(CGColor(gray: 1, alpha: 1)); ctx.fill(CGRect(origin: .zero, size: box.size))
        ctx.drawPDFPage(pdfPage)
        let img = try XCTUnwrap(ctx.makeImage())
        XCTAssertGreaterThan(darkPixels(img, limit: 235), 300, "既定の濃さ（0.25）の透かしが PDF の画像に無い")
    }

    // MARK: - 大きさの上限

    func test_sizeLimit_pdf_fitsAndKeepsPaperSize() throws {
        let img = noisy(2480, 3508)           // A4・300dpi 相当
        let free = tempURL("pdf")
        try FileExporter().export([page(img)], options: ExportOptions(format: .pdf), to: free)
        let limited = tempURL("pdf")
        let limit = 400_000
        try FileExporter().export([page(img), page(img)],
                                  options: ExportOptions(format: .pdf, maxBytes: limit), to: limited)
        let size = try Data(contentsOf: limited).count
        XCTAssertLessThanOrEqual(size, limit)
        let a = try XCTUnwrap(PDFDocument(url: free)?.page(at: 0)?.bounds(for: .mediaBox))
        let doc = try XCTUnwrap(PDFDocument(url: limited))
        XCTAssertEqual(doc.pageCount, 2)
        let b = try XCTUnwrap(doc.page(at: 0)?.bounds(for: .mediaBox))
        XCTAssertEqual(a.width, b.width, accuracy: 0.5, "紙の大きさが変わってはいけない")
        XCTAssertEqual(a.height, b.height, accuracy: 0.5)
        let latin = String(decoding: try Data(contentsOf: limited), as: UTF8.self)
        XCTAssertTrue(latin.contains("/DCTDecode"), "上限ありのときは JPEG で埋め込む")
    }

    func test_sizeLimit_jpeg_fits() throws {
        let url = tempURL("jpeg")
        try FileExporter().export([page(noisy(2000, 1300))],
                                  options: ExportOptions(format: .jpeg, jpegQuality: 1.0, maxBytes: 250_000), to: url)
        XCTAssertLessThanOrEqual(try Data(contentsOf: url).count, 250_000)
        let src = try XCTUnwrap(CGImageSourceCreateWithURL(url as CFURL, nil))
        let props = CGImageSourceCopyPropertiesAtIndex(src, 0, nil) as? [CFString: Any]
        let w = try XCTUnwrap(props?[kCGImagePropertyPixelWidth] as? Int)
        let h = try XCTUnwrap(props?[kCGImagePropertyPixelHeight] as? Int)
        XCTAssertGreaterThanOrEqual(min(w, h), FileExporter.minShortSidePx, "下限より小さくしない")
    }

    func test_sizeLimit_unreachable_throws_andWritesNothing() {
        let url = tempURL("pdf")
        XCTAssertThrowsError(try FileExporter().export([page(noisy(2480, 3508))],
                                                       options: ExportOptions(format: .pdf, maxBytes: 5_000), to: url)) { error in
            guard case MaskingError.sizeLimitUnreachable(let limit, let smallest) = error else {
                return XCTFail("想定外のエラー: \(error)")
            }
            XCTAssertEqual(limit, 5_000)
            XCTAssertGreaterThan(smallest, 5_000)
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path), "収まらないときはファイルを作らない")
    }

    func test_byteSizeLabel() {
        XCTAssertEqual(ByteSize.label(2_000_000), "2 MB")
        XCTAssertEqual(ByteSize.label(1_500_000), "1.5 MB")
        XCTAssertEqual(ByteSize.label(300_000), "300 KB")
    }

    // MARK: - パスワード

    func test_password_encrypts_andKeepsMaskExclusion() throws {
        let ocr = [OCRItem(text: "VISIBLEX", box: NormRect(x: 0.05, y: 0.6, w: 0.3, h: 0.1), confidence: 1, upright: true),
                   OCRItem(text: "SECRETX", box: NormRect(x: 0.05, y: 0.2, w: 0.3, h: 0.1), confidence: 1, upright: true)]
        let mask = NormRect(x: 0.0, y: 0.15, w: 0.5, h: 0.2)
        let url = tempURL("pdf")
        try FileExporter().export([page(solid(800, 500), ocr: ocr, masks: [mask])],
                                  options: ExportOptions(format: .pdf, searchableText: true, pdfPassword: "pass-1234"), to: url)
        let doc = try XCTUnwrap(PDFDocument(url: url))
        XCTAssertTrue(doc.isEncrypted)
        XCTAssertTrue(doc.isLocked)
        XCTAssertFalse(doc.unlock(withPassword: "wrong"))
        XCTAssertTrue(doc.unlock(withPassword: "pass-1234"))
        XCTAssertEqual(doc.pageCount, 1)
        let text = doc.string ?? ""
        XCTAssertTrue(text.contains("VISIBLEX"))
        XCTAssertFalse(text.contains("SECRETX"), "パスワード付きでも、塗った所の文字はテキスト層に入れない")
        // 文書情報は空の辞書（平文の Producer を書かない＝読む側で文字化けしない）
        var bytes = [UInt8](try Data(contentsOf: url))
        let before = bytes
        XCTAssertTrue(PDFInfoScrubber.scrub(&bytes, replacement: PDFInfoScrubber.emptyReplacement), "Info 辞書が見つかる形のまま")
        XCTAssertEqual(bytes, before, "すでに空の辞書になっている")
    }

    func test_password_withSizeLimit_bothApply() throws {
        let url = tempURL("pdf")
        try FileExporter().export([page(noisy(2480, 3508))],
                                  options: ExportOptions(format: .pdf, maxBytes: 600_000, pdfPassword: "abc"), to: url)
        XCTAssertLessThanOrEqual(try Data(contentsOf: url).count, 600_000)
        let doc = try XCTUnwrap(PDFDocument(url: url))
        XCTAssertTrue(doc.isLocked)
        XCTAssertTrue(doc.unlock(withPassword: "abc"))
    }

    func test_password_isIgnoredForImagesAndWhenEmpty() throws {
        XCTAssertNil(ExportOptions(format: .jpeg, pdfPassword: "x").effectivePDFPassword)
        XCTAssertNil(ExportOptions(format: .pdf, pdfPassword: "").effectivePDFPassword)
        let url = tempURL("pdf")
        try FileExporter().export([page(solid(400, 300))], options: ExportOptions(format: .pdf, pdfPassword: ""), to: url)
        XCTAssertFalse(try XCTUnwrap(PDFDocument(url: url)).isEncrypted)
    }

    /// macOS は ASCII 以外のパスワードで PDF を作れず、プレビューも開けない（実測）→ 半角だけ・32 文字まで。
    func test_password_rule_asciiOnly_upTo32() throws {
        XCTAssertTrue(ExportOptions.isValidPDFPassword("abc-1234 !~"))
        XCTAssertTrue(ExportOptions.isValidPDFPassword(String(repeating: "a", count: 32)))
        XCTAssertFalse(ExportOptions.isValidPDFPassword(String(repeating: "a", count: 33)))
        XCTAssertFalse(ExportOptions.isValidPDFPassword("pässwörd"))
        XCTAssertFalse(ExportOptions.isValidPDFPassword("パスワード"))
        XCTAssertFalse(ExportOptions.isValidPDFPassword("ａｂｃ"))
        XCTAssertFalse(ExportOptions.isValidPDFPassword(""))
        let url = tempURL("pdf")
        XCTAssertThrowsError(try FileExporter().export([page(solid(40, 30))],
                                                       options: ExportOptions(format: .pdf, pdfPassword: "日本語"), to: url))
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path))
    }
}
