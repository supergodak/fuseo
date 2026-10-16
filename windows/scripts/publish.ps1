# Fuseo for Windows の配布物を作る（docs/windows-design.md §6）。Windows（VM）で実行する:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1 [-Rids win-x64,win-arm64]
# 成果物（publish\ の下）:
#   Fuseo-win-<ver>-x64.zip / -arm64.zip  … 中身は Fuseo.exe・README.txt・LICENSE.txt・THIRD-PARTY-NOTICES.txt の 4 つだけ
#   SHA256SUMS.txt                         … zip と exe の SHA-256（LP・窓の杜に載せる値）
# exe は自己完結・単一ファイル。pdfium.dll と WPF のネイティブ DLL も exe の中に入れる（起動時に一時フォルダへ展開される）。
# 署名はしない（費用ゼロ方針）。通信する部品は入れない。
param(
    [string[]]$Rids = @('win-x64', 'win-arm64')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot          # windows\
$repo = Split-Path -Parent $root                  # リポジトリ直下（LICENSE）
$version = ([xml](Get-Content -Raw "$root\Directory.Build.props")).Project.PropertyGroup.Version
if (-not $version) { throw 'Directory.Build.props に Version が無い' }
$out = Join-Path $root 'publish'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null

function WriteText($path, $text) {
    $crlf = ($text -replace "`r`n", "`n") -replace "`n", "`r`n"
    [System.IO.File]::WriteAllText($path, $crlf, (New-Object System.Text.UTF8Encoding $true))
}

$sums = New-Object System.Collections.Generic.List[string]
foreach ($rid in $Rids) {
    $arch = $rid -replace '^win-', ''
    $bin = Join-Path $out "bin-$arch"
    Write-Host "==> publish $rid ($version)"
    dotnet publish "$root\src\Fuseo.App\Fuseo.App.csproj" -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None -p:DebugSymbols=false -p:GenerateDocumentationFile=false -o $bin -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish に失敗: $rid" }

    # exe 1 つだけのはず（pdfium.dll などが横に出ていたら単一ファイル化の失敗）
    $emitted = @(Get-ChildItem $bin -File -Recurse)
    if ($emitted.Count -ne 1 -or $emitted[0].Name -ne 'Fuseo.exe') {
        throw "exe 以外が出力された: $(($emitted | ForEach-Object { $_.Name }) -join ', ')"
    }

    $name = "Fuseo-win-$version-$arch"
    $stage = Join-Path $out $name
    New-Item -ItemType Directory -Force $stage | Out-Null
    Copy-Item "$bin\Fuseo.exe" $stage
    # テキストは UTF-8（BOM 付き）・CRLF にそろえる（古いメモ帳でも文字化けしないように）
    WriteText "$stage\README.txt" ((Get-Content -Raw -Encoding UTF8 "$root\packaging\README.txt") -replace '\{VERSION\}', $version)
    WriteText "$stage\LICENSE.txt" (Get-Content -Raw -Encoding UTF8 "$repo\LICENSE")
    WriteText "$stage\THIRD-PARTY-NOTICES.txt" (Get-Content -Raw -Encoding UTF8 "$root\THIRD-PARTY-NOTICES.txt")
    $expected = @('Fuseo.exe', 'LICENSE.txt', 'README.txt', 'THIRD-PARTY-NOTICES.txt')
    $actual = @(Get-ChildItem $stage -File | ForEach-Object { $_.Name } | Sort-Object)
    if (($actual -join '|') -ne ($expected -join '|')) { throw "zip の中身が想定と違う: $($actual -join ', ')" }

    $zip = Join-Path $out "$name.zip"
    Compress-Archive -Path "$stage\*" -DestinationPath $zip -CompressionLevel Optimal
    foreach ($f in @($zip, "$stage\Fuseo.exe")) {
        $h = (Get-FileHash -Algorithm SHA256 $f).Hash.ToLowerInvariant()
        $rel = if ($f -eq $zip) { "$name.zip" } else { "$name/Fuseo.exe" }
        $sums.Add("$h  $rel")
    }
    $mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    $exeMb = [math]::Round((Get-Item "$stage\Fuseo.exe").Length / 1MB, 1)
    Write-Host "    $name.zip  $mb MB（exe $exeMb MB）"
}
# 改行は CRLF・BOM なし（Windows の certutil / Get-FileHash の値と見比べやすく）
[System.IO.File]::WriteAllText((Join-Path $out 'SHA256SUMS.txt'), (($sums -join "`r`n") + "`r`n"), (New-Object System.Text.UTF8Encoding $false))
Get-Content (Join-Path $out 'SHA256SUMS.txt')
