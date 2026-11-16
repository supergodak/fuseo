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

    /// PDFの解像度（px→pt換算）。PDF 取り込みの描画解像度（PDFRasterizer 既定 300dpi）と同じにして、
    /// PDF から取り込んだページを元の紙の大きさで書き出す（0.2.3 までは 150dpi で紙が実寸の 2 倍だった）。
    /// Windows 版の書き出し（pt = px × 72 / 300）とも同じ。
    let pdfDPI: CGFloat

    public init(pdfDPI: CGFloat = 300) {
        self.pdfDPI = pdfDPI
    }

    public func export(_ pages: [RenderedPage], options: ExportOptions, to url: URL) throws {
        guard !pages.isEmpty else { throw MaskingError.exportFailed("出力するページがありません") }
        // カラーモードは焼き込み済み画像に適用（マスクの黒は不変）。OCR・マスク矩形はそのまま。
        // 透かし（0.4.0）はその後に焼き込む（白黒の二値化で薄い透かしが消えないように）。
        let pages = pages.map { page -> RenderedPage in
            var image = ColorModeFilter.apply(options.colorMode, to: page.image)
            if let wm = options.watermark { image = WatermarkRenderer.apply(wm, to: image) }
            return RenderedPage(image: image, ocrItems: page.ocrItems, maskRects: page.maskRects)
        }
        let data: Data
        switch options.format {
        case .jpeg, .png:
            guard pages.count == 1 else {
                throw MaskingError.exportFailed("画像形式は1ページのみ対応です（複数ページはPDFを使用）")
            }
            data = try fit(limit: options.maxBytes) { step in
                try encodeImage(pages[0].image, format: options.format,
                                quality: step?.quality ?? options.jpegQuality, scale: step?.scale ?? 1)
            }
        case .pdf:
            data = try fit(limit: options.maxBytes) { step in
                try pdfData(pages, searchable: options.searchableText, step: step,
                            password: options.effectivePDFPassword)
            }
        }
        do { try data.write(to: url, options: .atomic) } catch { throw MaskingError.exportFailed(url.path) }
    }

    // MARK: - 大きさの上限（0.4.0）

    /// 画質と縮小の 1 段。
    struct SizeStep: Equatable {
        var scale: CGFloat
        var quality: Double
    }

    /// 上から順に試す。先に画質を下げ、それでも足りなければ解像度を下げる（文字の読みやすさは解像度の方が効く）。
    static let sizeSteps: [SizeStep] = [
        .init(scale: 1.0, quality: 0.85), .init(scale: 1.0, quality: 0.75), .init(scale: 1.0, quality: 0.65),
        .init(scale: 0.85, quality: 0.7), .init(scale: 0.85, quality: 0.6),
        .init(scale: 0.7, quality: 0.65), .init(scale: 0.7, quality: 0.55),
        .init(scale: 0.6, quality: 0.55), .init(scale: 0.5, quality: 0.5),
    ]

    /// 縮小の下限: 短辺がこれより小さくなる段は使わない（番号や氏名が読めなくなるため）。
    static let minShortSidePx = 600

    /// 上限なしなら従来どおり 1 回で作る（PDF は無劣化の画像）。上限ありなら段を順に試して最初に収まったものを返す。
    private func fit(limit: Int?, _ make: (SizeStep?) throws -> Data) throws -> Data {
        guard let limit else { return try make(nil) }
        var smallest: Int?
        for step in Self.sizeSteps {
            let data = try make(step)
            if data.count <= limit { return data }
            smallest = min(smallest ?? .max, data.count)
        }
        throw MaskingError.sizeLimitUnreachable(limitBytes: limit, smallestBytes: smallest ?? 0)
    }

    /// 縮小（下限より小さくはしない）。
    static func scaled(_ image: CGImage, by scale: CGFloat) -> CGImage {
        let short = min(image.width, image.height)
        let floor = short > 0 ? CGFloat(minShortSidePx) / CGFloat(short) : 1
        let s = min(1, max(scale, floor))
        guard s < 0.999 else { return image }
        let w = max(1, Int((CGFloat(image.width) * s).rounded()))
        let h = max(1, Int((CGFloat(image.height) * s).rounded()))
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return image }
        ctx.interpolationQuality = .high
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        return ctx.makeImage() ?? image
    }

    // MARK: - 画像（JPEG/PNG）

    private func encodeImage(_ image: CGImage, format: ExportOptions.Format,
                             quality: Double, scale: CGFloat) throws -> Data {
        let type: UTType = (format == .png) ? .png : .jpeg
        let out = NSMutableData()
        guard let dest = CGImageDestinationCreateWithData(out, type.identifier as CFString, 1, nil) else {
            throw MaskingError.renderFailed
        }
        // 渡すのは圧縮品質のみ。メタデータ辞書（EXIF等）は決して渡さない。
        let properties: [CFString: Any]? = (type == .jpeg)
            ? [kCGImageDestinationLossyCompressionQuality: quality]
            : nil
        CGImageDestinationAddImage(dest, Self.scaled(image, by: scale), properties as CFDictionary?)
        guard CGImageDestinationFinalize(dest) else { throw MaskingError.renderFailed }
        return out as Data
    }

    // MARK: - PDF

    private func pointSize(of image: CGImage) -> CGSize {
        let scale = 72.0 / pdfDPI
        return CGSize(width: CGFloat(image.width) * scale, height: CGFloat(image.height) * scale)
    }

    /// PDF を作る。`step` があるときはページの画像を JPEG にして埋め込む（紙の大きさは元のまま）。
    /// `password` があるときは開くときのパスワードをかける（AES-128。CoreGraphics は権限パスワードも
    /// 無いと暗号化しないため、同じ値を渡す＝開けた人は印刷・コピーもできる）。
    private func pdfData(_ pages: [RenderedPage], searchable: Bool, step: SizeStep?, password: String?) throws -> Data {
        let out = NSMutableData()
        guard let consumer = CGDataConsumer(data: out as CFMutableData) else { throw MaskingError.renderFailed }
        var aux: [CFString: Any] = [:]
        if let password {
            guard ExportOptions.isValidPDFPassword(password) else {
                throw MaskingError.exportFailed("パスワードは半角の英数字と記号（\(ExportOptions.pdfPasswordMaxLength) 文字まで）で入力してください")
            }
            aux[kCGPDFContextUserPassword] = password
            aux[kCGPDFContextOwnerPassword] = password
        }
        var firstBox = CGRect(origin: .zero, size: pointSize(of: pages[0].image))
        guard let ctx = CGContext(consumer: consumer, mediaBox: &firstBox, aux as CFDictionary) else {
            throw MaskingError.renderFailed
        }
        for page in pages {
            var pageBox = CGRect(origin: .zero, size: pointSize(of: page.image))
            let boxData = withUnsafeBytes(of: &pageBox) { Data($0) }
            ctx.beginPDFPage([kCGPDFContextMediaBox as String: boxData] as CFDictionary)
            if let step {
                let jpeg = try encodeImage(page.image, format: .jpeg, quality: step.quality, scale: step.scale)
                guard let provider = CGDataProvider(data: jpeg as CFData),
                      let embedded = CGImage(jpegDataProviderSource: provider, decode: nil,
                                             shouldInterpolate: true, intent: .defaultIntent) else {
                    throw MaskingError.renderFailed
                }
                ctx.draw(embedded, in: pageBox)          // JPEG のまま埋め込まれる（DCTDecode）
            } else {
                ctx.draw(page.image, in: pageBox)
            }
            if searchable {
                drawInvisibleTextLayer(page, in: pageBox, context: ctx)
            }
            ctx.endPDFPage()
        }
        ctx.closePDF()
        // 文書情報から macOS の版と作成・更新日時を消す。暗号化したときは文字列も暗号化されているので、
        // 平文の Producer は書かずに辞書を空にする（平文を入れると読む側で文字化けする）。
        var bytes = [UInt8](out as Data)
        if !PDFInfoScrubber.scrub(&bytes, replacement: password == nil ? PDFInfoScrubber.replacement
                                                                      : PDFInfoScrubber.emptyReplacement) {
            MaskingLog.pipeline.error("PDF の文書情報を書き換えられませんでした")
        }
        return Data(bytes)
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
