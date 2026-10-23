# Publicar la extensión en las tiendas

Objetivo: que, al instalar IDMClone, el navegador muestre solo **«Se necesita acción»** para activar
la extensión, como hace IDM. Eso exige que la extensión esté publicada en la tienda de cada navegador:

| Tienda | Coste | Navegadores |
|---|---|---|
| **Microsoft Edge Add-ons** | gratis | Edge |
| **Chrome Web Store** | 5 USD, una sola vez | Chrome, Brave, Vivaldi (y Opera) |

Primero Edge (gratis). La Chrome Web Store se hace igual cuando quieras (ver el final).

## Lo que hay en `dist\store`

Se genera con:

```powershell
powershell -ExecutionPolicy Bypass -File store\build-store.ps1                     # paquete .zip
powershell -ExecutionPolicy Bypass -File store\make-images.ps1 <capturas --tidy>   # imágenes
```

| Archivo | Para qué |
|---|---|
| `IDMClone-Integration-1.3.zip` | El paquete que se sube (sin la clave de desarrollo: la tienda le da su propio ID) |
| `logo-300.png` | Logotipo de la tienda (Edge pide 300×300) |
| `logo-128.png` | Icono (Chrome Web Store) |
| `mosaico-440x280.png` | Mosaico promocional pequeño |
| `captura-1-panel.png`, `captura-2-informacion.png`, `captura-3-progreso.png` | Capturas de 1280×800 |

Además: `store\privacidad.html` (política de privacidad) y los textos de este documento.

## Antes de empezar (una vez)

1. **Dónde publicar la política de privacidad y el instalador.** Las tiendas piden una dirección web pública para la
   política de privacidad, y los revisores necesitan descargar IDMClone para probar la extensión (sin el programa
   no hace nada). Una opción gratis: una cuenta de GitHub con un repositorio público que tenga solo
   `privacidad.html` (activando *GitHub Pages*) y el instalador como *Release*. No hace falta subir el código.
2. Pon tu correo de contacto en `store\privacidad.html` (donde dice `[correo de contacto]`).
3. **Ojo con el nombre.** «IDMClone» contiene «IDM», la marca de Internet Download Manager. Las tiendas rechazan
   nombres que parezcan de otra empresa, y su dueño podría pedir que se retire. Para la tienda conviene un nombre
   propio (por ejemplo «Clon Download Manager» o el que prefieras). Si lo cambias, dímelo y lo cambio en todo
   (extensión, programa, instalador e imágenes).

## Edge Add-ons, paso a paso

1. Entra en el **Partner Center** de Microsoft Edge: <https://partner.microsoft.com/dashboard/microsoftedge/public/login>
   e inicia sesión con tu cuenta Microsoft. La primera vez te pide registrarte como desarrollador
   (cuenta **individual**, gratis): nombre, país, correo de contacto. Confirma el correo.
2. **Crear nueva extensión** → sube `dist\store\IDMClone-Integration-1.3.zip`.
3. En cuanto se sube aparece el **ID de la extensión** (Extension ID / CRX ID, 32 letras). **Pásamelo** y yo:
   - lo pongo en el instalador (`setup\StoreIds.cs`) para que IDMClone acepte la extensión de la tienda y Edge la
     instale solo con «Se necesita acción»;
   - te doy el instalador nuevo para que lo subas a tu enlace de descarga (paso 1 de «Antes de empezar»).
4. **Disponibilidad**: Pública, todos los mercados.
5. **Propiedades**:
   - Categoría: **Productividad**.
   - ¿Accede, recopila o transmite información personal? **Sí** (usa cookies y direcciones de las descargas,
     aunque solo las pasa al programa de tu PC) → **URL de la política de privacidad**: la de tu `privacidad.html`.
   - Sitio web y correo de soporte: los tuyos.
6. **Anuncios de la tienda** → añade el idioma **Español** (y si quieres **Inglés**, con los textos de abajo):
   - Descripción: el texto largo de abajo (cambia `[enlace de descarga]` por el tuyo).
   - Descripción corta: la de abajo.
   - Logotipo: `logo-300.png`. Mosaico promocional pequeño: `mosaico-440x280.png`.
   - Capturas: las tres `captura-*.png`.
   - Términos de búsqueda: los de abajo.
7. **Enviar** → en «Notas para la certificación» pega el texto de abajo.
8. La revisión tarda hasta unos 7 días laborables. Si piden cambios, pásame el mensaje.

## Textos

**Nombre:** IDMClone Integration

