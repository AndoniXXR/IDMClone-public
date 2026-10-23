# IDMClone

Clon del **núcleo** de Internet Download Manager, hecho a partir del análisis del PDF
(`IDM_Analisis_Funcionamiento.pdf`). Aplicación independiente de Windows en C# / WinForms.

Reproduce lo que hace a IDM lo que es (capítulos 7 y 8 del análisis) **sin** las partes
que interceptan o vigilan software ajeno.

## Qué incluye (igual que IDM)

- **Motor con segmentación dinámica, como IDM**: la primera conexión pide el archivo entero y
  empieza a guardar datos desde el primer momento (no hay petición de sondeo aparte); las demás
  se van abriendo y parten el trozo mayor, dejando a la conexión que ya descarga lo que bajará
  mientras la nueva conecta. Como IDM, **reparte el archivo entre 4 conexiones desde el principio**, pasa
  a 8 y **solo sigue añadiendo mientras suman velocidad** (hasta el máximo de Opciones). Tras un corte con
  datos ya recibidos reconecta al momento; ante un 429/503 momentáneo reintenta enseguida, y si el servidor
  de verdad admite menos conexiones, se queda con las que acepta (la conexión de más suelta su trozo). Un
  503 sin Retry-After se reintenta a los 0,5 s, 1 s, 2 s... (hasta "Reintentar cada").
- **Carpeta temporal** (como `DwnlData` de IDM): el archivo se baja allí y pasa a su destino ya
  completo. Opcional.
- **Conexión**: excepciones de conexiones por servidor, cuota ("no más de N MB cada H horas"),
  proxy (Windows, ninguno, manual o script PAC, con o sin usuario; en manual, uno para cada protocolo
  http/https/ftp, el FTP con "GET ftp://", y SOCKS 4/5 con "Proxy DNS"), usuario y contraseña por sitio
  o por descarga (cifrados; Basic, Digest MD5/SHA-256 y NTLM), y cookies con contenedor por dominio
  (también las que el servidor fija durante la descarga).
- **Reanudación**: guarda las posiciones de cada segmento varias veces por minuto; tras una
  pausa o un corte continúa donde iba (si el servidor admite `Range`).
- **HTTP / HTTPS** con `Range` + `If-Range` (detecta si el archivo cambió en el servidor y
  vuelve a empezar), redirecciones, proxy del sistema, reintentos y límite de velocidad global.
  Conexión como los navegadores (*Happy Eyeballs*): si un servidor anuncia una IPv6 que no responde,
  se usa su IPv4 al momento en vez de esperar los ~21 s de .NET (`src/HappyEyeballs.cs`).
  **FTP** con varias conexiones, `REST` y modo pasivo, y **FTPS** (`ftps://`: FTP cifrado con TLS explícito,
  AUTH TLS + PROT P; un certificado no válido o un servidor sin cifrado dan un error claro). Los errores permanentes (404, 403, 550…)
  fallan enseguida con un mensaje claro.
- **Nombre del servidor**: `Content-Disposition` (también `filename*=UTF-8''…`) o la URL final
  tras redirecciones; recoloca la descarga en su categoría y carpeta.
- **Categorías** como las de IDM (General, Comprimidos, Documentos, Música, Vídeo, Programas): cada una
  con su carpeta (`Descargas\Video`, `Descargas\Music`...) y sus tipos de archivo. Botón derecho en el
  panel Categorías: **Agregar categoría**, **Eliminar** y **Propiedades** (nombre, tipos de archivo,
  "solo de estos sitios", carpeta y "Recordar la última ruta de guardado"). Al crear una o cambiar
  sus tipos, ofrece asociarle las descargas antiguas de esos tipos (sin mover los archivos).
  Opciones > Guardar en: carpeta de cada categoría.
