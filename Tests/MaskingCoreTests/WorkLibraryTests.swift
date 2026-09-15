import XCTest
import CoreGraphics
import ImageIO
@testable import MaskingCore

/// WP-13 層0: 作業の保存と再開（docs/wp13-library-design.md §5）。
/// ①ラウンドトリップ（全フィールド一致・候補 id 保持）②原子性（途中失敗で既存を壊さない・
/// 一時ディレクトリを残さない）③スキーマ v1 の JSON 固定（ゴールデン）④一覧 ⑤削除 を監視する。
/// フィクスチャは合成画像のみ（fixtures-private は使わない）。
final class WorkLibraryTests: XCTestCase {

    private var root: URL!

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory
            .appendingPathComponent("fuseo-worklib-\(UUID().uuidString)", isDirectory: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: root)
    }

    // MARK: - ヘルパ

    private func image(width: Int = 40, height: Int = 30, gray: CGFloat = 0.5) -> CGImage {
        let ctx = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)!
        ctx.setFillColor(CGColor(red: gray, green: gray, blue: gray, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: width, height: height))
        return ctx.makeImage()!
    }

    private func rect(_ x: Double, _ y: Double, _ w: Double, _ h: Double) -> NormRect {
        NormRect(x: x, y: y, w: w, h: h)
    }

    /// 日付は ISO8601（ミリ秒）で往復するので、ミリ秒で表現できる値を使う。
    private func date(_ t: Double) -> Date { Date(timeIntervalSince1970: t) }

    private func candidate(ruleID: String, source: MaskCandidate.Source,
                           isOn: Bool, confidence: Float?) -> MaskCandidate {
        MaskCandidate(ruleID: ruleID, label: "個人番号", box: rect(0.1, 0.2, 0.3, 0.05),
                      source: source, confidence: confidence, isOn: isOn,
                      basis: "行政手続番号法第19条", labelEn: "Individual Number", basisEn: "Act No.27")
    }

    /// 2 ページ・候補（isOn 混在）・手動矩形＋ブラシ・manualQuad・回転・OCR を持つ作業。
    private func sampleDocument(id: UUID = UUID()) -> WorkDocument {
        let page0 = WorkPage(
            index: 0,
            sourceName: "mynumber-back.jpeg",
            isFlatSource: false,
            analysisOptions: .default,
            documentType: .myNumberCardBack,
            forcedType: nil,
            manualQuad: Quad(topLeft: CGPoint(x: 0.02, y: 0.98), topRight: CGPoint(x: 0.97, y: 0.96),
                             bottomRight: CGPoint(x: 0.96, y: 0.03), bottomLeft: CGPoint(x: 0.03, y: 0.05)),
            manualRotation: 90,
            ocr: [OCRItem(text: "個人番号", box: rect(0.1, 0.8, 0.2, 0.05), confidence: 0.75, upright: true),
                  OCRItem(text: "1234", box: rect(0.4, 0.8, 0.2, 0.05), confidence: 0.5, upright: false)],
            candidates: [candidate(ruleID: "mnb.number", source: .detector(.myNumber12), isOn: true, confidence: 0.75),
                         candidate(ruleID: "mnb.qr", source: .fixedRegion, isOn: false, confidence: nil)],
            manual: {
                var m = ManualMask()
                m.rects = [rect(0.5, 0.5, 0.1, 0.1)]
                m.strokes = [BrushStroke(points: [CGPoint(x: 0.1, y: 0.1), CGPoint(x: 0.2, y: 0.25)], width: 0.05)]
                return m
            }(),
            pixelWidth: 40, pixelHeight: 30)

        let page1 = WorkPage(
            index: 1,
            sourceName: "scan.pdf",
            isFlatSource: true,
            analysisOptions: .flatPageWithoutText,
            documentType: .generic,
            forcedType: .juminhyoMyNumber,
            manualQuad: nil,
            manualRotation: 0,
            ocr: [],
            candidates: [],
            manual: ManualMask(),
            pixelWidth: 20, pixelHeight: 20)

        return WorkDocument(id: id, title: "マイナンバーカード", createdAt: date(1_757_900_000.5),
                            updatedAt: date(1_757_900_123.25), status: .inProgress,
                            lastExportedName: nil, pages: [page0, page1])
    }

    private func images(for doc: WorkDocument) -> [Int: CGImage] {
        var out: [Int: CGImage] = [:]
        for page in doc.pages {
            out[page.index] = image(width: page.pixelWidth, height: page.pixelHeight)
        }
        return out
    }

    // MARK: - ① ラウンドトリップ

    func test_saveThenLoad_restoresEveryField() throws {
        let lib = FileWorkLibrary(rootURL: root)
        let doc = sampleDocument()
        try lib.save(doc, pageImages: images(for: doc))

        let (loaded, dir) = try lib.load(id: doc.id)
        XCTAssertEqual(loaded, doc, "保存→読込で全フィールドが一致すること")

        // 候補の id が保持されている（アプリ層の選択状態が紐づくため）
        XCTAssertEqual(loaded.pages[0].candidates.map(\.id), doc.pages[0].candidates.map(\.id))
        XCTAssertEqual(loaded.pages[0].candidates.map(\.isOn), [true, false])

        // 画像とサムネイルが実ファイルとして存在する
        for page in loaded.pages {
            XCTAssertTrue(FileManager.default.fileExists(
                atPath: dir.appendingPathComponent(page.imageFile).path), page.imageFile)
            XCTAssertTrue(FileManager.default.fileExists(
                atPath: dir.appendingPathComponent(page.thumbnailFile).path), page.thumbnailFile)
        }
    }

    func test_save_withoutPageImages_keepsPreviouslyWrittenImages() throws {
        let lib = FileWorkLibrary(rootURL: root)
        var doc = sampleDocument()
        try lib.save(doc, pageImages: images(for: doc))

        // 候補のトグルだけ変えた再保存（画像は渡さない＝再書き込みしない）
        doc.pages[0].candidates[1].isOn = true
        doc.updatedAt = date(1_757_900_500)
        try lib.save(doc, pageImages: [:])

        let (loaded, dir) = try lib.load(id: doc.id)
        XCTAssertEqual(loaded, doc)
        XCTAssertTrue(FileManager.default.fileExists(
            atPath: dir.appendingPathComponent(loaded.pages[0].imageFile).path))
    }

    func test_thumbnail_isDownscaledToLongEdge256() throws {
        let lib = FileWorkLibrary(rootURL: root)
        let page = WorkPage(index: 0, sourceName: "big.png", documentType: .generic,
                            pixelWidth: 1000, pixelHeight: 500)
        let doc = WorkDocument(title: "big", createdAt: date(0), updatedAt: date(0), pages: [page])
        try lib.save(doc, pageImages: [0: image(width: 1000, height: 500)])

        let (loaded, dir) = try lib.load(id: doc.id)
        let url = dir.appendingPathComponent(loaded.pages[0].thumbnailFile)
        let src = CGImageSourceCreateWithURL(url as CFURL, nil)!
        let thumb = CGImageSourceCreateImageAtIndex(src, 0, nil)!
        XCTAssertEqual(max(thumb.width, thumb.height), 256)
        XCTAssertEqual(thumb.width, 256)
        XCTAssertEqual(thumb.height, 128)
    }

    // MARK: - ② 原子性

    func test_save_invalidRelativePath_isRejectedAndLeavesNoTemporary() throws {
        let lib = FileWorkLibrary(rootURL: root)
        var doc = sampleDocument()
        doc.pages[0].imageFile = "../escape.png"        // ディレクトリ外への書き込みは禁止
        XCTAssertThrowsError(try lib.save(doc, pageImages: images(for: doc))) { error in
            guard case WorkLibraryError.corrupted = error else {
                return XCTFail("corrupted を期待: \(error)")
            }
        }
        XCTAssertTrue(try temporaryDirectories().isEmpty, ".tmp-* が残らないこと")
        XCTAssertFalse(FileManager.default.fileExists(
            atPath: root.appendingPathComponent("escape.png").path))
        XCTAssertEqual(try lib.list().count, 0, "失敗した保存は一覧に現れない")
    }

    func test_save_failingMidway_keepsPreviousContentsIntact() throws {
        let lib = FileWorkLibrary(rootURL: root)
        let good = sampleDocument()
        try lib.save(good, pageImages: images(for: good))

        // page0 を書いた**あと**に page1 の親ディレクトリ作成が失敗する構成
        // （"pages/000.png" は通常ファイルなので、その下にディレクトリは作れない）
        var broken = good
        broken.title = "壊れた保存"
        broken.pages[1].imageFile = "pages/000.png/nested.png"
        XCTAssertThrowsError(try lib.save(broken, pageImages: images(for: broken)))

        XCTAssertTrue(try temporaryDirectories().isEmpty, ".tmp-* が残らないこと")
        let (loaded, dir) = try lib.load(id: good.id)
        XCTAssertEqual(loaded, good, "失敗した保存で既存の内容が壊れないこと")
        XCTAssertTrue(FileManager.default.fileExists(
            atPath: dir.appendingPathComponent(loaded.pages[1].imageFile).path))
    }

    func test_list_removesStaleTemporaryDirectories() throws {
        let lib = FileWorkLibrary(rootURL: root)
        let doc = sampleDocument()
        try lib.save(doc, pageImages: images(for: doc))
        let stale = root.appendingPathComponent(".tmp-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: stale, withIntermediateDirectories: true)

        XCTAssertEqual(try lib.list().count, 1)
        XCTAssertTrue(try temporaryDirectories().isEmpty, "次回 list() で掃除されること")
    }

    private func temporaryDirectories() throws -> [URL] {
        let entries = (try? FileManager.default.contentsOfDirectory(
            at: root, includingPropertiesForKeys: nil, options: [])) ?? []
        return entries.filter { $0.lastPathComponent.hasPrefix(".tmp-") }
    }

    // MARK: - ③ ゴールデン JSON（スキーマ v1）

    func test_goldenJSON_schemaV1() throws {
        let fixed = UUID(uuidString: "11111111-2222-3333-4444-555555555555")!
        var page = WorkPage(index: 0, sourceName: "sample.png", isFlatSource: true,
                            analysisOptions: .flatPage, documentType: .menkyoshoFront,
                            forcedType: nil, manualQuad: .fullImage, manualRotation: 180,
                            ocr: [OCRItem(text: "番号", box: rect(0.25, 0.5, 0.125, 0.0625),
                                          confidence: 0.75, upright: true)],
                            candidates: [], manual: ManualMask(), pixelWidth: 8, pixelHeight: 4)
        page.candidates = try [fixedIDCandidate(id: UUID(uuidString: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")!,
                                            source: .detector(.licenseNumber12)),
                           fixedIDCandidate(id: UUID(uuidString: "aaaaaaaa-bbbb-cccc-dddd-ffffffffffff")!,
                                            source: .fixedRegion)]
        let doc = WorkDocument(id: fixed, title: "golden", createdAt: date(1_700_000_000),
                               updatedAt: date(1_700_000_001.5), status: .exported,
                               lastExportedName: "golden.pdf", pages: [page])

        let data = try WorkJSON.encoder(pretty: true).encode(doc)
        let json = String(decoding: data, as: UTF8.self)
        XCTAssertEqual(json, Self.goldenJSON,
                       "スキーマ v1 の JSON 表現が変わった。変更するなら schemaVersion を上げること")

        // 読み戻せること（ゴールデンが実際に復元可能な形であることの担保）
        let back = try WorkJSON.decoder().decode(WorkDocument.self, from: Data(json.utf8))
        XCTAssertEqual(back, doc)
    }

    /// id を固定した候補（ゴールデン用）。通常の init は新規 UUID を振るため decode 経由で作る。
    private func fixedIDCandidate(id: UUID, source: MaskCandidate.Source) throws -> MaskCandidate {
        let seed = MaskCandidate(ruleID: "lic.number", label: "免許証番号",
                                 box: rect(0.5, 0.25, 0.25, 0.0625), source: source,
                                 confidence: 0.5, isOn: true, basis: "個人情報保護法",
                                 labelEn: nil, basisEn: nil)
        var object = try JSONSerialization.jsonObject(
            with: WorkJSON.encoder().encode(seed)) as! [String: Any]
        object["id"] = id.uuidString
        let data = try JSONSerialization.data(withJSONObject: object)
        return try WorkJSON.decoder().decode(MaskCandidate.self, from: data)
    }

    // MARK: - ④ 一覧

    func test_list_ordersByUpdatedAtDescending_withCountsAndThumbnails() throws {
        let lib = FileWorkLibrary(rootURL: root)
        var saved: [WorkDocument] = []
        for (i, t) in [1_757_900_000.0, 1_757_900_300.0, 1_757_900_200.0].enumerated() {
            var doc = sampleDocument()
            doc.title = "doc\(i)"
            doc.updatedAt = date(t)
            if i == 2 { doc.status = .exported; doc.lastExportedName = "doc2.pdf" }
            try lib.save(doc, pageImages: images(for: doc))
            saved.append(doc)
        }

        let list = try lib.list()
        XCTAssertEqual(list.map(\.title), ["doc1", "doc2", "doc0"])
        XCTAssertEqual(list.map(\.pageCount), [2, 2, 2])
        XCTAssertEqual(list.map(\.status), [.inProgress, .exported, .inProgress])
        XCTAssertEqual(Set(list.map(\.id)), Set(saved.map(\.id)))
        for summary in list {
            let url = try XCTUnwrap(summary.thumbnailURL)
            XCTAssertTrue(FileManager.default.fileExists(atPath: url.path),
                          "サムネイルが実ファイルであること")
        }
    }

    func test_list_isFastForManyDocuments() throws {
        let lib = FileWorkLibrary(rootURL: root)
        for i in 0..<100 {
            let page = WorkPage(index: 0, sourceName: "s.png", documentType: .generic,
                                pixelWidth: 8, pixelHeight: 8)
            let doc = WorkDocument(title: "d\(i)", createdAt: date(0),
                                   updatedAt: date(Double(i)), pages: [page])
            try lib.save(doc, pageImages: [0: image(width: 8, height: 8)])
        }
        let start = Date()
        let list = try lib.list()
        let elapsed = Date().timeIntervalSince(start)
        XCTAssertEqual(list.count, 100)
        XCTAssertLessThan(elapsed, 0.1, "一覧 100 件が 100ms 以内（summary.json のみ読む）")
    }

    func test_list_ignoresUnrelatedEntries() throws {
        let lib = FileWorkLibrary(rootURL: root)
        let doc = sampleDocument()
        try lib.save(doc, pageImages: images(for: doc))
        try Data("x".utf8).write(to: root.appendingPathComponent("README.txt"))
        try FileManager.default.createDirectory(
            at: root.appendingPathComponent("not-a-uuid"), withIntermediateDirectories: true)

        XCTAssertEqual(try lib.list().map(\.id), [doc.id])
    }

    // MARK: - ⑤ 削除・エラー

    func test_delete_removesOnlyThatDocument() throws {
        let lib = FileWorkLibrary(rootURL: root)
        let a = sampleDocument(), b = sampleDocument()
        try lib.save(a, pageImages: images(for: a))
        try lib.save(b, pageImages: images(for: b))

        try lib.delete(id: a.id)
        XCTAssertEqual(try lib.list().map(\.id), [b.id])
        XCTAssertThrowsError(try lib.load(id: a.id)) { error in
            XCTAssertEqual(error as? WorkLibraryError, .notFound(a.id))
        }
    }

    func test_deleteAll_emptiesLibrary() throws {
        let lib = FileWorkLibrary(rootURL: root)
        for _ in 0..<3 {
            let doc = sampleDocument()
            try lib.save(doc, pageImages: images(for: doc))
        }
        try lib.deleteAll()
        XCTAssertEqual(try lib.list().count, 0)
    }

    func test_load_unknownID_throwsNotFound() throws {
        let lib = FileWorkLibrary(rootURL: root)
        let missing = UUID()
        XCTAssertThrowsError(try lib.load(id: missing)) { error in
            XCTAssertEqual(error as? WorkLibraryError, .notFound(missing))
        }
    }

    func test_load_unknownSchemaVersion_throwsCorrupted() throws {
        let lib = FileWorkLibrary(rootURL: root)
        var doc = sampleDocument()
        doc.schemaVersion = 99
        try lib.save(doc, pageImages: images(for: doc))
        XCTAssertThrowsError(try lib.load(id: doc.id)) { error in
            guard case WorkLibraryError.corrupted = error else {
                return XCTFail("corrupted を期待: \(error)")
            }
        }
    }

    // MARK: - ⑥ Codable 追加（既存型）

    func test_maskCandidate_codable_preservesIDAndBothSources() throws {
        let encoder = WorkJSON.encoder(), decoder = WorkJSON.decoder()
        for source in [MaskCandidate.Source.fixedRegion, .detector(.zairyuNumber)] {
            let original = candidate(ruleID: "r", source: source, isOn: true, confidence: 0.5)
            let back = try decoder.decode(MaskCandidate.self, from: encoder.encode(original))
            XCTAssertEqual(back, original)
            XCTAssertEqual(back.id, original.id, "decode で新しい UUID を振らないこと")
            XCTAssertEqual(back.source, source)
        }
    }

    func test_maskCandidateSource_jsonShape() throws {
        let encoder = WorkJSON.encoder()
        let fixedJSON = String(decoding: try encoder.encode(MaskCandidate.Source.fixedRegion), as: UTF8.self)
        XCTAssertEqual(fixedJSON, #"{"kind":"fixedRegion"}"#)
        let detectorJSON = String(decoding: try encoder.encode(MaskCandidate.Source.detector(.myNumber12)),
                                  as: UTF8.self)
        XCTAssertEqual(detectorJSON, #"{"detector":"myNumber12","kind":"detector"}"#)
    }

    func test_analysisOptionsAndQuadAndOCRItem_roundTrip() throws {
        let encoder = WorkJSON.encoder(), decoder = WorkJSON.decoder()
        let options = AnalysisOptions.flatPageWithoutText
        XCTAssertEqual(try decoder.decode(AnalysisOptions.self, from: encoder.encode(options)), options)

        let quad = Quad(topLeft: CGPoint(x: 0, y: 1), topRight: CGPoint(x: 1, y: 1),
                        bottomRight: CGPoint(x: 1, y: 0), bottomLeft: CGPoint(x: 0, y: 0))
        XCTAssertEqual(try decoder.decode(Quad.self, from: encoder.encode(quad)), quad)

        let item = OCRItem(text: "個人番号", box: rect(0.1, 0.2, 0.3, 0.4), confidence: 0.5, upright: true)
        XCTAssertEqual(try decoder.decode(OCRItem.self, from: encoder.encode(item)), item)

        var manual = ManualMask()
        manual.rects = [rect(0, 0, 0.5, 0.5)]
        manual.strokes = [BrushStroke(points: [CGPoint(x: 0.25, y: 0.75)], width: 0.125)]
        XCTAssertEqual(try decoder.decode(ManualMask.self, from: encoder.encode(manual)), manual)
    }

    func test_date_decodesISO8601WithAndWithoutFractionalSeconds() throws {
        struct Box: Codable, Equatable { var at: Date }
        let decoder = WorkJSON.decoder()
        let withFraction = try decoder.decode(Box.self, from: Data(#"{"at":"2026-09-15T00:00:00.500Z"}"#.utf8))
        let withoutFraction = try decoder.decode(Box.self, from: Data(#"{"at":"2026-09-15T00:00:00Z"}"#.utf8))
        XCTAssertEqual(withFraction.at.timeIntervalSince(withoutFraction.at), 0.5, accuracy: 0.001)
        XCTAssertEqual(String(decoding: try WorkJSON.encoder().encode(withoutFraction), as: UTF8.self),
                       #"{"at":"2026-09-15T00:00:00.000Z"}"#)
    }
}

// MARK: - ゴールデン（スキーマ v1）

extension WorkLibraryTests {
    static let goldenJSON = #"""
{
  "createdAt" : "2023-11-14T22:13:20.000Z",
  "id" : "11111111-2222-3333-4444-555555555555",
  "lastExportedName" : "golden.pdf",
  "pages" : [
    {
      "analysisOptions" : {
        "detectUpright" : false,
        "documentDetection" : "insetOnly",
        "insetMaxArea" : 0.85,
        "recognizeText" : true
      },
      "candidates" : [
        {
          "basis" : "個人情報保護法",
          "box" : {
            "h" : 0.0625,
            "w" : 0.25,
            "x" : 0.5,
            "y" : 0.25
          },
          "confidence" : 0.5,
          "id" : "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE",
          "isOn" : true,
          "label" : "免許証番号",
          "ruleID" : "lic.number",
          "source" : {
            "detector" : "licenseNumber12",
            "kind" : "detector"
          }
        },
        {
          "basis" : "個人情報保護法",
          "box" : {
            "h" : 0.0625,
            "w" : 0.25,
            "x" : 0.5,
            "y" : 0.25
          },
          "confidence" : 0.5,
          "id" : "AAAAAAAA-BBBB-CCCC-DDDD-FFFFFFFFFFFF",
          "isOn" : true,
          "label" : "免許証番号",
          "ruleID" : "lic.number",
          "source" : {
            "kind" : "fixedRegion"
          }
        }
      ],
      "documentType" : "menkyoshoFront",
      "imageFile" : "pages\/000.png",
      "index" : 0,
      "isFlatSource" : true,
      "manual" : {
        "rects" : [

        ],
        "strokes" : [

        ]
      },
      "manualQuad" : {
        "bottomLeft" : [
          0,
          0
        ],
        "bottomRight" : [
          1,
          0
        ],
        "topLeft" : [
          0,
          1
        ],
        "topRight" : [
          1,
          1
        ]
      },
      "manualRotation" : 180,
      "ocr" : [
        {
          "box" : {
            "h" : 0.0625,
            "w" : 0.125,
            "x" : 0.25,
            "y" : 0.5
          },
          "confidence" : 0.75,
          "text" : "番号",
          "upright" : true
        }
      ],
      "pixelHeight" : 4,
      "pixelWidth" : 8,
      "sourceName" : "sample.png",
      "thumbnailFile" : "thumbs\/000.jpg"
    }
  ],
  "schemaVersion" : 1,
  "status" : "exported",
  "title" : "golden",
  "updatedAt" : "2023-11-14T22:13:21.500Z"
}
"""#
}
