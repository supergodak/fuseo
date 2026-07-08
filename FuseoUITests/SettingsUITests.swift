import XCTest

/// 層2: 設定ウィンドウの開閉と値変更（wp5 §8）。
final class SettingsUITests: XCTestCase {
    private var app: XCUIApplication!

    override func setUp() { continueAfterFailure = false }
    override func tearDown() { app?.terminate(); app = nil; super.tearDown() }

    private func value(of element: XCUIElement) -> String { String(describing: element.value) }

    @discardableResult
    private func waitHittable(_ element: XCUIElement, _ timeout: TimeInterval = 10) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if element.exists && element.isHittable { return true }
            app.activate()
            usleep(150_000)
        }
        return element.exists && element.isHittable
    }

    private func launchWithSettings() -> XCUIElement {
        app = XCUIApplication()
        app.launchArguments += ["--uitest", "--uitest-open-settings"]
        app.launch()
        app.activate()
        let window = app.windows["Fuseo Settings"]
        XCTAssertTrue(window.waitForExistence(timeout: 15), "設定ウィンドウが開かない")
        return window
    }

    func test_settingsWindow_opensWithGeneralControls() {
        let window = launchWithSettings()
        XCTAssertTrue(window.descendants(matching: .any)["settings.general.searchable"].waitForExistence(timeout: 8))
        XCTAssertTrue(window.descendants(matching: .any)["settings.general.faceDefault"].exists)
    }

    func test_general_searchableToggle_flips() {
        let window = launchWithSettings()
        let toggle = window.switches["settings.general.searchable"]
        XCTAssertTrue(waitHittable(toggle), "検索可能PDFトグルが操作可能にならない")
        let before = value(of: toggle)
        toggle.click()
        XCTAssertNotEqual(before, value(of: toggle), "クリックでトグルが反転するはず")
    }
}
