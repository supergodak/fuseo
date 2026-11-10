import Foundation
import CoreGraphics
import MaskingCore

/// 文字枠の幾何の補助（文字入れ 0.3.0・docs/form-fill-design.md §1/§5）。AppKit 非依存（iOS と共有）。
///
/// - mm ⇄ 基準画像ピクセルは 300dpi 換算（`TextBox.pixelsPerInch`。0.2.4 で書き出し PDF の紙をこれに揃えた）。
///   写真の取り込みでは紙の大きさが推定なので「目安の mm」になる（注記はしない・設計 §8 の案どおり）。
/// - 座標変換は `CoordinateSpace` 経由のみ（手変換禁止）。mm の y はページの**左上**から測る。
enum TextGeometry {

    /// 位置と大きさ（mm・ページ左上から）。
    struct FrameMM: Equatable {
        var x: Double
        var y: Double
        var width: Double
        var height: Double
    }

    static func pixels(fromMM mm: Double) -> Double { mm / 25.4 * TextBox.pixelsPerInch }
    static func mm(fromPixels px: Double) -> Double { px / TextBox.pixelsPerInch * 25.4 }

    static func frameMM(_ box: NormRect, imageSize: CGSize) -> FrameMM {
        let px = CoordinateSpace.viewRect(box, in: imageSize)      // 基準画像のピクセル・左上原点
        return FrameMM(x: mm(fromPixels: px.minX), y: mm(fromPixels: px.minY),
                       width: mm(fromPixels: px.width), height: mm(fromPixels: px.height))
    }

    /// mm の枠 → 正規化。ページより大きい幅・高さはページに収め、はみ出す位置は内側へ寄せる。
    static func normRect(fromMM f: FrameMM, imageSize size: CGSize) -> NormRect? {
        guard size.width > 0, size.height > 0,
              [f.x, f.y, f.width, f.height].allSatisfy(\.isFinite) else { return nil }
        let w = min(max(1, pixels(fromMM: f.width)), size.width)
        let h = min(max(1, pixels(fromMM: f.height)), size.height)
        let x = min(max(0, pixels(fromMM: f.x)), size.width - w)
        let y = min(max(0, pixels(fromMM: f.y)), size.height - h)
        return CoordinateSpace.normRect(fromViewRect: CGRect(x: x, y: y, width: w, height: h), in: size)
    }

    /// クリックだけで作る枠（既定の大きさ）。クリック位置を枠の左端・縦の中央にする
    /// （表の欄の書き始めをクリックすれば、その行の高さに文字が来る）。
    /// 大きさは文字サイズから決める: 高さ = 文字サイズの 2 倍、幅 = 10 文字分（ページの 90% まで）。
    static func defaultBox(clickedAt p: CGPoint, style: TextBox.Style, imageSize size: CGSize) -> NormRect? {
        guard size.width > 0, size.height > 0 else { return nil }
        let fontPx = style.fontSizePt * TextBox.pixelsPerInch / 72
        let w = min(fontPx * 10, size.width * 0.9)
        let h = min(fontPx * 2, size.height * 0.9)
        let click = CoordinateSpace.viewPoint(p, in: size)          // 基準画像のピクセル・左上原点
        let x = min(max(0, click.x), size.width - w)
        let y = min(max(0, click.y - h / 2), size.height - h)
        return CoordinateSpace.normRect(fromViewRect: CGRect(x: x, y: y, width: w, height: h), in: size)
    }
}

/// 位置合わせの補助（吸い付き・設計 §1）。手作業を助けるだけで自動配置はしない。
/// 他の文字枠と **左端・右端・行の中心（縦の中央）** が近づいたら揃えて、細い線を出す。座標は表示座標（左上原点）。
enum TextSnap {

    struct Guide: Equatable, Hashable {
        enum Axis: Hashable { case vertical, horizontal }
        let axis: Axis
        /// vertical なら x、horizontal なら y（表示座標）。
        let position: CGFloat
    }

    struct Result: Equatable {
        var rect: CGRect
        var guides: [Guide]
    }

    /// 吸い付く距離（表示座標の pt）。
    static let threshold: CGFloat = 4

    /// 移動: 左端どうし・右端どうし（横）と、縦の中央どうし（行の中心）を揃える。
    static func snapMove(_ r: CGRect, others: [CGRect], threshold: CGFloat = threshold) -> Result {
        var rect = r
        var guides: [Guide] = []
        var bestX: (d: CGFloat, pos: CGFloat)?
        for o in others {
            for (mine, theirs) in [(r.minX, o.minX), (r.maxX, o.maxX)] {
                let d = theirs - mine
                if abs(d) <= threshold, abs(d) < abs(bestX?.d ?? .infinity) { bestX = (d, theirs) }
            }
        }
        if let b = bestX {
            rect.origin.x += b.d
            guides.append(Guide(axis: .vertical, position: b.pos))
        }
        var bestY: (d: CGFloat, pos: CGFloat)?
        for o in others {
            let d = o.midY - r.midY
            if abs(d) <= threshold, abs(d) < abs(bestY?.d ?? .infinity) { bestY = (d, o.midY) }
        }
        if let b = bestY {
            rect.origin.y += b.d
            guides.append(Guide(axis: .horizontal, position: b.pos))
        }
        return Result(rect: rect, guides: guides)
    }

    /// 大きさ変更: 動かしている左端/右端だけを、他の枠の同じ端に揃える。
    static func snapResize(_ r: CGRect, movingLeft: Bool, movingRight: Bool,
                           others: [CGRect], threshold: CGFloat = threshold) -> Result {
        var minX = r.minX, maxX = r.maxX
        var guides: [Guide] = []
        if movingLeft, let o = others.map(\.minX).min(by: { abs($0 - r.minX) < abs($1 - r.minX) }),
           abs(o - r.minX) <= threshold, o < maxX - 1 {
            minX = o
            guides.append(Guide(axis: .vertical, position: o))
        }
        if movingRight, let o = others.map(\.maxX).min(by: { abs($0 - r.maxX) < abs($1 - r.maxX) }),
           abs(o - r.maxX) <= threshold, o > minX + 1 {
            maxX = o
            guides.append(Guide(axis: .vertical, position: o))
        }
        return Result(rect: CGRect(x: minX, y: r.minY, width: maxX - minX, height: r.height), guides: guides)
    }
}
