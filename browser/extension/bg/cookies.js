// IDMClone Integration — fondo: cookies para las descargas y puente de cookies que pide IDMClone (#18).
(function (root) {
  "use strict";
  const G = root.IDMBG;
  const { B } = G;

  // Cookies del sitio, del almacén correcto (normal o incógnito).
  async function gatherCookies(url, incognito) {
    const q = { url };
    if (incognito && B.cookies.getAllCookieStores) {
      try {
        const stores = await B.cookies.getAllCookieStores();
        const priv = stores.find((s) => s.incognito) || stores.find((s) => s.id === "1" || s.id === "firefox-private");
        if (priv) q.storeId = priv.id;
      } catch (e) { }
    }
    const all = await B.cookies.getAll(q);
    return (all || []).map((c) => c.name + "=" + c.value).join("; ");
  }

  // IDMClone (Grabber) pide las cookies de una dirección: las que el navegador mandaría a ESA dirección (incluidas
  // las HttpOnly), del almacén normal; si no hay, de otro que no sea privado (contenedores de Firefox). No se guardan
  // en ningún sitio: se contestan y se olvidan.
  async function cookiesForUrl(url) {
    let all = await B.cookies.getAll({ url });
    if ((!all || !all.length) && B.cookies.getAllCookieStores) {
      let stores = [];
      try { stores = await B.cookies.getAllCookieStores(); } catch (e) { }
      for (const st of stores || []) {
        if (!st || st.incognito || st.id === "0" || st.id === "1" || st.id === "firefox-default" || st.id === "firefox-private") continue;
        try { all = await B.cookies.getAll({ url, storeId: st.id }); } catch (e) { all = []; }
        if (all && all.length) break;
      }
    }
    return (all || []).map((c) => c.name + "=" + c.value).join("; ");
  }

  // Para que el canal de cookies no sirva de "oráculo" (otro programa del mismo usuario que se hiciera pasar por
  // IDMClone podría pedir las cookies HttpOnly de cualquier sitio), solo se contesta por un sitio que el usuario tiene
  // abierto en una pestaña normal o que abrió (o al que navegó) en los últimos 15 minutos: el mismo servidor o su gemelo
  // con/sin "www.". Nunca cuentan las pestañas de incógnito. Es el caso del inicio de sesión manual del Grabber: IDMClone
  // abre la página en el navegador, el usuario inicia sesión y pulsa Continuar. Si no, se contesta sin cookies y con
  // reason:"notopen" (IDMClone dice que no hay cookies y ofrece pegarlas).
  const RECENT_HOST_MS = 15 * 60 * 1000;
  const RECENT_HOST_MAX = 300;
  const recentHosts = new Map(); // servidor -> última vez que se abrió o se navegó a él (pestañas normales)
  let recentLoaded = null;

  function siteHostOf(url) {
    try { const u = new URL(String(url || "")); return /^https?:$/.test(u.protocol) ? u.hostname.toLowerCase().replace(/\.$/, "") : ""; }
    catch (e) { return ""; }
  }
  function sameSite(a, b) { return !!a && !!b && (a === b || a === "www." + b || b === "www." + a); }

  function noteRecentHost(url, tab) {
    if (!url || !tab || tab.incognito) return;
    const h = siteHostOf(url);
    if (!h) return;
    recentHosts.delete(h);
    recentHosts.set(h, Date.now());
    while (recentHosts.size > RECENT_HOST_MAX) recentHosts.delete(recentHosts.keys().next().value);
    // El service worker se duerme y pierde la memoria: se copia en storage.session (solo en memoria del navegador,
    // se borra al cerrarlo; las páginas no pueden leerlo).
    try { if (B.storage.session) Promise.resolve(B.storage.session.set({ recentHosts: [...recentHosts] })).catch(() => {}); } catch (e) { }
  }

  function loadRecentHosts() {
    if (!recentLoaded) recentLoaded = (async () => {
      try {
        if (!B.storage.session) return;
        const s = await B.storage.session.get({ recentHosts: [] });
        for (const x of (s && Array.isArray(s.recentHosts) ? s.recentHosts : [])) {
          if (!Array.isArray(x) || typeof x[0] !== "string" || typeof x[1] !== "number") continue;
          if (!recentHosts.has(x[0]) || recentHosts.get(x[0]) < x[1]) recentHosts.set(x[0], x[1]);
        }
      } catch (e) { }
    })();
    return recentLoaded;
  }

  async function cookieHostAllowed(url) {
    const h = siteHostOf(url);
    if (!h) return false;
    await loadRecentHosts();
    const now = Date.now();
    for (const [k, at] of recentHosts) if (now - at < RECENT_HOST_MS && sameSite(k, h)) return true;
    let tabs = [];
    try { tabs = await B.tabs.query({}); } catch (e) { }
    return (tabs || []).some((x) => x && !x.incognito && (sameSite(siteHostOf(x.url), h) || sameSite(siteHostOf(x.pendingUrl), h)));
  }

  async function answerCookies(msg) {
    const url = typeof msg.url === "string" ? msg.url : "";
    if (!Number.isInteger(msg.req) || url.length > 4096 || !/^https?:\/\/[^/?#]+/i.test(url)) return;
    let cookies = "", ok = false, reason = "";
    let allowed = false;
    try { allowed = await cookieHostAllowed(url); } catch (e) { }
    if (!allowed) reason = "notopen";
    else { try { cookies = await cookiesForUrl(url); ok = true; } catch (e) { reason = "error"; } }
    const reply = { type: "cookies", req: msg.req, url, ok, cookies, userAgent: navigator.userAgent };
    if (reason) reply.reason = reason;
    await G.hostRequest(reply, G.PING_REPLY_MS);
  }

  G.resets.push(() => { recentHosts.clear(); recentLoaded = null; });
  Object.assign(G, { gatherCookies, cookiesForUrl, cookieHostAllowed, answerCookies, noteRecentHost, recentHosts });
})(typeof self !== "undefined" ? self : globalThis);
