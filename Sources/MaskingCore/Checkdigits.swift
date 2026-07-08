import Foundation

/// 公開仕様のチェックデジット検証。OCRで拾った数字列の誤検出（電話番号・日付等）を排除する要。
public enum Checkdigits {

    /// マイナンバー（12桁）。総務省令の検査用数字:
    /// 末尾1桁が検査用数字。本体11桁を「右から」P1..P11 とし、
    /// Qn = n+1 (1≤n≤6) / n−5 (7≤n≤11)。rem = Σ(Pn×Qn) mod 11。
    /// 検査用数字 = rem ≤ 1 ? 0 : 11 − rem。
    public static func isValidMyNumber(_ digits: String) -> Bool {
        guard digits.count == 12, digits.allSatisfy(\.isASCIINumber) else { return false }
        let ds = digits.compactMap { $0.wholeNumberValue }
        let check = ds[11]
        let body = Array(ds[0..<11])            // 左→右
        var sum = 0
        for n in 1...11 {
            let p = body[11 - n]                // 右から n 番目
            let q = (n <= 6) ? n + 1 : n - 5
            sum += p * q
        }
        let rem = sum % 11
        let expected = rem <= 1 ? 0 : 11 - rem
        return check == expected
    }

    /// 運転免許証番号（12桁）。11桁目がチェックデジット（モジュラス11・ウェイト2〜7巡回）:
    /// 先頭10桁を「右から」重み 2,3,4,5,6,7,2,3,… で乗じ、rem = Σ mod 11。
    /// チェックデジット = (11 − rem) mod 11（結果が10になる番号は発行されない前提）。
    /// 12桁目は再交付回数のため検証対象外。
    public static func isValidLicenseNumber(_ digits: String) -> Bool {
        guard digits.count == 12, digits.allSatisfy(\.isASCIINumber) else { return false }
        let ds = digits.compactMap { $0.wholeNumberValue }
        let check = ds[10]
        let body = Array(ds[0..<10])
        var sum = 0
        var weight = 2
        for p in body.reversed() {
            sum += p * weight
            weight = weight == 7 ? 2 : weight + 1
        }
        let expected = (11 - (sum % 11)) % 11
        guard expected <= 9 else { return false }   // 10 は1桁で表せない＝照合不能
        return check == expected
    }

    /// クレジットカード等の Luhn（13〜19桁で使用）。
    public static func isValidLuhn(_ digits: String) -> Bool {
        guard digits.count >= 13, digits.count <= 19, digits.allSatisfy(\.isASCIINumber) else { return false }
        var sum = 0
        for (i, ch) in digits.reversed().enumerated() {
            var d = ch.wholeNumberValue ?? 0
            if i % 2 == 1 {
                d *= 2
                if d > 9 { d -= 9 }
            }
            sum += d
        }
        return sum % 10 == 0
    }
}

extension Character {
    var isASCIINumber: Bool { isASCII && isNumber }
}
