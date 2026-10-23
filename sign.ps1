# Firma Authenticode de IDMClone.exe e IDMClone-Setup.exe (SHA-256 + sello de tiempo RFC 3161).
#
#   Con un archivo .pfx:
#     powershell -ExecutionPolicy Bypass -File sign.ps1 -PfxPath C:\claves\idmclone.pfx      (pide la contraseña)
#     o, desde una consola de PowerShell:  .\sign.ps1 -PfxPath ... -PfxPassword (Read-Host -AsSecureString)
#   Con un certificado del almacén del usuario (o del equipo) por su huella:
#     powershell -ExecutionPolicy Bypass -File sign.ps1 -Thumbprint 0123456789ABCDEF0123456789ABCDEF01234567
#   Otros parámetros: -Files <rutas> (por defecto IDMClone.exe y dist\IDMClone-Setup.exe de esta carpeta),
#     -TimestampUrl <url RFC 3161> (por defecto DigiCert), -NoTimestamp, -SignTool <ruta a signtool.exe>, -Description.
#   La contraseña del .pfx también puede venir en la variable de entorno IDMCLONE_PFX_PASSWORD (para CI).
#
# Usa signtool.exe (Windows SDK) si lo encuentra; si no, Set-AuthenticodeSignature de PowerShell para firmar y
# SignerTimeStampEx2 (mssign32.dll) para el sello RFC 3161, que Set-AuthenticodeSignature no pone (el suyo es el antiguo).
# El .pfx se carga en memoria: no se importa en ningún almacén de certificados.
#
# Para que Windows (SmartScreen, "Editor desconocido") confíe en la firma hace falta un certificado de firma de código
# de una entidad reconocida (OV o EV, comprado). Con uno autofirmado la firma es válida pero "no de confianza".
[CmdletBinding(DefaultParameterSetName = "Pfx")]
param(
  [string[]]$Files,
  [Parameter(ParameterSetName = "Pfx")][string]$PfxPath,
  [Parameter(ParameterSetName = "Pfx")][System.Security.SecureString]$PfxPassword,
  [Parameter(ParameterSetName = "Store")][string]$Thumbprint,
  [string]$TimestampUrl = "http://timestamp.digicert.com",
  [switch]$NoTimestamp,
  [string]$SignTool,
  [string]$Description = "IDMClone"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $Files -or $Files.Count -eq 0) {
  $Files = @((Join-Path $root "IDMClone.exe"), (Join-Path $root "dist\IDMClone-Setup.exe"))
}
# Con -File, PowerShell pasa la lista "a","b" como un solo texto "a,b": se separa (salvo que exista tal cual).
$Files = @($Files | ForEach-Object { if (Test-Path -LiteralPath $_) { $_ } else { $_ -split "," } } | Where-Object { $_ } | ForEach-Object { (Resolve-Path -LiteralPath $_.Trim()).ProviderPath })
if (-not $PfxPath -and -not $Thumbprint) { throw "Indica el certificado: -PfxPath <archivo.pfx> o -Thumbprint <huella>." }

# ------------------------------------------------------------------ certificado
function Get-PlainPassword {
  if ($PfxPassword) { $s = $PfxPassword }
  elseif ($env:IDMCLONE_PFX_PASSWORD) { return $env:IDMCLONE_PFX_PASSWORD }
  else { $s = Read-Host -AsSecureString "Contrasena del .pfx" }
  $p = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)
  try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($p) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($p) }
}

