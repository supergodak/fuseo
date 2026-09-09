import Foundation
import CoreImage
import CoreImage.CIFilterBuiltins
import Vision

/// Vision/CoreImage 共有コンテキスト。
enum VisionSupport {
    static let ciContext = CIContext()
}

/// 標準 DocumentRectifier: 読み込み（EXIF回転適用）→ 書類検出 → 台形補正 → 正立化。
/// 書類検出の失敗は throw せず全面フォールバック（rectified=false）で続行する（core-design.md §3）。
public struct VisionRectifier: DocumentRectifier {
    /// パラメータ設定（OCR言語などを注入。既定値は現挙動と一致）。
    let tuning: PipelineTuning

    public init(tuning: PipelineTuning = PipelineTuning()) {
        self.tuning = tuning
    }

    public func rectify(imageAt url: URL) throws -> PageImage {
        try rectifyKeepingOCR(imageAt: url).page
    }

    /// 切り抜き調整UI用のプレビュー素材（EXIF回転適用済みの元画像＋自動検出の四隅）。
    public struct CropPreview {
        public let originalImage: CGImage
        public let detectedQuad: Quad?
        public let confidence: Float?
    }

    /// 元画像と自動検出の四隅を返す（確認UIの「切り抜きを調整」の初期表示用）。
    public func cropPreview(imageAt url: URL) throws -> CropPreview {
        let original = try Self.loadOriginal(url)
        let obs = try Self.detectQuad(in: original)
        return CropPreview(originalImage: original,
                           detectedQuad: obs.map(Quad.init(observation:)),
                           confidence: obs?.confidence)
    }

    /// 正立化の判定過程で得た OCR を捨てずに返す高速経路（MaskingPipeline が再OCRを避けるために使う）。
    /// - Parameter manualQuad: ユーザーが四隅を手動指定した場合。自動検出をスキップしてこの四隅で台形補正する
    ///   （rectified=true・quadConfidence=nil。正立化以降は通常どおり）。
    public func rectifyKeepingOCR(imageAt url: URL,
                                  manualQuad: Quad? = nil,
                                  options: AnalysisOptions = .default) throws -> (page: PageImage, ocr: [OCRItem]) {
        let original = try Self.loadOriginal(url)

        let rectified: CIImage
        let isRectified: Bool
        let confidence: Float?
        if let manualQuad {
            MaskingLog.rectifier.info("手動指定の四隅で台形補正: \(url.lastPathComponent, privacy: .public)")
            rectified = Self.perspectiveCorrect(CIImage(cgImage: original), quad: manualQuad)
            isRectified = true
            confidence = nil
        } else if let obs = try Self.detectQuad(in: original) {
            rectified = Self.perspectiveCorrect(CIImage(cgImage: original), quad: Quad(observation: obs))
            isRectified = true
            confidence = obs.confidence
        } else {
            // 書類セグメンテーション失敗 → 全面フォールバック（throwしない。core-design.md §3）
            MaskingLog.rectifier.notice("書類検出に失敗。全面フォールバックで続行: \(url.lastPathComponent, privacy: .public)")
            rectified = CIImage(cgImage: original)
            isRectified = false
            confidence = nil
        }

        let result: (CGImage, [OCRItem])
        if options.detectUpright {
            result = try Self.uprightOrientation(of: rectified, tuning: tuning)
        } else {
            // 平面ページ（PDF 由来など）: 向きは入力のまま。OCR は必要なら 1 回だけ。
            let normalized = rectified.transformed(by: .init(translationX: -rectified.extent.origin.x,
                                                             y: -rectified.extent.origin.y))
            guard let cg = VisionSupport.ciContext.createCGImage(normalized, from: normalized.extent) else {
                throw MaskingError.renderFailed
            }
            let items = options.recognizeText ? try VisionTextRecognizer.recognize(in: cg, tuning: tuning) : []
            result = (cg, items)
        }
        let (cg, items) = result
        let page = PageImage(cgImage: cg, sourceURL: url,
                             rectified: isRectified, quadConfidence: confidence)
        return (page, items)
    }

    // MARK: - 内部段

    /// EXIF回転を焼き込んで読み込む（スマホ写真は raw ピクセルが横倒しのため必須）。
    static func loadOriginal(_ url: URL) throws -> CGImage {
        guard let ci = CIImage(contentsOf: url, options: [.applyOrientationProperty: true]) else {
            throw MaskingError.loadFailed(url.path)
        }
        guard let original = VisionSupport.ciContext.createCGImage(ci, from: ci.extent) else {
            throw MaskingError.renderFailed
        }
        return original
    }

    static func detectQuad(in image: CGImage) throws -> VNRectangleObservation? {
        let request = VNDetectDocumentSegmentationRequest()
        let handler = VNImageRequestHandler(cgImage: image, options: [:])
        try handler.perform([request])
        return request.results?.max(by: { $0.confidence < $1.confidence })
    }

