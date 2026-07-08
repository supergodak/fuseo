import XCTest
import CoreGraphics
@testable import MaskingCore

/// WP-2後半で追加した画像不要のユニットテスト群。
/// - VisionFieldDetector の数字抽出・行グルーピング・キーワード近傍（合成OCRItemで検証）
/// - KeywordClassifier のスコア勝者／全ゼロ→generic
/// - RuleEngine のフェイク FieldDetecting による fixed/dynamic/defaultOn 検証
final class PipelineUnitTests: XCTestCase {

    // MARK: - 合成ヘルパ

    /// 中心Y=`midY`・高さ`h`・x開始`x`・幅`w` の OCRItem を作る（left下原点・正規化）。
    private func item(_ text: String, x: Double, midY: Double,
                      w: Double = 0.08, h: Double = 0.03, conf: Float = 0.9,
                      upright: Bool = true) -> OCRItem {
        OCRItem(text: text, box: NormRect(x: x, y: midY - h / 2, w: w, h: h),
                confidence: conf, upright: upright)
    }

    /// テスト用のダミー基準画像（RuleEngine が PageImage を要求するため）。
    private func makePage(width: Int = 10, height: Int = 10) -> PageImage {
        let ctx = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8,
                            bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(),
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        return PageImage(cgImage: ctx.makeImage()!, sourceURL: nil, rectified: true, quadConfidence: nil)
    }

    // MARK: - VisionFieldDetector: 行内連結（B経路）

    func test_digitSequences_concatenatesSameLine12Digits() {
        // 「3120」「7397」「8337」が同一行（中心Y一致）→ x昇順連結で "312073978337"（12桁）
        let ocr = [
            item("3120", x: 0.10, midY: 0.50),
            item("7397", x: 0.20, midY: 0.50),
            item("8337", x: 0.30, midY: 0.50),
        ]
        let seqs = VisionFieldDetector.digitSequences(ocr: ocr)
        XCTAssertTrue(seqs.contains { $0.digits == "312073978337" && !$0.isReversedVariant },
                      "同一行の分割数字が正順で12桁連結されること")
        // 逆順の保険も出る（チェックデジットが誤採用を防ぐ前提）
        XCTAssertTrue(seqs.contains { $0.digits == "833773973120" && $0.isReversedVariant })
    }

    func test_digitSequences_doesNotConcatenateDifferentLines() {
        // 中心Yが2%超離れた別行 → B経路の連結対象にならない（各行の数字観測が1個ずつ）
        let ocr = [
            item("3120", x: 0.10, midY: 0.50),
            item("7397", x: 0.10, midY: 0.40),   // 0.10差 >> 0.02
            item("8337", x: 0.10, midY: 0.30),
        ]
        let seqs = VisionFieldDetector.digitSequences(ocr: ocr)
        XCTAssertFalse(seqs.contains { $0.digits.count == 12 }, "別行の数字は12桁連結されないこと")
        // A経路も4桁<最小6桁なので何も出ない
        XCTAssertTrue(seqs.isEmpty)
    }

    func test_digitSequences_concatenatesTiltedLine() {
        // 傾き残りで中心Yが2%超ずつずれる3群（実測: 斜め写真のマイナ裏）。
        // 縦区間は重なるので同一行として連結されること。
        let ocr = [
            item("3120", x: 0.30, midY: 0.770, h: 0.12),
            item("7397", x: 0.50, midY: 0.815, h: 0.12),   // Δ0.045 > 0.02 だが縦区間は大きく重なる
            item("8337", x: 0.70, midY: 0.860, h: 0.12),
        ]
        let seqs = VisionFieldDetector.digitSequences(ocr: ocr)
        XCTAssertTrue(seqs.contains { $0.digits == "312073978337" && !$0.isReversedVariant },
                      "斜めの行でも縦区間の重なりで12桁連結されること")
    }

