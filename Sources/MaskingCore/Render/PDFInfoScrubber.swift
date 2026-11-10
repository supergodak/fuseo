import Foundation

/// 書き出した PDF の文書情報（trailer の `/Info`）を `<< /Producer (Fuseo) >>` だけにする（Mac 0.2.4）。
///
/// CoreGraphics の PDF コンテキストは `/Producer (macOS Version … (Build …) Quartz PDFContext)` と
/// `/CreationDate`・`/ModDate` を必ず書き込み、補助辞書では消せない。利用者の端末の情報と作業日時が
/// 提出物に残るため、閉じた後のファイルで Info 辞書の中身を**同じバイト長**の空白詰めで上書きする。
/// 長さが変わらないので xref のオフセットはそのまま有効（オブジェクト内の空白は PDF の構文上無害）。
///
/// 対象は CoreGraphics が書く古典的な形式（非圧縮の xref・オブジェクトストリーム無し）。
/// 想定外の形なら何もせず false を返す（呼び出し側がログに残す。テストが形の変化を検知する）。
enum PDFInfoScrubber {

    static let replacement = Array("<< /Producer (Fuseo) >>".utf8)

    /// ファイルを書き換える。書き換えたら true。
    @discardableResult
    static func scrub(fileAt url: URL) throws -> Bool {
        var bytes = [UInt8](try Data(contentsOf: url))
        guard scrub(&bytes) else { return false }
        try Data(bytes).write(to: url, options: .atomic)
        return true
    }

    /// バイト列の Info 辞書を置き換える。置き換えたら true。
    static func scrub(_ bytes: inout [UInt8]) -> Bool {
        guard let (num, gen) = infoReference(in: bytes),
              let dictStart = objectBody(num: num, gen: gen, in: bytes),
              let dictEnd = dictionaryEnd(from: dictStart, in: bytes) else { return false }
        let length = dictEnd - dictStart
        guard length >= replacement.count else { return false }
        let padded = replacement + [UInt8](repeating: 0x20, count: length - replacement.count)
        bytes.replaceSubrange(dictStart..<dictEnd, with: padded)
        return true
    }

    // MARK: - 解析

    /// 最後の `trailer` 以降にある `/Info N G R` の N と G。
    static func infoReference(in bytes: [UInt8]) -> (Int, Int)? {
        guard let trailer = lastIndex(of: Array("trailer".utf8), in: bytes),
              let info = firstIndex(of: Array("/Info".utf8), in: bytes, from: trailer) else { return nil }
        var i = info + 5
        guard let num = readInt(bytes, &i), let gen = readInt(bytes, &i) else { return nil }
        skipWhitespace(bytes, &i)
        guard i < bytes.count, bytes[i] == UInt8(ascii: "R") else { return nil }
        return (num, gen)
    }

    /// 行頭の `N G obj` の直後にある `<<` の位置（最後の定義を採る）。
    static func objectBody(num: Int, gen: Int, in bytes: [UInt8]) -> Int? {
        let header = Array("\(num) \(gen) obj".utf8)
        var searchEnd = bytes.count
        while let at = lastIndex(of: header, in: bytes, before: searchEnd) {
            searchEnd = at
            guard at == 0 || bytes[at - 1] == 0x0A || bytes[at - 1] == 0x0D else { continue }
            var i = at + header.count
            skipWhitespace(bytes, &i)
            guard i + 1 < bytes.count, bytes[i] == UInt8(ascii: "<"), bytes[i + 1] == UInt8(ascii: "<") else { return nil }
            return i
        }
        return nil
    }

    /// `<<` から対応する `>>` の直後まで（入れ子の辞書・リテラル文字列のエスケープ・16 進文字列を考慮）。
    static func dictionaryEnd(from start: Int, in bytes: [UInt8]) -> Int? {
        var depth = 0
        var i = start
        let lt = UInt8(ascii: "<"), gt = UInt8(ascii: ">"), lp = UInt8(ascii: "("), rp = UInt8(ascii: ")")
        while i < bytes.count {
            let c = bytes[i]
            if c == lp {                                   // リテラル文字列（括弧の入れ子とエスケープ）
                var paren = 1
                i += 1
                while i < bytes.count, paren > 0 {
                    switch bytes[i] {
                    case UInt8(ascii: "\\"): i += 1
                    case lp: paren += 1
                    case rp: paren -= 1
                    default: break
                    }
                    i += 1
                }
                continue
            }
            if c == lt, i + 1 < bytes.count, bytes[i + 1] == lt { depth += 1; i += 2; continue }
            if c == gt, i + 1 < bytes.count, bytes[i + 1] == gt {
                depth -= 1; i += 2
                if depth == 0 { return i }
                continue
            }
            if c == lt {                                   // 16 進文字列
                guard let close = bytes[(i + 1)...].firstIndex(of: gt) else { return nil }
                i = close + 1
                continue
            }
            i += 1
        }
        return nil
    }

    // MARK: - 小道具

    private static func isWhitespace(_ c: UInt8) -> Bool { [0x00, 0x09, 0x0A, 0x0C, 0x0D, 0x20].contains(c) }

    private static func skipWhitespace(_ b: [UInt8], _ i: inout Int) {
        while i < b.count, isWhitespace(b[i]) { i += 1 }
    }

    private static func readInt(_ b: [UInt8], _ i: inout Int) -> Int? {
        skipWhitespace(b, &i)
        var value = 0, digits = 0
        while i < b.count, b[i] >= 0x30, b[i] <= 0x39, digits < 10 {
            value = value * 10 + Int(b[i] - 0x30); i += 1; digits += 1
        }
        return digits > 0 ? value : nil
    }

    private static func firstIndex(of pattern: [UInt8], in b: [UInt8], from start: Int) -> Int? {
        guard !pattern.isEmpty, b.count >= pattern.count else { return nil }
        var i = start
        while i <= b.count - pattern.count {
            if b[i] == pattern[0], Array(b[i..<(i + pattern.count)]) == pattern { return i }
            i += 1
        }
        return nil
    }

    private static func lastIndex(of pattern: [UInt8], in b: [UInt8], before end: Int? = nil) -> Int? {
        guard !pattern.isEmpty, b.count >= pattern.count else { return nil }
        var i = min((end ?? b.count) - 1, b.count - pattern.count)
        while i >= 0 {
            if b[i] == pattern[0], Array(b[i..<(i + pattern.count)]) == pattern { return i }
            i -= 1
        }
        return nil
    }
}
