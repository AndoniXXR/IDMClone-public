# Instala el host de mensajería nativa de IDMClone para todos los navegadores compatibles.
# No requiere administrador: todo va en HKCU. Se puede ejecutar de nuevo sin problema
# (por ejemplo, después de mover la carpeta de IDMClone).
$ErrorActionPreference = "Stop"

$browserDir = Split-Path -Parent $MyInvocation.MyCommand.Path          # ...\IDMClone\browser
$root       = Split-Path -Parent $browserDir                           # ...\IDMClone
$exe        = Join-Path $root "IDMClone.exe"
$hostName   = "com.idmclone.host"
$chromeId   = "jolfcgooaockjhnalagfilpljkloallc"                       # ID fijo (por la 'key' del manifiesto)
$firefoxId  = "idmclone@idmclone.local"                                # browser_specific_settings.gecko.id

if (-not (Test-Path $exe)) { throw "No se encontró IDMClone.exe. Compílalo antes con build.ps1." }

# UTF-8 sin BOM: con 'ascii' una ruta con ñ o tildes quedaba con '?' y el navegador no encontraba el host.
function Write-Json($path, $obj) {
  $json = $obj | ConvertTo-Json -Depth 5
  [System.IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding $false))
}

# 1) Manifiestos del host con la ruta real del .exe.
$chromiumManifest = Join-Path $browserDir "$hostName.json"
Write-Json $chromiumManifest ([ordered]@{
  name            = $hostName
  description     = "IDMClone native messaging host"
  path            = $exe
  type            = "stdio"
  allowed_origins = @("chrome-extension://$chromeId/")
})
$firefoxManifest = Join-Path $browserDir "$hostName.firefox.json"
Write-Json $firefoxManifest ([ordered]@{
  name               = $hostName
  description        = "IDMClone native messaging host"
  path               = $exe
  type               = "stdio"
  allowed_extensions = @($firefoxId)
})
Write-Host "Manifiestos: $chromiumManifest" -ForegroundColor Green
Write-Host "             $firefoxManifest" -ForegroundColor Green

# 2) Registro por navegador. Varios derivados de Chromium leen además la clave de Chrome,
#    pero se registran también en la suya propia por si no lo hacen.
$chromium = @(
  "HKCU:\Software\Google\Chrome\NativeMessagingHosts",
  "HKCU:\Software\Chromium\NativeMessagingHosts",
  "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts",
  "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts",
  "HKCU:\Software\Opera Software\NativeMessagingHosts",
  "HKCU:\Software\Vivaldi\NativeMessagingHosts",
  "HKCU:\Software\Yandex\YandexBrowser\NativeMessagingHosts",
  "HKCU:\Software\CocCoc\Browser\NativeMessagingHosts"
)
foreach ($base in $chromium) {
  $key = "$base\$hostName"
  New-Item -Path $key -Force | Out-Null
  Set-ItemProperty -Path $key -Name "(default)" -Value $chromiumManifest
  Write-Host "Registrado: $key" -ForegroundColor DarkGray
}
$ffKey = "HKCU:\Software\Mozilla\NativeMessagingHosts\$hostName"
New-Item -Path $ffKey -Force | Out-Null
Set-ItemProperty -Path $ffKey -Name "(default)" -Value $firefoxManifest
Write-Host "Registrado: $ffKey" -ForegroundColor DarkGray

Write-Host ""
Write-Host "Host nativo instalado." -ForegroundColor Cyan
Write-Host ""
Write-Host "Ahora carga la extensión (una sola vez):" -ForegroundColor Yellow
Write-Host "  Chrome / Edge / Brave / Opera / Vivaldi:"
Write-Host "    1. Abre la página de extensiones (chrome://extensions, edge://extensions, opera://extensions...)."
Write-Host "    2. Activa el 'Modo de desarrollador'."
Write-Host ("    3. 'Cargar descomprimida' y elige: " + (Join-Path $browserDir "extension"))
Write-Host ("    El ID debe ser " + $chromeId + " (lo fija la 'key' del manifiesto).")
Write-Host "  Firefox (128 o posterior):"
Write-Host "    about:debugging -> Este Firefox -> 'Cargar complemento temporal' -> extension\manifest.json"
Write-Host "    (para dejarla instalada de forma permanente hay que firmarla en addons.mozilla.org)."
