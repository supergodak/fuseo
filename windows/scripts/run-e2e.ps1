# 画面操作の E2E テスト（tests\Fuseo.E2E）を、ログオン中のユーザーのデスクトップで実行する。
# ssh のセッションには画面が無いので、Mac から prlctl exec --current-user で呼ぶ（../../scripts/vm-win.sh e2e が使う）:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-e2e.ps1 [-Exe <Fuseo.exe>] [-Filter <dotnet test の --filter>]
# 入出力・データ・結果はすべて VM ローカルの C:\fuseo-e2e の下（Mac とつながった「デスクトップ」「ドキュメント」等は使わない）。
# 結果: C:\fuseo-e2e\results\（e2e.trx・console.txt・exitcode.txt・screenshots\）
param(
    [string]$Exe = 'C:\fuseo-e2e\app\Fuseo.exe',
    [string]$Filter = '',
    [string]$Root = 'C:\fuseo-e2e',
    [int]$TimeoutMinutes = 30
)
$ErrorActionPreference = 'Stop'
# dotnet の出力を UTF-8 で受ける（console.txt を Mac で読めるように）。
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
$windows = Split-Path -Parent $PSScriptRoot
$results = Join-Path $Root 'results'
if (Test-Path $results) { Remove-Item -Recurse -Force $results }
New-Item -ItemType Directory -Force $results | Out-Null
# 古い実行の作業フォルダは消す（入力も出力も合成物だけ）。
if (Test-Path "$Root\runs") { Remove-Item -Recurse -Force "$Root\runs" -ErrorAction SilentlyContinue }

$env:FUSEO_E2E_EXE = $Exe
$env:FUSEO_E2E_ROOT = $Root
Remove-Item Env:FUSEO_DATA_DIR -ErrorAction SilentlyContinue  # テスト側がテストごとに渡す

$testArgs = @('test', "$windows\tests\Fuseo.E2E\Fuseo.E2E.csproj", '-c', 'Release', '--no-build', '-nologo',
          '--results-directory', $results, '--logger', 'trx;LogFileName=e2e.trx', '--logger', 'console;verbosity=normal',
          # 1 テストが 6 分動かなければ testhost ごと落とす（テスト側の見張りは 3 分でアプリを落とす。ここはその外側の保険）
          '--blame-hang', '--blame-hang-timeout', '6m', '--blame-hang-dump-type', 'none')
if ($Filter) { $testArgs += @('--filter', $Filter) }
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$out = Join-Path $results 'console.txt'
$err = Join-Path $results 'console-stderr.txt'
# Start-Process は引数を空白でつなぐだけなので、空白を含むもの（--filter の式など）は引用符で囲む。
$quoted = $testArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
$p = Start-Process -FilePath 'dotnet' -ArgumentList $quoted -NoNewWindow -PassThru `
        -RedirectStandardOutput $out -RedirectStandardError $err
$null = $p.Handle  # これが無いと終了後に ExitCode が取れないことがある（PowerShell 5）
# 全体の上限（全件で通常 6〜8 分）。超えたら dotnet・testhost と、E2E が起動した Fuseo.exe だけを止める。
$limitMinutes = $TimeoutMinutes
if (-not $p.WaitForExit($limitMinutes * 60 * 1000)) {
    "全体の上限 $limitMinutes 分を超えたので止めた" | Out-File -Append -Encoding utf8 $out
    & taskkill /T /F /PID $p.Id | Out-Null
    $code = 124
} else {
    $p.WaitForExit()
    $code = $p.ExitCode
}
Get-Process Fuseo -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe } | Stop-Process -Force -ErrorAction SilentlyContinue
$sw.Stop()
"exit=$code seconds=$([math]::Round($sw.Elapsed.TotalSeconds, 1))" | Out-File -Encoding utf8 (Join-Path $results 'exitcode.txt')
exit $code
