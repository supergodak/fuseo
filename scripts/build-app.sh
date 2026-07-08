#!/bin/sh
# Fuseo を Release ビルドして /Applications にインストールする日常用スクリプト。
#   ./scripts/build-app.sh
# ビルド番号(CFBundleVersion)は git のコミット数で自動採番する（修正→コミット→ビルドで +1）。
# git 未初期化の間は project.yml の CURRENT_PROJECT_VERSION にフォールバックする。
# 人が読む版(CFBundleShortVersionString=MARKETING_VERSION)は project.yml で手動更新する。
set -e
cd "$(dirname "$0")/.."

if git rev-parse --git-dir >/dev/null 2>&1; then
  BUILD_NUM=$(git rev-list --count HEAD)
else
  BUILD_NUM=$(grep -m1 'CURRENT_PROJECT_VERSION:' project.yml | sed -E 's/.*"([^"]+)".*/\1/')
  BUILD_NUM=${BUILD_NUM:-1}
fi
VERSION=$(grep -m1 'MARKETING_VERSION:' project.yml | sed -E 's/.*"([^"]+)".*/\1/')
echo "Building Fuseo v$VERSION (build $BUILD_NUM)…"

xcodegen generate
rm -rf build/release/Build/Products 2>/dev/null || true   # 署名済みバンドル増分コピー拒否対策
xcodebuild -scheme Fuseo -configuration Release -allowProvisioningUpdates \
  -destination 'platform=macOS' \
  CURRENT_PROJECT_VERSION="$BUILD_NUM" \
  -derivedDataPath build/release \
  build

APP="build/release/Build/Products/Release/Fuseo.app"
if [ ! -d "$APP" ]; then
  echo "ERROR: build product not found at $APP" >&2
  exit 1
fi

# 配布版と同一の身元（Developer ID）で再署名する。既定ビルドは Apple Development 署名になり、
# 配布版からの差し替え時に署名の身元が変わって TCC（アクセシビリティ等）が失効するため。
# --deep で同梱フレームワークまで再署名（ローカル用途では十分）。
# WP-7: Sparkle 等を同梱した場合も --deep で一括再署名される。
DIST_ID="Developer ID Application: ATI K.K. (8NY87P5TYV)"
codesign --force --deep --options runtime --sign "$DIST_ID" "$APP"
codesign --verify --strict "$APP"
echo "Re-signed with: $DIST_ID"

# 起動中なら終了してから差し替え（権限の身元は署名で安定なので再許可不要）。
osascript -e 'quit app "Fuseo"' 2>/dev/null || true
sleep 1
rm -rf /Applications/Fuseo.app
cp -R "$APP" /Applications/Fuseo.app

# Spotlight/LaunchServices が古い launchable コピーを掴まないよう、作業コピーを消し
# /Applications を正本として登録し直す（同一 bundle id の重複起動を防ぐ）。
rm -rf build/release
LSR="/System/Library/Frameworks/CoreServices.framework/Versions/A/Frameworks/LaunchServices.framework/Versions/A/Support/lsregister"
[ -x "$LSR" ] && "$LSR" -f /Applications/Fuseo.app 2>/dev/null || true

echo "Installed: /Applications/Fuseo.app  (v$VERSION build $BUILD_NUM)"
echo "Launching…"
open /Applications/Fuseo.app
