// IDMClone Integration — fondo: captura de las descargas del navegador (como IDM) y de los archivos que el navegador
// abriría él mismo (PDF, vídeo), con las teclas especiales, los formularios POST y el proxy del navegador.
(function (root) {
  "use strict";
  const G = root.IDMBG;
  const { B, L, t, IS_FIREFOX } = G;

  const noHash = (u) => String(u || "").split("#")[0];
  function header(details, name) {
    for (const x of details.responseHeaders || []) if ((x.name || "").toLowerCase() === name) return x.value;
    return "";
  }

  // ---- Cabeceras de las respuestas (Content-Type/Disposition/Length) ----
  // El navegador no siempre las da en la API de descargas, y sin ellas un "descargar.php?id=5" no se reconocería.
  const headerCache = new Map();
  const HEADER_TTL_MS = 60000;

  function rememberHeaders(details) {
    noteProxy(details);
    const h = {};
    for (const x of details.responseHeaders || []) {
      const n = (x.name || "").toLowerCase();
      if (n === "content-type") h.contentType = x.value;
      else if (n === "content-disposition") h.contentDisposition = x.value;
      else if (n === "content-length") h.contentLength = parseInt(x.value, 10) || 0;
    }
    const ct = (h.contentType || "").toLowerCase();
    const interesting = h.contentDisposition || (ct && !/^(text\/|image\/(png|gif|jpeg|webp|svg)|application\/(json|javascript|xml))/.test(ct));
    if (!interesting) return;
    h.t = Date.now();
    headerCache.set(details.url, h);
    if (headerCache.size > 300) {
      const now = Date.now();
      for (const [k, v] of headerCache) if (now - v.t > HEADER_TTL_MS || headerCache.size > 200) headerCache.delete(k);
    }
  }

  function headersFor(...urls) {
    for (const u of urls) {
      const h = u && headerCache.get(u);
      if (h && Date.now() - h.t < HEADER_TTL_MS) return h;
    }
    return {};
  }

  // ---- #17 Proxy que usó el navegador, por servidor ----
  // HTTP ("host:puerto") o SOCKS ("socks5://host:puerto", "socks4://..."). Solo Firefox lo dice en cada petición
  // (details.proxyInfo); Chromium solo si la extensión tuviera el permiso "proxy" (no se pide: ver README).
  const proxyByHost = new Map();
  const PROXY_TTL_MS = 10 * 60000;

  function noteProxy(details) {
    if (!details || !("proxyInfo" in details)) return;
    const h = G.hostOf(details.url);
    if (!h) return;
    const p = L.proxyFromInfo(details.proxyInfo);
    if (p) proxyByHost.set(h, { proxy: p, t: Date.now() }); else proxyByHost.delete(h);
    if (proxyByHost.size > 200) proxyByHost.delete(proxyByHost.keys().next().value);
  }

  // Proxy con el que el navegador llega a esa dirección, o "" (directo, proxy con TLS, desconocido).
  async function proxyFor(url) {
    const known = proxyByHost.get(G.hostOf(url));
    if (known && Date.now() - known.t < PROXY_TTL_MS) return known.proxy;
    if (B.proxy && B.proxy.settings && typeof B.proxy.settings.get === "function") {
      try {
        const d = await new Promise((res) => {
          try { const r = B.proxy.settings.get({}, res); if (r && typeof r.then === "function") r.then(res, () => res(null)); }
          catch (e) { res(null); }
        });
        return L.proxyFromChromeSettings(d && d.value, url);
      } catch (e) { }
    }
    return "";
  }

  // ---- Formularios (POST) ----
  // Algunas descargas solo las da el sitio tras enviar un formulario. Se guarda el cuerpo enviado para que IDMClone
  // pueda repetir la misma petición (como hace IDM).
  const postBodies = new Map();
  const POST_TTL_MS = 120000;

  function rememberPost(details) {
    if (details.method !== "POST" || !details.requestBody) return;
    let data = null;
    const fd = details.requestBody.formData;
    if (fd) {
      const parts = [];
      for (const k of Object.keys(fd)) for (const v of fd[k]) parts.push(encodeURIComponent(k) + "=" + encodeURIComponent(v));
      data = parts.join("&");
    } else if (details.requestBody.raw && details.requestBody.raw.length === 1 && details.requestBody.raw[0].bytes) {
      const bytes = details.requestBody.raw[0].bytes;
      if (bytes.byteLength <= 65536) { try { data = new TextDecoder("utf-8", { fatal: true }).decode(bytes); } catch (e) { data = null; } }
    }
    if (data === null) return;
    postBodies.set(details.url, { data, t: Date.now() });
    if (postBodies.size > 50) postBodies.delete(postBodies.keys().next().value);
  }

  function postFor(...urls) {
    for (const u of urls) {
      const p = u && postBodies.get(u);
      if (p && Date.now() - p.t < POST_TTL_MS) return p.data;
    }
    return "";
  }

  // ---- Teclas especiales (Opciones > General > "Teclas...") ----
  // Como IDM: una tecla pulsada al empezar la descarga decide si va (o no) a IDMClone. La descarga que sale del enlace
  // (redirecciones, cuerpo POST, referer) no se repite desde el enlace. content/keys.js avisa de cuándo se pulsan y se
  // sueltan (solo teclas de verdad, isTrusted, y solo las de las combinaciones configuradas: nunca lo que se escribe).
  // Como IDM, el estado es de todo el navegador; al perder el foco la ventana, se sueltan.
  const keyState = {}; // tecla -> { down, from, to, seen }
  // grace: tras soltarla aún vale (la descarga empieza un poco después del clic); stale: sin noticias de una tecla
  // "pulsada" (keys.js repite el aviso cada segundo mientras se mantiene) se da por suelta. Ver L.keysHeld.
  const KEY_TIMING = { grace: 3000, stale: 5000 };

  function noteKey(name, down, now) {
    if (L.KEY_NAMES.indexOf(name) < 0) return;
    const k = keyState[name] || (keyState[name] = { down: false, from: 0, to: 0, seen: 0 });
    if (down) {
      if (!k.down || now - k.seen > KEY_TIMING.stale) k.from = now; // pulsación nueva (o la anterior se dio por suelta)
      k.down = true; k.seen = now;
    } else if (k.down) {
      k.down = false; k.to = now; k.seen = now;
    }
  }

  function releaseKeys(now) { for (const n of Object.keys(keyState)) noteKey(n, false, now); }

  function specialKeys() {
    const a = G.appCfgNow() || {};
    return { prevent: typeof a.preventKeys === "string" ? a.preventKeys : "del", force: typeof a.forceKeys === "string" ? a.forceKeys : "" };
  }

  function keysHeld(kind) {
    return L.keysHeld(specialKeys()[kind], keyState, Date.now(), KEY_TIMING.grace, KEY_TIMING.stale);
  }

  // Además, el enlace pulsado con la tecla: vale aunque la descarga empiece unos segundos después de soltarla
  // (redirecciones lentas). prevented: "no descargar con IDMClone"; forcedLinks: "forzar la descarga".
  const prevented = new Map();
  const forcedLinks = new Map();
  const PREVENT_MS = 8000;

  function markLink(kind, url, referrer) {
    const map = kind === "prevent" ? prevented : forcedLinks;
    map.set(url, { t: Date.now(), referrer: typeof referrer === "string" ? referrer : "" });
    if (map.size > 50) map.delete(map.keys().next().value);
  }

  function markedLink(map, item) {
    const now = Date.now();
    for (const [u, p] of map) {
      if (now - p.t > PREVENT_MS) { map.delete(u); continue; }
      if (noHash(u) === noHash(item.url) || noHash(u) === noHash(item.finalUrl)) return "url"; // el navegador quita el "#..."
      // El enlace pudo redirigir: vale también si viene de la misma página en los segundos siguientes.
      if (p.referrer && item.referrer === p.referrer && now - p.t < 3000) return "referrer";
    }
    return "";
  }

  function isPrevented(item) { return keysHeld("prevent") || !!markedLink(prevented, item); }
  // "url": el enlace pulsado con la tecla (también una página web); "keys"/"referrer": otra cosa que empezó con la
  // tecla pulsada.
  function forcedBy(item) { return markedLink(forcedLinks, item) || (keysHeld("force") ? "keys" : ""); }

  // ---- Envío a IDMClone ----
  async function sendToIdm(info) {
    let cookies = "";
    try { cookies = await G.gatherCookies(info.url, info.incognito); } catch (e) { }
    const msg = {
      type: "download",
      url: info.url,
      referrer: info.referrer || "",
      cookies,
      filename: info.filename || "",
      userAgent: navigator.userAgent,
      incognito: !!info.incognito,
      size: info.size || 0,
      postData: info.postData || ""
    };
    // Captura automática (no un clic en el menú o en el panel): IDMClone la rechaza si el navegador está desmarcado (#1).
    if (info.auto) msg.auto = true;
    // #17: el proxy (HTTP o SOCKS) que usó el navegador, por si IDMClone no puede descargar directamente.
    let proxy = "";
    try { proxy = await proxyFor(info.url); } catch (e) { }
    if (proxy) msg.browserProxy = proxy;
    return G.hostRequest(msg, G.DOWNLOAD_REPLY_MS);
  }

  // ---- Captura automática ----
  // En Chromium se decide en onDeterminingFilename (ya se conoce el nombre final del servidor); en Firefox, en onCreated
  // (allí ya viene). La descarga se PAUSA mientras IDMClone confirma: si la acepta se cancela en el navegador; si no, se
  // reanuda y sigue allí (como IDM).
  const decided = new Set();
  const waitingName = new Map();
  const prePaused = new Set();
  const hasDetermining = !!(B.downloads && B.downloads.onDeterminingFilename);
  const SUGGEST_HOLD_MS = G.PING_REPLY_MS + G.DOWNLOAD_REPLY_MS + 5000; // getconfig + envío a IDMClone, con margen

  // Capturas a medias (decidiendo, pausada esperando a IDMClone, o cancelándola en el navegador): la recarga tras
  // actualizar espera a que no quede ninguna.
  let capturing = 0;
  function extIdle() { return capturing === 0 && G.pendingCount() === 0 && prePaused.size === 0 && waitingName.size === 0; }
  async function busyWhile(f) { capturing++; try { return await f(); } finally { capturing--; } }

  // ¿Podría ser para IDMClone? Se decide antes de conocer el nombre final: si la URL ya dice que es un tipo no vigilado
  // (p. ej. foto.png con image/png) no hace falta pausar; si no está claro, sí.
  async function mightCapture(item) {
    const s = await G.getSettings();
    if (isPrevented(item)) return false;
    if (forcedBy(item)) return L.decideForced({ url: item.url, finalUrl: item.finalUrl, byExtensionId: item.byExtensionId, ownId: B.runtime.id }).capture;
    const dec = L.decide({ url: item.url, finalUrl: item.finalUrl, mime: item.mime, totalBytes: item.totalBytes,
                           byExtensionId: item.byExtensionId, ownId: B.runtime.id }, s);
    if (dec.capture) return true;
    if (dec.reason.indexOf("tipo no vigilado") !== 0) return false; // desactivada, esquema, sitio excluido...
    const mime = String(item.mime || "").toLowerCase();
    const genericMime = !mime || mime === "application/octet-stream" || mime === "application/force-download" || mime === "binary/octet-stream";
    return !dec.ext || L.extOf(item.url) === "" || genericMime;
  }

  function markDecided(id) {
    decided.add(id);
    if (decided.size > 500) decided.delete(decided.values().next().value);
  }

  function consider(item) { return busyWhile(() => considerNow(item)); }

  async function considerNow(item) {
    if (decided.has(item.id)) return { capture: false, reason: "ya decidida" };
    markDecided(item.id);
    if (item.state && item.state !== "in_progress") return { capture: false, reason: "no está en curso" };

    const s = await G.getSettings();
    const hdr = headersFor(item.finalUrl, item.url);
    const d = {
      url: item.url, finalUrl: item.finalUrl, filename: item.filename, mime: item.mime || hdr.contentType,
      totalBytes: item.totalBytes > 0 ? item.totalBytes : (item.fileSize > 0 ? item.fileSize : (hdr.contentLength || 0)),
      contentDisposition: hdr.contentDisposition, byExtensionId: item.byExtensionId, ownId: B.runtime.id
    };
    let dec = L.decide(d, s);
    const wasPrePaused = prePaused.delete(item.id);
    // Teclas especiales: "no descargar con IDMClone" gana a "forzar" si estuvieran las dos.
    if (isPrevented(item)) { dec.capture = false; dec.reason = "tecla para no descargar con IDMClone"; }
    else if (forcedBy(item)) { const f = L.decideForced(d); if (f.capture) dec = f; }
    const inl = dec.capture ? inlineEntry(item.url, item.finalUrl) : null;
    if (inl && await inl.p) {
      // considerInline ya la mandó al abrirla (visor de PDF desactivado, vídeo que no sabe reproducir...) e IDMClone la
      // aceptó: no se manda dos veces. Si no la aceptó, sigue el camino normal (no se pierde).
      inlineSent.delete(inl.key);
      try { await B.downloads.cancel(item.id); } catch (e) { }
      try { await B.downloads.erase({ id: item.id }); } catch (e) { }
      dec.sent = true; dec.reason = "ya enviada al abrirla";
      return dec;
    }
    if (!dec.capture) {
      // Se pausó por si acaso y al final no es para IDMClone: sigue en el navegador.
      if (wasPrePaused) { try { await B.downloads.resume(item.id); } catch (e) { } }
      return dec;
    }

    let paused = wasPrePaused;
    if (!paused) { try { await B.downloads.pause(item.id); paused = true; } catch (e) { } }

    // Forzada con la tecla: es una orden del usuario (como el menú contextual), no una captura automática (#1).
    const r = await sendToIdm({
      url: item.url || item.finalUrl, referrer: item.referrer, filename: dec.name,
      incognito: item.incognito, size: item.totalBytes > 0 ? item.totalBytes : 0,
      postData: postFor(item.url, item.finalUrl), auto: !dec.forced
    });

    if (r && r.ok) {
      try { await B.downloads.cancel(item.id); } catch (e) { }
      // Si el navegador llegó a terminarla antes de cancelar, borrar su copia: ya la tiene IDMClone.
      try {
        const now = await B.downloads.search({ id: item.id });
        if (now && now[0] && now[0].state === "complete" && now[0].exists !== false) await B.downloads.removeFile(item.id);
      } catch (e) { }
      try { await B.downloads.erase({ id: item.id }); } catch (e) { }
      dec.sent = true;
    } else {
      if (paused) { try { await B.downloads.resume(item.id); } catch (e) { } }
      G.notify(t("notifyStaysInBrowser"), (r && r.error) || t("idmNotAvailable"));
      dec.sent = false;
      dec.error = r && r.error;
    }
    return dec;
  }

  // ---- Archivos que el navegador abriría él mismo ----
  // Un PDF en su visor, un .mp4 en su reproductor: no pasan por la API de descargas. Como IDM: si son de un tipo de la
  // lista, van a IDMClone. Firefox corta la respuesta (webRequestBlocking, como IDM); Chromium (MV3) no puede, así que la
  // pestaña vuelve a la página anterior, o se cierra si se abrió solo para eso. Solo la página entera (no los PDF o vídeos
  // incrustados en un marco) y nunca un "attachment" (eso sí es una descarga y va por la API de descargas).
  const inlineSent = new Map(); // url sin "#" -> { t, settled, p: promesa de si IDMClone la aceptó }
  const INLINE_DEDUP_MS = 4000; // solo para los avisos repetidos de una misma navegación (no "bloquea" volver a pulsar)

  function inlineEntry(...urls) {
    const now = Date.now();
    // Mientras IDMClone no contesta, la entrada sigue (si no, se mandaría otra vez); ya contestada, dura unos segundos.
    for (const [u, e] of inlineSent) if (e.settled && now - e.t > INLINE_DEDUP_MS) inlineSent.delete(u);
    for (const u of urls) { const k = u && noHash(u); if (k && inlineSent.has(k)) return Object.assign({ key: k }, inlineSent.get(k)); }
    return null;
  }
  function inlineSentRecently(...urls) { return !!inlineEntry(...urls); }

  // Pestañas recién abiertas (target=_blank o con la rueda): Firefox las cierra si se abrieron solo para el archivo.
  const newTabs = new Map(); // tabId -> cuándo se abrió
  if (B.tabs && B.tabs.onCreated) B.tabs.onCreated.addListener((tab) => {
    if (!tab || tab.id === undefined) return;
    newTabs.set(tab.id, Date.now());
    if (newTabs.size > 50) newTabs.delete(newTabs.keys().next().value);
  });

  function considerInline(details) { return busyWhile(() => considerInlineNow(details)); }

  async function considerInlineNow(details) {
    if (details.type !== "main_frame" || details.tabId < 0 || details.statusCode < 200 || details.statusCode >= 300) return null;
    if (details.method && details.method !== "GET" && details.method !== "POST") return null;
    // Un formulario cuyo cuerpo no se pudo guardar (archivos, más de 64 KB...): IDMClone no podría repetirlo; se queda.
    if (details.method === "POST" && !postFor(details.url)) return null;
    const ct = header(details, "content-type"), cd = header(details, "content-disposition");
    if (/^\s*attachment/i.test(cd)) return null;
    // Solo lo que el navegador muestra él mismo (visor de PDF, reproductor); lo demás ya es una descarga del navegador.
    // Firefox abre en su visor también un .pdf servido como "application/octet-stream" (Chromium lo descarga).
    const shown = /^\s*(application\/(pdf|x-pdf)|video\/|audio\/)/i.test(ct) ||
      (IS_FIREFOX && /^\s*(application\/(octet-stream|binary)|binary\/octet-stream)\s*(;|$)/i.test(ct) && L.extOf(details.url) === "pdf");
    // Con la tecla "forzar" también cualquier otra cosa que el navegador muestre él mismo (imagen, texto, JSON...), pero
    // no una página web (como IDM: mientras se mantiene la tecla no toma las páginas), salvo que sea el enlace pulsado
    // con ella. Lo que el navegador no sabe mostrar (zip, octet-stream...) se convierte en una descarga: va por consider().
    const page = /^\s*(text\/html|application\/xhtml\+xml)\s*(;|$)/i.test(ct);
    const viewable = shown || page || /^\s*(text\/|image\/|application\/(json|xml|javascript|x-javascript|ecmascript)\s*(;|$)|[a-z]+\/[^;\s]+\+(json|xml)\s*(;|$))/i.test(ct);
    if (!viewable) return null;
    // Lo normal: nada que mirar. (keyState guarda las teclas aunque ya estén sueltas: hay que preguntar si "forzar"
    // sigue pulsada; si no, en Firefox cada página esperaría a getSettings en su escucha bloqueante.)
    if (!shown && !forcedLinks.size && !keysHeld("force")) return null;
    const size = parseInt(header(details, "content-length"), 10) || 0;
    const s = await G.getSettings();
    const d = { url: details.url, mime: ct, contentDisposition: cd, totalBytes: size, ownId: B.runtime.id };
    let dec = shown ? L.decide(d, s) : { capture: false };
    const quick = { url: details.url, finalUrl: details.url, referrer: "" };
    if (!dec.capture && !forcedBy(quick)) return null;
    if (inlineSentRecently(details.url)) return null;
    let tab = null;
    try { tab = await B.tabs.get(details.tabId); } catch (e) { }
    // La página desde la que se pulsó: la de la pestaña; si es nueva (target=_blank), la que inició la navegación.
    const http = (u) => (u && /^https?:/i.test(u) && noHash(u) !== noHash(details.url) ? u : "");
    const referrer = http(tab && tab.url) || http(details.originUrl) || http(details.initiator);
    const who = { url: details.url, finalUrl: details.url, referrer };
    // Tecla "para no descargar con IDMClone" (también si el enlace redirige: misma página de origen).
    if (isPrevented(who)) return null;
    const forced = forcedBy(who);
    if (forced && !(page && forced !== "url")) { const f = L.decideForced(d); if (f.capture) dec = f; }
    if (!dec.capture) return null;
    const key = noHash(details.url);
    let done;
    const entry = { t: Date.now(), settled: false, p: new Promise((res) => { done = res; }) };
    inlineSent.set(key, entry);
    let r = null;
    try {
      r = await sendToIdm({ url: details.url, referrer, filename: dec.name, incognito: !!(tab && tab.incognito),
                            size, postData: details.method === "POST" ? postFor(details.url) : "", auto: !dec.forced });
    } finally { entry.settled = true; entry.t = Date.now(); done(!!(r && r.ok)); }
    if (!(r && r.ok)) { inlineSent.delete(key); return null; }
    return { tab };
  }

  // Chromium: la pestaña ya está abriendo el archivo. Se espera a que lo muestre (si se volviera atrás antes, se iría a
  // la página anterior a la del enlace) y se vuelve atrás; sin historial (pestaña abierta para el enlace), se cierra.
  async function leaveInline(details) {
    let shown = false;
    for (let i = 0; i < 40 && !shown; i++) {
      let tab = null;
      try { tab = await B.tabs.get(details.tabId); } catch (e) { return; }
      const want = noHash(details.url);
      if (tab && noHash(tab.url) === want && !tab.pendingUrl) shown = true;
      else if (tab && noHash(tab.url) !== want && noHash(tab.pendingUrl) !== want && i > 5) return; // el usuario ya se fue a otra página
      else await G.sleep(250);
    }
    if (!shown) return; // nunca llegó a mostrarlo: no se toca la pestaña
    try { await B.tabs.goBack(details.tabId); return; } catch (e) { }
    try { await B.tabs.remove(details.tabId); } catch (e) { }
  }

  // ---- Escuchas ----
  if (B.webRequest && B.webRequest.onHeadersReceived) {
    try {
      B.webRequest.onHeadersReceived.addListener(rememberHeaders,
        { urls: ["<all_urls>"], types: ["main_frame", "sub_frame", "other", "xmlhttprequest", "media", "object"] },
        ["responseHeaders"]);
    } catch (e) { console.warn("IDMClone: sin webRequest", e); }
    try {
      if (IS_FIREFOX) {
        B.webRequest.onHeadersReceived.addListener((d) => considerInline(d).then((x) => {
          if (!x) return {};
          // Pestaña abierta hace un momento solo para el enlace (sigue en blanco): se cierra, como IDM. Nunca una con historia.
          const born = newTabs.get(d.tabId);
          if (x.tab && born && Date.now() - born < 15000 && (!x.tab.url || x.tab.url === "about:blank")) {
            newTabs.delete(d.tabId);
            setTimeout(() => { Promise.resolve(B.tabs.remove(d.tabId)).catch(() => {}); }, 250);
          }
          return { cancel: true };
        }, () => ({})), { urls: ["<all_urls>"], types: ["main_frame"] }, ["blocking", "responseHeaders"]);
      } else {
        B.webRequest.onHeadersReceived.addListener((d) => {
          if (d.type !== "main_frame") return;
          considerInline(d).then((x) => { if (x) return leaveInline(d); }).catch(() => {});
        }, { urls: ["<all_urls>"], types: ["main_frame"] }, ["responseHeaders"]);
      }
    } catch (e) { console.warn("IDMClone: sin captura de los archivos que abre el navegador", e); }
  }
  if (B.webRequest && B.webRequest.onBeforeRequest) {
    try {
      B.webRequest.onBeforeRequest.addListener(rememberPost, { urls: ["<all_urls>"], types: ["main_frame", "sub_frame"] }, ["requestBody"]);
    } catch (e) { console.warn("IDMClone: sin requestBody", e); }
  }

  B.downloads.onCreated.addListener(async (item) => {
    if (hasDetermining && !item.filename) {
      // Esperar al nombre definitivo; si el navegador no lo da (otra extensión, etc.), decidir igual.
      // Mientras tanto, pausar si puede ser para IDMClone: el navegador no avanza mientras se decide.
      waitingName.set(item.id, item);
      setTimeout(() => { const it = waitingName.get(item.id); if (it) { waitingName.delete(item.id); consider(it); } }, 2000);
      try {
        if (await mightCapture(item) && waitingName.has(item.id) && !decided.has(item.id)) {
          prePaused.add(item.id);
          await B.downloads.pause(item.id);
        }
      } catch (e) { prePaused.delete(item.id); }
      return;
    }
    consider(item);
  });

  if (hasDetermining) {
    B.downloads.onDeterminingFilename.addListener((item, suggest) => {
      // Como IDM: el navegador espera a que se decida. Si va a IDMClone se cancela antes de que aparezca su "Guardar
      // como" o su aviso de descarga; si no, sigue con el nombre que él elija (solo lo leemos). Por si IDMClone no
      // contesta nunca, se suelta igual pasado SUGGEST_HOLD_MS.
      let done = false;
      const release = () => { if (done) return; done = true; try { suggest(); } catch (e) { } };
      const safety = setTimeout(release, SUGGEST_HOLD_MS);
      const base = waitingName.get(item.id);
      waitingName.delete(item.id);
      consider(Object.assign({}, base || {}, item))
        .then((dec) => { if (dec && dec.sent) done = true; }, () => {})
        .then(() => { clearTimeout(safety); release(); });
      return true;
    });
  }

  // Mensajes de las páginas (content/keys.js; solo de una página: sender.tab). Una tecla, pulsada o suelta, o "todas
  // sueltas" (la ventana perdió el foco); y el enlace pulsado con una tecla especial: la descarga que salga de él
  // (aunque tarde unos segundos) sigue la tecla.
  G.onMessage["idm-keys"] = (req, sender) => {
    if (!(sender && sender.tab)) return false;
    const now = Date.now();
    if (req.reset === true) releaseKeys(now);
    else if (typeof req.key === "string" && typeof req.down === "boolean") noteKey(req.key, req.down, now);
    return false;
  };
  const mark = (kind) => (req, sender) => {
    if (typeof req.url === "string" && sender && sender.tab) markLink(kind, req.url, req.referrer);
    return false;
  };
  G.onMessage["prevent-capture"] = mark("prevent");
  G.onMessage["force-mark"] = mark("force");

  G.resets.push(() => {
    decided.clear(); waitingName.clear(); prevented.clear(); forcedLinks.clear(); postBodies.clear(); proxyByHost.clear();
    for (const k of Object.keys(keyState)) delete keyState[k];
  });
  Object.assign(G, { consider, considerInline, leaveInline, rememberHeaders, rememberPost, headerCache, headersFor, postFor,
                     proxyFor, sendToIdm, inlineSentRecently, keyState, KEY_TIMING, extIdle });
})(typeof self !== "undefined" ? self : globalThis);
