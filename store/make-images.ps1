# Imágenes para las tiendas (Edge Add-ons y Chrome Web Store), en dist\store:
#   logo-300.png (logotipo 300x300), logo-128.png, mosaico-440x280.png (mosaico promocional) y
#   captura-2/3 (1280x800) compuestas con capturas reales de IDMClone (tests\bin\IDMCloneTests.exe --screens).
#   powershell -ExecutionPolicy Bypass -File store\make-images.ps1 <carpeta de capturas>
param([Parameter(Mandatory = $true)][string]$Screens)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$out = Join-Path $root "dist\store"
New-Item -ItemType Directory -Force $out | Out-Null

function New-Canvas($w, $h) {
  $b = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($b)
  $g.SmoothingMode = "AntiAlias"; $g.InterpolationMode = "HighQualityBicubic"; $g.PixelOffsetMode = "HighQuality"; $g.TextRenderingHint = "AntiAliasGridFit"
  return @($b, $g)
}

function RoundRect($x, $y, $w, $h, $r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $p.AddArc($x, $y, $r * 2, $r * 2, 180, 90); $p.AddArc($x + $w - $r * 2, $y, $r * 2, $r * 2, 270, 90)
  $p.AddArc($x + $w - $r * 2, $y + $h - $r * 2, $r * 2, $r * 2, 0, 90); $p.AddArc($x, $y + $h - $r * 2, $r * 2, $r * 2, 90, 90)
  $p.CloseFigure(); return $p
}

# El icono de IDMClone (cuadrado azul con la flecha de descarga), dibujado a cualquier tamaño.
function Draw-Logo($g, $x, $y, $s) {
  $rect = New-Object System.Drawing.RectangleF $x, $y, $s, $s
  $path = RoundRect $x $y $s $s ($s * 0.2)
  $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(64, 148, 245)), ([System.Drawing.Color]::FromArgb(22, 98, 214)), 90
  $g.FillPath($br, $path)
  $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
  $cx = $x + $s / 2
  $g.FillRectangle($white, [float]($cx - $s * 0.07), [float]($y + $s * 0.2), [float]($s * 0.14), [float]($s * 0.3))
  $pts = [System.Drawing.PointF[]]@(
    (New-Object System.Drawing.PointF ([float]($cx - $s * 0.23)), ([float]($y + $s * 0.45))),
    (New-Object System.Drawing.PointF ([float]($cx + $s * 0.23)), ([float]($y + $s * 0.45))),
    (New-Object System.Drawing.PointF ([float]$cx), ([float]($y + $s * 0.68))))
  $g.FillPolygon($white, $pts)
  $g.FillPath($white, (RoundRect ($x + $s * 0.22) ($y + $s * 0.74) ($s * 0.56) ($s * 0.09) ($s * 0.03)))
}

function Save($b, $name) { $b.Save((Join-Path $out $name), [System.Drawing.Imaging.ImageFormat]::Png); Write-Host ("  " + $name) }

# Logotipos
foreach ($s in 300, 128) {
  $c = New-Canvas $s $s; $c[1].Clear([System.Drawing.Color]::Transparent)
  Draw-Logo $c[1] ($s * 0.04) ($s * 0.04) ($s * 0.92)
  Save $c[0] ("logo-" + $s + ".png"); $c[1].Dispose(); $c[0].Dispose()
}

function Background($g, $w, $h) {
  $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
  $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(21, 72, 150)), ([System.Drawing.Color]::FromArgb(8, 27, 61)), 35
  $g.FillRectangle($br, $rect)
}

# Mosaico promocional 440x280
$c = New-Canvas 440 280; $g = $c[1]
Background $g 440 280
Draw-Logo $g 32 78 124
$f1 = New-Object System.Drawing.Font "Segoe UI Semibold", 22; $f2 = New-Object System.Drawing.Font "Segoe UI", 12
$g.DrawString("IDMClone", $f1, [System.Drawing.Brushes]::White, 176, 84)
$g.DrawString("Integration", $f1, [System.Drawing.Brushes]::White, 176, 118)
$sub = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 222, 255))
$g.DrawString("Descargas y vídeos del`r`nnavegador en IDMClone", $f2, $sub, 178, 164)
Save $c[0] "mosaico-440x280.png"; $g.Dispose(); $c[0].Dispose()

# Capturas compuestas 1280x800
function Shadowed($g, $img, $x, $y, $scale) {
  $w = [int]($img.Width * $scale); $h = [int]($img.Height * $scale)
  for ($i = 8; $i -ge 1; $i--) {
    $sh = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(10, 0, 0, 0))
    $g.FillRectangle($sh, $x - $i + 6, $y - $i + 8, $w + 2 * $i, $h + 2 * $i)
  }
  $g.DrawImage($img, $x, $y, $w, $h)
}

function Composite($name, $title, $back, $front) {
  $c = New-Canvas 1280 800; $g = $c[1]
  Background $g 1280 800
  $ft = New-Object System.Drawing.Font "Segoe UI Semibold", 26
  $g.DrawString($title, $ft, [System.Drawing.Brushes]::White, 60, 36)
  $a = [System.Drawing.Image]::FromFile((Join-Path $Screens $back))
  $b = [System.Drawing.Image]::FromFile((Join-Path $Screens $front))
  $sa = [Math]::Min(1.0, [Math]::Min(1000 / $a.Width, 560 / $a.Height))
  Shadowed $g $a 60 120 $sa
  $sb = [Math]::Min(1.0, [Math]::Min(620 / $b.Width, 470 / $b.Height))
  Shadowed $g $b (1280 - 60 - [int]($b.Width * $sb)) (800 - 50 - [int]($b.Height * $sb)) $sb
  Save $c[0] $name; $a.Dispose(); $b.Dispose(); $g.Dispose(); $c[0].Dispose()
}

Composite "captura-2-informacion.png" "Las descargas del navegador pasan a IDMClone" "01-principal.png" "08-informacion-archivo.png"
Composite "captura-3-progreso.png" "Varias conexiones a la vez y aviso al terminar" "04-progreso.png" "10-descarga-completada.png"
Write-Host ("Imágenes en " + $out) -ForegroundColor Green
