param([string]$Browser = "edge", [int]$Port = 9333, [switch]$Dialogs, [switch]$ForceKey)
# Lanza un navegador APARTE (perfil temporal) con la extensión de IDMClone cargada y el puerto de
# depuración abierto. IDMClone usa una carpeta de datos temporal (IDMCLONE_HOME), heredada por el host.
# -Dialogs: IDMClone muestra sus ventanas como IDM ("Información del archivo", "Descarga completada").
$root = Join-Path $env:TEMP ("idmclone-browsertest-" + $Browser)
if (Test-Path $root) { [IO.Directory]::Delete($root, $true) }
$profile = Join-Path $root "perfil"; $home2 = Join-Path $root "idmclone"; $bdl = Join-Path $root "descargas-navegador"; $dl = Join-Path $home2 "dl"
foreach ($d in @($profile, $home2, $bdl, $dl)) { New-Item -ItemType Directory -Force $d | Out-Null }
$dlj = $dl -replace '\\','\\'
$cats = @(@("General",""),@("Comprimidos","zip rar 7z"),@("Documentos","pdf doc docx"),@("Programas","exe msi")) | ForEach-Object { '{"Name":"' + $_[0] + '","Folder":"' + $dlj + '","Extensions":"' + $_[1] + '"}' }
$dlg = if ($Dialogs) { '"ShowFileInfoDialog":true,"StartWhileInfoDialog":true,"ShowCompleteDialog":true,"ProgressDialogMode":"normal"' } else { '"ShowFileInfoDialog":false,"ShowCompleteDialog":false,"ProgressDialogMode":"none"' }
if ($ForceKey) { $dlg += ',"UseKeyToForce":true,"ForceKeys":"ins"' }
$cfg = '{"MaxConnections":8,"MinSplitSize":262144,"SpeedLimitKBps":0,"DefaultFolder":"' + $dlj + '","UserAgent":"Mozilla/5.0 IDMClone/1.1","RetryCount":20,"RetryDelaySec":5,"LaunchOnStart":false,' + $dlg + ',"Categories":[' + ($cats -join ',') + ']}'
[IO.File]::WriteAllText((Join-Path $home2 "config.json"), $cfg, (New-Object Text.UTF8Encoding $false))

# Carpeta de descargas del navegador en las preferencias del perfil (como la elegiría un usuario).
New-Item -ItemType Directory -Force (Join-Path $profile "Default") | Out-Null
$prefs = '{"download":{"default_directory":"' + ($bdl -replace '\\','\\') + '","prompt_for_download":false,"directory_upgrade":true},"savefile":{"default_directory":"' + ($bdl -replace '\\','\\') + '"}}'
[IO.File]::WriteAllText((Join-Path $profile "Default\Preferences"), $prefs, (New-Object Text.UTF8Encoding $false))

$ext = Resolve-Path (Join-Path $PSScriptRoot "..\..\extension")
$exe = if ($Browser -eq "brave") { "C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe" } else { "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" }
$env:IDMCLONE_HOME = $home2
$args = @("--user-data-dir=`"$profile`"", "--remote-debugging-port=$Port", "--no-first-run", "--no-default-browser-check",
          "--disable-features=DisableLoadExtensionCommandLineSwitch", "--load-extension=`"$ext`"", "--disable-extensions-except=`"$ext`"",
          "--enable-unsafe-extension-debugging", "--disable-sync", "--window-size=1100,750", "about:blank")
$p = Start-Process -FilePath $exe -ArgumentList $args -PassThru
"pid=$($p.Id)"
"root=$root"
