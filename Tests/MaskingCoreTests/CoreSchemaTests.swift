import XCTest
@testable import MaskingCore

/// WP-1 スキーマ固定の検証: 座標規約・プリセットローダ・チェックデジット。
/// （チェックデジットの実番号検証は WP-0 で実物により実施済み。ここでは公開仕様の合成値で回帰させる）
final class CoreSchemaTests: XCTestCase {

    // MARK: - 座標規約（core-design.md §1）

    func test_fromTopOrigin_convertsToBottomLeft() {
        // yTop=0.2, h=0.1 → 下端 y = 1 - 0.2 - 0.1 = 0.7
        let r = CoordinateSpace.fromTopOrigin(x: 0.1, yTop: 0.2, w: 0.5, h: 0.1)
        XCTAssertEqual(r.y, 0.7, accuracy: 1e-9)
        XCTAssertEqual(r.x, 0.1, accuracy: 1e-9)
    }

    func test_viewRect_flipsY() {
        let n = NormRect(x: 0, y: 0, w: 1, h: 0.5)                  // 画像の下半分
        let v = CoordinateSpace.viewRect(n, in: CGSize(width: 100, height: 200))
        XCTAssertEqual(v.origin.y, 100, accuracy: 1e-9)             // 表示では下半分＝y=100から
        XCTAssertEqual(v.height, 100, accuracy: 1e-9)
    }

    func test_pixelRect_isPlainScaling() {
        let n = NormRect(x: 0.25, y: 0.5, w: 0.5, h: 0.25)
        let p = CoordinateSpace.pixelRect(n, in: CGSize(width: 400, height: 200))
        XCTAssertEqual(p, CGRect(x: 100, y: 100, width: 200, height: 50))
    }

    func test_padded_clampsToUnitSquare() {
        let r = NormRect(x: 0.0, y: 0.95, w: 0.2, h: 0.05).padded(by: 0.02)
        XCTAssertEqual(r.x, 0, accuracy: 1e-9)                       // 左端でクランプ
        XCTAssertLessThanOrEqual(r.y + r.h, 1.0 + 1e-9)              // 上端でクランプ
    }

    func test_normRectFromViewRect_roundTripsWithViewRect() {
        // 表示→正規化→表示 で元に戻る（手動矩形ツールの契約）
        let size = CGSize(width: 400, height: 200)
        let view = CGRect(x: 40, y: 30, width: 120, height: 80)
        let norm = CoordinateSpace.normRect(fromViewRect: view, in: size)
        let back = CoordinateSpace.viewRect(norm, in: size)
        XCTAssertEqual(back.minX, view.minX, accuracy: 1e-9)
        XCTAssertEqual(back.minY, view.minY, accuracy: 1e-9)
        XCTAssertEqual(back.width, view.width, accuracy: 1e-9)
        XCTAssertEqual(back.height, view.height, accuracy: 1e-9)
    }

    func test_normRectFromViewRect_clampsOutOfCanvasDrag() {
        // キャンバス外へはみ出すドラッグは 0..1 に切り詰める
        let size = CGSize(width: 100, height: 100)
        let r = CoordinateSpace.normRect(fromViewRect: CGRect(x: -20, y: 80, width: 60, height: 60), in: size)
        XCTAssertEqual(r.x, 0, accuracy: 1e-9)
        XCTAssertEqual(r.y, 0, accuracy: 1e-9)                        // 下端（表示のはみ出し下＝正規化y=0）
        XCTAssertEqual(r.w, 0.4, accuracy: 1e-9)
        XCTAssertEqual(r.h, 0.2, accuracy: 1e-9)
    }

    func test_normPointFromViewPoint_flipsAndClamps() {
        let size = CGSize(width: 200, height: 100)
        let p = CoordinateSpace.normPoint(fromViewPoint: CGPoint(x: 50, y: 25), in: size)
        XCTAssertEqual(p.x, 0.25, accuracy: 1e-9)
        XCTAssertEqual(p.y, 0.75, accuracy: 1e-9)                     // Yフリップ
        let clamped = CoordinateSpace.normPoint(fromViewPoint: CGPoint(x: 300, y: -10), in: size)
        XCTAssertEqual(clamped.x, 1.0, accuracy: 1e-9)
        XCTAssertEqual(clamped.y, 1.0, accuracy: 1e-9)
    }

    // MARK: - プリセットローダ（generic.json 同梱で実効検証）

    func test_bundledPresets_loadAndValidate() throws {
        let presets = try PresetStore.loadAll()
        XCTAssertFalse(presets.isEmpty)
        let generic = try XCTUnwrap(PresetStore.preset(for: .generic, in: presets))
        XCTAssertEqual(generic.schemaVersion, PresetStore.currentSchemaVersion)
        XCTAssertFalse(generic.rules.isEmpty)
        // 全ルール: basis 必須・kind整合（validate はローダで済んでいるが回帰として明示）
        for rule in generic.rules {
            XCTAssertFalse(rule.basis.isEmpty)
            XCTAssertNoThrow(try rule.validate())
        }
    }

    func test_invalidRule_failsValidation() {
        // dynamic なのに detector なし → presetInvalid
        let bad = MaskRule(id: "t.bad", label: "x", kind: .dynamic, detector: nil,
                           region: nil, defaultOn: true, basis: "b", padding: nil)
        XCTAssertThrowsError(try bad.validate())
        // basis 空 → presetInvalid
        let noBasis = MaskRule(id: "t.nb", label: "x", kind: .dynamic, detector: .face,
                               region: nil, defaultOn: true, basis: "", padding: nil)
        XCTAssertThrowsError(try noBasis.validate())
    }

    // MARK: - チェックデジット（公開仕様の合成値）

    func test_myNumberCheckdigit() {
        // 総務省令式で自作した検証値: 本体 12345678901 → 検査用数字 8
        XCTAssertTrue(Checkdigits.isValidMyNumber("123456789018"))
        XCTAssertFalse(Checkdigits.isValidMyNumber("123456789012"))
        XCTAssertFalse(Checkdigits.isValidMyNumber("12345678901"))     // 桁不足
    }

    func test_licenseNumberCheckdigit() {
        // WP-0 で実物により成立を確認した式の回帰。
        // 合成値: 先頭10桁 1234567890 → Σ(右から×2..7巡回)=195, 195 mod 11=8, CD=(11-8)%11=3。12桁目=再交付回数(任意)。
        XCTAssertTrue(Checkdigits.isValidLicenseNumber("123456789030"))
        XCTAssertFalse(Checkdigits.isValidLicenseNumber("123456789040"))
    }

    func test_luhn() {
        XCTAssertTrue(Checkdigits.isValidLuhn("4111111111111111"))     // 周知のテスト番号
        XCTAssertFalse(Checkdigits.isValidLuhn("4111111111111112"))
    }
}
