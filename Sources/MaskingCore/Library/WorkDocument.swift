import Foundation

// WP-13「作業の保存と再開」のデータモデル（docs/wp13-library-design.md §1・スキーマ v1）。
//
// 保存されるのは**マスク前の書類画像**を含むデータである。端末内のみ・通信なし・
// バックアップ対象外（FileWorkLibrary が rootURL に isExcludedFromBackup を付ける）。
// ログにタイトル・ファイル名・OCR 文字列などの機微情報を出さないこと。

/// 1 件の作業（= 1 書類・複数ページ）。`<root>/<id>/document.json` に保存する。
public struct WorkDocument: Codable, Sendable, Equatable {

    /// 保存形式の版。読み込み側は未知の版を拒否する（前方互換の事故防止）。
    public static let currentSchemaVersion = 1

    public enum Status: String, Codable, Sendable {
        /// 編集中（まだ書き出していない）
        case inProgress
        /// 書き出し済み（再編集で inProgress に戻すのはアプリ層の責務）
        case exported
    }

    public var schemaVersion: Int
    public var id: UUID
    /// 一覧の表示名（元ファイル名）。
    public var title: String
    public var createdAt: Date
    public var updatedAt: Date
    public var status: Status
    /// 直近に書き出したファイル名（拡張子込み・パスは保持しない）。
    public var lastExportedName: String?
    public var pages: [WorkPage]

    public init(id: UUID = UUID(),
                title: String,
                createdAt: Date,
                updatedAt: Date,
                status: Status = .inProgress,
                lastExportedName: String? = nil,
                pages: [WorkPage] = [],
                schemaVersion: Int = WorkDocument.currentSchemaVersion) {
        self.schemaVersion = schemaVersion
        self.id = id
        self.title = title
        self.createdAt = createdAt
        self.updatedAt = updatedAt
        self.status = status
        self.lastExportedName = lastExportedName
        self.pages = pages
    }
}

/// 1 ページ分の作業状態。画像は JSON に埋めず、同じディレクトリに PNG/JPEG で置く。
public struct WorkPage: Codable, Sendable, Equatable {

    /// ページ番号（0 始まり）。ファイル名の既定値の元になる。
    public var index: Int
    /// 作業ディレクトリからの相対パス（PNG）。**解析後の基準画像**＝マスク座標の基準。
    public var imageFile: String
    /// 作業ディレクトリからの相対パス（JPEG・長辺 256px）。
    public var thumbnailFile: String
    /// 取り込み元の表示名（元ファイル名。パスは保持しない）。
    public var sourceName: String
    /// PDF 由来の平面ページか（再解析時に .fullImage を維持する判断に使う）。
    public var isFlatSource: Bool
    public var analysisOptions: AnalysisOptions
    /// 実際に適用された種別。
    public var documentType: DocumentType
    /// ユーザーが手で指定した種別（nil = 自動判定のまま）。
    public var forcedType: DocumentType?
    /// 手動で調整した切り抜き四隅（正規化・左下原点。nil = 未調整）。
    public var manualQuad: Quad?
    /// 手動回転（度・90 の倍数を想定。正規化はアプリ層）。
    public var manualRotation: Int
    /// 基準画像の OCR（検索可能 PDF のテキスト層用）。
    public var ocr: [OCRItem]
    /// マスク候補（isOn 含む。id は保存・復元で保持される）。
    public var candidates: [MaskCandidate]
    /// 手動マスク（矩形＋ブラシ）。
    public var manual: ManualMask
    /// 基準画像のピクセルサイズ（復元前に一覧・レイアウトで使うため JSON にも持つ）。
    public var pixelWidth: Int
    public var pixelHeight: Int

    public init(index: Int,
                imageFile: String? = nil,
                thumbnailFile: String? = nil,
                sourceName: String,
                isFlatSource: Bool = false,
                analysisOptions: AnalysisOptions = .default,
                documentType: DocumentType,
                forcedType: DocumentType? = nil,
                manualQuad: Quad? = nil,
                manualRotation: Int = 0,
                ocr: [OCRItem] = [],
                candidates: [MaskCandidate] = [],
                manual: ManualMask = ManualMask(),
                pixelWidth: Int,
                pixelHeight: Int) {
        self.index = index
        self.imageFile = imageFile ?? WorkPage.canonicalImageFile(index: index)
        self.thumbnailFile = thumbnailFile ?? WorkPage.canonicalThumbnailFile(index: index)
        self.sourceName = sourceName
        self.isFlatSource = isFlatSource
        self.analysisOptions = analysisOptions
        self.documentType = documentType
        self.forcedType = forcedType
        self.manualQuad = manualQuad
        self.manualRotation = manualRotation
        self.ocr = ocr
        self.candidates = candidates
        self.manual = manual
        self.pixelWidth = pixelWidth
        self.pixelHeight = pixelHeight
    }

    /// 既定のページ画像パス（`pages/000.png`）。
    public static func canonicalImageFile(index: Int) -> String {
        "pages/\(numbered(index)).png"
    }

    /// 既定のサムネイルパス（`thumbs/000.jpg`）。
    public static func canonicalThumbnailFile(index: Int) -> String {
        "thumbs/\(numbered(index)).jpg"
    }

    private static func numbered(_ index: Int) -> String {
        String(format: "%03d", max(0, index))
    }
}

/// 一覧表示用の軽量情報（`document.json` 全体を読まずに済むよう `summary.json` から読む）。
public struct WorkDocumentSummary: Sendable, Equatable, Codable {
    public var id: UUID
    public var title: String
    public var updatedAt: Date
    public var status: WorkDocument.Status
    public var pageCount: Int
    /// 1 ページ目のサムネイルの絶対 URL（ページが無い場合 nil）。
    public var thumbnailURL: URL?

    public init(id: UUID, title: String, updatedAt: Date,
                status: WorkDocument.Status, pageCount: Int, thumbnailURL: URL?) {
        self.id = id
        self.title = title
        self.updatedAt = updatedAt
        self.status = status
        self.pageCount = pageCount
        self.thumbnailURL = thumbnailURL
    }
}

// MARK: - JSON の符号化（日付は ISO8601・キー順は安定）

/// 保存用 JSON の共通設定。**ゴールデンテストで固定**しているので、変更はスキーマ変更と同義。
public enum WorkJSON {

    /// ISO8601（ミリ秒まで）。デバウンス自動保存で同一秒に複数回更新されても順序が壊れないようにする。
    private static let formatter: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    /// 小数点以下なしの ISO8601（読み込みの後方互換用）。
    private static let plainFormatter: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()

    public static func encoder(pretty: Bool = false) -> JSONEncoder {
        let e = JSONEncoder()
        e.outputFormatting = pretty ? [.sortedKeys, .prettyPrinted] : [.sortedKeys]
        e.dateEncodingStrategy = .custom { date, encoder in
            var c = encoder.singleValueContainer()
            try c.encode(formatter.string(from: date))
        }
        return e
    }

    public static func decoder() -> JSONDecoder {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .custom { decoder in
            let s = try decoder.singleValueContainer().decode(String.self)
            if let date = formatter.date(from: s) ?? plainFormatter.date(from: s) { return date }
            throw DecodingError.dataCorrupted(
                .init(codingPath: decoder.codingPath, debugDescription: "invalid ISO8601 date"))
        }
        return d
    }
}
