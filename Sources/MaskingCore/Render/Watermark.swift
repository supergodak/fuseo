import Foundation
import CoreGraphics
import CoreText

/// 透かし（「〇〇提出用」など）。書き出しのときに**画素として焼き込む**（Mac 0.4.0）。
///
/// 文字として PDF に描くと選択・コピー・削除ができてしまう（唐櫃・Fubako の実機の教訓）。Fuseo は
/// 書き出しで全ページを画像にするので、黒塗りと同じく画素に描けば文字としては残らない。
/// 検索可能 PDF のテキスト層にも入らない（テキスト層は OCR の結果だけから作る）。
public struct Watermark: Sendable, Equatable, Codable {
    public enum Layout: String, Sendable, CaseIterable, Codable {
        /// 全面に斜めに敷き詰める（切り取って消しにくい。既定）
        case tiled
        /// 中央に対角線に沿って 1 つ
        case diagonal
    }

    public var text: String
    public var layout: Layout
    /// 濃さ（0〜1）。既定 0.25（Fubako の実機で、読みやすさと見えやすさの釣り合いが取れた値）
    public var opacity: Double

    public static let defaultOpacity = 0.25
    public static let opacityRange: ClosedRange<Double> = 0.1...0.6

    public init(text: String, layout: Layout = .tiled, opacity: Double = Watermark.defaultOpacity) {
        self.text = text
        self.layout = layout
        self.opacity = opacity
    }

    /// 前後の空白を除いた文言（空なら透かしを入れない）。
    public var trimmedText: String { text.trimmingCharacters(in: .whitespacesAndNewlines) }
}

/// 透かしを置く位置の計算（純関数。画面の見本と書き出しで同じものを使う）。
/// 座標は画像の px・左下原点。角度はラジアン・反時計回り。
public enum WatermarkLayout {

    public struct Placement: Equatable {
        /// 文字列の中心
        public var center: CGPoint
        public var angle: CGFloat
    }

    /// 敷き詰めの角度（30°）。
    public static let tiledAngle: CGFloat = .pi / 6

    /// 文字の大きさ（px）。
    /// - tiled: 短辺の 5.5%（カードでも A4 でも、本文を読む邪魔にならず写真に写っても読める大きさ）
    /// - diagonal: 文字列の幅が対角線の 70% になる大きさ（短辺の 18% を上限）
    public static func fontSize(for layout: Watermark.Layout, canvas: CGSize, widthPerPoint: CGFloat) -> CGFloat {
        let short = min(canvas.width, canvas.height)
        switch layout {
        case .tiled:
            return max(8, short * 0.055)
        case .diagonal:
            let diagonal = (canvas.width * canvas.width + canvas.height * canvas.height).squareRoot()
            let fit = widthPerPoint > 0 ? diagonal * 0.7 / widthPerPoint : short * 0.18
            return max(8, min(fit, short * 0.18))
        }
    }

    /// 置く位置。`textSize` は実際の文字の大きさで測った文字列の幅と高さ（px）。
    public static func placements(layout: Watermark.Layout, canvas: CGSize, textSize: CGSize) -> [Placement] {
        switch layout {
        case .diagonal:
            return [Placement(center: CGPoint(x: canvas.width / 2, y: canvas.height / 2),
                              angle: atan2(canvas.height, canvas.width))]
        case .tiled:
            guard textSize.width > 0, textSize.height > 0 else { return [] }
            // 回転した座標系（u: 文字の向き, v: それに直交）で格子を作り、画像を覆う範囲だけ返す。
            let stepU = textSize.width + textSize.height * 2.5
            let stepV = textSize.height * 4
            let angle = tiledAngle
            let (c, s) = (cos(angle), sin(angle))
            let center = CGPoint(x: canvas.width / 2, y: canvas.height / 2)
            let reach = (canvas.width * canvas.width + canvas.height * canvas.height).squareRoot() / 2
                + textSize.width
            let rows = Int((reach / stepV).rounded(.up))
            let cols = Int((reach / stepU).rounded(.up)) + 1
            var out: [Placement] = []
            let margin = textSize.width / 2 + textSize.height
            for row in -rows...rows {
                let v = CGFloat(row) * stepV
                let shift = (row & 1 == 0) ? 0 : stepU / 2       // 1 行おきに半分ずらす（レンガ積み）
                for col in -cols...cols {
                    let u = CGFloat(col) * stepU + shift
                    let p = CGPoint(x: center.x + u * c - v * s, y: center.y + u * s + v * c)
                    if p.x >= -margin, p.x <= canvas.width + margin,
                       p.y >= -margin, p.y <= canvas.height + margin {
                        out.append(Placement(center: p, angle: angle))
                    }
                }
            }
            return out
        }
    }
}

/// 透かしの焼き込み。入力は変えず、新しい画像を返す。
public enum WatermarkRenderer {

    static let fontName = "HiraginoSans-W6" as CFString

    public static func apply(_ watermark: Watermark, to image: CGImage) -> CGImage {
        let text = watermark.trimmedText
        guard !text.isEmpty else { return image }
        let w = image.width, h = image.height
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return image }
        let canvas = CGSize(width: w, height: h)
        ctx.draw(image, in: CGRect(origin: .zero, size: canvas))
        draw(watermark, in: ctx, canvas: canvas)
        return ctx.makeImage() ?? image
    }

    /// 既存のコンテキストに描く（画面の見本でも使う）。
    public static func draw(_ watermark: Watermark, in ctx: CGContext, canvas: CGSize) {
        let text = watermark.trimmedText
        guard !text.isEmpty else { return }
        // 1pt あたりの幅を測ってから大きさを決める
        let unit = line(text, size: 100)
        let unitWidth = CTLineGetTypographicBounds(unit, nil, nil, nil) / 100
        let size = WatermarkLayout.fontSize(for: watermark.layout, canvas: canvas, widthPerPoint: CGFloat(unitWidth))
        let ln = line(text, size: size)
        var ascent: CGFloat = 0, descent: CGFloat = 0
        let width = CGFloat(CTLineGetTypographicBounds(ln, &ascent, &descent, nil))
        let height = ascent + descent
        let opacity = min(max(watermark.opacity, Watermark.opacityRange.lowerBound), Watermark.opacityRange.upperBound)
        ctx.saveGState()
        ctx.setAlpha(CGFloat(opacity))
        for p in WatermarkLayout.placements(layout: watermark.layout, canvas: canvas,
                                            textSize: CGSize(width: width, height: height)) {
            ctx.saveGState()
            ctx.translateBy(x: p.center.x, y: p.center.y)
            ctx.rotate(by: p.angle)
            ctx.textMatrix = .identity
            ctx.textPosition = CGPoint(x: -width / 2, y: -height / 2 + descent)
            CTLineDraw(ln, ctx)
            ctx.restoreGState()
        }
        ctx.restoreGState()
    }

    private static func line(_ text: String, size: CGFloat) -> CTLine {
        let font = CTFontCreateWithName(fontName, size, nil)
        let attrs: [NSAttributedString.Key: Any] = [
            NSAttributedString.Key(kCTFontAttributeName as String): font,
            NSAttributedString.Key(kCTForegroundColorAttributeName as String): CGColor(gray: 0.2, alpha: 1),
        ]
        return CTLineCreateWithAttributedString(NSAttributedString(string: text, attributes: attrs))
    }
}
