import Foundation
import CoreGraphics
import CoreText

/// 文字枠の配置と描画（docs/form-fill-design.md §4）。**画面表示と焼き込みで同じ関数**を使う。
///
/// 座標は基準画像のピクセル・左下原点（CGBitmapContext の既定と同じ）。表示側は
/// コンテキストに「表示サイズ / 画像サイズ」の縮尺と上下反転を掛けてから `draw` を呼ぶ。
///
/// 規則（手で決める方針）:
/// - 打った改行だけが改行。枠の幅で自動折り返しはしない。
/// - 行の送り = 文字サイズ × 行間。行の塊の高さ = (行数 − 1) × 送り + (ascent + descent)。
/// - 縦の揃えは塊を枠に対して置く。横の揃えは行ごと。
/// - マス目（cells = N）: 枠の幅を N 等分し、i 文字目をマス i の中心に置く（1 行目だけ・N 文字まで）。
/// - はみ出しても切り取らない（`overflows` で画面に知らせる）。
public enum TextBoxRenderer {

    /// 1 行（またはマス目の 1 文字）の配置結果。
    public struct PlacedLine: Equatable {
        public let text: String
        /// ベースラインの始点（px・左下原点）。
        public let origin: CGPoint
        public let width: CGFloat
    }

    public struct Layout: Equatable {
        public let lines: [PlacedLine]
        /// 描く文字の外接（ascent〜descent・px）。空文字なら枠の位置の幅 0。
        public let inkBounds: CGRect
        public let overflows: Bool
    }

    // MARK: - 書体

    static func postScriptName(_ font: TextBox.Font) -> String {
        switch font {
        case .gothic: return "HiraginoSans-W3"
        case .mincho: return "HiraMinProN-W3"
        }
    }

    static func ctFont(_ font: TextBox.Font, sizePx: CGFloat) -> CTFont {
        CTFontCreateWithName(postScriptName(font) as CFString, sizePx, nil)
    }

    static func cgColor(_ color: TextBox.Color) -> CGColor {
        switch color {
        case .black: return CGColor(srgbRed: 0, green: 0, blue: 0, alpha: 1)
        case .blue: return CGColor(srgbRed: 0.05, green: 0.2, blue: 0.62, alpha: 1)   // ボールペンの青
        }
    }

    /// (ascent + descent) / 文字サイズ。枠に合わせるの逆算に使う（書体ごとに一定）。
    static func heightRatio(_ font: TextBox.Font) -> Double {
        let f = ctFont(font, sizePx: 100)
        return Double(CTFontGetAscent(f) + CTFontGetDescent(f)) / 100
    }

    private static func makeLine(_ s: String, font: CTFont, color: CGColor) -> CTLine {
        let attrs: [NSAttributedString.Key: Any] = [
            NSAttributedString.Key(kCTFontAttributeName as String): font,
            NSAttributedString.Key(kCTForegroundColorAttributeName as String): color,
        ]
        return CTLineCreateWithAttributedString(NSAttributedString(string: s, attributes: attrs))
    }

    private static func width(of line: CTLine) -> CGFloat {
        CGFloat(CTLineGetTypographicBounds(line, nil, nil, nil))
    }

    // MARK: - 配置

