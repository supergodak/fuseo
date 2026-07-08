import Foundation
import CoreGraphics
import ImageIO
import UniformTypeIdentifiers

/// 層2用の合成フィクスチャ生成（文字なし単色矩形＝generic・候補ゼロ→手動ツール経路を試験）。
/// **fixtures-private は一切参照しない**（wp5 §8）。
enum UITestFixture {
    /// 一時ディレクトリに単色 PNG を書き出し、その URL を返す。
    static func makeSolidPNG(width: Int = 900, height: Int = 560) -> URL {
        let ctx = CGContext(data: nil, width: width, height: height,
                            bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.setFillColor(CGColor(red: 0.85, green: 0.87, blue: 0.9, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: width, height: height))
        let image = ctx.makeImage()!

        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("fuseo-uitest-\(UUID().uuidString).png")
        let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil)!
        CGImageDestinationAddImage(dest, image, nil)
        CGImageDestinationFinalize(dest)
        return url
    }
}
