# Prueba la extensión en un navegador REAL (Edge o Brave) sin tocar tus perfiles:
#   powershell -ExecutionPolicy Bypass -File browser\tests\real-browser\run.ps1 [edge|brave] [normal|rechazo]
# Abre una instancia aparte con un perfil temporal, carga la extensión por DevTools
# (Extensions.loadUnpacked), hace clic en una página de pruebas y comprueba qué captura IDMClone.
# Requiere: IDMClone compilado, tests compilados (tests\run-tests.ps1), Node.js 22+ y el host
# registrado (browser\install.ps1). IDMClone usa una carpeta de datos temporal.
param([string]$Browser = "edge", [string]$Mode = "normal")
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$repo = Resolve-Path (Join-Path $here "..\..\..")
$tests = Join-Path $repo "tests\bin\IDMCloneTests.exe"
$port = if ($Browser -eq "brave") { 9334 } else { 9333 }
$root = Join-Path $env:TEMP ("idmclone-browsertest-" + $Browser)

$server = Start-Process -FilePath $tests -ArgumentList "--serve 18765" -WindowStyle Hidden -PassThru
& (Join-Path $here "start.ps1") -Browser $Browser -Port $port -Dialogs:($Mode -eq "dialogo") -ForceKey:($Mode -eq "4b") | Out-Null
Start-Sleep 5
$fake = $null
$helper = $null
$shots = Join-Path $env:TEMP ("idmclone-dialogos-" + $Browser)
if ($Mode -eq "dialogo" -or $Mode -eq "4b") {
  if (Test-Path $shots) { [IO.Directory]::Delete($shots, $true) }
  $hm = if ($Mode -eq "4b") { "enlaces" } else { "dialogo" }
  $helper = Start-Process -FilePath "powershell" -ArgumentList "-ExecutionPolicy Bypass -File `"$here\dialogs.ps1`" `"$shots`" $hm" -WindowStyle Hidden -PassThru
}
if ($Mode -eq "rechazo") { $fake = Start-Process -FilePath $tests -ArgumentList "--fakegui `"$root\idmclone`"" -WindowStyle Hidden -PassThru; Start-Sleep 2 }
try {
  Push-Location $here
  if ($Mode -eq "4b") { node test4b.js $port $root } elseif ($Mode -eq "video") { node testvideo.js $port $root } elseif ($Mode -eq "directo") { node testlive.js $port $root } else { node test.js $port $root $Mode }
  $hl = Join-Path $root "idmclone\host.log"
  if (Test-Path $hl) { ""; "Registro del host nativo (ultimas lineas):"; Get-Content $hl -Tail 8 -Encoding UTF8 }
  if ($helper) {
    $helper.WaitForExit(120000) | Out-Null
    ""
    "Ventanas de IDMClone (capturas en $shots):"
    Get-Content (Join-Path $shots "dialogs.log") -Encoding UTF8
  }
} finally {
  if ($helper -and -not $helper.HasExited) { Stop-Process -Id $helper.Id -Force -ErrorAction SilentlyContinue }
  Pop-Location
  node -e "const {CDP,browserWs}=require('$($here -replace '\\','/')/cdp.js');(async()=>{const c=await CDP.connect(await browserWs($port));try{await c.send('Browser.close')}catch(e){};c.close()})().catch(()=>{})"
  Start-Sleep 2
  Get-Process IDMClone -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $repo "IDMClone.exe") } | Stop-Process -Force
  if ($fake) { Stop-Process -Id $fake.Id -Force -ErrorAction SilentlyContinue }
  Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
  Start-Sleep 1
  if (Test-Path $root) { [IO.Directory]::Delete($root, $true) }
}
