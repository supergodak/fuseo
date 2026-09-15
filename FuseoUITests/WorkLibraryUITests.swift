import XCTest

/// 層2(Mac): WP-13「作業の保存と再開」の一本道。
/// 取り込み → 手動矩形 → 「新しい書類」 → 空画面の一覧から開く → 矩形が残っている → 書き出しシート。
///
/// 保存先は `--uitest` 起動のときだけ一時領域（`WorkLibraryLocation.makeLibrary(testing:)`）になるので、
/// 実ユーザーの `~/Library/Application Support/Fuseo/Library` は汚さない。
final class WorkLibraryUITests: XCTestCase {
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

    /// 失敗時の診断用。次に落ちたときに要素ツリーがログへ残るようにする。
    private func elementTree() -> String {
        let tree = app.debugDescription
        XCTContext.runActivity(named: "要素ツリー") { activity in
            let attachment = XCTAttachment(string: tree)
            attachment.name = "element-tree"
            attachment.lifetime = .keepAlways
            activity.add(attachment)
        }
        return tree
    }

    func test_newDocumentThenReopenFromLibrary_keepsManualMask() {
        let fixture = UITestFixture.makeSolidPNG()
        app = XCUIApplication()
        app.launchArguments += ["--uitest", "--uitest-fixture", fixture.path]
        app.launch()
        app.activate()

        // 1) Review まで進み、矩形ツールで手動マスクを1つ作る。
        let rectTool = app.descendants(matching: .any)["review.tool.rect"].firstMatch
        XCTAssertTrue(rectTool.waitForExistence(timeout: 25), "Review へ進んでいない")
        XCTAssertTrue(waitHittable(rectTool))
        rectTool.click()

        app.activate()
        let canvas = app.descendants(matching: .any)["review.canvas"].firstMatch
        XCTAssertTrue(canvas.waitForExistence(timeout: 8), "キャンバスが見つからない")
        canvas.coordinate(withNormalizedOffset: CGVector(dx: 0.35, dy: 0.40))
            .press(forDuration: 0.5,
                   thenDragTo: canvas.coordinate(withNormalizedOffset: CGVector(dx: 0.62, dy: 0.60)))
        let manualDelete = app.descendants(matching: .any)["review.manual.delete.0"].firstMatch
        XCTAssertTrue(manualDelete.waitForExistence(timeout: 8), "手動マスクが作られない")

        // 2) 「新しい書類」→ 確認アラートなしで空画面へ（WP-13 で導線が変わった）。
        let newDoc = app.descendants(matching: .any)["review.newDocButton"].firstMatch
        XCTAssertTrue(waitHittable(newDoc), "「新しい書類」が操作可能にならない")
        newDoc.click()

        let dropZone = app.descendants(matching: .any)["drop.zone"].firstMatch
        XCTAssertTrue(dropZone.waitForExistence(timeout: 10), "空画面に戻らない（確認アラートが残っている？）")

        // 3) 空画面上部の「作業中の書類」一覧から開く。
        let list = app.descendants(matching: .any)["library.list"].firstMatch
        XCTAssertTrue(list.waitForExistence(timeout: 10), "作業中の書類の一覧が出ない")
        // 要素型を .button に限定しない（SwiftUI/AppKit の露出の仕方に依存させない）。
        let openButton = app.descendants(matching: .any)
            .matching(NSPredicate(format: "identifier BEGINSWITH %@", "library.open."))
            .firstMatch
        XCTAssertTrue(openButton.waitForExistence(timeout: 10),
                      "一覧の「開く」が見つからない\n\(elementTree())")
        _ = waitHittable(openButton, 5)   // 前面化を待つだけ。hittable にならなくてもクリックは試す
        openButton.click()

        // 4) 復元されたページに手動マスクが残っている（＝再解析なしでマスク状態が戻る）。
        let restoredDelete = app.descendants(matching: .any)["review.manual.delete.0"].firstMatch
        XCTAssertTrue(restoredDelete.waitForExistence(timeout: 15),
                      "復元した作業に手動マスクが無い\n\(elementTree())")

        // 5) そのまま書き出しシートまで行ける。
        let exportButton = app.descendants(matching: .any)["review.exportButton"].firstMatch
        XCTAssertTrue(waitHittable(exportButton), "書き出しボタンが操作可能にならない")
        exportButton.click()
        let confirm = app.descendants(matching: .any)["export.confirmButton"].firstMatch
        XCTAssertTrue(confirm.waitForExistence(timeout: 8), "書き出しシートが表示されない")
    }
}
