// IDMClone Integration — fondo: vídeos que da la propia web de X, Facebook, Instagram y Vimeo (page/hooks.js los saca
// de sus respuestas de datos; content/bridge.js los pasa aquí). Como IDM, que lee esos mismos datos: así salen las
// calidades de verdad aunque el reproductor no las pida enteras por la red, y cada vídeo trae su número para unirse con
// su <video> (content/tracker.js).
// Todo lo que llega de una página es dato no fiable: solo se aceptan direcciones https de los servidores de vídeo de
// cada sitio, y la página tiene que ser de ese sitio (lo dice el navegador, sender.url, no la página).
(function (root) {
  "use strict";
  const G = root.IDMBG;
  const { L } = G;

  const CDN = {
    x: ["video.twimg.com"],
    facebook: ["fbcdn.net"],
    instagram: ["cdninstagram.com", "fbcdn.net"],
    vimeo: ["vimeocdn.com", "akamaized.net", "vimeo.com"],
  };

  function okUrl(u, site) {
    if (typeof u !== "string" || u.length > 4096) return "";
    let x;
    try { x = new URL(u.replace(/&amp;/g, "&")); } catch (e) { return ""; }
    if (x.protocol !== "https:") return "";
    const h = x.hostname.toLowerCase();
    return (CDN[site] || []).some((d) => h === d || h.endsWith("." + d)) ? x.toString() : "";
  }
  const num = (v) => (typeof v === "number" && isFinite(v) && v > 0 ? v : 0);
  const dims = (u) => { const m = /\/(\d{2,4})x(\d{2,4})\//.exec(u); return m ? { width: +m[1], height: +m[2] } : {}; };

  // Manifiesto DASH incrustado (Facebook, Instagram): cada Representation es un MP4 entero (vídeo o audio aparte).
  // Sin DOMParser (el service worker no lo tiene): se leen los atributos que hacen falta.
  function dashReps(xml, site) {
    const out = [];
    const s = String(xml || "").substring(0, 400000);
    const attr = (a, n) => { const m = new RegExp("\\b" + n + "=\"([^\"]*)\"").exec(a); return m ? m[1] : ""; };
    for (const set of s.split(/<AdaptationSet\b/i).slice(1)) {
      const head = set.substring(0, set.indexOf(">"));
      const setType = (attr(head, "mimeType") || attr(head, "contentType")).toLowerCase();
      for (const m of set.matchAll(/<Representation\b([^>]*)>([\s\S]*?)<\/Representation>/gi)) {
        const a = m[1];
        const type = (attr(a, "mimeType") || setType).toLowerCase();
        const base = /<BaseURL>([^<]+)<\/BaseURL>/i.exec(m[2]);
        const url = base ? okUrl(base[1].trim(), site) : "";
        if (!url) continue;
        out.push({ audio: /audio/.test(type), url, width: +attr(a, "width") || 0, height: +attr(a, "height") || 0,
                   bandwidth: +attr(a, "bandwidth") || 0, ext: /webm/.test(type) ? ".webm" : ".mp4" });
        if (out.length >= 30) return out;
      }
    }
    return out;
  }

  // Calidades de un manifiesto DASH: cada vídeo con el mejor audio (IDMClone los junta, como un "pair").
  function dashItems(xml, site) {
    const reps = dashReps(xml, site);
    const audio = reps.filter((r) => r.audio).sort((a, b) => b.bandwidth - a.bandwidth)[0];
    return reps.filter((r) => !r.audio).sort((a, b) => b.height - a.height || b.bandwidth - a.bandwidth)
      .map((v) => ({ kind: "video", video: v.url, audio: audio ? audio.url : "", ext: audio && audio.ext !== v.ext ? ".mkv" : v.ext,
                     width: v.width, height: v.height, bandwidth: v.bandwidth + (audio ? audio.bandwidth : 0), size: -1 }));
  }

  const ADAPT = {
    // X: las variantes MP4 (con su audio); la lista HLS ya la detecta la red. El número del medio une con el póster.
    x(d) {
      const out = [];
      for (const m of (Array.isArray(d.media) ? d.media : []).slice(0, 100)) {
        const id = /^\d{5,25}$/.test(String(m && m.id)) ? String(m.id) : "";
        if (!id) continue;
        const items = (Array.isArray(m.variants) ? m.variants : [])
          .filter((v) => v && /video\/mp4/i.test(String(v.type)) && okUrl(v.url, "x"))
          .map((v) => Object.assign({ kind: "video", url: okUrl(v.url, "x"), ext: ".mp4", bandwidth: num(v.bitrate), size: -1 }, dims(v.url)))
          .sort((a, b) => (b.height || 0) - (a.height || 0) || b.bandwidth - a.bandwidth);
        if (!items.length) continue;
        const hints = ["x:" + id];
        for (const it of items) for (const h of L.urlHints(it.url)) if (hints.indexOf(h) < 0) hints.push(h);
        out.push({ key: "x:" + id, media: "video", duration: num(m.duration), items, hints });
      }
      return out;
    },
    facebook(d) {
      const out = [];
      for (const m of (Array.isArray(d.media) ? d.media : []).slice(0, 50)) {
        const id = /^\d{5,25}$/.test(String(m && m.id)) ? String(m.id) : "";
        if (!id) continue;
        const items = dashItems(m.dash, "facebook");
        const files = [];
        for (const p of Array.isArray(m.progressive) ? m.progressive : []) { const u = okUrl(p && p.url, "facebook"); if (u) files.push({ url: u, quality: String(p.quality || "").substring(0, 10) }); }
        if (okUrl(m.hd, "facebook")) files.push({ url: okUrl(m.hd, "facebook"), quality: "HD" });
        if (okUrl(m.sd, "facebook")) files.push({ url: okUrl(m.sd, "facebook"), quality: "SD" });
        for (const f of files) if (!items.some((i) => i.url === f.url)) items.push({ kind: "video", url: L.stripRange(f.url), ext: ".mp4", quality: f.quality, size: -1 });
        if (!items.length) continue;
        out.push({ key: "fb:" + id, media: "video", duration: num(m.duration), items, hints: ["fb:" + id] });
      }
      return out;
    },
    instagram(d) {
      const out = [];
      for (const m of (Array.isArray(d.media) ? d.media : []).slice(0, 50)) {
        const code = /^[A-Za-z0-9_-]{5,40}$/.test(String(m && m.code)) ? String(m.code) : "";
        const id = /^\d{5,30}$/.test(String(m && m.id)) ? String(m.id) : "";
        if (!code && !id) continue;
        const items = dashItems(m.dash, "instagram");
        for (const v of Array.isArray(m.versions) ? m.versions : []) {
          const u = okUrl(v && v.url, "instagram");
          if (u && !items.some((i) => i.url === u || (i.height && i.height === v.height)))
            items.push({ kind: "video", url: u, ext: ".mp4", width: v.width | 0, height: v.height | 0, size: -1 });
        }
        if (!items.length) continue;
        const hints = [code && "ig:" + code, id && "igid:" + id].filter(Boolean);
        out.push({ key: hints[0], media: "video", duration: num(m.duration), items, hints });
      }
      return out;
    },
    vimeo(d, ctx) {
      const items = (Array.isArray(d.progressive) ? d.progressive : [])
        .map((p) => ({ kind: "video", url: okUrl(p && p.url, "vimeo"), ext: ".mp4", width: p.width | 0, height: p.height | 0, size: -1 }))
        .filter((i) => i.url).sort((a, b) => b.height - a.height);
      const id = /^\d{1,20}$/.test(String(d.id)) ? String(d.id) : "";
      // Sin archivos sueltos (cada vez más vídeos): su lista HLS, como la que detecta la red (IDMClone lee las calidades).
      if (!items.length && okUrl(d.hls, "vimeo")) ctx.probe(okUrl(d.hls, "vimeo"));
      if (!items.length) return [];
      return [{ key: "vimeo:" + (id || items[0].url), media: "video", duration: num(d.duration), title: String(d.title || "").substring(0, 200),
                items, hints: id ? ["vimeo:" + id] : [] }];
    },
  };

  async function siteData(req, sender) {
    const tab = sender && sender.tab;
    const d = req && req.data;
    if (!tab || !d || typeof d !== "object") return;
    const site = L.siteOf(sender.url || "");
    if (!site || d.site !== site) return; // la página dice ser de otro sitio: nada
    if (site === "youtube") { if (G.youtubeData) await G.youtubeData(d, tab, sender); return; }
    const adapt = ADAPT[site];
    if (!adapt) return;
    const cfg = (await G.getAppConfig()) || {};
    if (G.panelOff(cfg, tab.url || sender.url || "")) return;
    const details = { tabId: tab.id, frameId: sender.frameId || 0 };
    const ctx = { probe: (url) => G.detectMedia({ tabId: tab.id, frameId: details.frameId, url, type: "xmlhttprequest", statusCode: 200,
                                                  responseHeaders: [{ name: "content-type", value: "application/vnd.apple.mpegurl" }] }).catch(() => {}) };
    const found = adapt(d, ctx);
    const st = G.mediaState(tab.id);
    for (const e of found) {
      const key = "site:" + e.key;
      if (st.items.has(key)) continue;
      // #8: solo las calidades de un tipo marcado en la lista del panel (sin tamaño: no se miran los mínimos).
      const items = Array.isArray(cfg.mediaTypes)
        ? e.items.filter((i) => !L.mediaTypeAllowed(String(i.ext || "").replace(/^\./, ""), -1, "video", cfg).off) : e.items;
      if (!items.length) continue;
      const entry = { id: G.nextId(), kind: "site", source: "site", site, url: items[0].url || items[0].video, media: e.media,
                      duration: e.duration, title: e.title || "", items, hints: e.hints };
      st.items.set(key, entry);
      for (const h of e.hints) st.siteHints.add(h);
      // El mismo vídeo detectado por la red (la lista HLS de X, los trozos de Facebook): sobra, este trae más.
      for (const x of [...st.items.values()]) {
        if (!x || x === entry || x.source === "site" || x.hidden || !(x.hints || []).some((h) => e.hints.indexOf(h) >= 0)) continue;
        x.hidden = true;
        await G.removeEntry(details, x);
      }
      await G.sendEntry(details, entry);
    }
  }

  G.onMessage["site-data"] = (req, sender) => { siteData(req, sender).catch(() => {}); return false; };
  Object.assign(G, { siteData, dashItems });
})(typeof self !== "undefined" ? self : globalThis);
