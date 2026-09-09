import Foundation
import MaskingCore

/// 解析サービスの抽象（テストでフェイクを差し込むため）。
/// パイプラインの重い処理はメインアクター外・**直列**で実行する（UI凍結禁止・wp5 §5）。
protocol Analyzing: AnyObject {
    /// - Parameter options: 正立判定・文字認識の省略（WP-10b）。PDF 由来の平面ページは `.flatPage`、
    ///   文字認識を省いた長文書取り込みは `.flatPageWithoutText`、画像は `.default`。
    ///   **初回解析と再解析（種別変更・回転・切り抜き）で必ず同じ値を渡す**（`PageState.analysisOptions`）。
    ///   ※ プロトコル要件には既定値を書けないため、呼び出し側は常に明示する。
    func analyze(url: URL, forcedType: DocumentType?, manualQuad: Quad?, manualRotation: Int,
                 options: AnalysisOptions) async throws -> AnalyzedPage
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
    /// 種別表示名の英訳（WP-9）。UI言語が英語のときだけ使い、欠落時は ja へフォールバック。
    private let displayNamesEn: [DocumentType: String]
    /// 直列実行キュー（複数ファイルを1枚ずつ analyze する）。
    private let queue = DispatchQueue(label: "jp.co.ati-mirai.fuseo.analysis")

    init(tuning: PipelineTuning = PipelineTuning()) throws {
        let presets = try PresetStore.loadAll()
        self.pipeline = try MaskingPipeline(presets: presets, tuning: tuning)
        self.displayNames = Dictionary(uniqueKeysWithValues: presets.map { ($0.documentType, $0.displayName) })
        self.displayNamesEn = Dictionary(uniqueKeysWithValues:
            presets.compactMap { p in p.displayNameEn.map { (p.documentType, $0) } })
    }

    func analyze(url: URL, forcedType: DocumentType?, manualQuad: Quad?, manualRotation: Int,
                 options: AnalysisOptions = .default) async throws -> AnalyzedPage {
        try await withCheckedThrowingContinuation { continuation in
            queue.async {
                do {
                    let page = try self.pipeline.analyze(url: url, forcedType: forcedType, manualQuad: manualQuad,
                                                         manualRotation: manualRotation, options: options)
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

    /// 種別ピッカーの表示名。UI言語が英語なら英訳（欠落時は ja）を返す。
    /// 判定は `Bundle.main.preferredLocalizations`（UILang と同じ基準）。Mac版は en ローカリゼーション
    /// を持たないため常に ja が選ばれ、挙動は従来と1バイトも変わらない。
    func displayName(for type: DocumentType) -> String {
        let isEnglish = Bundle.main.preferredLocalizations.first?.hasPrefix("en") == true
        if isEnglish, let en = displayNamesEn[type] { return en }
        return displayNames[type] ?? type.rawValue
    }
}
