import Foundation
import Sparkle

/// Sparkle 自動更新の窓口（Tameo型・Mac版のみ）。
/// **注意: このファイルは Fuseo/ 直下に置く**——Fuseo/Services はiOSターゲットと共有しており、
/// Sparkle は macOS 専用のため Services に入れるとiOSビルドが壊れる。
///
/// - 起動時に updater を開始（初回にユーザーへ「自動確認を有効にするか」を尋ねる標準挙動）
/// - 更新チェックで送られるのはバージョン確認のリクエストのみ。**書類・画像・個人情報は一切送信しない**
///   （PRIVACY.md に明記済み）。フィードURL・公開鍵は Info.plist で設定。
@MainActor
final class UpdaterController {
    private let controller: SPUStandardUpdaterController

    init() {
        controller = SPUStandardUpdaterController(
            startingUpdater: true,
            updaterDelegate: nil,
            userDriverDelegate: nil
        )
    }

    /// 手動チェック（メニュー「アップデートを確認…」から）。多重呼び出しは Sparkle 側で無視される。
    func checkForUpdates() {
        controller.updater.checkForUpdates()
    }
}
