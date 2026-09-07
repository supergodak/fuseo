#!/bin/bash
# Xcode のテスト（層1/層2＝xcodebuild・XCUITest）を Parallels の macOS VM で実行する。
#   ./scripts/vm-test.sh sync    # 作業ツリーを VM へ同期するだけ
#   ./scripts/vm-test.sh mac     # 同期 → Mac 版 FuseoTests＋FuseoUITests
#   ./scripts/vm-test.sh ios     # 同期 → iOS 版 FuseoiOSTests＋FuseoiOSUITests（シミュレータ）
#   ./scripts/vm-test.sh all     # 同期 → mac → ios
#   ./scripts/vm-test.sh shell   # VM のリポジトリで対話シェル
#
# 方針（2026-09-07 ユーザー決定）: UI オートメーションはホストではなく VM で回す。
# - 同期は rsync（git archive は不可: FuseoiOS/ と Fuseo.xcodeproj は gitignore 対象で、
#   git archive だと iOS アプリ層が丸ごと欠ける）。fixtures-private/（実物書類）は同期しない。
# - VM には xcodegen が無いので、ホストで `xcodegen generate` してから .xcodeproj ごと送る。
# - VM に署名 identity が無いので、Mac 版はアドホック署名（CODE_SIGN_IDENTITY=-）で走らせる。
#   その際 hardened runtime は必ず切る（ENABLE_HARDENED_RUNTIME=NO）: 有効のままだと
#   XCUITest の Runner がライブラリ検証で「Team ID が違う」と .xctest の読み込みを拒否する
#   （実測 2026-09-07）。hardened runtime は公証用（release.sh）で、テストには不要。
#   iOS シミュレータは署名不要（CODE_SIGNING_ALLOWED=NO）。
# - pipefail 必須（scripts/test.sh と同じ理由: grep で要約しつつ失敗を exit code に反映）。
set -e -o pipefail
cd "$(dirname "$0")/.."

VM="${FUSEO_VM:-takeyaparallels@10.211.55.4}"
REMOTE="${FUSEO_VM_DIR:-polaris_fuseo}"
IOS_DEVICE="${FUSEO_IOS_DEVICE:-iPhone 17}"

SSH=(ssh -o BatchMode=yes -o ConnectTimeout=10 "$VM")

sync_tree() {
  echo "==> xcodegen generate（ホスト）…"
  xcodegen generate >/dev/null
  echo "==> rsync → $VM:$REMOTE/ …"
  rsync -az --delete \
    --exclude .git --exclude .build --exclude build --exclude dist \
    --exclude fixtures-private --exclude site --exclude .playwright-mcp \
    --exclude '*.xcresult' \
    ./ "$VM:$REMOTE/"
}

# 共通: 要約表示。テスト本体の失敗は pipefail で伝播する。
summarize() {
  grep -E "Test Case.*(passed|failed)|Executed [0-9]+ tests|error:|\*\* TEST (SUCCEEDED|FAILED)"
}

run_mac() {
  echo "==> VM: Mac 版テスト（アドホック署名）…"
  "${SSH[@]}" "cd $REMOTE && rm -rf build/test/Build/Products 2>/dev/null; \
    xcodebuild test -scheme Fuseo -destination 'platform=macOS' \
      -derivedDataPath build/test \
      CODE_SIGN_IDENTITY=- CODE_SIGN_STYLE=Manual DEVELOPMENT_TEAM= \
      PROVISIONING_PROFILE_SPECIFIER= ENABLE_HARDENED_RUNTIME=NO 2>&1" | summarize
}

run_ios() {
  echo "==> VM: iOS 版テスト（シミュレータ: $IOS_DEVICE）…"
  "${SSH[@]}" "cd $REMOTE && \
    xcodebuild test -scheme FuseoiOS \
      -destination 'platform=iOS Simulator,name=$IOS_DEVICE' \
      -derivedDataPath build/test-ios \
      CODE_SIGNING_ALLOWED=NO 2>&1" | summarize
}

case "$1" in
  sync)  sync_tree ;;
  mac)   sync_tree; run_mac ;;
  ios)   sync_tree; run_ios ;;
  all)   sync_tree; run_mac; run_ios ;;
  shell) exec ssh -t "$VM" "cd $REMOTE && exec \$SHELL -l" ;;
  *)     echo "usage: $0 {sync|mac|ios|all|shell}" >&2; exit 2 ;;
esac
