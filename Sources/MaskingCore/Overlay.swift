import Foundation
import CoreGraphics
import ImageIO
import UniformTypeIdentifiers

// 目視評価用のオーバレイ描画（pocハーネス・回帰確認用。製品UIはSwiftUI側で描く）。
// CGContext は左下原点＝内部 NormRect と同じ向きなので CoordinateSpace.pixelRect の単純スケールで描ける。

public enum Overlay {

    /// 基準画像に OCR枠(黄)・マスク候補(ON=赤塗り / OFF=橙枠) を重ねる。
    public static func render(base: CGImage, ocr: [OCRItem], candidates: [MaskCandidate]) -> CGImage? {
        let w = base.width, h = base.height
        let size = CGSize(width: w, height: h)
        guard let ctx = CGContext(data: nil, width: w, height: h,
                                  bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.draw(base, in: CGRect(origin: .zero, size: size))

        ctx.setLineWidth(2)
        ctx.setStrokeColor(CGColor(red: 1, green: 0.85, blue: 0, alpha: 0.9))
        for item in ocr { ctx.stroke(CoordinateSpace.pixelRect(item.box, in: size)) }

        for candidate in candidates {
            let r = CoordinateSpace.pixelRect(candidate.box, in: size)
            if candidate.isOn {
                ctx.setFillColor(CGColor(red: 1, green: 0, blue: 0, alpha: 0.35))
                ctx.fill(r)
                ctx.setStrokeColor(CGColor(red: 1, green: 0, blue: 0, alpha: 0.95))
                ctx.setLineWidth(4)
                ctx.setLineDash(phase: 0, lengths: [])
                ctx.stroke(r)
            } else {
                ctx.setStrokeColor(CGColor(red: 1, green: 0.55, blue: 0, alpha: 0.95))
                ctx.setLineWidth(4)
                ctx.setLineDash(phase: 0, lengths: [10, 6])
                ctx.stroke(r)
                ctx.setLineDash(phase: 0, lengths: [])
            }
        }

        return ctx.makeImage()
    }

    public static func writePNG(_ image: CGImage, to url: URL) throws {
        guard let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil) else {
            throw MaskingError.renderFailed
        }
        CGImageDestinationAddImage(dest, image, nil)
        guard CGImageDestinationFinalize(dest) else { throw MaskingError.renderFailed }
    }
}
