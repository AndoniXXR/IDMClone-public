# Ayudante de la prueba con navegador real (modo "dialogo"): espera las ventanas de IDMClone que
# aparecen al capturar una descarga, las fotografía y pulsa sus botones como lo haría el usuario
# (UI Automation de Windows, solo sobre las ventanas de IDMClone).
#   powershell -File dialogs.ps1 <carpeta de salida>
param([string]$Out, [string]$Mode = "dialogo")
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
# Pulsar un botón con su acción por defecto de accesibilidad (MSAA): funciona aunque la ventana no
# esté en primer plano (BM_CLICK, que usa InvokePattern con los botones Win32, no).
Add-Type -ReferencedAssemblies Accessibility -TypeDefinition @"
using System; using System.Runtime.InteropServices; using Accessibility;
public static class Msaa {
  [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object acc);
  public static void Press(IntPtr hwnd) {
    Guid g = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71"); object o;
    int hr = AccessibleObjectFromWindow(hwnd, 0xFFFFFFFC, ref g, out o);
    if (hr != 0) throw new Exception("AccessibleObjectFromWindow: " + hr);
    ((IAccessible)o).accDoDefaultAction(0);
  }
}
"@
New-Item -ItemType Directory -Force $Out | Out-Null
$log = Join-Path $Out "dialogs.log"
function Log($m) { $l = (Get-Date -Format "HH:mm:ss.fff") + " " + $m; Add-Content -Path $log -Value $l -Encoding UTF8 }
$root = [Windows.Automation.AutomationElement]::RootElement

function FindWindow([string]$name, [int]$seconds, [switch]$Prefix) {
  $end = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $end) {
    $wins = $root.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)
    foreach ($w in $wins) {
      $n = $w.Current.Name
      $ok = if ($Prefix) { $n -like "*$name" } else { $n -eq $name }
      if ($ok) {
        try { $p = Get-Process -Id $w.Current.ProcessId -ErrorAction Stop; if ($p.ProcessName -eq "IDMClone") { return $w } } catch { }
      }
    }
    Start-Sleep -Milliseconds 250
  }
  return $null
}

function Snap($w, [string]$file) {
  $r = $w.Current.BoundingRectangle
  $b = New-Object Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
  $g = [Drawing.Graphics]::FromImage($b)
  $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $b.Size)
  $b.Save((Join-Path $Out $file))
  $g.Dispose(); $b.Dispose()
}

function Click($w, [string]$button) {
  $c = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty), $button
  $btn = $w.FindFirst([Windows.Automation.TreeScope]::Descendants, $c)
  if ($btn -eq $null) { Log "NO ENCONTRADO el botón «$button»"; return $false }
  [Msaa]::Press([IntPtr]$btn.Current.NativeWindowHandle)
  Log "clic en «$button»"
  return $true
}

function Texts($w) {
  $all = $w.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
  $t = @()
  foreach ($e in $all) { $n = $e.Current.Name; if ($n -and $n.Length -lt 120) { $t += $n } }
  return ($t -join " | ")
}

if ($Mode -eq "enlaces") {
  $w = FindWindow "Descargar todos los enlaces con IDMClone" 120
  if ($w -eq $null) { Log "NO APARECIÓ la lista de enlaces"; exit 1 }
  Log "apareció «Descargar todos los enlaces con IDMClone»"
  Start-Sleep -Milliseconds 1500
  Snap $w "4-lista-de-enlaces.png"
  Click $w "Aceptar" | Out-Null
  Log "fin"
  exit 0
}

$info = FindWindow "Información del archivo a descargar" 60
if ($info -eq $null) { Log "NO APARECIÓ «Información del archivo a descargar»"; exit 1 }
Log "apareció «Información del archivo a descargar»"
$info.SetFocus()
Start-Sleep -Milliseconds 2500   # el archivo ya va bajando: el tamaño aparece en el diálogo
Snap $info "1-informacion-archivo.png"
Log ("textos: " + (Texts $info))
Click $info "Iniciar descarga" | Out-Null

$prog = FindWindow "paquete.zip" 15 -Prefix
if ($prog -ne $null) { Start-Sleep -Milliseconds 1200; Snap $prog "2-progreso.png"; Log ("progreso: " + $prog.Current.Name) } else { Log "no apareció la ventana de progreso" }

$done = FindWindow "Descarga completada" 90
if ($done -eq $null) { Log "NO APARECIÓ «Descarga completada»"; exit 1 }
Log "apareció «Descarga completada»"
$done.SetFocus()
Start-Sleep -Milliseconds 800
Snap $done "3-descarga-completada.png"
Log ("textos: " + (Texts $done))
Click $done "Cerrar" | Out-Null
Log "fin"
