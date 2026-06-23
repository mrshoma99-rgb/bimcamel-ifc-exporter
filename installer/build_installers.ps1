# Builds the BIMCamel plugin (Release) once PER Navisworks version (so each DLL is bound to the
# matching Autodesk.Navisworks.Api), stages the per-version DLLs, generates wizard assets, then
# compiles the unified installer with Inno Setup.
#
# Output (installer\output\):
#   BIMCamel_Setup.exe   - one per-user installer (no admin / no UAC); installs a version-specific
#                          DLL into a 2024\ / 2025\ / 2026\ folder for each Navisworks it found, and
#                          detects/uninstalls a prior version (including a leftover machine-wide one).
#
# Why per-version: a DLL compiled against e.g. Navisworks 2024 references Api 21.x and is rejected by
# 2025/2026 at load (PLUGIN_LOAD_07: invalid referenced Navisworks Api version). Each supported
# version needs its own build/DLL.
#
# Only the Navisworks versions actually installed on this machine are built. To build a version whose
# API lives somewhere non-standard, stage it yourself:
#   dotnet build ..\BIMCamel\BIMCamel.csproj -c Release -p:NavisworksDir="<api dir>" -p:NavisYear=2025
#   copy ..\BIMCamel\bin\Release\net48\BIMCamel.dll stage\2025\BIMCamel.dll
#
# Prerequisite: Inno Setup 6 or 7 (free) - https://jrsoftware.org/isdl.php

$ErrorActionPreference = 'Stop'
$root  = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj  = Join-Path $root '..\BIMCamel\BIMCamel.csproj'
$stage = Join-Path $root 'stage'
$relDll = Join-Path $root '..\BIMCamel\bin\Release\net48\BIMCamel.dll'

# year -> candidate Navisworks API folders (Manage preferred, then Simulate; both ship the same API).
$versions = [ordered]@{
    '2024' = @("$env:ProgramFiles\Autodesk\Navisworks Manage 2024", "$env:ProgramFiles\Autodesk\Navisworks Simulate 2024")
    '2025' = @("$env:ProgramFiles\Autodesk\Navisworks Manage 2025", "$env:ProgramFiles\Autodesk\Navisworks Simulate 2025")
    '2026' = @("$env:ProgramFiles\Autodesk\Navisworks Manage 2026", "$env:ProgramFiles\Autodesk\Navisworks Simulate 2026")
}

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

$built = @()
foreach ($year in $versions.Keys) {
    $apiDir = $versions[$year] |
        Where-Object { Test-Path (Join-Path $_ 'Autodesk.Navisworks.Api.dll') } |
        Select-Object -First 1
    if (-not $apiDir) {
        Write-Host "Navisworks $year not found - skipping." -ForegroundColor DarkYellow
        continue
    }

    Write-Host "Building plugin for Navisworks $year ($apiDir)..." -ForegroundColor Cyan
    # --no-incremental so the changed Navisworks reference is always picked up.
    dotnet build $proj -c Release -nologo --no-incremental -p:NavisworksDir="$apiDir" -p:NavisYear=$year
    if ($LASTEXITCODE -ne 0) { throw "Plugin build failed for $year." }
    if (-not (Test-Path $relDll)) { throw "Expected DLL not found after building $year: $relDll" }

    $dest = Join-Path $stage $year
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Copy-Item $relDll (Join-Path $dest 'BIMCamel.dll') -Force
    $built += $year
}

if ($built.Count -eq 0) {
    throw 'No Navisworks 2024/2025/2026 install found for compile-time references. Install one, or stage a DLL manually (see header).'
}
Write-Host "Staged version-specific DLLs for: $($built -join ', ')" -ForegroundColor Green

Write-Host 'Generating wizard image / icon...' -ForegroundColor Cyan
& (Join-Path $root 'generate_assets.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Asset generation failed.' }

# Locate the Inno Setup compiler (ISCC.exe). Checks PATH, then Program Files for Inno Setup 7/6,
# then the per-user install location (%LOCALAPPDATA%\Programs\Inno Setup *), then the Windows
# uninstall registry as a final fallback.
function Find-Iscc {
    $cmd = (Get-Command iscc -ErrorAction SilentlyContinue).Source
    if ($cmd) { return $cmd }

    $bases = @(
        "${env:ProgramFiles(x86)}\Inno Setup 7",
        "$env:ProgramFiles\Inno Setup 7",
        "${env:ProgramFiles(x86)}\Inno Setup 6",
        "$env:ProgramFiles\Inno Setup 6",
        "$env:LOCALAPPDATA\Programs\Inno Setup 7",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6"
    )
    foreach ($b in $bases) {
        $p = Join-Path $b 'ISCC.exe'
        if (Test-Path $p) { return $p }
    }

    foreach ($hive in 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
                      'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall') {
        Get-ChildItem -Path $hive -ErrorAction SilentlyContinue | ForEach-Object {
            $props = Get-ItemProperty -Path $_.PSPath -ErrorAction SilentlyContinue
            if ($props.DisplayName -like '*Inno Setup*' -and $props.InstallLocation) {
                $p = Join-Path $props.InstallLocation 'ISCC.exe'
                if (Test-Path $p) { return $p }
            }
        } | Select-Object -First 1 | ForEach-Object { return $_ }
    }
    return $null
}

$iscc = Find-Iscc
if (-not $iscc) {
    throw 'ISCC.exe (Inno Setup 6 or 7) not found. Install from https://jrsoftware.org/isdl.php'
}
Write-Host "Using Inno Setup compiler: $iscc" -ForegroundColor DarkGray

$iss = Join-Path $root 'BIMCamel.iss'
Write-Host 'Compiling installer...' -ForegroundColor Cyan
& $iscc $iss
if ($LASTEXITCODE -ne 0) { throw 'Installer compile failed.' }

Write-Host "Done. Installer is in: $(Join-Path $root 'output')" -ForegroundColor Green
