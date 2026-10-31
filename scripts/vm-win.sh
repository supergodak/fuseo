#!/bin/bash
# Windows 版（windows/）のビルド・テストを Parallels の Windows 11 VM で実行する。
#   ./scripts/vm-win.sh {sync|build|test|publish|e2e}
#   ./scripts/vm-win.sh e2e [--no-publish] [--filter <dotnet test の --filter>]
#     画面操作の E2E（windows/tests/Fuseo.E2E・publish 済みの Fuseo.exe を操作する）。ssh のセッションには画面が無いので、
#     テストの実行だけ prlctl exec --current-user（ログオン中のユーザーのデスクトップ）で行う。VM はロックしていないこと。
#     入出力は VM ローカルの C:\fuseo-e2e だけ（Mac とつながったデスクトップ等は使わない）。結果（e2e.trx・console.txt・
#     失敗時の screenshots/）は Mac の $FUSEO_E2E_OUT（既定 ${TMPDIR}/fuseo-e2e・リポジトリの外）に回収する。
#     --no-publish: publish を省き、前回 C:\fuseo-e2e\app に置いた exe をそのまま使う（ソースの同期と E2E のビルドはする）。
#     FUSEO_E2E_ARCH=x64 で x64 版（ARM の VM ではエミュレーション）を試せる。通常の test には E2E を混ぜない（sln に入れていない）。
# 同期は tar over ssh（Windows に rsync が無いため）。プリセット JSON（Sources/MaskingCore/Resources/Presets）も送る。
# fixtures-private/・FuseoiOS/ は送らない（実物書類と非公開コード）。
set -euo pipefail
cd "$(dirname "$0")/.."

VM="${FUSEO_WIN_VM:-takeyahideyuki@10.211.55.3}"
REMOTE="${FUSEO_WIN_REMOTE:-C:\fuseo-win}"  # 並行作業（worktree）ごとに別フォルダにする
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

PRL_VM="${FUSEO_WIN_PRL_VM:-Windows 11 Prod}"
E2E_ROOT='C:\fuseo-e2e'

e2e() {
  local publish=1 filter=""
  while [ $# -gt 0 ]; do
    case "$1" in
      --no-publish) publish=0 ;;
      --filter) filter="$2"; shift ;;
      *) echo "e2e: 知らない引数 $1" >&2; exit 2 ;;
    esac
    shift
  done
  local arch="${FUSEO_E2E_ARCH:-arm64}"
  local out="${FUSEO_E2E_OUT:-${TMPDIR:-/tmp}/fuseo-e2e}"
  case "$out" in "$PWD"|"$PWD"/*) echo "FUSEO_E2E_OUT はリポジトリの外にする: $out" >&2; exit 2 ;; esac
  prlctl status "$PRL_VM" | grep -q running || prlctl resume "$PRL_VM"
  sync_tree
  if [ "$publish" = 1 ]; then
    run "powershell -NoProfile -ExecutionPolicy Bypass -File scripts\\publish.ps1 -Rids win-$arch"
    "${SSH[@]}" "powershell -NoProfile -Command \"New-Item -ItemType Directory -Force '$E2E_ROOT\\app' | Out-Null; Copy-Item -Force '$REMOTE\\windows\\publish\\Fuseo-win-*-$arch\\Fuseo.exe' '$E2E_ROOT\\app\\Fuseo.exe'\""
  fi
  run "dotnet build tests\\Fuseo.E2E\\Fuseo.E2E.csproj -c Release -nologo -v q"
  echo "==> e2e（ログオン中のデスクトップで実行・その間 VM の画面に触らない）"
  # 上限: テスト 1 件 3 分（テスト側の見張り）・6 分（--blame-hang）・全体 FUSEO_E2E_TIMEOUT_MIN 分（run-e2e.ps1）。
  # ここでも全体 +5 分で打ち切る（VM 側の PowerShell ごと止まった場合の保険）。
  local limit="${FUSEO_E2E_TIMEOUT_MIN:-30}"
  local code=0
  prlctl exec "$PRL_VM" --current-user powershell -NoProfile -ExecutionPolicy Bypass \
    -File "$REMOTE\\windows\\scripts\\run-e2e.ps1" -Exe "$E2E_ROOT\\app\\Fuseo.exe" -TimeoutMinutes "$limit" \
    ${filter:+-Filter "$filter"} >/dev/null &
  local pid=$! deadline=$(( $(date +%s) + (limit + 5) * 60 ))
  while kill -0 "$pid" 2>/dev/null; do
    if [ "$(date +%s)" -ge "$deadline" ]; then
      echo "==> 上限 $((limit + 5)) 分を超えたので VM 側のテストを止める" >&2
      # E2E の testhost と、E2E 用に置いた exe から起動した Fuseo だけを止める（利用者が試している Fuseo には触れない）
      "${SSH[@]}" "powershell -NoProfile -Command \"Get-Process testhost -ErrorAction SilentlyContinue | Where-Object { \$_.Path -like '*Fuseo.E2E*' -or \$_.Path -like '*dotnet*' } | Stop-Process -Force; Get-Process Fuseo -ErrorAction SilentlyContinue | Where-Object { \$_.Path -eq '$E2E_ROOT\\app\\Fuseo.exe' } | Stop-Process -Force\"" >/dev/null 2>&1 || true
      kill "$pid" 2>/dev/null || true
      break
    fi
    sleep 5
  done
  wait "$pid" || code=$?
  rm -rf "$out/results"; mkdir -p "$out"
  "${SSH[@]}" "tar -c -f - -C $E2E_ROOT results" | tar -x -f - -C "$out"
  echo "==> 結果: $out/results"
  grep -E "^\s*(失敗|Failed|スキップ|Skipped) " "$out/results/console.txt" | head -n 80 || true
  tail -n 6 "$out/results/console.txt" || true
  cat "$out/results/exitcode.txt" 2>/dev/null || true
  return $code
}

case "${1:-test}" in
  sync)    sync_tree ;;
  build)   sync_tree; run "dotnet build Fuseo.Windows.sln -c Debug -nologo -v q" ;;
  test)    sync_tree; run "dotnet test Fuseo.Windows.sln -c Debug -nologo -v q" ;;
  publish) sync_tree; run "powershell -NoProfile -ExecutionPolicy Bypass -File scripts\\publish.ps1" ;;
  e2e)     shift; e2e "$@" ;;
  *) echo "usage: $0 {sync|build|test|publish|e2e}" >&2; exit 2 ;;
esac
