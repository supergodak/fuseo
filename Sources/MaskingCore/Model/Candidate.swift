import Foundation
import CoreGraphics

/// マスク候補（自動生成）。isOn の最終決定はユーザー（確認UI必須・設計書§1.5）。
public struct MaskCandidate: Identifiable, Sendable {
    public enum Source: Equatable, Sendable {
        case fixedRegion
        case detector(DetectorID)
    }

    public let id: UUID
    public let ruleID: String
    public let label: String
    /// 表示名の英訳（WP-9）。nil はja(label)へフォールバック。iOS英語UIで使用。
    public let labelEn: String?
    /// 基準画像正規化・左下原点。**rule.effectivePadding 適用済み**の最終マスク矩形。
    /// 確認UIでユーザーが移動・リサイズできる（自動検出が完璧でない前提・設計書§1.5）。
    public var box: NormRect
    public let source: Source
    public let confidence: Float?
    /// 初期値 = rule.defaultOn。確認UIでユーザーが編集する。
    public var isOn: Bool
    public let basis: String
    /// 根拠の英訳（WP-9）。nil はja(basis)へフォールバック。iOS英語UIで使用。
    public let basisEn: String?

    public init(ruleID: String, label: String, box: NormRect, source: Source,
                confidence: Float?, isOn: Bool, basis: String,
                labelEn: String? = nil, basisEn: String? = nil) {
        self.id = UUID()
        self.ruleID = ruleID
        self.label = label
        self.labelEn = labelEn
        self.box = box
        self.source = source
        self.confidence = confidence
        self.isOn = isOn
        self.basis = basis
        self.basisEn = basisEn
    }
}

/// 手動マスク（確認UIでの矩形追加＋ブラシ）。座標は基準画像正規化・左下原点。
public struct BrushStroke: Sendable {
    public var points: [CGPoint]     // 正規化座標
    public var width: Double         // 正規化（画像短辺比）
    public init(points: [CGPoint], width: Double) {
        self.points = points
        self.width = width
    }
}

public struct ManualMask: Sendable {
    public var rects: [NormRect] = []
    public var strokes: [BrushStroke] = []
    public init() {}
}
