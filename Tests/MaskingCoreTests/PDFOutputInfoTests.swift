import XCTest
import CoreGraphics
import PDFKit
@testable import MaskingCore

/// Mac 0.2.4: 書き出し PDF の紙の大きさと文書情報。
/// ① PDF から取り込んだページが元の紙の大きさで出る（0.2.3 までは 2 倍） ② /Info は Producer (Fuseo) だけで、
/// macOS の版・作成/更新日時が残らない ③ 書き換え後も PDF として正しく開け、検索用テキスト層も生きている。
final class PDFOutputInfoTests: XCTestCase {

    private func white(_ w: Int, _ h: Int) -> CGImage {
        let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: w, height: h))
        return ctx.makeImage()!
    }

    private func tempURL() -> URL {
        FileManager.default.temporaryDirectory.appendingPathComponent("pdfinfo-\(UUID().uuidString).pdf")
    }

    private func export(_ pages: [RenderedPage], searchable: Bool) throws -> URL {
        let url = tempURL()
        addTeardownBlock { try? FileManager.default.removeItem(at: url) }
        try FileExporter().export(pages, options: ExportOptions(format: .pdf, searchableText: searchable), to: url)
        return url
    }

    func test_info_isOnlyProducerFuseo_noOSVersionOrDates() throws {
        for searchable in [false, true] {
            let page = RenderedPage(image: white(400, 200),
                                    ocrItems: [OCRItem(text: "HELLO", box: NormRect(x: 0.05, y: 0.4, w: 0.3, h: 0.2),
                                                       confidence: 1, upright: true)],
                                    maskRects: [])
            let url = try export([page, page], searchable: searchable)
            let bytes = try Data(contentsOf: url)
            let latin = String(decoding: bytes, as: UTF8.self)
            for word in ["macOS", "Quartz", "CreationDate", "ModDate", "Build", "/Creator", "/Metadata"] {
                XCTAssertFalse(latin.contains(word), "PDF に \(word) が残っている（searchable=\(searchable)）")
            }
            XCTAssertTrue(latin.contains("/Producer (Fuseo)"))

            let doc = try XCTUnwrap(PDFDocument(url: url), "書き換え後も開ける")
            XCTAssertEqual(doc.pageCount, 2)
            let attrs = doc.documentAttributes ?? [:]
            XCTAssertNil(attrs[PDFDocumentAttribute.creationDateAttribute])
            XCTAssertNil(attrs[PDFDocumentAttribute.modificationDateAttribute])
            XCTAssertEqual(attrs[PDFDocumentAttribute.producerAttribute] as? String, "Fuseo")
            if searchable {
                XCTAssertTrue((doc.string ?? "").contains("HELLO"), "テキスト層が壊れていない")
            }
            // CoreGraphics でも開ける（xref のオフセットが有効）
            let cg = try XCTUnwrap(CGPDFDocument(url as CFURL))
            XCTAssertEqual(cg.numberOfPages, 2)
        }
    }

    func test_pageSize_is300dpi_soA4RasterComesOutA4() throws {
        // A4 を 300dpi で描いた大きさ（PDFRasterizer の既定と同じ）
        let url = try export([RenderedPage(image: white(2480, 3508), ocrItems: [], maskRects: [])], searchable: false)
        let box = try XCTUnwrap(PDFDocument(url: url)?.page(at: 0)?.bounds(for: .mediaBox))
        XCTAssertEqual(box.width, 595.2, accuracy: 0.5)
        XCTAssertEqual(box.height, 841.9, accuracy: 0.5)
    }

    /// 取り込み（PDFRasterizer 既定）→ 書き出しで元の紙の大きさに戻る。
    func test_pdfRoundTrip_keepsOriginalPaperSize() throws {
        let src = tempURL()
        addTeardownBlock { try? FileManager.default.removeItem(at: src) }
        var media = CGRect(x: 0, y: 0, width: 595, height: 842)
        let ctx = try XCTUnwrap(CGContext(src as CFURL, mediaBox: &media, nil))
        ctx.beginPDFPage(nil); ctx.setFillColor(gray: 1, alpha: 1); ctx.fill(media); ctx.endPDFPage(); ctx.closePDF()

        var raster: CGImage?
        try PDFRasterizer().rasterize(url: src) { raster = $0.cgImage }
        let out = try export([RenderedPage(image: try XCTUnwrap(raster), ocrItems: [], maskRects: [])], searchable: false)
        let box = try XCTUnwrap(PDFDocument(url: out)?.page(at: 0)?.bounds(for: .mediaBox))
        XCTAssertEqual(box.width, 595, accuracy: 0.5)
        XCTAssertEqual(box.height, 842, accuracy: 0.5)
    }

    // MARK: - 書き換え処理そのもの

    func test_scrubber_handlesEscapedParensNestedDictAndKeepsLength() {
        let pdf = """
        %PDF-1.3
        1 0 obj
        << /Type /Catalog >>
        endobj
        6 0 obj
        << /Producer (macOS \\(Build 1\\) (nested) Quartz) /Sub << /A <414243> >> /CreationDate (D:2026) >>
        endobj
        trailer
        << /Size 7 /Root 1 0 R /Info 6 0 R >>
        %%EOF
        """
        var bytes = Array(pdf.utf8)
        let before = bytes.count
        XCTAssertTrue(PDFInfoScrubber.scrub(&bytes))
        XCTAssertEqual(bytes.count, before, "長さが変わらない（xref が有効なまま）")
        let after = String(decoding: bytes, as: UTF8.self)
        XCTAssertTrue(after.contains("6 0 obj\n<< /Producer (Fuseo) >> "))
        XCTAssertFalse(after.contains("macOS"))
        XCTAssertFalse(after.contains("CreationDate"))
        XCTAssertTrue(after.contains("\nendobj\ntrailer"), "辞書の外は変えない")
        XCTAssertTrue(after.contains("<< /Type /Catalog >>"))
    }

    func test_scrubber_unexpectedShape_returnsFalseAndLeavesBytes() {
        for pdf in ["%PDF-1.3\ntrailer\n<< /Size 1 >>\n%%EOF",                       // Info なし
                    "%PDF-1.3\ntrailer\n<< /Info 9 0 R >>\n%%EOF",                    // 本体なし
                    "%PDF-1.3\n6 0 obj\n<< /P (x) >>\nendobj\ntrailer\n<< /Info 6 0 R >>"] {  // 短すぎる
            var bytes = Array(pdf.utf8)
            XCTAssertFalse(PDFInfoScrubber.scrub(&bytes))
            XCTAssertEqual(bytes, Array(pdf.utf8))
        }
    }
}
