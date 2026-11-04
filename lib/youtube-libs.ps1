# Argumentos de csc para el motor de YouTube (src\YouTube*.cs): referencias a Jint y compañía, y las DLL y el
# descifrador ejs comprimidos como recursos del exe (los carga src\YouTubeLibs.cs). Lo usan build.ps1,
# build-setup.ps1 y tests\run-tests.ps1:   . (Join-Path $root "lib\youtube-libs.ps1"); $args = YouTubeLibArgs $root $fw
function YouTubeLibArgs([string]$root, [string]$fw, [switch]$NoResources) {
  $lib = Join-Path $root "lib"
  foreach ($f in @("Jint.dll","Acornima.dll","System.Memory.dll","System.Buffers.dll","System.Numerics.Vectors.dll",
                   "System.Runtime.CompilerServices.Unsafe.dll","ejs\yt.solver.lib.js","ejs\yt.solver.core.js")) {
    if (-not (Test-Path (Join-Path $lib $f))) { throw "Falta lib\$f (ver lib\README.txt)." }
  }
  $r = @(
    "/nowarn:1701",  # Jint pide Acornima 1.7.0 y va la 1.9.1 (el cargador de YouTubeLibs da la que hay)
    ("/reference:`"" + (Join-Path $lib "Jint.dll") + "`""),
    ("/reference:`"" + (Join-Path $lib "Acornima.dll") + "`""),
    ("/reference:`"" + (Join-Path $lib "System.Memory.dll") + "`""),
    ("/reference:`"" + (Join-Path $fw "System.Numerics.dll") + "`"")
  )
  if ($NoResources) { return $r }
  $embed = Join-Path $root "obj\embed"
  New-Item -ItemType Directory -Force $embed | Out-Null
  $items = @()
  foreach ($d in Get-ChildItem $lib -Filter *.dll) { $items += ,@($d.FullName, ("IDMClone.lib." + $d.Name + ".gz")) }
  foreach ($j in Get-ChildItem (Join-Path $lib "ejs") -Filter *.js) { $items += ,@($j.FullName, ("IDMClone.ejs." + $j.Name + ".gz")) }
  foreach ($it in $items) {
    $src = $it[0]; $name = $it[1]
    $gz = Join-Path $embed $name
    if (-not (Test-Path $gz) -or (Get-Item $gz).LastWriteTimeUtc -lt (Get-Item $src).LastWriteTimeUtc) {
      $in = [System.IO.File]::OpenRead($src)
      try {
        $out = [System.IO.File]::Create($gz)
        try {
          $z = New-Object System.IO.Compression.GZipStream($out, [System.IO.Compression.CompressionLevel]::Optimal)
          try { $in.CopyTo($z) } finally { $z.Dispose() }
        } finally { $out.Dispose() }
      } finally { $in.Dispose() }
    }
    $r += ("/resource:`"" + $gz + "`"," + $name)
  }
  return $r
}
