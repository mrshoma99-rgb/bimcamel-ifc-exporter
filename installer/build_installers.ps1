# Builds the BIMCamel plugin (Release), generates wizard assets, then compiles the unified
# installer with Inno Setup.
#
# Output (installer\output\):
#   BIMCamel_Setup.exe   – one installer; the user picks admin (all users) or no-admin (per user)
#                          at startup, and the installer also detects/uninstalls a prior version.
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

# Locate the Inno Setup compiler (ISCC.exe)
$iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    foreach ($p in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $p) { $iscc = $p; break }
    }
}
if (-not $iscc) { throw 'ISCC.exe (Inno Setup 6) not found. Install from https://jrsoftware.org/isdl.php' }

$iss = Join-Path $root 'BIMCamel.iss'
Write-Host 'Compiling installer...' -ForegroundColor Cyan
& $iscc $iss
if ($LASTEXITCODE -ne 0) { throw 'Installer compile failed.' }

Write-Host "Done. Installer is in: $(Join-Path $root 'output')" -ForegroundColor Green
