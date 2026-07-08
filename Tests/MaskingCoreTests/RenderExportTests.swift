import XCTest
import CoreGraphics
import ImageIO
import PDFKit
@testable import MaskingCore

/// WP-4: 墨消しの正しさの自動検証（core-design.md §4・設計書§1.4）。
/// ①マスク下のピクセルが完全不透明の黒に置換される ②出力ファイルにメタデータが残らない
/// ③検索可能PDFのテキスト層からマスク交差の文字列が除外される — の3点を監視する。
final class RenderExportTests: XCTestCase {

    // MARK: - ヘルパ

    private func solidImage(r: CGFloat, g: CGFloat, b: CGFloat, width: Int = 100, height: Int = 100) -> CGImage {
        let ctx = CGContext(data: nil, width: width, height: height,
                            bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.setFillColor(CGColor(red: r, green: g, blue: b, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: width, height: height))
        return ctx.makeImage()!
    }

    /// 指定ラスタ座標（左上原点・行/列）の RGBA を読む。
    private func pixel(_ image: CGImage, x: Int, y: Int) -> (r: UInt8, g: UInt8, b: UInt8, a: UInt8) {
        let w = image.width, h = image.height
        var data = [UInt8](repeating: 0, count: w * h * 4)
        let ctx = CGContext(data: &data, width: w, height: h,
                            bitsPerComponent: 8, bytesPerRow: w * 4,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        let i = (y * w + x) * 4
        return (data[i], data[i + 1], data[i + 2], data[i + 3])
    }

    private func page(of image: CGImage) -> PageImage {
        PageImage(cgImage: image, sourceURL: nil, rectified: true, quadConfidence: 1)
    }

    private func tempURL(_ ext: String) -> URL {
        FileManager.default.temporaryDirectory
            .appendingPathComponent("fuseo-test-\(UUID().uuidString).\(ext)")
    }

    // MARK: - ① 焼き込み

    func test_burnIn_replacesMaskedPixelsWithOpaqueBlack() throws {
        let red = solidImage(r: 1, g: 0, b: 0)
        let out = try RasterMaskRenderer().burnIn(
            page: page(of: red),
            masks: [NormRect(x: 0.25, y: 0.25, w: 0.5, h: 0.5)],   // 中央50%
            strokes: [])

        let center = pixel(out, x: 50, y: 50)
        XCTAssertEqual(center.r, 0); XCTAssertEqual(center.g, 0); XCTAssertEqual(center.b, 0)
        XCTAssertEqual(center.a, 255, "マスクは完全不透明であること")

        let corner = pixel(out, x: 5, y: 5)                        // マスク外
        XCTAssertGreaterThan(corner.r, 200, "マスク外は元の赤のまま")

        // 入力画像は不変（確認UIのやり直しが効く）
        let original = pixel(red, x: 50, y: 50)
        XCTAssertGreaterThan(original.r, 200)
    }

    func test_burnIn_brushStrokeIsOpaque() throws {
        let red = solidImage(r: 1, g: 0, b: 0)
        let stroke = BrushStroke(points: [CGPoint(x: 0.1, y: 0.5), CGPoint(x: 0.9, y: 0.5)], width: 0.1)
        let out = try RasterMaskRenderer().burnIn(page: page(of: red), masks: [], strokes: [stroke])

        let onPath = pixel(out, x: 50, y: 50)                      // 線上（y=0.5）
        XCTAssertEqual(onPath.r, 0); XCTAssertEqual(onPath.a, 255)
        let offPath = pixel(out, x: 50, y: 10)                     // 線から離れた位置
        XCTAssertGreaterThan(offPath.r, 200)
    }

    // MARK: - ② メタデータ

    // MARK: - カラーモード（v1.1・墨消し後フィルタ）

    /// 赤地に黒マスクを焼き込み → 各カラーモードで PNG 書き出し → ピクセル検証。
    private func exportPixels(colorMode: ExportOptions.ColorMode) throws -> (masked: (r: UInt8, g: UInt8, b: UInt8, a: UInt8),
                                                                             outside: (r: UInt8, g: UInt8, b: UInt8, a: UInt8)) {
        let red = solidImage(r: 0.9, g: 0.2, b: 0.1)
        let mask = NormRect(x: 0.3, y: 0.3, w: 0.4, h: 0.4)
        let burned = try RasterMaskRenderer().burnIn(page: page(of: red), masks: [mask], strokes: [])
        let url = tempURL("png")
        defer { try? FileManager.default.removeItem(at: url) }
        try FileExporter().export([RenderedPage(image: burned, ocrItems: [], maskRects: [mask])],
                                  options: ExportOptions(format: .png, colorMode: colorMode), to: url)
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil),
              let out = CGImageSourceCreateImageAtIndex(src, 0, nil) else {
            throw MaskingError.loadFailed(url.path)
        }
        return (pixel(out, x: 50, y: 50), pixel(out, x: 5, y: 5))
    }

    func test_colorMode_grayscale_keepsMaskBlack() throws {
        let (masked, outside) = try exportPixels(colorMode: .grayscale)
        XCTAssertLessThan(masked.r, 10, "マスクの黒は黒のまま")
        XCTAssertLessThan(masked.g, 10)
        XCTAssertLessThan(masked.b, 10)
        // 赤 → 無彩色（R≈G≈B）
        XCTAssertLessThan(abs(Int(outside.r) - Int(outside.g)), 8, "グレースケール化されること")
        XCTAssertLessThan(abs(Int(outside.g) - Int(outside.b)), 8)
    }

    func test_colorMode_blackWhite_binarizesAndKeepsMaskBlack() throws {
        let (masked, outside) = try exportPixels(colorMode: .blackWhite)
        XCTAssertLessThan(masked.r, 10, "マスクの黒は黒のまま")
        // 二値化: 各画素は 0 近傍か 255 近傍のどちらか
        for v in [outside.r, outside.g, outside.b] {
            XCTAssertTrue(v < 10 || v > 245, "二値化されること（実測値=\(v)）")
        }
    }

    func test_colorMode_color_isUnchanged() throws {
        let (masked, outside) = try exportPixels(colorMode: .color)
        XCTAssertLessThan(masked.r, 10)
        XCTAssertGreaterThan(outside.r, 200, "カラーのまま（赤が残る）")
        XCTAssertLessThan(outside.b, 60)
    }

    func test_jpegExport_containsNoSensitiveMetadata() throws {
        let url = tempURL("jpg")
        defer { try? FileManager.default.removeItem(at: url) }
        let rendered = RenderedPage(image: solidImage(r: 0, g: 1, b: 0), ocrItems: [], maskRects: [])
        try FileExporter().export([rendered], options: ExportOptions(format: .jpeg), to: url)

        let source = try XCTUnwrap(CGImageSourceCreateWithURL(url as CFURL, nil))
        let props = try XCTUnwrap(CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any])

        // GPS・TIFF（撮影機材/日時/シリアル等）は存在しないこと
        XCTAssertNil(props[kCGImagePropertyGPSDictionary], "GPSを出力しないこと")
        XCTAssertNil(props[kCGImagePropertyTIFFDictionary], "TIFF(撮影機材/日時等)を出力しないこと")

        // Exif は ImageIO が寸法情報を必ず自動生成する（ColorSpace/PixelX/YDimension のみ＝プライバシー無関係）。
        // 許可リスト方式で「それ以外のExifキーが1つでも書かれたら失敗」にする（撮影日時・レンズ・
        // サムネイル等が将来の変更で混入しないことの監視）。
        if let exif = props[kCGImagePropertyExifDictionary] as? [CFString: Any] {
            let allowed: Set<CFString> = [kCGImagePropertyExifColorSpace,
                                          kCGImagePropertyExifPixelXDimension,
                                          kCGImagePropertyExifPixelYDimension]
            let unexpected = Set(exif.keys).subtracting(allowed)
            XCTAssertTrue(unexpected.isEmpty, "許可外のExifキーが出力された: \(unexpected)")
        }
    }

