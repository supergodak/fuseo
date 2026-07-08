#!/bin/bash
# Fuseo のテストを実行する。
#   ./scripts/test.sh            # 層0(SPMコア)＋層1(ユニット/統合)＋層2(確認画面/設定のUI駆動) を全部
#   ./scripts/test.sh spm        # 層0 のみ（swift test・速い）
#   ./scripts/test.sh unit       # 層1 のみ（速い・CI向き）
#   ./scripts/test.sh ui         # 層2 のみ
# pipefail 必須: grep で要約表示しつつ、テスト本体の失敗を exit code に必ず反映する
# （Tameo版の `| grep … || true` はテスト赤でも exit 0 になる罠があった）。
set -e -o pipefail
cd "$(dirname "$0")/.."

run_spm() {
  echo "==> SPM (swift test)…"
  swift test 2>&1 | grep -E "Test Suite.*(passed|failed)|Executed [0-9]+ tests|error:"
}

run_xcode() {
  ONLY="$1"
  xcodegen generate >/dev/null
  # 署名済みバンドルへの増分コピーがmacOSに拒否される問題への対処: 成果物を毎回作り直す
  rm -rf build/test/Build/Products 2>/dev/null || true
  xcodebuild test \
    -scheme Fuseo \
    -destination 'platform=macOS' \
    -derivedDataPath build/test \
    $ONLY \
    | grep -E "Test Case.*(passed|failed)|Executed [0-9]+ tests|\*\* TEST (SUCCEEDED|FAILED)"
}

case "$1" in
  spm)  run_spm ;;
  unit) run_xcode "-only-testing:FuseoTests" ;;
  ui)   run_xcode "-only-testing:FuseoUITests" ;;
  "")   run_spm; run_xcode "" ;;
  *)    echo "usage: $0 [spm|unit|ui]" >&2; exit 2 ;;
esac
