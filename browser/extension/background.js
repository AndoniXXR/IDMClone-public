// IDMClone Integration — fondo de la extensión.
// Chrome/Edge/Brave/Opera/Vivaldi: service worker (Manifest V3). Firefox: página de fondo (scripts).
// Captura descargas del navegador y las envía a IDMClone por el host de mensajería nativa
// com.idmclone.host. Solo usa las APIs que el navegador da a las extensiones: nada de drivers
// ni de interceptar el tráfico de otras aplicaciones.
"use strict";
if (typeof importScripts === "function" && typeof self.IDMLogic === "undefined") importScripts("logic.js");

const L = self.IDMLogic;
const B = (typeof browser !== "undefined" && browser.runtime) ? browser : chrome;
const t = L.t; // textos de _locales (español por defecto)
const HOST = "com.idmclone.host";
const DOWNLOAD_REPLY_MS = 25000; // con IDMClone cerrado, el host lo arranca y espera su respuesta
const PING_REPLY_MS = 5000;
const DEFAULTS = { enabled: true, exts: L.DEFAULT_EXTS, minSizeKB: 0, exclude: L.DEFAULT_EXCLUDE };

// Tipos de archivo y sitios excluidos: los de IDMClone (Opciones > Tipos de archivo), como hace IDM.
// Se piden al host y se guardan medio minuto (y se refrescan solas mientras el canal está abierto: así un cambio
// en Opciones llega pronto a las páginas); si el host no está, se usan los últimos conocidos o los propios.
const APP_CFG_TTL_MS = 30000;
let appCfg = null, appCfgAt = 0;

let appCfgPending = null;

function getAppConfig() {
  if (appCfg && Date.now() - appCfgAt < APP_CFG_TTL_MS) return Promise.resolve(appCfg);
  if (!appCfgPending) appCfgPending = fetchAppConfig().finally(() => { appCfgPending = null; });
  return appCfgPending;
}

// Firefox (y sus derivados) o Chromium: el menú contextual tiene opciones distintas para cada uno (#2).
const IS_FIREFOX = typeof browser !== "undefined" && !!browser.runtime && typeof browser.runtime.getBrowserInfo === "function";
let EXT_VERSION = "";
try { EXT_VERSION = (B.runtime.getManifest && B.runtime.getManifest().version) || ""; } catch (e) { }

// Las claves de IDMClone 1.4 son opcionales: con un IDMClone antiguo faltan y todo sigue como antes.
function readAppConfig(r) {
  const flags = (x) => (x && typeof x === "object" ? { link: x.link !== false, all: x.all !== false } : null);
  const sp = r.selectionPanel && typeof r.selectionPanel === "object" ? r.selectionPanel : null;
  return {
    exts: r.exts, exclude: Array.isArray(r.exclude) ? r.exclude : [],
    preventKeys: typeof r.preventKeys === "string" ? r.preventKeys : "del", forceKeys: r.forceKeys || "",
    mediaPanel: r.mediaPanel !== false, mediaMini: !!r.mediaMini, mediaExclude: Array.isArray(r.mediaExclude) ? r.mediaExclude : [],
    minVideoKB: r.minVideoKB || 0, minAudioKB: r.minAudioKB || 0,
    // #1: este navegador está desmarcado en "Capturar descargas para los siguientes navegadores".
    browser: typeof r.browser === "string" ? r.browser : "", captureOff: r.captureOff === true,
    // #2: opciones del menú contextual (null = IDMClone antiguo: los cuatro elementos de siempre).
    menu: flags(IS_FIREFOX ? r.menuFirefox : r.menuChrome),
    // #8, #9, #10: tipos del panel de vídeo, captura de los desmarcados y archivos protegidos.
    mediaTypes: Array.isArray(r.mediaTypes)
      ? r.mediaTypes.filter((t) => t && typeof t.ext === "string").map((t) => ({ ext: t.ext.toLowerCase(), minKB: Math.max(0, t.minKB | 0), on: t.on !== false }))
      : null,
    mediaNoAutoCapture: r.mediaNoAutoCapture !== false,
    mediaProtected: r.mediaProtected !== false,
    // #11: panel para los enlaces seleccionados (null = IDMClone antiguo: sin panel).
    selectionPanel: sp ? { mode: sp.mode | 0, mini: !!sp.mini, sites: Array.isArray(sp.sites) ? sp.sites.filter((x) => typeof x === "string") : [] } : null
  };
}

async function fetchAppConfig() {
  // caps: esta extensión sabe contestar a las peticiones de cookies de IDMClone (CookieBridge, #18).
  const r = await hostRequest({ type: "getconfig", v: EXT_VERSION, caps: ["getcookies"] }, PING_REPLY_MS);
  if (r && r.ok && Array.isArray(r.exts)) {
    appCfg = readAppConfig(r);
    appCfgAt = Date.now();
    try { await B.storage.local.set({ appCfg }); } catch (e) { }
    try { await applyMenuFlags(appCfg.menu, false); } catch (e) { }
    refreshIcon();
    checkExtUpdate(r.extVersion).catch(() => {});
  } else if (!appCfg) {
    try { appCfg = (await B.storage.local.get({ appCfg: null })).appCfg; } catch (e) { }
    appCfgAt = Date.now() - APP_CFG_TTL_MS + 5000; // reintentar pronto
  }
  return appCfg;
}

// Tras actualizar IDMClone (/update) los archivos de la extensión de su carpeta ya son los nuevos, pero el navegador
// sigue con los que cargó. Si esta extensión está cargada a mano (desempaquetada en Chromium, temporal en Firefox) y la
// de IDMClone es más nueva, se recarga sola desde la carpeta. Una vez por versión: si la carpeta cargada no es la de
// IDMClone (sigue siendo vieja tras recargar), no se repite en bucle. Las instaladas de una tienda se actualizan solas.
// Nunca a mitad de una captura (la descarga quedaría pausada en el navegador, o IDMClone la recibiría dos veces):
// se espera a que no haya ninguna en curso; si tarda, se intenta en la siguiente lectura de la configuración.
let reloadWaiting = false;
const RELOAD_IDLE_POLL_MS = 250, RELOAD_IDLE_MAX_MS = 120000;
async function checkExtUpdate(installed) {
  if (reloadWaiting || !installed || !L.newerVersion(installed, EXT_VERSION)) return false;
  let me = null;
  try { me = B.management && B.management.getSelf ? await B.management.getSelf() : null; } catch (e) { }
  if (!me || me.installType !== "development") return false;
  const s = await B.storage.local.get({ reloadedFor: "" });
  if (s.reloadedFor === installed) return false;
  reloadWaiting = true;
  try {
    for (let waited = 0; !extIdle(); waited += RELOAD_IDLE_POLL_MS) {
      if (waited >= RELOAD_IDLE_MAX_MS) return false;
      await new Promise((r) => setTimeout(r, RELOAD_IDLE_POLL_MS));
    }
    await B.storage.local.set({ reloadedFor: installed });
    B.runtime.reload();
    return true;
  } finally { reloadWaiting = false; }
}

// Capturas a medias: decidiendo, pausada esperando a IDMClone, o cancelándola en el navegador.
let capturing = 0;
function extIdle() { return capturing === 0 && pending.size === 0 && prePaused.size === 0 && waitingName.size === 0; }
async function busyWhile(f) { capturing++; try { return await f(); } finally { capturing--; } }

async function getSettings() {
  const s = await B.storage.local.get(DEFAULTS);
  if (!Array.isArray(s.exts) || s.exts.length === 0) s.exts = L.DEFAULT_EXTS;
  if (!Array.isArray(s.exclude)) s.exclude = L.DEFAULT_EXCLUDE;
  const a = await getAppConfig();
  if (a && a.exts && a.exts.length) { s.exts = a.exts; s.exclude = a.exclude || []; s.fromApp = true; }
  // #1 (como IDM 6.43 con el navegador desmarcado): sin captura automática ni paneles; el menú contextual sigue.
  if (a && a.captureOff) {
    s.userEnabled = s.enabled;
    s.enabled = false;
    s.captureOff = true;
    s.browser = a.browser || "";
    s.offReason = t("offReason", a.browser || t("thisBrowser"));
  }
  return s;
}

