import Foundation
import CoreGraphics
import CoreImage
import CoreImage.CIFilterBuiltins
import CoreText
import ImageIO
import UniformTypeIdentifiers

/// 出力カラーモードの適用（v1.1）。焼き込み**後**の画像に掛けるため、マスクの黒は
/// グレースケールでも二値化でも黒のまま（黒→黒。復元不能性に影響しない）。
enum ColorModeFilter {
    static func apply(_ mode: ExportOptions.ColorMode, to image: CGImage) -> CGImage {
        guard mode != .color else { return image }
        let input = CIImage(cgImage: image)
        let output: CIImage
        switch mode {
        case .color:
            return image
        case .grayscale:
            let f = CIFilter.colorControls()
            f.inputImage = input
            f.saturation = 0
            output = f.outputImage ?? input
        case .blackWhite:
            let f = CIFilter.colorThresholdOtsu()   // 文書向け・しきい値自動（Otsu法）
            f.inputImage = input
            output = f.outputImage ?? input
        }
        // 変換失敗時は元画像へフォールバック（出力が欠けるより安全）
        return VisionSupport.ciContext.createCGImage(output, from: input.extent) ?? image
    }
}

/// ファイル書き出し（core-design.md §4）。
///
/// **正しさの要件**:
/// - 出力は常に**新規生成**し、メタデータ辞書を一切渡さない（EXIF・GPS・サムネイルを引き継がない）。
///   そもそも入力は焼き込み済み CGImage（ピクセルのみ）なので、原本のメタデータは構造的に到達しない。
///   このクラスが将来「元ファイルのプロパティをコピーする」よう変更されないことをテストが監視する。
/// - PDF は焼き込み済みラスタのみを埋め込む。検索可能テキスト層は**マスク矩形と交差する OCRItem を
///   除外**して合成する（マスク下の文字列を PDF に残さない。部分交差も安全側に倒して除外）。
public struct FileExporter: Exporting {

    /// PDFの解像度（px→pt換算）。150dpi 相当＝A4スキャンの一般的な見た目。
    let pdfDPI: CGFloat

    public init(pdfDPI: CGFloat = 150) {
        self.pdfDPI = pdfDPI
    }

    public func export(_ pages: [RenderedPage], options: ExportOptions, to url: URL) throws {
        guard !pages.isEmpty else { throw MaskingError.exportFailed("出力するページがありません") }
        // カラーモードは焼き込み済み画像に適用（マスクの黒は不変）。OCR・マスク矩形はそのまま。
        let pages = pages.map { page in
            RenderedPage(image: ColorModeFilter.apply(options.colorMode, to: page.image),
                         ocrItems: page.ocrItems, maskRects: page.maskRects)
        }
        switch options.format {
        case .jpeg, .png:
            guard pages.count == 1 else {
                throw MaskingError.exportFailed("画像形式は1ページのみ対応です（複数ページはPDFを使用）")
            }
            try writeImage(pages[0].image, format: options.format, quality: options.jpegQuality, to: url)
        case .pdf:
            try writePDF(pages, searchable: options.searchableText, to: url)
        }
    }

    // MARK: - 画像（JPEG/PNG）

    private func writeImage(_ image: CGImage, format: ExportOptions.Format,
                            quality: Double, to url: URL) throws {
        let type: UTType = (format == .jpeg) ? .jpeg : .png
        guard let dest = CGImageDestinationCreateWithURL(url as CFURL, type.identifier as CFString, 1, nil) else {
            throw MaskingError.exportFailed(url.path)
        }
        // 渡すのは圧縮品質のみ。メタデータ辞書（EXIF等）は決して渡さない。
        let properties: [CFString: Any]? = (format == .jpeg)
            ? [kCGImageDestinationLossyCompressionQuality: quality]
            : nil
        CGImageDestinationAddImage(dest, image, properties as CFDictionary?)
        guard CGImageDestinationFinalize(dest) else { throw MaskingError.exportFailed(url.path) }
    }

    // MARK: - PDF

    private func pointSize(of image: CGImage) -> CGSize {
        let scale = 72.0 / pdfDPI
        return CGSize(width: CGFloat(image.width) * scale, height: CGFloat(image.height) * scale)
    }

    private func writePDF(_ pages: [RenderedPage], searchable: Bool, to url: URL) throws {
        var firstBox = CGRect(origin: .zero, size: pointSize(of: pages[0].image))
        guard let ctx = CGContext(url as CFURL, mediaBox: &firstBox, nil) else {
            throw MaskingError.exportFailed(url.path)
        }
        for page in pages {
            var pageBox = CGRect(origin: .zero, size: pointSize(of: page.image))
            let boxData = withUnsafeBytes(of: &pageBox) { Data($0) }
            ctx.beginPDFPage([kCGPDFContextMediaBox as String: boxData] as CFDictionary)
            ctx.draw(page.image, in: pageBox)
            if searchable {
                drawInvisibleTextLayer(page, in: pageBox, context: ctx)
            }
            ctx.endPDFPage()
        }
        ctx.closePDF()
    }

    /// 検索可能PDFのテキスト層（不可視テキスト描画＝OCRレイヤの標準手法）。
    /// マスク矩形と**交差する**観測は部分交差でも除外する（安全側）。
    private func drawInvisibleTextLayer(_ page: RenderedPage, in pageBox: CGRect, context ctx: CGContext) {
        ctx.saveGState()
        ctx.setTextDrawingMode(.invisible)
        ctx.textMatrix = .identity
        for item in page.ocrItems {
            guard !page.maskRects.contains(where: { $0.intersects(item.box) }) else { continue }
            let rect = CoordinateSpace.pixelRect(item.box, in: pageBox.size)
            let fontSize = max(4, rect.height * 0.9)
            let font = CTFontCreateWithName("HiraginoSans-W3" as CFString, fontSize, nil)
            let attributed = NSAttributedString(
                string: item.text,
                attributes: [NSAttributedString.Key(kCTFontAttributeName as String): font])
            let line = CTLineCreateWithAttributedString(attributed)
            ctx.textPosition = CGPoint(x: rect.minX, y: rect.minY + fontSize * 0.1)
            CTLineDraw(line, ctx)
        }
        ctx.restoreGState()
    }
}
