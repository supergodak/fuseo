import Foundation
import MaskingCore
import os

/// WP-13: 作業ライブラリ（保存された「作業中の書類」）の置き場所（docs/wp13-library-design.md §1）。
///
/// - Mac: `~/Library/Application Support/Fuseo/Library`
/// - iOS: `<container>/Library/Application Support/Library`
///
/// どちらも `FileManager.applicationSupportDirectory` 配下で、無ければ作成する。
/// バックアップ対象外にするのは `FileWorkLibrary`（`ensureRoot`）の責務。
///
/// **端末内のみ**・同期なし・通信なし。ログにパスは出さない（コンテナパスに個人名が入り得るため）。
enum WorkLibraryLocation {

    /// Mac だけ `Fuseo/` を挟む（アプリ固有コンテナを持たないため他アプリと混ざらないように）。
    static func rootURL(testing: Bool = false) -> URL {
        let base = (try? FileManager.default.url(for: .applicationSupportDirectory,
                                                 in: .userDomainMask,
                                                 appropriateFor: nil, create: true))
            ?? FileManager.default.temporaryDirectory
        if testing {
            // UI テストは実ユーザーのライブラリを汚さない（起動のたびに作り直す）。
            return FileManager.default.temporaryDirectory
                .appendingPathComponent("fuseo-uitest-library", isDirectory: true)
        }
        #if os(macOS)
        return base.appendingPathComponent("Fuseo/Library", isDirectory: true)
        #else
        return base.appendingPathComponent("Library", isDirectory: true)
        #endif
    }

    /// 既定のライブラリを作る。`testing` の場合は毎回まっさらにしてから返す。
    static func makeLibrary(testing: Bool = false) -> WorkLibrary {
        let root = rootURL(testing: testing)
        if testing { try? FileManager.default.removeItem(at: root) }
        if !FileManager.default.fileExists(atPath: root.path) {
            do {
                try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
            } catch {
                UILog.library.error("保存先を作成できません: \(error.localizedDescription, privacy: .public)")
            }
        }
        return FileWorkLibrary(rootURL: root)
    }
}
