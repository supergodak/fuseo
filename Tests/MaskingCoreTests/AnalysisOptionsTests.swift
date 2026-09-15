import CoreGraphics
import CoreImage
import CoreText
import Foundation
import ImageIO
import UniformTypeIdentifiers
import XCTest
@testable import MaskingCore

/// `AnalysisOptions`（正立判定・文字認識の省略）。合成画像のみ使用。
final class AnalysisOptionsTests: XCTestCase {

    // MARK: - 正立判定の省略

    /// 横長キャンバスに 90° 回転した文字を描く。既定（正立判定あり）なら縦長に直され、
    /// `.flatPage` なら入力の向きのまま返る。
    func test_flatPage_keepsSourceOrientation_whileDefaultUprights() throws {
        let url = try Self.writePNG(Self.rotatedTextImage(width: 900, height: 500), name: "rot")
        defer { try? FileManager.default.removeItem(at: url) }
        let pipeline = try MaskingPipeline()

        let flat = try pipeline.analyze(url: url, manualQuad: .fullImage, options: .flatPage)
        XCTAssertEqual(Int(flat.page.pixelSize.width), 900, "flatPage は向きを変えない")
        XCTAssertEqual(Int(flat.page.pixelSize.height), 500)

        let auto = try pipeline.analyze(url: url, manualQuad: .fullImage)
        XCTAssertEqual(Int(auto.page.pixelSize.width), 500, "既定は正立判定で縦長に直す")
        XCTAssertEqual(Int(auto.page.pixelSize.height), 900)
    }

    // MARK: - 文字認識の省略

    /// 検査用数字が成立する 12 桁と QR を描いたページ。
    /// 既定: 個人番号が候補に出る。`.flatPageWithoutText`: OCR 空・種別 generic・番号候補なし・QR 候補あり。
    func test_withoutText_skipsOCRButKeepsImageDetectors() throws {
        let myNumber = try XCTUnwrap(Self.validMyNumber())
        let url = try Self.writePNG(Self.numberAndQRImage(number: Self.grouped(myNumber)), name: "qr")
        defer { try? FileManager.default.removeItem(at: url) }
        let pipeline = try MaskingPipeline()

        let withText = try pipeline.analyze(url: url, manualQuad: .fullImage, options: .flatPage)
        XCTAssertFalse(withText.ocr.isEmpty, "OCR ありなら観測がある")
        XCTAssertTrue(withText.candidates.contains { $0.source == .detector(.myNumber12) },
                      "OCR ありなら個人番号が候補に出る（候補: \(withText.candidates.map(\.label))）")

        let noText = try pipeline.analyze(url: url, manualQuad: .fullImage, options: .flatPageWithoutText)
        XCTAssertTrue(noText.ocr.isEmpty, "OCR なしなら観測は空")
        XCTAssertEqual(noText.classification.type, .generic, "OCR なしは種別判定できず generic")
        XCTAssertFalse(noText.candidates.contains { $0.source == .detector(.myNumber12) },
                       "OCR なしでは番号系の候補は出ない")
        XCTAssertTrue(noText.candidates.contains { $0.source == .detector(.qrBarcode) },
                      "画像ベースの QR 検出は OCR なしでも動く（候補: \(noText.candidates.map(\.label))）")
        // 画像そのものは同じ寸法で得られている（手動マスク・書き出しに使える）
        XCTAssertEqual(noText.page.pixelSize, withText.page.pixelSize)
    }

    func test_withoutText_exportHasNoTextLayer() throws {
        let url = try Self.writePNG(Self.numberAndQRImage(number: "1234 5678 9018"), name: "noocr")
        defer { try? FileManager.default.removeItem(at: url) }
        let analyzed = try MaskingPipeline().analyze(url: url, manualQuad: .fullImage,
                                                     options: .flatPageWithoutText)
        let out = FileManager.default.temporaryDirectory.appendingPathComponent("noocr-\(UUID().uuidString).pdf")
        defer { try? FileManager.default.removeItem(at: out) }
        let rendered = RenderedPage(image: analyzed.page.cgImage, ocrItems: analyzed.ocr,
                                    maskRects: analyzed.effectiveMaskRects)
        try FileExporter().export([rendered], options: ExportOptions(format: .pdf, searchableText: true), to: out)
        let text = PDFKitBridge.string(of: out) ?? ""
        XCTAssertTrue(text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                      "OCR なしのページには検索可能テキスト層が付かない")
    }

