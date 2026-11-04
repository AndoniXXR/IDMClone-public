// IDMClone Integration — Subtítulos del panel (como IDM: "Subtítulos: Español (vtt)", se guardan junto al vídeo).
// Sin APIs del navegador (se prueba con Node). Los módulos de lib/ comparten el objeto IDMLogic y se cargan en orden:
// util, i18n, decide, media, subtitles, links (ver logic.js).
(function (root) {
  "use strict";
  const L = root.IDMLogic = root.IDMLogic || {};
  const { extOf, urlPath, t, mediaFileName } = L;

  // ---- Subtítulos del panel (como IDM: "Subtítulos: Español (vtt)", se guardan junto al vídeo) ----
  const SUB_MIME = { "text/vtt": "vtt", "application/x-subrip": "srt", "application/x-srt": "srt", "text/srt": "srt",
                     "application/ttml+xml": "ttml", "application/ttaf+xml": "ttml" };
  const SUB_EXTS = ["vtt", "webvtt", "srt", "ttml", "ttml2", "dfxp"];
  // Nombre de cada idioma: clave de _locales ("langEs" -> "Español" / "Spanish" / "西班牙语" / "スペイン語").
  const LANG_NAMES = { es: "langEs", en: "langEn", fr: "langFr", de: "langDe", it: "langIt", pt: "langPt", ca: "langCa",
    gl: "langGl", eu: "langEu", nl: "langNl", ru: "langRu", ja: "langJa", zh: "langZh", ko: "langKo", ar: "langAr",
    pl: "langPl", tr: "langTr", sv: "langSv", no: "langNo", nb: "langNo", da: "langDa", fi: "langFi", el: "langEl", he: "langHe",
    hi: "langHi", cs: "langCs", hu: "langHu", ro: "langRo", uk: "langUk", id: "langId", th: "langTh", vi: "langVi",
    spa: "langEs", eng: "langEn", fra: "langFr", fre: "langFr", deu: "langDe", ger: "langDe", ita: "langIt", por: "langPt" };

  // ¿Es un archivo de subtítulos suelto (.vtt/.srt/.ttml) que pide la página? d como en classifyMedia.
  // Devuelve null o { kind: "subtitle", format: "vtt"|"srt"|"ttml", ext, language, url }.
  function classifySubtitle(d) {
    if (!d || !/^https?:/i.test(String(d.url || ""))) return null;
    if (d.statusCode && d.statusCode !== 200 && d.statusCode !== 206) return null;
    if (d.type && ["xmlhttprequest", "other", "media", "object"].indexOf(d.type) < 0) return null;
    const ct = String(d.contentType || "").split(";")[0].trim().toLowerCase();
    const ext = extOf(urlPath(d.url) || d.url);
    let fmt = SUB_MIME[ct] || "";
    if (!fmt && SUB_EXTS.indexOf(ext) >= 0 && (!ct || /^(text\/(plain|xml)|application\/(octet-stream|xml))$/.test(ct))) fmt = ext === "webvtt" ? "vtt" : ext === "dfxp" || ext === "ttml2" ? "ttml" : ext;
    if (!fmt) return null;
    return { kind: "subtitle", format: fmt, ext: fmt === "ttml" ? ".srt" : "." + fmt, language: subtitleLang(d.url), url: String(d.url).split("#")[0] };
  }

  // Idioma de unos subtítulos por su dirección: "peli.es.vtt", "subs_en-US.vtt", "/en.vtt", "?lang=fr"... ("" si no se sabe).
  function subtitleLang(url) {
    try {
      const u = new URL(url);
      for (const k of ["lang", "language", "lng", "hl", "tlang", "srclang"]) {
        const v = u.searchParams.get(k);
        if (v && /^[a-z]{2,3}([-_][a-z0-9]{2,4})?$/i.test(v)) return v.replace("_", "-");
      }
      const m = /[\/._-]([a-z]{2,3}(?:[-_][A-Za-z]{2,4})?)\.(?:vtt|webvtt|srt|ttml2?|dfxp|xml)$/i.exec(decodeURIComponent(u.pathname));
      if (m && (LANG_NAMES[m[1].toLowerCase().split(/[-_]/)[0]])) return m[1].replace("_", "-");
    } catch (e) { }
    return "";
  }

  // "Español" para "es" / "es-ES" (en el idioma del navegador); si no se conoce, el propio código.
  function languageName(code) {
    const c = String(code || "").trim();
    if (!c) return "";
    const k = LANG_NAMES[c.toLowerCase().split(/[-_]/)[0]];
    return k ? t(k) : c;
  }

  // Texto del menú: "Subtítulos: Español (vtt)". El nombre que da el sitio (NAME de HLS, label de <track>) manda.
  function subtitleLabel(item) {
    const name = String(item.name || "").trim() || languageName(item.language) || t("subtitleNoLang");
    return t("subtitleItem", [name, String(item.ext || ".vtt").replace(".", "").toLowerCase()]);
  }

  // Nombre del archivo de subtítulos: el del vídeo con el idioma delante de la extensión ("Mi vídeo.es.vtt").
  function subtitleFileName(title, item) {
    const lang = String((item && item.language) || "").replace(/[^A-Za-z0-9-]/g, "").substring(0, 12);
    const ext = String((item && item.ext) || ".vtt");
    return mediaFileName(title, "") + (lang ? "." + lang : "") + (ext[0] === "." ? ext : "." + ext);
  }

  // Subtítulos y su tipo en la lista del panel (VTT, SRT, TTML, TTML2, DFXP), como IDM: desmarcado, no se ofrecen.
  // sub: { url (archivo suelto: manda su extensión), format ("vtt"|"srt"|"ttml": el del archivo de origen) }.
  // Se busca primero el tipo de la extensión (.dfxp, .ttml2) y, si no está en la lista, el del formato. El tamaño
  // mínimo no cuenta (los subtítulos son pequeños). Sin lista o sin tipo en ella: se ofrecen, como siempre.
  const SUB_TYPES = { vtt: "vtt", webvtt: "vtt", srt: "srt", ttml: "ttml", ttml2: "ttml2", dfxp: "dfxp" };
  function subtitleAllowed(sub, cfg) {
    if (!sub || !cfg || !Array.isArray(cfg.mediaTypes)) return true;
    const url = String(sub.url || "");
    const cands = [SUB_TYPES[url ? extOf(urlPath(url) || url) : ""], SUB_TYPES[String(sub.format || "").toLowerCase().replace(/^\./, "")]];
    for (const c of cands) {
      if (!c) continue;
      const rule = cfg.mediaTypes.find((t) => t && String(t.ext || "").toLowerCase() === c);
      if (rule) return rule.on !== false;
    }
    return true;
  }

  Object.assign(L, { classifySubtitle, subtitleLang, languageName, subtitleLabel, subtitleFileName, subtitleAllowed });
  if (typeof module !== "undefined" && module.exports) module.exports = L;
})(typeof self !== "undefined" ? self : globalThis);
