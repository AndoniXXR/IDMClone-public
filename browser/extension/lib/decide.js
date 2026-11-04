// IDMClone Integration — Captura de descargas: ¿IDMClone se queda con esta descarga? Y las teclas especiales.
// Sin APIs del navegador (se prueba con Node). Los módulos de lib/ comparten el objeto IDMLogic y se cargan en orden:
// util, i18n, decide, media, subtitles, links (ver logic.js).
(function (root) {
  "use strict";
  const L = root.IDMLogic = root.IDMLogic || {};
  const { extOf, baseName, extMatches, urlExcluded, filenameFromContentDisposition, isCapturableScheme, urlPath, MIME_EXT, DEFAULT_EXTS } = L;

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

  Object.assign(L, { decide, decideForced, keysHeld, KEY_NAMES });
  if (typeof module !== "undefined" && module.exports) module.exports = L;
})(typeof self !== "undefined" ? self : globalThis);
