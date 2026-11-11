import Foundation

/// 文字入れ（Mac 0.3.0・docs/form-fill-design.md §3）の文字枠 1 つ。
///
/// 位置と大きさ・文字サイズ・行間は**すべて手で決める**（マスや罫線の自動検出はしない）。
/// 座標は基準画像の正規化（左下原点・CoordinateSpace 規約）。文字はラスタに焼き込み、マスクは常に文字の上に塗る。
public struct TextBox: Codable, Sendable, Equatable, Identifiable {

    public enum Font: String, Codable, Sendable, CaseIterable { case gothic, mincho }
    /// 文字の色（sRGB・各 0〜1。1/255 単位に丸める）。パレットの色か、色選択で選んだ任意の色。
    public struct Color: Codable, Sendable, Equatable, Hashable {
        public var r: Double
        public var g: Double
        public var b: Double
        public init(r: Double, g: Double, b: Double) {
            func q(_ v: Double) -> Double { v.isFinite ? (min(max(v, 0), 1) * 255).rounded() / 255 : 0 }
            self.r = q(r); self.g = q(g); self.b = q(b)
        }
        public static let black = Color(r: 0, g: 0, b: 0)
        public static let blue = Color(r: 13 / 255.0, g: 51 / 255.0, b: 158 / 255.0)     // ボールペンの青
        public static let red = Color(r: 200 / 255.0, g: 28 / 255.0, b: 28 / 255.0)
        public static let green = Color(r: 20 / 255.0, g: 120 / 255.0, b: 50 / 255.0)
        public static let brown = Color(r: 115 / 255.0, g: 65 / 255.0, b: 25 / 255.0)
        public static let gray = Color(r: 110 / 255.0, g: 110 / 255.0, b: 110 / 255.0)
        /// 右パネルに並べる色（この順）。名前は画面の表示とアクセシビリティ用。
        public static let palette: [(name: String, color: Color)] = [
            ("黒", .black), ("青", .blue), ("赤", .red), ("緑", .green), ("茶", .brown), ("灰", .gray),
        ]
    }
    public enum HAlign: String, Codable, Sendable, CaseIterable { case left, center, right }
    public enum VAlign: String, Codable, Sendable, CaseIterable { case top, center, bottom }

    /// 文字サイズ（pt）の範囲と刻み。
    public static let fontSizeRange: ClosedRange<Double> = 4...72
    public static let fontSizeStep = 0.5
    /// 行間（行の送り＝文字サイズ×倍率）の範囲と刻み。
    public static let lineSpacingRange: ClosedRange<Double> = 0.8...3.0
    public static let lineSpacingStep = 0.05
    /// 字間（文字サイズに対する割合。0 = 標準）の範囲と刻み。マス目では使わない。
    public static let letterSpacingRange: ClosedRange<Double> = -0.2...2.0
    public static let letterSpacingStep = 0.05
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
    /// 字間（文字と文字の間に足す量 = 文字サイズ × letterSpacing。0 = 標準）。マス目（cells ≥ 1）では使わない。
    public var letterSpacing: Double
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
        self.letterSpacing = style.letterSpacing
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
        public var letterSpacing: Double
        public var font: Font
        public var color: Color
        public var hAlign: HAlign
        public var vAlign: VAlign
        public var cells: Int

        public init(fontSizePt: Double = 10.5, lineSpacing: Double = 1.2, letterSpacing: Double = 0,
                    font: Font = .gothic,
                    color: Color = .black, hAlign: HAlign = .left, vAlign: VAlign = .center, cells: Int = 0) {
            self.fontSizePt = fontSizePt
            self.lineSpacing = lineSpacing
            self.letterSpacing = letterSpacing
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
            s.letterSpacing = TextBox.snap(letterSpacing, range: TextBox.letterSpacingRange, step: TextBox.letterSpacingStep)
            s.cells = min(max(0, cells), TextBox.maxCells)
            return s
        }
    }

    public var style: Style {
        get { Style(fontSizePt: fontSizePt, lineSpacing: lineSpacing, letterSpacing: letterSpacing,
                    font: font, color: color,
                    hAlign: hAlign, vAlign: vAlign, cells: cells) }
        set {
            let s = newValue.clamped()
            fontSizePt = s.fontSizePt; lineSpacing = s.lineSpacing; letterSpacing = s.letterSpacing
            font = s.font; color = s.color
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
