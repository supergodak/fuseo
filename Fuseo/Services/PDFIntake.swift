import CoreGraphics
import Foundation
import ImageIO
import MaskingCore
import PDFKit
import UniformTypeIdentifiers
import os

/// 取り込み1件＝解析にかける画像ファイル1枚（WP-10 §2.4）。
///
/// PDF は「1ページ＝1ファイル」に展開してからパイプラインへ渡す（コアの `analyze` は URL 入力のまま）。
struct IntakeFile: Equatable, Sendable {
    let url: URL
    /// PDF 由来のページか。**true なら平面・全面**として扱い、初回解析にも再解析にも
    /// `Quad.fullImage` を渡して書類検出をバイパスする（§2.3）。
    let isFlatPage: Bool

    init(url: URL, isFlatPage: Bool = false) {
        self.url = url
        self.isFlatPage = isFlatPage
    }
}

/// PDF 入力の取り込み分岐（WP-10 §2.4）。View から切り出して単体テストできる形にしてある。
///
/// 流れ: 分類 → （暗号化なら）パスワード入力 → ページ数の確定と上限判定 → 逐次ラスタライズ →
/// ページごとに一時画像ファイル化。**全ページを同時にメモリへ載せない**（1ページ書き出すたびに解放）。
///
/// パスワードは引数で受け渡すだけで、保存もログ出力もしない（§2.4）。
enum PDFIntake {

    /// ページ画像を一時ファイル化する処理（iOS は `SessionFiles.importCGImage`、Mac は `makeTempPageStore()`）。
    typealias PageWriter = @MainActor (CGImage, String) throws -> URL

    /// パスワード入力 UI。`nil` を返したら「キャンセル＝取り込み中止」。
    /// 第2引数 `retry` は「直前の入力が誤りだった」の意（再入力の文言切替に使う）。
    typealias PasswordProvider = @MainActor (String, Bool) async -> String?

    /// 取り込みの結果。理由の分からない失敗は作らない（`failed` は必ず表示可能な文言を持つ）。
    enum Outcome: Equatable {
        case files([IntakeFile])
        /// 取り込み中止（ユーザーに理由を出す）。
        case failed(String)
        /// パスワード入力のキャンセル（ユーザー操作なのでエラー表示しない）。
        case cancelled
    }

    /// 画像枚数＋PDF総ページ数の合計上限。
    ///
    /// iOS は既存の `PhotosPicker(maxSelectionCount: 10)` に合わせて 10。
    /// Mac は v1.2 の一括処理（フォルダ取込）が青天井のため、コアの `PDFRasterizer.maxPages` と同じ 50。
    /// **判定は PDF を含む取り込みのときだけ行う**（画像だけの一括取込の既存挙動を変えないため）。
    #if os(iOS)
    static let combinedLimit = 10
    #else
    static let combinedLimit = 50
    #endif

    // MARK: - 判定・命名（純関数）

    /// 拡張子から PDF かを判定する。
    static func isPDF(_ url: URL) -> Bool {
        guard let type = UTType(filenameExtension: url.pathExtension.lowercased()) else { return false }
        return type.conforms(to: .pdf)
    }

    /// ページ画像の baseName（`<pdf名>-p03` 相当・ページ番号はゼロ埋め3桁・1始まり）。
    static func pageBaseName(pdfURL: URL, pageNumber: Int) -> String {
        let stem = pdfURL.deletingPathExtension().lastPathComponent
        let cleaned = stem.isEmpty ? "document" : stem
        return String(format: "%@-p%03d", cleaned, pageNumber)
    }

