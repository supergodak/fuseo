import XCTest
import MaskingCore
@testable import Fuseo

/// 0.4.0: 書き出しの「提出用の設定」（透かし・大きさの上限・PDF のパスワード）のアプリ側の扱い。
@MainActor
final class ExportExtrasAppTests: XCTestCase {

    private func makeSettings() -> SettingsStore {
        SettingsStore(defaults: UserDefaults(suiteName: "fuseo.test.\(UUID().uuidString)")!)
    }

    private func makeState(_ settings: SettingsStore) async -> AppState {
        let state = AppState(analysis: FakeAnalysis { _ in TestFixtures.analyzedPage() }, settings: settings)
        await state.processFiles([URL(fileURLWithPath: "/tmp/a.jpg")])
        return state
    }

    func test_watermark_onlyWhenEnabledAndNotBlank() async {
        let state = await makeState(makeSettings())
        XCTAssertNil(state.effectiveExportOptions.watermark)
        state.watermarkText = "提出用"
        XCTAssertNil(state.effectiveExportOptions.watermark, "オフなら入れない")
        state.watermarkEnabled = true
        state.watermarkLayout = .diagonal
        XCTAssertEqual(state.effectiveExportOptions.watermark, Watermark(text: "提出用", layout: .diagonal,
                                                                         opacity: Watermark.defaultOpacity))
        state.watermarkText = "   "
        XCTAssertNil(state.effectiveExportOptions.watermark, "空白だけなら入れない")
        XCTAssertNil(state.watermarkPreview())
        state.watermarkText = "提出用"
        XCTAssertNotNil(state.watermarkPreview())
    }

    func test_appendDate_addsOnce() async {
        let state = await makeState(makeSettings())
        state.watermarkText = "〇〇提出用"
        state.appendDateToWatermark()
        state.appendDateToWatermark()
        let date = AppState.watermarkDate()
        XCTAssertEqual(state.watermarkText, "〇〇提出用 \(date)")
        XCTAssertEqual(AppState.watermarkDate(Date(timeIntervalSince1970: 0)).count, 10)   // yyyy/MM/dd
    }

    func test_password_gatesConfirm_andAppliesOnlyToPDF() async {
        let state = await makeState(makeSettings())
        state.exportOptions.format = .pdf
        XCTAssertEqual(state.pdfPasswordState, .off)
        XCTAssertTrue(state.canConfirmExport)
        state.pdfPasswordEnabled = true
        XCTAssertEqual(state.pdfPasswordState, .empty)
        XCTAssertFalse(state.canConfirmExport)
        state.pdfPassword = "パスワード"
        state.pdfPasswordConfirm = "パスワード"
        XCTAssertEqual(state.pdfPasswordState, .invalid, "半角だけ")
        XCTAssertFalse(state.canConfirmExport)
        state.pdfPassword = "abc"
        state.pdfPasswordConfirm = "abd"
        XCTAssertEqual(state.pdfPasswordState, .mismatch)
        XCTAssertFalse(state.canConfirmExport)
        XCTAssertNil(state.effectiveExportOptions.pdfPassword)
        state.pdfPasswordConfirm = "abc"
        XCTAssertTrue(state.canConfirmExport)
        XCTAssertEqual(state.effectiveExportOptions.pdfPassword, "abc")
        state.exportOptions.format = .jpeg
        XCTAssertEqual(state.pdfPasswordState, .off, "画像の形式ではパスワードを使わない")
        XCTAssertNil(state.effectiveExportOptions.effectivePDFPassword)
    }

    func test_sizeLimit_isIgnoredForPNG() async {
        let state = await makeState(makeSettings())
        state.exportOptions.maxBytes = 2_000_000
        state.exportOptions.format = .png
        XCTAssertNil(state.effectiveExportOptions.maxBytes)
        state.exportOptions.format = .jpeg
        XCTAssertEqual(state.effectiveExportOptions.maxBytes, 2_000_000)
    }

    /// 書き出し後: 配置・濃さ・上限は次回へ。文言とパスワードは持ち越さない。
    func test_remember_keepsLayoutAndLimit_butNotTextOrPassword() async {
        let settings = makeSettings()
        let state = await makeState(settings)
        state.watermarkEnabled = true
        state.watermarkText = "A社提出用"
        state.watermarkLayout = .diagonal
        state.watermarkOpacity = 0.4
        state.exportOptions.maxBytes = 3_000_000
        state.pdfPasswordEnabled = true
        state.pdfPassword = "x"; state.pdfPasswordConfirm = "x"
        state.rememberExportChoices()
        XCTAssertFalse(state.pdfPasswordEnabled)
        XCTAssertEqual(state.pdfPassword, "")
        XCTAssertEqual(settings.watermarkLayout, .diagonal)
        XCTAssertEqual(settings.watermarkOpacity, 0.4, accuracy: 1e-9)
        XCTAssertEqual(settings.exportSizeLimit, 3_000_000)

        // 新しい書類: 透かしはオフ・文言は空。配置・濃さ・上限は引き継ぐ
        state.startNewDocument()
        XCTAssertFalse(state.watermarkEnabled)
        XCTAssertEqual(state.watermarkText, "")
        XCTAssertEqual(state.watermarkLayout, .diagonal)
        XCTAssertEqual(state.exportOptions.maxBytes, 3_000_000)

        // 別のインスタンス（次の起動）でも同じ
        let reloaded = AppState(analysis: FakeAnalysis { _ in TestFixtures.analyzedPage() }, settings: settings)
        XCTAssertEqual(reloaded.watermarkLayout, .diagonal)
        XCTAssertEqual(reloaded.exportOptions.maxBytes, 3_000_000)
    }

    func test_settings_rejectUnknownLimitAndClampOpacity() {
        let d = UserDefaults(suiteName: "fuseo.test.\(UUID().uuidString)")!
        d.set(1234, forKey: "fuseo.exportSizeLimit")
        d.set(5.0, forKey: "fuseo.watermarkOpacity")
        let s = SettingsStore(defaults: d)
        XCTAssertNil(s.exportSizeLimit)
        XCTAssertEqual(s.watermarkOpacity, Watermark.opacityRange.upperBound, accuracy: 1e-9)
    }

    /// 書き出しまで通す: 透かし＋パスワード付きの PDF ができる。
    func test_export_endToEnd_withWatermarkAndPassword() async throws {
        let state = await makeState(makeSettings())
        state.exportOptions.format = .pdf
        state.watermarkEnabled = true
        state.watermarkText = "提出用"
        state.pdfPasswordEnabled = true
        state.pdfPassword = "pw-1"; state.pdfPasswordConfirm = "pw-1"
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("extras-app-\(UUID().uuidString).pdf")
        defer { try? FileManager.default.removeItem(at: url) }
        try state.export(to: url)
        let data = try Data(contentsOf: url)
        XCTAssertTrue(String(decoding: data, as: UTF8.self).contains("/Encrypt"))
    }
}
