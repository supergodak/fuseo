import XCTest
import CoreGraphics
import CoreText
import PDFKit
@testable import MaskingCore

/// 文字入れ F1（docs/form-fill-design.md §3〜4・§6）。
/// ①配置（揃え・行間・マス目・はみ出し・枠に合わせる）②焼き込み（文字が出る・マスクは文字の上で全黒）
/// ③保存形式（文字が無ければ v1 のまま・あれば v2・往復）④書き出し PDF のテキスト層に入力文字が入らない。
final class TextBoxTests: XCTestCase {

    /// A4 を 300dpi で描いた大きさ（PDF 取り込みと同じ）。
    private let a4 = CGSize(width: 2480, height: 3508)

    private func white(_ size: CGSize) -> CGImage {
        let ctx = CGContext(data: nil, width: Int(size.width), height: Int(size.height), bitsPerComponent: 8,
                            bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        ctx.fill(CGRect(origin: .zero, size: size))
        return ctx.makeImage()!
    }

    /// RGBA（左下原点の px 座標で読む）。
    private func pixels(_ image: CGImage) -> (data: [UInt8], w: Int, h: Int) {
        let w = image.width, h = image.height
        var data = [UInt8](repeating: 0, count: w * h * 4)
        let ctx = CGContext(data: &data, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        return (data, w, h)
    }

    /// 左下原点の px 矩形内で「暗い」画素の数。
    private func darkCount(_ px: (data: [UInt8], w: Int, h: Int), in r: CGRect, threshold: UInt8 = 128) -> Int {
        var n = 0
        let x0 = max(0, Int(r.minX)), x1 = min(px.w, Int(r.maxX.rounded(.up)))
        let y0 = max(0, Int(r.minY)), y1 = min(px.h, Int(r.maxY.rounded(.up)))
        guard x0 < x1, y0 < y1 else { return 0 }
        for y in y0..<y1 {
            let row = px.h - 1 - y        // メモリは上の行から
            for x in x0..<x1 {
                let i = (row * px.w + x) * 4
                if px.data[i] < threshold && px.data[i + 1] < threshold && px.data[i + 2] < threshold { n += 1 }
            }
        }
        return n
    }

    private func box(_ text: String, _ r: NormRect, _ edit: (inout TextBox) -> Void = { _ in }) -> TextBox {
        var b = TextBox(box: r, text: text)
        edit(&b)
        return b
    }

    // MARK: - ① 配置

    func test_fontNames_resolveToHiragino() {
        for f in TextBox.Font.allCases {
            let font = TextBoxRenderer.ctFont(f, sizePx: 40)
            XCTAssertEqual(CTFontCopyPostScriptName(font) as String, TextBoxRenderer.postScriptName(f),
                           "\(f) の書体が見つからず代替になっている")
        }
    }

    func test_layout_leftTop_firstBaselineAtTopMinusAscent() {
        let b = box("見本", NormRect(x: 0.1, y: 0.5, w: 0.4, h: 0.05)) { $0.hAlign = .left; $0.vAlign = .top }
        let frame = CoordinateSpace.pixelRect(b.box, in: a4)
        let l = TextBoxRenderer.layout(b, imageSize: a4)
        let ascent = CTFontGetAscent(TextBoxRenderer.ctFont(.gothic, sizePx: CGFloat(b.fontSizePx)))
        XCTAssertEqual(l.lines[0].origin.x, frame.minX, accuracy: 0.01)
        XCTAssertEqual(l.lines[0].origin.y, frame.maxY - ascent, accuracy: 0.01)
    }

    func test_layout_lineSpacing_setsDistanceBetweenBaselines() {
        for ls in [1.0, 1.5, 2.25] {
            let b = box("一行目\n二行目\n三行目", NormRect(x: 0.1, y: 0.3, w: 0.5, h: 0.2)) {
                $0.fontSizePt = 12; $0.lineSpacing = ls
            }
            let l = TextBoxRenderer.layout(b, imageSize: a4)
            XCTAssertEqual(l.lines.count, 3)
            let expected = CGFloat(12 * 300.0 / 72 * ls)
            XCTAssertEqual(l.lines[0].origin.y - l.lines[1].origin.y, expected, accuracy: 0.01)
            XCTAssertEqual(l.lines[1].origin.y - l.lines[2].origin.y, expected, accuracy: 0.01)
        }
    }

    func test_layout_hAlign_centerAndRight() {
        let r = NormRect(x: 0.2, y: 0.4, w: 0.5, h: 0.05)
        let frame = CoordinateSpace.pixelRect(r, in: a4)
        let c = TextBoxRenderer.layout(box("中央", r) { $0.hAlign = .center }, imageSize: a4).lines[0]
        XCTAssertEqual(c.origin.x + c.width / 2, frame.midX, accuracy: 0.01)
        let rt = TextBoxRenderer.layout(box("右", r) { $0.hAlign = .right }, imageSize: a4).lines[0]
        XCTAssertEqual(rt.origin.x + rt.width, frame.maxX, accuracy: 0.01)
    }

    func test_layout_vAlignCenter_centersTheBlock() {
        let r = NormRect(x: 0.1, y: 0.4, w: 0.6, h: 0.1)
        let frame = CoordinateSpace.pixelRect(r, in: a4)
        let l = TextBoxRenderer.layout(box("上\n下", r) { $0.vAlign = .center; $0.lineSpacing = 1.6 }, imageSize: a4)
        XCTAssertEqual(l.inkBounds.midY, frame.midY, accuracy: 0.5)
    }

    func test_layout_cells_eachCharacterAtCellCenter_andOverflowWhenTooMany() {
        let r = NormRect(x: 0.1, y: 0.6, w: 0.6, h: 0.02)
        let frame = CoordinateSpace.pixelRect(r, in: a4)
        let b = box("123456789012", r) { $0.cells = 12; $0.fontSizePt = 10 }
        let l = TextBoxRenderer.layout(b, imageSize: a4)
        XCTAssertEqual(l.lines.count, 12)
        let cellW = frame.width / 12
        for (i, line) in l.lines.enumerated() {
            XCTAssertEqual(line.origin.x + line.width / 2, frame.minX + cellW * (CGFloat(i) + 0.5), accuracy: 0.01)
        }
        XCTAssertFalse(l.overflows)
        XCTAssertTrue(TextBoxRenderer.layout(box("1234567890123", r) { $0.cells = 12; $0.fontSizePt = 10 },
                                             imageSize: a4).overflows, "マス数を超えたらはみ出し")
        XCTAssertEqual(TextBoxRenderer.layout(box("1234567890123", r) { $0.cells = 12 }, imageSize: a4).lines.count, 12,
                       "マス数を超えた文字は描かない")
    }

    func test_layout_overflow_whenWiderOrTallerThanFrame() {
        let r = NormRect(x: 0.1, y: 0.5, w: 0.05, h: 0.01)
        XCTAssertTrue(TextBoxRenderer.layout(box("とても長い文字列です", r), imageSize: a4).overflows)
        let wide = NormRect(x: 0.1, y: 0.5, w: 0.8, h: 0.05)
        XCTAssertFalse(TextBoxRenderer.layout(box("短い", wide) { $0.fontSizePt = 10 }, imageSize: a4).overflows)
        XCTAssertTrue(TextBoxRenderer.layout(box("1\n2\n3\n4\n5", wide) { $0.fontSizePt = 10 }, imageSize: a4).overflows,
                      "行が多くて高さを超えたらはみ出し")
    }

    func test_fittingFontSize_fillsAbout90PercentOfHeight() {
        for (text, ls) in [("一行", 1.2), ("一行目\n二行目", 1.5)] {
            var b = box(text, NormRect(x: 0.1, y: 0.5, w: 0.8, h: 0.03)) { $0.lineSpacing = ls }
            b.fontSizePt = TextBoxRenderer.fittingFontSizePt(b, imageSize: a4)
            let frameH = CoordinateSpace.pixelRect(b.box, in: a4).height
            let l = TextBoxRenderer.layout(b, imageSize: a4)
            XCTAssertLessThanOrEqual(l.inkBounds.height, frameH)
            XCTAssertGreaterThan(l.inkBounds.height, frameH * 0.85)
            XCTAssertEqual(b.fontSizePt.truncatingRemainder(dividingBy: 0.5), 0, accuracy: 1e-9)
        }
    }

    func test_style_clamped_snapsToRangeAndStep() {
        var b = TextBox(box: NormRect(x: 0, y: 0, w: 0.1, h: 0.1))
        b.style = TextBox.Style(fontSizePt: 200, lineSpacing: 1.234, cells: 99)
        XCTAssertEqual(b.fontSizePt, 72)
        XCTAssertEqual(b.lineSpacing, 1.25, accuracy: 1e-9)
        XCTAssertEqual(b.cells, TextBox.maxCells)
        b.style = TextBox.Style(fontSizePt: 10.3, lineSpacing: 0.1)
        XCTAssertEqual(b.fontSizePt, 10.5)
        XCTAssertEqual(b.lineSpacing, 0.8, accuracy: 1e-9)
    }

    // MARK: - ② 焼き込み

    func test_burnIn_drawsTextInsideItsFrame() throws {
        let size = CGSize(width: 1000, height: 600)
        let r = NormRect(x: 0.1, y: 0.4, w: 0.8, h: 0.2)
        let b = box("見本 TARO", r) { $0.fontSizePt = 20 }
        let out = try RasterMaskRenderer().burnIn(page: PageImage(cgImage: white(size), sourceURL: nil, rectified: true,
                                                                  quadConfidence: 1),
                                                  texts: [b], masks: [], strokes: [])
        let px = pixels(out)
        let frame = CoordinateSpace.pixelRect(r, in: size)
        XCTAssertGreaterThan(darkCount(px, in: frame), 200, "枠の中に文字のインクがある")
        XCTAssertEqual(darkCount(px, in: CGRect(x: 0, y: 0, width: 1000, height: frame.minY - 2)), 0, "枠の外には出ない")
    }

    func test_burnIn_maskIsAboveText_allBlackInsideMask() throws {
        let size = CGSize(width: 800, height: 400)
        let r = NormRect(x: 0.1, y: 0.3, w: 0.8, h: 0.4)
        let b = box("123456789012", r) { $0.fontSizePt = 24; $0.color = .blue }
        let mask = NormRect(x: 0.3, y: 0.2, w: 0.3, h: 0.6)
        let out = try RasterMaskRenderer().burnIn(page: PageImage(cgImage: white(size), sourceURL: nil, rectified: true,
                                                                  quadConfidence: 1),
                                                  texts: [b], masks: [mask], strokes: [])
        let px = pixels(out)
        let m = CoordinateSpace.pixelRect(mask, in: size)
        let inner = m.insetBy(dx: 1, dy: 1)
        var nonBlack = 0
        for y in Int(inner.minY)..<Int(inner.maxY) {
            for x in Int(inner.minX)..<Int(inner.maxX) {
                let i = ((px.h - 1 - y) * px.w + x) * 4
                if px.data[i] != 0 || px.data[i + 1] != 0 || px.data[i + 2] != 0 || px.data[i + 3] != 255 { nonBlack += 1 }
            }
        }
        XCTAssertEqual(nonBlack, 0, "マスクの中に文字の画素が残っていない")
        // マスクの外には青い文字が残る
        var blue = 0
        for y in 0..<px.h { for x in 0..<Int(m.minX) - 2 {
            let i = ((px.h - 1 - y) * px.w + x) * 4
            if px.data[i + 2] > 100 && px.data[i] < 80 { blue += 1 }
        } }
        XCTAssertGreaterThan(blue, 20)
    }

    func test_burnIn_emptyTextDrawsNothing() throws {
        let size = CGSize(width: 200, height: 100)
        let out = try RasterMaskRenderer().burnIn(page: PageImage(cgImage: white(size), sourceURL: nil, rectified: true,
                                                                  quadConfidence: 1),
                                                  texts: [box("", NormRect(x: 0, y: 0, w: 1, h: 1))], masks: [], strokes: [])
        XCTAssertEqual(darkCount(pixels(out), in: CGRect(origin: .zero, size: size)), 0)
    }

    // MARK: - ③ 保存形式

    private func page(texts: [TextBox]) -> WorkPage {
        WorkPage(index: 0, sourceName: "form.pdf", documentType: .generic, pixelWidth: 8, pixelHeight: 4, texts: texts)
    }

    func test_schema_withoutTexts_staysV1_andHasNoTextsKey() throws {
        let doc = WorkDocument(title: "t", createdAt: Date(timeIntervalSince1970: 0),
                               updatedAt: Date(timeIntervalSince1970: 0), pages: [page(texts: [])])
        XCTAssertEqual(doc.schemaVersion, 1)
        let json = String(decoding: try WorkJSON.encoder().encode(doc), as: UTF8.self)
        XCTAssertFalse(json.contains("\"texts\""))
        XCTAssertTrue(json.contains("\"schemaVersion\":1"))
    }

    func test_schema_withTexts_isV2_andRoundTrips() throws {
        let t = box("見本\n二行目", NormRect(x: 0.1, y: 0.2, w: 0.3, h: 0.05)) {
            $0.fontSizePt = 11.5; $0.lineSpacing = 1.45; $0.font = .mincho; $0.color = .blue
            $0.hAlign = .right; $0.vAlign = .bottom; $0.cells = 0
        }
        let doc = WorkDocument(title: "t", createdAt: Date(timeIntervalSince1970: 0),
                               updatedAt: Date(timeIntervalSince1970: 0), pages: [page(texts: [t])])
        XCTAssertEqual(doc.schemaVersion, 2)
        let back = try WorkJSON.decoder().decode(WorkDocument.self, from: try WorkJSON.encoder().encode(doc))
        XCTAssertEqual(back, doc)
        XCTAssertEqual(back.pages[0].texts, [t])
    }

    func test_library_savesV2_onlyWhenTextsExist_andLoadsBoth_rejectsUnknown() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("fuseo-text-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let lib = FileWorkLibrary(rootURL: root)
        let img = white(CGSize(width: 8, height: 4))

        var doc = WorkDocument(title: "t", createdAt: Date(timeIntervalSince1970: 0),
                               updatedAt: Date(timeIntervalSince1970: 0), pages: [page(texts: [])])
        try lib.save(doc, pageImages: [0: img])
        let docURL = root.appendingPathComponent(doc.id.uuidString).appendingPathComponent("document.json")
        XCTAssertTrue(String(decoding: try Data(contentsOf: docURL), as: UTF8.self).contains("\"schemaVersion\":1"))

        // 文字を足して保存（呼び出し側が版を間違えても中身から決まる）
        doc.pages[0].texts = [box("A", NormRect(x: 0, y: 0, w: 0.5, h: 0.5))]
        doc.schemaVersion = 1
        try lib.save(doc, pageImages: [:])
        XCTAssertTrue(String(decoding: try Data(contentsOf: docURL), as: UTF8.self).contains("\"schemaVersion\":2"))
        XCTAssertEqual(try lib.load(id: doc.id).document.pages[0].texts.count, 1)

        // 文字を消して保存すると v1 に戻る（古い版でもまた開ける）
        doc.pages[0].texts = []
        try lib.save(doc, pageImages: [:])
        XCTAssertTrue(String(decoding: try Data(contentsOf: docURL), as: UTF8.self).contains("\"schemaVersion\":1"))

        // 未知の版は拒否
        let raw = String(decoding: try Data(contentsOf: docURL), as: UTF8.self)
            .replacingOccurrences(of: "\"schemaVersion\":1", with: "\"schemaVersion\":3")
        try Data(raw.utf8).write(to: docURL)
        XCTAssertThrowsError(try lib.load(id: doc.id))
    }

    // MARK: - ④ 書き出し

    func test_pdfExport_typedTextIsBurnedNotSearchable() throws {
        let size = CGSize(width: 1000, height: 600)
        let t = box("TYPEDSECRET", NormRect(x: 0.1, y: 0.4, w: 0.8, h: 0.2)) { $0.fontSizePt = 20 }
        let burned = try RasterMaskRenderer().burnIn(page: PageImage(cgImage: white(size), sourceURL: nil, rectified: true,
                                                                     quadConfidence: 1),
                                                     texts: [t], masks: [], strokes: [])
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("fuseo-text-\(UUID().uuidString).pdf")
        defer { try? FileManager.default.removeItem(at: url) }
        try FileExporter().export([RenderedPage(image: burned, ocrItems: [], maskRects: [])],
                                  options: ExportOptions(format: .pdf, searchableText: true), to: url)
        XCTAssertFalse((PDFDocument(url: url)?.string ?? "").contains("TYPEDSECRET"))
        XCTAssertFalse(String(decoding: try Data(contentsOf: url), as: UTF8.self).contains("Hiragino"),
                       "書体名などが PDF に残らない")
    }
}