$cert = $null; $plain = $null; $storeLocation = $null
if ($PfxPath) {
  $PfxPath = (Resolve-Path -LiteralPath $PfxPath).ProviderPath
  $plain = Get-PlainPassword
  # Sin PersistKeySet: la clave vive solo mientras dura este proceso (no queda en ningun almacen).
  $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($PfxPath, $plain, [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::DefaultKeySet)
} else {
  $Thumbprint = ($Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
  foreach ($loc in "CurrentUser", "LocalMachine") {
    $c = Get-Item -LiteralPath ("Cert:\$loc\My\$Thumbprint") -ErrorAction SilentlyContinue
    if ($c) { $cert = $c; $storeLocation = $loc; break }
  }
  if (-not $cert) { throw "No se encuentra el certificado $Thumbprint en Cert:\CurrentUser\My ni en Cert:\LocalMachine\My." }
}
if (-not $cert.HasPrivateKey) { throw "El certificado no tiene la clave privada: no sirve para firmar." }
$codeSigning = $cert.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq "1.3.6.1.5.5.7.3.3" }
if (-not $codeSigning) { Write-Warning "El certificado no indica el uso 'Firma de codigo' (1.3.6.1.5.5.7.3.3): Windows puede rechazar la firma." }
Write-Host ("Certificado: " + $cert.Subject + "  (huella " + $cert.Thumbprint + ", caduca " + $cert.NotAfter.ToString("yyyy-MM-dd") + ")")

# ------------------------------------------------------------------ signtool.exe
function Find-SignTool {
  if ($SignTool) { if (Test-Path -LiteralPath $SignTool) { return (Resolve-Path -LiteralPath $SignTool).ProviderPath } else { throw "No existe $SignTool" } }
  $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  $kits = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin") | Where-Object { $_ -and (Test-Path $_) }
  $arch = if ([Environment]::Is64BitOperatingSystem) { "x64" } else { "x86" }
  $found = foreach ($k in $kits) { Get-ChildItem -Path $k -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue | Where-Object { $_.Directory.Name -eq $arch } }
  $best = $found | Sort-Object { $_.FullName } -Descending | Select-Object -First 1
  if ($best) { return $best.FullName }
  return $null
}

# ------------------------------------------------------------------ sello RFC 3161 sin signtool
$tsCode = @"
using System;
using System.Runtime.InteropServices;
public static class IdmCloneTimeStamp
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SIGNER_FILE_INFO { public uint cbSize; public string pwszFileName; public IntPtr hFile; }
    [StructLayout(LayoutKind.Sequential)]
    struct SIGNER_SUBJECT_INFO { public uint cbSize; public IntPtr pdwIndex; public uint dwSubjectChoice; public IntPtr pSignerFileInfo; }

    [DllImport("mssign32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int SignerTimeStampEx2(uint dwFlags, ref SIGNER_SUBJECT_INFO pSubjectInfo, string pwszHttpTimeStamp,
                                         [MarshalAs(UnmanagedType.LPStr)] string pszTimeStampAlgorithmOid, IntPtr psRequest, IntPtr pSipData, out IntPtr ppSignerContext);
    [DllImport("mssign32.dll", ExactSpelling = true)]
    static extern int SignerFreeSignerContext(IntPtr pSignerContext);

    // SIGNER_TIMESTAMP_RFC3161 = 2 y el resumen del sello en SHA-256 (OID 2.16.840.1.101.3.4.2.1). Devuelve el HRESULT (0 = bien).
    public static int Stamp(string file, string url)
    {
        SIGNER_FILE_INFO fi = new SIGNER_FILE_INFO();
        fi.cbSize = (uint)Marshal.SizeOf(typeof(SIGNER_FILE_INFO));
        fi.pwszFileName = file;
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SIGNER_FILE_INFO)));
        IntPtr pIndex = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.StructureToPtr(fi, pFile, false);
            Marshal.WriteInt32(pIndex, 0);
            SIGNER_SUBJECT_INFO si = new SIGNER_SUBJECT_INFO();
            si.cbSize = (uint)Marshal.SizeOf(typeof(SIGNER_SUBJECT_INFO));
            si.pdwIndex = pIndex;
            si.dwSubjectChoice = 1; // SIGNER_SUBJECT_FILE
            si.pSignerFileInfo = pFile;
            IntPtr ctx;
            int hr = SignerTimeStampEx2(2, ref si, url, "2.16.840.1.101.3.4.2.1", IntPtr.Zero, IntPtr.Zero, out ctx);
            if (ctx != IntPtr.Zero) SignerFreeSignerContext(ctx);
            return hr;
        }
        finally
        {
            Marshal.DestroyStructure(pFile, typeof(SIGNER_FILE_INFO));
            Marshal.FreeHGlobal(pFile);
            Marshal.FreeHGlobal(pIndex);
        }
    }
}
"@