    /// 文字枠を画像サイズに対して配置する（描かない）。
    public static func layout(_ box: TextBox, imageSize: CGSize) -> Layout {
        let frame = CoordinateSpace.pixelRect(box.box, in: imageSize)
        let size = CGFloat(box.fontSizePx)
        let font = ctFont(box.font, sizePx: size)
        let color = cgColor(box.color)
        let ascent = CTFontGetAscent(font), descent = CTFontGetDescent(font)
        let advance = size * CGFloat(box.lineSpacing)

        if box.cells > 0 {
            let firstLine = box.text.split(separator: "\n", omittingEmptySubsequences: false).first.map(String.init) ?? ""
            let chars = firstLine.map(String.init)
            let cellW = frame.width / CGFloat(box.cells)
            let baseline = baselineY(frame: frame, lineCount: 1, advance: advance,
                                     ascent: ascent, descent: descent, vAlign: box.vAlign)
            var placed: [PlacedLine] = []
            for (i, ch) in chars.prefix(box.cells).enumerated() {
                let w = width(of: makeLine(ch, font: font, color: color))
                let x = frame.minX + cellW * (CGFloat(i) + 0.5) - w / 2
                placed.append(PlacedLine(text: ch, origin: CGPoint(x: x, y: baseline), width: w))
            }
            let ink = inkBounds(placed, ascent: ascent, descent: descent, fallback: frame)
            let overflow = chars.count > box.cells || (ascent + descent) > frame.height + 0.5
            return Layout(lines: placed, inkBounds: ink, overflows: overflow)
        }

        let texts = box.text.split(separator: "\n", omittingEmptySubsequences: false).map(String.init)
        let n = max(1, texts.count)
        let top = baselineY(frame: frame, lineCount: n, advance: advance,
                            ascent: ascent, descent: descent, vAlign: box.vAlign)
        var placed: [PlacedLine] = []
        var maxW: CGFloat = 0
        for (i, s) in texts.enumerated() {
            let w = width(of: makeLine(s, font: font, color: color))
            maxW = max(maxW, w)
            let x: CGFloat
            switch box.hAlign {
            case .left: x = frame.minX
            case .center: x = frame.midX - w / 2
            case .right: x = frame.maxX - w
            }
            placed.append(PlacedLine(text: s, origin: CGPoint(x: x, y: top - CGFloat(i) * advance), width: w))
        }
        let blockH = CGFloat(n - 1) * advance + ascent + descent
        let ink = inkBounds(placed, ascent: ascent, descent: descent, fallback: frame)
        let overflow = maxW > frame.width + 0.5 || blockH > frame.height + 0.5
        return Layout(lines: placed, inkBounds: ink, overflows: overflow)
    }

    /// 1 行目のベースラインの y（px）。
    private static func baselineY(frame: CGRect, lineCount n: Int, advance: CGFloat,
                                  ascent: CGFloat, descent: CGFloat, vAlign: TextBox.VAlign) -> CGFloat {
        let blockH = CGFloat(n - 1) * advance + ascent + descent
        let blockTop: CGFloat
        switch vAlign {
        case .top: blockTop = frame.maxY
        case .center: blockTop = frame.midY + blockH / 2
        case .bottom: blockTop = frame.minY + blockH
        }
        return blockTop - ascent
    }

    private static func inkBounds(_ lines: [PlacedLine], ascent: CGFloat, descent: CGFloat, fallback: CGRect) -> CGRect {
        let nonEmpty = lines.filter { $0.width > 0 }
        guard !nonEmpty.isEmpty else { return CGRect(x: fallback.minX, y: fallback.midY, width: 0, height: 0) }
        let rects = nonEmpty.map { CGRect(x: $0.origin.x, y: $0.origin.y - descent, width: $0.width, height: ascent + descent) }
        return rects.dropFirst().reduce(rects[0]) { $0.union($1) }
    }

    // MARK: - 描画

    /// 文字枠を描く。`ctx` のユーザー空間は基準画像のピクセル・左下原点であること。
    public static func draw(_ boxes: [TextBox], in ctx: CGContext, imageSize: CGSize) {
        for box in boxes where !box.text.isEmpty {
            let layout = layout(box, imageSize: imageSize)
            let font = ctFont(box.font, sizePx: CGFloat(box.fontSizePx))
            let color = cgColor(box.color)
            ctx.saveGState()
            ctx.textMatrix = .identity
            for line in layout.lines where !line.text.isEmpty {
                ctx.textPosition = line.origin
                CTLineDraw(makeLine(line.text, font: font, color: color), ctx)
            }
            ctx.restoreGState()
        }
    }

    // MARK: - 枠の高さに合わせる

    /// 行の塊の高さが枠の高さの 90% になる文字サイズ（pt・0.5 刻み・4〜72pt に丸め）。1 回だけ計算する。
    public static func fittingFontSizePt(_ box: TextBox, imageSize: CGSize) -> Double {
        let frameH = Double(CoordinateSpace.pixelRect(box.box, in: imageSize).height)
        let n = box.cells > 0 ? 1 : max(1, box.text.split(separator: "\n", omittingEmptySubsequences: false).count)
        let perPx = Double(n - 1) * box.lineSpacing + heightRatio(box.font)   // 塊の高さ / 文字サイズ
        guard perPx > 0, frameH > 0 else { return box.fontSizePt }
        let sizePx = frameH * 0.9 / perPx
        return TextBox.snap(sizePx * 72 / TextBox.pixelsPerInch, range: TextBox.fontSizeRange, step: TextBox.fontSizeStep)
    }
}
