# Builds the BIMCamel plugin (Release), generates wizard assets, then compiles the unified
# installer with Inno Setup.
#
# Output (installer\output\):
#   BIMCamel_Setup.exe   – one per-user installer (no admin / no UAC); installs into the running
#                          user's AppData ApplicationPlugins folder, and also detects/uninstalls a
#                          prior version (including a leftover machine-wide install from older builds).
#
# Prerequisite: Inno Setup 6 (free) — https://jrsoftware.org/isdl.php

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root '..\BIMCamel\BIMCamel.csproj'

Write-Host 'Building plugin (Release)...' -ForegroundColor Cyan
dotnet build $proj -c Release -nologo
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }

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
