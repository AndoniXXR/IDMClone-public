# Reescritura de la extensión (2.0): lista de comprobación

Plan aprobado: un panel por vídeo, vídeos por documento (no se vacían al cambiar de dirección sin recargar), datos
de la página en X/Facebook/Instagram/Vimeo y YouTube con el motor de IDMClone.exe (mensaje `ytinfo`). Mismo
protocolo con IDMClone 1.4.1 (solo se añaden mensajes), mismos permisos, mismo ID, Chrome/Edge/Brave y Firefox 128+.

Cada conducta de la 1.7 se marca `[x]` cuando la 2.0 la tiene (con sus tests). Referencias `archivo:línea` de la 1.7
(bg = background.js, lg = logic.js, ct = content.js, pp = popup.js, T = tests/extension.test.js).

## Estado (2026-11-03)
- Extensión 2.0 con IDMClone 1.5.0. Tests: `tests/extension.test.js` (102, los de
  la 1.7 adaptados), `tests/media2.test.js` (panel por vídeo) y `tests/sites.test.js` (datos de los sitios y YouTube):
  123 en total, todos pasan. Las conductas de la 1.7 de esta lista las cubren esos tests; lo que cambia a propósito:
  pushState ya no vacía el panel (cada panel va con su `<video>`; documento nuevo = ficha nueva de content/bridge.js).
- Probado en un navegador real (panel-casos, 21/21) y por el usuario en X, Facebook y YouTube con IDMClone 1.5.0.
  El código de la 1.7 está en el historial de git (commit ba49fcb).

