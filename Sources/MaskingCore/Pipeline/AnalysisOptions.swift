import Foundation

/// 1 ページ解析の**任意の省略**（WP-10 長文書対応・2026-09-10 決定）。
///
/// 既定値は従来どおり（正立判定あり・文字認識あり）。省略は呼び出し側（アプリ層）が
/// ページの素性を知っているときだけ使う:
/// - PDF 由来の平面ページは `/Rotate` 適用済みなので **正立判定（4方向 OCR）が不要**。
///   OCR は 1 回で済み、所要時間は約 1/4。機能の欠落は無い。
/// - ページ数の多い PDF で時間を優先したいとき **文字認識そのものを省略**できる。
///   その場合、番号系の自動検出（OCR 文字列に依存）・種別判定（キーワード照合）・検索可能 PDF の
///   テキスト層は使えない。顔・QR/バーコードの検出（画像ベース）と手動マスク・書き出しは使える。
public struct AnalysisOptions: Sendable, Equatable, Codable {
    /// 書類の輪郭検出（自動切り抜き）の方針。`manualQuad` 指定時はそちらが優先。
    public enum DocumentDetection: String, Sendable, Codable {
        /// 写真向け: 検出できれば台形補正する（信頼度・面積の下限あり）。
        case auto
        /// 平面ページ（PDF 等）向け: ページの**中に**小さく写った書類（スキャンしたカード等）だけ切り抜く。
        /// 観測がページの大半（`insetMaxArea` 超）を占める＝ページそのものが書類なら、そのまま使う。
        /// 検出なし・不採用でも「失敗」ではなく `rectified=true` として扱う（余計な警告を出さない）。
        case insetOnly
        /// 検出しない（復元した基準画像など、既に切り抜き済みの入力）。
        case off
    }

    /// 正立判定（0/90/180/270° を OCR で比較）を行う。false = 入力の向きをそのまま採用。
    public var detectUpright: Bool
    /// 文字認識を行う。false = OCR を走らせない（`AnalyzedPage.ocr` は空・種別は generic）。
    public var recognizeText: Bool
    /// 書類の輪郭検出の方針。既定は `.auto`（従来どおり）。
    public var documentDetection: DocumentDetection
    /// `.insetOnly` で「ページの中の書類」とみなす面積の上限（正規化・画像全体=1）。
    /// 文字だけの A4 ページは観測が 0.9 超になる（実測 0.93）ので、それより小さいものだけ切り抜く。
    public var insetMaxArea: Double

    public init(detectUpright: Bool = true, recognizeText: Bool = true,
                documentDetection: DocumentDetection = .auto, insetMaxArea: Double = 0.85) {
        self.detectUpright = detectUpright
        self.recognizeText = recognizeText
        self.documentDetection = documentDetection
        self.insetMaxArea = insetMaxArea
    }

    // 旧 JSON（WP-13 保存データ）に無いキーは既定値で補う。
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        detectUpright = try c.decodeIfPresent(Bool.self, forKey: .detectUpright) ?? true
        recognizeText = try c.decodeIfPresent(Bool.self, forKey: .recognizeText) ?? true
        documentDetection = try c.decodeIfPresent(DocumentDetection.self, forKey: .documentDetection) ?? .auto
        insetMaxArea = try c.decodeIfPresent(Double.self, forKey: .insetMaxArea) ?? 0.85
    }

    /// 従来どおり（写真・スキャン画像向け）。
    public static let `default` = AnalysisOptions()
    /// PDF 由来の平面ページ向け: 正立判定を省略し、OCR は 1 回だけ。ページ内の小さな書類だけ切り抜く。
    public static let flatPage = AnalysisOptions(detectUpright: false, recognizeText: true,
                                                 documentDetection: .insetOnly)
    /// PDF 由来の平面ページで、文字認識も省略（長文書の高速取り込み）。
    public static let flatPageWithoutText = AnalysisOptions(detectUpright: false, recognizeText: false,
                                                            documentDetection: .insetOnly)
    /// 復元した基準画像向け: 切り抜き済み・正立済みなので検出も正立判定もしない。
    public static let restored = AnalysisOptions(detectUpright: false, recognizeText: true,
                                                 documentDetection: .off)

    /// 検出観測を採用するか（純関数・テスト用に公開）。
    /// - Returns: 採用なら true。`.off` は常に false。`.auto` は信頼度と面積の下限のみ、
    ///   `.insetOnly` はさらに面積の上限（ページそのものは切り抜かない）。
    public func acceptsDocumentQuad(confidence: Float, area: Double,
                                    minConfidence: Float = 0.5, minArea: Double = 0.02) -> Bool {
        switch documentDetection {
        case .off: return false
        case .auto: return confidence >= minConfidence && area >= minArea
        case .insetOnly: return confidence >= minConfidence && area >= minArea && area <= insetMaxArea
        }
    }
}
