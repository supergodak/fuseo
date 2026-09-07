import XCTest

/// 層2(Mac): PDF を開く → 確認画面に各ページが並ぶ → 書き出しシートまで進める（WP-10 §2.5）。
/// PDF はテスト内で合成する（fixtures-private は使わない）。
final class PDFIntakeUITests: XCTestCase {
    private var app: XCUIApplication!

    override func setUp() { continueAfterFailure = false }
    override func tearDown() { app?.terminate(); app = nil; super.tearDown() }

    @discardableResult
    private func waitHittable(_ element: XCUIElement, _ timeout: TimeInterval = 15) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if element.exists && element.isHittable { return true }
            app.activate()
            usleep(150_000)
        }
        return element.exists && element.isHittable
    }

    func test_pdf_opensAsPagesAndReachesExportSheet() {
        let pdf = UITestFixture.makeMultiPagePDF(pageCount: 2)
        app = XCUIApplication()
        app.launchArguments += ["--uitest", "--uitest-fixture", pdf.path]
        app.launch()
        app.activate()

        // 2ページに展開されて Review へ（ページレールの2枚目が出ることで確認）。
        let secondPage = app.descendants(matching: .any)["review.page.1"].firstMatch
        XCTAssertTrue(secondPage.waitForExistence(timeout: 60), "PDFが2ページに展開されて確認画面に並んでいない")

        // 「画像として処理する」注意文が確認画面に出ている（安全側の仕様の説明・§2.1）。
        let notice = app.descendants(matching: .any)["review.pdfNotice"].firstMatch
        XCTAssertTrue(notice.waitForExistence(timeout: 10), "PDFの注意文が確認画面に出ていない")

        // 書き出しシートまで進める。
        let exportButton = app.descendants(matching: .any)["review.exportButton"].firstMatch
        XCTAssertTrue(waitHittable(exportButton), "書き出しボタンが操作可能にならない")
        exportButton.click()

        let confirm = app.descendants(matching: .any)["export.confirmButton"].firstMatch
        XCTAssertTrue(confirm.waitForExistence(timeout: 10), "書き出しシートが表示されない")
        XCTAssertTrue(app.descendants(matching: .any)["export.pdfNotice"].firstMatch.exists,
                      "書き出しシートにもPDFの注意文が出ていない")
    }
}
