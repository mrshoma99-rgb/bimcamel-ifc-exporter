# Builds the BIMCamel plugin (Release) and compiles BOTH installers with Inno Setup.
# Output EXEs land in installer\output\:
#   BIMCamel_Setup.exe          – admin, machine-wide (%ProgramData%\Autodesk\ApplicationPlugins)
#   BIMCamel_Setup_NoAdmin.exe  – per-user, no admin   (%AppData%\Autodesk\ApplicationPlugins)
#
# Prerequisite: Inno Setup 6 (free) — https://jrsoftware.org/isdl.php

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root '..\BIMCamel\BIMCamel.csproj'

Write-Host 'Building plugin (Release)...' -ForegroundColor Cyan
dotnet build $proj -c Release -nologo
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }

# Locate the Inno Setup compiler (ISCC.exe)
$iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    foreach ($p in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $p) { $iscc = $p; break }
    }
}
if (-not $iscc) { throw 'ISCC.exe (Inno Setup 6) not found. Install from https://jrsoftware.org/isdl.php' }

$iss = Join-Path $root 'BIMCamel.iss'
Write-Host 'Compiling admin installer...' -ForegroundColor Cyan
& $iscc $iss
if ($LASTEXITCODE -ne 0) { throw 'Admin installer compile failed.' }

Write-Host 'Compiling no-admin installer...' -ForegroundColor Cyan
& $iscc /DNOADMIN $iss
if ($LASTEXITCODE -ne 0) { throw 'No-admin installer compile failed.' }

Write-Host "Done. Installers are in: $(Join-Path $root 'output')" -ForegroundColor Green
