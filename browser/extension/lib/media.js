// IDMClone Integration — Vídeo y audio: clasificación de lo que pasa por la red, nombres, textos del panel y tipos del panel.
// Sin APIs del navegador (se prueba con Node). Los módulos de lib/ comparten el objeto IDMLogic y se cargan en orden:
// util, i18n, decide, media, subtitles, links (ver logic.js).
(function (root) {
  "use strict";
  const L = root.IDMLogic = root.IDMLogic || {};
  const { isCapturableScheme, extOf, urlPath, MIME_EXT, t } = L;

  // ---- Panel "Descargar este video" (como IDM) ----
  const HLS_MIME = ["application/vnd.apple.mpegurl", "application/x-mpegurl", "audio/mpegurl", "audio/x-mpegurl", "application/mpegurl"];
  // Los de la lista de IDM (menos TS, que son trozos de HLS) y algunos más que dan los navegadores.
  const MEDIA_EXTS = ["mp4", "m4v", "webm", "mov", "qt", "mkv", "flv", "f4v", "ogv", "3gp", "avi", "wmv", "asf", "mpg", "mpeg", "rm",
                      "mp3", "m4a", "oga", "ogg", "opus", "wav", "aac", "flac", "weba", "wma"];
  const AUDIO_EXTS = ["mp3", "m4a", "oga", "ogg", "opus", "wav", "aac", "flac", "weba", "wma"];
  // Tipos MIME de vídeo que no empiezan por "video/" (RealMedia).
  const isAvMime = (ct) => /^(video|audio)\//.test(ct) || ct === "application/vnd.rn-realmedia";

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
      const av = isAvMime(ct) || (MEDIA_EXTS.indexOf(ext) >= 0 && (!ct || ct === "application/octet-stream"));
      if (!av) return null;
      const t = /\/(\d+)\s*$/.exec(String(d.contentRange || ""));
      const media = ct.indexOf("audio/") === 0 || AUDIO_EXTS.indexOf(ext) >= 0 || /[?&]mime=audio/i.test(d.url) ? "audio" : "video";
      return { kind: "file", media, size: t ? parseInt(t[1], 10) : -1, url: stripRange(d.url), ranged: true };
    }
    if (d.type !== "media" && d.type !== "object" && d.type !== "other") return null;
    const isMedia = isAvMime(ct) || (MEDIA_EXTS.indexOf(ext) >= 0 && (!ct || ct === "application/octet-stream"));
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
  // sitio del final (" - Sitio", " | Sitio", " / X") cuando coincide con el servidor ("Mi vídeo - Ejemplo" en ejemplo.com).
  // Un nombre de sitio corto (la "X" de x.com) solo cuenta si es igual al del servidor.
  function cleanTitle(title, host) {
    let t = String(title || "").replace(/^\s*\(\d+\+?\)\s*/, "").trim();
    const labels = String(host || "").toLowerCase().replace(/:\d+$/, "").split(".").slice(0, -1)
      .map((x) => x.replace(/[^a-z0-9]/g, ""))
      .filter((x) => x && !/^(www|com|net|org|gob|gov|edu|mil|nom|int)$/.test(x)); // .com.mx, .gob.ar...
    const m = /^(.*\S)\s+[-|–—·/]\s+([^-|–—·/]+)$/.exec(t);
    if (m && labels.length) {
      const site = m[2].toLowerCase().replace(/[^a-z0-9]/g, "");
      if (site && labels.some((x) => site === x || (site.length >= 3 && x.length >= 3 && site.indexOf(x) === 0))) t = m[1];
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
    if (item.kind === "subtitle") return L.subtitleLabel(item); // lib/subtitles.js
    const size = formatSize(item.size);
    let t;
    if (item.kind === "audio") t = tr("mediaAudioOnly") + (item.language ? " (" + item.language + ")" : "") + " " + String(item.ext || ".m4a").replace(".", "").toUpperCase();
    else {
      t = String(item.ext || ".mp4").replace(".", "").toUpperCase();
      if (item.height > 0) t += " " + item.width + "x" + item.height;
      else if (item.bandwidth > 0) t += " " + Math.round(item.bandwidth / 1000) + " kbps";
      else if (item.quality) t += " " + String(item.quality).substring(0, 10); // "HD" / "SD" (Facebook)
    }
    return size ? t + "  " + size : t;
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

  Object.assign(L, { HLS_MIME, MEDIA_EXTS, AUDIO_EXTS, isAvMime, classifyMedia, stripRange, cleanTitle, mediaFileName, formatSize, formatDuration, mediaLabel, mediaExt, mediaTypeAllowed });
  if (typeof module !== "undefined" && module.exports) module.exports = L;
})(typeof self !== "undefined" ? self : globalThis);
