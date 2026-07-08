import Foundation

/// キーワード重み付けによる書類種別判定（プリセットJSONの classification.keywords 駆動）。
/// 全プリセットのスコアがゼロなら `.generic` へフォールバックする。
public struct KeywordClassifier: DocumentClassifying {
    public init() {}

    public func classify(ocr: [OCRItem], presets: [DocumentPreset]) -> ClassificationResult {
        let joined = ocr.map(\.text).joined().replacingOccurrences(of: " ", with: "")
        var ranking: [(type: DocumentType, score: Int)] = presets.map { preset in
            let score = preset.classification.keywords.reduce(0) { acc, kw in
                joined.contains(kw.text) ? acc + kw.weight : acc
            }
            return (preset.documentType, score)
        }
        ranking.sort { $0.score > $1.score }

        if let top = ranking.first, top.score > 0 {
            return ClassificationResult(type: top.type, score: top.score, ranking: ranking)
        }
        return ClassificationResult(type: .generic, score: 0, ranking: ranking)
    }
}
