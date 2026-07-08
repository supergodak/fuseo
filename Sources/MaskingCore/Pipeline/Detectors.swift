import Foundation
import CoreGraphics
import Vision

/// Vision＋ルール検証による標準 FieldDetecting 実装。
///
/// 番号系の規約（WP-0知見・core-design.md §2.3）:
/// - **窓スライド禁止**。候補は「必要桁数ちょうど」の2形のみ:
///   A) 1観測内の極大数字連続（空白のみ除去後）
///   B) 同一行（中心Y±2%）の数字のみ観測の連結（x昇順＋逆順の保険。逆順はチェックデジットが誤採用を防ぐ）
/// - チェックデジット（マイナンバー=総務省令式 / 免許証番号=モジュラス11ウェイト2-7 / カード=Luhn）で確定。
public struct VisionFieldDetector: FieldDetecting {
    /// パラメータ設定（桁数・行許容など。既定値は現挙動と一致）。
    let tuning: PipelineTuning

    public init(tuning: PipelineTuning = PipelineTuning()) {
        self.tuning = tuning
    }

    public func detect(_ id: DetectorID, page: PageImage, ocr: [OCRItem]) throws -> [DetectedField] {
        switch id {
        case .myNumber12:
            // 「ちょうど12桁」規約（core-design.md §2.3）。桁数は規約なので設定化しない。
            return Self.numberFields(ocr: ocr, lengths: [12],
                                     validate: Checkdigits.isValidMyNumber, detector: .myNumber12,
                                     tuning: tuning)
        case .licenseNumber12:
            return Self.numberFields(ocr: ocr, lengths: [12],
                                     validate: Checkdigits.isValidLicenseNumber, detector: .licenseNumber12,
                                     tuning: tuning)
        case .creditCardLuhn:
            // 14(Diners)/15(Amex)/16(主要ブランド)のみ。13桁は製造番号等の写り込みと衝突しやすく
            // 国内実流通もほぼ無いため対象外（誤検出対策）。桁は tuning.creditCardLengths。
            return Self.numberFields(ocr: ocr, lengths: tuning.creditCardLengths,
                                     validate: Checkdigits.isValidLuhn, detector: .creditCardLuhn,
                                     tuning: tuning)
        case .face:
            return try Self.rectangleFields(in: page.cgImage,
                                            request: VNDetectFaceRectanglesRequest(), detector: .face)
        case .qrBarcode:
            return try Self.rectangleFields(in: page.cgImage,
                                            request: VNDetectBarcodesRequest(), detector: .qrBarcode)
        case .insurerNumber:
            // 保険者番号: 8桁(健保組合等)/6桁(市町村国保)。チェックデジットが無いため近傍キーワード必須。
            // ※保険証実物のPoC未測定のため暫定実装（core-design.md §2.3。測定後に閾値確定）
            return Self.keywordProximityDigits(ocr: ocr, lengths: tuning.insurerNumberLengths,
                                               keywords: ["保険者番号"], detector: .insurerNumber,
                                               tuning: tuning)
        case .kigoBango:
            // 記号・番号: 桁数が保険者により様々。キーワードと同一行の数字列を対象にする（暫定）。
            return Self.keywordProximityDigits(ocr: ocr, lengths: tuning.kigoBangoLengths,
                                               keywords: ["記号", "番号"],
                                               excludeKeywords: ["保険者番号", "個人番号"],
                                               detector: .kigoBango,
                                               tuning: tuning)
        case .zairyuNumber:
            return Self.zairyuNumberFields(ocr: ocr)
        }
    }

    // MARK: - 在留カード番号（英2字＋数字8桁＋英2字）

    /// 在留カード番号: 「AB12345678CD」形式の**厳格一致**。チェックデジットの公開規定が無いため、
    /// 英数字の極大トークンが「ちょうど12文字・英2＋数8＋英2」の場合のみ候補化する（誤検出対策）。
    /// SAMPLEの実測では番号は単一観測で読める（政府見本・2026-07-08）。
    static func zairyuNumberFields(ocr: [OCRItem]) -> [DetectedField] {
        var out: [DetectedField] = []
        for item in ocr {
            let text = item.text.replacingOccurrences(of: " ", with: "").uppercased()
            var token = ""
            func flush() {
                defer { token = "" }
                guard token.count == 12 else { return }
                let head = token.prefix(2), body = token.dropFirst(2).prefix(8), tail = token.suffix(2)
                guard head.allSatisfy({ $0.isLetter }), body.allSatisfy(\.isNumber),
                      tail.allSatisfy({ $0.isLetter }) else { return }
                out.append(DetectedField(
                    detector: .zairyuNumber, box: item.box, confidence: item.confidence,
                    maskedDescription: "\(head)********\(tail)"))
            }
            for ch in text {
                if ch.isASCII && (ch.isLetter || ch.isNumber) { token.append(ch) } else { flush() }
            }
            flush()
        }
        return out
    }

    // MARK: - 番号系

    struct DigitSequence {
        let digits: String
        let box: NormRect
        let isReversedVariant: Bool
    }

    static func numberFields(ocr: [OCRItem], lengths: [Int],
                             validate: (String) -> Bool, detector: DetectorID,
                             tuning: PipelineTuning = PipelineTuning()) -> [DetectedField] {
        var fields: [DetectedField] = []
        var seen = Set<String>()
        // 正順候補を先に評価（逆順の保険が同一番号で競合しないよう seen で排他）
        for seq in digitSequences(ocr: ocr, tuning: tuning).sorted(by: { !$0.isReversedVariant && $1.isReversedVariant }) {
            guard lengths.contains(seq.digits.count), !seen.contains(seq.digits), validate(seq.digits) else { continue }
            seen.insert(seq.digits)
            fields.append(DetectedField(detector: detector, box: seq.box,
                                        confidence: nil, maskedDescription: masked(seq.digits)))
        }
        return fields
    }

