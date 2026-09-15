import Foundation
import CoreGraphics
import ImageIO
import UniformTypeIdentifiers
import os

// WP-13「作業の保存と再開」のストア（docs/wp13-library-design.md §1）。
//
// 保存先は**端末内のみ**。同期・通信は一切しない。ログにはファイル数と失敗理由だけを出し、
// タイトル・元ファイル名・OCR 文字列などの機微情報は決して出さない。

public enum WorkLibraryError: Error, CustomStringConvertible, Equatable {
    /// 指定 id の作業が無い。
    case notFound(UUID)
    /// ページ画像（またはサムネイル）の書き出しに失敗した。
    case imageWriteFailed(pageIndex: Int)
    /// 保存データが壊れている・想定外のスキーマ。
    case corrupted(String)
    /// ファイル操作の失敗（理由のみ。パスは含めない）。
    case ioFailed(String)

    public var description: String {
        switch self {
        case .notFound:                    return "作業が見つかりません"
        case .imageWriteFailed(let index): return "ページ画像を保存できません（\(index + 1)ページ目）"
        case .corrupted(let reason):       return "保存データが壊れています: \(reason)"
        case .ioFailed(let reason):        return "保存に失敗しました: \(reason)"
        }
    }
}

/// 作業ライブラリ（アプリ内・端末内保存）。
public protocol WorkLibrary {
    /// 一覧（更新日時の降順）。`document.json` 全体は読まない軽量経路。
    func list() throws -> [WorkDocumentSummary]
    /// 復元。`directory` は基準画像 PNG を読むための作業ディレクトリ。
    func load(id: UUID) throws -> (document: WorkDocument, directory: URL)
    /// 保存（原子的）。`pageImages` は**今回新しく書き込む必要のあるページ画像**だけを
    /// `WorkPage.index` をキーに渡す（既に保存済みのページは再書き込みしない）。
    /// サムネイル（長辺 256px・JPEG q0.8）はストア側で生成する。
    func save(_ doc: WorkDocument, pageImages: [Int: CGImage]) throws
    func delete(id: UUID) throws
    func deleteAll() throws
}

/// ファイルシステム実装。1 作業 = `<root>/<uuid>/`（`document.json` + `summary.json`
/// + `pages/NNN.png` + `thumbs/NNN.jpg`）。
///
/// **原子性**: 一時ディレクトリ `<root>/.tmp-<uuid>` に書き切ってから rename（既存があれば置換）。
/// 途中で失敗したら一時ディレクトリを消す。消し損ねても次回 `list()` が掃除する。
public final class FileWorkLibrary: WorkLibrary {

    public let rootURL: URL
    private let fm = FileManager.default
    private static let log = Logger(subsystem: "jp.co.ati-mirai.fuseo", category: "workLibrary")

    /// サムネイルの長辺（px）と JPEG 品質。
    private static let thumbnailLongEdge = 256
    private static let thumbnailQuality = 0.8
    private static let documentFileName = "document.json"
    private static let summaryFileName = "summary.json"
    private static let tempPrefix = ".tmp-"

    public init(rootURL: URL) {
        self.rootURL = rootURL
    }

    // MARK: - WorkLibrary

    public func list() throws -> [WorkDocumentSummary] {
        try ensureRoot()
        cleanUpTemporaries()
        let entries = (try? fm.contentsOfDirectory(at: rootURL,
                                                   includingPropertiesForKeys: [.isDirectoryKey],
                                                   options: [.skipsHiddenFiles])) ?? []
        let decoder = WorkJSON.decoder()
        var summaries: [WorkDocumentSummary] = []
        var skipped = 0
        for dir in entries {
            guard UUID(uuidString: dir.lastPathComponent) != nil else { continue }
            guard let summary = summary(in: dir, decoder: decoder) else { skipped += 1; continue }
            summaries.append(summary)
        }
        if skipped > 0 {
            Self.log.error("list: skipped \(skipped, privacy: .public) unreadable work directories")
        }
        // 同一時刻の並びが揺れないよう id で決定的にタイブレークする。
        return summaries.sorted {
            $0.updatedAt == $1.updatedAt
                ? $0.id.uuidString > $1.id.uuidString
                : $0.updatedAt > $1.updatedAt
        }
    }

