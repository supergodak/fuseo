import Foundation
import CoreGraphics

/// マスク候補（自動生成）。isOn の最終決定はユーザー（確認UI必須・設計書§1.5）。
public struct MaskCandidate: Identifiable, Sendable, Equatable, Codable {
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

// MARK: - 永続化（WP-13）
//
// 保存・復元では `id` を**保持する**（Codable 合成の init(from:) は stored property を
// そのまま読むため、上の `init` が振る新規 UUID は使われない）。確認UIの選択状態や
// アプリ層の差分更新が id で紐づくため、開き直したときに同一性が保たれることが要件。

extension MaskCandidate.Source: Codable {
    private enum CodingKeys: String, CodingKey { case kind, detector }
    /// JSON 上の表現: `{"kind":"fixedRegion"}` / `{"kind":"detector","detector":"myNumber12"}`
    private enum Kind: String, Codable { case fixedRegion, detector }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        switch try c.decode(Kind.self, forKey: .kind) {
        case .fixedRegion:
            self = .fixedRegion
        case .detector:
            self = .detector(try c.decode(DetectorID.self, forKey: .detector))
        }
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        switch self {
        case .fixedRegion:
            try c.encode(Kind.fixedRegion, forKey: .kind)
        case .detector(let id):
            try c.encode(Kind.detector, forKey: .kind)
            try c.encode(id, forKey: .detector)
        }
    }
}

/// 手動マスク（確認UIでの矩形追加＋ブラシ）。座標は基準画像正規化・左下原点。
public struct BrushStroke: Sendable, Equatable, Codable {
    public var points: [CGPoint]     // 正規化座標
    public var width: Double         // 正規化（画像短辺比）
    public init(points: [CGPoint], width: Double) {
        self.points = points
        self.width = width
    }
}

public struct ManualMask: Sendable, Equatable, Codable {
    public var rects: [NormRect] = []
    public var strokes: [BrushStroke] = []
    public init() {}
}