function Add-TimeStamp([string]$file) {
  if (-not ("IdmCloneTimeStamp" -as [type])) { Add-Type -TypeDefinition $tsCode -Language CSharp }
  for ($i = 1; $i -le 3; $i++) {
    $hr = [IdmCloneTimeStamp]::Stamp($file, $TimestampUrl)
    if ($hr -eq 0) { return }
    Write-Warning ("Sello de tiempo fallido (intento $i, 0x{0:X8}); reintentando..." -f $hr)
    Start-Sleep -Seconds (2 * $i)
  }
  throw ("No se pudo poner el sello de tiempo RFC 3161 con $TimestampUrl (0x{0:X8})." -f $hr)
}

# ------------------------------------------------------------------ firmar
$tool = Find-SignTool
try {
  if ($tool) {
    Write-Host "Firmando con $tool"
    $stArgs = @("sign", "/fd", "SHA256", "/d", $Description)
    if (-not $NoTimestamp) { $stArgs += @("/tr", $TimestampUrl, "/td", "SHA256") }
    # signtool solo acepta la contraseña del .pfx en la línea de órdenes (/p): otros procesos del equipo podrían verla mientras
    # firma. En un equipo compartido es mejor un certificado del almacén o en un token (-Thumbprint).
    if ($PfxPath) { $stArgs += @("/f", $PfxPath); if ($plain) { $stArgs += @("/p", $plain) } }
    else { $stArgs += @("/sha1", $cert.Thumbprint, "/s", "My"); if ($storeLocation -eq "LocalMachine") { $stArgs += "/sm" } }
    & $tool @stArgs @Files
    if ($LASTEXITCODE -ne 0) { throw "signtool.exe termino con el codigo $LASTEXITCODE." }
  } else {
    Write-Host "signtool.exe no encontrado: firmando con Set-AuthenticodeSignature"
    foreach ($f in $Files) {
      $r = Set-AuthenticodeSignature -LiteralPath $f -Certificate $cert -HashAlgorithm SHA256
      if (-not $r.SignerCertificate) { throw ("No se pudo firmar ${f}: " + $r.StatusMessage) }
      if (-not $NoTimestamp) { Add-TimeStamp $f }
    }
  }
} finally {
  if ($PfxPath -and $cert) { $cert.Dispose() }
  $plain = $null
}

# ------------------------------------------------------------------ comprobar
$bad = 0
foreach ($f in $Files) {
  $s = Get-AuthenticodeSignature -LiteralPath $f
  $ts = if ($s.TimeStamperCertificate) { "sello: " + $s.TimeStamperCertificate.Subject } else { "sin sello de tiempo" }
  $sha = (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.ToLowerInvariant()
  Write-Host ("{0}`n  {1} | {2} | {3}`n  SHA-256: {4}" -f $f, $s.Status, $s.SignerCertificate.Subject, $ts, $sha)
  if ($s.Status -eq "Valid") { continue }
  if ($s.SignerCertificate -and $s.Status -eq "UnknownError") {
    # Raiz no reconocida (certificado autofirmado o de pruebas): firmado, pero Windows no confia en el editor.
    Write-Warning ("Firmado, pero la cadena no es de confianza en este equipo: " + $s.StatusMessage)
    continue
  }
  Write-Warning ("Firma incorrecta: " + $s.StatusMessage); $bad++
}
if ($bad -gt 0) { throw "$bad archivo(s) sin una firma correcta." }
