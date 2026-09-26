# SwAiAssistant portable ZIP packager (ASCII-only for PS 5.1)
# Usage:
#   powershell -ExecutionPolicy Bypass -File build\pack.ps1 -Version 0.1.0
param(
    [string]$Version = "0.2.0",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot "build.ps1")
    if ($LASTEXITCODE -ne 0) { throw "build.ps1 failed." }
}

$artifacts = Join-Path $root "build\artifacts"
$stageName = "SwAiAssistant-$Version"
$stage = Join-Path $artifacts $stageName
$zip = Join-Path $artifacts "$stageName.zip"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
if (Test-Path $zip) { Remove-Item $zip -Force }

# 1) Add-in binaries (build.ps1 already staged SW interop DLLs there)
$addInOut = Join-Path $root "src\SwAiAssistant.AddIn\bin\Release"
Copy-Item (Join-Path $addInOut "*.dll") $stage -Force
Copy-Item (Join-Path $addInOut "*.pdb") $stage -Force -ErrorAction SilentlyContinue

# 2) Install / uninstall scripts + release notes at ZIP root
Copy-Item (Join-Path $root "install\*.bat") $stage -Force
Copy-Item (Join-Path $root "install\release-notes.txt") $stage -Force

# 2a) cmd.exe requires bat files in ANSI(GBK) + CRLF; normalize to avoid regressions from editors
$gbk = [System.Text.Encoding]::GetEncoding(936)
Get-ChildItem $stage -Filter *.bat | ForEach-Object {
    $text = [System.IO.File]::ReadAllText($_.FullName, $gbk)
    $text = $text -replace "`r`n", "`n" -replace "`n", "`r`n"
    [System.IO.File]::WriteAllText($_.FullName, $text, $gbk)
}

# 3) ZIP
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal

Write-Host "[pack] Stage = $stage"
Get-ChildItem $stage | ForEach-Object { Write-Host ("  " + $_.Name) }
Write-Host "[pack] ZIP   = $zip"
Write-Host "[pack] OK"
