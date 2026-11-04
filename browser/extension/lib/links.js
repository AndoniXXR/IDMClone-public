// IDMClone Integration — Enlaces: panel de los seleccionados (#11), direcciones escritas en un texto y listas que llegan de una página.
// Sin APIs del navegador (se prueba con Node). Los módulos de lib/ comparten el objeto IDMLogic y se cargan en orden:
// util, i18n, decide, media, subtitles, links (ver logic.js).
(function (root) {
  "use strict";
  const L = root.IDMLogic = root.IDMLogic || {};
  const { isCapturableScheme } = L;

  // ---- Panel para los enlaces seleccionados (#11) ----

  // sp: { mode: 0 mostrar | 1 no mostrar | 2 solo en los sitios de la lista, sites: [...] }. Solo páginas http(s).
  function selectionPanelAllowed(pageUrl, sp) {
    if (!sp || typeof sp !== "object" || !/^https?:\/\//i.test(String(pageUrl || ""))) return false;
    const mode = sp.mode | 0;
    if (mode === 1) return false;
    if (mode === 2) return siteListed(pageUrl, Array.isArray(sp.sites) ? sp.sites : []);
    return true;
  }

  // ¿La página es de uno de esos sitios? Cada sitio es un servidor (con o sin "https://", ruta o puerto, que se
  // ignoran), vale también para sus subdominios y admite comodines ("*.mega.nz"). Nombres con acentos: en punycode.
  function siteListed(pageUrl, sites) {
    let host;
    try { host = new URL(pageUrl).hostname.toLowerCase(); } catch (e) { return false; }
    for (const raw of sites || []) {
      let p = String(raw || "").trim().toLowerCase().replace(/^[a-z][a-z0-9+.-]*:\/\//, "");
      p = p.split(/[/?#]/)[0].replace(/:\d*$/, "");
      if (!p) continue;
      if (p.indexOf("*") < 0 && p.indexOf("?") < 0) {
        try { p = new URL("http://" + p + "/").hostname; } catch (e) { continue; }
        if (host === p || host.endsWith("." + p)) return true;
        continue;
      }
      const re = new RegExp("^" + p.replace(/[.+^${}()|[\]\\]/g, "\\$&").replace(/\*/g, ".*").replace(/\?/g, ".") + "$");
      if (re.test(host) || (p.indexOf("*.") === 0 && host === p.substring(2))) return true;
    }
    return false;
  }

  // Direcciones escritas en un texto (http, https, ftp, ftps), sin la puntuación que las sigue.
  function textUrls(text, max) {
    max = max || 1000;
    const out = [];
    const s = String(text || "").substring(0, 500000);
    // Termina en un espacio, en signos que no van en una URL o en la puntuación de ancho completo (。，「」...).
    const re = /\b(?:https?|ftps?):\/\/[^\s<>"'`{}|\\^　-〿＀-￯]{1,4096}/gi;
    const count = (u, ch) => { let n = 0; for (let i = u.indexOf(ch); i >= 0; i = u.indexOf(ch, i + 1)) n++; return n; };
    let m;
    while ((m = re.exec(s)) !== null && out.length < max) {
      let u = m[0];
      // Paréntesis y corchetes de cierre sobrantes (el texto, no la URL): se cuentan una vez (lineal).
      let open = count(u, "("), close = count(u, ")"), sOpen = count(u, "["), sClose = count(u, "]");
      for (;;) {
        const last = u[u.length - 1];
        if (/[.,;:!?'»”’…]/.test(last)) { u = u.slice(0, -1); continue; }
        if (last === ")" && open < close) { u = u.slice(0, -1); close--; continue; }
        if (last === "]" && sOpen < sClose) { u = u.slice(0, -1); sClose--; continue; }
        break;
      }
      if (/^[a-z]+:\/\/[^/?#]+/i.test(u) && out.indexOf(u) < 0) out.push(u);
    }
    return out;
  }

  // Lista de enlaces que llega de una página (no se confía en ella): solo http/ftp, sin repetir, texto recortado.
  function sanitizeLinks(list, max) {
    max = max || 1000;
    const out = [];
    const seen = new Set();
    if (!Array.isArray(list)) return out;
    for (const raw of list) {
      if (out.length >= max) break;
      const o = typeof raw === "string" ? { url: raw } : raw;
      if (!o || typeof o.url !== "string") continue;
      const url = o.url.trim();
      if (url.length > 4096 || !isCapturableScheme(url) || /[\u0000-\u001f\u007f]/.test(url) || seen.has(url)) continue;
      try { new URL(url); } catch (e) { continue; }
      seen.add(url);
      out.push({ url, text: String(typeof o.text === "string" ? o.text : "").replace(/\s+/g, " ").trim().substring(0, 200) });
    }
    return out;
  }

  Object.assign(L, { selectionPanelAllowed, siteListed, textUrls, sanitizeLinks });
  if (typeof module !== "undefined" && module.exports) module.exports = L;
})(typeof self !== "undefined" ? self : globalThis);
