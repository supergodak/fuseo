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
public struct AnalysisOptions: Sendable, Equatable {
    /// 正立判定（0/90/180/270° を OCR で比較）を行う。false = 入力の向きをそのまま採用。
    public var detectUpright: Bool
    /// 文字認識を行う。false = OCR を走らせない（`AnalyzedPage.ocr` は空・種別は generic）。
    public var recognizeText: Bool

    public init(detectUpright: Bool = true, recognizeText: Bool = true) {
        self.detectUpright = detectUpright
        self.recognizeText = recognizeText
    }

    /// 従来どおり（写真・スキャン画像向け）。
    public static let `default` = AnalysisOptions()
    /// PDF 由来の平面ページ向け: 正立判定を省略し、OCR は 1 回だけ。
    public static let flatPage = AnalysisOptions(detectUpright: false, recognizeText: true)
    /// PDF 由来の平面ページで、文字認識も省略（長文書の高速取り込み）。
    public static let flatPageWithoutText = AnalysisOptions(detectUpright: false, recognizeText: false)
}
