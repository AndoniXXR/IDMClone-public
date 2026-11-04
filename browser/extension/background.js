// IDMClone Integration — fondo de la extensión: solo carga los módulos de bg/ (ver bg/core.js).
// Chromium (service worker): importScripts. Firefox (página de fondo): ya los cargó background.scripts del manifiesto,
// en este mismo orden. Node (pruebas): require, cada vez desde cero, y exporta los nombres que usan las pruebas.
(function (root) {
  "use strict";
  const FILES = ["bg/core.js", "bg/host.js", "bg/config.js", "bg/cookies.js", "bg/capture.js", "bg/menus.js", "bg/links.js",
                 "bg/media-store.js", "bg/media-net.js", "bg/sites.js",
                 "bg/youtube.js", "bg/main.js"];
  if (typeof module !== "undefined" && module.exports && typeof require === "function") {
    const path = require("path");
    require("./logic.js");
    root.IDMBG = {};
    for (const f of FILES) {
      const file = path.join(__dirname, f);
      delete require.cache[require.resolve(file)];
      require(file);
    }
    module.exports = root.IDMBG.exports;
  } else if (typeof importScripts === "function" && !root.IDMBG) {
    importScripts("logic.js", ...FILES);
  }
})(typeof self !== "undefined" ? self : globalThis);
