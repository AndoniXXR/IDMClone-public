# Paquete de la extensión para las tiendas de Chromium (Edge Add-ons y Chrome Web Store):
#   dist\store\IDMClone-Integration-<versión>.zip
# Igual que browser\extension, pero el manifiesto va sin "key" (cada tienda pone su propio ID) y sin lo
# que solo usa Firefox (browser_specific_settings y background.scripts), para que no salgan avisos.
#   powershell -ExecutionPolicy Bypass -File store\build-store.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$ext = Join-Path $root "browser\extension"
$out = Join-Path $root "dist\store"
$tmp = Join-Path $root "obj\store-extension"
New-Item -ItemType Directory -Force $out | Out-Null
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
Copy-Item $ext $tmp -Recurse

$manifest = Join-Path $tmp "manifest.json"
$version = node -e @"
const fs = require('fs'); const p = process.argv[1];
const m = JSON.parse(fs.readFileSync(p, 'utf8'));
delete m.key; delete m.browser_specific_settings;
if (m.background) delete m.background.scripts;
fs.writeFileSync(p, JSON.stringify(m, null, 2) + '\n');
process.stdout.write(m.version);
"@ $manifest
if ($LASTEXITCODE -ne 0) { throw "No se pudo preparar el manifiesto." }

$zip = Join-Path $out ("IDMClone-Integration-" + $version + ".zip")
if (Test-Path $zip) { Remove-Item $zip }
# Entrada a entrada con "/" (CreateFromDirectory de .NET Framework pone "\" y las tiendas no lo aceptan).
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::Open($zip, "Create")
try {
  Get-ChildItem $tmp -Recurse -File | ForEach-Object {
    $name = $_.FullName.Substring($tmp.Length + 1) -replace '\\', '/'
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $_.FullName, $name, "Optimal") | Out-Null
  }
} finally { $z.Dispose() }
Remove-Item $tmp -Recurse -Force
Write-Host ("OK -> " + $zip) -ForegroundColor Green