    // MARK: - 合成画像

    /// 白地に、90° 回転した大きな文字を描く（正立判定が縦長を選ぶ入力）。
    private static func rotatedTextImage(width: Int, height: Int) -> CGImage {
        let ctx = makeContext(width: width, height: height)
        ctx.saveGState()
        // キャンバス中央で 90° 回転し、横方向に長い文章を「縦に」描く
        ctx.translateBy(x: CGFloat(width) / 2, y: CGFloat(height) / 2)
        ctx.rotate(by: .pi / 2)
        drawLines(["HELLO WORLD 12345", "REDACT FIRST THEN SUBMIT", "MASKING CORE UPRIGHT TEST"],
                  in: ctx, origin: CGPoint(x: -220, y: 40), fontSize: 30, lineGap: 44)
        ctx.restoreGState()
        return ctx.makeImage()!
    }

    /// 白地に 12 桁（4 桁区切り）と QR コードを描く。
    private static func numberAndQRImage(number: String) -> CGImage {
        let width = 1200, height = 800
        let ctx = makeContext(width: width, height: height)
        drawLines(["Notice of assignment", number], in: ctx,
                  origin: CGPoint(x: 80, y: 640), fontSize: 48, lineGap: 80)
        if let qr = qrImage(message: "https://fuseo.ati-mirai.co.jp/", sidePx: 320) {
            ctx.draw(qr, in: CGRect(x: 780, y: 120, width: 320, height: 320))
        }
        return ctx.makeImage()!
    }

    private static func makeContext(width: Int, height: Int) -> CGContext {
        let ctx = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: width, height: height))
        return ctx
    }

    private static func drawLines(_ lines: [String], in ctx: CGContext, origin: CGPoint,
                                  fontSize: CGFloat, lineGap: CGFloat) {
        let font = CTFontCreateWithName("Helvetica" as CFString, fontSize, nil)
        for (i, text) in lines.enumerated() {
            let attributed = NSAttributedString(string: text, attributes: [
                .font: font, .foregroundColor: CGColor(red: 0, green: 0, blue: 0, alpha: 1),
            ])
            ctx.textPosition = CGPoint(x: origin.x, y: origin.y - CGFloat(i) * lineGap)
            CTLineDraw(CTLineCreateWithAttributedString(attributed), ctx)
        }
    }

    private static func qrImage(message: String, sidePx: Int) -> CGImage? {
        guard let filter = CIFilter(name: "CIQRCodeGenerator") else { return nil }
        filter.setValue(Data(message.utf8), forKey: "inputMessage")
        filter.setValue("M", forKey: "inputCorrectionLevel")
        guard let small = filter.outputImage else { return nil }
        let scale = CGFloat(sidePx) / small.extent.width
        let scaled = small.transformed(by: CGAffineTransform(scaleX: scale, y: scale))
        return CIContext().createCGImage(scaled, from: scaled.extent)
    }

    private static func writePNG(_ image: CGImage, name: String) throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("opts-\(name)-\(UUID().uuidString).png")
        guard let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil) else {
            throw CocoaError(.fileWriteUnknown)
        }
        CGImageDestinationAddImage(dest, image, nil)
        guard CGImageDestinationFinalize(dest) else { throw CocoaError(.fileWriteUnknown) }
        return url
    }

    private static func validMyNumber() -> String? {
        for seed in 200_000_000_00...200_000_010_00 {
            let body = String(format: "%011ld", seed)   // Int は 64bit なので %ld（%d だと下位32bitに切られる）
            for check in 0...9 where Checkdigits.isValidMyNumber(body + String(check)) {
                return body + String(check)
            }
        }
        return nil
    }

    private static func grouped(_ digits: String) -> String {
        stride(from: 0, to: digits.count, by: 4).map { offset -> String in
            let start = digits.index(digits.startIndex, offsetBy: offset)
            let end = digits.index(start, offsetBy: min(4, digits.count - offset))
            return String(digits[start..<end])
        }.joined(separator: " ")
    }
}

