import Foundation
import CoreGraphics

/// 内部の唯一の矩形型: 基準画像の正規化座標（0..1・**左下原点**）。
/// Vision の boundingBox / CIImage と同じ向きなので、パイプライン内は変換なしで受け渡せる。
/// 変換が必要なのは描画(pixelRect)と表示(viewRect)の2箇所だけ。手変換禁止（core-design.md §1）。
public struct NormRect: Codable, Equatable, Sendable {
    public var x: Double
    public var y: Double
    public var w: Double
    public var h: Double

    public init(x: Double, y: Double, w: Double, h: Double) {
        self.x = x; self.y = y; self.w = w; self.h = h
    }

    public init(_ r: CGRect) {
        self.init(x: r.minX, y: r.minY, w: r.width, h: r.height)
    }

    public var cgRect: CGRect { CGRect(x: x, y: y, width: w, height: h) }

    /// 外周に余白を足す（0..1へクランプ）。マスクの「復元できない程度」安全マージン用。
    public func padded(by p: Double) -> NormRect {
        let nx = max(0, x - p), ny = max(0, y - p)
        return NormRect(x: nx, y: ny,
                        w: min(1 - nx, w + 2 * p),
                        h: min(1 - ny, h + 2 * p))
    }

    public func intersects(_ other: NormRect) -> Bool {
        cgRect.intersects(other.cgRect)
    }

    public func union(_ other: NormRect) -> NormRect {
        NormRect(cgRect.union(other.cgRect))
    }
}

/// 書類の四隅（正規化 0..1・**左下原点**。Vision の VNRectangleObservation と同じ向き）。
/// 切り抜きの手動調整（確認UI）と検出結果の受け渡しに使う。
public struct Quad: Equatable, Sendable {
    public var topLeft: CGPoint
    public var topRight: CGPoint
    public var bottomRight: CGPoint
    public var bottomLeft: CGPoint

    public init(topLeft: CGPoint, topRight: CGPoint, bottomRight: CGPoint, bottomLeft: CGPoint) {
        self.topLeft = topLeft
        self.topRight = topRight
        self.bottomRight = bottomRight
        self.bottomLeft = bottomLeft
    }

    /// 画像全体（切り抜きなし）。
    public static let fullImage = Quad(topLeft: CGPoint(x: 0, y: 1), topRight: CGPoint(x: 1, y: 1),
                                       bottomRight: CGPoint(x: 1, y: 0), bottomLeft: CGPoint(x: 0, y: 0))

    /// 凸四角形か（自己交差・つぶれの排除）。UIが「適用」の可否判定に使う。
    public var isConvex: Bool {
        let pts = [topLeft, topRight, bottomRight, bottomLeft]
        var sign = 0.0
        for i in 0..<4 {
            let a = pts[i], b = pts[(i + 1) % 4], c = pts[(i + 2) % 4]
            let cross = (b.x - a.x) * (c.y - b.y) - (b.y - a.y) * (c.x - b.x)
            if abs(cross) < 1e-9 { return false }          // 3点が一直線＝つぶれ
            if sign == 0 { sign = cross } else if sign * cross < 0 { return false }
        }
        return true
    }
}

public enum CoordinateSpace {

    /// 正規化（左下原点）の1点 → 表示座標（左上原点）。`normPoint(fromViewPoint:)` の逆変換。
    public static func viewPoint(_ p: CGPoint, in size: CGSize) -> CGPoint {
        CGPoint(x: p.x * size.width, y: (1 - p.y) * size.height)
    }


    /// 描画用（CGContext・左下原点）: 単純スケール。
    public static func pixelRect(_ r: NormRect, in size: CGSize) -> CGRect {
        CGRect(x: r.x * size.width, y: r.y * size.height,
               width: r.w * size.width, height: r.h * size.height)
    }

    /// SwiftUI/AppKit 表示用（左上原点）: Yフリップ＋スケール。
    public static func viewRect(_ r: NormRect, in size: CGSize) -> CGRect {
        CGRect(x: r.x * size.width,
               y: (1 - r.y - r.h) * size.height,
               width: r.w * size.width, height: r.h * size.height)
    }

    /// プリセットJSONの固定領域（人間が書く左上原点 yTop 表記）→ 内部の左下原点。
    public static func fromTopOrigin(x: Double, yTop: Double, w: Double, h: Double) -> NormRect {
        NormRect(x: x, y: 1 - yTop - h, w: w, h: h)
    }

    /// Vision の正規化 boundingBox → NormRect（同じ向きなので詰め替えのみ。意図の明示用）。
    public static func fromVision(_ box: CGRect) -> NormRect { NormRect(box) }

    /// SwiftUI/AppKit 表示座標（左上原点）→ 内部正規化（左下原点）。確認UIの手動矩形ツール用。
    /// ドラッグがキャンバス外へはみ出しても安全なように 0..1 へクランプする。
    public static func normRect(fromViewRect r: CGRect, in size: CGSize) -> NormRect {
        guard size.width > 0, size.height > 0 else { return NormRect(x: 0, y: 0, w: 0, h: 0) }
        let x = max(0, min(1, r.minX / size.width))
        let maxX = max(0, min(1, r.maxX / size.width))
        // 左上原点の minY/maxY は左下原点では上端/下端が入れ替わる
        let y = max(0, min(1, 1 - r.maxY / size.height))
        let maxY = max(0, min(1, 1 - r.minY / size.height))
        return NormRect(x: x, y: y, w: maxX - x, h: maxY - y)
    }

    /// 表示座標の1点（左上原点）→ 内部正規化（左下原点）。ブラシストロークの点列用。0..1へクランプ。
    public static func normPoint(fromViewPoint p: CGPoint, in size: CGSize) -> CGPoint {
        guard size.width > 0, size.height > 0 else { return .zero }
        return CGPoint(x: max(0, min(1, p.x / size.width)),
                       y: max(0, min(1, 1 - p.y / size.height)))
    }
}