// ---------------------------------------------------------------------------------------------
// Canal persistente con el host nativo (un solo proceso para muchas peticiones). Cada petición
// lleva un "rid" y el host lo devuelve en la respuesta. Si el canal se cae, se reabre en la siguiente.
let port = null;
let nextRid = 1;
const pending = new Map();

// Estado en el botón de la barra, como IDM: insignia gris "X" y "[ERROR]" en la descripción si no se puede
// hablar con IDMClone, "[DESACTIVADA]" si la captura automática está desactivada (aquí o en IDMClone, #1).
let hostDown = ""; // último error al hablar con el host ("" = responde)
let iconKey = "";

function noteHost(err) {
  const v = err || "";
  if (v === hostDown) return;
  hostDown = v;
  refreshIcon();
}

async function refreshIcon() {
  const A = B.action || B.browserAction;
  if (!A || typeof A.setBadgeText !== "function") return;
  let off = false;
  try { const s = await B.storage.local.get({ enabled: true }); off = s.enabled === false || !!(appCfg && appCfg.captureOff); } catch (e) { }
  const state = hostDown ? "error" : off ? "off" : "ok"; // hostDown leído DESPUÉS de esperar: un error recién llegado gana
  const title = t("extName") + (state === "error" ? " " + t("titleError") + " " + hostDown : state === "off" ? " " + t("titleOff") : "");
  if (iconKey === state + title) return;
  iconKey = state + title;
  const quiet = (r) => { if (r && typeof r.then === "function") r.then(null, () => {}); };
  try {
    quiet(A.setBadgeText({ text: state === "ok" ? "" : "X" }));
    if (state !== "ok" && A.setBadgeBackgroundColor) quiet(A.setBadgeBackgroundColor({ color: "#808080" }));
    quiet(A.setTitle({ title }));
  } catch (e) { }
}

if (B.storage && B.storage.onChanged) B.storage.onChanged.addListener((ch) => { if (ch.enabled) refreshIcon(); });

function connect() {
  if (port) return port;
  const p = B.runtime.connectNative(HOST);
  p.onMessage.addListener((msg) => {
    noteHost("");
    // Petición de IDMClone (no una respuesta: no lleva "rid"). Solo llega por el canal nativo, que solo abre IDMClone.
    if (msg && msg.type === "getcookies" && msg.rid === undefined) { answerCookies(msg).catch(() => {}); return; }
    const w = msg && pending.get(msg.rid);
    if (!w) return;
    pending.delete(msg.rid);
    clearTimeout(w.timer);
    w.resolve(msg);
  });
  p.onDisconnect.addListener(() => {
    const e = (p.error && p.error.message) || (B.runtime.lastError && B.runtime.lastError.message) || t("hostClosed");
    if (port === p) port = null;
    // Se cerró con peticiones sin contestar (host no instalado, IDMClone movido...): error en el icono.
    // Si no esperaba nada (IDMClone se cerró), el canal se reabre en la siguiente petición.
    if (pending.size) noteHost(e);
    for (const [rid, w] of pending) { clearTimeout(w.timer); w.resolve({ ok: false, error: t("hostUnavailable", noDot(e)) }); }
    pending.clear();
  });
  port = p;
  return p;
}

// "Specified native messaging host not found." -> sin el punto final (el texto traducido sigue con ". ¿Ejecutaste...?").
function noDot(e) { return String(e).replace(/[.\s]+$/, ""); }

function hostRequest(msg, timeoutMs) {
  return new Promise((resolve) => {
    let p;
    try { p = connect(); } catch (e) { noteHost(String(e && e.message || e)); resolve({ ok: false, error: t("hostUnavailable", noDot(e && e.message || e)) }); return; }
    const rid = nextRid++;
    const timer = setTimeout(() => { pending.delete(rid); resolve({ ok: false, error: t("hostTimeout") }); }, timeoutMs);
    pending.set(rid, { resolve, timer });
    try { p.postMessage(Object.assign({ rid }, msg)); }
    catch (e) { pending.delete(rid); clearTimeout(timer); if (port === p) port = null; resolve({ ok: false, error: String(e && e.message || e) }); }
  });
}

// ---------------------------------------------------------------------------------------------
// Cabeceras de las respuestas (Content-Type/Disposition/Length): el navegador no siempre las da
// en la API de descargas, y sin ellas un "descargar.php?id=5" no se reconocería.
const headerCache = new Map();
const HEADER_TTL_MS = 60000;

// #17 Proxy que usó el navegador, por servidor: HTTP ("host:puerto") o SOCKS ("socks5://host:puerto", "socks4://...").
// Solo Firefox lo dice en cada petición (details.proxyInfo); Chromium no lo da (ver proxyFor).
const proxyByHost = new Map();
const PROXY_TTL_MS = 10 * 60000;

function hostOf(url) { try { return new URL(url).host.toLowerCase(); } catch (e) { return ""; } }

function noteProxy(details) {
  if (!details || !("proxyInfo" in details)) return;
  const h = hostOf(details.url);
  if (!h) return;
  const p = L.proxyFromInfo(details.proxyInfo);
  if (p) proxyByHost.set(h, { proxy: p, t: Date.now() }); else proxyByHost.delete(h);
  if (proxyByHost.size > 200) proxyByHost.delete(proxyByHost.keys().next().value);
}

