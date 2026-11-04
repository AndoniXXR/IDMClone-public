// IDMClone Integration — lógica común sin APIs del navegador (se prueba con Node), repartida en lib/*.js.
// Este archivo solo los carga: en Node (tests) con require y en el service worker de Chromium con importScripts.
// Los content scripts, el popup y el fondo de Firefox (background.scripts) cargan los de lib/ directamente.
(function (root) {
  "use strict";
  const FILES = ["util.js", "i18n.js", "decide.js", "media.js", "subtitles.js", "links.js", "sites.js"];
  if (typeof module !== "undefined" && module.exports && typeof require === "function") {
    const path = require("path");
    for (const f of FILES) require(path.join(__dirname, "lib", f));
    module.exports = root.IDMLogic;
  } else if (typeof importScripts === "function" && !(root.IDMLogic && root.IDMLogic.sanitizeLinks)) {
    importScripts(...FILES.map((f) => "lib/" + f));
  }
  if (root.IDMLogic) root.IDMLogic.FILES = FILES;
})(typeof self !== "undefined" ? self : globalThis);
