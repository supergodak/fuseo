import os

/// MaskingCore 共通ロガー（os.Logger）。
///
/// ライブラリのため **stdout への print は禁止**。診断は os_log 経由で出す
/// （Console.app / `log stream` で subsystem 単位に絞れる）。生の番号など機微情報は
/// 決してログに載せない（DetectedField.maskedDescription などの部分マスク表記のみ）。
enum MaskingLog {
    static let subsystem = "jp.co.ati-mirai.fuseo"

    /// 書類検出・台形補正・正立化（VisionRectifier）。
    static let rectifier = Logger(subsystem: subsystem, category: "rectifier")
    /// 動的検出子（VisionFieldDetector）。
    static let detector = Logger(subsystem: subsystem, category: "detector")
    /// ルール適用（RuleEngine）。
    static let ruleEngine = Logger(subsystem: subsystem, category: "ruleEngine")
    /// オーケストレータ（MaskingPipeline）。
    static let pipeline = Logger(subsystem: subsystem, category: "pipeline")
}
