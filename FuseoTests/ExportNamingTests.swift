import XCTest
import MaskingCore
@testable import Fuseo

/// 層1: 書き出しファイル名の規約。
final class ExportNamingTests: XCTestCase {

    func test_defaultFileName_appendsMaskedAndExtension() {
        XCTAssertEqual(ExportNaming.defaultFileName(firstSourceName: "photo.jpeg", format: .pdf),
                       "photo-masked.pdf")
        XCTAssertEqual(ExportNaming.defaultFileName(firstSourceName: "IMG_0001.HEIC", format: .jpeg),
                       "IMG_0001-masked.jpeg")
        XCTAssertEqual(ExportNaming.defaultFileName(firstSourceName: "no-ext", format: .png),
                       "no-ext-masked.png")
    }

    func test_defaultFileName_emptyStemFallsBackToDocument() {
        XCTAssertEqual(ExportNaming.defaultFileName(firstSourceName: "", format: .pdf),
                       "document-masked.pdf")
    }

    func test_imagePageURL_splitsWithPageNumber() {
        let base = URL(fileURLWithPath: "/tmp/scan-masked.jpeg")
        XCTAssertEqual(ExportNaming.imagePageURL(base: base, pageNumber: 1).lastPathComponent,
                       "scan-masked-1.jpeg")
        XCTAssertEqual(ExportNaming.imagePageURL(base: base, pageNumber: 2).lastPathComponent,
                       "scan-masked-2.jpeg")
    }

    func test_uniqueURL_appendsCounterOnCollision() {
        let dir = URL(fileURLWithPath: "/tmp/out")
        var existing: Set<String> = ["/tmp/out/a-masked.png", "/tmp/out/a-masked-2.png"]
        let url = ExportNaming.uniqueURL(in: dir, fileName: "a-masked.png") { existing.contains($0.path) }
        XCTAssertEqual(url.lastPathComponent, "a-masked-3.png")
        existing = []
        let first = ExportNaming.uniqueURL(in: dir, fileName: "a-masked.png") { existing.contains($0.path) }
        XCTAssertEqual(first.lastPathComponent, "a-masked.png")
    }
}
