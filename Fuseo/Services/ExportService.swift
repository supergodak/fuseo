import Foundation
import MaskingCore

/// 焼き込み（burnIn）→ 書き出し（export）を束ねるサービス。
/// **描画・メタデータ処理は自前実装しない**（コアの `MaskRendering`/`Exporting` に委譲・wp5 §3-3）。
struct ExportService {
    let renderer: MaskRendering
    let exporter: Exporting

    init(renderer: MaskRendering = RasterMaskRenderer(), exporter: Exporting = FileExporter()) {
        self.renderer = renderer
        self.exporter = exporter
    }

    /// 確定済みページ群を書き出す。
    /// - PDF: 全ページを1ファイルへ。
    /// - JPEG/PNG: 複数ページは `<名前>-1.jpeg, -2.jpeg …` に分割保存（先頭は URL そのまま）。
    func export(pages: [AnalyzedPage], options: ExportOptions, to url: URL) throws {
        guard !pages.isEmpty else { throw MaskingError.exportFailed("出力するページがありません") }
        let rendered = try pages.map { page -> RenderedPage in
            let masks = page.effectiveMaskRects
            let image = try renderer.burnIn(page: page.page, masks: masks, strokes: page.manual.strokes)
            return RenderedPage(image: image, ocrItems: page.ocr, maskRects: masks)
        }

        switch options.format {
        case .pdf:
            try exporter.export(rendered, options: options, to: url)
        case .jpeg, .png:
            if rendered.count == 1 {
                try exporter.export(rendered, options: options, to: url)
            } else {
                for (index, page) in rendered.enumerated() {
                    let pageURL = ExportNaming.imagePageURL(base: url, pageNumber: index + 1)
                    try exporter.export([page], options: options, to: pageURL)
                }
            }
        }
    }
}

/// 書き出しファイル名の規約（層1テスト対象）。
enum ExportNaming {
    /// 既定保存名 = `<先頭ファイル名（拡張子除去）>-masked.<ext>`（wp5 §3-2）。
    static func defaultFileName(firstSourceName: String, format: ExportOptions.Format) -> String {
        let base = (firstSourceName as NSString).deletingPathExtension
        let name = base.isEmpty ? "document" : base
        return "\(name)-masked.\(format.rawValue)"
    }

    /// JPEG/PNG 複数ページ分割時の各ページ URL（`<名前>-<n>.<ext>`）。
    static func imagePageURL(base: URL, pageNumber: Int) -> URL {
        let ext = base.pathExtension
        let stem = base.deletingPathExtension().lastPathComponent
        let dir = base.deletingLastPathComponent()
        return dir.appendingPathComponent("\(stem)-\(pageNumber).\(ext)")
    }
}
