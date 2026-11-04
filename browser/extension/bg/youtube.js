// IDMClone Integration — fondo: YouTube. Como IDM, la extensión no descarga nada de YouTube por su cuenta: solo pasa a
// IDMClone qué vídeo se está viendo (su número y los datos de la página que hacen falta para pedirlo) y IDMClone, con su
// motor (src\YouTube.cs), lee las calidades y luego las descarga. Mensajes nuevos del host (IDMClone 1.5 o posterior,
// que lo anuncia en getconfig con features: ["ytinfo"]): "ytinfo" y "download" con mediaKind "youtube".
// Con un IDMClone anterior no se le pregunta nada (abriría IDMClone en cada vídeo): el panel avisa de que hay que
// actualizarlo. Este archivo no va en el paquete de las tiendas (package.ps1, store\build-store.ps1).
(function (root) {
  "use strict";
  const G = root.IDMBG;
  const { t } = G;

  const VIDEO_ID = /^[A-Za-z0-9_-]{11}$/;

  async function youtubeData(d, tab, sender) {
    if (!d || d.kind !== "yt-video" || !VIDEO_ID.test(String(d.videoId))) return;
    const cfg = (await G.getAppConfig()) || {};
    if (G.panelOff(cfg, tab.url || sender.url || "")) return;
    const st = G.mediaState(tab.id);
    const key = "yt:" + d.videoId;
    if (st.items.has(key)) return;
    st.items.set(key, null); // en curso
    const details = { tabId: tab.id, frameId: sender.frameId || 0 };
    const hints = [key];
    const title = String(d.title || "").substring(0, 200);
    if (!(Array.isArray(cfg.features) && cfg.features.indexOf("ytinfo") >= 0)) {
      // IDMClone sin motor de YouTube: un panel que solo dice cómo conseguirlo.
      const entry = { id: G.nextId(), kind: "youtube", source: "site", url: watchUrl(d.videoId), media: "video", title, hints, items: [], note: t("panelYtUpdate") };
      st.items.set(key, entry);
      await G.sendEntry(details, entry);
      return;
    }
    let cookies = "";
    try { cookies = await G.gatherCookies("https://www.youtube.com/", tab.incognito); } catch (e) { }
    const r = await G.hostRequest({
      type: "ytinfo", videoId: d.videoId, visitorData: String(d.visitorData || "").substring(0, 2000),
      playerJsUrl: /^\/s\/player\/[A-Za-z0-9_./-]{1,200}$/.test(String(d.playerJsUrl || "")) ? d.playerJsUrl : "",
      sts: (d.sts | 0) > 0 ? d.sts | 0 : 0, loggedIn: !!d.loggedIn, cookies, userAgent: navigator.userAgent, referrer: tab.url || ""
    }, G.YT_REPLY_MS);
    if (G.tabMedia.get(tab.id) !== st) return; // documento nuevo mientras IDMClone leía el vídeo
    if (!r || !r.ok || !Array.isArray(r.items)) {
      st.items.delete(key); // otro intento si se vuelve a ver
      const msg = r && typeof r.error === "string" ? r.error.substring(0, 300) : "";
      if (msg) {
        const entry = { id: G.nextId(), kind: "youtube", source: "site", url: watchUrl(d.videoId), media: "video", title, hints, items: [], note: msg };
        await G.sendEntry(details, entry);
      }
      return;
    }
    const entry = G.listEntry(Object.assign({}, r, { kind: "youtube", title: r.title || title }), watchUrl(d.videoId), cfg);
    if (!entry) { st.items.set(key, { id: G.nextId(), kind: "youtube", url: watchUrl(d.videoId), hidden: true, items: [] }); return; }
    entry.source = "site";
    entry.videoId = d.videoId;
    entry.hints = hints;
    st.items.set(key, entry);
    for (const h of hints) st.siteHints.add(h);
    await G.sendEntry(details, entry);
  }

  function watchUrl(id) { return "https://www.youtube.com/watch?v=" + id; }

  // Al elegir una calidad: IDMClone vuelve a pedir las direcciones (caducan) y junta vídeo y audio si van aparte.
  async function downloadYouTube(entry, item, tab, filename) {
    const id = String(entry.videoId || (/[?&]v=([A-Za-z0-9_-]{11})/.exec(entry.url) || [])[1] || "");
    if (!VIDEO_ID.test(id)) return { ok: false, error: t("panelNoReply") };
    const incognito = !!(tab && tab.incognito);
    let cookies = "";
    try { cookies = await G.gatherCookies("https://www.youtube.com/", incognito); } catch (e) { }
    const msg = { type: "download", url: watchUrl(id), referrer: (tab && tab.url) || "", cookies, filename, userAgent: navigator.userAgent, incognito,
                  mediaKind: "youtube", mediaVideo: String(item.video || ""), mediaAudio: item.kind === "audio" ? String(item.audio || "") : String(item.audio || "-") };
    if (item.kind === "audio") msg.mediaVideo = ""; // solo el audio
    const proxy = await G.proxyFor(msg.url);
    if (proxy) msg.browserProxy = proxy;
    return G.hostRequest(msg, G.DOWNLOAD_REPLY_MS);
  }

  Object.assign(G, { youtubeData, downloadYouTube });
})(typeof self !== "undefined" ? self : globalThis);
