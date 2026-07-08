import Foundation

/// 書類種別（プリセットJSONの `documentType` と同一 raw value）。
/// PoC の `DocumentClass` は WP-2 で本型に置換して廃止する。
public enum DocumentType: String, Codable, CaseIterable, Sendable {
    case hokensho               // 健康保険証
    case shikakuKakuninsho      // 資格確認書
    case juminhyoMyNumber       // マイナンバー記載の住民票等（紙・A4系）
    case menkyoshoFront         // 運転免許証（表）
    case menkyoshoBack          // 運転免許証（裏）
    case myNumberCardFront      // マイナンバーカード表面
    case myNumberCardBack       // マイナンバーカード裏面（提出不可の場面が多い→presetのwarnings）
    case generic                // 種別不明のフォールバック（固定領域なし・動的検出＋手動のみ）

    /// ID-1規格（85.60×53.98mm・長短比1.5858）で**寸法が確定している**カードか。
    /// アスペクト正規化（MaskingPipeline・斜め撮影の補正）の対象判定に使う。
    /// 保険証・資格確認書はカード様式と紙様式が混在するため含めない（安全側）。
    public var isID1Card: Bool {
        switch self {
        case .menkyoshoFront, .menkyoshoBack, .myNumberCardFront, .myNumberCardBack:
            return true
        default:
            return false
        }
    }

    /// カード類（ID-1・横長）か。正立化の縦横絞り込みと enhance 既定の参考に使う。
    public var isCard: Bool {
        switch self {
        case .hokensho, .shikakuKakuninsho, .menkyoshoFront, .menkyoshoBack,
             .myNumberCardFront, .myNumberCardBack:
            return true
        case .juminhyoMyNumber, .generic:
            return false
        }
    }
}
