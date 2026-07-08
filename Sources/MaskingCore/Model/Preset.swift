import Foundation

// プリセット＝データ駆動のマスクルールセット（core-design.md §2.2 スキーマ v1）。
// JSONは Sources/MaskingCore/Resources/Presets/<documentType>.json に置く（WP-3）。
// v2 の「提出先テンプレート」は MaskRule.id を参照する選択セットとして同スキーマ上に載る（前方互換）。

/// 動的検出子の識別子（core-design.md §2.3）。
// 検出子ID。追加時は VisionFieldDetector.detect のディスパッチにも実装を足すこと。
public enum DetectorID: String, Codable, CaseIterable, Sendable {
    case myNumber12         // マイナンバー12桁＋チェックデジット（総務省令式）
    case licenseNumber12    // 免許証番号12桁＋チェックデジット（モジュラス11・ウェイト2-7）
    case creditCardLuhn     // 13-19桁 Luhn
    case insurerNumber      // 保険者番号（8桁/6桁）＋近傍キーワード必須
    case kigoBango          // 保険証の記号・番号（キーワード近傍の数字列）
    case face               // 顔写真（既定OFFトグル用）
    case qrBarcode          // QR・バーコード
    case zairyuNumber       // 在留カード番号（英2字＋数字8桁＋英2字・書式厳格一致）
}

public struct WeightedKeyword: Codable, Sendable {
    public let text: String
    public let weight: Int
}

public struct ClassificationSpec: Codable, Sendable {
    public let keywords: [WeightedKeyword]
}

public struct MaskRule: Codable, Identifiable, Sendable {
    public enum Kind: String, Codable, Sendable {
        case fixed      // カード枠正規化座標の固定領域
        case dynamic    // 検出子による動的領域
    }

    /// JSON上の固定領域は人間が書きやすい**左上原点 yTop 表記**。
    /// 内部座標へは `normRect` でのみ変換する（自前で 1−y−h 計算をしない）。
    public struct Region: Codable, Sendable {
        public let x: Double
        public let yTop: Double
        public let w: Double
        public let h: Double
        public var normRect: NormRect {
            CoordinateSpace.fromTopOrigin(x: x, yTop: yTop, w: w, h: h)
        }
    }

    public let id: String           // "<種別略称>.<欄名>" で一意（v2テンプレートが参照する）
    public let label: String        // 確認UIの表示名
    public let kind: Kind
    public let detector: DetectorID?    // kind == .dynamic で必須
    public let region: Region?          // kind == .fixed で必須
    public let defaultOn: Bool
    public let basis: String        // 根拠（法令/実務）。全ルール必須・UI表示する
    public let padding: Double?     // マスク外周余白（正規化）。nil は既定値
    /// dynamic ルール専用: 検出子が1件も見つけられなかったときに使う固定領域（yTop表記）。
    /// 書式が規格で固定されている欄（マイナ裏の個人番号等）の「読めなくても位置で塗る」保険。
    /// 候補ラベルには「（位置推定）」を付けて確認UIで区別できるようにする。
    public var fallbackRegion: Region? = nil

    public var effectivePadding: Double { padding ?? 0.01 }

    /// スキーマ整合性（ローダが全ルールに対して呼ぶ）。
    public func validate() throws {
        switch kind {
        case .fixed:
            guard region != nil else { throw MaskingError.presetInvalid("\(id): fixed rule requires region") }
            guard fallbackRegion == nil else {
                throw MaskingError.presetInvalid("\(id): fallbackRegion is only for dynamic rules")
            }
        case .dynamic:
            guard detector != nil else { throw MaskingError.presetInvalid("\(id): dynamic rule requires detector") }
        }
        guard !basis.isEmpty else { throw MaskingError.presetInvalid("\(id): basis is required") }
    }
}

public struct DocumentPreset: Codable, Identifiable, Sendable {
    public let schemaVersion: Int
    public let documentType: DocumentType
    public let displayName: String
    /// 基準画像に CIDocumentEnhancer を掛けるか（カード類=false / 紙書類=true。core-design.md §2.2）
    public let enhance: Bool
    public let classification: ClassificationSpec
    public let warnings: [String]
    public let rules: [MaskRule]

    public var id: DocumentType { documentType }
}

/// バンドル内プリセットの読み込みと検証。
public enum PresetStore {
    public static let currentSchemaVersion = 1

    /// バンドル同梱プリセット（Resources/Presets）を読む標準経路。
    public static func loadAll() throws -> [DocumentPreset] {
        try loadAll(from: .module)   // Bundle.module は internal のため public デフォルト引数にできない
    }

    /// テスト・差し替え用。
    public static func loadAll(from bundle: Bundle) throws -> [DocumentPreset] {
        let urls = bundle.urls(forResourcesWithExtension: "json", subdirectory: nil) ?? []
        var presets: [DocumentPreset] = []
        let decoder = JSONDecoder()
        for url in urls {
            let data = try Data(contentsOf: url)
            let preset = try decoder.decode(DocumentPreset.self, from: data)
            guard preset.schemaVersion == currentSchemaVersion else {
                throw MaskingError.presetInvalid("\(url.lastPathComponent): schemaVersion \(preset.schemaVersion) != \(currentSchemaVersion)")
            }
            let ids = preset.rules.map(\.id)
            guard Set(ids).count == ids.count else {
                throw MaskingError.presetInvalid("\(url.lastPathComponent): duplicate rule id")
            }
            try preset.rules.forEach { try $0.validate() }
            presets.append(preset)
        }
        guard !presets.isEmpty else { throw MaskingError.presetInvalid("no presets found in bundle") }
        return presets.sorted { $0.documentType.rawValue < $1.documentType.rawValue }
    }

    public static func preset(for type: DocumentType, in presets: [DocumentPreset]) -> DocumentPreset? {
        presets.first { $0.documentType == type }
    }
}
