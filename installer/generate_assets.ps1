# Generates the installer's wizard images and icon from the camel logo PNGs.
# Outputs are written to .\assets\ and consumed by BIMCamel.iss.
#
#   wizard_image.bmp   164x314, 24-bit BMP   (left panel on Welcome/Finished pages)
#   wizard_small.bmp   55x55,   24-bit BMP   (top-right corner on interior pages)
#   bimcamel.ico                              (Setup EXE icon + uninstaller icon)
#
# Source is the largest camel PNG we have (appstore_submission\store_logo_80.png, 80x80).
# We upscale with HighQualityBicubic and pair it with the BIMCamel wordmark + website URL.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$logoCandidates = @(
    (Join-Path $root '..\appstore_submission\store_logo_80.png'),
    (Join-Path $root '..\BIMCamel\Resources\camel_32.png')
)
$logoPath = $null
foreach ($p in $logoCandidates) { if (Test-Path $p) { $logoPath = (Resolve-Path $p).Path; break } }
if (-not $logoPath) { throw 'No camel logo PNG found.' }

$assets = Join-Path $root 'assets'
if (-not (Test-Path $assets)) { New-Item -ItemType Directory -Path $assets | Out-Null }

Add-Type -AssemblyName System.Drawing

function New-WizardBitmap {
    param(
        [string]$LogoPath,
        [string]$OutPath,
        [int]$Width,
        [int]$Height,
        [bool]$WithText
    )

    $bmp = New-Object System.Drawing.Bitmap $Width, $Height, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint  = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    $rect = New-Object System.Drawing.Rectangle 0, 0, $Width, $Height
    $top    = [System.Drawing.Color]::FromArgb(255, 248, 250, 252)
    $bottom = [System.Drawing.Color]::FromArgb(255, 214, 222, 232)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $top, $bottom, ([System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillRectangle($bg, $rect)

    # Subtle accent stripe at the bottom in BIMCamel-ish slate.
    $accent = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 31, 45, 61))
    $stripeH = [Math]::Max(3, [int]($Height * 0.012))
    $g.FillRectangle($accent, 0, $Height - $stripeH, $Width, $stripeH)

    $logo = [System.Drawing.Image]::FromFile($LogoPath)
    try {
        if ($WithText) {
            $logoBox = [int]([Math]::Min($Width * 0.7, $Height * 0.42))
            $lx = [int](($Width  - $logoBox) / 2)
            $ly = [int]($Height * 0.16)
        } else {
            $logoBox = [int]([Math]::Min($Width, $Height) * 0.85)
            $lx = [int](($Width  - $logoBox) / 2)
            $ly = [int](($Height - $logoBox) / 2)
        }
        $g.DrawImage($logo, $lx, $ly, $logoBox, $logoBox)

        if ($WithText) {
            $textBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 31, 45, 61))
            $urlBrush  = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 70, 90, 115))
            $sf = New-Object System.Drawing.StringFormat
            $sf.Alignment = [System.Drawing.StringAlignment]::Center

            $titleFont = New-Object System.Drawing.Font 'Segoe UI', 18, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
            $tagFont   = New-Object System.Drawing.Font 'Segoe UI', 11, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
            $urlFont   = New-Object System.Drawing.Font 'Segoe UI', 11, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)

            $titleY = $ly + $logoBox + 8
            $tagY   = $titleY + 26
            $urlY   = $Height - $stripeH - 22

            $g.DrawString('BIMCamel',         $titleFont, $textBrush, [single]($Width/2), [single]$titleY, $sf)
            $g.DrawString('IFC Exporter',     $tagFont,   $urlBrush,  [single]($Width/2), [single]$tagY,   $sf)
            $g.DrawString('www.bimcamel.com', $urlFont,   $urlBrush,  [single]($Width/2), [single]$urlY,   $sf)
        }
    } finally {
        $logo.Dispose()
    }

    $g.Dispose()
    $bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $bmp.Dispose()
}

function New-AppIcon {
    param(
        [string]$LogoPath,
        [string]$OutPath,
        [int]$Size = 48
    )
    $src = [System.Drawing.Image]::FromFile($LogoPath)
    try {
        $bmp = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($src, 0, 0, $Size, $Size)
        $g.Dispose()

        $h = $bmp.GetHicon()
        try {
            $icon = [System.Drawing.Icon]::FromHandle($h)
            $fs = [System.IO.File]::Create($OutPath)
            try { $icon.Save($fs) } finally { $fs.Close() }
        } finally {
            $bmp.Dispose()
        }
    } finally {
        $src.Dispose()
    }
}

$wizardBig   = Join-Path $assets 'wizard_image.bmp'
$wizardSmall = Join-Path $assets 'wizard_small.bmp'
$icoOut      = Join-Path $assets 'bimcamel.ico'

Write-Host "Generating wizard assets from: $logoPath" -ForegroundColor Cyan
New-WizardBitmap -LogoPath $logoPath -OutPath $wizardBig   -Width 164 -Height 314 -WithText $true
New-WizardBitmap -LogoPath $logoPath -OutPath $wizardSmall -Width 55  -Height 55  -WithText $false
New-AppIcon      -LogoPath $logoPath -OutPath $icoOut      -Size 48

Write-Host "  $wizardBig"
Write-Host "  $wizardSmall"
Write-Host "  $icoOut"