    func test_groupIntoLines_adjacentRowsWithTinyOverlapStaySeparate() {
        // 行間が詰まって縦区間がわずかに触れる隣接行（重なり < 文字高の35%）は別行のまま。
        let ocr = [
            item("1111", x: 0.10, midY: 0.500, h: 0.04),   // 範囲 0.48-0.52
            item("2222", x: 0.10, midY: 0.545, h: 0.04),   // 範囲 0.525-0.565（重なりゼロ・Δ0.045）
        ]
        let lines = VisionFieldDetector.groupIntoLines(ocr)
        XCTAssertEqual(lines.count, 2)
    }

    func test_groupIntoLines_splitsByCenterYTolerance() {
        let ocr = [
            item("A", x: 0.10, midY: 0.500),
            item("B", x: 0.20, midY: 0.515),   // 0.015差 < 0.02 → 同一行
            item("C", x: 0.10, midY: 0.400),   // 別行
        ]
        let lines = VisionFieldDetector.groupIntoLines(ocr)
        XCTAssertEqual(lines.count, 2)
        XCTAssertEqual(lines.first?.count, 2)
    }

    func test_digitSequences_phoneNumberDoesNotProduce12Digit() {
        // 「0570-783-578(24時間受付)」: ハイフンで分断され最長4桁 → 12桁候補は出ない
        let ocr = [item("0570-783-578(24時間受付)", x: 0.10, midY: 0.50, w: 0.5)]
        let seqs = VisionFieldDetector.digitSequences(ocr: ocr)
        XCTAssertFalse(seqs.contains { $0.digits.count == 12 })
        // myNumber 検出子に通しても候補ゼロ
        let fields = VisionFieldDetector.numberFields(
            ocr: ocr, lengths: [12], validate: Checkdigits.isValidMyNumber, detector: .myNumber12)
        XCTAssertTrue(fields.isEmpty)
    }

    // MARK: - VisionFieldDetector: キーワード近傍（保険証系）

    func test_keywordProximity_insurerNumberPicksEightDigits() {
        // 「保険者番号」行の8桁を insurerNumber が拾う
        let ocr = [
            item("保険者番号", x: 0.10, midY: 0.60, w: 0.2),
            item("01234567", x: 0.35, midY: 0.60, w: 0.2),
        ]
        let fields = VisionFieldDetector.keywordProximityDigits(
            ocr: ocr, lengths: [6, 8], keywords: ["保険者番号"], detector: .insurerNumber)
        XCTAssertEqual(fields.count, 1)
        XCTAssertEqual(fields.first?.detector, .insurerNumber)
    }

    func test_keywordProximity_kigoBangoExcludesInsurerAndKojin() {
        // 「個人番号」行は excludeKeywords により kigoBango から除外される
        let kojin = [
            item("個人番号", x: 0.10, midY: 0.60, w: 0.2),
            item("12345678", x: 0.35, midY: 0.60, w: 0.2),
        ]
        let excluded = VisionFieldDetector.keywordProximityDigits(
            ocr: kojin, lengths: Array(2...10), keywords: ["記号", "番号"],
            excludeKeywords: ["保険者番号", "個人番号"], detector: .kigoBango)
        XCTAssertTrue(excluded.isEmpty, "個人番号行はkigoBangoに含めない")

        // 「保険者番号」行も同様に kigoBango からは除外（insurerNumber 側で拾う分担）
        let hokensha = [
            item("保険者番号", x: 0.10, midY: 0.60, w: 0.2),
            item("01234567", x: 0.35, midY: 0.60, w: 0.2),
        ]
        let excluded2 = VisionFieldDetector.keywordProximityDigits(
            ocr: hokensha, lengths: Array(2...10), keywords: ["記号", "番号"],
            excludeKeywords: ["保険者番号", "個人番号"], detector: .kigoBango)
        XCTAssertTrue(excluded2.isEmpty)
    }

    // MARK: - KeywordClassifier

