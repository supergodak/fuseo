import Foundation

/// プリセットのルールを基準画像へ適用し、マスク候補（padding適用済み）を生成する。
/// isOn の初期値は rule.defaultOn。最終決定は確認UIのユーザー操作（設計書§1.5）。
public struct RuleEngine {
    let fieldDetector: FieldDetecting

    public init(fieldDetector: FieldDetecting = VisionFieldDetector()) {
        self.fieldDetector = fieldDetector
    }

    public func candidates(for preset: DocumentPreset, page: PageImage, ocr: [OCRItem]) -> [MaskCandidate] {
        var out: [MaskCandidate] = []
        for rule in preset.rules {
            switch rule.kind {
            case .fixed:
                guard let region = rule.region else { continue }   // validate済みだが防御
                out.append(MaskCandidate(
                    ruleID: rule.id, label: rule.label,
                    box: region.normRect.padded(by: rule.effectivePadding),
                    source: .fixedRegion, confidence: nil,
                    isOn: rule.defaultOn, basis: rule.basis))
            case .dynamic:
                guard let detectorID = rule.detector else { continue }
                // 検出子の失敗（Vision実行エラー）はルール単位で空扱い＝候補が出ないだけにする（挙動は従来通り）。
                // ただし原因追跡のためエラー内容はログに残す（従来は try? で握りつぶしていた）。
                let fields: [DetectedField]
                do {
                    fields = try fieldDetector.detect(detectorID, page: page, ocr: ocr)
                } catch {
                    MaskingLog.ruleEngine.error(
                        "検出子 \(detectorID.rawValue, privacy: .public)（rule=\(rule.id, privacy: .public)）が失敗: \(String(describing: error), privacy: .public)")
                    fields = []
                }
                for field in fields {
                    let label = field.maskedDescription.map { "\(rule.label)（\($0)）" } ?? rule.label
                    out.append(MaskCandidate(
                        ruleID: rule.id, label: label,
                        box: field.box.padded(by: rule.effectivePadding),
                        source: .detector(detectorID), confidence: field.confidence,
                        isOn: rule.defaultOn, basis: rule.basis))
                }
                // 検出ゼロ時のフォールバック固定領域（光の反射等でOCRが読めない写真の保険）。
                // ラベルに「（位置推定）」を付けて、確認UIで実測検出と区別できるようにする。
                if fields.isEmpty, let fallback = rule.fallbackRegion {
                    MaskingLog.ruleEngine.notice(
                        "検出子 \(detectorID.rawValue, privacy: .public)（rule=\(rule.id, privacy: .public)）が0件 → フォールバック領域を候補化")
                    out.append(MaskCandidate(
                        ruleID: rule.id, label: "\(rule.label)（位置推定）",
                        box: fallback.normRect.padded(by: rule.effectivePadding),
                        source: .fixedRegion, confidence: nil,
                        isOn: rule.defaultOn, basis: rule.basis))
                }
            }
        }
        return out
    }
}
