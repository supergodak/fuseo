// swift-tools-version: 5.9
import PackageDescription

// MaskingCore = パイプライン本体（将来 App/iOS から再利用）。
// poc         = WP-0 の検出精度測定ハーネス（CLI）。後で回帰テストに転用する。
let package = Package(
    name: "MaskingCore",
    platforms: [.macOS(.v14), .iOS(.v17)],   // iOS = WP-8（コア共用。2026-07-07ユーザー決定で優先度上げ）
    products: [
        .library(name: "MaskingCore", targets: ["MaskingCore"]),
        .executable(name: "poc", targets: ["poc"]),
    ],
    targets: [
        .target(
            name: "MaskingCore",
            resources: [.process("Resources")]   // Presets/*.json（Bundle.module から読む）
        ),
        .executableTarget(name: "poc", dependencies: ["MaskingCore"]),
        .testTarget(name: "MaskingCoreTests", dependencies: ["MaskingCore"]),
    ]
)