/// PDFKit を直接 import せずに文字列を取り出す薄いブリッジ（テスト専用）。
private enum PDFKitBridge {
    static func string(of url: URL) -> String? {
        guard let doc = CGPDFDocument(url as CFURL), doc.numberOfPages > 0 else { return nil }
        // CGPDF はテキスト抽出 API を持たないため PDFKit を使う。
        return PDFDocumentText.string(url)
    }
}

import PDFKit
private enum PDFDocumentText {
    static func string(_ url: URL) -> String? { PDFDocument(url: url)?.string }
}

// MARK: - 書類検出の採否（純関数・実測値ベース）

final class DocumentDetectionPolicyTests: XCTestCase {
    /// 2026-09-16 実測: 充電器の写真 conf=0.00/area=0.01、PDF内カード 0.94/0.05、文字だけのA4 0.55/0.93、
    /// 実書類（PoC 6枚）0.86〜0.99 / 0.15〜0.39。
    func test_auto_rejectsNoDocumentPhotoButAcceptsRealDocuments() {
        let auto = AnalysisOptions.default
        XCTAssertFalse(auto.acceptsDocumentQuad(confidence: 0.00, area: 0.01), "書類が無い写真の低信頼・極小観測は不採用")
        XCTAssertTrue(auto.acceptsDocumentQuad(confidence: 0.86, area: 0.15), "斜め撮影のカード")
        XCTAssertTrue(auto.acceptsDocumentQuad(confidence: 0.99, area: 0.39), "正面のカード")
        XCTAssertTrue(auto.acceptsDocumentQuad(confidence: 0.55, area: 0.93), "写真では大きな観測も採用してよい")
        XCTAssertFalse(auto.acceptsDocumentQuad(confidence: 0.00, area: 0.98), "信頼度ゼロは面積が大きくても不採用")
    }

    func test_insetOnly_cropsSmallDocumentInsidePageButKeepsTextPage() {
        let flat = AnalysisOptions.flatPage
        XCTAssertEqual(flat.documentDetection, .insetOnly)
        XCTAssertTrue(flat.acceptsDocumentQuad(confidence: 0.94, area: 0.05), "白いページ中央の小さなカードは切り抜く")
        XCTAssertFalse(flat.acceptsDocumentQuad(confidence: 0.55, area: 0.93), "文字だけの A4 はページそのものが書類＝切り抜かない")
        XCTAssertFalse(flat.acceptsDocumentQuad(confidence: 0.00, area: 0.01), "低信頼は不採用")
        XCTAssertFalse(flat.acceptsDocumentQuad(confidence: 0.99, area: 0.90), "上限（0.85）を超える観測は切り抜かない")
    }

    func test_off_neverAccepts() {
        XCTAssertFalse(AnalysisOptions.restored.acceptsDocumentQuad(confidence: 0.99, area: 0.30))
        XCTAssertEqual(AnalysisOptions.restored.documentDetection, .off)
    }

    func test_decode_missingKeysFallBackToDefaults() throws {
        let legacy = Data(#"{"detectUpright":false,"recognizeText":true}"#.utf8)
        let opts = try JSONDecoder().decode(AnalysisOptions.self, from: legacy)
        XCTAssertEqual(opts.documentDetection, .auto)
        XCTAssertEqual(opts.insetMaxArea, 0.85, accuracy: 0.0001)
        XCTAssertFalse(opts.detectUpright)
    }
}