    public func load(id: UUID) throws -> (document: WorkDocument, directory: URL) {
        let dir = directory(for: id)
        let docURL = dir.appendingPathComponent(Self.documentFileName)
        guard fm.fileExists(atPath: docURL.path) else { throw WorkLibraryError.notFound(id) }
        let data: Data
        do { data = try Data(contentsOf: docURL) }
        catch { throw WorkLibraryError.ioFailed("document.json を読めません") }
        let doc: WorkDocument
        do { doc = try WorkJSON.decoder().decode(WorkDocument.self, from: data) }
        catch { throw WorkLibraryError.corrupted("document.json の形式が不正です") }
        guard doc.schemaVersion == WorkDocument.currentSchemaVersion else {
            throw WorkLibraryError.corrupted("schemaVersion \(doc.schemaVersion)")
        }
        return (doc, dir)
    }

    public func save(_ doc: WorkDocument, pageImages: [Int: CGImage]) throws {
        try ensureRoot()
        let dest = directory(for: doc.id)
        let tmp = rootURL.appendingPathComponent(Self.tempPrefix + doc.id.uuidString)

        try? fm.removeItem(at: tmp)
        do {
            // 既存の保存内容（前回書いたページ画像）を引き継いでから上書きする。
            if fm.fileExists(atPath: dest.path) {
                try fm.copyItem(at: dest, to: tmp)
            } else {
                try fm.createDirectory(at: tmp, withIntermediateDirectories: true)
            }
        } catch {
            try? fm.removeItem(at: tmp)
            throw WorkLibraryError.ioFailed("作業ディレクトリを準備できません")
        }

        do {
            try writeContents(doc, pageImages: pageImages, into: tmp)
            try replace(dest, with: tmp)
        } catch {
            try? fm.removeItem(at: tmp)     // 途中失敗: 既存の保存内容は無傷のまま残る
            throw error
        }
    }

    public func delete(id: UUID) throws {
        let dir = directory(for: id)
        guard fm.fileExists(atPath: dir.path) else { throw WorkLibraryError.notFound(id) }
        do { try fm.removeItem(at: dir) }
        catch { throw WorkLibraryError.ioFailed("削除できません") }
    }

    public func deleteAll() throws {
        guard fm.fileExists(atPath: rootURL.path) else { return }
        let entries = (try? fm.contentsOfDirectory(at: rootURL,
                                                   includingPropertiesForKeys: nil,
                                                   options: [])) ?? []
        var failures = 0
        for entry in entries where UUID(uuidString: entry.lastPathComponent) != nil
            || entry.lastPathComponent.hasPrefix(Self.tempPrefix) {
            do { try fm.removeItem(at: entry) } catch { failures += 1 }
        }
        if failures > 0 {
            Self.log.error("deleteAll: \(failures, privacy: .public) entries could not be removed")
            throw WorkLibraryError.ioFailed("一部を削除できません")
        }
    }

    // MARK: - ディレクトリ

    /// 作業ディレクトリの絶対 URL（アプリ層がページ画像を読むために使う）。
    public func directory(for id: UUID) -> URL {
        rootURL.appendingPathComponent(id.uuidString, isDirectory: true)
    }

    private func ensureRoot() throws {
        if !fm.fileExists(atPath: rootURL.path) {
            do { try fm.createDirectory(at: rootURL, withIntermediateDirectories: true) }
            catch { throw WorkLibraryError.ioFailed("保存先を作成できません") }
        }
        // iCloud バックアップ対象外（端末内のみ・本人確認書類の原本を含むため）。Mac でも害はない。
        var url = rootURL
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        try? url.setResourceValues(values)
    }

