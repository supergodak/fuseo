import XCTest
import CoreGraphics
@testable import MaskingCore

/// WP-9: プリセット英語フィールドの整合性テスト。
/// (a) 空文字 labelEn/basisEn が validate で弾かれる
/// (b) バンドル全プリセットで En カバレッジ完全（部分翻訳での出荷を機械的に防ぐ）
/// (c) フォールバック候補の labelEn に " (position estimated)" が付く
final class I18nCoverageTests: XCTestCase {

    // MARK: - (a) 空文字の英語フィールドは validate で弾かれる

    func test_emptyLabelEn_failsValidation() {
        let bad = MaskRule(id: "t.emptyLabelEn", label: "x", labelEn: "",
                           kind: .dynamic, detector: .face,
                           region: nil, defaultOn: true, basis: "b", padding: nil)
        XCTAssertThrowsError(try bad.validate(), "labelEn=空文字は presetInvalid")
    }

    func test_emptyBasisEn_failsValidation() {
        let bad = MaskRule(id: "t.emptyBasisEn", label: "x", kind: .dynamic, detector: .face,
                           region: nil, defaultOn: true, basis: "b", basisEn: "", padding: nil)
        XCTAssertThrowsError(try bad.validate(), "basisEn=空文字は presetInvalid")
    }

    func test_nilEnFields_passValidation() {
        // 英語フィールドを付けない（nil）ルールは従来どおり通る（追加的変更である証明）。
        let ok = MaskRule(id: "t.nilEn", label: "x", kind: .dynamic, detector: .face,
                          region: nil, defaultOn: true, basis: "b", padding: nil)
        XCTAssertNoThrow(try ok.validate())
    }

    // MARK: - (b) バンドル全プリセットの En カバレッジ完全性

    func test_bundledPresets_haveCompleteEnglishCoverage() throws {
        let presets = try PresetStore.loadAll()
        XCTAssertFalse(presets.isEmpty)
        for preset in presets {
            let file = preset.documentType.rawValue
            // displayNameEn: 非nil・非空
            let displayNameEn = try XCTUnwrap(preset.displayNameEn, "\(file): displayNameEn が必要")
            XCTAssertFalse(displayNameEn.isEmpty, "\(file): displayNameEn 非空")
            // warnings 数 == warningsEn 数（warnings があれば warningsEn 必須・件数一致）
            if preset.warnings.isEmpty {
                // warnings が無ければ warningsEn は nil か空のいずれか（件数一致でOK）
                XCTAssertEqual(preset.warningsEn?.count ?? 0, 0, "\(file): warnings 0件なら warningsEn も0件")
            } else {
                let warningsEn = try XCTUnwrap(preset.warningsEn, "\(file): warnings があるなら warningsEn 必須")
                XCTAssertEqual(warningsEn.count, preset.warnings.count, "\(file): warnings 数と warningsEn 数が一致")
                for (i, w) in warningsEn.enumerated() {
                    XCTAssertFalse(w.isEmpty, "\(file): warningsEn[\(i)] 非空")
                }
            }
            // 全ルール: labelEn/basisEn 非nil・非空
            for rule in preset.rules {
                let labelEn = try XCTUnwrap(rule.labelEn, "\(file)/\(rule.id): labelEn が必要")
                XCTAssertFalse(labelEn.isEmpty, "\(file)/\(rule.id): labelEn 非空")
                let basisEn = try XCTUnwrap(rule.basisEn, "\(file)/\(rule.id): basisEn が必要")
                XCTAssertFalse(basisEn.isEmpty, "\(file)/\(rule.id): basisEn 非空")
            }
        }
    }

    // MARK: - (c) フォールバック候補の labelEn 接尾辞

    private struct FakeDetector: FieldDetecting {
        let fields: [DetectedField]
        func detect(_ id: DetectorID, page: PageImage, ocr: [OCRItem]) throws -> [DetectedField] { fields }
    }

    private func makePage(width: Int = 10, height: Int = 10) -> PageImage {
        let ctx = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8,
                            bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(),
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        return PageImage(cgImage: ctx.makeImage()!, sourceURL: nil, rectified: true, quadConfidence: nil)
    }

    func test_fallbackCandidate_englishLabelHasPositionEstimatedSuffix() {
        let fallback = MaskRule.Region(x: 0.32, yTop: 0.08, w: 0.62, h: 0.28)
        let rule = MaskRule(id: "t.dyn", label: "個人番号", labelEn: "Individual Number (My Number)",
                            kind: .dynamic, detector: .myNumber12,
                            region: nil, defaultOn: true, basis: "根拠", basisEn: "Basis EN",
                            padding: nil, fallbackRegion: fallback)
        let preset = DocumentPreset(schemaVersion: 1, documentType: .myNumberCardBack, displayName: "裏",
                                    enhance: false, classification: ClassificationSpec(keywords: []),
                                    warnings: [], rules: [rule])
        // 検出ゼロ → フォールバック候補
        let cands = RuleEngine(fieldDetector: FakeDetector(fields: []))
            .candidates(for: preset, page: makePage(), ocr: [])
        XCTAssertEqual(cands.count, 1)
        XCTAssertEqual(cands[0].label, "個人番号（位置推定）", "ja側は既存挙動のまま")
        XCTAssertEqual(cands[0].labelEn, "Individual Number (My Number) (position estimated)",
                       "en側は labelEn + \" (position estimated)\"")
        XCTAssertEqual(cands[0].basisEn, "Basis EN")
    }

