import XCTest

/// 層2: 起動→フィクスチャ自動ロード→Review→矩形ツールで手動マスク追加→書き出しシート表示（wp5 §8）。
final class ReviewSmokeUITests: XCTestCase {
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

    private func launchWithFixture() -> XCUIApplication {
        let fixture = UITestFixture.makeSolidPNG()
        let app = XCUIApplication()
        app.launchArguments += ["--uitest", "--uitest-fixture", fixture.path]
        app.launch()
        app.activate()
        return app
    }

    func test_cropSheet_opensAdjustsAndApplies() {
        app = launchWithFixture()

        // Review 到達 → 「切り抜きを調整…」を開く。
        let cropButton = app.descendants(matching: .any)["review.cropButton"].firstMatch
        XCTAssertTrue(cropButton.waitForExistence(timeout: 25), "切り抜きボタンが出ない")
        XCTAssertTrue(waitHittable(cropButton))
        cropButton.click()

        // シートが出て、「全体を使う」→「適用」で再解析されて閉じる。
        let fullButton = app.descendants(matching: .any)["crop.full"].firstMatch
        XCTAssertTrue(fullButton.waitForExistence(timeout: 10), "切り抜きシートが表示されない")
        XCTAssertTrue(waitHittable(fullButton))
        fullButton.click()

        let applyButton = app.descendants(matching: .any)["crop.apply"].firstMatch
        XCTAssertTrue(waitHittable(applyButton), "適用ボタンが操作可能にならない")
        applyButton.click()

        // 再解析が終わるとシートが閉じ、Review に戻る（合成画像なので数秒で完了する）。
        let deadline = Date().addingTimeInterval(20)
        while fullButton.exists && Date() < deadline { usleep(200_000) }
        XCTAssertFalse(fullButton.exists, "適用後にシートが閉じない")
        let canvas = app.descendants(matching: .any)["review.canvas"].firstMatch
        XCTAssertTrue(canvas.waitForExistence(timeout: 8), "Review に戻らない")
    }

    func test_smoke_loadDrawRectAndOpenExportSheet() {
        app = launchWithFixture()

        // Review まで自動で進む（ツールバーの矩形ツール＝Button の出現で確認）。
        let rectTool = app.descendants(matching: .any)["review.tool.rect"].firstMatch
        XCTAssertTrue(rectTool.waitForExistence(timeout: 25), "Review へ進んでいない（矩形ツールが出ない）")
        XCTAssertTrue(waitHittable(rectTool), "矩形ツールが操作可能にならない")
        rectTool.click()

        // キャンバス上でドラッグして手動マスクを1つ作る（キャンバス要素のローカル座標を使う）。
        app.activate()
        let canvas = app.descendants(matching: .any)["review.canvas"].firstMatch
        XCTAssertTrue(canvas.waitForExistence(timeout: 8), "キャンバスが見つからない")
        let start = canvas.coordinate(withNormalizedOffset: CGVector(dx: 0.35, dy: 0.40))
        let end = canvas.coordinate(withNormalizedOffset: CGVector(dx: 0.62, dy: 0.60))
        start.press(forDuration: 0.5, thenDragTo: end)

        // 手動リストに1件現れる（削除ボタン＝Button の出現で確認）。
        let manualDelete = app.descendants(matching: .any)["review.manual.delete.0"].firstMatch
        XCTAssertTrue(manualDelete.waitForExistence(timeout: 8), "手動マスクがリストに追加されない")

        // 書き出しシートを開く。
        let exportButton = app.descendants(matching: .any)["review.exportButton"].firstMatch
        XCTAssertTrue(waitHittable(exportButton), "書き出しボタンが操作可能にならない")
        exportButton.click()

        let confirm = app.descendants(matching: .any)["export.confirmButton"].firstMatch
        XCTAssertTrue(confirm.waitForExistence(timeout: 8), "書き出しシートが表示されない")

        // マスクを1つ足したので「マスクなし」警告は出ない。
        let zeroWarning = app.descendants(matching: .any)["export.zeroMaskWarning"].firstMatch
        XCTAssertFalse(zeroWarning.exists, "マスクを付けたのにゼロマスク警告が出ている")
    }
}
