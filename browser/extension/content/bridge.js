// IDMClone Integration — enlace de cada documento con el fondo (el último de content/).
// Al cargar, el marco principal manda una ficha al azar: así el fondo sabe que la pestaña tiene un documento nuevo (sus
// vídeos son otros) sin confundirlo con un cambio de dirección sin recarga (history.pushState), que no cambia la ficha.
// Además pasa al fondo lo que encuentra el script de página de algunos sitios (page/hooks.js: X, Facebook, Instagram,
// Vimeo, YouTube), que se trata como dato no fiable: el fondo lo comprueba todo.
(function (root) {
  "use strict";
  const U = root.IDMContent;
  if (!U) return;
  let doc = "";
  try { const a = new Uint32Array(4); crypto.getRandomValues(a); doc = Array.from(a, (x) => x.toString(36)).join(""); }
  catch (e) { doc = String(Math.random()).slice(2) + String(Date.now()); }
  U.doc = doc;
  if (window === window.top) U.send({ type: "media-hello", doc });

  // Datos del sitio (page/hooks.js): un evento en el documento con el texto JSON en detail. Con límite de tamaño y de
  // frecuencia: la página podría mandar de todo.
  const MAX_DETAIL = 512 * 1024;
  let budget = 40, budgetAt = Date.now();
  document.addEventListener("idmclone-data", (e) => {
    const d = e && typeof e.detail === "string" ? e.detail : "";
    if (!d || d.length > MAX_DETAIL) return;
    const now = Date.now();
    if (now - budgetAt > 10000) { budget = 40; budgetAt = now; }
    if (budget-- <= 0) return;
    let data;
    try { data = JSON.parse(d); } catch (x) { return; }
    if (!data || typeof data !== "object") return;
    U.send({ type: "site-data", doc, page: location.href, data });
  }, true);
})(typeof self !== "undefined" ? self : globalThis);