// Proxy con el que el navegador llega a esa dirección ("host:puerto" si es HTTP, "socks5://host:puerto"...), o ""
// (directo, proxy con TLS, desconocido).
async function proxyFor(url) {
  const known = proxyByHost.get(hostOf(url));
  if (known && Date.now() - known.t < PROXY_TTL_MS) return known.proxy;
  // Chromium: solo si la extensión tuviera el permiso "proxy" (no se pide: ver README). Sin él, B.proxy no existe.
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

if (B.webRequest && B.webRequest.onHeadersReceived) {
  try {
    B.webRequest.onHeadersReceived.addListener(rememberHeaders,
      { urls: ["<all_urls>"], types: ["main_frame", "sub_frame", "other", "xmlhttprequest", "media", "object"] },
      ["responseHeaders"]);
  } catch (e) { console.warn("IDMClone: sin webRequest", e); }
}

// ---------------------------------------------------------------------------------------------
// Formularios (POST): algunas descargas solo las da el sitio tras enviar un formulario. Se guarda el
// cuerpo enviado para que IDMClone pueda repetir la misma petición (como hace IDM).
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

if (B.webRequest && B.webRequest.onBeforeRequest) {
  try {
    B.webRequest.onBeforeRequest.addListener(rememberPost, { urls: ["<all_urls>"], types: ["main_frame", "sub_frame"] }, ["requestBody"]);
  } catch (e) { console.warn("IDMClone: sin requestBody", e); }
}

// ---------------------------------------------------------------------------------------------
// Panel "Descargar este video" (como IDM): cuando la página reproduce un vídeo o un audio (archivo,
// lista HLS .m3u8 o DASH .mpd), se avisa a la página (content.js) para que muestre el panel encima
// del reproductor. Las calidades de HLS/DASH las lee IDMClone (mensaje "probe" del host nativo).
const tabMedia = new Map(); // tabId -> { items: Map(url -> entrada), children: Set(listas de cada calidad), streaming }
const tabPage = new Map();  // tabId -> dirección de la página (sin "#..."), para notar los cambios sin recarga
const PROBE_REPLY_MS = 20000;
// audioWait: un audio pedido a trozos sin vídeo aún espera (oculto) a su vídeo antes de ofrecerse solo: el
// reproductor (Facebook, Instagram...) suele pedir el audio un poco antes que el vídeo, y no es música.
const MEDIA_TIMING = { audioWait: 2500 };
let mediaSeq = 1;

function mediaState(tabId) {
  let s = tabMedia.get(tabId);
  if (!s) { s = { items: new Map(), children: new Set() }; tabMedia.set(tabId, s); }
  return s;
}

function header(details, name) {
  for (const x of details.responseHeaders || []) if ((x.name || "").toLowerCase() === name) return x.value;
  return "";
}

async function detectMedia(details) {
  if (details.tabId < 0) return;
  const contentType = header(details, "content-type");
  const m = L.classifyMedia({ url: details.url, type: details.type, statusCode: details.statusCode,
    contentType, contentLength: parseInt(header(details, "content-length"), 10) || 0,
    contentRange: header(details, "content-range") });
  if (!m) {
    const sub = L.classifySubtitle({ url: details.url, type: details.type, statusCode: details.statusCode, contentType });
    if (sub) await detectSubtitle(details, sub);
    return;
  }
  const st = mediaState(details.tabId);
  const key = (m.url || details.url).split("#")[0];
  if (st.items.has(key) || st.children.has(key)) return;
  if (m.kind === "hls" || m.kind === "dash") st.streaming = true; // sus .vtt son trozos de sus subtítulos, no archivos sueltos
  // Trozos de un archivo cuando la página ya tiene su lista HLS/DASH: son de esa lista.
  if (m.ranged && [...st.items.values()].some((e) => e && e.kind !== "file" && e.kind !== "pair" && e.kind !== "subs")) return;
  st.items.set(key, null); // en curso: no preguntar dos veces por la misma lista
  const cfg = (await getAppConfig()) || {};
  // #1: navegador desmarcado en IDMClone (como IDM 6.43): sin panel de vídeo ni captura de reproductores.
  if (cfg.captureOff) return;
  let tab = null;
  try { tab = await B.tabs.get(details.tabId); } catch (e) { }
  const page = (tab && tab.url) || details.documentUrl || details.initiator || "";
  if (cfg.mediaPanel === false || (cfg.mediaExclude && L.urlExcluded(page, cfg.mediaExclude))) return;

  let entry;
  if (m.kind === "file") {
    const url = m.url || details.url;
    // #8: tipo de archivo de la lista del panel (y su tamaño mínimo).
    const allow = L.mediaTypeAllowed(L.mediaExt(url, contentType), m.size, m.media, cfg);
    if (allow.off && !m.ranged) {
      // Tipo desmarcado: sin panel; si además está en Tipos de archivo, IDMClone lo descarga solo (#9).
      st.items.set(key, { kind: "file", url, media: m.media, hidden: true, auto: true, t: Date.now(), items: [] });
      await autoCapture(details, url, contentType, m, allow, cfg, tab, page);
      return;
    }
    // Más pequeño que el mínimo (o tipo desmarcado): no se ofrece solo; pero el audio de un vídeo pedido a trozos
    // se guarda (sin enseñarlo) para poder juntarlo con su vídeo.
    const small = !allow.show;
    if (small && !m.ranged) return;
    const ext = "." + (L.extOf(url) || (m.media === "audio" ? (m.ranged ? "m4a" : "mp3") : "mp4"));
    entry = { id: mediaSeq++, kind: "file", url, media: m.media, ranged: !!m.ranged, t: Date.now(), hidden: small,
              items: [{ kind: m.media, ext, size: m.size }] };
    // Vídeo y audio en archivos aparte (el reproductor pide los dos a trozos): se ofrecen juntos, y
    // IDMClone los mezcla en un archivo (como IDM con Facebook o Instagram).
    if (m.ranged) {
      if (m.media === "video" && small) {
        // Vídeo demasiado pequeño (anuncio, clip corto...): ni solo ni con audio. El audio que ya se ofrecía solo
        // era suyo (no música): se quita del panel; el que espera a su vídeo ya no sale (ver showLoneAudio).
        st.items.set(key, entry);
        for (const e of [...st.items.values()]) if (e && e.alone) { e.alone = false; e.hidden = true; await removeEntry(details, e); }
        return;
      }
      const other = [...st.items.values()].filter((e) => e && e.kind === "file" && e.ranged && e.media !== m.media && Date.now() - e.t < 120000).pop();
      if (other) {
        st.items.set(key, entry);
        if (m.media === "audio") {
          // El audio sirve para todas las calidades de vídeo vistas: un par por cada una.
          for (const e of [...st.items.values()]) if (e && e.kind === "file" && e.ranged && e.media === "video" && !e.hidden) await sendPair(details, st, e, entry);
          return;
        }
        await sendPair(details, st, entry, other);
        return;
      }
      if (small) { st.items.set(key, entry); return; }
      if (m.media === "audio") {
        // Audio sin vídeo aún: no se enseña ya (saldría como música y luego cambiaría a vídeo); espera oculto a su vídeo.
        entry.hidden = true;
        st.items.set(key, entry);
        setTimeout(() => { showLoneAudio(details, st, key, entry).catch(() => {}); }, MEDIA_TIMING.audioWait);
        return;
      }
    }
  } else {
    let cookies = "";
    try { cookies = await gatherCookies(details.url, tab && tab.incognito); } catch (e) { }
    const probing = hostRequest({ type: "probe", url: details.url, cookies, referrer: page, userAgent: navigator.userAgent }, PROBE_REPLY_MS);
    if (!st.probes) st.probes = new Set();
    st.probes.add(probing);
    let r;
    try { r = await probing; } finally { st.probes.delete(probing); }
    if (!r || !r.ok) { st.items.delete(key); return; }
    // Lista de una sola calidad (sin hijas) mientras IDMClone aún lee otra lista de la pestaña: suele ser una calidad
    // o el audio de esa maestra (X pide la maestra y enseguida sus hijas). Se espera a la maestra antes de enseñarla,
    // para que no aparezca un momento en el panel y desaparezca.
    if (!(r.children && r.children.length) && st.probes.size) {
      try { await Promise.all([...st.probes]); } catch (e) { }
      if (tabMedia.get(details.tabId) !== st) return; // página nueva
    }
    // Las listas de cada calidad no son vídeos aparte. El reproductor pudo pedir alguna mientras
    // IDMClone leía la maestra: se quitan del panel.
    for (const c of r.children || []) {
      const ck = c.split("#")[0];
      st.children.add(ck);
      const old = st.items.get(ck);
      st.items.delete(ck);
      if (old) { try { await B.tabs.sendMessage(details.tabId, { type: "idm-media-remove", url: old.url }); } catch (e) { } }
    }
    if (st.children.has(key)) { st.items.delete(key); return; } // resultó ser una calidad de otra lista
    if (!r.items || !r.items.length) return;
    const hiddenList = () => { st.items.set(key, { id: mediaSeq++, kind: r.kind, url: details.url, hidden: true, items: [] }); };
    // #10: contenido protegido (DRM) con "Mostrar el panel para los archivos protegidos" desmarcada (como IDM).
    if (r.protected && cfg.mediaProtected === false) { hiddenList(); return; }
    // Subtítulos de la lista: van después de las calidades (no cuentan para los tipos del panel ni se ofrecen solos).
    const subItems = r.items.filter((i) => i.kind === "subtitle");
    const avItems = r.items.filter((i) => i.kind !== "subtitle");
    // #8: solo las calidades de un tipo marcado (por la extensión del archivo que sale: .mp4, .m4a, .webm...).
    let items = avItems;
    if (Array.isArray(cfg.mediaTypes)) {
      const checked = avItems.map((i) => ({ i, a: L.mediaTypeAllowed(String(i.ext || "").replace(/^\./, ""), i.size, i.kind === "audio" ? "audio" : "video", cfg) }));
      items = checked.filter((x) => x.a.show).map((x) => x.i);
      // Si ninguna calidad de vídeo llega al mínimo (clip corto a baja resolución, como los de X), la lista es el vídeo
      // de la página, no un anuncio: se ofrecen igual sus calidades (las de tipos marcados) con su audio, en vez de
      // dejar solo el audio como si fuera música. El tamaño de una lista es una estimación (bitrate x duración).
      const videos = checked.filter((x) => x.i.kind !== "audio");
      if (videos.length && !videos.some((x) => x.a.show) && videos.some((x) => x.a.small)) items = checked.filter((x) => !x.a.off).map((x) => x.i);
    }
    if (!items.length) { hiddenList(); return; }
    // En directo también (se graba, como IDM).
    entry = { id: mediaSeq++, kind: r.kind, url: details.url, duration: r.duration, protected: !!r.protected, live: !!r.live,
              media: items.some((i) => i.kind === "video") ? "video" : "audio", items: items.concat(subItems) };
    // Trozos de archivos que ya se ofrecían sueltos: eran de esta lista.
    for (const [k, e] of [...st.items.entries()]) if (e && e.ranged) { st.items.delete(k); await removeEntry(details, e); }
  }
  st.items.set(key, entry);
  await sendEntry(details, entry);
  await flushSubtitles(details, st);
}

// Al marco que lo pidió (el del reproductor); si no tiene content.js (about:blank...), a la página.
async function sendEntry(details, entry) {
  const send = (frameId) => B.tabs.sendMessage(details.tabId, { type: "idm-media", entry }, { frameId });
  try { await send(details.frameId > 0 ? details.frameId : 0); }
  catch (e) { if (details.frameId > 0) { try { await send(0); } catch (e2) { } } }
}

// Pasado MEDIA_TIMING.audioWait sin ningún vídeo a trozos en la pestaña, el audio sí se ofrece solo (puede ser solo
// audio). Nada si la página cambió (tabMedia nuevo), si resultó ser de una lista HLS/DASH o si llegó un vídeo
// (con él ya hay par, o era pequeño y el audio era suyo).
async function showLoneAudio(details, st, key, entry) {
  if (tabMedia.get(details.tabId) !== st || st.items.get(key) !== entry || !entry.hidden) return;
  if ([...st.items.values()].some((e) => e && e.kind === "file" && e.ranged && e.media === "video" && e.t >= entry.t)) return;
  entry.hidden = false; entry.alone = true;
  await sendEntry(details, entry);
  await flushSubtitles(details, st);
}

// Subtítulos sueltos (.vtt/.srt/.ttml) que pide el reproductor: se ofrecen en el panel junto al vídeo ("Subtítulos:
// Español (vtt)"), como IDM. Si aún no hay vídeo en el panel, esperan (ocultos) a que lo haya. Los .vtt de una página
// con lista HLS/DASH son trozos de sus subtítulos: esos los ofrece IDMClone con la propia lista.
async function detectSubtitle(details, sub) {
  if (details.tabId < 0) return;
  const st = mediaState(details.tabId);
  if (st.streaming || st.items.has(sub.url) || st.children.has(sub.url)) return;
  st.items.set(sub.url, null); // en curso
  const cfg = (await getAppConfig()) || {};
  let tab = null;
  try { tab = await B.tabs.get(details.tabId); } catch (e) { }
  const page = (tab && tab.url) || details.documentUrl || details.initiator || "";
  if (cfg.captureOff || cfg.mediaPanel === false || (cfg.mediaExclude && L.urlExcluded(page, cfg.mediaExclude)) || st.streaming) return;
  const entry = { id: mediaSeq++, kind: "subs", url: sub.url, media: "subtitle", hidden: true,
                  items: [{ kind: "subtitle", ext: sub.ext, language: sub.language, url: sub.url }] };
  st.items.set(sub.url, entry);
  if ([...st.items.values()].some((e) => e && !e.hidden && e.media !== "subtitle")) await flushSubtitles(details, st);
}

// Manda al panel los subtítulos sueltos que esperaban a un vídeo.
async function flushSubtitles(details, st) {
  for (const e of [...st.items.values()]) {
    if (!e || e.media !== "subtitle" || !e.hidden) continue;
    e.hidden = false;
    const send = (frameId) => B.tabs.sendMessage(details.tabId, { type: "idm-media", entry: e }, { frameId });
    try { await send(details.frameId > 0 ? details.frameId : 0); }
    catch (x) { if (details.frameId > 0) { try { await send(0); } catch (x2) { } } }
  }
}

if (B.webRequest && B.webRequest.onHeadersReceived) {
  try {
    B.webRequest.onHeadersReceived.addListener((d) => { detectMedia(d).catch(() => {}); },
      { urls: ["<all_urls>"], types: ["sub_frame", "other", "xmlhttprequest", "media", "object"] }, ["responseHeaders"]);
  } catch (e) { console.warn("IDMClone: sin detección de vídeo", e); }
}
if (B.tabs && B.tabs.onUpdated) {
  B.tabs.onUpdated.addListener((tabId, info, tab) => {
    if (info.url) {
      // Página nueva: sus vídeos son otros. En una página que cambia de dirección sin recargarse (history.pushState),
      // el panel también se vacía, como en IDM; un cambio solo de "#..." no cuenta.
      // Solo al marco principal: un iframe-reproductor que no ha cambiado conserva su panel; y la página no lo vacía si
      // está reproduciendo algo (sitios que cambian la dirección mientras se ve el vídeo).
      const now = info.url.split("#")[0], before = tabPage.get(tabId);
      tabPage.set(tabId, now);
      if (info.status === "loading") tabMedia.delete(tabId);
      if (before !== undefined && before !== now) {
        tabMedia.delete(tabId);
        try { Promise.resolve(B.tabs.sendMessage(tabId, { type: "idm-media-reset" }, { frameId: 0 })).catch(() => {}); } catch (e) { }
      }
    }
    // #18: sitios abiertos hace poco (para contestar a las peticiones de cookies de IDMClone).
    if (info.url || info.status === "loading") noteRecentHost(info.url || (tab && tab.url), tab);
  });
  B.tabs.onRemoved.addListener((tabId) => { tabMedia.delete(tabId); tabPage.delete(tabId); });
}

// #9 "No capturar las descargas automáticamente de los reproductores-web" DESMARCADA (como IDM por defecto): un
// archivo de un tipo desmarcado en la lista del panel que está en Tipos de archivo se manda a IDMClone, una vez por
// página, respetando la captura activada, los sitios excluidos y los tamaños mínimos. Límite honesto: una extensión
// MV3 no puede cortar la petición del reproductor, así que el vídeo se sigue reproduciendo en el navegador.
// Solo archivos enteros (no los pedidos a trozos, que suelen ser la mitad de un par vídeo + audio).
async function autoCapture(details, url, contentType, m, allow, cfg, tab, page) {
  if (cfg.mediaNoAutoCapture !== false) return; // marcada, o IDMClone antiguo
  if (inlineSentRecently(url)) return;           // un vídeo abierto como página: ya lo mandó considerInline
  const s = await getSettings();                 // incluye "captura desactivada" y #1
  const dec = L.decide({ url, mime: contentType, totalBytes: m.size > 0 ? m.size : 0 }, s);
  if (!dec.capture) return;
  if (m.size > 0 && m.size < (allow.minKB || 0) * 1024) return;
  const r = await sendToIdm({ url, referrer: page, filename: "", incognito: !!(tab && tab.incognito), size: m.size > 0 ? m.size : 0, auto: true });
  if (!(r && r.ok)) notify(t("notifyUnavailable"), r && r.error); // como IDM: sin aviso si va bien (sale su ventana)
}

async function removeEntry(details, e) {
  try { await B.tabs.sendMessage(details.tabId, { type: "idm-media-remove", url: e.url }); } catch (x) { }
}

// Un par vídeo + audio (los sueltos se quitan del panel).
async function sendPair(details, st, v, a) {
  const vext = v.items[0].ext === ".webm" ? ".webm" : ".mp4";
  const size = v.items[0].size > 0 && a.items[0].size > 0 ? v.items[0].size + a.items[0].size : -1;
  const pairKey = "pair:" + v.url;
  const old = st.items.get(pairKey);
  if (old) await removeEntry(details, old);
  for (const e of [v, a]) await removeEntry(details, e);
  a.alone = false;
  const entry = { id: mediaSeq++, kind: "pair", url: v.url, media: "video",
                  items: [{ kind: "video", video: v.url, audio: a.url, ext: vext, size }] };
  st.items.set(pairKey, entry);
  const send = (frameId) => B.tabs.sendMessage(details.tabId, { type: "idm-media", entry }, { frameId });
  try { await send(details.frameId > 0 ? details.frameId : 0); }
  catch (e) { if (details.frameId > 0) { try { await send(0); } catch (e2) { } } }
  await flushSubtitles(details, st);
}

// El usuario eligió una calidad en el panel.
async function downloadMedia(entry, item, tab) {
  // Como IDM: el título de la página, sin "(3) " delante ni " - Sitio" detrás.
  const title = L.cleanTitle((tab && tab.title) || "", hostOf((tab && tab.url) || ""));
  const filename = L.mediaFileName(title || "video", item.ext || ".mp4");
  const incognito = !!(tab && tab.incognito);
  const referrer = (tab && tab.url) || "";
  if (item.kind === "subtitle") return downloadSubtitle(entry, item, title, referrer, incognito);
  if (entry.kind === "file") return sendToIdm({ url: entry.url, referrer, filename, incognito });
  let cookies = "";
  try { cookies = await gatherCookies(entry.url, incognito); } catch (e) { }
  const msg = { type: "download", url: item.video && entry.kind === "pair" ? item.video : entry.url, referrer, cookies, filename,
                userAgent: navigator.userAgent, incognito,
                mediaKind: entry.kind, mediaVideo: item.video || "", mediaAudio: item.audio || "" };
  const proxy = await proxyFor(msg.url);
  if (proxy) msg.browserProxy = proxy;
  return hostRequest(msg, DOWNLOAD_REPLY_MS);
}

// Subtítulos elegidos en el panel: de una lista HLS/DASH (la lista y la clave de la pista) o un archivo suelto
// (<track> del vídeo o pedido por el reproductor). IDMClone los guarda junto al vídeo: "Título.es.vtt".
async function downloadSubtitle(entry, item, title, referrer, incognito) {
  const list = entry.kind === "hls" || entry.kind === "dash";
  const url = String((list ? entry.url : (item.url || entry.url)) || "");
  if (!/^https?:\/\//i.test(url)) return { ok: false, error: t("subtitleNoUrl") };
  let cookies = "";
  try { cookies = await gatherCookies(url, incognito); } catch (e) { }
  const msg = { type: "download", url, referrer, cookies, filename: L.subtitleFileName(title || "video", item),
                userAgent: navigator.userAgent, incognito, mediaKind: "subs", mediaVideo: list ? String(item.subtitle || "") : "", mediaAudio: "" };
  const proxy = await proxyFor(url);
  if (proxy) msg.browserProxy = proxy;
  return hostRequest(msg, DOWNLOAD_REPLY_MS);
}

// ---------------------------------------------------------------------------------------------
// Teclas especiales (como IDM; IDMClone > Opciones > General > "Teclas..."): mientras se mantiene pulsada la tecla
// "para no descargar con IDMClone" (por defecto Supr) la descarga sigue en el navegador; con la de "forzar la descarga"
// (Insert, desactivada por defecto) va a IDMClone sea del tipo que sea. Valen para CUALQUIER descarga que empiece
// mientras están pulsadas (o justo después de soltarlas): un enlace, un formulario (con su POST), una descarga que
// lanza la página por JavaScript o un PDF/vídeo que el navegador abriría él mismo; la descarga sale del navegador tal
// cual (redirecciones, cuerpo POST, referer), no se repite desde el enlace.
// content.js avisa de cuándo se pulsan y se sueltan (solo teclas de verdad, isTrusted, y solo las de las combinaciones
// configuradas: nunca lo que se escribe). Como IDM, el estado es de todo el navegador; al perder el foco la ventana, se sueltan.
const keyState = {}; // tecla -> { down, from, to, seen }
// grace: tras soltarla aún vale (la descarga empieza un poco después del clic); stale: sin noticias de una tecla "pulsada"
// (content.js repite el aviso cada segundo mientras se mantiene) se da por suelta. Ver L.keysHeld.
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
  const a = appCfg || {};
  return { prevent: typeof a.preventKeys === "string" ? a.preventKeys : "del", force: typeof a.forceKeys === "string" ? a.forceKeys : "" };
}

function keysHeld(kind) {
  return L.keysHeld(specialKeys()[kind], keyState, Date.now(), KEY_TIMING.grace, KEY_TIMING.stale);
}

// Además, el enlace pulsado con la tecla (content.js): vale aunque la descarga empiece unos segundos después de soltarla
// (redirecciones lentas). prevented: "no descargar con IDMClone"; forcedLinks: "forzar la descarga".
const prevented = new Map();
const forcedLinks = new Map();
const PREVENT_MS = 8000;

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
// "url": el enlace pulsado con la tecla (también una página web); "keys"/"referrer": otra cosa que empezó con la tecla pulsada.
function forcedBy(item) { return markedLink(forcedLinks, item) || (keysHeld("force") ? "keys" : ""); }

// ---------------------------------------------------------------------------------------------
// Cookies del sitio, del almacén correcto (normal o incógnito).
async function gatherCookies(url, incognito) {
  const q = { url };
  if (incognito && B.cookies.getAllCookieStores) {
    try {
      const stores = await B.cookies.getAllCookieStores();
      const priv = stores.find((s) => s.incognito) || stores.find((s) => s.id === "1" || s.id === "firefox-private");
      if (priv) q.storeId = priv.id;
    } catch (e) { }
  }
  const all = await B.cookies.getAll(q);
  return (all || []).map((c) => c.name + "=" + c.value).join("; ");
}

async function sendToIdm(info) {
  let cookies = "";
  try { cookies = await gatherCookies(info.url, info.incognito); } catch (e) { }
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
  return hostRequest(msg, DOWNLOAD_REPLY_MS);
}

// #18 IDMClone (Grabber) pide las cookies de una dirección: las que el navegador mandaría a ESA dirección
// (incluidas las HttpOnly), del almacén normal; si no hay, de otro que no sea privado (contenedores de Firefox).
// No se guardan en ningún sitio: se contestan y se olvidan.
async function cookiesForUrl(url) {
  let all = await B.cookies.getAll({ url });
  if ((!all || !all.length) && B.cookies.getAllCookieStores) {
    let stores = [];
    try { stores = await B.cookies.getAllCookieStores(); } catch (e) { }
    for (const st of stores || []) {
      if (!st || st.incognito || st.id === "0" || st.id === "1" || st.id === "firefox-default" || st.id === "firefox-private") continue;
      try { all = await B.cookies.getAll({ url, storeId: st.id }); } catch (e) { all = []; }
      if (all && all.length) break;
    }
  }
  return (all || []).map((c) => c.name + "=" + c.value).join("; ");
}

// Para que el canal de cookies no sirva de "oráculo" (otro programa del mismo usuario que se hiciera pasar por
// IDMClone podría pedir las cookies HttpOnly de cualquier sitio), solo se contesta por un sitio que el usuario tiene
// abierto en una pestaña normal o que abrió (o al que navegó) en los últimos 15 minutos: el mismo servidor o su gemelo
// con/sin "www.". Nunca cuentan las pestañas de incógnito. Es el caso del inicio de sesión manual del Grabber: IDMClone
// abre la página en el navegador, el usuario inicia sesión y pulsa Continuar. Si no, se contesta sin cookies y con
// reason:"notopen" (IDMClone dice que no hay cookies y ofrece pegarlas).
const RECENT_HOST_MS = 15 * 60 * 1000;
const RECENT_HOST_MAX = 300;
const recentHosts = new Map(); // servidor -> última vez que se abrió o se navegó a él (pestañas normales)
let recentLoaded = null;

function siteHostOf(url) {
  try { const u = new URL(String(url || "")); return /^https?:$/.test(u.protocol) ? u.hostname.toLowerCase().replace(/\.$/, "") : ""; }
  catch (e) { return ""; }
}
function sameSite(a, b) { return !!a && !!b && (a === b || a === "www." + b || b === "www." + a); }

function noteRecentHost(url, tab) {
  if (!url || !tab || tab.incognito) return;
  const h = siteHostOf(url);
  if (!h) return;
  recentHosts.delete(h);
  recentHosts.set(h, Date.now());
  while (recentHosts.size > RECENT_HOST_MAX) recentHosts.delete(recentHosts.keys().next().value);
  // El service worker se duerme y pierde la memoria: se copia en storage.session (solo en memoria del navegador,
  // se borra al cerrarlo; las páginas no pueden leerlo).
  try { if (B.storage.session) Promise.resolve(B.storage.session.set({ recentHosts: [...recentHosts] })).catch(() => {}); } catch (e) { }
}

function loadRecentHosts() {
  if (!recentLoaded) recentLoaded = (async () => {
    try {
      if (!B.storage.session) return;
      const s = await B.storage.session.get({ recentHosts: [] });
      for (const x of (s && Array.isArray(s.recentHosts) ? s.recentHosts : [])) {
        if (!Array.isArray(x) || typeof x[0] !== "string" || typeof x[1] !== "number") continue;
        if (!recentHosts.has(x[0]) || recentHosts.get(x[0]) < x[1]) recentHosts.set(x[0], x[1]);
      }
    } catch (e) { }
  })();
  return recentLoaded;
}

async function cookieHostAllowed(url) {
  const h = siteHostOf(url);
  if (!h) return false;
  await loadRecentHosts();
  const now = Date.now();
  for (const [k, t] of recentHosts) if (now - t < RECENT_HOST_MS && sameSite(k, h)) return true;
  let tabs = [];
  try { tabs = await B.tabs.query({}); } catch (e) { }
  return (tabs || []).some((t) => t && !t.incognito && (sameSite(siteHostOf(t.url), h) || sameSite(siteHostOf(t.pendingUrl), h)));
}

async function answerCookies(msg) {
  const url = typeof msg.url === "string" ? msg.url : "";
  if (!Number.isInteger(msg.req) || url.length > 4096 || !/^https?:\/\/[^/?#]+/i.test(url)) return;
  let cookies = "", ok = false, reason = "";
  let allowed = false;
  try { allowed = await cookieHostAllowed(url); } catch (e) { }
  if (!allowed) reason = "notopen";
  else { try { cookies = await cookiesForUrl(url); ok = true; } catch (e) { reason = "error"; } }
  const reply = { type: "cookies", req: msg.req, url, ok, cookies, userAgent: navigator.userAgent };
  if (reason) reply.reason = reason;
  await hostRequest(reply, PING_REPLY_MS);
}

function notify(title, message) {
  try {
    B.notifications.create("", { type: "basic", iconUrl: "icons/icon48.png", title, message: String(message || "").substring(0, 200) });
  } catch (e) { }
}

// ---------------------------------------------------------------------------------------------
// Captura automática. En Chromium se decide en onDeterminingFilename (ya se conoce el nombre final
// del servidor); en Firefox, en onCreated (allí ya viene). La descarga se PAUSA mientras IDMClone
// confirma: si la acepta se cancela en el navegador; si no, se reanuda y sigue allí (como IDM).
const decided = new Set();
const waitingName = new Map();
const prePaused = new Set();
const hasDetermining = !!(B.downloads && B.downloads.onDeterminingFilename);
const SUGGEST_HOLD_MS = PING_REPLY_MS + DOWNLOAD_REPLY_MS + 5000; // getconfig + envío a IDMClone, con margen

// ¿Podría ser para IDMClone? Se decide antes de conocer el nombre final: si la URL ya dice que es
// un tipo no vigilado (p. ej. foto.png con image/png) no hace falta pausar; si no está claro, sí.
async function mightCapture(item) {
  const s = await getSettings();
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

  const s = await getSettings();
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
    notify(t("notifyStaysInBrowser"), (r && r.error) || t("idmNotAvailable"));
    dec.sent = false;
    dec.error = r && r.error;
  }
  return dec;
}

// ---------------------------------------------------------------------------------------------
// Enlaces que el navegador abriría él mismo (un PDF en su visor, un .mp4 en su reproductor): no pasan por la API de
// descargas. Como IDM: si son de un tipo de la lista, van a IDMClone. Firefox corta la respuesta (webRequestBlocking,
// como IDM); Chromium (MV3) no puede, así que la pestaña vuelve a la página anterior, o se cierra si se abrió solo para
// eso. Solo la página entera (no los PDF o vídeos incrustados en un marco) y nunca un "attachment" (eso sí es una
// descarga y va por la API de descargas).
const inlineSent = new Map(); // url sin "#" -> { t, p: promesa de si IDMClone la aceptó }
const INLINE_DEDUP_MS = 4000;   // solo para los avisos repetidos de una misma navegación (no "bloquea" volver a pulsar)

function noHash(u) { return String(u || "").split("#")[0]; }

function inlineEntry(...urls) {
  const now = Date.now();
  // Mientras IDMClone no contesta, la entrada sigue (si no, se mandaría otra vez); ya contestada, dura unos segundos.
  for (const [u, e] of inlineSent) if (e.settled && now - e.t > INLINE_DEDUP_MS) inlineSent.delete(u);
  for (const u of urls) { const k = u && noHash(u); if (k && inlineSent.has(k)) return Object.assign({ key: k }, inlineSent.get(k)); }
  return null;
}
function inlineSentRecently(...urls) { return !!inlineEntry(...urls); }

// Pestañas recién abiertas (un enlace con target=_blank o con la rueda): Firefox las cierra si se abrieron solo para el archivo.
const newTabs = new Map(); // tabId -> cuándo se abrió
if (B.tabs && B.tabs.onCreated) B.tabs.onCreated.addListener((t) => {
  if (!t || t.id === undefined) return;
  newTabs.set(t.id, Date.now());
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
  // Con la tecla "forzar" también cualquier otra cosa que el navegador muestre él mismo (imagen, texto, JSON...), pero no
  // una página web (como IDM: mientras se mantiene la tecla no toma las páginas), salvo que sea el enlace pulsado con
  // ella. Lo que el navegador no sabe mostrar (zip, octet-stream...) se convierte en una descarga: va por consider().
  const page = /^\s*(text\/html|application\/xhtml\+xml)\s*(;|$)/i.test(ct);
  const viewable = shown || page || /^\s*(text\/|image\/|application\/(json|xml|javascript|x-javascript|ecmascript)\s*(;|$)|[a-z]+\/[^;\s]+\+(json|xml)\s*(;|$))/i.test(ct);
  if (!viewable) return null;
  // Lo normal: nada que mirar. (keyState guarda las teclas aunque ya estén sueltas: hay que preguntar si "forzar" sigue
  // pulsada; si no, en Firefox cada página esperaría a getSettings en su escucha bloqueante.)
  if (!shown && !forcedLinks.size && !keysHeld("force")) return null;
  const size = parseInt(header(details, "content-length"), 10) || 0;
  const s = await getSettings();
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

// Chromium: la pestaña ya está abriendo el archivo. Se espera a que lo muestre (si se volviera atrás antes, se iría a la
// página anterior a la del enlace) y se vuelve atrás; sin historial (pestaña abierta para el enlace), se cierra.
async function leaveInline(details) {
  let shown = false;
  for (let i = 0; i < 40 && !shown; i++) {
    let t = null;
    try { t = await B.tabs.get(details.tabId); } catch (e) { return; }
    const want = noHash(details.url);
    if (t && noHash(t.url) === want && !t.pendingUrl) shown = true;
    else if (t && noHash(t.url) !== want && noHash(t.pendingUrl) !== want && i > 5) return; // el usuario ya se fue a otra página
    else await new Promise((res) => setTimeout(res, 250));
  }
  if (!shown) return; // nunca llegó a mostrarlo: no se toca la pestaña
  try { await B.tabs.goBack(details.tabId); return; } catch (e) { }
  try { await B.tabs.remove(details.tabId); } catch (e) { }
}

if (B.webRequest && B.webRequest.onHeadersReceived) {
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
    // Como IDM: el navegador espera a que se decida. Si va a IDMClone se cancela antes de que aparezca su
    // "Guardar como" o su aviso de descarga; si no, sigue con el nombre que él elija (solo lo leemos).
    // Por si IDMClone no contesta nunca, se suelta igual pasado SUGGEST_HOLD_MS.
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

// --- Menú contextual: "Descargar con IDMClone" ---
// #2 Opciones > General > "Personalizar el menú contextual del navegador" (Chromium y Firefox por separado):
//   link: "Descargar con IDMClone" (enlace, imagen/vídeo/audio y página) y "los seleccionados"; all: "todos los enlaces".
// Los menús persisten entre reinicios del service worker: solo se rehacen al instalar, al arrancar el navegador
// o cuando IDMClone manda opciones distintas de las guardadas.
const MENU_ALL = { link: true, all: true }; // IDMClone antiguo (sin opciones): los cuatro, como hasta ahora
let menuChain = Promise.resolve();

function createMenus(flags) {
  const f = flags || MENU_ALL;
  const mk = (o) => {
    try { B.contextMenus.create(o, () => { void (B.runtime.lastError); }); } catch (e) { }
  };
  menuChain = menuChain.then(async () => {
    try { await B.contextMenus.removeAll(); } catch (e) { }
    if (f.link) {
      mk({ id: "idmclone-link", title: t("menuDownload"), contexts: ["link"] });
      mk({ id: "idmclone-media", title: t("menuDownload"), contexts: ["image", "video", "audio"] });
      // Como IDM: sobre la página (o un marco) descarga esa dirección, p. ej. un PDF abierto en el navegador.
      // Solo "page" (también vale para el fondo de un marco, con frameUrl): "frame" saldría además sobre los enlaces y
      // las selecciones de dentro de un iframe.
      mk({ id: "idmclone-page", title: t("menuDownload"), contexts: ["page"] });
      // En IDM "los enlaces seleccionados" va con "Descargar con IDM" (no con "todos los enlaces").
      mk({ id: "idmclone-selected", title: t("menuSelected"), contexts: ["selection"] });
    }
    if (f.all) mk({ id: "idmclone-all", title: t("menuAll"), contexts: ["page"] });
  }).catch(() => {});
  return menuChain;
}

async function savedMenuFlags() {
  try { return (await B.storage.local.get({ menuFlags: null })).menuFlags; } catch (e) { return null; }
}

async function applyMenuFlags(flags, force) {
  if (!flags) return; // IDMClone antiguo: se quedan como estén
  const saved = await savedMenuFlags();
  if (!force && saved && saved.link === flags.link && saved.all === flags.all) return;
  try { await B.storage.local.set({ menuFlags: { link: flags.link, all: flags.all } }); } catch (e) { }
  await createMenus(flags);
}

async function initMenus() { await createMenus(await savedMenuFlags()); }

// Al instalar o actualizar, Chromium no pone content.js en las pestañas ya abiertas (Firefox sí): sin esto, el panel de
// vídeo y las teclas especiales no funcionarían en ellas hasta recargarlas. IDM hace lo mismo. Se salta la pestaña que ya
// lo tiene funcionando; las que no se pueden tocar (páginas del navegador, tienda...) fallan sin más.
async function injectIntoOpenTabs() {
  if (IS_FIREFOX || !B.scripting || !B.scripting.executeScript || !B.tabs || !B.tabs.query) return;
  let tabs = [];
  try { tabs = await B.tabs.query({ url: ["http://*/*", "https://*/*"] }); } catch (e) { return; }
  for (const t of tabs || []) {
    if (!t || t.discarded) continue;
    try {
      // Marco a marco: un iframe cargado después de instalar ya lo tiene aunque la página no.
      const r = await B.scripting.executeScript({ target: { tabId: t.id, allFrames: true }, func: () => !!(self.__idmcloneAlive && self.__idmcloneAlive()) });
      const frameIds = (r || []).filter((x) => x && !x.result && typeof x.frameId === "number").map((x) => x.frameId);
      if (frameIds.length) await B.scripting.executeScript({ target: { tabId: t.id, frameIds }, files: ["logic.js", "content.js"] });
    } catch (e) { }
  }
}

B.runtime.onInstalled.addListener(() => { initMenus(); injectIntoOpenTabs().catch(() => {}); });
if (B.runtime.onStartup) B.runtime.onStartup.addListener(() => { initMenus(); });

// Recoge los enlaces de un marco (o de su selección), como IDM: <a href>, <area href>, imágenes, vídeo y audio, y lo
// mismo de los iframes sin dirección propia (about:blank, escritos por la página) a los que el marco llega
// (contentDocument: mismo origen). Con selección, también el texto seleccionado y el de los <textarea> tocados por ella
// (o el trozo seleccionado dentro del que tiene el foco), para buscar en él direcciones escritas (L.textUrls, en el fondo).
// Se ejecuta DENTRO de la página, solo cuando el usuario elige la opción del menú; no lee nada más.
// Devuelve { origin, links: [{ url, text }], text }.
function collectLinks(selectionOnly) {
  const MAX = 5000, TEXT_MAX = 200000;
  const out = [];
  const seen = new Set();
  let text = "";
  const add = (url, label) => {
    if (out.length >= MAX || typeof url !== "string" || !/^(https?|ftps?):/i.test(url) || seen.has(url)) return;
    seen.add(url);
    out.push({ url, text: String(label || "").replace(/\s+/g, " ").trim().substring(0, 200) });
  };
  const addText = (s) => { if (s && text.length < TEXT_MAX) text += String(s).substring(0, TEXT_MAX - text.length) + "\n"; };
  const scan = (doc, win, depth) => {
    let sel = null;
    if (selectionOnly) {
      try { sel = win.getSelection(); } catch (e) { sel = null; }
      // Texto seleccionado dentro de un <textarea> o <input> con el foco (la selección del documento no lo ve).
      const f = doc.activeElement;
      try {
        if (f && /^(textarea|input)$/i.test(f.tagName || "") && f.selectionEnd > f.selectionStart) addText(String(f.value).substring(f.selectionStart, f.selectionEnd));
      } catch (e) { }
      if (!sel || sel.rangeCount === 0 || (sel.isCollapsed && !String(sel).trim())) sel = null;
      else addText(String(sel));
    }
    const inSel = (el) => !selectionOnly || (!!sel && sel.containsNode(el, true));
    if (selectionOnly && !sel) return; // nada seleccionado en este documento (sí puede haberlo en el foco, arriba)
    for (const a of doc.querySelectorAll("a[href], area[href]")) {
      if (out.length >= MAX) break;
      if (inSel(a)) add(a.href, a.innerText || a.alt || a.title);
    }
    for (const m of doc.querySelectorAll("img[src], video[src], audio[src], source[src]")) {
      if (out.length >= MAX) break;
      if (inSel(m)) add(m.src, m.alt || m.title);
    }
    if (selectionOnly) for (const ta of doc.querySelectorAll("textarea")) if (inSel(ta)) addText(ta.value);
    if (depth >= 3) return;
    for (const fr of doc.querySelectorAll("iframe, frame")) {
      let d = null;
      try { d = fr.contentDocument; } catch (e) { d = null; } // de otro origen: null o excepción
      const src = fr.getAttribute ? (fr.getAttribute("src") || "") : "";
      // Los que tienen dirección propia ya los lee executeScript (allFrames) por su cuenta.
      if (d && (!src || /^(about:|javascript:)/i.test(src))) scan(d, fr.contentWindow || win, depth + 1);
    }
  };
  scan(document, window, 0);
  let origin = "";
  try { origin = location.origin; } catch (e) { }
  return { origin, links: out, text };
}

// Une lo de cada marco: el marco principal primero y, de los demás, solo los del mismo origen (como IDM, que solo llega a
// los marcos de la página; así no se cuelan los enlaces de los anuncios de otros sitios). Admite el formato antiguo (lista).
function mergeFrameLinks(res, sameOriginOnly, extraText) {
  const frames = (Array.isArray(res) ? res : []).filter((r) => r && r.result)
    .map((r) => ({ frameId: typeof r.frameId === "number" ? r.frameId : 0, v: Array.isArray(r.result) ? { origin: "", links: r.result, text: "" } : r.result }));
  frames.sort((a, b) => a.frameId - b.frameId);
  const top = frames.find((f) => f.frameId === 0) || frames[0];
  const all = [];
  let text = typeof extraText === "string" ? extraText.substring(0, 200000) : "";
  for (const f of frames) {
    if (sameOriginOnly && f !== top && (!top || !f.v.origin || f.v.origin !== top.v.origin)) continue;
    if (Array.isArray(f.v.links)) for (const l of f.v.links) all.push(l);
    if (typeof f.v.text === "string" && text.length < 400000) text += "\n" + f.v.text.substring(0, 200000);
  }
  // Direcciones escritas en el texto seleccionado (no las que ya son enlaces).
  for (const u of L.textUrls(text, 1000)) all.push({ url: u, text: "" });
  return L.sanitizeLinks(all, 1000);
}

async function sendLinks(tab, selectionOnly, frameId, selectionText) {
  let links = [];
  try {
    // Selección: en el marco donde se hizo clic (una selección dentro de un iframe está en ese iframe, no en la página).
    // Todos los enlaces: todos los marcos de la página (y se quedan los del mismo origen).
    const target = selectionOnly ? (frameId > 0 ? { tabId: tab.id, frameIds: [frameId] } : { tabId: tab.id }) : { tabId: tab.id, allFrames: true };
    const res = await B.scripting.executeScript({ target, func: collectLinks, args: [!!selectionOnly] });
    links = mergeFrameLinks(res, !selectionOnly, selectionOnly ? selectionText : "");
  } catch (e) {
    notify("IDMClone", t("cantReadLinks", String(e && e.message || e)));
    return null;
  }
  if (!links.length) { notify("IDMClone", t(selectionOnly ? "selectionNoLinks" : "pageNoLinks")); return null; }
  return sendLinkList(tab, links, !!selectionOnly, tab.url || "");
}

// La lista de enlaces a IDMClone (ventana para elegir cuáles descargar), con las cookies de cada servidor.
async function sendLinkList(tab, links, selection, referrer) {
  const cookies = {};
  for (const host of [...new Set(links.map((l) => { try { return new URL(l.url).host; } catch (e) { return ""; } }))].filter(Boolean).slice(0, 30)) {
    try { const c = await gatherCookies("https://" + host + "/", tab.incognito); if (c) cookies[host] = c; } catch (e) { }
  }
  const r = await hostRequest({ type: "links", links, referrer: referrer || "", userAgent: navigator.userAgent, cookies, selection: !!selection }, DOWNLOAD_REPLY_MS);
  if (!r || !r.ok) notify(t("notifyUnavailable"), r && r.error);
  return r;
}

B.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId === "idmclone-all" || info.menuItemId === "idmclone-selected") {
    await sendLinks(tab, info.menuItemId === "idmclone-selected", info.frameId, info.selectionText);
    return;
  }
  const url = info.menuItemId === "idmclone-page" ? (info.frameUrl || info.pageUrl || (tab && tab.url)) : (info.linkUrl || info.srcUrl);
  if (!url) return;
  if (!L.isCapturableScheme(url)) { notify("IDMClone", t("linkNotDownloadable", String(url).split(":")[0])); return; }
  const r = await sendToIdm({ url, referrer: tab ? tab.url : "", filename: "", incognito: !!(tab && tab.incognito) });
  if (!(r && r.ok)) notify(t("notifyUnavailable"), r && r.error); // como IDM: sin aviso si va bien (sale su ventana)
});

// --- Mensajes del popup ---
B.runtime.onMessage.addListener((req, sender, sendResponse) => {
  if (req && req.type === "ping-host") {
    hostRequest({ type: "ping" }, PING_REPLY_MS).then(sendResponse);
    return true;
  }
  // Teclas especiales (content.js, solo de una página: sender.tab). Una tecla, pulsada o suelta, o "todas sueltas"
  // (la ventana perdió el foco).
  if (req && req.type === "idm-keys") {
    if (!(sender && sender.tab)) return false;
    const now = Date.now();
    if (req.reset === true) releaseKeys(now);
    else if (typeof req.key === "string" && typeof req.down === "boolean") noteKey(req.key, req.down, now);
    return false;
  }
  // El enlace pulsado con una tecla especial: la descarga que salga de él (aunque tarde unos segundos) sigue la tecla.
  if (req && (req.type === "prevent-capture" || req.type === "force-mark") && typeof req.url === "string" && sender && sender.tab) {
    const map = req.type === "prevent-capture" ? prevented : forcedLinks;
    map.set(req.url, { t: Date.now(), referrer: typeof req.referrer === "string" ? req.referrer : "" });
    if (map.size > 50) map.delete(map.keys().next().value);
    return false;
  }
  if (req && req.type === "media-download" && req.entry && req.item) {
    const tab = sender && sender.tab;
    downloadMedia(req.entry, req.item, tab).then((r) => {
      if (!(r && r.ok)) notify(t("notifyUnavailable"), r && r.error); // como IDM: sin aviso si va bien (sale su ventana)
      sendResponse(r);
    });
    return true;
  }
  if (req && req.type === "selection-links") {
    // #11 Panel de los enlaces seleccionados (content.js), tras un clic del usuario. La lista viene de una página
    // (puede ser hostil): solo http/ftp, como mucho 1000, textos recortados.
    const tab = sender && sender.tab;
    if (!tab) return false;
    const links = L.sanitizeLinks(req.links, 1000);
    if (!links.length) { sendResponse({ ok: false, error: t("selectionNoLinks") }); return false; }
    const ref = sender.url && /^https?:\/\//i.test(sender.url) ? sender.url : (tab.url || "");
    sendLinkList(tab, links, true, ref).then(sendResponse);
    return true;
  }
  if (req && req.type === "media-options") {
    hostRequest({ type: "showoptions", tab: "General" }, DOWNLOAD_REPLY_MS).then(sendResponse);
    return true;
  }
  if (req && req.type === "get-settings") {
    appCfgAt = 0; // el popup quiere lo último
    getSettings().then(sendResponse);
    return true;
  }
  if (req && req.type === "open-options") {
    hostRequest({ type: "showoptions", tab: "Tipos de archivo" }, DOWNLOAD_REPLY_MS).then(sendResponse);
    return true;
  }
  return false;
});

// Para las pruebas con Node (no hace nada en el navegador).
if (typeof module !== "undefined" && module.exports) module.exports = { consider, considerInline, leaveInline, hostRequest, getSettings, getAppConfig, fetchAppConfig, checkExtUpdate, rememberHeaders, rememberPost, collectLinks, mergeFrameLinks, sendLinks, sendLinkList, keyState, KEY_TIMING, MEDIA_TIMING, headerCache, detectMedia, detectSubtitle, downloadMedia, tabMedia, createMenus, applyMenuFlags, initMenus, menuDone: () => menuChain, proxyFor, cookiesForUrl, answerCookies, cookieHostAllowed, noteRecentHost, recentHosts, IS_FIREFOX, _reset: () => { decided.clear(); waitingName.clear(); appCfg = null; appCfgAt = 0; prevented.clear(); forcedLinks.clear(); for (const k of Object.keys(keyState)) delete keyState[k]; postBodies.clear(); tabMedia.clear(); proxyByHost.clear(); recentHosts.clear(); recentLoaded = null; } };
else {
  // Al arrancar el navegador: leer ya las teclas especiales de IDMClone para las páginas (content.js).
  setTimeout(() => { getAppConfig().catch(() => {}); }, 3000);
  // También con el icono en error: así la "X" se quita sola cuando IDMClone vuelve a responder.
  setInterval(() => { if (port || hostDown) getAppConfig().catch(() => {}); }, APP_CFG_TTL_MS + 1000);
}