    private func preset(_ type: DocumentType, keywords: [(String, Int)],
                        rules: [MaskRule] = []) -> DocumentPreset {
        DocumentPreset(
            schemaVersion: 1, documentType: type, displayName: type.rawValue,
            enhance: false,
            classification: ClassificationSpec(keywords: keywords.map { WeightedKeyword(text: $0.0, weight: $0.1) }),
            warnings: [], rules: rules)
    }

    func test_classifier_picksHighestScore() {
        let presets = [
            preset(.menkyoshoFront, keywords: [("運転免許証", 6)]),
            preset(.myNumberCardFront, keywords: [("個人番号カード", 6)]),
            preset(.generic, keywords: []),
        ]
        let ocr = [item("運転免許証", x: 0.1, midY: 0.9, w: 0.3)]
        let result = KeywordClassifier().classify(ocr: ocr, presets: presets)
        XCTAssertEqual(result.type, .menkyoshoFront)
        XCTAssertEqual(result.score, 6)
    }

    func test_classifier_allZeroFallsBackToGeneric() {
        let presets = [
            preset(.menkyoshoFront, keywords: [("運転免許証", 6)]),
            preset(.generic, keywords: []),
        ]
        let ocr = [item("該当語なし", x: 0.1, midY: 0.9, w: 0.3)]
        let result = KeywordClassifier().classify(ocr: ocr, presets: presets)
        XCTAssertEqual(result.type, .generic)
        XCTAssertEqual(result.score, 0)
    }

    // MARK: - RuleEngine（フェイク FieldDetecting）

    /// 指定した DetectedField を常に返すスタブ（プロトコル準拠）。
    private struct FakeDetector: FieldDetecting {
        let fields: [DetectedField]
        func detect(_ id: DetectorID, page: PageImage, ocr: [OCRItem]) throws -> [DetectedField] { fields }
    }

    func test_ruleEngine_fixedAppliesPadding() {
        let rule = MaskRule(id: "t.fixed", label: "固定欄", kind: .fixed, detector: nil,
                            region: MaskRule.Region(x: 0.2, yTop: 0.3, w: 0.4, h: 0.1),
                            defaultOn: true, basis: "根拠", padding: 0.05)
        let preset = DocumentPreset(schemaVersion: 1, documentType: .generic, displayName: "g",
                                    enhance: false, classification: ClassificationSpec(keywords: []),
                                    warnings: [], rules: [rule])
        let engine = RuleEngine(fieldDetector: FakeDetector(fields: []))
        let cands = engine.candidates(for: preset, page: makePage(), ocr: [])
        XCTAssertEqual(cands.count, 1)
        XCTAssertEqual(cands[0].box, rule.region!.normRect.padded(by: 0.05))
        XCTAssertEqual(cands[0].source, .fixedRegion)
        XCTAssertTrue(cands[0].isOn)   // defaultOn 反映
    }

    // MARK: - MaskingPipeline.analyze(forcedType:)（確認UI「種別を変更」の契約）

    /// ダミー基準画像を返すスタブ（画像ファイル不要でパイプラインを通す）。
    private struct FakeRectifier: DocumentRectifier {
        let page: PageImage
        func rectify(imageAt url: URL) throws -> PageImage { page }
    }
    private struct FakeRecognizer: TextRecognizer {
        let ocr: [OCRItem]
        func recognize(_ page: PageImage) throws -> [OCRItem] { ocr }
    }

    /// 再OCR回数を数えるフェイク（アスペクト正規化の2パス確認用）。
    private final class CountingRecognizer: TextRecognizer {
        let ocr: [OCRItem]
        private(set) var callCount = 0
        init(ocr: [OCRItem]) { self.ocr = ocr }
        func recognize(_ page: PageImage) throws -> [OCRItem] { callCount += 1; return ocr }
    }

