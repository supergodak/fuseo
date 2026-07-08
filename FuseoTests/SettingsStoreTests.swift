import XCTest
import MaskingCore
@testable import Fuseo

/// 層1: 設定の既定値と永続化（隔離 UserDefaults を注入）。
@MainActor
final class SettingsStoreTests: XCTestCase {

    private func makeDefaults() -> UserDefaults {
        UserDefaults(suiteName: "fuseo.test.\(UUID().uuidString)")!
    }

    func test_defaults_matchDesign() {
        let s = SettingsStore(defaults: makeDefaults())
        XCTAssertEqual(s.defaultExportFormat, .pdf)
        XCTAssertTrue(s.searchablePDF)
        XCTAssertEqual(s.jpegQuality, 0.9, accuracy: 0.0001)
        XCTAssertEqual(s.enhancerAmount, 1.0, accuracy: 0.0001)
        XCTAssertFalse(s.faceMaskDefaultOn)
    }

    func test_settings_persistAcrossInstances() {
        let d = makeDefaults()
        do {
            let s = SettingsStore(defaults: d)
            s.defaultExportFormat = .jpeg
            s.searchablePDF = false
            s.jpegQuality = 0.5
            s.enhancerAmount = 0.25
            s.faceMaskDefaultOn = true
        }
        let reloaded = SettingsStore(defaults: d)
        XCTAssertEqual(reloaded.defaultExportFormat, .jpeg)
        XCTAssertFalse(reloaded.searchablePDF)
        XCTAssertEqual(reloaded.jpegQuality, 0.5, accuracy: 0.0001)
        XCTAssertEqual(reloaded.enhancerAmount, 0.25, accuracy: 0.0001)
        XCTAssertTrue(reloaded.faceMaskDefaultOn)
    }

    func test_pipelineTuning_carriesEnhancerAmount() {
        let s = SettingsStore(defaults: makeDefaults())
        s.enhancerAmount = 0.4
        XCTAssertEqual(s.pipelineTuning.enhancerAmount, 0.4, accuracy: 0.0001)
    }

    func test_initialExportOptions_reflectSettings() {
        let s = SettingsStore(defaults: makeDefaults())
        s.defaultExportFormat = .png
        s.jpegQuality = 0.7
        let opts = s.initialExportOptions
        XCTAssertEqual(opts.format, .png)
        XCTAssertEqual(opts.jpegQuality, 0.7, accuracy: 0.0001)
    }
}