## Estructura
- `lib/` lógica pura (Node y navegador): util, decide, media, subtitles, links, i18n, sites.
- `bg/` service worker: host, config, capture, cookies, menus, links, media-store, media-net, sites/*, main.
- `content/` scripts aislados: bridge, tracker, video-panel, selection-panel, keys.
- `page/hooks.js` en la página (world MAIN), solo en los sitios soportados.
- `background.js` y `content.js` quedan como entradas: cargan los módulos (importScripts / require) y `background.js`
  exporta en Node la fachada con los nombres que usan los tests.

## 0. Arranque y empaquetado
- [ ] Namespace `browser` si existe `browser.runtime`, si no `chrome` (bg, ct, pp).
- [ ] `IS_FIREFOX` = `runtime.getBrowserInfo`; `EXT_VERSION` del manifiesto.
- [ ] Manifiesto: MV3, default_locale es, min Chrome 121, misma `key` (ID jolfcgooaockjhnalagfilpljkloallc), gecko
      `idmclone@idmclone.local` ≥128, mismos permisos, `<all_urls>`, service_worker + scripts, content_scripts en
      document_start y all_frames. Nuevo: content_script world MAIN solo en los sitios soportados.
- [ ] package.ps1 / store/build-store.ps1: zip Chromium (sin key, sin scripts, sin gecko, sin webRequestBlocking) y
      Firefox (sin key, sin service_worker, sin minimum_chrome_version, data_collection_permissions none). El de
      tienda sin YouTube.
- [ ] getAppConfig a los 3 s del arranque; cada 31 s si hay puerto abierto o host caído.

## 1. Captura de descargas
- [ ] DEFAULTS {enabled, exts, minSizeKB 0, exclude}; getSettings con appCfg.exts (fromApp) y captureOff
      (userEnabled, enabled false, browser, offReason).
- [ ] DEFAULT_EXTS (comodines r0*), DEFAULT_EXCLUDE, MIME_EXT, extMatches (* ?), urlExcluded (host/ruta, puerto,
      subdominios salvo `*.`), filenameFromContentDisposition (RFC 6266/5987), esquemas http(s)/ftp(s).
- [ ] decide: desactivada → esquema → descarga propia → sitio excluido (url o finalUrl) → tipo no vigilado → tamaño
      mínimo (0 = se captura). Motivos en español (bg depende de "tipo no vigilado"). Nombre: filename > CD > ruta
      finalUrl > ruta url; extensión o la del MIME. decideForced (solo esquema/propia, forced:true).
- [ ] Caché de cabeceras (onHeadersReceived; main/sub_frame, other, xhr, media, object): CT, CD, CL si hay CD o tipo
      no web; TTL 60 s, tope 300→200.
- [ ] Cuerpos POST (onBeforeRequest main/sub_frame): formData → urlencoded, raw único ≤64 KB UTF-8 estricto; TTL
      120 s, tope 50.
- [ ] Proxy: Firefox proxyInfo (TTL 10 min, tope 200); Chromium proxy.settings fixed_servers si existe.
- [ ] onCreated: Chromium sin filename → waitingName 2 s; mightCapture → pre-pausa (también sin extensión o MIME
      genérico); Firefox → consider directo.
- [ ] onDeterminingFilename: mezcla waitingName, devuelve true, suggest al terminar o a los 35 s; si se envió, no.
- [ ] considerNow: dedup por id (500), solo in_progress, completar mime/tamaño de la caché, prevent gana a force,
      inline ya aceptado → cancel+erase; no capturar y pre-pausada → resume; capturar → pause → sendToIdm (postData,
      auto:!forced); ok → cancel (+removeFile si complete) → erase; fallo → resume + notifyStaysInBrowser.
- [ ] Contador capturing y extIdle (pending, prePaused, waitingName).
- [ ] Teclas: KEY_TIMING grace 3 s / stale 5 s; noteKey/releaseKeys; specialKeys (prevent "del", force "");
      enlaces prevented/forcedLinks 8 s por url sin #, o mismo referrer 3 s, tope 50; keysHeld (KEY_NAMES del ins alt
      ctrl shift). Mensajes ct→bg (solo con sender.tab): idm-keys {key, down}|{reset}, prevent-capture, force-mark.
- [ ] considerInline: main_frame, tab ≥0, 2xx, GET/POST (POST con cuerpo guardado), nunca attachment; shown = pdf,
      video/, audio/ (+ Firefox octet-stream .pdf); force → también lo visible; html solo si forced === "url"; dedup
      inlineSent 4 s; referrer tab.url/originUrl/initiator; auto:!forced.
- [ ] Firefox inline bloqueante {cancel:true} + cierra pestaña nueva en blanco (<15 s) a los 250 ms (tabs.onCreated).
- [ ] Chromium leaveInline: espera a que se muestre (40×250 ms) → goBack o tabs.remove.
- [ ] Incógnito: cookies del almacén privado (incognito, "1", "firefox-private"); `incognito` en download.
- [ ] autoCapture (#9) si mediaNoAutoCapture === false: no si ya inline; decide + minKB del tipo; auto:true; aviso
      solo si falla.

## 2. Vídeo (se rehace: ver plan) — conductas que se mantienen
- [ ] onHeadersReceived detectMedia (sub_frame, other, xhr, media, object) + onResponseStarted solo fromCache.
- [ ] MEDIA_TIMING.audioWait 2500 ms (los tests lo cambian).
- [ ] classifyMedia (200/206, HLS/DASH por MIME o extensión, descarta .ts/.m4s, rangos → file ranged + stripRange,
      audio por MIME/ext/mime=audio, tamaño por content-range/length); mediaExt; mediaTypeAllowed {show,off,small,
      minKB,known}; sin lista minVideoKB/minAudioKB.
- [ ] Corta si captureOff, mediaPanel false, mediaExclude. Archivo de tipo desmarcado no ranged → oculto + autoCapture.
      Pequeño no ranged → nada. Extensiones por defecto .mp4/.m4a/.mp3.
- [ ] Pares de rango (vídeo pequeño retira audio suelto; par <120 s; audio con todos los vídeos ranged; audio solo
      oculto → showLoneAudio a los 2,5 s). sendPair: ext webm/mp4, tamaño suma o -1, clave "pair:"+v.url.
- [ ] HLS/DASH: probe con cookies (20 s), st.probes, calidad sin hijas espera a otras sondas, hijas → children +
      remove, protected con mediaProtected false → oculto, subtítulos (subtitleAllowed, formato por defecto), filtro
      mediaTypes con alternativa de baja resolución, entrada {id, kind, url, duration, protected, live, media, items},
      borra ranged previas. En directo también.
- [ ] Subtítulos sueltos: ignorados con streaming, esperan ocultos a un vídeo visible; classifySubtitle,
      subtitleLang, languageName, subtitleLabel, subtitleFileName, subtitleAllowed.
- [ ] downloadMedia: cleanTitle + mediaFileName; subs; file → sendToIdm; hls/dash/pair → download con mediaKind,
      mediaVideo, mediaAudio, browserProxy.
- [ ] Panel: Shadow DOM cerrado, :host forzado visible, z-index máximo; botones .main/.help/.x, mini sin label ni ?;
      textos solo con textContent; onMain (una opción no protegida → directo; menú con cabecera de vídeos/duración,
      panelLive/panelDuration, panelDrm, items, subtítulos + pageTracks, Acerca de, foco); pageTracks (<track> http(s),
      ≤20, srclang ≤12, label ≤60); mediaLabel; send media-download con estados panelSending/Sent(4 s)/NoReply/
      SendFailReload/SendFailUpdated; isTrusted, tooSoon 500 ms, stopPropagation; X cierra; ? → alert(panelAboutText);
      posición encima a la derecha, dentro si no cabe, pantalla completa; copia huérfana se quita.
- [ ] Cambios de la 2.0: panel por vídeo, por documento, mini y tipos en vivo, comprueba captureOff.

## 4. Panel de selección, menú contextual y enlaces
- [ ] Solo http(s); appCfg + onChanged; enabled = viva && !captureOff && selectionPanel && !closed &&
      selectionPanelAllowed (siteListed).
- [ ] mouseup/keyup reales (no sobre el panel) → check a los 200 ms; nunca selectionchange; Escape y mousedown fuera
      ocultan.
- [ ] check: salta marcos <200×80 y editables; collect: <a> tocados + textUrls + sanitizeLinks, ≤1000.
- [ ] show: <idmclone-selection-panel> cerrado en <html>, mousedown preventDefault, junto al último rect.
- [ ] build: mini; menuSelected, menuDownload, selClose (closed para la página), ? → alert(selAboutText).
- [ ] send: selection-links {links} → selSentOne/selSentMany, se oculta a los 2,5 s; bg: sanitizeLinks, ref
      sender.url http o tab.url, sendLinkList(selection:true).
- [ ] Menú: idmclone-link (link), -media (image/video/audio), -page (page), -selected (selection) con flags.link;
      -all (page) con flags.all; menuChain removeAll→create; menuFlags en storage; null = IDMClone antiguo.
- [ ] onInstalled → initMenus + injectIntoOpenTabs; onStartup → initMenus.
- [ ] onClicked: all/selected → sendLinks; page → frameUrl/pageUrl/tab.url; link/media → linkUrl/srcUrl; no
      capturable → linkNotDownloadable; sendToIdm manual; aviso solo si falla.
- [ ] collectLinks (inyectada): a/area, img/video/audio/source, iframes del mismo origen sin src (profundidad 3),
      selección/input/textarea, ≤5000 enlaces y 200 000 caracteres.
- [ ] mergeFrameLinks (marco 0 primero; todos: mismo origen; textUrls + sanitizeLinks 1000; formato antiguo array).
- [ ] sendLinks (selección: marco pulsado; todos: allFrames) con avisos cantReadLinks/selectionNoLinks/pageNoLinks;
      sendLinkList con cookies de ≤30 servidores.
- [ ] injectIntoOpenTabs (Chromium): http(s) no descartadas, __idmcloneAlive por marco, inyecta los content scripts.

## 5. Protocolo (no cambia; solo se añade `ytinfo` y mediaKind "youtube")
- [ ] connectNative("com.idmclone.host") persistente, rid incremental desde 1; desconexión → pendientes con
      hostUnavailable; timeout → hostTimeout. Tiempos: ping/getconfig 5 s, download/links/showoptions 25 s, probe 20 s.
- [ ] getconfig {v, caps:["getcookies"]}; ping; download normal {url, referrer, cookies, filename, userAgent,
      incognito, size, postData, auto?, browserProxy?}; download de medio {…, mediaKind hls|dash|pair, mediaVideo,
      mediaAudio}; subs (mediaKind "subs"); probe {url, cookies, referrer, userAgent}; links {links[{url,text}],
      referrer, userAgent, cookies{host:str}, selection}; showoptions {tab "General"|"Tipos de archivo"}; cookies
      {req, url, ok, cookies, userAgent, reason?}.
- [ ] getcookies {req, url} sin rid: req entero, url ≤4096 http(s).
- [ ] Respuesta getconfig: exts, exclude, preventKeys (def "del"), forceKeys, mediaPanel (≠false), mediaMini,
      mediaExclude, minVideoKB, minAudioKB, browser, captureOff (===true), menuChrome/menuFirefox {link, all},
      mediaTypes [{ext,minKB,on}], mediaNoAutoCapture (≠false), mediaProtected (≠false), selectionPanel {mode, mini,
      sites}, extVersion. Aceptada si ok && Array.isArray(exts).
- [ ] appCfg: caché 30 s con promesa compartida; ok → storage + applyMenuFlags + refreshIcon + checkExtUpdate; fallo →
      el guardado y reintento a los 5 s; el popup fuerza recarga (appCfgAt = 0).
- [ ] Mensajes internos: ct→bg idm-keys, prevent-capture, force-mark, media-download, selection-links; popup→bg
      ping-host, get-settings, open-options; bg→ct idm-media, idm-media-remove, idm-media-reset (la 2.0 los cambia).
- [ ] storage.local: enabled, exts, minSizeKB, exclude, appCfg, menuFlags, reloadedFor; storage.session recentHosts.

## 6. Otras áreas
- [ ] Puente de cookies: solo servidor abierto en pestaña normal (url o pendingUrl) o visto en 15 min (y gemelo
      www.), nunca incógnito, si no notopen; cookiesForUrl: almacén normal, si no contenedores.
- [ ] noteRecentHost (onUpdated url/loading): http(s), sin incógnito, LRU 300, storage.session; loadRecentHosts.
- [ ] Icono: insignia X gris + "[ERROR] err" si el host falla; "[DESACTIVADA]" si enabled false o captureOff;
      action o browserAction; refresco al cambiar storage.enabled.
- [ ] Notificaciones basic, icon48, ≤200 caracteres (notifyUnavailable, notifyStaysInBrowser, avisos de enlaces).
- [ ] Popup: enabled, minSize, offNote, fromApp (solo lectura, "Editar en IDMClone"), exts (espacios) y exclude
      (líneas), defaults/options/save/test, status, data-i18n, lang; guardar exts/exclude solo si no fromApp; probar
      conexión popupConnected/HostOnly/NoHost/Error; si falla get-settings, lee storage.
- [ ] i18n: mismas claves en es, en, ja, zh_CN; t() devuelve la clave si no hay traducción; 31 idiomas de subtítulos.
- [ ] Recarga tras actualizar: extVersion más nueva, installType development, reloadedFor distinto; espera extIdle
      (250 ms, ≤120 s), guarda reloadedFor, runtime.reload().
- [ ] Ramas de Firefox: menuFirefox, sin onDeterminingFilename, inline bloqueante, PDF octet-stream, proxyInfo,
      almacenes firefox-private/default, sin injectIntoOpenTabs, background.scripts.

## 7. Tests
- Los tests de `tests/extension.test.js` cargan `background.js` en Node con chrome/browser simulados y usan los
  nombres de la fachada; `loadContent` ejecuta los content scripts en un vm con DOM falso. Algunos tests añaden
  APIs después de cargar (scripting, action, tabs.goBack/remove): se leen al usarlas, no al cargar.
- Tests que miran el código fuente: T:275/737 (manifiesto, versión), T:944 (cadenas del panel de selección),
  T:1755 (claves i18n usadas/definidas, interpolaciones de innerHTML), T:1832 (package.ps1).
- real-browser/* y panel-casos/probar.js usan globales del service worker (getAppConfig, tabMedia, sendLinks,
  hostRequest): se mantienen en `self`.
