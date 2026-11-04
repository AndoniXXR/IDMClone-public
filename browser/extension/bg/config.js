// IDMClone Integration — fondo: configuración de IDMClone (getconfig), ajustes propios, icono y recarga tras actualizar.
(function (root) {
  "use strict";
  const G = root.IDMBG;
  const { B, L, t, IS_FIREFOX } = G;

  const DEFAULTS = { enabled: true, exts: L.DEFAULT_EXTS, minSizeKB: 0, exclude: L.DEFAULT_EXCLUDE };

  // Tipos de archivo y sitios excluidos: los de IDMClone (Opciones > Tipos de archivo), como hace IDM.
  // Se piden al host y se guardan medio minuto (y se refrescan solas mientras el canal está abierto: así un cambio
  // en Opciones llega pronto a las páginas); si el host no está, se usan los últimos conocidos o los propios.
  const APP_CFG_TTL_MS = 30000;
  let appCfg = null, appCfgAt = 0, appCfgPending = null;

  function getAppConfig() {
    if (appCfg && Date.now() - appCfgAt < APP_CFG_TTL_MS) return Promise.resolve(appCfg);
    if (!appCfgPending) appCfgPending = fetchAppConfig().finally(() => { appCfgPending = null; });
    return appCfgPending;
  }

  // Las claves de IDMClone 1.4 son opcionales: con un IDMClone antiguo faltan y todo sigue como antes.
  function readAppConfig(r) {
    const flags = (x) => (x && typeof x === "object" ? { link: x.link !== false, all: x.all !== false } : null);
    const sp = r.selectionPanel && typeof r.selectionPanel === "object" ? r.selectionPanel : null;
    return {
      exts: r.exts, exclude: Array.isArray(r.exclude) ? r.exclude : [],
      preventKeys: typeof r.preventKeys === "string" ? r.preventKeys : "del", forceKeys: r.forceKeys || "",
      mediaPanel: r.mediaPanel !== false, mediaMini: !!r.mediaMini, mediaExclude: Array.isArray(r.mediaExclude) ? r.mediaExclude : [],
      minVideoKB: r.minVideoKB || 0, minAudioKB: r.minAudioKB || 0,
      // #1: este navegador está desmarcado en "Capturar descargas para los siguientes navegadores".
      browser: typeof r.browser === "string" ? r.browser : "", captureOff: r.captureOff === true,
      // #2: opciones del menú contextual (null = IDMClone antiguo: los cuatro elementos de siempre).
      menu: flags(IS_FIREFOX ? r.menuFirefox : r.menuChrome),
      // #8, #9, #10: tipos del panel de vídeo, captura de los desmarcados y archivos protegidos.
      mediaTypes: Array.isArray(r.mediaTypes)
        ? r.mediaTypes.filter((x) => x && typeof x.ext === "string").map((x) => ({ ext: x.ext.toLowerCase(), minKB: Math.max(0, x.minKB | 0), on: x.on !== false }))
        : null,
      mediaNoAutoCapture: r.mediaNoAutoCapture !== false,
      mediaProtected: r.mediaProtected !== false,
      // #11: panel para los enlaces seleccionados (null = IDMClone antiguo: sin panel).
      // IDMClone 1.5: lo que sabe hacer además ("ytinfo": motor de YouTube).
      features: Array.isArray(r.features) ? r.features.filter((x) => typeof x === "string").slice(0, 50) : [],
      selectionPanel: sp ? { mode: sp.mode | 0, mini: !!sp.mini, sites: Array.isArray(sp.sites) ? sp.sites.filter((x) => typeof x === "string") : [] } : null
    };
  }

  async function fetchAppConfig() {
    // caps: esta extensión sabe contestar a las peticiones de cookies de IDMClone (CookieBridge, #18).
    const r = await G.hostRequest({ type: "getconfig", v: G.EXT_VERSION, caps: ["getcookies"] }, G.PING_REPLY_MS);
    if (r && r.ok && Array.isArray(r.exts)) {
      appCfg = readAppConfig(r);
      appCfgAt = Date.now();
      try { await B.storage.local.set({ appCfg }); } catch (e) { }
      try { await G.applyMenuFlags(appCfg.menu, false); } catch (e) { }
      refreshIcon();
      checkExtUpdate(r.extVersion).catch(() => {});
    } else if (!appCfg) {
      try { appCfg = (await B.storage.local.get({ appCfg: null })).appCfg; } catch (e) { }
      appCfgAt = Date.now() - APP_CFG_TTL_MS + 5000; // reintentar pronto
    }
    return appCfg;
  }

  // El popup pide la configuración de nuevo (acaba de probar la conexión o de abrir Opciones).
  function expireAppConfig() { appCfgAt = 0; }

  // Tras actualizar IDMClone (/update) los archivos de la extensión de su carpeta ya son los nuevos, pero el navegador
  // sigue con los que cargó. Si esta extensión está cargada a mano (desempaquetada en Chromium, temporal en Firefox) y la
  // de IDMClone es más nueva, se recarga sola desde la carpeta. Una vez por versión: si la carpeta cargada no es la de
  // IDMClone (sigue siendo vieja tras recargar), no se repite en bucle. Las instaladas de una tienda se actualizan solas.
  // Nunca a mitad de una captura (la descarga quedaría pausada en el navegador, o IDMClone la recibiría dos veces):
  // se espera a que no haya ninguna en curso; si tarda, se intenta en la siguiente lectura de la configuración.
  let reloadWaiting = false;
  const RELOAD_IDLE_POLL_MS = 250, RELOAD_IDLE_MAX_MS = 120000;
  async function checkExtUpdate(installed) {
    if (reloadWaiting || !installed || !L.newerVersion(installed, G.EXT_VERSION)) return false;
    let me = null;
    try { me = B.management && B.management.getSelf ? await B.management.getSelf() : null; } catch (e) { }
    if (!me || me.installType !== "development") return false;
    const s = await B.storage.local.get({ reloadedFor: "" });
    if (s.reloadedFor === installed) return false;
    reloadWaiting = true;
    try {
      for (let waited = 0; !G.extIdle(); waited += RELOAD_IDLE_POLL_MS) {
        if (waited >= RELOAD_IDLE_MAX_MS) return false;
        await G.sleep(RELOAD_IDLE_POLL_MS);
      }
      await B.storage.local.set({ reloadedFor: installed });
      B.runtime.reload();
      return true;
    } finally { reloadWaiting = false; }
  }

  async function getSettings() {
    const s = await B.storage.local.get(DEFAULTS);
    if (!Array.isArray(s.exts) || s.exts.length === 0) s.exts = L.DEFAULT_EXTS;
    if (!Array.isArray(s.exclude)) s.exclude = L.DEFAULT_EXCLUDE;
    const a = await getAppConfig();
    if (a && a.exts && a.exts.length) { s.exts = a.exts; s.exclude = a.exclude || []; s.fromApp = true; }
    // #1 (como IDM 6.43 con el navegador desmarcado): sin captura automática ni paneles; el menú contextual sigue.
    if (a && a.captureOff) {
      s.userEnabled = s.enabled;
      s.enabled = false;
      s.captureOff = true;
      s.browser = a.browser || "";
      s.offReason = t("offReason", a.browser || t("thisBrowser"));
    }
    return s;
  }

  // Estado en el botón de la barra, como IDM: insignia gris "X" y "[ERROR]" en la descripción si no se puede
  // hablar con IDMClone, "[DESACTIVADA]" si la captura automática está desactivada (aquí o en IDMClone, #1).
  let iconKey = "";
  async function refreshIcon() {
    const A = B.action || B.browserAction;
    if (!A || typeof A.setBadgeText !== "function") return;
    let off = false;
    try { const s = await B.storage.local.get({ enabled: true }); off = s.enabled === false || !!(appCfg && appCfg.captureOff); } catch (e) { }
    const down = G.hostState().down; // leído DESPUÉS de esperar: un error recién llegado gana
    const state = down ? "error" : off ? "off" : "ok";
    const title = t("extName") + (state === "error" ? " " + t("titleError") + " " + down : state === "off" ? " " + t("titleOff") : "");
    if (iconKey === state + title) return;
    iconKey = state + title;
    try {
      G.quiet(A.setBadgeText({ text: state === "ok" ? "" : "X" }));
      if (state !== "ok" && A.setBadgeBackgroundColor) G.quiet(A.setBadgeBackgroundColor({ color: "#808080" }));
      G.quiet(A.setTitle({ title }));
    } catch (e) { }
  }

  if (B.storage && B.storage.onChanged) B.storage.onChanged.addListener((ch) => { if (ch.enabled) refreshIcon(); });

  G.resets.push(() => { appCfg = null; appCfgAt = 0; });
  Object.assign(G, { DEFAULTS, APP_CFG_TTL_MS, getAppConfig, fetchAppConfig, expireAppConfig, readAppConfig, checkExtUpdate,
                     getSettings, refreshIcon, appCfgNow: () => appCfg });
})(typeof self !== "undefined" ? self : globalThis);
