# Builds the BIMCamel plugin (Release) once PER NAVISWORKS YEAR against that year's API, generates
# wizard assets, then compiles the unified installer with Inno Setup.
#
# Why per-year: a Navisworks plug-in must be compiled against the API of the release it runs in
# (2024 = Autodesk.Navisworks.Api v21, 2025 = v22, 2026 = v23). A single DLL built against one year
# still LOADS in another year (it shows in the Plugin Manager) but its ribbon tab silently fails to
# register, because Navisworks reflects over the [RibbonLayout]/[RibbonTab]/[Command] attributes and
# those attribute types don't resolve across a major API version. So we build one DLL per year into
# installer\staging\<year>\ and the installer ships each into its own bundle subfolder.
#
# The per-year Navisworks API reference assemblies are restored from NuGet automatically (see
# BIMCamel.csproj), so this PC needs NO Navisworks installed to build all three years. To build a
# year against a local Navisworks install instead — Autodesk's genuine assemblies, for maximal
# license cleanliness — pass -p:NavisworksDir to the dotnet build below.
#
# Output (installer\output\):
#   BIMCamel_Setup.exe   – one per-user installer (no admin / no UAC); installs into the running
#                          user's AppData ApplicationPlugins folder, and also detects/uninstalls a
#                          prior version (including a leftover machine-wide install from older builds).
#
# Prerequisites: .NET SDK (build) + Inno Setup 6/7 (free) — https://jrsoftware.org/isdl.php

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj    = Join-Path $root '..\BIMCamel\BIMCamel.csproj'
$staging = Join-Path $root 'staging'

# Navisworks release years to build. The matching per-year API version is restored from NuGet by the
# .csproj (derived from NavisworksYear), so no Navisworks install is required on this PC.
$years = @('2024', '2025', '2026')

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

foreach ($year in $years) {
    Write-Host ("Building plugin for Navisworks {0} (API restored from NuGet)..." -f $year) -ForegroundColor Cyan
    # --no-incremental forces a clean recompile each pass so switching the per-year API version can't
    # be skipped by up-to-date checks (the source files are identical across years).
    dotnet build $proj -c Release -nologo --no-incremental -p:NavisworksYear=$year
    if ($LASTEXITCODE -ne 0) { throw ("Plugin build failed for Navisworks {0}." -f $year) }

    $outDll = Join-Path $root '..\BIMCamel\bin\x64\Release\net48\BIMCamel.dll'
    if (-not (Test-Path $outDll)) { $outDll = Join-Path $root '..\BIMCamel\bin\Release\net48\BIMCamel.dll' }
    if (-not (Test-Path $outDll)) { throw ("Built DLL not found for Navisworks {0} (looked in bin\Release\net48)." -f $year) }

    $dest = Join-Path $staging $year
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item $outDll (Join-Path $dest 'BIMCamel.dll') -Force
}
Write-Host ("Built per-year plug-ins for: {0}" -f ($years -join ', ')) -ForegroundColor Green

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
