import Foundation
import CoreGraphics
import MaskingCore
@testable import Fuseo

/// 層1テスト用のフィクスチャ生成（合成画像のみ。fixtures-private は使わない）。
enum TestFixtures {
    /// 単色の合成 CGImage。
    static func cgImage(width: Int = 40, height: Int = 24) -> CGImage {
        let ctx = CGContext(data: nil, width: width, height: height,
                            bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.setFillColor(CGColor(red: 0.9, green: 0.9, blue: 0.9, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: width, height: height))
        return ctx.makeImage()!
    }

    static func pageImage(rectified: Bool = true) -> PageImage {
        PageImage(cgImage: cgImage(), sourceURL: nil, rectified: rectified, quadConfidence: 0.5)
    }

    /// バンドル同梱の実プリセットを1件返す（DocumentPreset の memberwise init は非公開のため）。
    static func preset(_ type: DocumentType = .generic) -> DocumentPreset {
        let all = try! PresetStore.loadAll()
        return PresetStore.preset(for: type, in: all) ?? PresetStore.preset(for: .generic, in: all)!
    }

    static func candidate(ruleID: String, source: MaskCandidate.Source, isOn: Bool) -> MaskCandidate {
        MaskCandidate(ruleID: ruleID, label: ruleID, box: NormRect(x: 0.1, y: 0.1, w: 0.2, h: 0.2),
                      source: source, confidence: 0.9, isOn: isOn, basis: "テスト根拠")
    }

    static func analyzedPage(type: DocumentType = .generic, candidates: [MaskCandidate] = [],
                             rectified: Bool = true) -> AnalyzedPage {
        let preset = preset(type)
        return AnalyzedPage(
            page: pageImage(rectified: rectified),
            ocr: [],
            classification: ClassificationResult(type: type, score: 1, ranking: [(type, 1)]),
            preset: preset,
            candidates: candidates)
    }
}

/// 差し込み用フェイク解析。analyze 呼び出しを記録し、指定した候補を返す。
final class FakeAnalysis: Analyzing, @unchecked Sendable {
    var pageFactory: (DocumentType?) -> AnalyzedPage
    private(set) var analyzeCallCount = 0
    private(set) var lastForcedType: DocumentType?
    private(set) var lastManualQuad: Quad?

    init(pageFactory: @escaping (DocumentType?) -> AnalyzedPage) {
        self.pageFactory = pageFactory
    }

    func analyze(url: URL, forcedType: DocumentType?, manualQuad: Quad?) async throws -> AnalyzedPage {
        analyzeCallCount += 1
        lastForcedType = forcedType
        lastManualQuad = manualQuad
        return pageFactory(forcedType)
    }

    func cropPreview(url: URL) async throws -> VisionRectifier.CropPreview? { nil }

    func displayName(for type: DocumentType) -> String { type.rawValue }
}
