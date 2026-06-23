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
# Each targeted year needs that Navisworks (Manage or Simulate) installed on THIS build PC so its
# reference assemblies are present. Years whose Navisworks isn't found are skipped (with a warning);
# the installer still compiles, and VerifyInstall flags the gap if a user later selects that year.
#
# Output (installer\output\):
#   BIMCamel_Setup.exe   – one per-user installer (no admin / no UAC); installs into the running
#                          user's AppData ApplicationPlugins folder, and also detects/uninstalls a
#                          prior version (including a leftover machine-wide install from older builds).
#
# Prerequisite: Inno Setup 6 (free) — https://jrsoftware.org/isdl.php

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj    = Join-Path $root '..\BIMCamel\BIMCamel.csproj'
$staging = Join-Path $root 'staging'

# Navisworks release year -> series number (Nw21/22/23). Mirrors the .iss detection.
$targets = @(
    @{ Year = '2024'; Ver = '21' },
    @{ Year = '2025'; Ver = '22' },
    @{ Year = '2026'; Ver = '23' }
)

# Return the install dir of a Navisworks of the given series (Manage or Simulate), or $null. Tries
# the registry InstallDir first (handles non-default drives), then the default Program Files path.
# Only counts if Autodesk.Navisworks.Api.dll is actually present (we reference it at compile time).
function Get-NavisworksDir([string] $ver, [string] $year) {
    foreach ($flavour in 'Manage', 'Simulate') {
        foreach ($key in @(
                "HKLM:\SOFTWARE\Autodesk\Navisworks $flavour x64\$ver.0",
                "HKLM:\SOFTWARE\Autodesk\Navisworks $flavour\$ver.0")) {
            try {
                $dir = (Get-ItemProperty -Path $key -Name InstallDir -ErrorAction Stop).InstallDir
                if ($dir) {
                    $dir = $dir.TrimEnd('\')
                    if (Test-Path (Join-Path $dir 'Autodesk.Navisworks.Api.dll')) { return $dir }
                }
            } catch { }
        }
        $pf = $env:ProgramW6432; if (-not $pf) { $pf = $env:ProgramFiles }
        $dir = Join-Path $pf "Autodesk\Navisworks $flavour $year"
        if (Test-Path (Join-Path $dir 'Autodesk.Navisworks.Api.dll')) { return $dir }
    }
    return $null
}

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

$built = @()
foreach ($t in $targets) {
    $navDir = Get-NavisworksDir $t.Ver $t.Year
    if (-not $navDir) {
        Write-Warning ("Navisworks {0} (v{1}) not found on this PC - it will NOT be included in the installer." -f $t.Year, $t.Ver)
        continue
    }

    Write-Host ("Building plugin for Navisworks {0} (against {1})..." -f $t.Year, $navDir) -ForegroundColor Cyan
    dotnet build $proj -c Release -nologo -p:NavisworksDir=$navDir
    if ($LASTEXITCODE -ne 0) { throw ("Plugin build failed for Navisworks {0}." -f $t.Year) }

    $outDll = Join-Path $root '..\BIMCamel\bin\x64\Release\net48\BIMCamel.dll'
    if (-not (Test-Path $outDll)) { $outDll = Join-Path $root '..\BIMCamel\bin\Release\net48\BIMCamel.dll' }
    if (-not (Test-Path $outDll)) { throw ("Built DLL not found for Navisworks {0} (looked in bin\Release\net48)." -f $t.Year) }

    $dest = Join-Path $staging $t.Year
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item $outDll (Join-Path $dest 'BIMCamel.dll') -Force
    $built += $t.Year
}

if ($built.Count -eq 0) {
    throw 'No supported Navisworks (2024-2026) found on this PC, so no plug-in could be built. Install at least one and re-run.'
}
Write-Host ("Built per-year plug-ins for: {0}" -f ($built -join ', ')) -ForegroundColor Green

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