- **Vídeo por streaming** (panel **"Descargar este video"** de IDM): HLS (`.m3u8`, también cifrado
  AES-128) y DASH (`.mpd`: plantillas, SegmentTimeline, listas y archivo único). Elige calidad o
  "solo audio", baja los trozos con varias conexiones a la carpeta temporal (se reanuda por trozos) y
  al terminar **une vídeo y audio en un solo archivo** sin programas externos: `.ts` (H.264 o HEVC +
  AAC) y MP4 fragmentado → **MP4**; WebM (VP9/Opus) → **WebM** con índice. Si los formatos no casan,
  guarda el audio aparte (como IDM). **Emisiones en directo** (HLS y DASH dinámico): se graban desde cerca
  del final de la emisión hasta que pulsas Detener o la emisión termina, y se unen en un archivo normal.
  También los reproductores que piden **vídeo y audio a trozos de archivos aparte** (Range o
  `?bytestart=&byteend=`, como Facebook o Instagram): se ofrecen juntos y se mezclan.
  No se puede: el contenido con **DRM** (Netflix, Vimeo cifrado...), igual que en IDM, ni **YouTube**: su
  reproductor ya no pide el vídeo por direcciones normales sino con un protocolo propio (SABR) y fichas
  anti-bots; bajarlo exigiría eludir esas protecciones técnicas, cosa que IDMClone no hace.
- **Colas como las de IDM** (Planificador): cola principal, **cola de sincronización** y las que crees.
  Cada una con sus archivos a la vez, reintentos, horario (al arrancar, a una hora una vez o por días,
  parada a una hora), **repetir cada X horas** y qué hacer al terminar (abrir un archivo, salir de
  IDMClone o apagar el equipo; una cola que no tenía nada que hacer no hace nada). Los completados
  salen de su cola; en la de sincronización se quedan y en cada pasada se vuelven a bajar solo los
  que han cambiado en el servidor. Un archivo fallido pasa al final y se reintenta cada 30 s.
- **Entrada de enlaces**: "Descargar todos los enlaces" (lista con filtros: html, repetidos, imágenes,
  por tipo; carpeta y cola), **descarga por lotes** (`archivo*.zip` con números o letras), portapapeles
  (opcional, como IDM), **arrastrar y soltar** en la lista o en el **destino de arrastre** flotante,
  e importar/exportar en el formato de IDM (**.ef2**) y en texto.
- **Grabber** (capturador de sitios de IDM): asistente de 4 pasos (página de inicio y plantilla, dónde
  guardar, qué páginas explorar, qué archivos bajar) y ventana del proyecto con árbol (por enlace y por
  carpeta), lista marcable, descarga, "Añadir a IDMClone" y estadísticas. Plantillas de IDM (todas las
  imágenes, audio, vídeo, sitio completo, página de inicio completa para navegar sin conexión...),
  niveles de enlace en el sitio y en otros sitios, directorios padre, dominio principal, ventanas
  emergentes, página de desconexión, filtros editables, tamaño mínimo/máximo, duplicados, subcarpetas
  originales y **conversión de enlaces para navegar sin conexión**. Los proyectos se guardan y aparecen
  en "Proyectos Grabber" del panel Categorías (abrir y eliminar); también plantillas propias.
- **Idiomas** (Ver > Idioma), como los archivos `.lng` de IDM: se cambia al momento en todas las ventanas
  y se recuerda. Incluye inglés (`Languages\idmclone_en.lng`), chino simplificado (`idmclone_zh.lng`, con la
  letra Microsoft YaHei UI para que los ideogramas no salgan mezclados) y japonés (`idmclone_ja.lng`); el
  instalador sale en el idioma de Windows. Para otro idioma basta copiar ese archivo
  como `idmclone_<código>.lng` y traducir la parte derecha de cada línea (`texto en español=traducción`,
  con `%s` para lo variable); lo que falte se queda en español.