    func test_fallbackCandidate_nilLabelEnStaysNil() {
        // labelEn が nil のフォールバックルールは labelEn も nil のまま（ja挙動不変）。
        let fallback = MaskRule.Region(x: 0.32, yTop: 0.08, w: 0.62, h: 0.28)
        let rule = MaskRule(id: "t.dynNil", label: "個人番号", kind: .dynamic, detector: .myNumber12,
                            region: nil, defaultOn: true, basis: "根拠", padding: nil,
                            fallbackRegion: fallback)
        let preset = DocumentPreset(schemaVersion: 1, documentType: .myNumberCardBack, displayName: "裏",
                                    enhance: false, classification: ClassificationSpec(keywords: []),
                                    warnings: [], rules: [rule])
        let cands = RuleEngine(fieldDetector: FakeDetector(fields: []))
            .candidates(for: preset, page: makePage(), ocr: [])
        XCTAssertEqual(cands.count, 1)
        XCTAssertEqual(cands[0].label, "個人番号（位置推定）")
        XCTAssertNil(cands[0].labelEn, "labelEn が nil なら接尾辞も付かず nil のまま")
    }

    func test_ruleEngine_copiesEnglishFieldsToDetectorCandidate() {
        // maskedDescription なし → labelEn は素通しコピー
        let field = DetectedField(detector: .myNumber12,
                                  box: NormRect(x: 0.1, y: 0.1, w: 0.2, h: 0.05),
                                  confidence: nil, maskedDescription: nil)
        let rule = MaskRule(id: "t.dyn", label: "マイナンバー", labelEn: "My Number (12 digits)",
                            kind: .dynamic, detector: .myNumber12,
                            region: nil, defaultOn: false, basis: "根拠", basisEn: "Basis EN", padding: 0.02)
        let preset = DocumentPreset(schemaVersion: 1, documentType: .generic, displayName: "g",
                                    enhance: false, classification: ClassificationSpec(keywords: []),
                                    warnings: [], rules: [rule])
        let cands = RuleEngine(fieldDetector: FakeDetector(fields: [field]))
            .candidates(for: preset, page: makePage(), ocr: [])
        XCTAssertEqual(cands.count, 1)
        XCTAssertEqual(cands[0].label, "マイナンバー", "ja側は既存挙動のまま")
        XCTAssertEqual(cands[0].labelEn, "My Number (12 digits)", "maskedDescription 無しはそのままコピー")
        XCTAssertEqual(cands[0].basisEn, "Basis EN")
    }

    func test_ruleEngine_detectorCandidate_enLabelConcatenatesMaskedDescription() {
        // maskedDescription あり → ja=全角括弧（既存書式）、en=半角スペース＋半角括弧（Fable決定）。
        let field = DetectedField(detector: .myNumber12,
                                  box: NormRect(x: 0.1, y: 0.1, w: 0.2, h: 0.05),
                                  confidence: nil, maskedDescription: "31********37")
        let rule = MaskRule(id: "t.dyn", label: "マイナンバー", labelEn: "My Number (12 digits)",
                            kind: .dynamic, detector: .myNumber12,
                            region: nil, defaultOn: false, basis: "根拠", basisEn: "Basis EN", padding: 0.02)
        let preset = DocumentPreset(schemaVersion: 1, documentType: .generic, displayName: "g",
                                    enhance: false, classification: ClassificationSpec(keywords: []),
                                    warnings: [], rules: [rule])
        let cands = RuleEngine(fieldDetector: FakeDetector(fields: [field]))
            .candidates(for: preset, page: makePage(), ocr: [])
        XCTAssertEqual(cands.count, 1)
        XCTAssertEqual(cands[0].label, "マイナンバー（31********37）", "ja側は全角括弧のまま")
        XCTAssertEqual(cands[0].labelEn, "My Number (12 digits) (31********37)",
                       "en側は半角スペース＋半角括弧で連結")

        // labelEn が nil のルールでは labelEn は nil のまま（連結しない）。
        let ruleNil = MaskRule(id: "t.dynNilEn", label: "マイナンバー", kind: .dynamic, detector: .myNumber12,
                               region: nil, defaultOn: false, basis: "根拠", padding: 0.02)
        let presetNil = DocumentPreset(schemaVersion: 1, documentType: .generic, displayName: "g",
                                       enhance: false, classification: ClassificationSpec(keywords: []),
                                       warnings: [], rules: [ruleNil])
        let candsNil = RuleEngine(fieldDetector: FakeDetector(fields: [field]))
            .candidates(for: presetNil, page: makePage(), ocr: [])
        XCTAssertEqual(candsNil.count, 1)
        XCTAssertEqual(candsNil[0].label, "マイナンバー（31********37）")
        XCTAssertNil(candsNil[0].labelEn, "labelEn=nil なら連結せず nil のまま")
    }
}
