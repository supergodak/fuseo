#!/bin/bash
# Windows 版（windows/）のビルド・テストを Parallels の Windows 11 VM で実行する。
#   ./scripts/vm-win.sh {sync|build|test|publish}
# 同期は tar over ssh（Windows に rsync が無いため）。プリセット JSON（Sources/MaskingCore/Resources/Presets）も送る。
# fixtures-private/・FuseoiOS/ は送らない（実物書類と非公開コード）。
set -euo pipefail
cd "$(dirname "$0")/.."

VM="${FUSEO_WIN_VM:-takeyahideyuki@10.211.55.3}"
REMOTE='C:\fuseo-win'
SSH=(ssh -o BatchMode=yes -o ConnectTimeout=10 "$VM")

sync_tree() {
  echo "==> sync → $VM:$REMOTE"
  # cmd の `if ... & ...` は後続まで if の中に入るので、PowerShell で作り直す。
  "${SSH[@]}" "powershell -NoProfile -Command \"Remove-Item -Recurse -Force '$REMOTE\\windows' -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force '$REMOTE' | Out-Null\"" >/dev/null
  COPYFILE_DISABLE=1 tar -c --exclude 'bin' --exclude 'obj' --exclude 'publish' --exclude 'TestResults' \
      windows Sources/MaskingCore/Resources/Presets LICENSE \
    | "${SSH[@]}" "tar -x -f - -C $REMOTE"
}

run() { "${SSH[@]}" "cd /d $REMOTE\\windows && $*"; }

case "${1:-test}" in
  sync)    sync_tree ;;
  build)   sync_tree; run "dotnet build Fuseo.Windows.sln -c Debug -nologo -v q" ;;
  test)    sync_tree; run "dotnet test Fuseo.Windows.sln -c Debug -nologo -v q" ;;
  publish) sync_tree; run "powershell -NoProfile -ExecutionPolicy Bypass -File scripts\\publish.ps1" ;;
  *) echo "usage: $0 {sync|build|test|publish}" >&2; exit 2 ;;
esac