    /// 失敗理由を「何が起きたか分かる」文言にする（§2.4・それぞれ別の文言）。
    /// - Parameter maxPages: `.tooManyPages` の文言に出す上限。**実際に使う rasterizer の値を渡すこと**
    ///   （既定値から作ると、小さい上限で走らせたときに文言の数字が実際と食い違う）。
    static func message(for failure: PDFRasterizer.Failure, fileName: String,
                        maxPages: Int = PDFRasterizer().maxPages) -> String {
        switch failure {
        case .locked:
            return String(localized: "「\(fileName)」はパスワードで保護されています。パスワードを入力してください。")
        case .wrongPassword:
            return String(localized: "「\(fileName)」のパスワードが違います。もう一度入力してください。")
        case .unreadable:
            return String(localized: "「\(fileName)」を読み込めませんでした。PDFが壊れているか、内容の取り出しが許可されていない可能性があります。")
        case .tooManyPages(let count):
            return String(localized: "「\(fileName)」は\(count)ページあります。一度に処理できるのは\(maxPages)ページまでです。")
        }
    }

    /// 合計上限の超過メッセージ（黙って切り捨てず、必ず理由を出す）。
    static func limitMessage(total: Int, limit: Int) -> String {
        String(localized: "一度に取り込めるのは合計\(limit)ページまでです（選んだ画像とPDFのページを合わせて\(total)ページありました）。減らしてからもう一度お試しください。")
    }

    // MARK: - 一時ファイル書き出し（Mac 経路）

