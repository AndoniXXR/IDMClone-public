# Compila IDMClone y el banco de pruebas, y ejecuta los tests.
#   powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1 [filtro]
# Los tests usan una carpeta de datos temporal (IDMCLONE_HOME): no tocan tu lista real.
$ErrorActionPreference = "Stop"
$tests = Split-Path -Parent $MyInvocation.MyCommand.Path
$root  = Split-Path -Parent $tests

& (Join-Path $root "build.ps1")

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
$fw = Split-Path -Parent $csc
$refs = @(
  "System.dll","System.Core.dll","System.Windows.Forms.dll","System.Drawing.dll",
  "System.Web.Extensions.dll","System.Xml.dll","System.Security.dll",
  "System.IO.Compression.dll","System.IO.Compression.FileSystem.dll"
) | ForEach-Object { "/reference:`"" + (Join-Path $fw $_) + "`"" }

$src  = Get-ChildItem (Join-Path $root "src") -Filter *.cs | ForEach-Object { "`"" + $_.FullName + "`"" }
$src += Get-ChildItem (Join-Path $root "setup") -Filter *.cs | ForEach-Object { "`"" + $_.FullName + "`"" }
$src += Get-ChildItem $tests -Filter *.cs | ForEach-Object { "`"" + $_.FullName + "`"" }
$bin  = Join-Path $tests "bin"
New-Item -ItemType Directory -Force $bin | Out-Null
$out  = Join-Path $bin "IDMCloneTests.exe"

Write-Host "Compilando tests..." -ForegroundColor Cyan
$icon = Join-Path $root "app.ico"
& $csc /nologo /target:exe /platform:anycpu /optimize+ /langversion:5 /main:IDMClone.Tests.TestMain "/out:$out" "/resource:$icon,IDMClone.app.ico" @refs @src
if ($LASTEXITCODE -ne 0) { throw "La compilación de los tests falló." }

& $out --exe (Join-Path $root "IDMClone.exe") @args
$code = $LASTEXITCODE

# Tests de la extensión del navegador (lógica y flujo con las APIs simuladas), si hay Node.js.
if ($args.Count -eq 0) {
  $node = Get-Command node -ErrorAction SilentlyContinue
  if ($node) {
    Write-Host ""
    Write-Host "Tests de la extensión (Node.js)..." -ForegroundColor Cyan
    & node --test (Join-Path $root "browser\tests\extension.test.js")
    if ($LASTEXITCODE -ne 0) { $code = 1 }
  } else { Write-Host "Node.js no está instalado: se omiten los tests de la extensión." -ForegroundColor Yellow }
}
exit $code