    static func perspectiveCorrect(_ image: CIImage, quad: Quad) -> CIImage {
        let size = image.extent.size
        func p(_ n: CGPoint) -> CGPoint { CGPoint(x: n.x * size.width, y: n.y * size.height) }
        let filter = CIFilter.perspectiveCorrection()
        filter.inputImage = image
        filter.topLeft = p(quad.topLeft)
        filter.topRight = p(quad.topRight)
        filter.bottomLeft = p(quad.bottomLeft)
        filter.bottomRight = p(quad.bottomRight)
        let out = filter.outputImage ?? image
        // 原点を(0,0)へ正規化（以降の座標計算の前提）
        return out.transformed(by: .init(translationX: -out.extent.origin.x, y: -out.extent.origin.y))
    }

    /// 正立化: **4方向（0/90/180/270°）すべて**を「正立スコア」で比較する。
    /// Vision は 180°逆さの文字を完全に読めるため総認識量では判別できない（WP-0知見）。
    /// 各観測の upright（テキスト四隅の上下関係）だけを加点し、全ゼロ時は総量スコアへフォールバック。
    /// 旧実装は「縦長＝横倒しのカード」と決め打ちして2択に絞っていたが、縦長の紙書類（住民票等）を
    /// 強制横倒しにするバグがあった（2026-07-08 dogfood）。4方向比較なら縦書類は縦のまま勝つ。
    static func uprightOrientation(of image: CIImage,
                                   tuning: PipelineTuning = PipelineTuning()) throws -> (CGImage, [OCRItem]) {
        let candidates: [CGImagePropertyOrientation] = [.up, .right, .down, .left]

        var best: (CGImage, [OCRItem], Double)? = nil
        var fallback: (CGImage, [OCRItem], Double)? = nil
        for orientation in candidates {
            let rotated = image.oriented(orientation)
            let normalized = rotated.transformed(by: .init(translationX: -rotated.extent.origin.x,
                                                           y: -rotated.extent.origin.y))
            guard let cg = VisionSupport.ciContext.createCGImage(normalized, from: normalized.extent) else { continue }
            let items = try VisionTextRecognizer.recognize(in: cg, tuning: tuning)
            let uprightScore = items.reduce(0.0) {
                $0 + ($1.upright ? Double($1.confidence) * Double(Self.meaningfulCharCount($1.text)) : 0)
            }
            let totalScore = items.reduce(0.0) {
                $0 + Double($1.confidence) * Double(Self.meaningfulCharCount($1.text))
            }
            if best == nil || uprightScore > best!.2 { best = (cg, items, uprightScore) }
            if fallback == nil || totalScore > fallback!.2 { fallback = (cg, items, totalScore) }
        }
        if let b = best, b.2 > 0 { return (b.0, b.1) }
        // 正立スコア全ゼロ（upright な観測が無い）→ 総量スコアで向きを決める（WP-0知見）
        MaskingLog.rectifier.notice("正立スコアが全ゼロ。総量スコアにフォールバックして向きを決定")
        guard let f = fallback else { throw MaskingError.renderFailed }
        return (f.0, f.1)
    }

    static func meaningfulCharCount(_ s: String) -> Int {
        s.unicodeScalars.filter { sc in
            (0x3040...0x30FF).contains(Int(sc.value))       // かな・カナ
            || (0x4E00...0x9FFF).contains(Int(sc.value))    // 漢字
            || (sc.properties.isAlphabetic && sc.isASCII)
            || ("0"..."9").contains(String(sc))
        }.count
    }
}

extension Quad {
    /// Vision の検出結果から（同じ正規化・左下原点なので詰め替えのみ）。
    init(observation: VNRectangleObservation) {
        self.init(topLeft: observation.topLeft, topRight: observation.topRight,
                  bottomRight: observation.bottomRight, bottomLeft: observation.bottomLeft)
    }
}

/// スキャン風仕上げ（CIDocumentEnhancer: 影除去・白背景化・コントラスト強調）。
/// 幾何は不変＝OCR座標・マスク座標はそのまま使える。プリセットの `enhance` が true の種別
/// （紙書類）にだけ適用する。カード類は顔写真・地紋が破綻するため適用しない（core-design.md §2.2）。
public enum DocumentEnhancer {
    public static func enhance(_ image: CGImage, amount: Float = 1.0) -> CGImage? {
        let filter = CIFilter.documentEnhancer()
        filter.inputImage = CIImage(cgImage: image)
        filter.amount = amount
        guard let out = filter.outputImage else { return nil }
        let normalized = out.transformed(by: .init(translationX: -out.extent.origin.x,
                                                   y: -out.extent.origin.y))
        return VisionSupport.ciContext.createCGImage(normalized, from: normalized.extent)
    }
}