    func test_analyze_normalizesID1CardAspectAndReruns0CR() throws {
        // 長短比2.0の歪んだ基準画像＋ID-1種別（免許表を強制）→ 1.586へ正規化・再OCR。
        let menkyoRule = MaskRule(id: "menkyo.fixed", label: "欄", kind: .fixed, detector: nil,
                                  region: MaskRule.Region(x: 0.1, yTop: 0.1, w: 0.2, h: 0.1),
                                  defaultOn: true, basis: "根拠", padding: nil)
        let presets = [
            preset(.menkyoshoFront, keywords: [("運転免許証", 6)], rules: [menkyoRule]),
            preset(.generic, keywords: []),
        ]
        let recognizer = CountingRecognizer(ocr: [])
        let pipeline = try MaskingPipeline(
            presets: presets,
            rectifier: FakeRectifier(page: makePage(width: 200, height: 100)),
            recognizer: recognizer,
            classifier: KeywordClassifier(),
            fieldDetector: FakeDetector(fields: []))
        let url = URL(fileURLWithPath: "/dev/null")

        let forced = try pipeline.analyze(url: url, forcedType: .menkyoshoFront)
        let aspect = forced.page.pixelSize.width / forced.page.pixelSize.height
        XCTAssertEqual(Double(aspect), 85.60 / 53.98, accuracy: 0.02, "ID-1比へ正規化されること")
        XCTAssertEqual(recognizer.callCount, 2, "正規化後に再OCRが走ること（初回＋再）")

        // generic（ID-1でない）は正規化しない。
        let genericResult = try pipeline.analyze(url: url)
        let gAspect = genericResult.page.pixelSize.width / genericResult.page.pixelSize.height
        XCTAssertEqual(Double(gAspect), 2.0, accuracy: 0.01, "generic はアスペクトを変えないこと")
    }

    func test_analyze_manualRotation_rotatesBaseAndRerunsOCR() throws {
        let presets = [preset(.generic, keywords: [])]
        let recognizer = CountingRecognizer(ocr: [])
        let pipeline = try MaskingPipeline(
            presets: presets,
            rectifier: FakeRectifier(page: makePage(width: 200, height: 100)),
            recognizer: recognizer,
            classifier: KeywordClassifier(),
            fieldDetector: FakeDetector(fields: []))
        let url = URL(fileURLWithPath: "/dev/null")

        let rotated = try pipeline.analyze(url: url, manualRotation: 1)
        XCTAssertEqual(Int(rotated.page.pixelSize.width), 100, "90°回転で幅と高さが入れ替わる")
        XCTAssertEqual(Int(rotated.page.pixelSize.height), 200)
        XCTAssertEqual(recognizer.callCount, 2, "回転後に再OCRされる（初回＋再）")

        let upsideDown = try pipeline.analyze(url: url, manualRotation: 2)
        XCTAssertEqual(Int(upsideDown.page.pixelSize.width), 200, "180°は寸法不変")
        XCTAssertEqual(Int(upsideDown.page.pixelSize.height), 100)
    }

    func test_analyze_forcedTypeOverridesPresetButKeepsClassification() throws {
        let menkyoRule = MaskRule(id: "menkyo.fixed", label: "免許固定欄", kind: .fixed, detector: nil,
                                  region: MaskRule.Region(x: 0.1, yTop: 0.1, w: 0.2, h: 0.1),
                                  defaultOn: true, basis: "根拠", padding: nil)
        let mynaRule = MaskRule(id: "myna.fixed", label: "マイナ固定欄", kind: .fixed, detector: nil,
                                region: MaskRule.Region(x: 0.5, yTop: 0.5, w: 0.2, h: 0.1),
                                defaultOn: true, basis: "根拠", padding: nil)
        let presets = [
            preset(.menkyoshoFront, keywords: [("運転免許証", 6)], rules: [menkyoRule]),
            preset(.myNumberCardFront, keywords: [("個人番号カード", 6)], rules: [mynaRule]),
            preset(.generic, keywords: []),
        ]
        let ocr = [item("運転免許証", x: 0.1, midY: 0.9, w: 0.3)]
        let pipeline = try MaskingPipeline(
            presets: presets,
            rectifier: FakeRectifier(page: makePage()),
            recognizer: FakeRecognizer(ocr: ocr),
            classifier: KeywordClassifier(),
            fieldDetector: FakeDetector(fields: []))
        let url = URL(fileURLWithPath: "/dev/null")

        // 通常: 自動判定どおり免許プリセットが適用される
        let auto = try pipeline.analyze(url: url)
        XCTAssertEqual(auto.classification.type, .menkyoshoFront)
        XCTAssertEqual(auto.preset.documentType, .menkyoshoFront)
        XCTAssertEqual(auto.candidates.map(\.ruleID), ["menkyo.fixed"])

        // 強制: プリセット・候補は指定種別に切り替わり、classification は自動判定のまま（ranking表示用）
        let forced = try pipeline.analyze(url: url, forcedType: .myNumberCardFront)
        XCTAssertEqual(forced.classification.type, .menkyoshoFront, "自動判定結果は保持される")
        XCTAssertEqual(forced.preset.documentType, .myNumberCardFront)
        XCTAssertEqual(forced.candidates.map(\.ruleID), ["myna.fixed"])
    }