- **Inicio con Windows** (`/onboot`: oculto en la bandeja).
- **Interfaz como la de IDM** (mismos textos que el IDM en español):
  - Menús **Tareas / Archivo / Descargas / Ver / Ayuda**, barra con los **cuatro estilos de IDM**
    (botones 3D o de neón, grandes o pequeños; clic derecho en la barra o Ver > Barra de herramientas) y
    **Personalizar...** (qué botones salen y en qué orden), panel **Categorías** (con No finalizadas,
    Finalizadas y Colas) e iconos de cada tipo de archivo.
  - Lista con columnas **configurables y ordenables** (Ver > Columnas..., Ver > Organizar archivos):
    Nombre, Q, Tamaño, Estado, Tiempo restante, Velocidad, Última fecha de intento, Descripción...
  - Menú contextual completo: Abrir, Abrir con..., Abrir carpeta, Mover/Renombrar, Redescargar,
    Reanudar/Detener, **Actualizar dirección de descarga**, Quitar, cola, Al hacer doble clic, Propiedades.
  - **Información del archivo a descargar**: el archivo ya va bajando mientras se muestra; "Iniciar
    descarga", "Descargar más tarde" (panel de colas) o "Cancelar" (no deja rastro).
  - **Progreso de descarga** con pestañas *Estado de la descarga* (con el gráfico de segmentos y la
    **tabla de conexiones** N./Descargado/Info), *Límite de velocidad* (por descarga) y *Opciones de
    finalización* (diálogo final, salir de IDMClone, apagar el equipo).
  - **Descarga completada** (Abrir, Abrir con..., Abrir carpeta), **Propiedades de archivo**,
    **Enlace de descarga duplicado**, **Agregar URL** con autorización y limitador de velocidad global.
  - **Opciones** con las pestañas de IDM: General, Tipos de archivo, Guardar en, Descargas, Conexión,
    Proxy, Inicios de sesión y Sonidos. Los tipos de archivo y los sitios excluidos los usa la extensión.
  - **Comprobación de virus** (Opciones > Descargas > Editar..., como IDM): al terminar cada descarga
    se analiza con el antivirus elegido; `[File]` es la ruta del archivo. Para Microsoft Defender, Kaspersky,
    ESET, AVG, avast, ClamAV y Sophos los parámetros se ponen solos; si Defender encuentra una amenaza, avisa.
- **Línea de comandos** al estilo IDM + **instancia única** con IPC por tubería con nombre
  (una segunda ejecución envía la URL a la ventana ya abierta, como el `CIDMLinkTransmitter`
  del análisis, pero solo entre procesos de IDMClone).

- **Captura desde el navegador** (Chrome, Edge, Brave, Opera, Vivaldi y Firefox) con una
  extensión propia (Manifest V3) y un host de mensajería nativa: ver [`browser/`](browser/).
  Decide como IDM (tipos de archivo de IDMClone, MIME, `Content-Disposition`), reenvía la URL con sus
  **cookies, referer y user-agent** (y el **formulario**, si la descarga sale de un POST), y si IDMClone
  no la acepta la descarga sigue en el navegador. Menú "Descargar todos los enlaces / los enlaces
  seleccionados con IDMClone" y teclas especiales de IDM: **Supr** + clic para que no la capture,
  **Insert** + clic para forzarla (se cambian en Opciones > General). Es la vía legítima (solo APIs del navegador), equivalente a lo que describe el análisis.
  Cuando una página reproduce un vídeo o un audio (archivo, HLS o DASH), muestra el **panel "Descargar
  este video"** encima del reproductor con la lista de calidades, su tamaño estimado y la duración.
  Se configura en Opciones > General > Panel de descarga (modo completo/mini, tamaño mínimo, sitios
  donde no mostrarlo, mezclar audio y vídeo).

## Qué NO incluye (a propósito)

- **Driver de kernel WFP** que intercepta e inyecta el tráfico TCP de *otras* aplicaciones.
- **Ganchos globales** de teclado/ratón de todo el sistema.

Son las partes que tocan software ajeno al propio programa; este clon es un gestor de
descargas normal que tú controlas. La captura desde el navegador se hace con la extensión
(APIs oficiales), no enganchándose al sistema por debajo.

## Instalar

Como IDM, con un instalador: **`dist\IDMClone-Setup.exe`** (doble clic). El asistente (Bienvenida,
Licencia, Carpeta y opciones, Instalando y Finalizar) instala IDMClone **solo para tu usuario, sin pedir
administrador**, en `%LOCALAPPDATA%\Programs\IDMClone`:

- copia el programa, los idiomas y la extensión del navegador;
- registra el host nativo en Chrome, Chromium, Edge, Brave, Opera, Vivaldi, Yandex, Cốc Cốc y Firefox;
- crea los accesos directos (menú Inicio y, si quieres, escritorio) y el inicio con Windows;
- se añade a **Aplicaciones instaladas** con su desinstalador (`Uninstall.exe`), que quita todo lo suyo,
  respeta otra copia de IDMClone registrada en otra carpeta y pregunta si conservar la lista y la configuración.

