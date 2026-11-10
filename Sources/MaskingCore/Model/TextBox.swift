import Foundation

/// 文字入れ（Mac 0.3.0・docs/form-fill-design.md §3）の文字枠 1 つ。
///
/// 位置と大きさ・文字サイズ・行間は**すべて手で決める**（マスや罫線の自動検出はしない）。
/// 座標は基準画像の正規化（左下原点・CoordinateSpace 規約）。文字はラスタに焼き込み、マスクは常に文字の上に塗る。
public struct TextBox: Codable, Sendable, Equatable, Identifiable {

    public enum Font: String, Codable, Sendable, CaseIterable { case gothic, mincho }
    public enum Color: String, Codable, Sendable, CaseIterable { case black, blue }
    public enum HAlign: String, Codable, Sendable, CaseIterable { case left, center, right }
    public enum VAlign: String, Codable, Sendable, CaseIterable { case top, center, bottom }

    /// 文字サイズ（pt）の範囲と刻み。
    public static let fontSizeRange: ClosedRange<Double> = 4...72
    public static let fontSizeStep = 0.5
    /// 行間（行の送り＝文字サイズ×倍率）の範囲と刻み。
    public static let lineSpacingRange: ClosedRange<Double> = 0.8...3.0
    public static let lineSpacingStep = 0.05
    /// マス数の上限（0 = マス目なし）。
    public static let maxCells = 40
    /// pt → px の換算に使う解像度（書き出し PDF の 300dpi 換算と同じ。0.2.4 で揃えた）。
    public static let pixelsPerInch = 300.0

    public var id: UUID
    /// 枠（位置と大きさ）。
    public var box: NormRect
    /// 入力した文字。`\n` で改行（枠の幅での自動折り返しはしない）。
    public var text: String
    /// 文字サイズ（pt・300dpi 換算。px = pt × 300 / 72）。
    public var fontSizePt: Double
    /// 行間の倍率（行の送り = 文字サイズ × lineSpacing）。
    public var lineSpacing: Double
    public var font: Font
    public var color: Color
    public var hAlign: HAlign
    public var vAlign: VAlign
    /// 0 = 通常の文章、N ≥ 1 = マス目（枠の幅を N 等分して 1 マス 1 文字・1 行だけ）。
    public var cells: Int

    public init(id: UUID = UUID(), box: NormRect, text: String = "",
                style: Style = .default) {
        self.id = id
        self.box = box
        self.text = text
        self.fontSizePt = style.fontSizePt
        self.lineSpacing = style.lineSpacing
        self.font = style.font
        self.color = style.color
        self.hAlign = style.hAlign
        self.vAlign = style.vAlign
        self.cells = style.cells
    }

    /// 文字以外の設定（次に作る枠への引き継ぎに使う）。
    public struct Style: Codable, Sendable, Equatable {
        public var fontSizePt: Double
        public var lineSpacing: Double
        public var font: Font
        public var color: Color
        public var hAlign: HAlign
        public var vAlign: VAlign
        public var cells: Int

        public init(fontSizePt: Double = 10.5, lineSpacing: Double = 1.2, font: Font = .gothic,
                    color: Color = .black, hAlign: HAlign = .left, vAlign: VAlign = .center, cells: Int = 0) {
            self.fontSizePt = fontSizePt
            self.lineSpacing = lineSpacing
            self.font = font
            self.color = color
            self.hAlign = hAlign
            self.vAlign = vAlign
            self.cells = cells
        }

        public static let `default` = Style()

        /// 範囲外・刻み外の値を丸める（UI と読み込みの両方で使う）。
        public func clamped() -> Style {
            var s = self
            s.fontSizePt = TextBox.snap(fontSizePt, range: TextBox.fontSizeRange, step: TextBox.fontSizeStep)
            s.lineSpacing = TextBox.snap(lineSpacing, range: TextBox.lineSpacingRange, step: TextBox.lineSpacingStep)
            s.cells = min(max(0, cells), TextBox.maxCells)
            return s
        }
    }

    public var style: Style {
        get { Style(fontSizePt: fontSizePt, lineSpacing: lineSpacing, font: font, color: color,
                    hAlign: hAlign, vAlign: vAlign, cells: cells) }
        set {
            let s = newValue.clamped()
            fontSizePt = s.fontSizePt; lineSpacing = s.lineSpacing; font = s.font; color = s.color
            hAlign = s.hAlign; vAlign = s.vAlign; cells = s.cells
        }
    }

    /// 文字サイズ（px）。基準画像のピクセルで描くときの大きさ。
    public var fontSizePx: Double { fontSizePt * TextBox.pixelsPerInch / 72 }

    static func snap(_ v: Double, range: ClosedRange<Double>, step: Double) -> Double {
        guard v.isFinite else { return range.lowerBound }
        let stepped = (v / step).rounded() * step
        return min(max(stepped, range.lowerBound), range.upperBound)
    }
}
