import Foundation
import CoreGraphics

/// パイプラインのオーケストレータ:
///   rectify（検出・補正・正立化）→ OCR → 種別判定 → プリセット適用（enhance/ルール）→ AnalyzedPage
/// 以降（ユーザー確認 → 焼き込み → 書き出し）は UI 層と WP-4（Render/Export）が担当する。
public final class MaskingPipeline {
    private let presets: [DocumentPreset]
    private let rectifier: DocumentRectifier
    private let recognizer: TextRecognizer
    private let classifier: DocumentClassifying
    private let ruleEngine: RuleEngine
    private let tuning: PipelineTuning

    /// - Parameters:
    ///   - presets: nil ならバンドル同梱プリセットを読む。
    ///   - tuning: パラメータ設定。**各段を省略（nil）した場合は、この tuning で標準実装を構築して伝播する**。
    ///     各段を明示注入した場合はその実装側の設定が優先される（tuning は Enhancer 強度にのみ使用）。
    ///   - rectifier/recognizer/classifier/fieldDetector: テスト時にフィクスチャ実装を差し込める（nil=標準実装）。
    public init(presets: [DocumentPreset]? = nil,
                tuning: PipelineTuning = PipelineTuning(),
                rectifier: DocumentRectifier? = nil,
                recognizer: TextRecognizer? = nil,
                classifier: DocumentClassifying? = nil,
                fieldDetector: FieldDetecting? = nil) throws {
        let loaded = try presets ?? PresetStore.loadAll()
        guard PresetStore.preset(for: .generic, in: loaded) != nil else {
            throw MaskingError.presetInvalid("generic preset is required as fallback")
        }
        MaskingLog.pipeline.info("プリセット \(loaded.count, privacy: .public) 件を読み込み")
        self.presets = loaded
        self.tuning = tuning
        // デフォルト構築時は tuning を各標準実装へ伝播（「pipelineに渡したのに効かない」罠の解消）
        self.rectifier = rectifier ?? VisionRectifier(tuning: tuning)
        self.recognizer = recognizer ?? VisionTextRecognizer(tuning: tuning)
        self.classifier = classifier ?? KeywordClassifier()
        self.ruleEngine = RuleEngine(fieldDetector: fieldDetector ?? VisionFieldDetector(tuning: tuning))
    }

    /// 切り抜き調整UI用: 元画像＋自動検出の四隅（標準実装=VisionRectifier のときのみ。それ以外は nil）。
    public func cropPreview(url: URL) throws -> VisionRectifier.CropPreview? {
        guard let vision = rectifier as? VisionRectifier else { return nil }
        return try vision.cropPreview(imageAt: url)
    }

    /// - Parameters:
    ///   - forcedType: 確認UIの「種別を変更」用。指定時は自動判定を上書きしてその種別の
    ///     プリセットを適用する（classification 自体は自動判定の結果のまま返す＝ranking表示用）。
    ///     enhance の要否がプリセットごとに異なるため、種別変更は本メソッドで URL から再解析する。
    ///   - manualQuad: 確認UIの「切り抜きを調整」用。指定時は自動の書類検出をスキップし、
    ///     この四隅（元画像の正規化・左下原点）で台形補正する。標準実装（VisionRectifier）でのみ有効。
    public func analyze(url: URL, forcedType: DocumentType? = nil,
                        manualQuad: Quad? = nil) throws -> AnalyzedPage {
        // 標準実装（VisionRectifier）は正立化の判定過程で OCR を得ているため再OCRしない高速経路を使う。
        var basePage: PageImage
        var ocr: [OCRItem]
        if let vision = rectifier as? VisionRectifier {
            (basePage, ocr) = try vision.rectifyKeepingOCR(imageAt: url, manualQuad: manualQuad)
        } else {
            basePage = try rectifier.rectify(imageAt: url)
            ocr = try recognizer.recognize(basePage)
        }

        let classification = classifier.classify(ocr: ocr, presets: presets)
        if let forcedType {
            MaskingLog.pipeline.info("種別を手動指定: \(forcedType.rawValue, privacy: .public)（自動判定=\(classification.type.rawValue, privacy: .public)）")
        }
        // 適用種別のプリセット。無ければ generic（init で存在保証済み）。
        let preset = PresetStore.preset(for: forcedType ?? classification.type, in: presets)
            ?? PresetStore.preset(for: .generic, in: presets)!

        // ID-1カードのアスペクト正規化（2パス）: 斜め撮影では台形補正後の縦横比が実物からずれ、
        // 文字が横に伸びて OCR の読み落としが起きる（dogfood実測: 長短比2.05でマイナ裏の
        // 個人番号3群のうち1群が無認識→12桁不成立）。種別が ID-1 と分かった時点で
        // 実物比 1.5858 へ再サンプルし、OCR をやり直す。正規化座標は水平スケールで意味が変わらない。
        if preset.documentType.isID1Card,
           basePage.pixelSize.width > basePage.pixelSize.height {
            let target = CGFloat(85.60 / 53.98)
            let aspect = basePage.pixelSize.width / basePage.pixelSize.height
            if abs(aspect - target) / target > 0.05,
               let resampled = Self.resample(basePage.cgImage, toAspect: target) {
                MaskingLog.pipeline.info("ID-1アスペクト正規化: 長短比 \(String(format: "%.3f", aspect), privacy: .public) → 1.586 で再OCR")
                basePage = PageImage(cgImage: resampled, sourceURL: basePage.sourceURL,
                                     rectified: basePage.rectified, quadConfidence: basePage.quadConfidence)
                ocr = try recognizer.recognize(basePage)
            }
        }

        // スキャン風仕上げは紙書類のみ（幾何不変＝OCR座標はそのまま有効。core-design.md §2.2）
        var page = basePage
        if preset.enhance, let enhanced = DocumentEnhancer.enhance(basePage.cgImage, amount: tuning.enhancerAmount) {
            page = PageImage(cgImage: enhanced, sourceURL: basePage.sourceURL,
                             rectified: basePage.rectified, quadConfidence: basePage.quadConfidence)
        }

        let candidates = ruleEngine.candidates(for: preset, page: page, ocr: ocr)
        return AnalyzedPage(page: page, ocr: ocr, classification: classification,
                            preset: preset, candidates: candidates)
    }

    /// 横長画像を目標の縦横比へ再サンプルする（高さ維持・幅をスケール。高品質補間）。
    static func resample(_ image: CGImage, toAspect target: CGFloat) -> CGImage? {
        let h = image.height
        let w = Int((CGFloat(h) * target).rounded())
        guard w > 0,
              let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8,
                                  bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.interpolationQuality = .high
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        return ctx.makeImage()
    }
}
