// IDMClone Integration — sitios con datos propios del vídeo (X, Facebook, Instagram, Vimeo, YouTube).
// Sin APIs del navegador (se prueba con Node). Los módulos de lib/ comparten el objeto IDMLogic (ver logic.js).
(function (root) {
  "use strict";
  const L = root.IDMLogic = root.IDMLogic || {};

  // Servidor de la página -> sitio. Debe coincidir con los "matches" del script de página del manifiesto.
  const SITE_HOSTS = {
    x: ["x.com", "twitter.com"],
    facebook: ["facebook.com"],
    instagram: ["instagram.com"],
    vimeo: ["vimeo.com"],
    youtube: ["youtube.com", "youtube-nocookie.com"],
  };

  // "x", "facebook"... para una página de esos sitios (o de sus subdominios: m.facebook.com, player.vimeo.com); si no, "".
  function siteOf(url) {
    let host;
    try { host = new URL(url).hostname.toLowerCase(); } catch (e) { return ""; }
    for (const site of Object.keys(SITE_HOSTS))
      for (const h of SITE_HOSTS[site]) if (host === h || host.endsWith("." + h)) return site;
    return "";
  }

  // ---- Pistas para unir un vídeo detectado con su <video> de la página ----
  // Una pista es un texto ("x:1234") que sale igual de la dirección del vídeo y de algo del elemento (su póster, su
  // dirección, los datos del sitio). En X la dirección del vídeo y la de su póster llevan el mismo número de medio:
  //   video.twimg.com/ext_tw_video/1234/pu/pl/...m3u8   <->  pbs.twimg.com/ext_tw_video_thumb/1234/pu/img/....jpg
  //   video.twimg.com/amplify_video/1234/...            <->  pbs.twimg.com/amplify_video_thumb/1234/img/....jpg
  //   video.twimg.com/tweet_video/AbC.mp4 ("GIF")       <->  pbs.twimg.com/tweet_video_thumb/AbC.jpg
  function urlHints(url) {
    const out = [];
    const s = String(url || "");
    let m = /^https:\/\/video\.twimg\.com\/(?:ext_tw_video|amplify_video)\/(\d{5,25})\//i.exec(s);
    if (m) out.push("x:" + m[1]);
    m = /^https:\/\/video\.twimg\.com\/tweet_video\/([A-Za-z0-9_-]{4,64})\.mp4/i.exec(s);
    if (m) out.push("xg:" + m[1]);
    return out;
  }

  function posterHints(url) {
    const out = [];
    const s = String(url || "");
    let m = /^https:\/\/pbs\.twimg\.com\/(?:ext_tw_video_thumb|amplify_video_thumb)\/(\d{5,25})\//i.exec(s);
    if (m) out.push("x:" + m[1]);
    m = /^https:\/\/pbs\.twimg\.com\/tweet_video_thumb\/([A-Za-z0-9_-]{4,64})[.?]/i.exec(s);
    if (m) out.push("xg:" + m[1]);
    return out;
  }

  // Una pista que llega de la página (datos del sitio): solo "sitio:valor" corto y sin caracteres raros.
  function cleanHint(h) {
    const s = String(h || "");
    return /^[a-z]{1,10}:[A-Za-z0-9_.-]{1,80}$/.test(s) ? s : "";
  }

  Object.assign(L, { SITE_HOSTS, siteOf, urlHints, posterHints, cleanHint });
  if (typeof module !== "undefined" && module.exports) module.exports = L;
})(typeof self !== "undefined" ? self : globalThis);