    /// 前回の失敗で残った `.tmp-*` を掃除する。
    private func cleanUpTemporaries() {
        let entries = (try? fm.contentsOfDirectory(at: rootURL,
                                                   includingPropertiesForKeys: nil,
                                                   options: [])) ?? []
        var removed = 0
        for entry in entries where entry.lastPathComponent.hasPrefix(Self.tempPrefix) {
            if (try? fm.removeItem(at: entry)) != nil { removed += 1 }
        }
        if removed > 0 {
            Self.log.notice("list: removed \(removed, privacy: .public) stale temp directories")
        }
    }

    // MARK: - 書き込み

    private func writeContents(_ doc: WorkDocument, pageImages: [Int: CGImage], into dir: URL) throws {
        // 1) ページ画像とサムネイル（渡されたぶんだけ。既存は据え置き）
        for page in doc.pages {
            guard let image = pageImages[page.index] else { continue }
            let imageURL = try resolve(page.imageFile, in: dir)
            let thumbURL = try resolve(page.thumbnailFile, in: dir)
            try createParentDirectory(of: imageURL)
            try createParentDirectory(of: thumbURL)
            try write(image, to: imageURL, as: .png, quality: nil, pageIndex: page.index)
            let thumb = try thumbnail(of: image, pageIndex: page.index)
            try write(thumb, to: thumbURL, as: .jpeg, quality: Self.thumbnailQuality, pageIndex: page.index)
        }

        // 2) 参照されなくなったページ画像を掃除する（ページ削除で残骸が積もらないように）
        prune(dir: dir, keeping: Set(doc.pages.flatMap { [$0.imageFile, $0.thumbnailFile] }))

        // 3) JSON（document.json と一覧用 summary.json）
        let encoder = WorkJSON.encoder()
        do {
            let docData = try encoder.encode(doc)
            try docData.write(to: dir.appendingPathComponent(Self.documentFileName))
            // 一覧はこの1ファイルだけを読む（100件でも document.json 全体を開かない）。
            // サムネイルは**相対パス**で持つ（保存先ごと移動しても壊れない）。
            let record = SummaryRecord(id: doc.id, title: doc.title, updatedAt: doc.updatedAt,
                                       status: doc.status, pageCount: doc.pages.count,
                                       thumbnailFile: doc.pages.first?.thumbnailFile)
            try encoder.encode(record).write(to: dir.appendingPathComponent(Self.summaryFileName))
        } catch {
            throw WorkLibraryError.ioFailed("保存データを書き出せません")
        }
    }

    /// 相対パスを作業ディレクトリ配下へ解決する（`..` や絶対パスによる外部書き込みを禁止）。
    private func resolve(_ relativePath: String, in dir: URL) throws -> URL {
        let components = relativePath.split(separator: "/", omittingEmptySubsequences: false).map(String.init)
        guard !relativePath.isEmpty, !relativePath.hasPrefix("/"),
              !components.contains(where: { $0.isEmpty || $0 == ".." || $0 == "." }) else {
            throw WorkLibraryError.corrupted("不正な相対パスです")
        }
        return components.reduce(dir) { $0.appendingPathComponent($1) }
    }

    private func createParentDirectory(of url: URL) throws {
        let parent = url.deletingLastPathComponent()
        if !fm.fileExists(atPath: parent.path) {
            do { try fm.createDirectory(at: parent, withIntermediateDirectories: true) }
            catch { throw WorkLibraryError.ioFailed("ディレクトリを作成できません") }
        }
    }