    /// 候補となる数字列の抽出（A: 観測内の極大連続 / B: 行内の数字のみ観測の連結）。
    static func digitSequences(ocr: [OCRItem],
                               tuning: PipelineTuning = PipelineTuning()) -> [DigitSequence] {
        var result: [DigitSequence] = []

        // A) 観測内: 空白のみ除去 → 極大数字連続（ハイフン等は区切りとして残す＝電話番号を分断する）
        for item in ocr {
            let stripped = item.text.replacingOccurrences(of: " ", with: "")
            var run = ""
            for ch in stripped + "\u{0}" {   // 終端番兵
                if ch.isASCIINumber {
                    run.append(ch)
                } else {
                    if run.count >= tuning.minDigitRunLength {   // 最小桁未満は対象外（どの検出子も使わない）
                        result.append(DigitSequence(digits: run, box: item.box, isReversedVariant: false))
                    }
                    run = ""
                }
            }
        }

        // B) 行内の数字のみ観測（「3120」「7397」「8337」のような分割）を x昇順で連結＋逆順の保険
        for line in groupIntoLines(ocr, tuning: tuning) {
            let digitObs = line
                .filter { !$0.text.isEmpty && $0.text.replacingOccurrences(of: " ", with: "").allSatisfy(\.isASCIINumber) }
                .sorted { $0.box.x < $1.box.x }
            guard digitObs.count >= 2 else { continue }
            let parts = digitObs.map { $0.text.replacingOccurrences(of: " ", with: "") }
            let box = digitObs.map(\.box).reduce(digitObs[0].box) { $0.union($1) }
            result.append(DigitSequence(digits: parts.joined(), box: box, isReversedVariant: false))
            result.append(DigitSequence(digits: parts.reversed().joined(), box: box, isReversedVariant: true))
        }
        return result
    }

    /// 行グルーピング: box中心Yが許容範囲（既定±2%・WP-0実証値）以内、**または縦区間が実質的に
    /// 重なる**場合に同一行とみなす。後者は台形補正の傾き残りで行が斜めになった画像への対策
    /// （2026-07-07 dogfood: 斜め写真のマイナ裏で数字3群の中心Yが2%超ずれ、12桁連結に失敗した）。
    /// 比較対象は「行に最後に追加した観測」= 斜めの行を連鎖的に追える。
    static func groupIntoLines(_ items: [OCRItem],
                               tuning: PipelineTuning = PipelineTuning()) -> [[OCRItem]] {
        var lines: [[OCRItem]] = []
        for item in items.sorted(by: { ($0.box.y + $0.box.h / 2) > ($1.box.y + $1.box.h / 2) }) {
            if let last = lines.last?.last, isSameLine(last, item, tuning: tuning) {
                lines[lines.count - 1].append(item)
            } else {
                lines.append([item])
            }
        }
        return lines
    }

    /// 同一行判定: 中心Y差が許容内、または縦区間の重なりが「低い方の文字高の35%以上」。
    /// 35% = 行間が詰まった隣接行（重なってもわずか）を弾きつつ、±3〜4°程度の傾きは許す値。
    private static func isSameLine(_ a: OCRItem, _ b: OCRItem, tuning: PipelineTuning) -> Bool {
        let ca = a.box.y + a.box.h / 2
        let cb = b.box.y + b.box.h / 2
        if abs(ca - cb) < tuning.lineGroupingCenterYTolerance { return true }
        let overlap = min(a.box.y + a.box.h, b.box.y + b.box.h) - max(a.box.y, b.box.y)
        return overlap >= 0.35 * min(a.box.h, b.box.h)
    }

    // MARK: - キーワード近傍の数字列（保険証系・チェックデジット無し）

    static func keywordProximityDigits(ocr: [OCRItem], lengths: [Int], keywords: [String],
                                       excludeKeywords: [String] = [], detector: DetectorID,
                                       tuning: PipelineTuning = PipelineTuning()) -> [DetectedField] {
        var fields: [DetectedField] = []
        for line in groupIntoLines(ocr, tuning: tuning) {
            let lineText = line.map(\.text).joined()
            guard keywords.contains(where: { lineText.contains($0) }),
                  !excludeKeywords.contains(where: { lineText.contains($0) }) else { continue }
            // 行内の数字連続（観測単位）をマスク対象にする
            for item in line {
                let stripped = item.text.replacingOccurrences(of: " ", with: "")
                var run = ""
                for ch in stripped + "\u{0}" {
                    if ch.isASCIINumber {
                        run.append(ch)
                    } else {
                        if lengths.contains(run.count) {
                            fields.append(DetectedField(detector: detector, box: item.box,
                                                        confidence: nil, maskedDescription: masked(run)))
                        }
                        run = ""
                    }
                }
            }
        }
        return fields
    }

    // MARK: - 矩形系（顔・バーコード/QR）

    static func rectangleFields(in image: CGImage, request: VNImageBasedRequest,
                                detector: DetectorID) throws -> [DetectedField] {
        let handler = VNImageRequestHandler(cgImage: image, options: [:])
        try handler.perform([request])
        let observations = (request.results as? [VNDetectedObjectObservation]) ?? []
        return observations.map {
            DetectedField(detector: detector,
                          box: CoordinateSpace.fromVision($0.boundingBox),
                          confidence: $0.confidence, maskedDescription: nil)
        }
    }

    /// 部分マスク表示（生の番号をログ・UIへ出さない）。
    static func masked(_ digits: String) -> String {
        guard digits.count > 4 else { return "****" }
        return digits.prefix(2) + String(repeating: "*", count: digits.count - 4) + digits.suffix(2)
    }
}
