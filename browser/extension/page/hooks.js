// IDMClone Integration — script de PÁGINA (world MAIN), solo en X, Facebook, Instagram, Vimeo y YouTube (manifiesto).
// Como IDM: los vídeos de estos sitios no siempre pasan por la red de forma reconocible (MSE, trozos, listas propias),
// pero sí vienen en las respuestas de datos de la propia web. Este script mira SOLO esas respuestas (fetch y
// XMLHttpRequest a las direcciones de datos de cada sitio) y algunas variables de la página, saca lo justo de cada vídeo
// (direcciones, calidades, duración, su número) y se lo pasa al script aislado de la extensión (content/bridge.js) con
// un evento. No toca las peticiones ni las respuestas: la página recibe lo mismo de siempre.
// Corre dentro de la página, sin ningún privilegio de la extensión: lo que manda se trata como dato no fiable.
(function () {
  "use strict";
  if (window.__idmclonePageHooks) return;
  Object.defineProperty(window, "__idmclonePageHooks", { value: true });
  const host = location.hostname.toLowerCase();
  const on = (d) => host === d || host.endsWith("." + d);
  const site = on("x.com") || on("twitter.com") ? "x" : on("facebook.com") ? "facebook" : on("instagram.com") ? "instagram"
             : on("vimeo.com") ? "vimeo" : on("youtube.com") || on("youtube-nocookie.com") ? "youtube" : "";
  if (!site) return;

  const MAX_BODY = 8 * 1024 * 1024; // respuestas más grandes: no se miran
  const DATA_URL = {
    x: /\/i\/api\/graphql\/|\/graphql\/[^/]+\/(Tweet|User|Home|Search|List|Bookmark|Community)/,
    facebook: /\/api\/graphql\/?(\?|$)|\/ajax\/|\/video\/|\/watch\//,
    instagram: /\/api\/v1\/|\/graphql\/query|\/api\/graphql/,
    vimeo: /\/video\/\d+\/config|\/config(\?|$)/,
    youtube: /\/youtubei\/v1\/(player|next)(\?|$)/,
  }[site];

  function emit(data) {
    try { document.dispatchEvent(new CustomEvent("idmclone-data", { detail: JSON.stringify(Object.assign({ site }, data)) })); } catch (e) { }
  }
  function hint(el, h) {
    try { el.dispatchEvent(new CustomEvent("idmclone-hint", { detail: h })); } catch (e) { }
  }

  // Recorre un JSON (sin seguir ciclos ni pasar de cierta profundidad) y llama a f con cada objeto.
  function walk(v, f, depth, seen) {
    if (!v || typeof v !== "object" || depth > 60) return;
    seen = seen || new Set();
    if (seen.has(v)) return;
    seen.add(v);
    if (seen.size > 200000) return;
    if (!Array.isArray(v)) { try { f(v); } catch (e) { } }
    for (const k in v) { if (Object.prototype.hasOwnProperty.call(v, k)) walk(v[k], f, depth + 1, seen); }
  }
  const str = (s, max) => (typeof s === "string" ? s.substring(0, max || 4096) : "");

  // ---- Qué se saca de cada sitio ----
  const PICK = {
    // X: cada medio con video_info.variants (vídeo o "GIF"); el número del medio (id_str) une con su <video> por el póster.
    x(json) {
      const out = [];
      walk(json, (o) => {
        if (!o.video_info || !Array.isArray(o.video_info.variants) || out.length >= 100) return;
        const id = str(o.id_str, 30);
        if (!id || out.some((m) => m.id === id)) return;
        out.push({ id, type: str(o.type, 20), duration: (o.video_info.duration_millis | 0) / 1000, poster: str(o.media_url_https, 400),
                   variants: o.video_info.variants.slice(0, 20).map((x) => ({ url: str(x && x.url), type: str(x && x.content_type, 60), bitrate: (x && x.bitrate) | 0 })) });
      }, 0);
      return out.length ? { kind: "x-media", media: out } : null;
    },
    // Facebook: nodos de vídeo con sus direcciones (antiguas y nuevas) y su manifiesto DASH.
    facebook(json) {
      const out = [];
      walk(json, (o) => {
        if (out.length >= 50) return;
        const sd = o.playable_url || o.browser_native_sd_url, hd = o.playable_url_quality_hd || o.browser_native_hd_url;
        const prog = Array.isArray(o.progressive_urls) ? o.progressive_urls : null;
        const dash = typeof o.dash_manifest === "string" ? o.dash_manifest
                   : Array.isArray(o.dash_manifests) && o.dash_manifests[0] && typeof o.dash_manifests[0].manifest_xml === "string" ? o.dash_manifests[0].manifest_xml : "";
        if (!sd && !hd && !prog && !dash) return;
        const id = str(o.videoId || o.video_id || o.id, 40);
        if (!id || out.some((m) => m.id === id)) return;
        out.push({ id, sd: str(sd), hd: str(hd), dash: str(dash, 400000), duration: +(o.playable_duration_in_ms || 0) / 1000 || +(o.length_in_second || 0),
                   progressive: prog ? prog.slice(0, 10).map((p) => ({ url: str(p && p.progressive_url), quality: str(p && p.metadata && p.metadata.quality, 20) })) : [] });
      }, 0);
      return out.length ? { kind: "fb-media", media: out } : null;
    },
    // Instagram: video_versions (archivo con audio) y video_dash_manifest (más calidad, audio aparte).
    instagram(json) {
      const out = [];
      walk(json, (o) => {
        if (out.length >= 50 || !Array.isArray(o.video_versions) || !o.video_versions.length) return;
        const code = str(o.code || o.shortcode, 40), id = str(String(o.pk || o.id || ""), 40);
        if ((!code && !id) || out.some((m) => m.code === code && m.id === id)) return;
        out.push({ code, id, duration: +o.video_duration || 0, dash: str(o.video_dash_manifest, 400000),
                   versions: o.video_versions.slice(0, 10).map((v) => ({ url: str(v && v.url), width: (v && v.width) | 0, height: (v && v.height) | 0 })) });
      }, 0);
      return out.length ? { kind: "ig-media", media: out } : null;
    },
    // Vimeo: la configuración del reproductor.
    vimeo(json) {
      const r = json && json.request, files = r && r.files;
      if (!files) return null;
      const cdn = (x) => { if (!x || !x.cdns) return ""; const c = x.cdns[x.default_cdn] || Object.values(x.cdns)[0]; return str(c && c.url); };
      return { kind: "vimeo-config", id: str(String((json.video && json.video.id) || ""), 20), title: str(json.video && json.video.title, 300),
               duration: +(json.video && json.video.duration) || 0, hls: cdn(files.hls), dash: cdn(files.dash),
               progressive: Array.isArray(files.progressive) ? files.progressive.slice(0, 10).map((p) => ({ url: str(p && p.url), width: (p && p.width) | 0, height: (p && p.height) | 0 })) : [] };
    },
    // YouTube: lo que necesita IDMClone para pedir las calidades (ver bg/sites.js y src/YouTube.cs).
    youtube(json) {
      const d = json && json.videoDetails;
      if (!d || typeof d.videoId !== "string") return null;
      return Object.assign({ kind: "yt-video", videoId: str(d.videoId, 20), title: str(d.title, 300), duration: +d.lengthSeconds || 0,
                             live: !!d.isLiveContent && !!(json.playabilityStatus && json.playabilityStatus.liveStreamability) }, ytContext());
    },
  };

  function ytContext() {
    const c = (window.ytcfg && typeof window.ytcfg.get === "function") ? window.ytcfg : null;
    const g = (k) => { try { return c ? c.get(k) : undefined; } catch (e) { return undefined; } };
    return { visitorData: str(g("VISITOR_DATA"), 2000), playerJsUrl: str(g("PLAYER_JS_URL"), 300), sts: (g("STS") | 0) || 0, loggedIn: !!g("LOGGED_IN") };
  }

  function handle(url, text) {
    if (typeof text !== "string" || !text || text.length > MAX_BODY) return;
    // Facebook antepone "for (;;);" y a veces manda varios JSON, uno por línea.
    const s = text.replace(/^\s*for\s*\(;;\);/, "");
    const parts = s.indexOf("\n{") > 0 && site === "facebook" ? s.split("\n") : [s];
    for (const p of parts) {
      let json;
      try { json = JSON.parse(p); } catch (e) { continue; }
      const r = PICK[site](json);
      if (r) emit(r);
    }
  }

  // ---- fetch y XMLHttpRequest: solo se LEE una copia de la respuesta ----
  const watched = (u) => { try { return DATA_URL.test(new URL(String(u), location.href).pathname + new URL(String(u), location.href).search); } catch (e) { return false; } };
  const origFetch = window.fetch;
  if (typeof origFetch === "function") {
    const hooked = function (input, init) {
      const p = origFetch.apply(this, arguments);
      try {
        const url = typeof input === "string" ? input : input && input.url;
        if (watched(url)) p.then((r) => { if (r && r.ok) r.clone().text().then((x) => handle(url, x), () => {}); }, () => {});
      } catch (e) { }
      return p;
    };
    try { Object.defineProperty(hooked, "name", { value: "fetch" }); window.fetch = hooked; } catch (e) { }
  }
  const XHR = window.XMLHttpRequest && window.XMLHttpRequest.prototype;
  if (XHR) {
    const open = XHR.open, send = XHR.send;
    XHR.open = function (method, url) { try { this.__idmcloneUrl = watched(url) ? String(url) : ""; } catch (e) { } return open.apply(this, arguments); };
    XHR.send = function () {
      try {
        if (this.__idmcloneUrl) this.addEventListener("load", () => {
          try {
            if (this.status < 200 || this.status >= 300) return;
            const t = this.responseType;
            if (t === "" || t === "text") handle(this.__idmcloneUrl, this.responseText);
            else if (t === "json" && this.response) { const r = PICK[site](this.response); if (r) emit(r); }
          } catch (e) { }
        });
      } catch (e) { }
      return send.apply(this, arguments);
    };
  }

  // ---- Lo que ya trae la página al cargarse ----
  function scanPage() {
    try {
      if (site === "facebook" || site === "instagram") {
        // Datos de la carga inicial: <script type="application/json" data-sjs> (no pasan por fetch ni XHR).
        for (const sc of document.querySelectorAll('script[type="application/json"]')) {
          const tx = sc.textContent || "";
          if (tx.length < MAX_BODY && /playable_url|progressive_url|dash_manifest|video_versions/.test(tx)) handle(location.href, tx);
        }
      } else if (site === "vimeo" && window.playerConfig) {
        const r = PICK.vimeo(window.playerConfig);
        if (r) emit(r);
      } else if (site === "youtube" && window.ytInitialPlayerResponse) {
        const r = PICK.youtube(window.ytInitialPlayerResponse);
        if (r) emit(r);
      }
    } catch (e) { }
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", scanPage); else scanPage();
  addEventListener("load", scanPage);
  // YouTube cambia de vídeo sin recargar: avisa con su propio evento; el reproductor nuevo trae otra respuesta.
  if (site === "youtube") {
    document.addEventListener("yt-navigate-finish", () => setTimeout(() => {
      try { const p = document.getElementById("movie_player"); const r = p && p.getPlayerResponse && PICK.youtube(p.getPlayerResponse()); if (r) emit(r); } catch (e) { }
    }, 500));
    // El <video> del reproductor con el número del vídeo que reproduce (cambia sin recargar la página).
    setInterval(() => {
      try {
        const v = document.querySelector("#movie_player video, video.html5-main-video");
        const p = document.getElementById("movie_player");
        const id = p && p.getVideoData ? String((p.getVideoData() || {}).video_id || "") : "";
        if (v && /^[A-Za-z0-9_-]{11}$/.test(id) && tagged.get(v) !== id) { tagged.set(v, id); hint(v, "yt:" + id); }
      } catch (e) { }
    }, 1500);
  }

  // ---- Pistas para unir cada <video> con su vídeo (content/tracker.js) ----
  // Facebook: el número del vídeo está en las propiedades de React del reproductor (como lo busca IDM). Instagram: el
  // código de la publicación, en su enlace.
  const tagged = new WeakMap();
  function fbId(el) {
    let key = null;
    for (const k in el) if (k.indexOf("__reactFiber$") === 0) { key = k; break; }
    let f = key && el[key];
    for (let i = 0; f && i < 40; i++, f = f.return) {
      const p = f.memoizedProps;
      if (!p) continue;
      const v = p.videoFBID || p.videoId || (p.coreVideoPlayerMetaData && p.coreVideoPlayerMetaData.videoFBID) || (p.video && p.video.id);
      if (v && /^\d{5,25}$/.test(String(v))) return String(v);
    }
    return "";
  }
  function igCode(el) {
    for (let n = el, i = 0; n && i < 25; n = n.parentElement, i++) {
      const a = n.querySelector && n.querySelector('a[href*="/p/"], a[href*="/reel/"]');
      const m = a && /\/(?:p|reel)\/([A-Za-z0-9_-]{5,40})/.exec(a.getAttribute("href") || "");
      if (m) return m[1];
      const m2 = /\/(?:p|reel)\/([A-Za-z0-9_-]{5,40})/.exec(location.pathname);
      if (m2 && n === document.body) return m2[1];
    }
    return "";
  }
  function tagVideos() {
    if (site !== "facebook" && site !== "instagram") return;
    for (const v of document.querySelectorAll("video")) {
      const h = site === "facebook" ? (fbId(v) && "fb:" + fbId(v)) : (igCode(v) && "ig:" + igCode(v));
      if (h && tagged.get(v) !== h) { tagged.set(v, h); hint(v, h); }
    }
  }
  if (site === "facebook" || site === "instagram") {
    setInterval(tagVideos, 1500);
    document.addEventListener("play", tagVideos, true);
  }
})();
