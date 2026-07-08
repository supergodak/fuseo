import Foundation
import MaskingCore

/// 解析サービスの抽象（テストでフェイクを差し込むため）。
/// パイプラインの重い処理はメインアクター外・**直列**で実行する（UI凍結禁止・wp5 §5）。
protocol Analyzing: AnyObject {
    func analyze(url: URL, forcedType: DocumentType?, manualQuad: Quad?) async throws -> AnalyzedPage
    /// 切り抜き調整シート用: 元画像＋自動検出の四隅（標準実装以外は nil）。
    func cropPreview(url: URL) async throws -> VisionRectifier.CropPreview?
    /// 種別ピッカーの表示名（`classification.ranking` の各 type を人間可読名にする）。
    func displayName(for type: DocumentType) -> String
}

/// `MaskingPipeline` を1個保持し、直列キュー上で解析する標準実装。
/// Vision は内部で並列化されるため、複数ファイルはこのキューで直列に流す（wp5 §1 Processing）。
final class AnalysisService: Analyzing {
    private let pipeline: MaskingPipeline
    private let displayNames: [DocumentType: String]
    /// 直列実行キュー（複数ファイルを1枚ずつ analyze する）。
    private let queue = DispatchQueue(label: "jp.co.ati-mirai.fuseo.analysis")

    init(tuning: PipelineTuning = PipelineTuning()) throws {
        let presets = try PresetStore.loadAll()
        self.pipeline = try MaskingPipeline(presets: presets, tuning: tuning)
        self.displayNames = Dictionary(uniqueKeysWithValues: presets.map { ($0.documentType, $0.displayName) })
    }

    func analyze(url: URL, forcedType: DocumentType?, manualQuad: Quad?) async throws -> AnalyzedPage {
        try await withCheckedThrowingContinuation { continuation in
            queue.async {
                do {
                    let page = try self.pipeline.analyze(url: url, forcedType: forcedType, manualQuad: manualQuad)
                    continuation.resume(returning: page)
                } catch {
                    continuation.resume(throwing: error)
                }
            }
        }
    }

    func cropPreview(url: URL) async throws -> VisionRectifier.CropPreview? {
        try await withCheckedThrowingContinuation { continuation in
            queue.async {
                do {
                    continuation.resume(returning: try self.pipeline.cropPreview(url: url))
                } catch {
                    continuation.resume(throwing: error)
                }
            }
        }
    }

    func displayName(for type: DocumentType) -> String {
        displayNames[type] ?? type.rawValue
    }
}
