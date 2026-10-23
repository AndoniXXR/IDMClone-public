# Genera app.ico (16, 24, 32, 48, 64, 128 px; 32 bits con transparencia) a partir del icono
# de la extensión. Se ejecuta una vez; build.ps1 incrusta app.ico en IDMClone.exe.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$src  = [System.Drawing.Image]::FromFile((Join-Path $root "browser\extension\icons\icon128.png"))
$sizes = @(16, 24, 32, 48, 64, 128)

$images = @()
foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.Clear([System.Drawing.Color]::Transparent)
  $g.DrawImage($src, 0, 0, $s, $s)
  $g.Dispose()

  # Entrada DIB: BITMAPINFOHEADER + píxeles BGRA de abajo arriba + máscara AND (1 bpp).
  $ms = New-Object System.IO.MemoryStream
  $w = New-Object System.IO.BinaryWriter $ms
  $w.Write([int]40); $w.Write([int]$s); $w.Write([int]($s * 2)); $w.Write([int16]1); $w.Write([int16]32)
  $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
  for ($y = $s - 1; $y -ge 0; $y--) {
    for ($x = 0; $x -lt $s; $x++) {
      $c = $bmp.GetPixel($x, $y)
      $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
    }
  }
  $maskStride = [int]([Math]::Ceiling($s / 32.0) * 4)
  for ($y = $s - 1; $y -ge 0; $y--) {
    $row = New-Object byte[] $maskStride
    for ($x = 0; $x -lt $s; $x++) {
      if ($bmp.GetPixel($x, $y).A -lt 128) {
        $i = [int][Math]::Floor($x / 8)
        $row[$i] = [byte]($row[$i] -bor (0x80 -shr ($x % 8)))
      }
    }
    $w.Write($row)
  }
  $w.Flush()
  $images += ,@($s, $ms.ToArray())
  $bmp.Dispose()
}
$src.Dispose()

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($im in $images) {
  $s = $im[0]; $data = $im[1]
  $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
  $w.Write([byte]0); $w.Write([byte]0); $w.Write([int16]1); $w.Write([int16]32)
  $w.Write([int]$data.Length); $w.Write([int]$offset)
  $offset += $data.Length
}
foreach ($im in $images) { $w.Write($im[1]) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $root "app.ico"), $out.ToArray())
Write-Host ("app.ico creado (" + $out.Length + " bytes)") -ForegroundColor Green
