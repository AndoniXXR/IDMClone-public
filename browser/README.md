# IDMClone — Integración con el navegador

Captura las descargas de **Chrome, Edge, Brave, Opera, Vivaldi, Yandex, Cốc Cốc y Firefox** y
las envía a IDMClone. Es la versión **legítima** de lo descrito en el análisis: usa solo las APIs
que el navegador ofrece a las extensiones (`downloads`, `webRequest` en modo observación,
`cookies`, `contextMenus`) y un **host de mensajería nativa**. No hay driver de kernel, ni
interceptación del tráfico de otras aplicaciones, ni ganchos de teclado.

## Cómo funciona

```
Navegador ─(extensión)─► host nativo (IDMClone.exe) ─(tubería con nombre)─► ventana de IDMClone
  downloads + webRequest     canal persistente, JSON con          responde "ok" solo cuando
  cookies.getAll             prefijo de 4 bytes y "rid"            ha añadido la descarga
```

1. La extensión ve la descarga y decide como IDM: por el **tipo de archivo** (la lista de IDM,
   con comodines como `r0*`) a partir del nombre final del navegador, de `Content-Disposition`
   o del tipo MIME (así también captura `descargar.php?id=5`). Respeta el tamaño mínimo y la
   lista de sitios excluidos ("No descargar automáticamente desde").
2. **Pausa** la descarga del navegador y la manda al host con las cookies del sitio (del almacén
   privado si es una ventana de incógnito), el referer y el user-agent.
3. El host la pasa a IDMClone. Si IDMClone está cerrado, el host lo abre **oculto en la bandeja**.
4. Si IDMClone confirma que la añadió, la extensión cancela la descarga del navegador. Si no
   (no está instalado, no responde, URL rechazada...), **la reanuda en el navegador**: nunca se
   pierde una descarga.

Las descargas de incógnito se hacen, pero no quedan guardadas en la lista de IDMClone.

### Opciones de IDMClone que aplica la extensión (1.4)

La extensión pide la configuración al host (`getconfig`); con un IDMClone antiguo faltan las claves
nuevas y todo sigue como en la 1.3.

- **Capturar descargas para los siguientes navegadores**: el host sabe qué navegador lo lanzó (proceso
  padre). Si está desmarcado, como en IDM 6.43: ni captura automática ni paneles (vídeo y selección);
  el menú contextual sigue funcionando.
- **Menú contextual** (pestañas Chromium y Firefox): crea solo los elementos marcados.
- **Panel de descarga**: tipos de archivo con su tamaño mínimo (también calidades HLS/DASH por su
  extensión), archivos protegidos (DRM) y "No capturar automáticamente de los reproductores-web".
  Una extensión MV3 no puede cortar la petición del reproductor: si IDMClone descarga un tipo
  desmarcado, el navegador lo sigue reproduciendo.
- **Panel para los enlaces seleccionados**: al soltar el ratón o una tecla con una selección que
  contiene enlaces (o direcciones escritas). Solo se lee la selección tras esa acción y no se manda
  nada hasta pulsar el panel.
- **Proxy del navegador** (`browserProxy`, para reintentar con él si la descarga falla): en Firefox,
  el proxy de cada petición (`proxyInfo`): HTTP como `host:puerto` (lo entiende también un IDMClone
  antiguo), SOCKS5 como `socks5://host:puerto` y SOCKS4 como `socks4://` (`socks4a://` si el proxy
  resuelve los nombres). IDMClone sale por un SOCKS con su puente local (`src\Socks.cs`, el mismo de
  Opciones > Proxy/Socks). Proxys con TLS (`https`) y contraseñas no (el navegador no da la contraseña).
  En Chromium no hay forma de saberlo sin el permiso `proxy` (`chrome.proxy.settings`), que no se pide:
  en Firefox ese permiso añadiría el aviso «controlar la configuración del proxy». El código lo usa si
  alguien lo añade al manifiesto (solo `fixed_servers`: http, socks4 y socks5).
- **Teclas especiales** (Opciones > General > «Teclas...»): `content.js` avisa al fondo de cuándo se pulsan
  y se sueltan las teclas de las combinaciones configuradas (solo `isTrusted`, nunca lo que se escribe; al
  perder el foco la ventana se dan por sueltas). Mientras están pulsadas (y 3 s después de soltarlas),
  «no descargar con IDMClone» deja en el navegador cualquier descarga, y «forzar» manda a IDMClone
  cualquier descarga (formularios con su POST, descargas lanzadas por la página, PDF/vídeos/imágenes que
  el navegador abriría; una página web solo si es el enlace pulsado). El clic no se cancela: la descarga
  sale del navegador con sus redirecciones, su referer y su cuerpo POST.
- **Descargar todos los enlaces / los enlaces seleccionados**: `<a>`, `<area>`, imágenes, vídeo y audio de
  todos los marcos del mismo origen que la página (`scripting.executeScript` con `allFrames`, más los
  iframes sin dirección propia); con selección, también las direcciones escritas en el texto
  seleccionado y en los `<textarea>`. Como mucho 1000 enlaces, solo http(s)/ftp(s).
- **Idiomas**: `_locales` (español por defecto, inglés, chino simplificado y japonés), según el idioma
  del navegador: nombre y descripción, menú contextual, popup, paneles y avisos.
