import Foundation
import CoreGraphics
import Vision

/// OCRの1観測。text は半角正規化済み・box は基準画像正規化（左下原点）。
public struct OCRItem: Sendable, Equatable, Codable {
    public let text: String
    public let box: NormRect
    public let confidence: Float
    /// テキストが画像内で正立しているか。Vision の観測四隅は「テキスト自身の上下」を基準に付くため、
    /// topLeft.y > bottomLeft.y なら正立。**Vision は 180°逆さの文字も完全に読める**ので、
    /// 認識可否では向きを判別できない（WP-0知見・core-design.md §1.3）。
    public let upright: Bool

    public init(text: String, box: NormRect, confidence: Float, upright: Bool) {
        self.text = text
        self.box = box
        self.confidence = confidence
        self.upright = upright
    }
}

/// Vision による標準 TextRecognizer（ja+en・accurate）。
public struct VisionTextRecognizer: TextRecognizer {
    /// パラメータ設定（OCR言語を注入。既定値は現挙動と一致）。
    let tuning: PipelineTuning

    public init(tuning: PipelineTuning = PipelineTuning()) {
        self.tuning = tuning
    }

    public func recognize(_ page: PageImage) throws -> [OCRItem] {
        try Self.recognize(in: page.cgImage, tuning: tuning)
    }

    /// CGImage 直叩き（正立化の試行など内部利用）。
    static func recognize(in image: CGImage,
                          tuning: PipelineTuning = PipelineTuning()) throws -> [OCRItem] {
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.recognitionLanguages = tuning.ocrLanguages
        request.usesLanguageCorrection = true
        let handler = VNImageRequestHandler(cgImage: image, options: [:])
        try handler.perform([request])
        return (request.results ?? []).compactMap { obs in
            guard let top = obs.topCandidates(1).first else { return nil }
            // 全角→半角へ正規化（数字・英字の全角揺れを吸収）
            let half = top.string.applyingTransform(StringTransform("Fullwidth-Halfwidth"), reverse: false) ?? top.string
            return OCRItem(text: half,
                           box: CoordinateSpace.fromVision(obs.boundingBox),
                           confidence: top.confidence,
                           upright: obs.topLeft.y > obs.bottomLeft.y)
        }
    }
}
