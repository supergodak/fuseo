import XCTest
import CoreGraphics
import CoreText
import ImageIO
import UniformTypeIdentifiers
@testable import MaskingCore

/// 0.3.0: 紙の大きさの PDF ページをカード類と判定しない（文字入れの検証で、「個人番号」欄のある A4 の申告書が
/// マイナンバーカード（裏）になり、位置推定の大きなマスクが付いた）。合成画像のみ。
final class FlatPageCardGateTests: XCTestCase {

    func test_allowsCardTypes_truthTable() {
        let a4 = CGSize(width: 2480, height: 3508), a4Landscape = CGSize(width: 3508, height: 2480)
        let letter = CGSize(width: 2550, height: 3300), card = CGSize(width: 1712, height: 1080)
        XCTAssertFalse(MaskingPipeline.allowsCardTypes(options: .flatPage, pixelSize: a4))
        XCTAssertFalse(MaskingPipeline.allowsCardTypes(options: .flatPage, pixelSize: a4Landscape))
        XCTAssertFalse(MaskingPipeline.allowsCardTypes(options: .flatPage, pixelSize: letter))
        XCTAssertTrue(MaskingPipeline.allowsCardTypes(options: .flatPage, pixelSize: card), "ページの中のカードを切り抜いた")
        XCTAssertTrue(MaskingPipeline.allowsCardTypes(options: .default, pixelSize: a4), "写真は従来どおり")
        XCTAssertTrue(MaskingPipeline.allowsCardTypes(options: .restored, pixelSize: a4), "復元ページは従来どおり")
    }

    func test_classifier_allowCardsFalse_skipsCardTypes() throws {
        let presets = try PresetStore.loadAll()
        let ocr = [OCRItem(text: "個人番号", box: NormRect(x: 0.1, y: 0.8, w: 0.2, h: 0.03), confidence: 1, upright: true)]
        XCTAssertEqual(KeywordClassifier().classify(ocr: ocr, presets: presets).type, .myNumberCardBack, "前提: 従来はカード裏")
        let gated = KeywordClassifier().classify(ocr: ocr, presets: presets, allowCards: false)
        XCTAssertFalse(gated.type.isID1Card)
        XCTAssertEqual(gated.ranking.first?.type, .myNumberCardBack, "ranking は生スコアのまま（種別変更の候補用）")
    }

    /// 白地に見出しだけの紙を描いて PNG にする。
    private func makePage(size: CGSize, labels: [(String, CGFloat, CGFloat)], fontSize: CGFloat) throws -> URL {
        let ctx = CGContext(data: nil, width: Int(size.width), height: Int(size.height), bitsPerComponent: 8,
                            bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        ctx.fill(CGRect(origin: .zero, size: size))
        let font = CTFontCreateWithName("HiraginoSans-W6" as CFString, fontSize, nil)
        for (s, x, yTop) in labels {
            let line = CTLineCreateWithAttributedString(NSAttributedString(
                string: s, attributes: [NSAttributedString.Key(kCTFontAttributeName as String): font]))
            ctx.textPosition = CGPoint(x: x * size.width, y: (1 - yTop) * size.height)
            CTLineDraw(line, ctx)
        }
        ctx.setStrokeColor(CGColor(red: 0, green: 0, blue: 0, alpha: 1)); ctx.setLineWidth(4)
        ctx.stroke(CGRect(x: size.width * 0.1, y: size.height * 0.7, width: size.width * 0.8, height: size.height * 0.05))
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("gate-\(UUID().uuidString).png")
        let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil)!
        CGImageDestinationAddImage(dest, ctx.makeImage()!, nil)
        XCTAssertTrue(CGImageDestinationFinalize(dest))
        addTeardownBlock { try? FileManager.default.removeItem(at: url) }
        return url
    }

    func test_pipeline_a4FormWithMyNumberLabel_isNotACard() throws {
        let url = try makePage(size: CGSize(width: 2480, height: 3508),
                               labels: [("申告書（見本）", 0.1, 0.06), ("氏名", 0.1, 0.15), ("個人番号", 0.1, 0.21),
                                        ("住所", 0.1, 0.27), ("郵便番号", 0.1, 0.35)],
                               fontSize: 48)
        let page = try MaskingPipeline().analyze(url: url, options: .flatPage)
        XCTAssertEqual(page.classification.ranking.first?.type, .myNumberCardBack,
                       "前提: 見出しを読めていて、ゲートが無ければカード裏になる")
        XCTAssertFalse(page.preset.documentType.isID1Card, "A4 の申告書がカード（\(page.preset.documentType)）になった")
        XCTAssertFalse(page.candidates.contains { $0.source == .fixedRegion }, "カード用の位置推定マスクが付いた")
    }
}