    func test_ruleEngine_fallbackRegionUsedOnlyWhenDetectorFindsNothing() {
        let fallback = MaskRule.Region(x: 0.32, yTop: 0.08, w: 0.62, h: 0.28)
        let rule = MaskRule(id: "t.dyn", label: "個人番号", kind: .dynamic, detector: .myNumber12,
                            region: nil, defaultOn: true, basis: "根拠", padding: nil,
                            fallbackRegion: fallback)
        let preset = DocumentPreset(schemaVersion: 1, documentType: .myNumberCardBack, displayName: "裏",
                                    enhance: false, classification: ClassificationSpec(keywords: []),
                                    warnings: [], rules: [rule])

        // 検出ゼロ → フォールバック候補（位置推定ラベル・固定領域ソース）
        let empty = RuleEngine(fieldDetector: FakeDetector(fields: []))
            .candidates(for: preset, page: makePage(), ocr: [])
        XCTAssertEqual(empty.count, 1)
        XCTAssertEqual(empty[0].label, "個人番号（位置推定）")
        XCTAssertEqual(empty[0].source, .fixedRegion)
        XCTAssertEqual(empty[0].box, fallback.normRect.padded(by: rule.effectivePadding))

        // 検出成功 → フォールバックは出ない（重複しない）
        let field = DetectedField(detector: .myNumber12,
                                  box: NormRect(x: 0.4, y: 0.7, w: 0.4, h: 0.08),
                                  confidence: 1.0, maskedDescription: nil)
        let found = RuleEngine(fieldDetector: FakeDetector(fields: [field]))
            .candidates(for: preset, page: makePage(), ocr: [])
        XCTAssertEqual(found.count, 1)
        XCTAssertEqual(found[0].source, .detector(.myNumber12))
    }

    func test_ruleEngine_dynamicLabelIncludesMaskedDescription() {
        let field = DetectedField(detector: .myNumber12,
                                  box: NormRect(x: 0.1, y: 0.1, w: 0.2, h: 0.05),
                                  confidence: nil, maskedDescription: "31********37")
        let rule = MaskRule(id: "t.dyn", label: "マイナンバー", kind: .dynamic, detector: .myNumber12,
                            region: nil, defaultOn: false, basis: "根拠", padding: 0.02)
        let preset = DocumentPreset(schemaVersion: 1, documentType: .generic, displayName: "g",
                                    enhance: false, classification: ClassificationSpec(keywords: []),
                                    warnings: [], rules: [rule])
        let engine = RuleEngine(fieldDetector: FakeDetector(fields: [field]))
        let cands = engine.candidates(for: preset, page: makePage(), ocr: [])
        XCTAssertEqual(cands.count, 1)
        XCTAssertEqual(cands[0].label, "マイナンバー（31********37）", "maskedDescription がラベルに入る")
        XCTAssertEqual(cands[0].box, field.box.padded(by: 0.02))
        XCTAssertEqual(cands[0].source, .detector(.myNumber12))
        XCTAssertFalse(cands[0].isOn)  // defaultOn=false 反映
    }
}