    // MARK: - ③ PDFテキスト層

    func test_pdf_excludesMaskedTextFromSearchableLayer() throws {
        let url = tempURL("pdf")
        defer { try? FileManager.default.removeItem(at: url) }

        let image = solidImage(r: 1, g: 1, b: 1, width: 400, height: 200)
        let visible = OCRItem(text: "HELLO", box: NormRect(x: 0.05, y: 0.4, w: 0.3, h: 0.2),
                              confidence: 1, upright: true)
        let secret = OCRItem(text: "SECRET", box: NormRect(x: 0.6, y: 0.4, w: 0.3, h: 0.2),
                             confidence: 1, upright: true)
        let mask = NormRect(x: 0.55, y: 0.3, w: 0.4, h: 0.4)       // secret と交差・hello と非交差
        let rendered = RenderedPage(image: image, ocrItems: [visible, secret], maskRects: [mask])

        try FileExporter().export([rendered],
                                  options: ExportOptions(format: .pdf, searchableText: true), to: url)

        let text = try XCTUnwrap(PDFDocument(url: url)?.string)
        XCTAssertTrue(text.contains("HELLO"), "マスク外のテキストは検索可能であること")
        XCTAssertFalse(text.contains("SECRET"), "マスク交差のテキストはPDFに存在しないこと（部分交差も除外）")
    }

    func test_pdf_withoutSearchableText_hasNoTextLayer() throws {
        let url = tempURL("pdf")
        defer { try? FileManager.default.removeItem(at: url) }
        let rendered = RenderedPage(image: solidImage(r: 1, g: 1, b: 1),
                                    ocrItems: [OCRItem(text: "HELLO", box: NormRect(x: 0.1, y: 0.4, w: 0.5, h: 0.2),
                                                       confidence: 1, upright: true)],
                                    maskRects: [])
        try FileExporter().export([rendered],
                                  options: ExportOptions(format: .pdf, searchableText: false), to: url)
        let text = PDFDocument(url: url)?.string ?? ""
        XCTAssertFalse(text.contains("HELLO"), "searchableText=false ではテキスト層を作らない")
    }

    func test_export_rejectsEmptyAndMultiPageImages() {
        XCTAssertThrowsError(try FileExporter().export([], options: ExportOptions(format: .pdf),
                                                       to: tempURL("pdf")))
        let p = RenderedPage(image: solidImage(r: 0, g: 0, b: 1), ocrItems: [], maskRects: [])
        XCTAssertThrowsError(try FileExporter().export([p, p], options: ExportOptions(format: .jpeg),
                                                       to: tempURL("jpg")),
                             "画像形式は1ページのみ（複数ページはPDF）")
    }
}
