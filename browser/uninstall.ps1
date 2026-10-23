# Quita el registro del host de mensajería nativa de IDMClone (HKCU) de todos los navegadores.
$ErrorActionPreference = "SilentlyContinue"
$hostName = "com.idmclone.host"
$bases = @(
  "HKCU:\Software\Google\Chrome\NativeMessagingHosts",
  "HKCU:\Software\Chromium\NativeMessagingHosts",
  "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts",
  "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts",
  "HKCU:\Software\Opera Software\NativeMessagingHosts",
  "HKCU:\Software\Vivaldi\NativeMessagingHosts",
  "HKCU:\Software\Yandex\YandexBrowser\NativeMessagingHosts",
  "HKCU:\Software\CocCoc\Browser\NativeMessagingHosts",
  "HKCU:\Software\Mozilla\NativeMessagingHosts"
)
foreach ($base in $bases) {
  $key = "$base\$hostName"
  if (Test-Path $key) { Remove-Item $key -Force; Write-Host "Eliminado: $key" }
}
Write-Host "Listo. Quita también la extensión desde la página de extensiones de cada navegador." -ForegroundColor Cyan
