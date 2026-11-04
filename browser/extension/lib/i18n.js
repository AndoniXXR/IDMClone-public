// IDMClone Integration — Textos traducidos (_locales).
// Sin APIs del navegador (se prueba con Node). Los módulos de lib/ comparten el objeto IDMLogic y se cargan en orden:
// util, i18n, decide, media, subtitles, links (ver logic.js).
(function (root) {
  "use strict";
  const L = root.IDMLogic = root.IDMLogic || {};

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

  Object.assign(L, { t });
  if (typeof module !== "undefined" && module.exports) module.exports = L;
})(typeof self !== "undefined" ? self : globalThis);