**Descripción corta** (también es la del manifiesto):
> Envía las descargas y los vídeos del navegador a IDMClone, el gestor de descargas para Windows.

**Descripción:**
> IDMClone Integration conecta tu navegador con IDMClone, el gestor de descargas para Windows.
>
> • Captura las descargas del navegador y las pasa a IDMClone, que las baja con varias conexiones a la vez, las reanuda si se cortan y las ordena por categorías y colas.
> • Muestra el botón «Descargar este video» encima de los reproductores de vídeo y audio (archivos, HLS y DASH), con la lista de calidades y su tamaño.
> • Menú contextual: «Descargar con IDMClone», «Descargar todos los enlaces» y «Descargar los enlaces seleccionados».
> • Teclas especiales: Supr + clic para que una descarga siga en el navegador; Insert + clic para forzarla.
> • Usa los tipos de archivo y los sitios excluidos que eliges en IDMClone.
>
> Necesita el programa IDMClone instalado en Windows: [enlace de descarga].
> La extensión solo se comunica con IDMClone en tu propio ordenador: no envía datos a nadie, no tiene publicidad ni estadísticas.
> No descarga contenido protegido con DRM.

**Description (English):**
> IDMClone Integration connects your browser with IDMClone, the download manager for Windows.
>
> • Captures browser downloads and hands them to IDMClone, which downloads them with several connections at once, resumes them when they break and organizes them in categories and queues.
> • Shows a "Download this video" button over video and audio players (files, HLS and DASH), with the list of qualities and their size.
> • Context menu: "Download with IDMClone", "Download all links" and "Download selected links".
> • Special keys: Del + click keeps a download in the browser; Insert + click forces it.
> • Uses the file types and excluded sites you choose in IDMClone.
>
> Requires the IDMClone program installed on Windows: [download link].
> The extension only talks to IDMClone on your own computer: it sends no data to anyone and has no ads or analytics.
> It does not download DRM-protected content.

**Términos de búsqueda** (máximo 7): gestor de descargas · download manager · descargar vídeos · acelerador de descargas ·
descargas · HLS · IDMClone

**Notas para la certificación** (en inglés, para el revisor):
> This extension works together with the IDMClone desktop application for Windows, which must be installed to test it:
> [download link]. Steps: 1) run IDMClone-Setup.exe; 2) install this extension; 3) download any file (for example a .zip):
> IDMClone's "Download File Info" window opens; 4) open a page with an HTML5 video: a "Descargar este video" button
> appears over the player. The extension only communicates with the local application through native messaging
> (host "com.idmclone.host"); it uses no remote servers and executes no remote code.

## Chrome Web Store (cuando quieras)

Igual que Edge, en <https://chrome.google.com/webstore/devconsole> (pago único de 5 USD). Mismo `.zip`,
mismas imágenes (icono: `logo-128.png`) y textos. Al subir el borrador aparece el ID: pásamelo y lo pongo en
`setup\StoreIds.cs` (Chrome, Brave y Vivaldi lo usarán). La pestaña **Privacidad** pide además:

- **Finalidad única:** Enviar las descargas y los vídeos del navegador al gestor de descargas IDMClone instalado en el equipo.
- **Justificación de los permisos:**
  - `downloads`: detectar las descargas del navegador para pasarlas a IDMClone y cancelarlas en el navegador cuando IDMClone las acepta.
  - `nativeMessaging`: comunicarse con el programa IDMClone instalado en el equipo.
  - `cookies`: enviar a IDMClone las cookies del sitio de la descarga, para que funcione en sitios con sesión iniciada.
  - `webRequest`: leer el tipo y el nombre de archivo de las respuestas y detectar las listas de vídeo (HLS/DASH) que pide la página.
  - `contextMenus`: menú «Descargar con IDMClone» y «Descargar todos los enlaces».
  - `scripting` y `activeTab`: leer los enlaces de la página cuando eliges «Descargar todos los enlaces».
  - `storage`: guardar los ajustes de la extensión.
  - `notifications`: avisar de si una descarga pasó a IDMClone o siguió en el navegador.
  - Acceso a todos los sitios: las descargas y los vídeos pueden estar en cualquier web, y el panel «Descargar este video» debe aparecer en cualquier página con un reproductor.
- **Código remoto:** No.
- **Datos:** marca «Historial web» y «Contenido del sitio web» solo si te lo pide, y declara que **no se venden ni se
  transfieren a terceros** y que se usan solo para la función principal (se quedan en tu ordenador).
