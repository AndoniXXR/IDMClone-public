// IDMClone Integration — fondo: vídeos y audios que pasan por la red (archivos, listas HLS .m3u8 y DASH .mpd, vídeo y
// audio pedidos a trozos, subtítulos sueltos). Las calidades de HLS/DASH las lee IDMClone (mensaje "probe" del host).
// Lo que se detecta va a bg/media-store.js, que lo manda al panel de la página.
(function (root) {
  "use strict";
  const G = root.IDMBG;
  const { B, L, t } = G;

  function header(details, name) {
    for (const x of details.responseHeaders || []) if ((x.name || "").toLowerCase() === name) return x.value;
    return "";
  }

  async function pageOf(details) {
    let tab = null;
    try { tab = await B.tabs.get(details.tabId); } catch (e) { }
    return { tab, page: (tab && tab.url) || details.documentUrl || details.initiator || "" };
  }

  // #1 navegador desmarcado en IDMClone (como IDM 6.43): sin panel de vídeo ni captura de reproductores.
  // También sin panel si está desactivado o el sitio está en "no mostrar el panel en estos sitios".
  function panelOff(cfg, page) {
    return cfg.captureOff || cfg.mediaPanel === false || !!(cfg.mediaExclude && L.urlExcluded(page, cfg.mediaExclude));
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
    const st = G.mediaState(details.tabId);
    const key = (m.url || details.url).split("#")[0];
    if (st.items.has(key) || st.children.has(key)) return;
    // Trozos de un archivo cuando la página ya tiene su lista HLS/DASH: son de esa lista.
    if (m.ranged && [...st.items.values()].some((e) => e && e.kind !== "file" && e.kind !== "pair" && e.kind !== "subs")) return;
    const cfg = (await G.getAppConfig()) || {};
    const { tab, page } = await pageOf(details);
    if (panelOff(cfg, page)) return; // sin marcador: si se vuelve a activar, el vídeo se ofrece
    if (st.items.has(key) || st.children.has(key)) return; // otra petición igual mientras se leía la configuración
    if (m.kind === "hls" || m.kind === "dash") st.streaming = true; // sus .vtt son trozos de sus subtítulos, no archivos sueltos
    st.items.set(key, null); // en curso: no preguntar dos veces por la misma lista

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
      // Más pequeño que el mínimo (o tipo desmarcado): no se ofrece solo; pero el audio de un vídeo pedido a trozos se
      // guarda (sin enseñarlo) para poder juntarlo con su vídeo.
      const small = !allow.show;
      if (small && !m.ranged) { st.items.delete(key); return; }
      const ext = "." + (L.extOf(url) || (m.media === "audio" ? (m.ranged ? "m4a" : "mp3") : "mp4"));
      entry = { id: G.nextId(), kind: "file", url, media: m.media, ranged: !!m.ranged, t: Date.now(), hidden: small,
                items: [{ kind: m.media, ext, size: m.size }] };
      // Vídeo y audio en archivos aparte (el reproductor pide los dos a trozos): se ofrecen juntos, y IDMClone los mezcla
      // en un archivo (como IDM con Facebook o Instagram).
      if (m.ranged) {
        if (m.media === "video" && small) {
          // Vídeo demasiado pequeño (anuncio, clip corto...): ni solo ni con audio. El audio que ya se ofrecía solo era
          // suyo (no música): se quita del panel; el que espera a su vídeo ya no sale (ver showLoneAudio).
          st.items.set(key, entry);
          for (const e of [...st.items.values()]) if (e && e.alone) { e.alone = false; e.hidden = true; await G.removeEntry(details, e); }
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
          setTimeout(() => { showLoneAudio(details, st, key, entry).catch(() => {}); }, G.MEDIA_TIMING.audioWait);
          return;
        }
      }
    } else {
      let cookies = "";
      try { cookies = await G.gatherCookies(details.url, tab && tab.incognito); } catch (e) { }
      const probing = G.hostRequest({ type: "probe", url: details.url, cookies, referrer: page, userAgent: navigator.userAgent }, G.PROBE_REPLY_MS);
      st.probes.add(probing);
      let r;
      try { r = await probing; } finally { st.probes.delete(probing); }
      if (G.tabMedia.get(details.tabId) !== st) return; // documento nuevo mientras IDMClone leía la lista
      if (!r || !r.ok) { st.items.delete(key); return; }
      // Lista de una sola calidad (sin hijas) mientras IDMClone aún lee otra lista de la pestaña: suele ser una calidad o
      // el audio de esa maestra (X pide la maestra y enseguida sus hijas). Se espera a la maestra antes de enseñarla,
      // para que no aparezca un momento en el panel y desaparezca.
      if (!(r.children && r.children.length) && st.probes.size) {
        try { await Promise.all([...st.probes]); } catch (e) { }
        if (G.tabMedia.get(details.tabId) !== st) return;
      }
      // Las listas de cada calidad no son vídeos aparte. El reproductor pudo pedir alguna mientras IDMClone leía la
      // maestra: se quitan del panel.
      for (const c of r.children || []) {
        const ck = String(c).split("#")[0];
        st.children.add(ck);
        const old = st.items.get(ck);
        st.items.delete(ck);
        if (old) await G.removeEntry(details, old);
      }
      if (st.children.has(key)) { st.items.delete(key); return; } // resultó ser una calidad de otra lista
      if (!r.items || !r.items.length) return;
      const hiddenList = () => { st.items.set(key, { id: G.nextId(), kind: r.kind, url: details.url, hidden: true, items: [] }); };
      // #10: contenido protegido (DRM) con "Mostrar el panel para los archivos protegidos" desmarcada (como IDM).
      if (r.protected && cfg.mediaProtected === false) { hiddenList(); return; }
      entry = listEntry(r, details.url, cfg);
      if (!entry) { hiddenList(); return; }
      // Trozos de archivos que ya se ofrecían sueltos: eran de esta lista.
      for (const [k, e] of [...st.items.entries()]) if (e && e.ranged) { st.items.delete(k); await G.removeEntry(details, e); }
    }
    st.items.set(key, entry);
    await G.sendEntry(details, entry);
    await G.flushSubtitles(details, st);
  }

  // Entrada del panel para una lista HLS/DASH leída por IDMClone (respuesta de "probe", o de "ytinfo" para YouTube), o
  // null si ninguna calidad se puede ofrecer.
  function listEntry(r, url, cfg) {
    // Subtítulos de la lista: van después de las calidades (no cuentan para los tipos del panel ni se ofrecen solos).
    // Los de un tipo desmarcado en la lista del panel (VTT, TTML...), no, como IDM. El formato de origen: el que diga
    // IDMClone; si no, los de HLS siempre son WebVTT, y en DASH ".srt" son TTML pasados a SRT.
    const subFormat = (i) => i.format || (r.kind === "hls" || String(i.ext || "").toLowerCase() === ".vtt" ? "vtt" : "ttml");
    const subItems = r.items.filter((i) => i && i.kind === "subtitle" && L.subtitleAllowed({ format: subFormat(i) }, cfg));
    const avItems = r.items.filter((i) => i && i.kind !== "subtitle");
    // #8: solo las calidades de un tipo marcado (por la extensión del archivo que sale: .mp4, .m4a, .webm...).
    let items = avItems;
    if (Array.isArray(cfg.mediaTypes)) {
      const checked = avItems.map((i) => ({ i, a: L.mediaTypeAllowed(String(i.ext || "").replace(/^\./, ""), i.size, i.kind === "audio" ? "audio" : "video", cfg) }));
      items = checked.filter((x) => x.a.show).map((x) => x.i);
      // Si ninguna calidad de vídeo llega al mínimo (clip corto a baja resolución, como los de X), la lista es el vídeo
      // de la página, no un anuncio: se ofrecen igual sus calidades (las de tipos marcados) con su audio, en vez de dejar
      // solo el audio como si fuera música. El tamaño de una lista es una estimación (bitrate x duración).
      const videos = checked.filter((x) => x.i.kind !== "audio");
      if (videos.length && !videos.some((x) => x.a.show) && videos.some((x) => x.a.small)) items = checked.filter((x) => !x.a.off).map((x) => x.i);
    }
    if (!items.length) return null;
    // En directo también (se graba, como IDM).
    return { id: G.nextId(), kind: r.kind, url, duration: r.duration, protected: !!r.protected, live: !!r.live, title: r.title || "",
             media: items.some((i) => i.kind === "video") ? "video" : "audio", items: items.concat(subItems) };
  }

  // Pasado MEDIA_TIMING.audioWait sin ningún vídeo a trozos en la pestaña, el audio sí se ofrece solo (puede ser solo
  // audio). Nada si la página cambió (documento nuevo), si resultó ser de una lista HLS/DASH o si llegó un vídeo (con él
  // ya hay par, o era pequeño y el audio era suyo).
  async function showLoneAudio(details, st, key, entry) {
    if (G.tabMedia.get(details.tabId) !== st || st.items.get(key) !== entry || !entry.hidden) return;
    if ([...st.items.values()].some((e) => e && e.kind === "file" && e.ranged && e.media === "video" && e.t >= entry.t)) return;
    entry.hidden = false; entry.alone = true;
    await G.sendEntry(details, entry);
    await G.flushSubtitles(details, st);
  }

  // Un par vídeo + audio (los sueltos se quitan del panel).
  async function sendPair(details, st, v, a) {
    const vext = v.items[0].ext === ".webm" ? ".webm" : ".mp4";
    const size = v.items[0].size > 0 && a.items[0].size > 0 ? v.items[0].size + a.items[0].size : -1;
    const pairKey = "pair:" + v.url;
    const old = st.items.get(pairKey);
    if (old) await G.removeEntry(details, old);
    for (const e of [v, a]) await G.removeEntry(details, e);
    a.alone = false;
    const entry = { id: G.nextId(), kind: "pair", url: v.url, media: "video",
                    items: [{ kind: "video", video: v.url, audio: a.url, ext: vext, size }] };
    st.items.set(pairKey, entry);
    await G.sendEntry(details, entry);
    await G.flushSubtitles(details, st);
  }

  // Subtítulos sueltos (.vtt/.srt/.ttml) que pide el reproductor: se ofrecen en el panel junto al vídeo ("Subtítulos:
  // Español (vtt)"), como IDM. Si aún no hay vídeo en el panel, esperan (ocultos) a que lo haya. Los .vtt de una página
  // con lista HLS/DASH son trozos de sus subtítulos: esos los ofrece IDMClone con la propia lista.
  async function detectSubtitle(details, sub) {
    if (details.tabId < 0) return;
    const st = G.mediaState(details.tabId);
    if (st.streaming || st.items.has(sub.url) || st.children.has(sub.url)) return;
    const cfg = (await G.getAppConfig()) || {};
    const { page } = await pageOf(details);
    if (panelOff(cfg, page) || st.streaming || st.items.has(sub.url)) return;
    if (!L.subtitleAllowed(sub, cfg)) return; // su tipo (VTT, SRT, TTML...) está desmarcado en la lista del panel
    const entry = { id: G.nextId(), kind: "subs", url: sub.url, media: "subtitle", hidden: true,
                    items: [{ kind: "subtitle", ext: sub.ext, language: sub.language, url: sub.url }] };
    st.items.set(sub.url, entry);
    if (G.hasVisible(st)) await G.flushSubtitles(details, st);
  }

  // #9 "No capturar las descargas automáticamente de los reproductores-web" DESMARCADA (como IDM por defecto): un
  // archivo de un tipo desmarcado en la lista del panel que está en Tipos de archivo se manda a IDMClone, una vez por
  // página, respetando la captura activada, los sitios excluidos y los tamaños mínimos. Límite honesto: una extensión MV3
  // no puede cortar la petición del reproductor, así que el vídeo se sigue reproduciendo en el navegador. Solo archivos
  // enteros (no los pedidos a trozos, que suelen ser la mitad de un par vídeo + audio).
  async function autoCapture(details, url, contentType, m, allow, cfg, tab, page) {
    if (cfg.mediaNoAutoCapture !== false) return; // marcada, o IDMClone antiguo
    if (G.inlineSentRecently(url)) return;         // un vídeo abierto como página: ya lo mandó considerInline
    const s = await G.getSettings();               // incluye "captura desactivada" y #1
    const dec = L.decide({ url, mime: contentType, totalBytes: m.size > 0 ? m.size : 0 }, s);
    if (!dec.capture) return;
    if (m.size > 0 && m.size < (allow.minKB || 0) * 1024) return;
    const r = await G.sendToIdm({ url, referrer: page, filename: "", incognito: !!(tab && tab.incognito), size: m.size > 0 ? m.size : 0, auto: true });
    if (!(r && r.ok)) G.notify(t("notifyUnavailable"), r && r.error); // como IDM: sin aviso si va bien (sale su ventana)
  }

  if (B.webRequest && B.webRequest.onHeadersReceived) {
    try {
      B.webRequest.onHeadersReceived.addListener((d) => { detectMedia(d).catch(() => {}); },
        { urls: ["<all_urls>"], types: ["sub_frame", "other", "xmlhttprequest", "media", "object"] }, ["responseHeaders"]);
    } catch (e) { console.warn("IDMClone: sin detección de vídeo", e); }
  }
  // Lo que el navegador sirve desde su caché no pasa por onHeadersReceived: un vídeo ya visto (un "GIF" de X al volver a
  // la publicación) se quedaba sin panel, e IDM sí lo ofrece. Solo las de la caché: las demás ya llegaron por arriba.
  if (B.webRequest && B.webRequest.onResponseStarted) {
    try {
      B.webRequest.onResponseStarted.addListener((d) => { if (d.fromCache) detectMedia(d).catch(() => {}); },
        { urls: ["<all_urls>"], types: ["sub_frame", "other", "xmlhttprequest", "media", "object"] }, ["responseHeaders"]);
    } catch (e) { }
  }

  Object.assign(G, { detectMedia, detectSubtitle, listEntry, panelOff, pageOf });
})(typeof self !== "undefined" ? self : globalThis);
