import CoreGraphics
import Foundation
import PDFKit

/// PDF を 1 ページずつ画像化する入口（WP-10 §2.1・§2.2）。
///
/// **方式はラスタライズ焼き込み一択**。黒矩形を上に描くだけでは下のテキストが残り復元できてしまうため、
/// Fuseo は各ページを画像へ潰してから既存パイプライン（検出→確認→焼き込み）へ渡す。
/// この変換の時点で、テキスト層・フォーム入力値・注釈・非表示レイヤー・添付は**すべて画像に潰れて消える**。
///
/// 副作用として出力 PDF は文字の選択・検索ができなくなる。検索性が要る場合は
/// `ExportOptions.searchableText`（不可視 OCR テキスト層）で復元する。マスクと交差する
/// OCR 観測は `Exporter.drawInvisibleTextLayer` が既に除外している。
///
/// メモリ: 全ページを同時に保持しない。`rasterize` は 1 ページ生成するたびに `handler` へ渡し、
/// autoreleasepool で解放する。長辺は `maxLongSidePx` で頭打ちにする。
public struct PDFRasterizer {

    /// ラスタライズ済みの 1 ページ。
    public struct Page {
        /// 0 始まりのページ番号。
        public let index: Int
        /// 画像化した結果。
        public let cgImage: CGImage
        /// 元 PDF のページサイズ（pt・`/Rotate` 適用後）。
        public let pointSize: CGSize

        public init(index: Int, cgImage: CGImage, pointSize: CGSize) {
            self.index = index
            self.cgImage = cgImage
            self.pointSize = pointSize
        }
    }

    public enum Failure: Error, Equatable {
        /// 暗号化されており、パスワードが渡されなかった。
        case locked
        /// パスワードが誤っている。
        case wrongPassword
        /// PDF として読めない、またはページの描画に失敗した（権限フラグ等）。
        case unreadable
        /// ページ数が上限を超えている（実ページ数を伴う）。
        case tooManyPages(Int)
    }

    /// 描画解像度（dpi）。既定 300dpi は A4 で 2480×3508px。
    public let dpi: Double
    /// 長辺のピクセル上限。超える場合は dpi を落として収める（ポスター等の保険）。
    public let maxLongSidePx: Int
    /// 受け入れる最大ページ数。
    public let maxPages: Int

    public init(dpi: Double = 300, maxLongSidePx: Int = 4000, maxPages: Int = 50) {
        self.dpi = dpi
        self.maxLongSidePx = maxLongSidePx
        self.maxPages = maxPages
    }

    /// 暗号化されて未解錠か。パスワード入力 UI を出すかの判定に使う。
    /// PDF として開けない場合は false（`rasterize` 側で `.unreadable` にする）。
    public static func isLocked(url: URL) -> Bool {
        guard let doc = PDFDocument(url: url) else { return false }
        return doc.isLocked
    }

    /// 1 ページずつラスタライズして `handler` に渡す。
    ///
    /// - Parameters:
    ///   - url: 入力 PDF。
    ///   - password: 暗号化 PDF の解錠パスワード。**メモリ内のみで扱い、保存もログ出力もしない。**
    ///   - handler: ページごとに呼ばれる。ここで一時ファイル化するなどして次のページへ進む。
    public func rasterize(url: URL, password: String? = nil,
                          handler: (Page) throws -> Void) throws {
        guard let doc = PDFDocument(url: url) else { throw Failure.unreadable }

        if doc.isLocked {
            guard let password else { throw Failure.locked }
            // unlock 失敗時もパスワードそのものはログに出さない。
            guard doc.unlock(withPassword: password) else { throw Failure.wrongPassword }
        }

        let count = doc.pageCount
        guard count > 0 else { throw Failure.unreadable }
        guard count <= maxPages else { throw Failure.tooManyPages(count) }

        MaskingLog.pipeline.info("PDF入力: \(count, privacy: .public)ページを画像化（dpi=\(self.dpi, privacy: .public)）")

        for index in 0..<count {
            guard let page = doc.page(at: index), let cgPage = page.pageRef else {
                throw Failure.unreadable
            }
            // ページごとにプールを閉じ、次ページ生成前にビットマップを解放する。
            try autoreleasepool {
                let rendered = try render(cgPage)
                try handler(Page(index: index, cgImage: rendered.image, pointSize: rendered.pointSize))
            }
        }
    }

    // MARK: - 描画

    /// `/Rotate` を適用した実サイズで 1 ページを描画する。
    ///
    /// PDF もビットマップコンテキストも**左下原点**なので上下反転は不要。回転とスケールは
    /// `CGPDFPage.getDrawingTransform` に任せる（この関数はページの `/Rotate` を考慮する）。
    /// 自前で回転行列を組むと `/Rotate` と二重適用になり、逆さ・潰れの典型バグを生む。
    private func render(_ cgPage: CGPDFPage) throws -> (image: CGImage, pointSize: CGSize) {
        let box = cgPage.getBoxRect(.mediaBox)
        guard box.width > 0, box.height > 0 else { throw Failure.unreadable }

        // /Rotate は 90 の倍数（負値・360超も正規化する）。
        let rotation = ((Int(cgPage.rotationAngle) % 360) + 360) % 360
        let quarterTurned = (rotation == 90 || rotation == 270)
        let pointSize = quarterTurned
            ? CGSize(width: box.height, height: box.width)
            : CGSize(width: box.width, height: box.height)

        // dpi 換算。長辺が上限を超える場合だけ縮尺を落とす。
        var scale = dpi / 72.0
        let longSidePt = max(pointSize.width, pointSize.height)
        if longSidePt * scale > Double(maxLongSidePx) {
            scale = Double(maxLongSidePx) / longSidePt
        }
        let pxWidth = max(1, Int((pointSize.width * scale).rounded()))
        let pxHeight = max(1, Int((pointSize.height * scale).rounded()))

        guard let ctx = CGContext(data: nil, width: pxWidth, height: pxHeight,
                                  bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw Failure.unreadable
        }

        // PDF の紙は透過。白で下地を塗ってから描かないと、黒塗り結果が黒地に黒になる。
        ctx.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: pxWidth, height: pxHeight))

        // 座標系: まず pt→px の拡大を CTM に載せ、その上で **pt 単位の矩形**（/Rotate 適用後のページサイズ）へ
        // getDrawingTransform でフィットさせる。ピクセル矩形を直接 getDrawingTransform に渡すと内容が
        // 約 57% に縮小して中央に描かれる（2026-09-17 実測: 0.21..0.74 / 正しくは 0.10..0.90）。
        // /Rotate はこの関数が考慮する（自前で回転行列を組むと二重適用になる）。
        ctx.scaleBy(x: CGFloat(scale), y: CGFloat(scale))
        let pointRect = CGRect(origin: .zero, size: pointSize)
        ctx.concatenate(cgPage.getDrawingTransform(.mediaBox, rect: pointRect,
                                                   rotate: 0, preserveAspectRatio: true))
        ctx.drawPDFPage(cgPage)

        guard let image = ctx.makeImage() else { throw Failure.unreadable }
        return (image, pointSize)
    }
}
