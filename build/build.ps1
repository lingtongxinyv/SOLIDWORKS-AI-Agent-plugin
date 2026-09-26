# SwAiAssistant build script (ASCII-only for Windows PowerShell 5.1 compatibility)
# Usage:
#   powershell -ExecutionPolicy Bypass -File build\build.ps1
#   powershell -ExecutionPolicy Bypass -File build\build.ps1 -RunTests
param(
    [string]$Configuration = "Release",
    [switch]$RunTests
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
        if ($found) { return $found }
    }
    $candidates = @(
        "D:\Apps\VS\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    throw "MSBuild not found. Install Visual Studio Build Tools (MSBuild + .NET Framework 4.8 targeting pack)."
}

function Find-SwRedist {
    if ($env:SW_REDIST_DIR -and (Test-Path (Join-Path $env:SW_REDIST_DIR "SolidWorks.Interop.sldworks.dll"))) {
        return $env:SW_REDIST_DIR
    }
    $candidates = @(
        "D:\Program Files\SOLIDWORKS Corp\SOLIDWORKS (2)\api\redist",
        "C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS 2026\api\redist",
        "C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS 2025\api\redist",
        "D:\Program Files\SOLIDWORKS Corp\SOLIDWORKS 2026\api\redist",
        "D:\Program Files\SOLIDWORKS Corp\SOLIDWORKS 2025\api\redist"
    )
    foreach ($c in $candidates) { if (Test-Path (Join-Path $c "SolidWorks.Interop.sldworks.dll")) { return $c } }
    throw "SolidWorks Interop (api\redist) not found. Set SW_REDIST_DIR environment variable."
}

$msbuild = Find-MSBuild
Write-Host "[build] MSBuild = $msbuild"
$redist = Find-SwRedist
Write-Host "[build] SW redist = $redist"

$addInProj = Join-Path $root "src\SwAiAssistant.AddIn\SwAiAssistant.AddIn.csproj"
$testProjs = @(
    (Join-Path $root "tools\Tests\SwAiAssistant.Core.Tests\SwAiAssistant.Core.Tests.csproj"),
    (Join-Path $root "tools\Tests\SwAiAssistant.Ai.Tests\SwAiAssistant.Ai.Tests.csproj"),
    (Join-Path $root "tools\Tests\SwAiAssistant.Planner.Tests\SwAiAssistant.Planner.Tests.csproj")
)

& $msbuild $addInProj /t:Rebuild /restore "/p:Configuration=$Configuration" "/p:Platform=x64" "/p:SwRedistDir=$redist" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "AddIn build failed (exit $LASTEXITCODE)." }

# SwIaTest integration harness (dev tool; interop DLLs copied to its output via its csproj)
$swIaTestProj = Join-Path $root "tools\SwIaTest\SwIaTest.csproj"
& $msbuild $swIaTestProj /t:Rebuild /restore "/p:Configuration=$Configuration" "/p:Platform=x64" "/p:SwRedistDir=$redist" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "SwIaTest build failed (exit $LASTEXITCODE)." }

# SwBench benchmark harness (M5-T20 dev tool, not shipped)
$swBenchProj = Join-Path $root "tools\SwBench\SwBench.csproj"
& $msbuild $swBenchProj /t:Rebuild /restore "/p:Configuration=$Configuration" "/p:Platform=x64" "/p:SwRedistDir=$redist" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "SwBench build failed (exit $LASTEXITCODE)." }

if ($RunTests) {
    foreach ($testProj in $testProjs) {
        & $msbuild $testProj /t:Rebuild /restore "/p:Configuration=$Configuration" "/p:Platform=x64" /v:minimal /nologo
        if ($LASTEXITCODE -ne 0) { throw "Test project build failed (exit $LASTEXITCODE): $testProj" }
        $testName = [IO.Path]::GetFileNameWithoutExtension($testProj)
        $testDll = Join-Path $root "tools\Tests\$testName\bin\x64\$Configuration\$testName.dll"
        if (-not (Test-Path $testDll)) { $testDll = Join-Path $root "tools\Tests\$testName\bin\$Configuration\$testName.dll" }
        & dotnet test $testDll --no-build -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw "Unit tests failed (exit $LASTEXITCODE): $testName" }
    }
}

# Stage SolidWorks interop DLLs next to the add-in (needed at runtime and for RegAsm)
$addInOut = Join-Path $root "src\SwAiAssistant.AddIn\bin\$Configuration"
foreach ($name in @("SolidWorks.Interop.sldworks.dll", "SolidWorks.Interop.swconst.dll", "SolidWorks.Interop.swpublished.dll")) {
    Copy-Item (Join-Path $redist $name) $addInOut -Force
}
Write-Host "[build] Staged SW interop DLLs into $addInOut"
Write-Host "[build] OK"
