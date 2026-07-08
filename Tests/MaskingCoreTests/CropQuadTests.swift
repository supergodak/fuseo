import XCTest
import CoreGraphics
import ImageIO
import UniformTypeIdentifiers
@testable import MaskingCore

/// 切り抜きの手動調整（wp5 §9.5）のコア契約:
/// - Quad の凸判定・fullImage
/// - CoordinateSpace.viewPoint（正変換）と normPoint の往復
/// - VisionRectifier の manualQuad パス（検出スキップ・指定四隅で台形補正）
final class CropQuadTests: XCTestCase {

    // MARK: - Quad

    func test_quad_fullImage_isConvex() {
        XCTAssertTrue(Quad.fullImage.isConvex)
    }

    func test_quad_selfIntersecting_isNotConvex() {
        // topRight と bottomRight を入れ替えた自己交差（砂時計型）
        let crossed = Quad(topLeft: CGPoint(x: 0, y: 1), topRight: CGPoint(x: 1, y: 0),
                           bottomRight: CGPoint(x: 1, y: 1), bottomLeft: CGPoint(x: 0, y: 0))
        XCTAssertFalse(crossed.isConvex)
    }

    func test_quad_collinearCorners_isNotConvex() {
        // 3点が一直線＝つぶれた四角形
        let flat = Quad(topLeft: CGPoint(x: 0, y: 0.5), topRight: CGPoint(x: 0.5, y: 0.5),
                        bottomRight: CGPoint(x: 1, y: 0.5), bottomLeft: CGPoint(x: 0, y: 0))
        XCTAssertFalse(flat.isConvex)
    }

    // MARK: - viewPoint / normPoint の往復

    func test_viewPoint_isInverseOfNormPoint() {
        let size = CGSize(width: 320, height: 200)
        let norm = CGPoint(x: 0.3, y: 0.75)
        let view = CoordinateSpace.viewPoint(norm, in: size)
        XCTAssertEqual(view.x, 96, accuracy: 1e-9)
        XCTAssertEqual(view.y, 50, accuracy: 1e-9)               // Yフリップ
        let back = CoordinateSpace.normPoint(fromViewPoint: view, in: size)
        XCTAssertEqual(back.x, norm.x, accuracy: 1e-9)
        XCTAssertEqual(back.y, norm.y, accuracy: 1e-9)
    }

    // MARK: - VisionRectifier.manualQuad（実CoreImageで検証）

    /// 単色の合成画像を一時ファイルへ書き出す（fixtures-private は使わない）。
    private func writeSyntheticPNG(width: Int, height: Int) throws -> URL {
        let ctx = CGContext(data: nil, width: width, height: height,
                            bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: width, height: height))
        let image = ctx.makeImage()!
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("crop-quad-\(UUID().uuidString).png")
        let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil)!
        CGImageDestinationAddImage(dest, image, nil)
        XCTAssertTrue(CGImageDestinationFinalize(dest))
        return url
    }

    func test_manualQuad_fullImage_keepsOriginalSize() throws {
        let url = try writeSyntheticPNG(width: 200, height: 100)
        defer { try? FileManager.default.removeItem(at: url) }
        let (page, _) = try VisionRectifier().rectifyKeepingOCR(imageAt: url, manualQuad: .fullImage)
        XCTAssertTrue(page.rectified)
        XCTAssertNil(page.quadConfidence, "手動指定に検出信頼度は付かない")
        XCTAssertEqual(Double(page.pixelSize.width), 200, accuracy: 2)
        XCTAssertEqual(Double(page.pixelSize.height), 100, accuracy: 2)
    }

    func test_manualQuad_leftHalf_cropsWidth() throws {
        let url = try writeSyntheticPNG(width: 200, height: 100)
        defer { try? FileManager.default.removeItem(at: url) }
        let leftHalf = Quad(topLeft: CGPoint(x: 0, y: 1), topRight: CGPoint(x: 0.5, y: 1),
                            bottomRight: CGPoint(x: 0.5, y: 0), bottomLeft: CGPoint(x: 0, y: 0))
        let (page, _) = try VisionRectifier().rectifyKeepingOCR(imageAt: url, manualQuad: leftHalf)
        // 200x100 の左半分 = 100x100（正立化は正方形なので回転しても同寸）
        XCTAssertEqual(Double(page.pixelSize.width), 100, accuracy: 2)
        XCTAssertEqual(Double(page.pixelSize.height), 100, accuracy: 2)
    }

    func test_pipeline_plumbsManualQuad() throws {
        let url = try writeSyntheticPNG(width: 200, height: 100)
        defer { try? FileManager.default.removeItem(at: url) }
        let leftHalf = Quad(topLeft: CGPoint(x: 0, y: 1), topRight: CGPoint(x: 0.5, y: 1),
                            bottomRight: CGPoint(x: 0.5, y: 0), bottomLeft: CGPoint(x: 0, y: 0))
        let pipeline = try MaskingPipeline()
        let analyzed = try pipeline.analyze(url: url, manualQuad: leftHalf)
        XCTAssertEqual(Double(analyzed.page.pixelSize.width), 100, accuracy: 2)
        XCTAssertTrue(analyzed.page.rectified)
        XCTAssertEqual(analyzed.classification.type, .generic, "文字なし合成画像は generic")
    }

    func test_cropPreview_returnsOriginalImage() throws {
        let url = try writeSyntheticPNG(width: 200, height: 100)
        defer { try? FileManager.default.removeItem(at: url) }
        let preview = try VisionRectifier().cropPreview(imageAt: url)
        XCTAssertEqual(preview.originalImage.width, 200)
        XCTAssertEqual(preview.originalImage.height, 100)
        // 単色画像では書類が検出されない見込みだが、環境差があるため detectedQuad の有無は断定しない
    }
}
