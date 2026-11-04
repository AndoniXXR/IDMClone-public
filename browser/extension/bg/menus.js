// IDMClone Integration — fondo: menú contextual "Descargar con IDMClone" y content scripts en las pestañas ya abiertas.
// #2 Opciones > General > "Personalizar el menú contextual del navegador" (Chromium y Firefox por separado):
//   link: "Descargar con IDMClone" (enlace, imagen/vídeo/audio y página) y "los seleccionados"; all: "todos los enlaces".
// Los menús persisten entre reinicios del service worker: solo se rehacen al instalar, al arrancar el navegador o cuando
// IDMClone manda opciones distintas de las guardadas.
(function (root) {
  "use strict";
  const G = root.IDMBG;
  const { B, L, t, IS_FIREFOX } = G;

  const MENU_ALL = { link: true, all: true }; // IDMClone antiguo (sin opciones): los cuatro, como hasta ahora
  let menuChain = Promise.resolve();

  function createMenus(flags) {
    const f = flags || MENU_ALL;
    const mk = (o) => {
      try { B.contextMenus.create(o, () => { void (B.runtime.lastError); }); } catch (e) { }
    };
    menuChain = menuChain.then(async () => {
      try { await B.contextMenus.removeAll(); } catch (e) { }
      if (f.link) {
        mk({ id: "idmclone-link", title: t("menuDownload"), contexts: ["link"] });
        mk({ id: "idmclone-media", title: t("menuDownload"), contexts: ["image", "video", "audio"] });
        // Como IDM: sobre la página (o un marco) descarga esa dirección, p. ej. un PDF abierto en el navegador. Solo
        // "page" (también vale para el fondo de un marco, con frameUrl): "frame" saldría además sobre los enlaces y las
        // selecciones de dentro de un iframe.
        mk({ id: "idmclone-page", title: t("menuDownload"), contexts: ["page"] });
        // En IDM "los enlaces seleccionados" va con "Descargar con IDM" (no con "todos los enlaces").
        mk({ id: "idmclone-selected", title: t("menuSelected"), contexts: ["selection"] });
      }
      if (f.all) mk({ id: "idmclone-all", title: t("menuAll"), contexts: ["page"] });
    }).catch(() => {});
    return menuChain;
  }

  async function savedMenuFlags() {
    try { return (await B.storage.local.get({ menuFlags: null })).menuFlags; } catch (e) { return null; }
  }

  async function applyMenuFlags(flags, force) {
    if (!flags) return; // IDMClone antiguo: se quedan como estén
    const saved = await savedMenuFlags();
    if (!force && saved && saved.link === flags.link && saved.all === flags.all) return;
    try { await B.storage.local.set({ menuFlags: { link: flags.link, all: flags.all } }); } catch (e) { }
    await createMenus(flags);
  }

  async function initMenus() { await createMenus(await savedMenuFlags()); }

  // Al instalar o actualizar, Chromium no pone los content scripts en las pestañas ya abiertas (Firefox sí): sin esto,
  // el panel de vídeo y las teclas especiales no funcionarían en ellas hasta recargarlas. IDM hace lo mismo. Se salta el
  // marco que ya lo tiene funcionando; las pestañas que no se pueden tocar (páginas del navegador, tienda...) fallan sin
  // más. Los scripts de página (world MAIN) no se ponen: sin ellos queda la detección por la red hasta recargar.
  function contentFiles() {
    try {
      const cs = (B.runtime.getManifest().content_scripts || []).find((c) => c && c.all_frames && !c.world && Array.isArray(c.js));
      if (cs) return cs.js;
    } catch (e) { }
    return null;
  }

  async function injectIntoOpenTabs() {
    if (IS_FIREFOX || !B.scripting || !B.scripting.executeScript || !B.tabs || !B.tabs.query) return;
    const files = contentFiles();
    if (!files) return;
    let tabs = [];
    try { tabs = await B.tabs.query({ url: ["http://*/*", "https://*/*"] }); } catch (e) { return; }
    for (const tab of tabs || []) {
      if (!tab || tab.discarded) continue;
      try {
        // Marco a marco: un iframe cargado después de instalar ya lo tiene aunque la página no.
        const r = await B.scripting.executeScript({ target: { tabId: tab.id, allFrames: true }, func: () => !!(self.__idmcloneAlive && self.__idmcloneAlive()) });
        const frameIds = (r || []).filter((x) => x && !x.result && typeof x.frameId === "number").map((x) => x.frameId);
        if (frameIds.length) await B.scripting.executeScript({ target: { tabId: tab.id, frameIds }, files });
      } catch (e) { }
    }
  }

  B.runtime.onInstalled.addListener(() => { initMenus(); injectIntoOpenTabs().catch(() => {}); });
  if (B.runtime.onStartup) B.runtime.onStartup.addListener(() => { initMenus(); });

  B.contextMenus.onClicked.addListener(async (info, tab) => {
    if (info.menuItemId === "idmclone-all" || info.menuItemId === "idmclone-selected") {
      await G.sendLinks(tab, info.menuItemId === "idmclone-selected", info.frameId, info.selectionText);
      return;
    }
    const url = info.menuItemId === "idmclone-page" ? (info.frameUrl || info.pageUrl || (tab && tab.url)) : (info.linkUrl || info.srcUrl);
    if (!url) return;
    if (!L.isCapturableScheme(url)) { G.notify("IDMClone", t("linkNotDownloadable", String(url).split(":")[0])); return; }
    const r = await G.sendToIdm({ url, referrer: tab ? tab.url : "", filename: "", incognito: !!(tab && tab.incognito) });
    if (!(r && r.ok)) G.notify(t("notifyUnavailable"), r && r.error); // como IDM: sin aviso si va bien (sale su ventana)
  });

  Object.assign(G, { createMenus, applyMenuFlags, initMenus, injectIntoOpenTabs, menuDone: () => menuChain });
})(typeof self !== "undefined" ? self : globalThis);
