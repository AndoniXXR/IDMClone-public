// IDMClone Integration — fondo: "Descargar todos los enlaces" / "los enlaces seleccionados" (menú contextual y panel de
// la selección, #11): se recogen en la página y van a IDMClone, que abre la lista para elegir cuáles descargar.
(function (root) {
  "use strict";
  const G = root.IDMBG;
  const { B, L, t } = G;

  // Recoge los enlaces de un marco (o de su selección), como IDM: <a href>, <area href>, imágenes, vídeo y audio, y lo
  // mismo de los iframes sin dirección propia (about:blank, escritos por la página) a los que el marco llega
  // (contentDocument: mismo origen). Con selección, también el texto seleccionado y el de los <textarea> tocados por ella
  // (o el trozo seleccionado dentro del que tiene el foco), para buscar en él direcciones escritas (L.textUrls, en el
  // fondo). Se ejecuta DENTRO de la página (executeScript), solo cuando el usuario elige la opción del menú; no lee nada
  // más. Devuelve { origin, links: [{ url, text }], text }.
  function collectLinks(selectionOnly) {
    const MAX = 5000, TEXT_MAX = 200000;
    const out = [];
    const seen = new Set();
    let text = "";
    const add = (url, label) => {
      if (out.length >= MAX || typeof url !== "string" || !/^(https?|ftps?):/i.test(url) || seen.has(url)) return;
      seen.add(url);
      out.push({ url, text: String(label || "").replace(/\s+/g, " ").trim().substring(0, 200) });
    };
    const addText = (s) => { if (s && text.length < TEXT_MAX) text += String(s).substring(0, TEXT_MAX - text.length) + "\n"; };
    const scan = (doc, win, depth) => {
      let sel = null;
      if (selectionOnly) {
        try { sel = win.getSelection(); } catch (e) { sel = null; }
        // Texto seleccionado dentro de un <textarea> o <input> con el foco (la selección del documento no lo ve).
        const f = doc.activeElement;
        try {
          if (f && /^(textarea|input)$/i.test(f.tagName || "") && f.selectionEnd > f.selectionStart) addText(String(f.value).substring(f.selectionStart, f.selectionEnd));
        } catch (e) { }
        if (!sel || sel.rangeCount === 0 || (sel.isCollapsed && !String(sel).trim())) sel = null;
        else addText(String(sel));
      }
      const inSel = (el) => !selectionOnly || (!!sel && sel.containsNode(el, true));
      if (selectionOnly && !sel) return; // nada seleccionado en este documento (sí puede haberlo en el foco, arriba)
      for (const a of doc.querySelectorAll("a[href], area[href]")) {
        if (out.length >= MAX) break;
        if (inSel(a)) add(a.href, a.innerText || a.alt || a.title);
      }
      for (const m of doc.querySelectorAll("img[src], video[src], audio[src], source[src]")) {
        if (out.length >= MAX) break;
        if (inSel(m)) add(m.src, m.alt || m.title);
      }
      if (selectionOnly) for (const ta of doc.querySelectorAll("textarea")) if (inSel(ta)) addText(ta.value);
      if (depth >= 3) return;
      for (const fr of doc.querySelectorAll("iframe, frame")) {
        let d = null;
        try { d = fr.contentDocument; } catch (e) { d = null; } // de otro origen: null o excepción
        const src = fr.getAttribute ? (fr.getAttribute("src") || "") : "";
        // Los que tienen dirección propia ya los lee executeScript (allFrames) por su cuenta.
        if (d && (!src || /^(about:|javascript:)/i.test(src))) scan(d, fr.contentWindow || win, depth + 1);
      }
    };
    scan(document, window, 0);
    let origin = "";
    try { origin = location.origin; } catch (e) { }
    return { origin, links: out, text };
  }

  // Une lo de cada marco: el marco principal primero y, de los demás, solo los del mismo origen (como IDM, que solo
  // llega a los marcos de la página; así no se cuelan los enlaces de los anuncios de otros sitios). Admite el formato
  // antiguo (lista).
  function mergeFrameLinks(res, sameOriginOnly, extraText) {
    const frames = (Array.isArray(res) ? res : []).filter((r) => r && r.result)
      .map((r) => ({ frameId: typeof r.frameId === "number" ? r.frameId : 0, v: Array.isArray(r.result) ? { origin: "", links: r.result, text: "" } : r.result }));
    frames.sort((a, b) => a.frameId - b.frameId);
    const top = frames.find((f) => f.frameId === 0) || frames[0];
    const all = [];
    let text = typeof extraText === "string" ? extraText.substring(0, 200000) : "";
    for (const f of frames) {
      if (sameOriginOnly && f !== top && (!top || !f.v.origin || f.v.origin !== top.v.origin)) continue;
      if (Array.isArray(f.v.links)) for (const l of f.v.links) all.push(l);
      if (typeof f.v.text === "string" && text.length < 400000) text += "\n" + f.v.text.substring(0, 200000);
    }
    // Direcciones escritas en el texto seleccionado (no las que ya son enlaces).
    for (const u of L.textUrls(text, 1000)) all.push({ url: u, text: "" });
    return L.sanitizeLinks(all, 1000);
  }

  async function sendLinks(tab, selectionOnly, frameId, selectionText) {
    let links = [];
    try {
      // Selección: en el marco donde se hizo clic (una selección dentro de un iframe está en ese iframe, no en la
      // página). Todos los enlaces: todos los marcos de la página (y se quedan los del mismo origen).
      const target = selectionOnly ? (frameId > 0 ? { tabId: tab.id, frameIds: [frameId] } : { tabId: tab.id }) : { tabId: tab.id, allFrames: true };
      const res = await B.scripting.executeScript({ target, func: collectLinks, args: [!!selectionOnly] });
      links = mergeFrameLinks(res, !selectionOnly, selectionOnly ? selectionText : "");
    } catch (e) {
      G.notify("IDMClone", t("cantReadLinks", String(e && e.message || e)));
      return null;
    }
    if (!links.length) { G.notify("IDMClone", t(selectionOnly ? "selectionNoLinks" : "pageNoLinks")); return null; }
    return sendLinkList(tab, links, !!selectionOnly, tab.url || "");
  }

  // La lista de enlaces a IDMClone (ventana para elegir cuáles descargar), con las cookies de cada servidor.
  async function sendLinkList(tab, links, selection, referrer) {
    const cookies = {};
    for (const host of [...new Set(links.map((l) => { try { return new URL(l.url).host; } catch (e) { return ""; } }))].filter(Boolean).slice(0, 30)) {
      try { const c = await G.gatherCookies("https://" + host + "/", tab.incognito); if (c) cookies[host] = c; } catch (e) { }
    }
    const r = await G.hostRequest({ type: "links", links, referrer: referrer || "", userAgent: navigator.userAgent, cookies, selection: !!selection }, G.DOWNLOAD_REPLY_MS);
    if (!r || !r.ok) G.notify(t("notifyUnavailable"), r && r.error);
    return r;
  }

  // #11 Panel de los enlaces seleccionados (content/selection-panel.js), tras un clic del usuario. La lista viene de una
  // página (puede ser hostil): solo http/ftp, como mucho 1000, textos recortados.
  G.onMessage["selection-links"] = (req, sender, sendResponse) => {
    const tab = sender && sender.tab;
    if (!tab) return false;
    const links = L.sanitizeLinks(req.links, 1000);
    if (!links.length) { sendResponse({ ok: false, error: t("selectionNoLinks") }); return false; }
    const ref = sender.url && /^https?:\/\//i.test(sender.url) ? sender.url : (tab.url || "");
    sendLinkList(tab, links, true, ref).then(sendResponse);
    return true;
  };

  Object.assign(G, { collectLinks, mergeFrameLinks, sendLinks, sendLinkList });
})(typeof self !== "undefined" ? self : globalThis);
