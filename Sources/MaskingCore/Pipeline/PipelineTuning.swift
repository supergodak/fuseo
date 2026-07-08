import Foundation

/// パイプライン各段のチューニング定数を一元管理する構造体。
///
/// これまで各実装（VisionRectifier / VisionTextRecognizer / VisionFieldDetector /
/// DocumentEnhancer 経由の呼び出し）に散らばっていたマジックナンバーを集約し、
/// イニシャライザ注入で差し替え可能にする（WP-2後半・設定化）。
///
/// **既定値は WP-0/WP-2 前半で確定した現挙動と完全一致**させてある。デフォルト引数のまま
/// 全既存呼び出しが無変更で通る。挙動を変えたい場合のみ明示的に値を渡す（設計変更ではなく
/// あくまで運用パラメータの調整点）。
public struct PipelineTuning: Sendable {
    /// 行グルーピングの中心Y許容（正規化）。box中心Yがこの範囲内なら同一行とみなす。
    /// WP-0実証値 = 0.02（core-design.md §2.3・「同一行 中心Y±2%」）。
    public var lineGroupingCenterYTolerance: Double

    /// 観測内の極大数字連続を候補化する最小桁数。これ未満はどの検出子も使わない（誤検出抑制）。
    public var minDigitRunLength: Int

    /// クレジットカード（Luhn）で候補化する桁数。14(Diners)/15(Amex)/16(主要ブランド)のみ。
    /// 13桁は製造番号等と衝突しやすく国内実流通もほぼ無いため対象外（Detectors.swift 参照）。
    public var creditCardLengths: [Int]

    /// OCR 認識言語（VNRecognizeTextRequest.recognitionLanguages）。
    public var ocrLanguages: [String]

    /// CIDocumentEnhancer の強度（0..1）。紙書類のスキャン風仕上げに使う。
    public var enhancerAmount: Float

    /// 保険者番号の桁数（8桁=健保組合等 / 6桁=市町村国保）。チェックデジット無しのため近傍キーワード必須。
    public var insurerNumberLengths: [Int]

    /// 保険証の記号・番号の桁数レンジ。保険者により様々なため広めに取る（暫定・PoC未測定）。
    public var kigoBangoLengths: [Int]

    public init(lineGroupingCenterYTolerance: Double = 0.02,
                minDigitRunLength: Int = 6,
                creditCardLengths: [Int] = [14, 15, 16],
                ocrLanguages: [String] = ["ja-JP", "en-US"],
                enhancerAmount: Float = 1.0,
                insurerNumberLengths: [Int] = [6, 8],
                kigoBangoLengths: [Int] = Array(2...10)) {
        self.lineGroupingCenterYTolerance = lineGroupingCenterYTolerance
        self.minDigitRunLength = minDigitRunLength
        self.creditCardLengths = creditCardLengths
        self.ocrLanguages = ocrLanguages
        self.enhancerAmount = enhancerAmount
        self.insurerNumberLengths = insurerNumberLengths
        self.kigoBangoLengths = kigoBangoLengths
    }
}
