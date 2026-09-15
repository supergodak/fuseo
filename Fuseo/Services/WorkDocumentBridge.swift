import CoreGraphics
import Foundation
import ImageIO
import MaskingCore

/// WP-13: 編集状態（`[PageState]`）と保存形式（`WorkDocument`）の橋渡し（docs/wp13-library-design.md §1/§2）。
///
/// **保存されるのは「解析後の基準画像」**（マスク座標の基準）であって、撮影した元写真ではない。
/// そのため復元したページは
/// - 既に台形補正・正立化・切り抜き済み＝**平面**として扱い（再解析は `Quad.fullImage`）、
/// - 回転は「ユーザーが今までに回した量」の記録として持ち直す（`PageState.rotationBaseline`）
/// という規約になる。ここがこのファイルの唯一の非自明な点。
@MainActor
enum WorkDocumentBridge {

    enum BridgeError: Error, CustomStringConvertible {
        case imageUnreadable(pageIndex: Int)
        case emptyDocument

        var description: String {
            switch self {
            case .imageUnreadable(let i): return "ページ画像を読めません（\(i + 1)ページ目）"
            case .emptyDocument:          return "保存データにページがありません"
            }
        }
    }

    // MARK: - 保存（PageState → WorkDocument）

    /// 現在の編集状態から保存用の書類を組み立てる。
    ///
    /// - Note: `analysisOptions` は `detectUpright = false` で保存する。基準画像は既に正立済みで、
    ///   復元後の再解析で向きを再判定させると**かえって回ってしまう**ため（契約 B）。
    static func makeDocument(id: UUID,
                             title: String,
                             createdAt: Date,
                             updatedAt: Date,
                             status: WorkDocument.Status,
                             lastExportedName: String?,
                             pages: [PageState]) -> WorkDocument {
        let workPages = pages.enumerated().map { index, page -> WorkPage in
            let image = page.analyzed.page.cgImage
            return WorkPage(
                index: index,
                sourceName: page.sourceURL.lastPathComponent,
                isFlatSource: page.isFlatSource,
                analysisOptions: AnalysisOptions(detectUpright: false,
                                                 recognizeText: page.analysisOptions.recognizeText,
                                                 documentDetection: .off),   // 基準画像は切り抜き済み
                documentType: page.analyzed.preset.documentType,
                forcedType: page.forcedType,
                // ユーザーが指定した切り抜きの**記録**。基準画像には適用済みなので復元時には使わない。
                manualQuad: page.manualQuad,
                manualRotation: page.manualRotation,
                ocr: page.analyzed.ocr,
                candidates: page.analyzed.candidates,
                manual: page.analyzed.manual,
                pixelWidth: image.width,
                pixelHeight: image.height)
        }
        return WorkDocument(id: id, title: title, createdAt: createdAt, updatedAt: updatedAt,
                            status: status, lastExportedName: lastExportedName, pages: workPages)
    }

    /// 今回書き込む必要のあるページ画像（新規取り込み・再解析で基準画像が変わったページだけ）。
    static func pageImages(for pages: [PageState], dirty: Set<PageState.ID>) -> [Int: CGImage] {
        var out: [Int: CGImage] = [:]
        for (index, page) in pages.enumerated() where dirty.contains(page.id) {
            out[index] = page.analyzed.page.cgImage
        }
        return out
    }

    // MARK: - 復元（WorkDocument → PageState）

    /// 保存された書類を編集状態へ戻す。**Vision は走らせない**（即時）。
    ///
    /// - Parameter presets: プリセット全件。`documentType` で引き、無ければ `.generic` へフォールバックする。
    static func restore(document: WorkDocument,
                        directory: URL,
                        presets: [DocumentPreset]) throws -> [PageState] {
        guard !document.pages.isEmpty else { throw BridgeError.emptyDocument }
        let generic = PresetStore.preset(for: .generic, in: presets)
        return try document.pages.sorted { $0.index < $1.index }.map { wp in
            let imageURL = directory.appendingPathComponent(wp.imageFile)
            guard let image = loadImage(at: imageURL) else {
                throw BridgeError.imageUnreadable(pageIndex: wp.index)
            }
            guard let preset = PresetStore.preset(for: wp.documentType, in: presets) ?? generic else {
                throw BridgeError.imageUnreadable(pageIndex: wp.index)
            }
            // 復元した基準画像は補正済み。ranking は保存していないので空で作り、
            // 種別ピッカーは `AppState.typeOptions(for:)`（プリセット全件）から作る（契約 B）。
            let analyzed = AnalyzedPage(
                page: PageImage(cgImage: image, sourceURL: imageURL,
                                rectified: true, quadConfidence: nil),
                ocr: wp.ocr,
                classification: ClassificationResult(type: wp.documentType, score: 0, ranking: []),
                preset: preset,
                candidates: wp.candidates,
                manual: wp.manual)
            let page = PageState(sourceURL: imageURL, analyzed: analyzed,
                                 isFlatSource: wp.isFlatSource,
                                 analysisOptions: wp.analysisOptions,
                                 isRestored: true,
                                 rotationBaseline: wp.manualRotation)
            page.forcedType = wp.forcedType
            page.manualRotation = wp.manualRotation
            // manualQuad は復元しない: 基準画像には既に適用済みで、再解析で二重に切り抜かれるため。
            // 「切り抜きを調整」はこの基準画像に対して改めて行える（設計 §2）。
            return page
        }
    }

    /// PNG を読む（ImageIO・1枚目のみ）。
    private static func loadImage(at url: URL) -> CGImage? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              CGImageSourceGetCount(source) > 0 else { return nil }
        return CGImageSourceCreateImageAtIndex(source, 0, nil)
    }
}