Si IDMClone está abierto se cierra solo para actualizarlo (orden `/exit`), y lo que esté en uso (el host
que tenga abierto un navegador) se aparta y se borra al arrancar la versión nueva. La página final explica
cómo añadir la extensión: Chrome, Edge y Brave solo instalan solas las extensiones de su tienda, así que abre
una guía en tu navegador, deja copiada la ruta y marca ✓ en cuanto la extensión se conecta. Cuando esté publicada
en una tienda (ver `store/PUBLICAR.md`), el instalador la registra como extensión externa y el navegador pide
activarla («Se necesita acción»), como con IDM.
Actualización (la usa IDMClone): `IDMClone-Setup.exe /update [/D=carpeta] [/S]`.
Modo silencioso: `IDMClone-Setup.exe /S [/D=carpeta] [/nodesktop] [/noautostart] [/noexplorermenu]` y `Uninstall.exe /S [/removedata]`.
El texto del acuerdo de licencia está en `setup\LICENCIA.txt`.

Ejecútalo con doble clic (desde el Explorador): si lo lanza una app empaquetada de la Store (MSIX),
Windows guarda el registro en una copia que solo ve esa app.

## Compilar

No hace falta instalar nada: usa el compilador de C# que ya trae Windows (.NET Framework 4.8).

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1          # IDMClone.exe
powershell -ExecutionPolicy Bypass -File build-setup.ps1    # dist\IDMClone-Setup.exe (instalador)
```

`build.ps1` genera `IDMClone.exe` (un único ejecutable, sin dependencias externas). `build-setup.ps1`
lo empaqueta con los idiomas, la extensión y la licencia (nunca las claves de `browser\keys`) dentro del instalador.

### Firmar y publicar una versión

`sign.ps1` firma `IDMClone.exe` y `dist\IDMClone-Setup.exe` con Authenticode (SHA-256 y sello de tiempo RFC 3161),
con un `.pfx` (`-PfxPath`, contraseña en `-PfxPassword` o en `IDMCLONE_PFX_PASSWORD`) o con un certificado del
almacén por su huella (`-Thumbprint`). Usa `signtool.exe` si está instalado; si no, `Set-AuthenticodeSignature`.
`build-setup.ps1 -Sign -PfxPath ...` firma el programa antes de empaquetarlo y el instalador al final, e imprime el
SHA-256 del instalador. Para que Windows confíe en el editor hace falta un certificado de firma de código comprado (OV/EV).

Actualizaciones (Ayuda > Comprobar actualizaciones..., y cada semana): IDMClone lee la última versión de GitHub Releases
del repositorio **público** `AndoniXXR/IDMClone-public` (el privado no sirve: leerlo pediría un token dentro del .exe).
Basta con publicar allí una versión con el instalador como asset `IDMClone-Setup.exe` (GitHub publica su SHA-256); la
etiqueta es la versión, mayor que la instalada (`v1.3.0`):

```
gh release create v1.3.0 dist\IDMClone-Setup.exe --repo AndoniXXR/IDMClone-public --title "IDMClone 1.3.0" --notes "Novedades..."
```

También vale cualquier manifiesto JSON en HTTPS que indique `UpdateFeedUrl` en `config.json`:
`{ "version": "1.3.0", "url": "https://.../IDMClone-Setup.exe", "sha256": "...", "notes": "..." }`.
Solo instala si el SHA-256 coincide y, si el IDMClone instalado está firmado, si el instalador lo está por el mismo editor;
entonces lo ejecuta con `/update` (reinstala en la misma carpeta sin preguntar y vuelve a abrir IDMClone).
## Uso

GUI: ejecuta `IDMClone.exe`.

Línea de comandos (estilo IDM):

```
IDMClone.exe /d URL [/p carpeta] [/f nombre] [/q] [/h] [/n] [/a]
IDMClone.exe /s                 inicia la cola principal
IDMClone.exe https://...        añade la URL a la ventana abierta
```

- `/d URL`  descargar   · `/p`  carpeta destino · `/f`  nombre de archivo · `/a`  solo añadir a la cola
- `/n`  silencioso: sin preguntas ni ventanas (como IDM) · `/q`  salir de IDMClone al terminar
- `/h`  colgar al terminar (se acepta por compatibilidad, sin efecto) · `/s`  iniciar la cola (después de añadir)
- `/onboot`  arranque con Windows: oculto en la bandeja
- Propios de IDMClone: `/console` descarga en la consola sin ventana · `/tray` abrir oculto en la bandeja
- Menú contextual del Explorador: `/urlfile archivo.url` descarga la dirección de un acceso directo de Internet ·
  `/linksfile archivo` abre la lista de enlaces con las direcciones de un .txt, .htm o .html

## Dónde guarda los datos

`%APPDATA%\IDMClone\`: `config.json` (opciones y categorías), `downloads.json` (la lista),
`queues.json` (colas y su programación), `state\<id>.json` (progreso de segmentos para reanudar) y
`grabber\` (proyectos del Grabber, sus plantillas y filtros).

## Estructura

| Archivo | Contenido |
|---|---|
| `src/DownloadEngine.cs` | Motor: segmentación dinámica, robo de trabajo, reanudación, límite de velocidad |
| `src/DownloadManager.cs` | Lista, colas, persistencia, medición de velocidad |
| `src/DownloadItem.cs` · `src/Core.cs` | Modelo, configuración, categorías, formato |
| `src/Scheduler.cs` | Programador y colas (queues.json) |
| `src/Ipc.cs` | Instancia única + tubería con nombre |
| `src/Net.cs` | Proxy, PAC, inicios de sesión, Digest SHA-256 y cuota |
| `src/VirusCheck.cs` | Comprobación de virus al terminar (parámetros de los antivirus conocidos) y su ventana |
| `src/Toolbar.cs` | Botones de la barra y "Personalizar barra de herramientas" |
| `setup/*.cs`, `build-setup.ps1` | Instalador y desinstalador (asistente, registro por usuario, accesos directos) |
| `setup/guia.html`, `setup/StoreIds.cs` | Guía para añadir la extensión (la abre el instalador) e ID de las tiendas cuando esté publicada |
| `store/` | Publicación en Edge Add-ons y Chrome Web Store: paquete, imágenes, textos y guía (`store/PUBLICAR.md`) |
| `src/HappyEyeballs.cs` | Carrera IPv6/IPv4 al conectar (RFC 8305) para todas las conexiones de .NET |
| `src/MainForm.cs` · `src/ProgressForm.cs` · `src/Dialogs.cs` · `src/Dialogs2.cs` | Interfaz WinForms |
| `src/Columns.cs` · `src/Icons.cs` | Columnas de la lista e iconos (dibujados y de Windows) |
| `src/Links.cs` | Lista de enlaces, descarga por lotes, destino de arrastre y formato .ef2 |
| `src/Categories.cs` | Ventana "Agregando categoría" / "Editando propiedades de categoría" |
| `src/Media.cs` · `src/MediaEngine.cs` | Listas HLS/DASH y descarga de vídeo por trozos |
| `src/Mp4.cs` · `src/Ts.cs` · `src/Mkv.cs` | Unión de vídeo y audio: MP4, MPEG-TS (H.264/HEVC/AAC) y WebM |
| `src/MediaDialogs.cs` | Opciones del panel "Descargar este video" |
| `src/Lang.cs` · `Languages\` | Idiomas: traductor de la interfaz y archivos .lng |
| `src/Grabber.cs` · `src/GrabberForm.cs` | Grabber: proyectos, filtros, exploración, descarga y conversión de enlaces; asistente y ventanas |
| `src/Program.cs` | Punto de entrada y línea de comandos |
| `src/Startup.cs` | Inicio con Windows (clave Run) e icono de la aplicación |
| `src/Updater.cs` · `src/UpdateForm.cs` · `src/ConfigUpdates.cs` | Actualizaciones: manifiesto/GitHub Releases, descarga, SHA-256 y firma, ventana y comprobación semanal |
| `sign.ps1` | Firma Authenticode (SHA-256 + RFC 3161) del programa y el instalador |
| `app.ico` · `tools/make-icon.ps1` | Icono (se genera a partir del de la extensión) |

## Tests

```powershell
powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1          # todos
powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1 motor    # solo los que contienen "motor"
```

Compila el programa y el banco de pruebas (`tests\`) y descarga contra un servidor HTTP local
(`tests\TestServer.cs`) que simula servidores problemáticos: sin rangos, que cortan la conexión,
que dejan de respetar `Range`, `206` desplazados o sin `Content-Range`, enlaces caducados que
devuelven HTML, sin `Content-Length`, que exigen cookies/referer… Cada descarga se comprueba con
SHA-256. También hay un servidor FTP de pruebas (`tests\TestFtpServer.cs`), y se prueban el
guardado seguro, la cola y el programador, la disposición de las ventanas, la tubería, `/n`,
`/onboot` y el host nativo de punta a punta.

Si hay Node.js, `run-tests.ps1` ejecuta también los tests de la extensión (`browser\tests`).

La autenticación se prueba con un servidor propio que **verifica de verdad** la respuesta NTLMv2 (la
genera Windows) y Digest, y contra el NTLM del propio Windows (http.sys).

Otras pruebas manuales:
- `tests\bin\IDMCloneTests.exe --screens <carpeta> [--lang Languages\idmclone_en.lng] [--lng-dump textos.txt]`: abre cada
  ventana con datos de prueba y guarda una captura (en otro idioma, y/o con la lista de sus textos para traducir).
- `browser\tests\real-browser\run.ps1 edge dialogo`: captura en Edge real y pulsa los botones de
  "Información del archivo" y "Descarga completada" (también modos `normal` y `rechazo`).
- `browser\tests\real-browser\run.ps1 edge 4b`: Supr/Insert + clic, formulario POST y "Descargar
  todos los enlaces" en un navegador real.
- `browser\tests\real-browser\run.ps1 edge directo`: graba una emisión en directo real desde el panel y la
  detiene; y un reproductor que pide vídeo y audio a trozos (se juntan y se mezclan).
- `browser\tests\real-browser\run.ps1 edge video`: panel "Descargar este video" con reproductores reales
  (hls.js, dash.js y un `<video>` MP4): elige calidad, IDMClone baja y une, y comprueba que se reproduce.
- `tests\bin\IDMCloneTests.exe --grabber <carpeta> <url> <plantilla 0-7> [niveles|todo] [descargar]`: el
  Grabber contra un sitio real; `node tests\real-media\offlinecheck.js <index.html>` abre la página
  guardada en Edge sin red y comprueba que se ven sus imágenes y estilos.
- `tests\bin\IDMCloneTests.exe --media <carpeta> <hls|dash> <url> [calidad|-] [audio|*]`: baja un vídeo
  HLS/DASH real; `node tests\real-media\playcheck.js <archivo>` comprueba en Edge (sin ventana) que se
  reproduce con vídeo y audio y que se puede saltar.
- Las pruebas **nunca apagan el equipo**: el apagado se sustituye por uno simulado.
- `node tests\real-proxy\proxy.js <puerto> [usuario:clave]`: proxy HTTP/HTTPS real para probar con
  `--real <carpeta> --proxy 127.0.0.1:<puerto> [--proxyauth u:c]`, `--pac <url>`, `--temp <carpeta>`
  o `--login <sitio> <usuario> <clave>`.

Para probar la interfaz a mano: `tests\bin\IDMCloneTests.exe --serve 18765` deja el servidor en
marcha e imprime URLs de ejemplo (descarga lenta, sin rangos, enlace caducado…).

Pruebas con archivos reales de internet (imagen, vídeo, audio, programas, documentos,
comprimidos; comprueba el SHA oficial cuando lo hay): `tests\bin\IDMCloneTests.exe --real <carpeta> [conexiones]`.
La variable `IDMCLONE_TRACE=1` muestra en stderr lo que decide el motor (útil para depurar).
Usa una carpeta de datos temporal (variable `IDMCLONE_HOME`), así que no toca tu lista real.

## Verificado

- Descarga de 50 MB con 8 conexiones simultáneas; SHA-256 idéntico al original; cobertura
  `0..fin` sin huecos.
- GUI arranca y persiste configuración; instancia única + IPC reenvían la URL a la ventana abierta.
