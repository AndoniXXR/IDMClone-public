// IDMClone Integration — Utilidades comunes: extensiones, nombres, sitios excluidos, Content-Disposition, proxy y versiones.
// Sin APIs del navegador (se prueba con Node). Los módulos de lib/ comparten el objeto IDMLogic y se cargan en orden:
// util, i18n, decide, media, subtitles, links (ver logic.js).
(function (root) {
  "use strict";
  const L = root.IDMLogic = root.IDMLogic || {};

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
    // Resto de tipos de la lista del panel de IDM ("Para reproductores Web"): F4V, M4V, ASF, RM...
    "video/x-f4v": "f4v", "video/f4v": "f4v", "video/x-m4v": "m4v", "video/x-ms-asf": "asf", "video/avi": "avi",
    "application/vnd.rn-realmedia": "rm", "audio/x-aac": "aac", "audio/x-flac": "flac", "audio/opus": "opus",
    "audio/wave": "wav", "audio/vnd.wave": "wav",
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

  function urlPath(url) {
    try { return decodeURIComponent(new URL(url).pathname); } catch (e) { return ""; }
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

  Object.assign(L, { DEFAULT_EXTS, DEFAULT_EXCLUDE, MIME_EXT, extOf, baseName, extMatches, urlExcluded, filenameFromContentDisposition, isCapturableScheme, urlPath, hostPort, proxyFromInfo, proxyFromChromeSettings, newerVersion });
  if (typeof module !== "undefined" && module.exports) module.exports = L;
})(typeof self !== "undefined" ? self : globalThis);
