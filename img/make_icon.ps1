# Derives the square NuGet package icon (img/icon.png, 128x128) from the banner logo (img/logo.png)
# by cropping the mark and scaling it down. The banner itself is too wide and too large for a package
# icon, which NuGet caps at 1 MB and renders in a small square.
#
# Run: pwsh img/make_icon.ps1

Add-Type -AssemblyName System.Drawing

$source = Join-Path $PSScriptRoot 'logo.png'
$out = Join-Path $PSScriptRoot 'icon.png'
if (-not (Test-Path $source)) { throw "missing $source" }

$logo = [System.Drawing.Image]::FromFile($source)
try {
    # The mark sits in the upper middle of the banner. These fractions of the source frame the mark
    # with a little dark margin; adjust them if the artwork is ever recomposed.
    $cropX = [int]($logo.Width * 0.314)
    $cropY = [int]($logo.Height * 0.086)
    $cropSize = [int]($logo.Height * 0.58)
    $crop = New-Object System.Drawing.Rectangle($cropX, $cropY, $cropSize, $cropSize)

    $size = 128
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.DrawImage($logo, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)), $crop, [System.Drawing.GraphicsUnit]::Pixel)
    }
    finally { $g.Dispose() }

    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
finally { $logo.Dispose() }

Write-Host "wrote $out ($([math]::Round((Get-Item $out).Length / 1KB)) KB)"