    /// Mac 用の最小限の一時ファイル経路。取り込みごとに一時ディレクトリを1つ作り、
    /// ページ画像を**PNG（可逆）**で書き出す。iOS は `SessionFiles.importCGImage` を使う。
    ///
    /// ディレクトリの**寿命は呼び出し側（`AppState`）が管理する**（本人確認書類の像を temp に残さない）。
    /// セッション中は再解析で読み直すため消さず、取り込みの置き換え・アプリ終了で削除する。
    /// ※ Phase 2 メモ: Mac は PNG（可逆）、iOS は `SessionFiles.importCGImage` の JPEG q0.95 で
    ///   形式が揃っていない（既存 API を使う方針を優先。OCR 品質への影響は小さいと判断）。
    static func makeTempPageStore() -> (directory: URL, writer: PageWriter) {
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("fuseo-pdf-\(UUID().uuidString.prefix(8))", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let writer: PageWriter = { image, baseName in
            let url = dir.appendingPathComponent("\(baseName).png")
            guard let dest = CGImageDestinationCreateWithURL(
                url as CFURL, UTType.png.identifier as CFString, 1, nil) else {
                throw PDFRasterizer.Failure.unreadable
            }
            CGImageDestinationAddImage(dest, image, nil)
            guard CGImageDestinationFinalize(dest) else { throw PDFRasterizer.Failure.unreadable }
            return url
        }
        return (dir, writer)
    }

    // MARK: - 取り込み本体

    /// URL 群を解析対象のファイル一覧に展開する。PDF はページ画像へ、画像はそのまま。
    ///
    /// - Parameters:
    ///   - urls: 受理済み（`FileIntake.isAccepted`）の URL 群。順序は保つ。
    ///   - limit: 画像枚数＋PDFページ数の合計上限。`nil` で無制限。**PDF を含むときだけ判定する**。
    ///   - rasterizer: 差し替え可能（テストで `maxPages` を小さくする）。
    ///   - passwordProvider: 暗号化 PDF のパスワード入力 UI。
    ///   - writer: ページ画像の一時ファイル化。
    @MainActor
    static func run(urls: [URL],
                    limit: Int? = combinedLimit,
                    rasterizer: PDFRasterizer = PDFRasterizer(),
                    passwordProvider: PasswordProvider,
                    writer: @escaping PageWriter) async -> Outcome {
        guard !urls.isEmpty else { return .files([]) }

        /// 展開前の予定表（順序保持）。
        enum Entry {
            case image(URL)
            case pdf(url: URL, password: String?, pageCount: Int)
        }

        var entries: [Entry] = []
        var totalPages = 0
        var sawPDF = false

        // 1) 分類＋（暗号化なら）パスワード確定＋ページ数の確定
        for url in urls {
            guard isPDF(url) else {
                entries.append(.image(url))
                totalPages += 1
                continue
            }
            sawPDF = true
            let name = url.lastPathComponent

            var password: String?
            if PDFRasterizer.isLocked(url: url) {
                var retry = false
                while true {
                    guard let entered = await passwordProvider(name, retry) else { return .cancelled }
                    if let doc = PDFDocument(url: url), doc.unlock(withPassword: entered) {
                        password = entered
                        break
                    }
                    retry = true      // .wrongPassword → 理由を出して再入力
                }
            }

            guard let doc = unlockedDocument(url, password: password), doc.pageCount > 0 else {
                return .failed(message(for: .unreadable, fileName: name, maxPages: rasterizer.maxPages))
            }
            let count = doc.pageCount
            if count > rasterizer.maxPages {
                return .failed(message(for: .tooManyPages(count), fileName: name, maxPages: rasterizer.maxPages))
            }
            entries.append(.pdf(url: url, password: password, pageCount: count))
            totalPages += count
        }

        // 2) 合計上限（黙って切り捨てない）。PDF を含む取り込みのみ判定する。
        if sawPDF, let limit, totalPages > limit {
            return .failed(limitMessage(total: totalPages, limit: limit))
        }

        // 3) 逐次ラスタライズ（1ページずつ書き出して解放）
        var files: [IntakeFile] = []
        for entry in entries {
            switch entry {
            case .image(let url):
                files.append(IntakeFile(url: url, isFlatPage: false))
            case .pdf(let url, let password, let pageCount):
                UILog.intake.info("PDF取り込み: \(pageCount, privacy: .public)ページ（\(url.lastPathComponent, privacy: .private)）")
                do {
                    let pages = try await rasterizePages(url, password: password,
                                                         rasterizer: rasterizer, writer: writer)
                    files += pages.map { IntakeFile(url: $0, isFlatPage: true) }
                } catch let failure as PDFRasterizer.Failure {
                    return .failed(message(for: failure, fileName: url.lastPathComponent, maxPages: rasterizer.maxPages))
                } catch {
                    return .failed(message(for: .unreadable, fileName: url.lastPathComponent, maxPages: rasterizer.maxPages))
                }
            }
        }
        return .files(files)
    }

    // MARK: - 内部

    /// 解錠済みの `PDFDocument`（ページ数を数えるためだけに開く）。開けない/解錠できないと nil。
    private static func unlockedDocument(_ url: URL, password: String?) -> PDFDocument? {
        guard let doc = PDFDocument(url: url) else { return nil }
        if doc.isLocked {
            guard let password, doc.unlock(withPassword: password) else { return nil }
        }
        return doc
    }

    /// 1つの PDF を**背景スレッドで**逐次ラスタライズし、各ページの一時ファイル化だけを
    /// MainActor（`writer`）で行う。300dpi の描画でメインスレッドを止めないための分離。
    ///
    /// 背景側はページごとに書き出し完了を待ってから次ページへ進む（全ページ同時保持の禁止・§5）。
    /// 待つのは背景スレッドだけで、MainActor は `await` で空いているためデッドロックしない。
    private static func rasterizePages(_ url: URL, password: String?,
                                       rasterizer: PDFRasterizer,
                                       writer: @escaping PageWriter) async throws -> [URL] {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<[URL], Error>) in
            DispatchQueue.global(qos: .userInitiated).async {
                var written: [URL] = []
                do {
                    try rasterizer.rasterize(url: url, password: password) { page in
                        let baseName = pageBaseName(pdfURL: url, pageNumber: page.index + 1)
                        let image = page.cgImage
                        var result: Result<URL, Error>?
                        let semaphore = DispatchSemaphore(value: 0)
                        DispatchQueue.main.async {
                            MainActor.assumeIsolated {
                                result = Result { try writer(image, baseName) }
                            }
                            semaphore.signal()
                        }
                        semaphore.wait()
                        written.append(try result!.get())
                    }
                    continuation.resume(returning: written)
                } catch {
                    continuation.resume(throwing: error)
                }
            }
        }
    }
}
