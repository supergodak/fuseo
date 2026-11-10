import XCTest

/// 層2: 文字入れ 0.3.0（docs/form-fill-design.md §6）。文字ツールで枠を作って打つ → 確定 → 一覧に出る →
/// 書き出しシートに「文字 1箇所」が出る。合成フィクスチャのみ（fixtures-private は使わない）。
final class TextToolUITests: XCTestCase {
    private var app: XCUIApplication!

    override func setUp() { continueAfterFailure = false }
    override func tearDown() { app?.terminate(); app = nil; super.tearDown() }

    @discardableResult
    private func waitHittable(_ element: XCUIElement, _ timeout: TimeInterval = 15) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if element.exists && element.isHittable { return true }
            app.activate()   // 他アプリにフォーカスを奪われても前面へ戻す（フレーク対策）
            usleep(150_000)
        }
        return element.exists && element.isHittable
    }

    func test_textTool_dragFrameTypeCommit_andExportSheetShowsCount() {
        let fixture = UITestFixture.makeSolidPNG()
        app = XCUIApplication()
        app.launchArguments += ["--uitest", "--uitest-fixture", fixture.path]
        app.launch()
        app.activate()

        let textTool = app.descendants(matching: .any)["review.tool.text"].firstMatch
        XCTAssertTrue(textTool.waitForExistence(timeout: 25), "Review へ進んでいない（文字ツールが出ない）")
        XCTAssertTrue(waitHittable(textTool), "文字ツールが操作可能にならない")
        textTool.click()

        // 文字ツールを選ぶと右に文字パネル（次に作る枠の設定）が出る
        let panelSize = app.descendants(matching: .any)["review.textPanel.size"].firstMatch
        XCTAssertTrue(panelSize.waitForExistence(timeout: 8), "文字パネルが出ない")

        // キャンバス上をドラッグして枠を作る → 入力欄が出る
        app.activate()
        let canvas = app.descendants(matching: .any)["review.canvas"].firstMatch
        XCTAssertTrue(canvas.waitForExistence(timeout: 8), "キャンバスが見つからない")
        let start = canvas.coordinate(withNormalizedOffset: CGVector(dx: 0.30, dy: 0.30))
        let end = canvas.coordinate(withNormalizedOffset: CGVector(dx: 0.60, dy: 0.40))
        start.press(forDuration: 0.5, thenDragTo: end)

        let editor = app.descendants(matching: .any)["review.textEditor"].firstMatch
        XCTAssertTrue(editor.waitForExistence(timeout: 8), "枠を作っても入力欄が出ない")
        XCTAssertTrue(waitHittable(editor))
        editor.click()
        editor.typeText("Taro Yamada")
        editor.typeKey(.return, modifierFlags: .option)           // ⌥Return で改行（確定しない）
        XCTAssertTrue(editor.exists, "⌥Return で入力欄が閉じてしまった")
        editor.typeText("Tokyo")
        editor.typeText("\r")                                     // Return で確定

        // 確定すると入力欄が閉じ、右の一覧に 1 件出る
        let deadline = Date().addingTimeInterval(8)
        while editor.exists && Date() < deadline { usleep(150_000) }
        XCTAssertFalse(editor.exists, "Return で確定しても入力欄が閉じない")
        let row = app.descendants(matching: .any)["review.text.row.0"].firstMatch
        XCTAssertTrue(row.waitForExistence(timeout: 8), "文字が一覧に出ない")
        XCTAssertTrue(text(of: row).contains("Taro Yamada"), "一覧の文字が違う: \(text(of: row))")
        XCTAssertTrue(text(of: row).contains("2 行"), "⌥Return の改行が入っていない: \(text(of: row))")

        // 書き出しシートに「文字 1箇所」
        let exportButton = app.descendants(matching: .any)["review.exportButton"].firstMatch
        XCTAssertTrue(waitHittable(exportButton), "書き出しボタンが操作可能にならない")
        exportButton.click()
        let textCount = app.descendants(matching: .any)["export.textCount"].firstMatch
        XCTAssertTrue(textCount.waitForExistence(timeout: 8), "書き出しシートに文字の数が出ない")
        XCTAssertTrue(text(of: textCount).contains("文字 1"), "文字の数が違う: \(text(of: textCount))")
    }

    /// macOS の SwiftUI Text は文字列が label ではなく value に入ることがある。
    private func text(of element: XCUIElement) -> String {
        let value = element.value as? String ?? ""
        return value.isEmpty ? element.label : value
    }
}
