import Foundation
import CoreGraphics

// パイプライン各段の I/O 型と protocol（core-design.md §3 が正）。
// 標準実装は Vision/CoreImage 版（WP-2）。テストはフィクスチャ実装を差し込む。

// MARK: - I/O 型

/// 基準画像＝書類検出→台形補正→正立化済みの画像。後段はすべてこれを対象にする。
public struct PageImage {
    public let cgImage: CGImage
    public let pixelSize: CGSize
    /// 原本ファイル参照（出力には含めない。セッション終了時に破棄可能）
    public let sourceURL: URL?
    /// 書類検出に成功したか（false = 全面フォールバック。エラーにはしない）
    public let rectified: Bool
    public let quadConfidence: Float?

    public init(cgImage: CGImage, sourceURL: URL?, rectified: Bool, quadConfidence: Float?) {
        self.cgImage = cgImage
        self.pixelSize = CGSize(width: cgImage.width, height: cgImage.height)
        self.sourceURL = sourceURL
        self.rectified = rectified
        self.quadConfidence = quadConfidence
    }
}

public struct ClassificationResult: Sendable {
    public let type: DocumentType
    public let score: Int
    /// スコア降順の全順位（診断・確認UIの「種別を変更」候補に使う）
    public let ranking: [(type: DocumentType, score: Int)]

    public init(type: DocumentType, score: Int, ranking: [(type: DocumentType, score: Int)]) {
        self.type = type
        self.score = score
        self.ranking = ranking
    }
}

/// 動的検出子の出力。RuleEngine が MaskCandidate へ変換する（padding適用はその時点）。
public struct DetectedField: Sendable {
    public let detector: DetectorID
    public let box: NormRect
    public let confidence: Float?
    /// ログ・確認UI表示用の部分マスク済み説明（例: "31********37"）。生の番号は保持しない。
    public let maskedDescription: String?

    public init(detector: DetectorID, box: NormRect, confidence: Float?, maskedDescription: String?) {
        self.detector = detector
        self.box = box
        self.confidence = confidence
        self.maskedDescription = maskedDescription
    }
}

/// analyze() の結果スナップショット。確認UIはこれを表示・編集する。
public struct AnalyzedPage {
    public let page: PageImage
    public let ocr: [OCRItem]
    public let classification: ClassificationResult
    public let preset: DocumentPreset
    public var candidates: [MaskCandidate]
    public var manual: ManualMask

    public init(page: PageImage, ocr: [OCRItem], classification: ClassificationResult,
                preset: DocumentPreset, candidates: [MaskCandidate], manual: ManualMask = ManualMask()) {
        self.page = page
        self.ocr = ocr
        self.classification = classification
        self.preset = preset
        self.candidates = candidates
        self.manual = manual
    }

    /// 焼き込み対象の最終マスク矩形（isOn の候補＋手動矩形。ブラシは renderer が別途扱う）。
    public var effectiveMaskRects: [NormRect] {
        candidates.filter(\.isOn).map(\.box) + manual.rects
    }
}

/// Exporter への入力（検索可能PDFのテキスト層からマスク下の文字を除外するため OCR とマスクを併せて渡す）。
public struct RenderedPage {
    public let image: CGImage            // 焼き込み済み
    public let ocrItems: [OCRItem]       // 基準画像のOCR（テキスト層合成用）
    public let maskRects: [NormRect]     // 適用済みマスク（交差する OCRItem をテキスト層から除外）

    public init(image: CGImage, ocrItems: [OCRItem], maskRects: [NormRect]) {
        self.image = image
        self.ocrItems = ocrItems
        self.maskRects = maskRects
    }
}

public struct ExportOptions: Sendable {
    public enum Format: String, Sendable { case pdf, jpeg, png }
    /// 出力のカラーモード（v1.1 スキャンユーティリティ）。**焼き込み後**に適用するため
    /// マスクの黒は常に黒のまま＝墨消しの正しさに影響しない。
    public enum ColorMode: String, Sendable, CaseIterable {
        case color        // 元のまま
        case grayscale    // グレースケール（彩度0）
        case blackWhite   // 白黒二値（Otsu自動しきい値・文書向け）
    }
    public var format: Format
    public var searchableText: Bool      // pdf のみ有効
    public var jpegQuality: Double
    public var colorMode: ColorMode

    public init(format: Format, searchableText: Bool = false, jpegQuality: Double = 0.9,
                colorMode: ColorMode = .color) {
        self.format = format
        self.searchableText = searchableText
        self.jpegQuality = jpegQuality
        self.colorMode = colorMode
    }
}

public enum MaskingError: Error, CustomStringConvertible {
    case loadFailed(String)
    case renderFailed
    case presetInvalid(String)
    case exportFailed(String)

    public var description: String {
        switch self {
        case .loadFailed(let path): return "画像を読み込めません: \(path)"
        case .renderFailed: return "画像の生成に失敗しました"
        case .presetInvalid(let reason): return "プリセット定義が不正です: \(reason)"
        case .exportFailed(let reason): return "書き出しに失敗しました: \(reason)"
        }
    }
}

// MARK: - 各段の protocol

public protocol DocumentRectifier {
    /// 読み込み（EXIF回転適用）→ 書類検出 → 台形補正 → 正立化。検出失敗は全面フォールバック（throwしない）。
    func rectify(imageAt url: URL) throws -> PageImage
}

public protocol TextRecognizer {
    func recognize(_ page: PageImage) throws -> [OCRItem]
}

public protocol DocumentClassifying {
    func classify(ocr: [OCRItem], presets: [DocumentPreset]) -> ClassificationResult
}

public protocol FieldDetecting {
    func detect(_ id: DetectorID, page: PageImage, ocr: [OCRItem]) throws -> [DetectedField]
}

public protocol MaskRendering {
    /// ラスタ焼き込み: 新規ビットマップに基準画像を描き、マスクを完全不透明（黒）で塗って返す。
    /// α合成・注釈・レイヤーは禁止（core-design.md §4）。
    func burnIn(page: PageImage, masks: [NormRect], strokes: [BrushStroke]) throws -> CGImage
}

public protocol Exporting {
    /// メタデータなしで出力する（EXIF/GPS/サムネイル引き継ぎ禁止）。
    /// PDF は焼き込み済みラスタのみ埋め込み、テキスト層はマスク交差 OCRItem を除外して合成。
    func export(_ pages: [RenderedPage], options: ExportOptions, to url: URL) throws
}
