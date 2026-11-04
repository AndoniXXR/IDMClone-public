// IDMClone Integration — fondo: une los módulos de bg/ (el último que se carga): mensajes de las páginas y del popup,
// pestañas, arranque y, en Node, los nombres que usan las pruebas.
(function (root) {
  "use strict";
  const G = root.IDMBG;
  const { B } = G;

  // Mensajes del popup.
  G.onMessage["ping-host"] = (req, sender, sendResponse) => { G.hostRequest({ type: "ping" }, G.PING_REPLY_MS).then(sendResponse); return true; };
  G.onMessage["get-settings"] = (req, sender, sendResponse) => {
    G.expireAppConfig(); // el popup quiere lo último
    G.getSettings().then(sendResponse);
    return true;
  };
  G.onMessage["open-options"] = (req, sender, sendResponse) => {
    G.hostRequest({ type: "showoptions", tab: "Tipos de archivo" }, G.DOWNLOAD_REPLY_MS).then(sendResponse);
    return true;
  };

  // Una sola escucha: cada módulo pone sus tipos en G.onMessage.
  B.runtime.onMessage.addListener((req, sender, sendResponse) => {
    const f = req && typeof req.type === "string" && Object.prototype.hasOwnProperty.call(G.onMessage, req.type) ? G.onMessage[req.type] : null;
    return f ? f(req, sender, sendResponse) === true : false;
  });

  if (B.tabs && B.tabs.onUpdated) {
    B.tabs.onUpdated.addListener((tabId, info, tab) => {
      if (G.mediaTabUpdated) G.mediaTabUpdated(tabId, info, tab);
      // #18: sitios abiertos hace poco (para contestar a las peticiones de cookies de IDMClone).
      if (info.url || info.status === "loading") G.noteRecentHost(info.url || (tab && tab.url), tab);
    });
    B.tabs.onRemoved.addListener((tabId) => { if (G.mediaTabRemoved) G.mediaTabRemoved(tabId); });
  }

  const inNode = typeof module !== "undefined" && module.exports;
  if (!inNode) {
    // Al arrancar el navegador: leer ya las teclas especiales de IDMClone para las páginas.
    setTimeout(() => { G.getAppConfig().catch(() => {}); }, 3000);
    // También con el icono en error: así la "X" se quita sola cuando IDMClone vuelve a responder.
    setInterval(() => { const h = G.hostState(); if (h.open || h.down) G.getAppConfig().catch(() => {}); }, G.APP_CFG_TTL_MS + 1000);
    // Las pruebas en un navegador de verdad (tests/real-browser, tests/panel-casos) las usan desde el service worker.
    for (const k of ["getAppConfig", "hostRequest", "sendLinks", "tabMedia"]) if (G[k] !== undefined) root[k] = G[k];
  }

  // Para las pruebas con Node (no hace nada en el navegador).
  G.exports = Object.assign({}, G, {
    _reset: () => { for (const f of G.resets) f(); },
  });
})(typeof self !== "undefined" ? self : globalThis);
