# Crea el instalador dist\IDMClone-Setup.exe (como el setup de IDM): compila IDMClone, empaqueta
# el programa, los idiomas, la extensión del navegador y la licencia en un .zip incrustado, y compila
# el asistente (setup\*.cs, que reutiliza el código de src\).
#   powershell -ExecutionPolicy Bypass -File build-setup.ps1
# Con -Sign firma (sign.ps1: Authenticode SHA-256 + sello RFC 3161) IDMClone.exe antes de empaquetarlo y el
# instalador al final. El certificado: -PfxPath (+ -PfxPassword o la variable IDMCLONE_PFX_PASSWORD) o -Thumbprint.
#   powershell -ExecutionPolicy Bypass -File build-setup.ps1 -Sign -PfxPath C:\claves\idmclone.pfx
param(
  [switch]$Sign,
  [string]$PfxPath,
  [System.Security.SecureString]$PfxPassword,
  [string]$Thumbprint,
  [string]$TimestampUrl
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $root "build.ps1")

# Parámetros para sign.ps1 (solo los indicados).
$signArgs = @{}
# La contraseña se pide una sola vez (se firma dos veces: el programa y el instalador).
if ($Sign -and $PfxPath -and -not $PfxPassword -and -not $env:IDMCLONE_PFX_PASSWORD) { $PfxPassword = Read-Host -AsSecureString "Contrasena del .pfx" }
if ($PfxPath) { $signArgs.PfxPath = $PfxPath }
if ($PfxPassword) { $signArgs.PfxPassword = $PfxPassword }
if ($Thumbprint) { $signArgs.Thumbprint = $Thumbprint }
if ($TimestampUrl) { $signArgs.TimestampUrl = $TimestampUrl }
if ($Sign) {
  # Firmado dentro del paquete: el IDMClone.exe instalado solo aceptará actualizaciones del mismo editor (ver src\Updater.cs).
  Write-Host "Firmando IDMClone.exe..." -ForegroundColor Cyan
  & (Join-Path $root "sign.ps1") -Files (Join-Path $root "IDMClone.exe") @signArgs
}

$obj = Join-Path $root "obj"
$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force $obj, $dist | Out-Null
$payload = Join-Path $obj "payload.zip"
if (Test-Path $payload) { Remove-Item $payload }

# Archivos que se instalan (nunca las claves privadas de browser\keys ni las pruebas).
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($payload, "Create")
function Add-ToZip($file, $entry) {
  [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file, ($entry -replace '\\','/'), "Optimal") | Out-Null
}
try {
  Add-ToZip (Join-Path $root "IDMClone.exe") "IDMClone.exe"
  Add-ToZip (Join-Path $root "README.md") "README.md"
  Add-ToZip (Join-Path $root "setup\LICENCIA.txt") "LICENCIA.txt"
  Add-ToZip (Join-Path $root "setup\guia.html") "browser\guia.html"
  Get-ChildItem (Join-Path $root "Languages") -Filter *.lng | ForEach-Object { Add-ToZip $_.FullName ("Languages\" + $_.Name) }
  # Ayuda local (botones "Ayuda" y menú Ayuda): help\*.html, help\ayuda.css y help\en\*.html.
  $help = Join-Path $root "help"
  Get-ChildItem $help -Recurse -File | Where-Object { $_.Extension -in ".html", ".css" } | ForEach-Object {
    Add-ToZip $_.FullName ("help\" + $_.FullName.Substring($help.Length + 1))
  }
  $ext = Join-Path $root "browser\extension"
  Get-ChildItem $ext -Recurse -File | ForEach-Object {
    Add-ToZip $_.FullName ("browser\extension\" + $_.FullName.Substring($ext.Length + 1))
  }
} finally { $zip.Dispose() }

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
$out = Join-Path $dist "IDMClone-Setup.exe"
$icon = Join-Path $root "app.ico"

Write-Host "Compilando el instalador..." -ForegroundColor Cyan
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /langversion:5 /main:IDMClone.Setup.SetupProgram `
  "/out:$out" "/win32manifest:$(Join-Path $root 'app.manifest')" "/win32icon:$icon" `
  "/resource:$icon,IDMClone.app.ico" "/resource:$payload,IDMClone.payload.zip" @refs @src
if ($LASTEXITCODE -ne 0) { throw "La compilación del instalador falló." }
if ($Sign) {
  Write-Host "Firmando el instalador..." -ForegroundColor Cyan
  & (Join-Path $root "sign.ps1") -Files $out @signArgs
}
Write-Host ("OK -> " + $out + " (" + [math]::Round((Get-Item $out).Length / 1KB) + " KB)") -ForegroundColor Green
# Para el manifiesto de actualizaciones ("sha256") o las notas de la versión de GitHub ("SHA-256: ...").
Write-Host ("SHA-256: " + (Get-FileHash -LiteralPath $out -Algorithm SHA256).Hash.ToLowerInvariant())
