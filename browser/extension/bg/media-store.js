// IDMClone Integration — fondo: vídeos detectados en cada pestaña y su paso a las páginas (panel "Descargar este
// video", como IDM) y a IDMClone cuando el usuario elige uno.
//
// Lo detectado es de la pestaña y de su DOCUMENTO, no de su dirección: una página que cambia de dirección sin recargarse
// (history.pushState: X, Facebook, YouTube...) sigue teniendo sus vídeos; solo se vacía al cargar otro documento (lo
// avisa content/bridge.js con una ficha nueva) o al cerrar la pestaña. Cada panel va unido a su <video> en la página
// (content/tracker.js): un vídeo que ya no está en pantalla no enseña panel, y vuelve a enseñarlo si reaparece.
(function (root) {
  "use strict";
  const G = root.IDMBG;
  const { B, L, t } = G;

  // tabId -> { doc, items: Map(url -> entrada | null en curso), children: Set(listas de cada calidad), streaming, probes }
  const tabMedia = new Map();
  // audioWait: un audio pedido a trozos sin vídeo aún espera (oculto) a su vídeo antes de ofrecerse solo: el
  // reproductor (Facebook, Instagram...) suele pedir el audio un poco antes que el vídeo, y no es música.
  const MEDIA_TIMING = { audioWait: 2500 };
  let mediaSeq = 1;
  const nextId = () => mediaSeq++;

  function mediaState(tabId) {
    let s = tabMedia.get(tabId);
    if (!s) { s = { doc: "", items: new Map(), children: new Set(), probes: new Set(), siteHints: new Set() }; tabMedia.set(tabId, s); }
    return s;
  }

  // Pistas de una entrada para unirla con su <video> (lib/sites.js): de su dirección y de las de sus calidades.
  function entryHints(entry) {
    const hs = new Set(entry.hints || []);
    for (const u of [entry.url].concat((entry.items || []).map((i) => i && (i.url || i.video)))) for (const h of L.urlHints(u)) hs.add(h);
    return [...hs];
  }

  // Al marco que lo pidió (el del reproductor); si no tiene content scripts (about:blank...), a la página.
  async function sendEntry(details, entry) {
    entry.at = entry.at || Date.now();
    entry.hints = entryHints(entry);
    // El mismo vídeo ya lo dio la web del sitio (bg/sites.js, con más calidades): lo de la red sobra.
    const st = tabMedia.get(details.tabId);
    if (entry.source !== "site" && st && entry.hints.some((h) => st.siteHints.has(h))) { entry.hidden = true; return; }
    const msg = { type: "idm-media", entry };
    const send = (frameId) => B.tabs.sendMessage(details.tabId, msg, { frameId });
    try { await send(details.frameId > 0 ? details.frameId : 0); }
    catch (e) { if (details.frameId > 0) { try { await send(0); } catch (e2) { } } }
  }

  async function removeEntry(details, e) {
    try { await B.tabs.sendMessage(details.tabId, { type: "idm-media-remove", url: e.url }); } catch (x) { }
  }

  // Manda al panel los subtítulos sueltos que esperaban a un vídeo.
  async function flushSubtitles(details, st) {
    for (const e of [...st.items.values()]) {
      if (!e || e.media !== "subtitle" || !e.hidden) continue;
      e.hidden = false;
      await sendEntry(details, e);
    }
  }

  // ¿La pestaña ya tiene algo visible que no sean subtítulos?
  function hasVisible(st) { return [...st.items.values()].some((e) => e && !e.hidden && e.media !== "subtitle"); }

  // Documento nuevo en la pestaña (content/bridge.js al cargar, en el marco principal): sus vídeos son otros.
  G.onMessage["media-hello"] = (req, sender) => {
    const tab = sender && sender.tab;
    if (!tab || typeof req.doc !== "string" || sender.frameId) return false;
    const st = tabMedia.get(tab.id);
    if (st && st.doc && st.doc !== req.doc) tabMedia.delete(tab.id);
    mediaState(tab.id).doc = req.doc;
    return false;
  };

  // La página cambió de dirección sin recargarse: se avisa al marco principal (content/bridge.js) para que el panel
  // general (vídeos sin <video> conocido) se quede solo con lo nuevo. Los vídeos con su elemento siguen con él.
  const tabPage = new Map(); // tabId -> dirección de la página sin "#..."
  function mediaTabUpdated(tabId, info) {
    if (!info.url) return;
    const now = String(info.url).split("#")[0], before = tabPage.get(tabId);
    tabPage.set(tabId, now);
    if (before !== undefined && before !== now) {
      try { Promise.resolve(B.tabs.sendMessage(tabId, { type: "idm-media-nav", url: now }, { frameId: 0 })).catch(() => {}); } catch (e) { }
    }
  }
  function mediaTabRemoved(tabId) { tabMedia.delete(tabId); tabPage.delete(tabId); }

  // ---- El usuario eligió una calidad en el panel ----
  // La entrada se busca en lo que detectó el fondo (por su número): lo que manda la página solo dice cuál.
  function storedEntry(tabId, entry) {
    const st = tabMedia.get(tabId);
    if (!st || !entry) return null;
    for (const e of st.items.values()) if (e && e.id === entry.id && e.url === entry.url) return e;
    return null;
  }

  async function downloadMedia(entry, item, tab) {
    // Como IDM: el título de la página, sin "(3) " delante ni " - Sitio" detrás.
    // (Vimeo y YouTube dan el título del vídeo: ese manda.)
    const title = String(entry.title || "").trim() || L.cleanTitle((tab && tab.title) || "", G.hostOf((tab && tab.url) || ""));
    const filename = L.mediaFileName(title || "video", item.ext || ".mp4");
    const incognito = !!(tab && tab.incognito);
    const referrer = (tab && tab.url) || "";
    if (item.kind === "subtitle") return downloadSubtitle(entry, item, title, referrer, incognito);
    if (entry.kind === "file") return G.sendToIdm({ url: entry.url, referrer, filename, incognito });
    // Datos del sitio: un archivo con su audio (X, Vimeo, Instagram) o vídeo y audio aparte que IDMClone junta.
    if (entry.kind === "site" && !item.audio) return G.sendToIdm({ url: item.url || entry.url, referrer, filename, incognito });
    if (entry.kind === "site") entry = Object.assign({}, entry, { kind: "pair" });
    if (entry.kind === "youtube" && G.downloadYouTube) return G.downloadYouTube(entry, item, tab, filename);
    let cookies = "";
    try { cookies = await G.gatherCookies(entry.url, incognito); } catch (e) { }
    const msg = { type: "download", url: item.video && entry.kind === "pair" ? item.video : entry.url, referrer, cookies, filename,
                  userAgent: navigator.userAgent, incognito,
                  mediaKind: entry.kind, mediaVideo: item.video || "", mediaAudio: item.audio || "" };
    const proxy = await G.proxyFor(msg.url);
    if (proxy) msg.browserProxy = proxy;
    return G.hostRequest(msg, G.DOWNLOAD_REPLY_MS);
  }

  // Subtítulos elegidos en el panel: de una lista HLS/DASH (la lista y la clave de la pista) o un archivo suelto
  // (<track> del vídeo o pedido por el reproductor). IDMClone los guarda junto al vídeo: "Título.es.vtt".
  async function downloadSubtitle(entry, item, title, referrer, incognito) {
    const list = entry.kind === "hls" || entry.kind === "dash";
    const url = String((list ? entry.url : (item.url || entry.url)) || "");
    if (!/^https?:\/\//i.test(url)) return { ok: false, error: t("subtitleNoUrl") };
    let cookies = "";
    try { cookies = await G.gatherCookies(url, incognito); } catch (e) { }
    const msg = { type: "download", url, referrer, cookies, filename: L.subtitleFileName(title || "video", item),
                  userAgent: navigator.userAgent, incognito, mediaKind: "subs", mediaVideo: list ? String(item.subtitle || "") : "", mediaAudio: "" };
    const proxy = await G.proxyFor(url);
    if (proxy) msg.browserProxy = proxy;
    return G.hostRequest(msg, G.DOWNLOAD_REPLY_MS);
  }

  G.onMessage["media-download"] = (req, sender, sendResponse) => {
    if (!req.entry || !req.item) return false;
    const tab = sender && sender.tab;
    // Con la entrada guardada, la calidad elegida también sale de ella (misma posición en la lista).
    const stored = tab ? storedEntry(tab.id, req.entry) : null;
    let entry = req.entry, item = req.item;
    if (stored) {
      entry = stored;
      const i = typeof req.index === "number" ? req.index : -1;
      if (i >= 0 && stored.items && stored.items[i]) item = stored.items[i];
    }
    downloadMedia(entry, item, tab).then((r) => {
      if (!(r && r.ok)) G.notify(t("notifyUnavailable"), r && r.error); // como IDM: sin aviso si va bien (sale su ventana)
      sendResponse(r);
    });
    return true;
  };

  G.resets.push(() => { tabMedia.clear(); tabPage.clear(); });
  Object.assign(G, { tabMedia, MEDIA_TIMING, mediaState, nextId, sendEntry, removeEntry, flushSubtitles, hasVisible,
                     mediaTabUpdated, mediaTabRemoved, downloadMedia });
})(typeof self !== "undefined" ? self : globalThis);
