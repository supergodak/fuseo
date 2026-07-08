import SwiftUI
import AppKit
import MaskingCore

/// Fuseo のエントリポイント。単一ウィンドウの書類マスクツール（wp5 §1）。
///
/// 起動フック（wp5 §8）:
/// - `--uitest`             … UIテスト実行の目印（副作用の抑制。本アプリは常駐副作用を持たない）
/// - `--uitest-open-settings` … 設定画面を専用ウィンドウで開く（AppDelegate が提示）
/// - `--uitest-fixture <path>` … 指定画像を起動時に自動ロードして Review まで進める
///
/// 通常起動は `WindowGroup` の主ウィンドウを使う。ただし XCUITest 下では SwiftUI の
/// `WindowGroup` が窓を生成しない既知の制約があるため、UIテスト時のみ AppDelegate が
/// `RootView` を明示的な `NSWindow` に載せて提示する（Tameo の設定ウィンドウ提示と同型）。
@main
struct FuseoApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @State private var appState: AppState
    @State private var settings: SettingsStore

    /// テストホストとして起動されたか（副作用の抑制判定に使う）。
    static var isRunningTests: Bool {
        ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] != nil
            || CommandLine.arguments.contains("--uitest")
    }

    init() {
        let settings = SettingsStore()
        // 解析サービス（プリセットは Bundle.module から読む）。構築失敗は致命的（プリセット同梱ミス）。
        let analysis: Analyzing
        do {
            analysis = try AnalysisService(tuning: settings.pipelineTuning)
        } catch {
            fatalError("解析サービスの初期化に失敗: \(error)")
        }
        let state = AppState(analysis: analysis, settings: settings)

        // UIテスト時のみ、AppDelegate が提示するビュー（環境注入済み）を準備する。
        if Self.isRunningTests {
            if CommandLine.arguments.contains("--uitest-open-settings") {
                AppDelegate.settingsContent = AnyView(SettingsView().environment(settings))
            } else {
                AppDelegate.mainContent = AnyView(
                    RootView().environment(state).environment(settings)
                )
                // フィクスチャ自動ロードは AppDelegate 提示ウィンドウでも走らせる。
                if let i = CommandLine.arguments.firstIndex(of: "--uitest-fixture"),
                   i + 1 < CommandLine.arguments.count {
                    AppDelegate.fixtureURL = URL(fileURLWithPath: CommandLine.arguments[i + 1])
                    AppDelegate.appState = state
                }
            }
        }

        _settings = State(initialValue: settings)
        _appState = State(initialValue: state)
    }

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(appState)
                .environment(settings)
                .frame(minWidth: 1000, minHeight: 680)
                .task { await bootstrapFixtureIfNeeded() }
        }
        .windowResizability(.contentMinSize)

        Settings {
            SettingsView()
                .environment(settings)
        }
    }

    /// `--uitest-fixture <path>` が渡されていれば、その画像を1枚ロードして Review まで進める
    /// （通常の WindowGroup 経路。AppDelegate 経路でも別途ロードする）。
    private func bootstrapFixtureIfNeeded() async {
        guard appState.stage == .empty else { return }
        let args = CommandLine.arguments
        guard let i = args.firstIndex(of: "--uitest-fixture"), i + 1 < args.count else { return }
        await appState.processFiles([URL(fileURLWithPath: args[i + 1])])
    }
}

/// アプリデリゲート。通常起動では何もしない。UIテスト時のみ、設定画面または主画面を
/// 明示的な `NSWindow` に載せて提示する（XCUITest で AX 露出を保証するため）。
final class AppDelegate: NSObject, NSApplicationDelegate {
    static var settingsContent: AnyView?
    static var mainContent: AnyView?
    static var fixtureURL: URL?
    static var appState: AppState?   // UIテスト専用の強参照（フィクスチャ自動ロード用）
    private var uiTestWindow: NSWindow?

    func applicationDidFinishLaunching(_ notification: Notification) {
        if let content = Self.settingsContent {
            present(content, title: "Fuseo Settings", size: NSSize(width: 460, height: 360))
            return
        }
        if let content = Self.mainContent {
            present(content, title: "Fuseo", size: NSSize(width: 1000, height: 680))
            // フィクスチャの自動ロード（AppDelegate 提示ウィンドウ経路）。
            if let url = Self.fixtureURL, let state = Self.appState {
                Task { await state.processFiles([url]) }
            }
        }
    }

    private func present(_ content: AnyView, title: String, size: NSSize) {
        NSApp.setActivationPolicy(.regular)
        let hosting = NSHostingController(rootView: content)
        let window = NSWindow(contentViewController: hosting)
        window.title = title
        window.setContentSize(size)
        window.styleMask = [.titled, .closable, .miniaturizable, .resizable]
        window.center()
        uiTestWindow = window
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        // XCUITest 中に他アプリへフォーカスを奪われても、再活性化のたびに窓を key に戻す
        // （ツールバー等の hittable を安定させるフレーク対策）。
        NotificationCenter.default.addObserver(
            forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main) { [weak window] _ in
            window?.makeKeyAndOrderFront(nil)
        }
    }
}
