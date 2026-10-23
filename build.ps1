# Compila IDMClone con el compilador de C# de .NET Framework (sin SDK).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw "No se encontró csc.exe de .NET Framework 4." }

$fw = Split-Path -Parent $csc
$refs = @(
  "System.dll","System.Core.dll","System.Windows.Forms.dll","System.Drawing.dll",
  "System.Web.Extensions.dll","System.Xml.dll","System.Security.dll"
) | ForEach-Object { "/reference:`"" + (Join-Path $fw $_) + "`"" }

$src = Get-ChildItem (Join-Path $root "src") -Filter *.cs | ForEach-Object { "`"" + $_.FullName + "`"" }
$out = Join-Path $root "IDMClone.exe"
$manifest = Join-Path $root "app.manifest"
$icon = Join-Path $root "app.ico"
if (-not (Test-Path $icon)) { & (Join-Path $root "tools\make-icon.ps1") }

$cmd = @(
  "/nologo","/target:winexe","/platform:anycpu","/optimize+","/langversion:5",
  "/out:`"$out`"","/win32manifest:`"$manifest`"","/win32icon:`"$icon`"","/resource:`"$icon`",IDMClone.app.ico"
) + $refs + $src

Write-Host "Compilando IDMClone..." -ForegroundColor Cyan
& $csc @cmd
if ($LASTEXITCODE -ne 0) { throw "La compilación falló (código $LASTEXITCODE)." }
Write-Host ("OK -> " + $out) -ForegroundColor Green
