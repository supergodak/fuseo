import SwiftUI

/// 設定ウィンドウのルート（Tameo型 TabView）。MVP は「一般」1タブのみ（wp5 §4）。
struct SettingsView: View {
    var body: some View {
        TabView {
            GeneralSettingsTab()
                .tabItem { Label("一般", systemImage: "gearshape") }
        }
        .frame(width: 460, height: 360)
    }
}
