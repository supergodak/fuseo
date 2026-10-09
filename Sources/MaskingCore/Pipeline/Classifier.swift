import Foundation

/// キーワード重み付けによる書類種別判定（プリセットJSONの classification.keywords 駆動）。
/// 全プリセットのスコアがゼロなら `.generic` へフォールバックする。
///
/// **文字の多い画像での誤判定を防ぐ採否ゲート（2026-10-09 dogfood）**:
/// 返済明細書（文字数百）が「備考」1語だけで運転免許証（裏）と判定され、免許証用の固定領域
/// （臓器提供欄）が無関係な位置に提案された。キーワード1語は、文字の少ないカードでは十分な証拠だが
/// （実物: 斜め撮影のマイナ裏は 1語・18観測で正解）、文字の多い書類では偶然の一致になりやすい。
/// - OCR 観測数が `maxCardObservations` を超える画像では、
///   1. カード類（`isID1Card`）は候補にしない（実物カードは 13〜32 観測）
///   2. それ以外の種別も**異なるキーワード2語以上**の一致を要する
/// - `ranking` は従来どおり生スコア（確認UIの「種別を変更」候補・診断用）。採否は `type`/`score` だけに効く。
public struct KeywordClassifier: DocumentClassifying {
    /// カードとみなせる OCR 観測数の上限。実物カード 6 枚の実測 13〜32 観測（2026-10-09）に余裕を持たせた値。
    public static let maxCardObservations = 80

    public init() {}

    public func classify(ocr: [OCRItem], presets: [DocumentPreset]) -> ClassificationResult {
        let joined = ocr.map(\.text).joined().replacingOccurrences(of: " ", with: "")
        let textHeavy = ocr.count > Self.maxCardObservations

        var scored: [(type: DocumentType, score: Int, hits: Int)] = presets.map { preset in
            var score = 0
            var hits = 0
            for kw in preset.classification.keywords where joined.contains(kw.text) {
                score += kw.weight
                hits += 1
            }
            return (preset.documentType, score, hits)
        }
        scored.sort { $0.score > $1.score }
        let ranking = scored.map { (type: $0.type, score: $0.score) }

        let accepted = scored.first { candidate in
            guard candidate.score > 0 else { return false }
            guard textHeavy else { return true }
            if candidate.type.isID1Card { return false }
            return candidate.hits >= 2
        }
        if let top = accepted {
            return ClassificationResult(type: top.type, score: top.score, ranking: ranking)
        }
        return ClassificationResult(type: .generic, score: 0, ranking: ranking)
    }
}