    private func write(_ image: CGImage, to url: URL, as type: UTType,
                       quality: Double?, pageIndex: Int) throws {
        guard image.width > 0, image.height > 0 else {
            throw WorkLibraryError.imageWriteFailed(pageIndex: pageIndex)
        }
        guard let dest = CGImageDestinationCreateWithURL(url as CFURL, type.identifier as CFString, 1, nil) else {
            throw WorkLibraryError.imageWriteFailed(pageIndex: pageIndex)
        }
        // 圧縮品質のみ。メタデータ辞書は渡さない（原本の EXIF/GPS を引き継がない）。
        let properties: [CFString: Any]? = quality.map { [kCGImageDestinationLossyCompressionQuality: $0] }
        CGImageDestinationAddImage(dest, image, properties as CFDictionary?)
        guard CGImageDestinationFinalize(dest) else {
            throw WorkLibraryError.imageWriteFailed(pageIndex: pageIndex)
        }
    }

    /// 長辺 256px のサムネイル（元が小さければ拡大しない）。
    private func thumbnail(of image: CGImage, pageIndex: Int) throws -> CGImage {
        let long = max(image.width, image.height)
        guard long > Self.thumbnailLongEdge else { return image }
        let scale = Double(Self.thumbnailLongEdge) / Double(long)
        let w = max(1, Int((Double(image.width) * scale).rounded()))
        let h = max(1, Int((Double(image.height) * scale).rounded()))
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: space,
                                  bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else {
            throw WorkLibraryError.imageWriteFailed(pageIndex: pageIndex)
        }
        ctx.interpolationQuality = .high
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        guard let out = ctx.makeImage() else {
            throw WorkLibraryError.imageWriteFailed(pageIndex: pageIndex)
        }
        return out
    }

    /// `pages/` `thumbs/` 配下で、いまのページ構成から参照されていないファイルを消す。
    private func prune(dir: URL, keeping keep: Set<String>) {
        for folder in ["pages", "thumbs"] {
            let sub = dir.appendingPathComponent(folder)
            let files = (try? fm.contentsOfDirectory(at: sub, includingPropertiesForKeys: nil, options: [])) ?? []
            for file in files where !keep.contains("\(folder)/\(file.lastPathComponent)") {
                try? fm.removeItem(at: file)
            }
        }
    }

    /// 一時ディレクトリ → 本番ディレクトリの置換（同一ボリューム内の rename）。
    private func replace(_ dest: URL, with tmp: URL) throws {
        do {
            if fm.fileExists(atPath: dest.path) {
                _ = try fm.replaceItemAt(dest, withItemAt: tmp)
            } else {
                try fm.moveItem(at: tmp, to: dest)
            }
        } catch {
            throw WorkLibraryError.ioFailed("保存を確定できません")
        }
    }

    // MARK: - 一覧の読み取り

    /// `summary.json`（軽量）優先。無い・壊れている場合だけ `document.json` から作り直す。
    private func summary(in dir: URL, decoder: JSONDecoder) -> WorkDocumentSummary? {
        let summaryURL = dir.appendingPathComponent(Self.summaryFileName)
        if let data = try? Data(contentsOf: summaryURL),
           let record = try? decoder.decode(SummaryRecord.self, from: data) {
            return record.summary(in: dir)
        }
        guard let data = try? Data(contentsOf: dir.appendingPathComponent(Self.documentFileName)),
              let doc = try? decoder.decode(WorkDocument.self, from: data) else { return nil }
        return WorkDocumentSummary(
            id: doc.id, title: doc.title, updatedAt: doc.updatedAt, status: doc.status,
            pageCount: doc.pages.count,
            thumbnailURL: doc.pages.first.map { dir.appendingPathComponent($0.thumbnailFile) })
    }

    /// `summary.json` の中身（サムネイルは相対パスで持ち、読み出し時に絶対 URL へ組み立てる）。
    private struct SummaryRecord: Codable {
        var id: UUID
        var title: String
        var updatedAt: Date
        var status: WorkDocument.Status
        var pageCount: Int
        var thumbnailFile: String?

        func summary(in dir: URL) -> WorkDocumentSummary {
            WorkDocumentSummary(id: id, title: title, updatedAt: updatedAt, status: status,
                                pageCount: pageCount,
                                thumbnailURL: thumbnailFile.map { dir.appendingPathComponent($0) })
        }
    }
}
