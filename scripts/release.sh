#!/bin/sh
# Developer ID 配布用: archive → export(developer-id) → 署名DMG → 公証。
#   ./scripts/release.sh                       # ビルド+DMGのみ（公証コマンドを表示）
#   NOTARY_PROFILE=fuseo-notary ./scripts/release.sh   # 公証+staple まで自動
# export(developer-id) はデバッグ用 com.apple.security.get-task-allow を自動除去し、
# hardened runtime + secure timestamp で署名する（`xcodebuild build` 直署名だと公証で弾かれるため）。
# ビルド番号(CFBundleVersion)は git コミット数で自動採番（git 未初期化時は project.yml にフォールバック）。
# WP-7: Sparkle 導入時は appcast 生成・EdDSA 署名（sign_update）をこの後段に追加する。
set -e
cd "$(dirname "$0")/.."

if git rev-parse --git-dir >/dev/null 2>&1; then
  BUILD_NUM=$(git rev-list --count HEAD)
else
  BUILD_NUM=$(grep -m1 'CURRENT_PROJECT_VERSION:' project.yml | sed -E 's/.*"([^"]+)".*/\1/')
  BUILD_NUM=${BUILD_NUM:-1}
fi
VERSION=$(grep -m1 'MARKETING_VERSION:' project.yml | sed -E 's/.*"([^"]+)".*/\1/')
ARCHIVE="build/Fuseo.xcarchive"
EXPORT="build/export"
APP="$EXPORT/Fuseo.app"
DMG="build/Fuseo-$VERSION.dmg"
ID="Developer ID Application: ATI K.K. (8NY87P5TYV)"

echo "==> Archiving Release v$VERSION (build $BUILD_NUM)…"
xcodegen generate
rm -rf "$ARCHIVE" "$EXPORT"
xcodebuild archive -scheme Fuseo -configuration Release -allowProvisioningUpdates \
  -destination 'generic/platform=macOS' -archivePath "$ARCHIVE" \
  CURRENT_PROJECT_VERSION="$BUILD_NUM"

echo "==> Exporting Developer ID app…"
xcodebuild -exportArchive -archivePath "$ARCHIVE" -exportPath "$EXPORT" \
  -exportOptionsPlist scripts/ExportOptions.plist

[ -d "$APP" ] || { echo "ERROR: exported app not found at $APP" >&2; exit 1; }

echo "==> Verifying…"
codesign -dvv "$APP" 2>&1 | grep -E "Authority=|TeamIdentifier=|Timestamp=|Runtime|flags=" || true
if codesign -d --entitlements - "$APP" 2>/dev/null | grep -q "get-task-allow"; then
  echo "    WARNING: get-task-allow still present (notarization will fail)" >&2
else
  echo "    get-task-allow: absent (good)"
fi
codesign --verify --strict --verbose=2 "$APP"

echo "==> Creating DMG (with drag-to-Applications)…"
STAGE="$(mktemp -d)"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
mkdir -p build
rm -f "$DMG"
hdiutil create -volname "Fuseo" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
rm -rf "$STAGE"
codesign --force --timestamp --sign "$ID" "$DMG"
echo "    DMG: $DMG"

if [ -n "$NOTARY_PROFILE" ]; then
  echo "==> Notarizing with keychain profile '$NOTARY_PROFILE'…"
  xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait
  xcrun stapler staple "$DMG"
  echo "==> Stapled. Gatekeeper check:"
  spctl -a -t open --context context:primary-signature -v "$DMG" || true
else
  echo "==> Next (notarize):"
  echo "    xcrun notarytool submit \"$DMG\" --keychain-profile fuseo-notary --wait"
  echo "    xcrun stapler staple \"$DMG\""
fi
echo "==> Done. v$VERSION (build $BUILD_NUM)"
