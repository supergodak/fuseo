import Foundation
import Observation
import MaskingCore

/// アプリのスカラ設定の単一の真実源（Tameo型・@Observable＋UserDefaults 手書き永続化）。
///
/// 方針: `@Observable` で UI へ反映しつつ、各プロパティの `didSet` で `UserDefaults` へ永続化する。
/// テスト用に `UserDefaults(suiteName:)` を注入できる（実ユーザー設定を汚さない）。
/// キーと既定値は wp5-ui-design.md §4 の表に一致させる。
@MainActor
@Observable
final class SettingsStore {
    private let defaults: UserDefaults

    /// 書き出しシートの初期形式（既定 pdf）。
    var defaultExportFormat: ExportOptions.Format {
        didSet { defaults.set(defaultExportFormat.rawValue, forKey: Keys.defaultExportFormat) }
    }

    /// 検索可能PDFを既定で有効にするか（既定 true）。PDF選択時のみ意味を持つ。
    var searchablePDF: Bool {
        didSet { defaults.set(searchablePDF, forKey: Keys.searchablePDF) }
    }

    /// JPEG 書き出し品質（0.1..1.0・既定 0.9）。
    var jpegQuality: Double {
        didSet { defaults.set(jpegQuality, forKey: Keys.jpegQuality) }
    }

    /// スキャン風仕上げ強度（0..1・既定 1.0）。紙書類のみ効く（`PipelineTuning.enhancerAmount` へ注入）。
    var enhancerAmount: Float {
        didSet { defaults.set(enhancerAmount, forKey: Keys.enhancerAmount) }
    }

    /// 顔写真マスクを解析後に既定でONにするか（既定 false）。`source == .detector(.face)` の候補へ適用。
    var faceMaskDefaultOn: Bool {
        didSet { defaults.set(faceMaskDefaultOn, forKey: Keys.faceMaskDefaultOn) }
    }

    /// 透かしの配置と濃さ（0.4.0）。前に書き出したときのものを引き継ぐ。**文言は覚えない**
    /// （提出先の名前が入るため。前の提出先の名前で次の書類に透かしを入れる事故を防ぐ）。
    var watermarkLayout: Watermark.Layout {
        didSet { defaults.set(watermarkLayout.rawValue, forKey: Keys.watermarkLayout) }
    }
    var watermarkOpacity: Double {
        didSet { defaults.set(watermarkOpacity, forKey: Keys.watermarkOpacity) }
    }

    /// ファイルの大きさの上限（バイト・0.4.0）。nil = 上限なし。前に書き出したときのものを引き継ぐ。
    var exportSizeLimit: Int? {
        didSet { defaults.set(exportSizeLimit ?? 0, forKey: Keys.exportSizeLimit) }
    }

    /// 選べる上限（1 MB = 1,000,000 バイト）。
    static let sizeLimitChoices: [Int] = [1_000_000, 2_000_000, 3_000_000, 5_000_000, 10_000_000]

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        // 未設定なら既定値。init 内の代入は didSet を発火しない（不要な書き戻しを避ける）。
        self.defaultExportFormat = ExportOptions.Format(rawValue: defaults.string(forKey: Keys.defaultExportFormat) ?? "") ?? .pdf
        self.searchablePDF = (defaults.object(forKey: Keys.searchablePDF) as? Bool) ?? true
        self.jpegQuality = (defaults.object(forKey: Keys.jpegQuality) as? Double) ?? 0.9
        self.enhancerAmount = (defaults.object(forKey: Keys.enhancerAmount) as? Float) ?? 1.0
        self.faceMaskDefaultOn = (defaults.object(forKey: Keys.faceMaskDefaultOn) as? Bool) ?? false
        self.watermarkLayout = Watermark.Layout(rawValue: defaults.string(forKey: Keys.watermarkLayout) ?? "") ?? .tiled
        let opacity = (defaults.object(forKey: Keys.watermarkOpacity) as? Double) ?? Watermark.defaultOpacity
        self.watermarkOpacity = min(max(opacity, Watermark.opacityRange.lowerBound), Watermark.opacityRange.upperBound)
        let limit = defaults.integer(forKey: Keys.exportSizeLimit)
        self.exportSizeLimit = Self.sizeLimitChoices.contains(limit) ? limit : nil
    }

    /// 現在の設定から書き出しシートの初期 ExportOptions を作る。
    var initialExportOptions: ExportOptions {
        ExportOptions(format: defaultExportFormat, searchableText: searchablePDF, jpegQuality: jpegQuality,
                      maxBytes: exportSizeLimit)
    }

    /// パイプラインへ渡すチューニング（enhancerAmount のみ設定由来。他は既定）。
    var pipelineTuning: PipelineTuning {
        PipelineTuning(enhancerAmount: enhancerAmount)
    }

    private enum Keys {
        static let defaultExportFormat = "fuseo.defaultExportFormat"
        static let searchablePDF = "fuseo.searchablePDF"
        static let jpegQuality = "fuseo.jpegQuality"
        static let enhancerAmount = "fuseo.enhancerAmount"
        static let faceMaskDefaultOn = "fuseo.faceMaskDefaultOn"
        static let watermarkLayout = "fuseo.watermarkLayout"
        static let watermarkOpacity = "fuseo.watermarkOpacity"
        static let exportSizeLimit = "fuseo.exportSizeLimit"
    }
}
