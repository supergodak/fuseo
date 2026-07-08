import Foundation
import CoreGraphics

/// ラスタ焼き込みによる墨消し（core-design.md §4）。
///
/// **正しさの要件（設計書§1.4・法文言「復元できない程度に」）**:
/// - 新規ビットマップに基準画像を描き、マスクを**完全不透明の黒**で塗る。出力画像のマスク下に
///   元ピクセルは存在しない（α合成・注釈・レイヤー重ねは禁止。「見た目だけ黒い」を作らない）。
/// - 元の CGImage は変更しない（確認UIのやり直しが効くよう、入力は不変）。
public struct RasterMaskRenderer: MaskRendering {
    public init() {}

    public func burnIn(page: PageImage, masks: [NormRect], strokes: [BrushStroke]) throws -> CGImage {
        let w = page.cgImage.width
        let h = page.cgImage.height
        let size = CGSize(width: w, height: h)
        guard let ctx = CGContext(data: nil, width: w, height: h,
                                  bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw MaskingError.renderFailed
        }
        ctx.draw(page.cgImage, in: CGRect(origin: .zero, size: size))

        let opaqueBlack = CGColor(red: 0, green: 0, blue: 0, alpha: 1)

        // 矩形マスク（候補＋手動矩形）
        ctx.setFillColor(opaqueBlack)
        for mask in masks {
            ctx.fill(CoordinateSpace.pixelRect(mask, in: size))
        }

        // ブラシストローク（不透明黒・丸キャップ。widthは画像短辺比）
        if !strokes.isEmpty {
            ctx.setStrokeColor(opaqueBlack)
            ctx.setLineCap(.round)
            ctx.setLineJoin(.round)
            for stroke in strokes {
                guard let first = stroke.points.first else { continue }
                ctx.setLineWidth(max(1, stroke.width * min(size.width, size.height)))
                ctx.beginPath()
                ctx.move(to: CGPoint(x: first.x * size.width, y: first.y * size.height))
                for p in stroke.points.dropFirst() {
                    ctx.addLine(to: CGPoint(x: p.x * size.width, y: p.y * size.height))
                }
                if stroke.points.count == 1 {
                    // 1点タップでも丸キャップで打点になるよう極小線分にする
                    ctx.addLine(to: CGPoint(x: first.x * size.width + 0.1, y: first.y * size.height))
                }
                ctx.strokePath()
            }
        }

        guard let out = ctx.makeImage() else { throw MaskingError.renderFailed }
        return out
    }
}
