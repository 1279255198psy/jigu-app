# FILE: jigu-app/tools/make-icon.ps1
# Generates the ancient-style application icon:
#   rice-paper background + cinnabar seal frame + the character "Ji" (U+7A3D)
# Uses only GDI+ (System.Drawing). Outputs multi-size PNGs and one .ico file.
# Usage: powershell -NoProfile -File tools\make-icon.ps1
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root   = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root "resources"
$tmpDir = Join-Path $PSScriptRoot "icon-tmp"
New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null

$paper    = [System.Drawing.Color]::FromArgb(255, 246, 241, 228)
$paper2   = [System.Drawing.Color]::FromArgb(255, 238, 230, 211)
$cinnabar = [System.Drawing.Color]::FromArgb(255, 192, 57, 43)
$ink      = [System.Drawing.Color]::FromArgb(255, 43, 42, 38)

function New-IconPng([int]$size, [string]$path) {
  $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

  $s = [double]$size / 256.0

  # 1) rounded rice-paper square with faint fibre texture
  $g.Clear([System.Drawing.Color]::Transparent)
  $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
  $rad = [int](34 * $s)
  if ($rad -lt 2) { $rad = 2 }
  $bgPath = New-Object System.Drawing.Drawing2D.GraphicsPath
  $bgPath.AddArc(0, 0, $rad, $rad, 180, 90)
  $bgPath.AddArc($size - $rad, 0, $rad, $rad, 270, 90)
  $bgPath.AddArc($size - $rad, $size - $rad, $rad, $rad, 0, 90)
  $bgPath.AddArc(0, $size - $rad, $rad, $rad, 90, 90)
  $bgPath.CloseFigure()
  $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    $rect, $paper, $paper2, [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
  $g.FillPath($bgBrush, $bgPath)
  $bgBrush.Dispose()

  $fibPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(16, 150, 130, 95), [single](1 * $s))
  $step = [int](7 * $s); if ($step -lt 2) { $step = 2 }
  for ($y = 6; $y -lt $size; $y += $step) { $g.DrawLine($fibPen, 0, $y, $size, $y) }
  $fibPen.Dispose()

  # 2) cinnabar seal frame
  $inset = [int](26 * $s)
  $bw = [single](10 * $s); if ($bw -lt 1) { $bw = 1 }
  $sr = New-Object System.Drawing.Rectangle($inset, $inset, ($size - (2 * $inset)), ($size - (2 * $inset)))
  $rad2 = [int](18 * $s); if ($rad2 -lt 2) { $rad2 = 2 }
  $sealPath = New-Object System.Drawing.Drawing2D.GraphicsPath
  $sealPath.AddArc($sr.X, $sr.Y, $rad2, $rad2, 180, 90)
  $sealPath.AddArc($sr.Right - $rad2, $sr.Y, $rad2, $rad2, 270, 90)
  $sealPath.AddArc($sr.Right - $rad2, $sr.Bottom - $rad2, $rad2, $rad2, 0, 90)
  $sealPath.AddArc($sr.X, $sr.Bottom - $rad2, $rad2, $rad2, 90, 90)
  $sealPath.CloseFigure()
  $sealPen = New-Object System.Drawing.Pen($cinnabar, $bw)
  $sealPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
  $g.DrawPath($sealPen, $sealPath)
  $sealPen.Dispose()

  # 3) weathering speckles on the seal (looks hand-stamped)
  $rnd = New-Object System.Random(20240924)
  $wear = New-Object System.Drawing.SolidBrush($paper)
  $count = [int](90 * $s * $s) + 30
  for ($i = 0; $i -lt $count; $i++) {
    $x = $rnd.Next($inset, $sr.Right)
    $y = $rnd.Next($inset, $sr.Bottom)
    $d = $rnd.Next(1, [int](3 * $s) + 2)
    $g.FillEllipse($wear, $x, $y, $d, $d)
  }
  $wear.Dispose()

  # 4) the character, centred
  $fontSize = [single](128 * $s)
  if ($fontSize -lt 6) { $fontSize = 6 }
  $font = $null
  foreach ($name in @("KaiTi", "STKaiti", "SimSun", "NSimSun", "Microsoft YaHei")) {
    try {
      $f = New-Object System.Drawing.Font($name, $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
      if ($f.Name -eq $name) { $font = $f; break }
      $f.Dispose()
    } catch { }
  }
  if ($null -eq $font) {
    $font = New-Object System.Drawing.Font("SimSun", $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
  }

  $text = [string][char]0x7A3D
  $sf = New-Object System.Drawing.StringFormat
  $sf.Alignment = [System.Drawing.StringAlignment]::Center
  $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
  $rectF = New-Object System.Drawing.RectangleF(0, 0, $size, $size)
  $inkBrush = New-Object System.Drawing.SolidBrush($cinnabar)
  $g.DrawString($text, $font, $inkBrush, $rectF, $sf)
  $inkBrush.Dispose()
  $font.Dispose()
  $sf.Dispose()

  # 5) a small ink dot at the bottom (a brush touch)
  if ($size -ge 32) {
    $dotBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(120, 43, 42, 38))
    $g.FillEllipse($dotBrush, [single]($size / 2 - 3 * $s), [single]($sr.Bottom - 30 * $s), [single](6 * $s), [single](6 * $s))
    $dotBrush.Dispose()
  }

  $g.Dispose()
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$entries = New-Object System.Collections.ArrayList
foreach ($sz in $sizes) {
  $p = Join-Path $tmpDir ("icon-" + $sz + ".png")
  New-IconPng -size $sz -path $p
  [void]$entries.Add(@($sz, $p))
  Write-Host ("  png {0,3}px  {1} bytes" -f $sz, (Get-Item $p).Length)
}

$icoPath = Join-Path $outDir "app.ico"
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
  $sz = [int]$e[0]
  $bytes = [System.IO.File]::ReadAllBytes($e[1])
  $w = [byte]$(if ($sz -ge 256) { 0 } else { $sz })
  $bw.Write($w); $bw.Write($w); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32)
  $bw.Write([uint32]$bytes.Length); $bw.Write([uint32]$offset)
  $offset += $bytes.Length
}
foreach ($e in $entries) { $bw.Write([System.IO.File]::ReadAllBytes($e[1])) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
$bw.Close(); $ms.Close()

Copy-Item (Join-Path $tmpDir "icon-256.png") (Join-Path $outDir "app.png") -Force
Write-Host ""
Write-Host ("icon: " + $icoPath + "  " + (Get-Item $icoPath).Length + " bytes, " + $entries.Count + " sizes")
