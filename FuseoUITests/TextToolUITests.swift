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

    // MARK: - 共通の手順

    private func el(_ id: String) -> XCUIElement { app.descendants(matching: .any)[id].firstMatch }

    private func launchAndChooseTextTool() -> XCUIElement {
        let fixture = UITestFixture.makeSolidPNG()
        app = XCUIApplication()
        app.launchArguments += ["--uitest", "--uitest-fixture", fixture.path]
        app.launch()
        app.activate()
        let textTool = el("review.tool.text")
        XCTAssertTrue(textTool.waitForExistence(timeout: 25), "Review へ進んでいない（文字ツールが出ない）")
        XCTAssertTrue(waitHittable(textTool))
        textTool.click()
        let canvas = el("review.canvas")
        XCTAssertTrue(canvas.waitForExistence(timeout: 8), "キャンバスが見つからない")
        return canvas
    }

    /// 文字ツールで枠を作り、打って確定する（lines は ⌥Return で改行して続ける行）。
    private func createText(on canvas: XCUIElement, from: CGVector, to: CGVector, _ first: String, lines: [String] = []) {
        app.activate()
        canvas.coordinate(withNormalizedOffset: from).press(forDuration: 0.5, thenDragTo: canvas.coordinate(withNormalizedOffset: to))
        let editor = el("review.textEditor")
        XCTAssertTrue(editor.waitForExistence(timeout: 8), "枠を作っても入力欄が出ない")
        XCTAssertTrue(waitHittable(editor))
        editor.click()
        editor.typeText(first)
        for line in lines {
            editor.typeKey(.return, modifierFlags: .option)
            editor.typeText(line)
        }
        editor.typeText("\r")
        let deadline = Date().addingTimeInterval(8)
        while editor.exists && Date() < deadline { usleep(150_000) }
        XCTAssertFalse(editor.exists, "Return で確定しても入力欄が閉じない")
    }

    /// 右パネルの数値欄に値を入れる（全選択して打ち、Return で確定）。
    private func setField(_ id: String, _ value: String) {
        let field = el(id)
        XCTAssertTrue(field.waitForExistence(timeout: 8), "\(id) が見つからない")
        XCTAssertTrue(waitHittable(field), "\(id) が操作可能にならない")
        field.doubleClick()
        field.typeKey("a", modifierFlags: .command)
        field.typeText("\(value)\r")
        usleep(400_000)
    }

    /// 数値欄の値（"1.6" と "1.60" の違いを吸収して比べる）。
    private func number(_ id: String) -> Double? {
        let field = el(id)
        guard field.waitForExistence(timeout: 5) else { return nil }
        return Double(text(of: field).replacingOccurrences(of: ",", with: "."))
    }

    private func selectRow(_ index: Int) {
        let row = el("review.text.row.\(index)")
        XCTAssertTrue(row.waitForExistence(timeout: 10), "文字の一覧に \(index + 1) 件目が無い")
        _ = waitHittable(row, 5)
        row.click()
        usleep(400_000)
        XCTAssertEqual(text(of: el("review.textPanel.target")), "選択中の枠", "一覧のクリックで枠が選ばれない")
    }

    private func isSelected(_ id: String) -> Bool {
        let e = el(id)
        return e.waitForExistence(timeout: 5) && e.isSelected
    }

    /// キャンバスの撮影（表示が変わったかの確認用）。
    private func canvasPixels(_ canvas: XCUIElement) -> (data: [UInt8], w: Int, h: Int) {
        let image = canvas.screenshot().image
        var rect = CGRect(origin: .zero, size: image.size)
        guard let cg = image.cgImage(forProposedRect: &rect, context: nil, hints: nil) else { return ([], 0, 0) }
        let w = cg.width, h = cg.height
        var data = [UInt8](repeating: 0, count: w * h * 4)
        let ctx = CGContext(data: &data, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.draw(cg, in: CGRect(x: 0, y: 0, width: w, height: h))
        return (data, w, h)
    }

    private func reddishCount(_ px: (data: [UInt8], w: Int, h: Int)) -> Int {
        var n = 0
        for i in stride(from: 0, to: px.data.count, by: 4)
        where Int(px.data[i]) > 150 && Int(px.data[i + 1]) < 90 && Int(px.data[i + 2]) < 90 { n += 1 }
        return n
    }

    // MARK: - 開き直し・右パネル

    /// 文字入りの作業を「新しい書類」→ 一覧から開き直しても、文字と設定（サイズ・行間・字間・色・マス数）が残る。
    func test_reopenFromLibrary_keepsTextsAndTheirSettings() {
        let canvas = launchAndChooseTextTool()

        // 1 つ目: 2 行・14pt・行間 1.6・字間 0.5・赤
        createText(on: canvas, from: CGVector(dx: 0.20, dy: 0.20), to: CGVector(dx: 0.70, dy: 0.40), "Taro Yamada", lines: ["Tokyo"])
        selectRow(0)
        setField("review.textPanel.size", "14")
        setField("review.textPanel.lineSpacing", "1.6")
        setField("review.textPanel.letterSpacing", "0.5")
        el("review.textPanel.color.2").click()
        usleep(400_000)

        // 2 つ目: マス目 7・青
        createText(on: canvas, from: CGVector(dx: 0.20, dy: 0.60), to: CGVector(dx: 0.60, dy: 0.70), "1000001")
        selectRow(1)
        setField("review.textPanel.cells", "7")
        el("review.textPanel.color.1").click()
        usleep(400_000)

        // 新しい書類 → 一覧から開き直す
        let newDoc = el("review.newDocButton")
        XCTAssertTrue(waitHittable(newDoc), "「新しい書類」が操作可能にならない")
        newDoc.click()
        XCTAssertTrue(el("drop.zone").waitForExistence(timeout: 10), "空画面に戻らない")
        let openButton = app.descendants(matching: .any)
            .matching(NSPredicate(format: "identifier BEGINSWITH %@", "library.open.")).firstMatch
        XCTAssertTrue(openButton.waitForExistence(timeout: 10), "一覧の「開く」が見つからない")
        _ = waitHittable(openButton, 5)
        openButton.click()

        // 文字が 2 件とも戻り、設定もそのまま
        let row0 = el("review.text.row.0")
        XCTAssertTrue(row0.waitForExistence(timeout: 15), "開き直した作業に文字が無い（読み込みエラー？）")
        XCTAssertTrue(text(of: row0).contains("Taro Yamada") && text(of: row0).contains("2 行"), "1 つ目の文字: \(text(of: row0))")
        XCTAssertTrue(text(of: el("review.text.row.1")).contains("1000001"))

        selectRow(0)
        XCTAssertEqual(number("review.textPanel.size"), 14)
        XCTAssertEqual(number("review.textPanel.lineSpacing"), 1.6)
        XCTAssertEqual(number("review.textPanel.letterSpacing"), 0.5)
        XCTAssertTrue(isSelected("review.textPanel.color.2"), "色（赤）が戻っていない")

        selectRow(1)
        XCTAssertEqual(number("review.textPanel.cells"), 7)
        XCTAssertTrue(isSelected("review.textPanel.color.1"), "色（青）が戻っていない")
    }

    /// 右パネルの mm・字間・色を入れると、枠の位置と画面の表示が変わる。
    func test_panel_mmLetterSpacingAndColor_changeTheFrameAndTheCanvas() {
        let canvas = launchAndChooseTextTool()
        createText(on: canvas, from: CGVector(dx: 0.20, dy: 0.40), to: CGVector(dx: 0.50, dy: 0.50), "ABCD")
        selectRow(0)

        // mm で位置と大きさを入れると、そのまま読み返せる（入れた順に依らない）
        setField("review.textPanel.width", "40")
        setField("review.textPanel.height", "8")
        setField("review.textPanel.x", "10")
        setField("review.textPanel.y", "12.5")
        XCTAssertEqual(number("review.textPanel.x") ?? -1, 10, accuracy: 0.11)
        XCTAssertEqual(number("review.textPanel.y") ?? -1, 12.5, accuracy: 0.11)
        XCTAssertEqual(number("review.textPanel.width") ?? -1, 40, accuracy: 0.11)
        XCTAssertEqual(number("review.textPanel.height") ?? -1, 8, accuracy: 0.11)

        // 字間を変えると表示が変わる（選択枠の上ではなく、描いた文字が動く）
        // 枠（40×8mm）に収まる大きさにする（はみ出すと枠が赤い点線になり、色の判定を邪魔する）
        setField("review.textPanel.size", "14")
        let before = canvasPixels(canvas)
        setField("review.textPanel.letterSpacing", "1")
        XCTAssertEqual(number("review.textPanel.letterSpacing"), 1)
        let after = canvasPixels(canvas)
        XCTAssertEqual(before.w, after.w)
        XCTAssertNotEqual(before.data, after.data, "字間を変えても表示が変わらない")

        // 赤を選ぶと赤い文字が描かれる
        XCTAssertLessThan(reddishCount(after), 20, "前提: まだ赤くない（はみ出しの赤い点線も出ていない）")
        el("review.textPanel.color.2").click()
        usleep(600_000)
        XCTAssertTrue(isSelected("review.textPanel.color.2"))
        XCTAssertGreaterThan(reddishCount(canvasPixels(canvas)), 50, "赤を選んでも文字が赤くならない")

        // マス数を入れると字間の欄は操作できなくなる
        setField("review.textPanel.cells", "4")
        XCTAssertFalse(el("review.textPanel.letterSpacing").isEnabled, "マス目なのに字間を変えられる")
    }

    /// macOS の SwiftUI Text は文字列が label ではなく value に入ることがある。
    private func text(of element: XCUIElement) -> String {
        let value = element.value as? String ?? ""
        return value.isEmpty ? element.label : value
    }
}
