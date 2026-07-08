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

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        // 未設定なら既定値。init 内の代入は didSet を発火しない（不要な書き戻しを避ける）。
        self.defaultExportFormat = ExportOptions.Format(rawValue: defaults.string(forKey: Keys.defaultExportFormat) ?? "") ?? .pdf
        self.searchablePDF = (defaults.object(forKey: Keys.searchablePDF) as? Bool) ?? true
        self.jpegQuality = (defaults.object(forKey: Keys.jpegQuality) as? Double) ?? 0.9
        self.enhancerAmount = (defaults.object(forKey: Keys.enhancerAmount) as? Float) ?? 1.0
        self.faceMaskDefaultOn = (defaults.object(forKey: Keys.faceMaskDefaultOn) as? Bool) ?? false
    }

    /// 現在の設定から書き出しシートの初期 ExportOptions を作る。
    var initialExportOptions: ExportOptions {
        ExportOptions(format: defaultExportFormat, searchableText: searchablePDF, jpegQuality: jpegQuality)
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
    }
}
