# Empaqueta la extensión para las tiendas, con un manifiesto limpio para cada familia de navegadores:
#   powershell -ExecutionPolicy Bypass -File browser\package.ps1 [-Out <carpeta>]
# browser\extension sirve tal cual para cargarla "sin empaquetar" en todos los navegadores (por eso lleva claves de
# Chromium y de Firefox, y cada uno avisa de las del otro). Aquí se separan:
#   - idmclone-chromium-<versión>.zip (Chrome Web Store, Edge Add-ons, Opera...): sin "key" (la tienda asigna el ID;
#     ponlo luego en setup\StoreIds.cs), sin "background.scripts", sin "browser_specific_settings" ni "webRequestBlocking"
#     (en Chromium MV3 solo vale para extensiones instaladas por directiva).
#   - idmclone-firefox-<versión>.zip (addons.mozilla.org): sin "key", sin "background.service_worker" ni
#     "minimum_chrome_version"; Firefox usa "background.scripts" y sí permite "webRequestBlocking". Lleva además
#     "data_collection_permissions" (no se recoge ningún dato), que AMO pide a las extensiones nuevas.
param([string]$Out = (Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) "dist\extension"))
$ErrorActionPreference = "Stop"
$ext = Join-Path $PSScriptRoot "extension"
# Ruta absoluta: ZipFile usa la carpeta actual del proceso, que en PowerShell 5.1 no sigue a Set-Location.
$Out = (New-Item -ItemType Directory -Force $Out).FullName
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$manifest = Get-Content (Join-Path $ext "manifest.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$version = $manifest.version

# Idiomas (_locales, se empaquetan con el resto de la carpeta): el de "default_locale" tiene que estar y llevar todos los
# __MSG_...__ del manifiesto; si no, el navegador (y la tienda) rechazan la extensión.
if ($manifest.default_locale) {
  $msgs = Join-Path $ext ("_locales\" + $manifest.default_locale + "\messages.json")
  if (-not (Test-Path $msgs)) { throw ("Falta " + $msgs + " (default_locale).") }
  $texts = Get-Content $msgs -Raw -Encoding UTF8 | ConvertFrom-Json
  foreach ($m in [regex]::Matches((Get-Content (Join-Path $ext "manifest.json") -Raw -Encoding UTF8), '__MSG_(\w+)__')) {
    if (-not $texts.PSObject.Properties[$m.Groups[1].Value]) { throw ("El manifiesto usa " + $m.Value + " y no aparece en " + $msgs) }
  }
}

function Without($obj, [string[]]$names) {
  $copy = $obj | ConvertTo-Json -Depth 20 | ConvertFrom-Json
  foreach ($n in $names) {
    $parts = $n.Split(".")
    $target = $copy
    for ($i = 0; $i -lt $parts.Length - 1; $i++) { $target = $target.($parts[$i]) }
    if ($target -and $target.PSObject.Properties[$parts[-1]]) { $target.PSObject.Properties.Remove($parts[-1]) }
  }
  return $copy
}

function Pack($name, $m) {
  $zipPath = Join-Path $Out ("idmclone-" + $name + "-" + $version + ".zip")
  if (Test-Path $zipPath) { Remove-Item $zipPath }
  $zip = [System.IO.Compression.ZipFile]::Open($zipPath, "Create")
  try {
    # manifest.json propio (UTF-8 sin BOM); el resto de archivos, tal cual (las tiendas piden "/" en las rutas).
    $json = $m | ConvertTo-Json -Depth 20
    $entry = $zip.CreateEntry("manifest.json", "Optimal")
    $w = New-Object System.IO.StreamWriter($entry.Open(), (New-Object System.Text.UTF8Encoding $false))
    try { $w.Write($json) } finally { $w.Dispose() }
    Get-ChildItem $ext -Recurse -File | Where-Object { $_.Name -ne "manifest.json" } | ForEach-Object {
      $rel = $_.FullName.Substring($ext.Length + 1) -replace '\\', '/'
      [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $rel, "Optimal") | Out-Null
    }
  } finally { $zip.Dispose() }
  Write-Host ("OK -> " + $zipPath)
}

$chromium = Without $manifest @("key", "background.scripts", "browser_specific_settings")
$chromium.permissions = @($chromium.permissions | Where-Object { $_ -ne "webRequestBlocking" })
Pack "chromium" $chromium

$firefox = Without $manifest @("key", "background.service_worker", "minimum_chrome_version")
# addons.mozilla.org pide declarar qué datos se envían al desarrollador: ninguno (todo va a IDMClone, en el mismo equipo).
$firefox.browser_specific_settings.gecko | Add-Member -NotePropertyName data_collection_permissions -NotePropertyValue ([ordered]@{ required = @("none") }) -Force
Pack "firefox" $firefox
