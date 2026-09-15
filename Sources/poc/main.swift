import Foundation
import MaskingCore

// WP-0/WP-2 測定・回帰ハーネス CLI（MaskingPipeline 駆動）。
//   swift run poc <画像...> [--out <出力ディレクトリ>] [--dump] [--flat]
//   --flat: PDF由来の平面ページとして解析（AnalysisOptions.flatPage＝正立判定なし・ページ内の書類だけ切り抜き）
// 各画像を analyze し、種別判定・マスク候補・処理時間を stdout（markdown）へ、
// 候補つきオーバレイPNGを出力ディレクトリへ書く。検出番号は部分マスク表示（実物書類のため）。

var args = Array(CommandLine.arguments.dropFirst())
var outDir = URL(fileURLWithPath: "fixtures-private/out")
if let i = args.firstIndex(of: "--out"), i + 1 < args.count {
    outDir = URL(fileURLWithPath: args[i + 1])
    args.removeSubrange(i...(i + 1))
}
var dumpOCR = false
var flat = false
if let i = args.firstIndex(of: "--flat") { flat = true; args.remove(at: i) }
if let i = args.firstIndex(of: "--dump") {
    dumpOCR = true
    args.remove(at: i)
}
guard !args.isEmpty else {
    print("usage: poc <image...> [--out dir] [--dump]")
    exit(2)
}
try? FileManager.default.createDirectory(at: outDir, withIntermediateDirectories: true)

let pipeline: MaskingPipeline
do {
    pipeline = try MaskingPipeline()
} catch {
    print("パイプライン初期化に失敗: \(error)")
    exit(1)
}

print("# 検出パイプライン測定結果（MaskingPipeline）\n")

var summary: [(name: String, rectified: Bool, type: String, candidates: Int)] = []

for path in args {
    let url = URL(fileURLWithPath: path)
    let stem = url.deletingPathExtension().lastPathComponent
    print("## \(url.lastPathComponent)\n")
    do {
        let t0 = Date()
        let page = try pipeline.analyze(url: url, options: flat ? .flatPage : .default)
        let elapsed = Date().timeIntervalSince(t0)

        let size = page.page.pixelSize
        let aspect = max(size.width, size.height) / min(size.width, size.height)
        print("- 書類検出: \(page.page.rectified ? "OK" : "失敗（全面フォールバック）") (conf=\(page.page.quadConfidence.map { String(format: "%.2f", $0) } ?? "-"))")
        print("- 基準画像: \(Int(size.width))×\(Int(size.height)) px, 長短比=\(String(format: "%.3f", aspect)) (ID-1基準1.586)")
        print("- OCR: \(page.ocr.count) 観測")
        let runnerUp = page.classification.ranking.dropFirst().first
        print("- 種別判定: **\(page.preset.displayName)** (score=\(page.classification.score)"
              + (runnerUp.map { ", 次点=\($0.type.rawValue):\($0.score)" } ?? "") + ")")
        for warning in page.preset.warnings {
            print("- ⚠️ \(warning)")
        }
        if page.candidates.isEmpty {
            print("- マスク候補: なし")
        } else {
            for c in page.candidates {
                let src: String
                switch c.source {
                case .fixedRegion: src = "固定領域"
                case .detector(let d): src = d.rawValue
                }
                print("- 候補[\(c.isOn ? "ON " : "off")] \(c.label) 〈\(src)〉 box=\(String(format: "(%.2f,%.2f %.2f×%.2f)", c.box.x, c.box.y, c.box.w, c.box.h))")
            }
        }
        print("- 処理時間: \(String(format: "%.2f", elapsed)) 秒")

        if dumpOCR {
            let lines = page.ocr
                .sorted { ($0.box.y + $0.box.h / 2) > ($1.box.y + $1.box.h / 2) }
                .map { item in
                    String(format: "y=%.3f x=%.3f-%.3f conf=%.2f up=%@  %@",
                           item.box.y + item.box.h / 2, item.box.x, item.box.x + item.box.w,
                           item.confidence, item.upright ? "Y" : "N", item.text)
                }
            let dest = outDir.appendingPathComponent("\(stem)-ocr.txt")
            try lines.joined(separator: "\n").write(to: dest, atomically: true, encoding: .utf8)
            print("- OCRダンプ: \(dest.path)")
        }

        if let overlay = Overlay.render(base: page.page.cgImage, ocr: page.ocr, candidates: page.candidates) {
            let dest = outDir.appendingPathComponent("\(stem)-overlay.png")
            try Overlay.writePNG(overlay, to: dest)
            print("- オーバレイ: \(dest.path)")
        }
        print("")
        summary.append((url.lastPathComponent, page.page.rectified, page.preset.displayName, page.candidates.count))
    } catch {
        print("- エラー: \(error)\n")
        summary.append((url.lastPathComponent, false, "エラー", 0))
    }
}

print("## サマリ\n")
for s in summary {
    print("- \(s.name): 検出=\(s.rectified ? "OK" : "NG") 種別=\(s.type) 候補=\(s.candidates)")
}
