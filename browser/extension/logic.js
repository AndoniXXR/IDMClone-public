// IDMClone Integration — lógica de decisión (sin APIs del navegador, se prueba con Node).
// La usan background.js (service worker / página de fondo) y popup.js.
(function (root) {
  "use strict";

  // Tipos de archivo que IDM captura por defecto (Opciones -> Tipos de archivo, PDF §5.3).
  const DEFAULT_EXTS = [
    "3gp", "7z", "aac", "ace", "aif", "apk", "arj", "asf", "avi", "bin", "bz2", "exe", "gz", "gzip", "img", "iso",
    "lzh", "m4a", "m4v", "mkv", "mov", "mp3", "mp4", "mpa", "mpe", "mpeg", "mpg", "msi", "msu", "ogg", "ogv", "pdf",
    "plj", "pps", "ppt", "qt", "r0*", "r1*", "ra", "rar", "rm", "rmvb", "sea", "sit", "sitx", "tar", "tif", "tiff",
    "wav", "wma", "wmv", "z", "zip"
  ];

  // Sitios desde los que no se captura (como defexclist.txt de IDM: sonidos y vídeos de webs).
  const DEFAULT_EXCLUDE = ["*.meebo.com/*.mp3", "images.apple.com/*.mov", "*.update.microsoft.com/*"];

  // Tipo MIME -> extensión, para cuando ni la URL ni el servidor dan un nombre con extensión.
  const MIME_EXT = {
    "application/zip": "zip", "application/x-zip-compressed": "zip", "application/x-7z-compressed": "7z",
    "application/x-rar-compressed": "rar", "application/vnd.rar": "rar", "application/gzip": "gz", "application/x-gzip": "gz",
    "application/x-bzip2": "bz2", "application/x-tar": "tar", "application/x-iso9660-image": "iso",
    "application/x-msdownload": "exe", "application/x-msdos-program": "exe", "application/x-ms-installer": "msi",
    "application/x-msi": "msi", "application/vnd.android.package-archive": "apk",
    "application/pdf": "pdf", "application/vnd.ms-powerpoint": "ppt",
    "audio/mpeg": "mp3", "audio/mp3": "mp3", "audio/wav": "wav", "audio/x-wav": "wav", "audio/aac": "aac", "audio/mp4": "m4a",
    "audio/ogg": "ogg", "audio/x-ms-wma": "wma", "audio/webm": "webm", "audio/x-m4a": "m4a", "audio/flac": "flac",
    "video/mp4": "mp4", "video/x-matroska": "mkv", "video/quicktime": "mov", "video/x-msvideo": "avi", "video/x-ms-wmv": "wmv",
    "video/mpeg": "mpg", "video/ogg": "ogv", "video/3gpp": "3gp", "video/webm": "webm", "video/x-flv": "flv",
    "application/vnd.apple.mpegurl": "m3u8", "application/x-mpegurl": "m3u8", "application/dash+xml": "mpd",
    "text/html": "html"
  };

  function extOf(name) {
    if (!name) return "";
    let q = String(name).split(/[?#]/)[0];
    q = q.replace(/\\/g, "/");
    q = q.substring(q.lastIndexOf("/") + 1);
    const dot = q.lastIndexOf(".");
    if (dot <= 0 || dot === q.length - 1) return "";
    return q.substring(dot + 1).toLowerCase();
  }

  function baseName(path) {
    if (!path) return "";
    const p = String(path).replace(/\\/g, "/");
    return p.substring(p.lastIndexOf("/") + 1);
  }

  // "r0*" captura .r00 .r01...; "mp3" solo .mp3.
  function extMatches(ext, patterns) {
    if (!ext) return false;
    ext = ext.toLowerCase();
    for (const raw of patterns || []) {
      const p = String(raw).toLowerCase().replace(/^\./, "").trim();
      if (!p) continue;
      if (p.indexOf("*") >= 0 || p.indexOf("?") >= 0) {
        const re = new RegExp("^" + p.replace(/[.+^${}()|[\]\\]/g, "\\$&").replace(/\*/g, ".*").replace(/\?/g, ".") + "$");
        if (re.test(ext)) return true;
      } else if (p === ext) return true;
    }
    return false;
  }

  // Patrón de exclusión: "host/ruta" con comodines; si no lleva "/", solo el host.
  function urlExcluded(url, patterns) {
    let u;
    try { u = new URL(url); } catch (e) { return false; }
    // Con y sin puerto: "sitio.com" (sin puerto) vale también para "sitio.com:8080"; un patrón con puerto, solo para ese.
    const targets = [(u.host + u.pathname).toLowerCase()];
    if (u.port) targets.push((u.hostname + u.pathname).toLowerCase());
    for (const raw of patterns || []) {
      let p = String(raw).trim().toLowerCase();
      if (!p) continue;
      p = p.replace(/^[a-z]+:\/\//, "");
      if (p.indexOf("/") < 0) p += "/*";
      const esc = p.replace(/[.+^${}()|[\]\\]/g, "\\$&").replace(/\*/g, ".*").replace(/\?/g, ".");
      const re = new RegExp("^" + esc + "$");
      // "dominio.com/*" también vale para sus subdominios
      const re2 = p.indexOf("*.") !== 0 ? new RegExp("^([^/]+\\.)?" + esc + "$") : null;
      for (const target of targets) if (re.test(target) || (re2 && re2.test(target))) return true;
    }
    return false;
  }

  // Content-Disposition (RFC 6266 + RFC 5987), igual que en la aplicación.
  function filenameFromContentDisposition(cd) {
    if (!cd) return "";
    let plain = "", ext = "";
    const re = /(filename\*?)\s*=\s*(?:"((?:[^"\\]|\\.)*)"|([^;]*))/gi;
    let m;
    while ((m = re.exec(cd)) !== null) {
      const key = m[1].toLowerCase();
      const val = m[2] !== undefined ? m[2].replace(/\\(.)/g, "$1") : (m[3] || "").trim();
      if (key === "filename*") {
        const parts = val.split("'");
        const data = parts.length >= 3 ? parts.slice(2).join("'") : val;
        try { ext = decodeURIComponent(data); } catch (e) { ext = data; }
      } else {
        let v = val;
        if (/%[0-9a-f]{2}/i.test(v)) { try { v = decodeURIComponent(v); } catch (e) { } }
        plain = v;
      }
    }
    return baseName(ext || plain).trim();
  }

  function isCapturableScheme(url) {
    return typeof url === "string" && /^(https?|ftps?):\/\//i.test(url);
  }

  // Decide si IDMClone se queda con una descarga del navegador.
  //   d: { url, finalUrl, filename, mime, totalBytes, contentDisposition, byExtensionId }
  //   s: { enabled, exts, minSizeKB, exclude }
  // Devuelve { capture, reason, name, ext }.
  function decide(d, s) {
    const out = { capture: false, reason: "", name: "", ext: "" };
    if (!s || !s.enabled) { out.reason = "captura desactivada"; return out; }
    const url = d.finalUrl || d.url;
    if (!isCapturableScheme(url)) { out.reason = "esquema no capturable (" + String(url).split(":")[0] + ")"; return out; }
    if (d.byExtensionId && d.ownId && d.byExtensionId === d.ownId) { out.reason = "descarga propia"; return out; }
    if (urlExcluded(url, s.exclude) || (d.url && urlExcluded(d.url, s.exclude))) { out.reason = "sitio excluido"; return out; }

    const n = nameFor(d, url);
    const name = n.name, ext = n.ext;
    out.name = name; out.ext = ext;

    if (!extMatches(ext, s.exts && s.exts.length ? s.exts : DEFAULT_EXTS)) { out.reason = "tipo no vigilado (" + (ext || "sin extensión") + ")"; return out; }
    const min = (s.minSizeKB || 0) * 1024;
    if (min > 0 && d.totalBytes > 0 && d.totalBytes < min) { out.reason = "más pequeño que el mínimo"; return out; }
    out.capture = true;
    out.reason = "tipo vigilado (" + ext + ")";
    return out;
  }

  // Nombre: el que decidió el navegador > Content-Disposition > URL final > URL original; la extensión, del nombre
  // o, si no tiene, del tipo MIME.
  function nameFor(d, url) {
    const name = baseName(d.filename) || filenameFromContentDisposition(d.contentDisposition) || baseName(urlPath(url)) || baseName(urlPath(d.url));
    let ext = extOf(name);
    if (!ext && d.mime) ext = MIME_EXT[String(d.mime).split(";")[0].trim().toLowerCase()] || "";
    return { name, ext };
  }

  // Tecla "para forzar la descarga" pulsada (como IDM): va a IDMClone sea del tipo que sea, sin mirar la lista de tipos,
  // el tamaño mínimo, los sitios excluidos ni si la captura automática está desactivada (es una orden del usuario).
  // Solo quedan fuera lo que IDMClone no puede bajar (blob:, data:...) y las descargas de la propia extensión.
  function decideForced(d) {
    const out = { capture: false, reason: "", name: "", ext: "", forced: true };
    const url = d.finalUrl || d.url;
    if (!isCapturableScheme(url)) { out.reason = "esquema no capturable (" + String(url).split(":")[0] + ")"; return out; }
    if (d.byExtensionId && d.ownId && d.byExtensionId === d.ownId) { out.reason = "descarga propia"; return out; }
    const n = nameFor(d, url);
    out.name = n.name; out.ext = n.ext;
    out.capture = true;
    out.reason = "tecla para forzar la descarga";
    return out;
  }

  // ---- Teclas especiales (Opciones > General > "Teclas..." de IDMClone) ----
  // state: { tecla: { down, from, to, seen } } con los avisos de content.js (keydown/keyup de verdad, ver background.js).
  // ¿Estuvieron pulsadas A LA VEZ todas las teclas de la combinación ("del", "ctrl+alt"...) ahora mismo o hace menos de
  // graceMs? Una tecla "pulsada" de la que no se sabe nada desde hace staleMs se da por suelta (keyup perdido).
  const KEY_NAMES = ["del", "ins", "alt", "ctrl", "shift"];
  function keysHeld(combo, state, now, graceMs, staleMs) {
    if (!combo || typeof combo !== "string" || !state) return false;
    let from = 0, to = Infinity;
    for (const k of combo.toLowerCase().split("+")) {
      if (KEY_NAMES.indexOf(k) < 0) return false;
      const s = state[k];
      if (!s || !s.from) return false;
      const end = s.down ? (now - s.seen > staleMs ? s.seen : now) : s.to;
      from = Math.max(from, s.from);
      to = Math.min(to, end);
    }
    return from <= to && now - to <= graceMs;
  }

  // ---- Textos traducidos (_locales; español por defecto) ----
  // chrome.i18n.getMessage existe en el fondo, en content.js y en el popup. Sin él (o en la copia huérfana de content.js
  // tras actualizar la extensión) se devuelve la clave: nunca rompe nada.
  function t(key, subs) {
    try {
      const B = (typeof browser !== "undefined" && browser && browser.i18n) ? browser : (typeof chrome !== "undefined" ? chrome : null);
      if (B && B.i18n && typeof B.i18n.getMessage === "function") {
        const r = B.i18n.getMessage(key, subs === undefined ? undefined : [].concat(subs).map(String));
        if (r) return r;
      }
    } catch (e) { }
    return key;
  }

  function urlPath(url) {
    try { return decodeURIComponent(new URL(url).pathname); } catch (e) { return ""; }
  }

  // ---- Panel "Descargar este video" (como IDM) ----
  const HLS_MIME = ["application/vnd.apple.mpegurl", "application/x-mpegurl", "audio/mpegurl", "audio/x-mpegurl", "application/mpegurl"];
  const MEDIA_EXTS = ["mp4", "m4v", "webm", "mov", "mkv", "flv", "ogv", "3gp", "mp3", "m4a", "oga", "ogg", "opus", "wav", "aac", "flac", "weba"];
  const AUDIO_EXTS = ["mp3", "m4a", "oga", "ogg", "opus", "wav", "aac", "flac", "weba"];

  // ¿Es un vídeo o un audio que el panel puede ofrecer? Los trozos que piden los reproductores por
  // JavaScript (MSE: .ts, .m4s, fragmentos por XHR) no: se ofrece la lista HLS/DASH de la que salen.
  //   d: { url, type, statusCode, contentType, contentLength, contentRange }
  // Devuelve null o { kind: "hls"|"dash"|"file", media: "video"|"audio", size }.
  function classifyMedia(d) {
    if (!d || !isCapturableScheme(d.url)) return null;
    if (d.statusCode && d.statusCode !== 200 && d.statusCode !== 206) return null;
    const ct = String(d.contentType || "").split(";")[0].trim().toLowerCase();
    const ext = extOf(urlPath(d.url) || d.url);
    if (HLS_MIME.indexOf(ct) >= 0 || ext === "m3u8") return { kind: "hls", media: "video", size: -1 };
    if (ct === "application/dash+xml" || ext === "mpd") return { kind: "dash", media: "video", size: -1 };
    if (ct === "video/mp2t" || ct === "audio/mp2t" || ct === "video/iso.segment" || ext === "ts" || ext === "m4s") return null;
    // Archivo entero que el reproductor pide a trozos (Range o ?bytestart=&byteend=, como Facebook o Instagram):
    // se ofrece el archivo completo, sin los parámetros del trozo.
    const rangeInQuery = /[?&](bytestart|byteend|range)=/i.test(d.url);
    if ((d.type === "xmlhttprequest" || d.type === "other") && (d.statusCode === 206 || rangeInQuery)) {
      const av = /^(video|audio)\//.test(ct) || (MEDIA_EXTS.indexOf(ext) >= 0 && (!ct || ct === "application/octet-stream"));
      if (!av) return null;
      const t = /\/(\d+)\s*$/.exec(String(d.contentRange || ""));
      const media = ct.indexOf("audio/") === 0 || AUDIO_EXTS.indexOf(ext) >= 0 || /[?&]mime=audio/i.test(d.url) ? "audio" : "video";
      return { kind: "file", media, size: t ? parseInt(t[1], 10) : -1, url: stripRange(d.url), ranged: true };
    }
    if (d.type !== "media" && d.type !== "object" && d.type !== "other") return null;
    const isMedia = /^(video|audio)\//.test(ct) || (MEDIA_EXTS.indexOf(ext) >= 0 && (!ct || ct === "application/octet-stream"));
    if (!isMedia) return null;
    let size = -1;
    const m = /\/(\d+)\s*$/.exec(String(d.contentRange || ""));
    if (m) size = parseInt(m[1], 10);
    else if (d.statusCode !== 206 && d.contentLength > 0) size = d.contentLength;
    const media = ct.indexOf("audio/") === 0 || AUDIO_EXTS.indexOf(ext) >= 0 ? "audio" : "video";
    return { kind: "file", media, size };
  }

  // Dirección del archivo entero: sin los parámetros que piden un trozo.
  function stripRange(url) {
    try {
      const u = new URL(url);
      let changed = false;
      for (const p of ["bytestart", "byteend", "range", "rn", "rbuf"]) if (u.searchParams.has(p)) { u.searchParams.delete(p); changed = true; }
      return changed ? u.toString() : url;
    } catch (e) { return url; }
  }

  // Título de la página para el nombre del vídeo, como IDM: sin el contador "(3) " del principio ni el nombre del
  // sitio del final (" - Sitio", " | Sitio") cuando coincide con el servidor ("Mi vídeo - Ejemplo" en ejemplo.com).
  function cleanTitle(title, host) {
    let t = String(title || "").replace(/^\s*\(\d+\+?\)\s*/, "").trim();
    const labels = String(host || "").toLowerCase().replace(/:\d+$/, "").split(".").slice(0, -1)
      .filter((x) => x.length >= 3 && !/^(www|com|net|org|gob|gov|edu|mil|nom|int)$/.test(x)); // .com.mx, .gob.ar...
    const m = /^(.*\S)\s+[-|–—·]\s+([^-|–—·]+)$/.exec(t);
    if (m && labels.length) {
      const site = m[2].toLowerCase().replace(/[^a-z0-9]/g, "");
      if (site.length >= 3 && labels.some((x) => { x = x.replace(/[^a-z0-9]/g, ""); return x.length >= 3 && site.indexOf(x) === 0; })) t = m[1];
    }
    return t;
  }

  // Nombre del archivo a partir del título de la página (como IDM).
  function mediaFileName(title, ext) {
    let t = String(title || "").replace(/[\\/:*?"<>|\u0000-\u001f]+/g, " ").replace(/\s+/g, " ").trim();
    t = t.replace(/^[. ]+|[. ]+$/g, "");
    if (t.length > 120) t = t.substring(0, 120).trim();
    if (!t) t = "video";
    if (ext && ext[0] !== ".") ext = "." + ext;
    return t + (ext || "");
  }

  const tr = (key, subs) => t(key, subs); // mediaLabel tiene su propia "t"

  function formatSize(bytes) {
    if (!(bytes > 0)) return "";
    if (bytes >= 1073741824) return (bytes / 1073741824).toFixed(2) + " GB";
    if (bytes >= 1048576) return (bytes / 1048576).toFixed(1) + " MB";
    return Math.max(1, Math.round(bytes / 1024)) + " KB";
  }

  function formatDuration(sec) {
    sec = Math.round(sec || 0);
    if (sec <= 0) return "";
    const h = Math.floor(sec / 3600), m = Math.floor(sec % 3600 / 60), s = sec % 60;
    return (h > 0 ? h + ":" + String(m).padStart(2, "0") : String(m)) + ":" + String(s).padStart(2, "0");
  }

  // Texto de cada calidad del menú del panel: "MP4 1280x720  25.3 MB" / "solo audio (es)  3.1 MB".
  function mediaLabel(item) {
    if (item.kind === "subtitle") return subtitleLabel(item);
    const size = formatSize(item.size);
    let t;
    if (item.kind === "audio") t = tr("mediaAudioOnly") + (item.language ? " (" + item.language + ")" : "") + " " + String(item.ext || ".m4a").replace(".", "").toUpperCase();
    else {
      t = String(item.ext || ".mp4").replace(".", "").toUpperCase();
      if (item.height > 0) t += " " + item.width + "x" + item.height;
      else if (item.bandwidth > 0) t += " " + Math.round(item.bandwidth / 1000) + " kbps";
    }
    return size ? t + "  " + size : t;
  }

  // ---- Subtítulos del panel (como IDM: "Subtítulos: Español (vtt)", se guardan junto al vídeo) ----
  const SUB_MIME = { "text/vtt": "vtt", "application/x-subrip": "srt", "application/x-srt": "srt", "text/srt": "srt",
                     "application/ttml+xml": "ttml", "application/ttaf+xml": "ttml" };
  const SUB_EXTS = ["vtt", "webvtt", "srt", "ttml", "dfxp"];
  // Nombre de cada idioma: clave de _locales ("langEs" -> "Español" / "Spanish" / "西班牙语" / "スペイン語").
  const LANG_NAMES = { es: "langEs", en: "langEn", fr: "langFr", de: "langDe", it: "langIt", pt: "langPt", ca: "langCa",
    gl: "langGl", eu: "langEu", nl: "langNl", ru: "langRu", ja: "langJa", zh: "langZh", ko: "langKo", ar: "langAr",
    pl: "langPl", tr: "langTr", sv: "langSv", no: "langNo", nb: "langNo", da: "langDa", fi: "langFi", el: "langEl", he: "langHe",
    hi: "langHi", cs: "langCs", hu: "langHu", ro: "langRo", uk: "langUk", id: "langId", th: "langTh", vi: "langVi",
    spa: "langEs", eng: "langEn", fra: "langFr", fre: "langFr", deu: "langDe", ger: "langDe", ita: "langIt", por: "langPt" };

  // ¿Es un archivo de subtítulos suelto (.vtt/.srt/.ttml) que pide la página? d como en classifyMedia.
  // Devuelve null o { kind: "subtitle", format: "vtt"|"srt"|"ttml", ext, language, url }.
  function classifySubtitle(d) {
    if (!d || !/^https?:/i.test(String(d.url || ""))) return null;
    if (d.statusCode && d.statusCode !== 200 && d.statusCode !== 206) return null;
    if (d.type && ["xmlhttprequest", "other", "media", "object"].indexOf(d.type) < 0) return null;
    const ct = String(d.contentType || "").split(";")[0].trim().toLowerCase();
    const ext = extOf(urlPath(d.url) || d.url);
    let fmt = SUB_MIME[ct] || "";
    if (!fmt && SUB_EXTS.indexOf(ext) >= 0 && (!ct || /^(text\/(plain|xml)|application\/(octet-stream|xml))$/.test(ct))) fmt = ext === "webvtt" ? "vtt" : ext === "dfxp" ? "ttml" : ext;
    if (!fmt) return null;
    return { kind: "subtitle", format: fmt, ext: fmt === "ttml" ? ".srt" : "." + fmt, language: subtitleLang(d.url), url: String(d.url).split("#")[0] };
  }

  // Idioma de unos subtítulos por su dirección: "peli.es.vtt", "subs_en-US.vtt", "/en.vtt", "?lang=fr"... ("" si no se sabe).
  function subtitleLang(url) {
    try {
      const u = new URL(url);
      for (const k of ["lang", "language", "lng", "hl", "tlang", "srclang"]) {
        const v = u.searchParams.get(k);
        if (v && /^[a-z]{2,3}([-_][a-z0-9]{2,4})?$/i.test(v)) return v.replace("_", "-");
      }
      const m = /[\/._-]([a-z]{2,3}(?:[-_][A-Za-z]{2,4})?)\.(?:vtt|webvtt|srt|ttml|dfxp|xml)$/i.exec(decodeURIComponent(u.pathname));
      if (m && (LANG_NAMES[m[1].toLowerCase().split(/[-_]/)[0]])) return m[1].replace("_", "-");
    } catch (e) { }
    return "";
  }

  // "Español" para "es" / "es-ES" (en el idioma del navegador); si no se conoce, el propio código.
  function languageName(code) {
    const c = String(code || "").trim();
    if (!c) return "";
    const k = LANG_NAMES[c.toLowerCase().split(/[-_]/)[0]];
    return k ? t(k) : c;
  }

  // Texto del menú: "Subtítulos: Español (vtt)". El nombre que da el sitio (NAME de HLS, label de <track>) manda.
  function subtitleLabel(item) {
    const name = String(item.name || "").trim() || languageName(item.language) || t("subtitleNoLang");
    return t("subtitleItem", [name, String(item.ext || ".vtt").replace(".", "").toLowerCase()]);
  }

  // Nombre del archivo de subtítulos: el del vídeo con el idioma delante de la extensión ("Mi vídeo.es.vtt").
  function subtitleFileName(title, item) {
    const lang = String((item && item.language) || "").replace(/[^A-Za-z0-9-]/g, "").substring(0, 12);
    const ext = String((item && item.ext) || ".vtt");
    return mediaFileName(title, "") + (lang ? "." + lang : "") + (ext[0] === "." ? ext : "." + ext);
  }

  // ---- Tipos de archivo del panel (#8: Opciones > General > panel > "Para reproductores Web") ----

  // Extensión con la que se busca un vídeo o audio en la lista: la de la URL si es de vídeo o audio; si no
  // (googlevideo "videoplayback", "ver.php"...), la del Content-Type; si tampoco, la de la URL.
  function mediaExt(url, contentType) {
    const u = extOf(urlPath(url) || url);
    if (u && MEDIA_EXTS.indexOf(u) >= 0) return u;
    const ct = String(contentType || "").split(";")[0].trim().toLowerCase();
    return MIME_EXT[ct] || u;
  }

  // ¿Se ofrece en el panel? cfg: la de IDMClone (mediaTypes: [{ext, minKB, on}], minVideoKB, minAudioKB).
  // Sin lista (IDMClone antiguo): solo los mínimos de vídeo y audio, como siempre. Un tipo que no está en la
  // lista (o sin extensión conocida) se muestra con el mínimo general, para no perder sitios sin extensión.
  // Devuelve { show, off, small, minKB, known }; off = tipo desmarcado (ver #9).
  function mediaTypeAllowed(ext, size, media, cfg) {
    cfg = cfg || {};
    const general = media === "audio" ? (cfg.minAudioKB || 0) : (cfg.minVideoKB || 0);
    const e = String(ext || "").toLowerCase().replace(/^\./, "");
    let rule = null;
    if (Array.isArray(cfg.mediaTypes) && e) rule = cfg.mediaTypes.find((t) => t && String(t.ext || "").toLowerCase() === e) || null;
    const minKB = rule ? Math.max(0, rule.minKB | 0) : general;
    if (rule && rule.on === false) return { show: false, off: true, small: false, minKB, known: true };
    const small = size > 0 && size < minKB * 1024;
    return { show: !small, off: false, small, minKB, known: !!rule };
  }

  // ---- Panel para los enlaces seleccionados (#11) ----

  // sp: { mode: 0 mostrar | 1 no mostrar | 2 solo en los sitios de la lista, sites: [...] }. Solo páginas http(s).
  function selectionPanelAllowed(pageUrl, sp) {
    if (!sp || typeof sp !== "object" || !/^https?:\/\//i.test(String(pageUrl || ""))) return false;
    const mode = sp.mode | 0;
    if (mode === 1) return false;
    if (mode === 2) return siteListed(pageUrl, Array.isArray(sp.sites) ? sp.sites : []);
    return true;
  }

  // ¿La página es de uno de esos sitios? Cada sitio es un servidor (con o sin "https://", ruta o puerto, que se
  // ignoran), vale también para sus subdominios y admite comodines ("*.mega.nz"). Nombres con acentos: en punycode.
  function siteListed(pageUrl, sites) {
    let host;
    try { host = new URL(pageUrl).hostname.toLowerCase(); } catch (e) { return false; }
    for (const raw of sites || []) {
      let p = String(raw || "").trim().toLowerCase().replace(/^[a-z][a-z0-9+.-]*:\/\//, "");
      p = p.split(/[/?#]/)[0].replace(/:\d*$/, "");
      if (!p) continue;
      if (p.indexOf("*") < 0 && p.indexOf("?") < 0) {
        try { p = new URL("http://" + p + "/").hostname; } catch (e) { continue; }
        if (host === p || host.endsWith("." + p)) return true;
        continue;
      }
      const re = new RegExp("^" + p.replace(/[.+^${}()|[\]\\]/g, "\\$&").replace(/\*/g, ".*").replace(/\?/g, ".") + "$");
      if (re.test(host) || (p.indexOf("*.") === 0 && host === p.substring(2))) return true;
    }
    return false;
  }

  // Direcciones escritas en un texto (http, https, ftp, ftps), sin la puntuación que las sigue.
  function textUrls(text, max) {
    max = max || 1000;
    const out = [];
    const s = String(text || "").substring(0, 500000);
    // Termina en un espacio, en signos que no van en una URL o en la puntuación de ancho completo (。，「」...).
    const re = /\b(?:https?|ftps?):\/\/[^\s<>"'`{}|\\^　-〿＀-￯]{1,4096}/gi;
    const count = (u, ch) => { let n = 0; for (let i = u.indexOf(ch); i >= 0; i = u.indexOf(ch, i + 1)) n++; return n; };
    let m;
    while ((m = re.exec(s)) !== null && out.length < max) {
      let u = m[0];
      // Paréntesis y corchetes de cierre sobrantes (el texto, no la URL): se cuentan una vez (lineal).
      let open = count(u, "("), close = count(u, ")"), sOpen = count(u, "["), sClose = count(u, "]");
      for (;;) {
        const last = u[u.length - 1];
        if (/[.,;:!?'»”’…]/.test(last)) { u = u.slice(0, -1); continue; }
        if (last === ")" && open < close) { u = u.slice(0, -1); close--; continue; }
        if (last === "]" && sOpen < sClose) { u = u.slice(0, -1); sClose--; continue; }
        break;
      }
      if (/^[a-z]+:\/\/[^/?#]+/i.test(u) && out.indexOf(u) < 0) out.push(u);
    }
    return out;
  }

  // Lista de enlaces que llega de una página (no se confía en ella): solo http/ftp, sin repetir, texto recortado.
  function sanitizeLinks(list, max) {
    max = max || 1000;
    const out = [];
    const seen = new Set();
    if (!Array.isArray(list)) return out;
    for (const raw of list) {
      if (out.length >= max) break;
      const o = typeof raw === "string" ? { url: raw } : raw;
      if (!o || typeof o.url !== "string") continue;
      const url = o.url.trim();
      if (url.length > 4096 || !isCapturableScheme(url) || /[\u0000-\u001f\u007f]/.test(url) || seen.has(url)) continue;
      try { new URL(url); } catch (e) { continue; }
      seen.add(url);
      out.push({ url, text: String(typeof o.text === "string" ? o.text : "").replace(/\s+/g, " ").trim().substring(0, 200) });
    }
    return out;
  }

  // ---- Proxy del navegador (#17): "host:puerto" de un proxy HTTP, "socks5://host:puerto", "socks4://..." o "" ----
  // El proxy HTTP va sin esquema (así lo entiende también un IDMClone antiguo, que solo admitía "host:puerto"); un
  // IDMClone antiguo descarta los "socks...://" sin más. IDMClone sale por SOCKS con su puente local (src\Socks.cs).

  function hostPort(host, port) {
    host = String(host || "").trim();
    port = parseInt(port, 10);
    if (!host || !(port >= 1 && port <= 65535) || /[\s/@]/.test(host)) return "";
    if (host.indexOf(":") >= 0 && host[0] !== "[") host = "[" + host + "]"; // IPv6
    return host + ":" + port;
  }

  // Firefox: details.proxyInfo de webRequest. type: "http", "https" (TLS hasta el proxy: IDMClone no lo habla),
  // "socks" (SOCKS5), "socks4" o "direct". proxyDNS: el proxy resuelve los nombres (SOCKS4 -> SOCKS4a; el SOCKS5 de
  // IDMClone siempre le manda el nombre). La contraseña no la da el navegador, y el usuario nunca se manda.
  function proxyFromInfo(pi) {
    if (!pi) return "";
    const hp = hostPort(pi.host, pi.port);
    if (!hp) return "";
    if (pi.type === "http") return hp;
    if (pi.type === "socks") return "socks5://" + hp;
    if (pi.type === "socks4") return (pi.proxyDNS ? "socks4a://" : "socks4://") + hp;
    return "";
  }

  // Chromium: valor de chrome.proxy.settings.get() (solo si la extensión tuviera el permiso "proxy").
  function proxyFromChromeSettings(value, url) {
    if (!value || value.mode !== "fixed_servers" || !value.rules) return "";
    let u;
    try { u = new URL(url); } catch (e) { return ""; }
    const r = value.rules;
    for (const b of r.bypassList || []) {
      const p = String(b).trim().toLowerCase();
      if (p === "<local>" && u.hostname.indexOf(".") < 0) return "";
      if (p && (p === u.hostname || (p.indexOf("*.") === 0 && u.hostname.endsWith(p.substring(1))))) return "";
    }
    const s = (u.protocol === "https:" ? r.proxyForHttps : r.proxyForHttp) || r.singleProxy || r.fallbackProxy;
    if (!s) return "";
    const scheme = String(s.scheme || "http").toLowerCase();
    if (scheme === "http") return hostPort(s.host, s.port || 80);
    if (scheme === "socks5" || scheme === "socks4") { const hp = hostPort(s.host, s.port || 1080); return hp ? scheme + "://" + hp : ""; }
    return ""; // "https", "quic": TLS/QUIC hasta el proxy
  }

  // ¿La versión a ("1.5", "1.10.2") es más nueva que b? Por números, no por texto ("1.10" > "1.9").
  function newerVersion(a, b) {
    const pa = String(a || "").split("."), pb = String(b || "").split(".");
    if (!/^\d+(\.\d+)*$/.test(String(a || ""))) return false;
    for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
      const x = parseInt(pa[i] || "0", 10) || 0, y = parseInt(pb[i] || "0", 10) || 0;
      if (x !== y) return x > y;
    }
    return false;
  }

  const api = { DEFAULT_EXTS, DEFAULT_EXCLUDE, MIME_EXT, extOf, baseName, extMatches, urlExcluded, filenameFromContentDisposition, isCapturableScheme, decide,
                classifyMedia, stripRange, cleanTitle, mediaFileName, formatSize, formatDuration, mediaLabel,
                mediaExt, mediaTypeAllowed, selectionPanelAllowed, textUrls, sanitizeLinks, proxyFromInfo, proxyFromChromeSettings,
                decideForced, keysHeld, KEY_NAMES, t,
                classifySubtitle, subtitleLang, languageName, subtitleLabel, subtitleFileName, newerVersion };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  root.IDMLogic = api;
})(typeof self !== "undefined" ? self : globalThis);