- **Cookies para el Grabber** ("iniciar sesión manualmente"): IDMClone puede pedir al navegador las
  cookies de UNA dirección (protocolo en `src\CookieBridge.cs`). Solo IDMClone puede pedirlas (tubería
  del usuario y comprobación de que el otro extremo es IDMClone.exe), nunca se guardan y nunca se leen
  las de incógnito.

## Instalación (sin administrador)

1. Compila IDMClone (`..\build.ps1`) para tener `IDMClone.exe`.
2. Registra el host nativo (para todos los navegadores a la vez). Si instalaste con `..\dist\IDMClone-Setup.exe`
   ya está hecho; si usas la carpeta de desarrollo: doble clic en `install.cmd`, o
   ```powershell
   powershell -ExecutionPolicy Bypass -File install.ps1
   ```
   Ejecútalo desde el Explorador o una consola normal. Si se lanza desde una app empaquetada
   (MSIX, como algunas apps de escritorio de la Store), Windows guarda el registro en una copia
   virtual que solo ve esa app y el navegador dirá «Specified native messaging host not found».
3. Carga la extensión una vez:
   - **Chrome / Edge / Brave / Opera / Vivaldi**: página de extensiones (`chrome://extensions`,
     `edge://extensions`, `opera://extensions`…) → **Modo de desarrollador** → **Cargar
     descomprimida** → carpeta `browser\extension`. El ID debe ser
     **`jolfcgooaockjhnalagfilpljkloallc`** (lo fija la `key` del manifiesto).
   - **Firefox 128+**: `about:debugging` → *Este Firefox* → *Cargar complemento temporal* →
     `extension\manifest.json`. Para dejarla instalada de forma permanente hay que firmarla en
     addons.mozilla.org (Firefox no admite extensiones sin firmar fuera de esa opción).
   - **Vivaldi** pregunta «¿Descargar...?» antes de que la extensión vea cada descarga. Para que
     vaya directa a IDMClone: Configuración › Descargas › **Guardar archivos siempre en la carpeta
     por defecto** (la misma casilla que trae el aviso). Probado en Vivaldi (Windows Sandbox).
   - Al actualizar IDMClone (`/update`), la extensión cargada así (desempaquetada o temporal) ve
     que la de la carpeta de IDMClone es más nueva y se recarga sola desde ella (1.5+).
4. En el popup de la extensión: activar/desactivar la captura, tamaño mínimo, tipos de archivo
   (botón **Valores de IDM**), sitios excluidos y **Probar conexión** (dice si el host responde y
   si IDMClone está abierto).

Para desinstalar el host: `uninstall.ps1` (y quita la extensión de cada navegador).

## Archivos

| Archivo | Contenido |
|---|---|
| `extension/manifest.json` | Manifest V3 válido para Chromium (`service_worker`) y Firefox (`scripts`, `gecko.id`) |
| `extension/logic.js` | Decisión de captura: tipos de IDM, MIME, `Content-Disposition`, exclusiones (sin APIs, se prueba con Node) |
| `extension/background.js` | Captura, pausa/reanudación, cookies, canal persistente con el host, menú contextual |
| `extension/popup.html/.js` | Ajustes y prueba de conexión |
| `com.idmclone.host.json` / `.firefox.json` | Manifiestos del host (los genera `install.ps1` con la ruta real) |
| `install.cmd` / `install.ps1` / `uninstall.ps1` | Registro en HKCU para Chrome, Chromium, Edge, Brave, Opera, Vivaldi, Yandex, Cốc Cốc y Firefox |
| `tests/extension.test.js` | Pruebas con Node: `node --test tests\extension.test.js` |
| `keys/` | Clave de la extensión (ID fijo). No la publiques si distribuyes la extensión. |

## Verificado

- **En Edge 155 y Brave 155 reales** (instancia aparte con perfil temporal, extensión cargada por
  DevTools, clics de ratón reales en una página de pruebas):
  - `.zip`, `.exe` y un `descargar.php` (que el servidor llama «informe final €.pdf») → la
    extensión pausa, IDMClone (cerrado al empezar: el host lo abre en la bandeja) confirma, el
    navegador cancela y borra su entrada; IDMClone los completa con SHA-256 correcto y el referer
    de la página. Nada queda duplicado en el navegador.
  - `.png` → se pausa un instante (llegaba como binario genérico), se reanuda y la termina el
    navegador íntegra.
  - IDMClone rechaza → la descarga se reanuda y la termina el navegador, íntegra.
  - El popup: «Conectado: el host responde e IDMClone está abierto».
- Pruebas con Node (APIs de Chromium y de Firefox simuladas), 48 casos.
- Host lanzado como lo hacen Edge, Brave y Firefox (registro → manifiesto → `.exe`): responde.
- **En Windows Sandbox**, con el instalador: Edge, Chrome, Firefox, Brave, Opera y Vivaldi capturan
  las descargas (en Vivaldi, tras su aviso o con la opción de arriba).

Para repetir la prueba en un navegador real (no toca tus perfiles):

```powershell
powershell -ExecutionPolicy Bypass -File tests\real-browser\run.ps1 edge normal
powershell -ExecutionPolicy Bypass -File tests\real-browser\run.ps1 brave rechazo
```
