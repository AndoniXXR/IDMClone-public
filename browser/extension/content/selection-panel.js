// Panel "Descargar con IDMClone" para los enlaces seleccionados (como el de IDM, #11): al seleccionar un texto que
// contiene enlaces (o direcciones escritas), aparece junto a la selección; al pulsarlo, IDMClone abre la lista de esos
// enlaces para elegir cuáles descargar (el mismo camino que "Descargar los enlaces seleccionados" del menú).
// Privacidad: solo se mira la selección después de que el usuario suelte el ratón o una tecla, y no se manda nada hasta
// que pulsa el panel. El panel vive en un Shadow DOM cerrado y solo contiene texto propio (nada de la página).
// Se configura en IDMClone > Opciones > General > panel > "Para los archivos seleccionados".
(function (root) {
  "use strict";
  const U = root.IDMContent;
  const L = root.IDMLogic;
  if (!U || !L || !/^https?:$/.test(location.protocol)) return;
  const t = L.t;
  let cfg = null, host = null, shadow = null, builtMini = null, links = [], closed = false, timer = 0, statusTimer = 0;

  U.onConfig((a) => { cfg = a || null; if (!enabled()) hide(); });

  // #1: con el navegador desmarcado en IDMClone no hay paneles (como IDM 6.43); el menú contextual sigue.
  // Copia huérfana tras actualizar la extensión: sin panel (la nueva pone el suyo).
  function enabled() {
    return U.alive() && !!(cfg && !cfg.captureOff && cfg.selectionPanel && !closed && L.selectionPanelAllowed(location.href, cfg.selectionPanel));
  }

  function editable(node) {
    const el = node && (node.nodeType === 1 ? node : node.parentElement);
    return !!(el && (el.isContentEditable || (el.closest && el.closest("input, textarea, select"))));
  }

  // Enlaces de la selección: <a href> tocados por ella y direcciones escritas en el texto seleccionado.
  function collect(sel) {
    const out = [];
    const range = sel.getRangeAt(0);
    let base = range.commonAncestorContainer;
    if (base && base.nodeType !== 1) base = base.parentElement;
    if (!base) return out;
    const cands = [];
    const inside = base.closest ? base.closest("a[href]") : null;
    if (inside) cands.push(inside);
    for (const a of base.querySelectorAll("a[href]")) { if (cands.length >= 5000) break; cands.push(a); }
    for (const a of cands) {
      if (out.length >= 1000) break;
      if (sel.containsNode(a, true)) out.push({ url: a.href, text: a.textContent || a.title || "" });
    }
    for (const u of L.textUrls(sel.toString(), 1000)) out.push({ url: u, text: "" });
    return L.sanitizeLinks(out, 1000);
  }

  function check() {
    timer = 0;
    if (!enabled()) { hide(); return; }
    if (window !== window.top && (innerWidth < 200 || innerHeight < 80)) return; // marcos diminutos (anuncios...)
    const sel = getSelection();
    if (!sel || sel.isCollapsed || sel.rangeCount === 0) { hide(); return; }
    if (editable(document.activeElement) || editable(sel.anchorNode) || editable(sel.focusNode)) { hide(); return; }
    const found = collect(sel);
    if (!found.length) { hide(); return; }
    links = found;
    show(sel.getRangeAt(sel.rangeCount - 1));
  }

  function onPanel(e) { return !!(host && e && typeof e.composedPath === "function" && e.composedPath().indexOf(host) >= 0); }
  function schedule(e) {
    if (!e || !e.isTrusted || onPanel(e) || !cfg || !cfg.selectionPanel) return; // solo acciones del usuario
    clearTimeout(timer);
    timer = setTimeout(check, 200);
  }
  addEventListener("mouseup", schedule, true);
  addEventListener("keyup", (e) => { if (e.isTrusted && e.key === "Escape") { hide(); return; } schedule(e); }, true);
  addEventListener("mousedown", (e) => { if (e.isTrusted && host && !onPanel(e)) hide(); }, true);

  const CSS = U.BASE_CSS + `
    .main { gap: 6px; padding: 3px 9px 3px 6px; font: bold 13px Tahoma, "Segoe UI", Arial, sans-serif; white-space: nowrap; }
  `;
  const ICON = U.ICON;

  function build(mini) {
    // Solo texto y marcas propias (constantes): nada que venga de la página.
    shadow.innerHTML = `<style>${CSS}</style>
      <div class="bar" role="toolbar">
        <button class="main">${ICON}${mini ? "" : '<span class="label"></span>'}</button>
        ${mini ? "" : '<button class="help">?</button>'}
        <button class="x">X</button>
      </div>`;
    // Los textos (traducidos), como texto y atributos: nunca como HTML.
    shadow.querySelector(".bar").setAttribute("aria-label", t("panelAria"));
    shadow.querySelector(".main").setAttribute("title", t("menuSelected"));
    const label = shadow.querySelector(".label");
    if (label) label.textContent = t("menuDownload");
    const h = shadow.querySelector(".help");
    if (h) { h.setAttribute("title", t("panelAbout")); h.setAttribute("aria-label", t("panelAbout")); }
    shadow.querySelector(".x").setAttribute("title", t("selClose"));
    shadow.querySelector(".x").setAttribute("aria-label", t("panelClose"));
    builtMini = mini;
    // Solo clics de verdad del usuario (isTrusted) envían o cierran algo.
    shadow.querySelector(".main").addEventListener("click", (e) => { e.stopPropagation(); if (e.isTrusted && !tooSoon()) send(); });
    shadow.querySelector(".x").addEventListener("click", (e) => { e.stopPropagation(); if (e.isTrusted) { closed = true; hide(); } });
    if (h) h.addEventListener("click", (e) => {
      e.stopPropagation();
      if (!e.isTrusted) return;
      alert(t("selAboutText"));
    });
  }

  let shownAt = 0;
  function tooSoon() { return Date.now() - shownAt < U.CLICK_GUARD_MS; }

  function show(range) {
    const mini = !!cfg.selectionPanel.mini;
    if (!host) {
      host = document.createElement("idmclone-selection-panel");
      shadow = host.attachShadow({ mode: "closed" });
      // Pulsar el panel no debe quitar la selección de la página.
      host.addEventListener("mousedown", (e) => { e.preventDefault(); e.stopPropagation(); });
      builtMini = null;
    }
    if (builtMini !== mini) build(mini);
    const old = shadow.querySelector(".status");
    if (old) old.remove();
    // En <html> y no en <body>: un <body> con position:relative y márgenes desplazaría el panel.
    if (host.parentNode !== document.documentElement) { document.documentElement.appendChild(host); shownAt = Date.now(); }
    let r = range.getBoundingClientRect();
    const rects = range.getClientRects();
    if (rects && rects.length) r = rects[rects.length - 1]; // junto al final de la selección
    const w = host.offsetWidth || 190;
    const before = host.style.top + "|" + host.style.left;
    host.style.position = "absolute";
    host.style.top = Math.max(0, r.bottom + scrollY + 4) + "px";
    host.style.left = Math.max(0, Math.min(r.right + scrollX - w, scrollX + innerWidth - w - 8)) + "px";
    if (host.style.top + "|" + host.style.left !== before) shownAt = Date.now(); // recolocado: vuelve a contar
  }

  function hide() {
    clearTimeout(statusTimer);
    if (host) { host.remove(); }
  }

  function send() {
    const list = links.slice(0, 1000);
    status(t("panelSending"), "");
    let p;
    try { p = U.B.runtime.sendMessage({ type: "selection-links", links: list }); }
    catch (e) { status(t("panelSendFailReload", String(e && e.message || e)), "err"); return; }
    Promise.resolve(p).then((r) => {
      if (r && r.ok) { status(list.length === 1 ? t("selSentOne") : t("selSentMany", String(list.length)), "ok"); statusTimer = setTimeout(hide, 2500); }
      else status((r && r.error) || t("panelNoReply"), "err");
    }).catch((e) => status(t("panelSendFailUpdated", String(e && e.message || e)), "err"));
  }

  function status(text, kind) {
    if (!shadow) return;
    let n = shadow.querySelector(".status");
    if (!n) { n = document.createElement("div"); n.className = "status"; shadow.appendChild(n); }
    n.textContent = text;
    n.className = "status " + kind;
  }
})(typeof self !== "undefined" ? self : globalThis);
