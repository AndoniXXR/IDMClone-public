// IDMClone Integration — teclas especiales (como IDM):
//   - mantener pulsada la tecla "para no descargar con IDMClone" (por defecto Supr): la descarga sigue en el navegador;
//   - mantener pulsada la tecla "para forzar la descarga" (por defecto Insert, desactivada): va a IDMClone sea del tipo que sea.
// Las teclas se eligen en IDMClone (Opciones > General). Este script solo avisa al fondo (background.js) de cuándo se
// pulsan y se sueltan ESAS teclas (nunca de lo que se escribe) y de qué enlace se pulsó con ellas; el fondo las aplica a
// cualquier descarga que empiece mientras tanto (enlace, formulario, descarga lanzada por la página, PDF o vídeo que el
// navegador abriría). El clic no se toca: la descarga sale del navegador tal cual (redirecciones, POST, referer).
(() => {
  "use strict";
  const B = (typeof browser !== "undefined" && browser.runtime) ? browser : chrome;
  // background.js lo mira antes de poner este script en las pestañas abiertas al instalar o actualizar: si la copia
  // anterior se quedó huérfana (extensión actualizada), su runtime ya no tiene id y se pone otra.
  const alive = () => { try { return !!(B.runtime && B.runtime.id); } catch (e) { return false; } };
  self.__idmcloneAlive = alive;
  let keys = { prevent: "del", force: "" };   // valores de IDM hasta saber los de IDMClone
  const down = { del: false, ins: false };
  const NAMES = { Delete: "del", Insert: "ins", Alt: "alt", Control: "ctrl", Shift: "shift" };
  const sentAt = {};          // tecla -> último aviso de "pulsada" (los repetidos de una tecla mantenida: uno por segundo)
  const reported = new Set(); // teclas avisadas como pulsadas desde este marco (para soltarlas si pierde el foco)

  function apply(a) {
    if (a && typeof a.preventKeys === "string") keys = { prevent: a.preventKeys, force: a.forceKeys || "" };
  }
  try {
    Promise.resolve(B.storage.local.get({ appCfg: null })).then((s) => apply(s && s.appCfg)).catch(() => {});
    B.storage.onChanged.addListener((ch) => { if (ch.appCfg) apply(ch.appCfg.newValue); });
  } catch (e) { }

  function report(m) {
    if (!alive()) return; // copia huérfana (extensión actualizada): ya no puede hablar con el fondo
    try { const p = B.runtime.sendMessage(m); if (p && typeof p.catch === "function") p.catch(() => {}); } catch (x) { }
  }
  // ¿Forma parte de alguna de las combinaciones configuradas? Solo de esas teclas se avisa.
  function watched(name) { return (keys.prevent + "+" + keys.force).split("+").indexOf(name) >= 0; }

  // Solo teclas de verdad (isTrusted): una página no puede fingir que el usuario mantiene pulsada una tecla.
  function onKey(e) {
    if (!e.isTrusted) return;
    const name = NAMES[e.key];
    if (!name) return;
    const isDown = e.type === "keydown";
    if (name === "del" || name === "ins") down[name] = isDown;
    if (!watched(name)) return;
    const now = Date.now();
    if (isDown && e.repeat && now - (sentAt[name] || 0) < 1000) return; // sigue pulsada: basta un aviso por segundo
    sentAt[name] = isDown ? now : 0;
    if (isDown) reported.add(name); else reported.delete(name);
    report({ type: "idm-keys", key: name, down: isDown });
  }
  addEventListener("keydown", onKey, true);
  addEventListener("keyup", onKey, true);
  // Solo cuando la ventana pierde el foco (no al pasar el foco de un enlace a otro: eso ocurre justo en el clic): el
  // keyup llegará a otra ventana, así que se dan por sueltas (como IDM).
  addEventListener("blur", (e) => {
    if (e.target !== window) return;
    down.del = down.ins = false;
    if (reported.size) { reported.clear(); report({ type: "idm-keys", reset: true }); }
  });

  function held(combo, e) {
    if (!combo) return false;
    return combo.split("+").every((k) =>
      k === "alt" ? e.altKey : k === "ctrl" ? e.ctrlKey : k === "shift" ? e.shiftKey : k === "del" ? down.del : k === "ins" ? down.ins : false);
  }

  // El enlace pulsado con la tecla: el fondo lo recuerda unos segundos por si la descarga tarda en empezar (redirecciones).
  // Solo un clic del usuario (no un a.click() de la página). El clic sigue su curso: no se cancela.
  function onClick(e) {
    if (!e.isTrusted) return;
    const a = e.target && e.target.closest ? e.target.closest("a[href], area[href]") : null;
    if (!a || !/^(https?|ftps?):/i.test(a.href)) return;
    if (held(keys.prevent, e)) report({ type: "prevent-capture", url: a.href, referrer: location.href });
    else if (held(keys.force, e)) report({ type: "force-mark", url: a.href, referrer: location.href });
  }
  addEventListener("click", onClick, true);
})();

// Panel "Descargar este video" (como el de IDM): aparece encima del reproductor cuando la página reproduce
// un vídeo o un audio que IDMClone puede bajar. Con varias calidades, el botón abre la lista para elegir.
// Lo avisa background.js (detecta las peticiones de vídeo); el panel vive en un Shadow DOM CERRADO (la página no
// llega a sus botones; solo este script guarda la referencia) para que los estilos de la página no le afecten, y no
// lee nada de la página salvo dónde están sus <video>/<audio> y sus <track> de subtítulos (dirección, idioma y nombre).
// Solo un clic de verdad del usuario (isTrusted) envía algo: un clic simulado por un script de la página no hace nada.
(() => {
  "use strict";
  const B = (typeof browser !== "undefined" && browser.runtime) ? browser : chrome;
  const L = (typeof self !== "undefined" && self.IDMLogic) || null;
  if (!L) return;
  const t = L.t;
  const entries = [];
  let host = null, root = null, closed = false, mini = false, timer = 0, types = null;

  try {
    Promise.resolve(B.storage.local.get({ appCfg: null })).then((s) => {
      mini = !!(s && s.appCfg && s.appCfg.mediaMini);
      types = s && s.appCfg && Array.isArray(s.appCfg.mediaTypes) ? s.appCfg.mediaTypes : null; // tipos del panel (VTT, SRT...)
      if (host) render();
    }).catch(() => {});
  } catch (e) { }

  B.runtime.onMessage.addListener((msg) => {
    // La página cambió de dirección sin recargarse (history.pushState): sus vídeos son otros (como IDM).
    if (msg && msg.type === "idm-media-reset") {
      for (const m of document.querySelectorAll("video, audio")) if (!m.paused && !m.ended) return; // se sigue viendo
      entries.length = 0;
      closed = false;
      if (host) { host.remove(); host = null; root = null; clearInterval(timer); }
      return;
    }
    if (msg && msg.type === "idm-media-remove") {
      const i = entries.findIndex((e) => e.url === msg.url);
      if (i >= 0) { entries.splice(i, 1); if (host) { if (entries.length) render(); else { host.remove(); host = null; root = null; clearInterval(timer); } } }
      return;
    }
    if (!msg || msg.type !== "idm-media" || !msg.entry) return;
    if (entries.some((e) => e.url === msg.entry.url)) return;
    entries.unshift(msg.entry);
    if (!closed) show();
  });

  // El reproductor: el <video> visible más grande (o un <audio> con controles).
  function anchor() {
    let best = null, area = 0;
    for (const el of document.querySelectorAll("video, audio")) {
      const r = el.getBoundingClientRect();
      const a = r.width * r.height;
      if (r.width < 120 || r.height < 30 || a <= area) continue;
      const st = getComputedStyle(el);
      if (st.visibility === "hidden" || st.display === "none") continue;
      best = el; area = a;
    }
    return best;
  }

  const CSS = `
    :host { all: initial; position: absolute; z-index: 2147483647; font: 12px "Segoe UI", Arial, sans-serif;
            opacity: 1 !important; visibility: visible !important; filter: none !important; mask: none !important;
            -webkit-mask: none !important; mix-blend-mode: normal !important; } /* la página no puede volverlo invisible bajo el cursor */
    .status { margin-top: 3px; padding: 4px 8px; border-radius: 3px; background: #fff; color: #1b1b1b; border: 1px solid #9aa7b6; max-width: 320px;
              box-shadow: 0 2px 6px rgba(0,0,0,.3); cursor: pointer; }
    .status.ok { border-color: #2e8b3e; color: #1d6b2b; }
    .status.err { border-color: #c0392b; color: #a12a1d; }
    .bar { display: flex; align-items: center; gap: 2px; user-select: none; }
    button { all: unset; cursor: pointer; display: flex; align-items: center; box-sizing: border-box; }
    .bar button { color: #1e3c78; border: 1px solid #7ea2ce; border-radius: 2px; background: linear-gradient(#f6faff, #bed8f3); box-shadow: 0 1px 3px rgba(0,0,0,.35); }
    .bar button:hover { background: linear-gradient(#ffffff, #d4e6f8); border-color: #4f86c6; }
    .bar button:focus-visible { outline: 2px solid #1e3c78; outline-offset: 1px; }
    .main { gap: 6px; padding: 3px 9px 3px 6px; font: bold 14px Tahoma, "Segoe UI", Arial, sans-serif; white-space: nowrap; }
    .help, .x { justify-content: center; width: 18px; height: 18px; font: bold 12px Tahoma, Arial, sans-serif; }
    svg { width: 14px; height: 14px; flex: none; }
    .menu { position: absolute; right: 0; top: calc(100% + 3px); min-width: 230px; max-height: 320px; overflow: auto; background: #fff; color: #1b1b1b;
            border: 1px solid #9aa7b6; border-radius: 3px; box-shadow: 0 3px 10px rgba(0,0,0,.35); padding: 3px 0; }
    .menu[hidden] { display: none; }
    .item { display: flex; justify-content: space-between; gap: 14px; width: 100%; box-sizing: border-box; padding: 5px 12px; color: #1b1b1b; }
    .item:hover, .item:focus-visible { background: #cfe3fb; outline: none; }
    .item .size { color: #5d6670; }
    .head { padding: 4px 12px 2px; color: #5d6670; font-size: 11px; }
    hr { border: 0; border-top: 1px solid #dde3ea; margin: 3px 0; }
    .note { padding: 6px 12px; color: #8a2b14; max-width: 300px; white-space: normal; }
  `;
  // Como el panel de IDM: botón claro con degradado azul y triángulo verde, más "?" y "X".
  const ICON = '<svg viewBox="0 0 16 16" aria-hidden="true"><defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#7ddc7d"/><stop offset="1" stop-color="#1a9a2c"/></linearGradient></defs><path d="M3 1.5 14.5 8 3 14.5z" fill="url(#g)" stroke="#16702a" stroke-width=".8" stroke-linejoin="round"/></svg>';

  // Un clic en el primer medio segundo tras aparecer no cuenta: evita que una página lo ponga bajo el cursor justo
  // antes de un clic del usuario.
  let shownAt = 0;
  function tooSoon() { return Date.now() - shownAt < 500; }

  function show() {
    if (!host) {
      host = document.createElement("idmclone-panel");
      root = host.attachShadow({ mode: "closed" }); // referencia privada: host.shadowRoot es null para la página
      (document.body || document.documentElement).appendChild(host);
      shownAt = Date.now();
      addEventListener("scroll", place, true);
      addEventListener("resize", place);
      document.addEventListener("fullscreenchange", place);
      timer = setInterval(place, 700);
    }
    render();
    place();
  }

  function render() {
    const video = entries.some((e) => e.media === "video");
    // Solo marcas propias en el HTML; los textos (traducidos) van como texto, nunca como HTML.
    root.innerHTML = `<style>${CSS}</style>
      <div class="bar" role="toolbar">
        <button class="main">${ICON}${mini ? "" : '<span class="label"></span>'}</button>
        ${mini ? "" : '<button class="help">?</button>'}
        <button class="x">X</button>
      </div>
      <div class="menu" role="menu" hidden></div>`;
    labels(video ? "panelDownloadVideo" : "panelDownloadAudio", video ? "panelDownloadVideoTitle" : "panelDownloadAudioTitle");
    root.querySelector(".main").addEventListener("click", (e) => { e.stopPropagation(); if (e.isTrusted && !tooSoon()) onMain(); });
    root.querySelector(".x").addEventListener("click", (e) => { e.stopPropagation(); if (e.isTrusted) hide(); });
    const help = root.querySelector(".help");
    if (help) help.addEventListener("click", (e) => { e.stopPropagation(); if (e.isTrusted) aboutPanel(); });
  }

  // Textos del panel: título y nombre accesible de cada botón.
  function labels(text, title) {
    root.querySelector(".bar").setAttribute("aria-label", t("panelAria"));
    root.querySelector(".main").setAttribute("title", t(title));
    const label = root.querySelector(".label");
    if (label) label.textContent = t(text);
    const help = root.querySelector(".help");
    if (help) { help.setAttribute("title", t("panelAbout")); help.setAttribute("aria-label", t("panelAbout")); }
    const x = root.querySelector(".x");
    x.setAttribute("title", t("panelClose")); x.setAttribute("aria-label", t("panelClose"));
  }

  function choices() {
    const out = [];
    for (const e of entries) for (const it of e.items || []) out.push({ entry: e, item: it });
    return out.concat(pageTracks());
  }

  // Subtítulos de los <video>/<audio> de la página (<track kind="subtitles|captions">), como IDM: se ofrecen en la
  // lista para bajarlos junto al vídeo. Solo direcciones http(s); los que ya avisó background.js no se repiten.
  function pageTracks() {
    const out = [], seen = new Set(entries.map((e) => String(e.url || "").split("#")[0]));
    for (const m of document.querySelectorAll("video, audio")) {
      for (const tk of m.querySelectorAll ? m.querySelectorAll("track") : []) {
        const kind = String(tk.kind || (tk.getAttribute && tk.getAttribute("kind")) || "subtitles").toLowerCase();
        const src = String(tk.src || "").split("#")[0];
        if ((kind !== "subtitles" && kind !== "captions") || !/^https?:\/\//i.test(src) || seen.has(src) || out.length >= 20) continue;
        seen.add(src);
        // Tipo desmarcado en la lista del panel (como IDM). Un <track> sin extensión conocida es WebVTT (el único que lee el navegador).
        if (!L.subtitleAllowed({ url: src, format: "vtt" }, { mediaTypes: types })) continue;
        const ext = /\.(srt|ttml|dfxp)(?:[?]|$)/i.test(src) ? ".srt" : ".vtt"; // TTML: IDMClone lo pasa a SRT
        out.push({ entry: { kind: "subs", url: src, media: "subtitle" },
                   item: { kind: "subtitle", ext, url: src, language: String(tk.srclang || "").substring(0, 12), name: String(tk.label || "").substring(0, 60) } });
      }
    }
    return out;
  }

  // Un elemento de la lista (calidad, solo audio o subtítulos).
  function addItem(menu, c) {
    const b = document.createElement("button"); b.className = "item"; b.setAttribute("role", "menuitem");
    const label = L.mediaLabel(c.item), sep = label.indexOf("  ");
    const name = document.createElement("span"); name.textContent = sep > 0 ? label.substring(0, sep) : label;
    const size = document.createElement("span"); size.className = "size"; size.textContent = sep > 0 ? label.substring(sep + 2) : "";
    b.appendChild(name); b.appendChild(size);
    b.addEventListener("click", (ev) => { ev.stopPropagation(); if (!ev.isTrusted || tooSoon()) return; menu.hidden = true; send(c); });
    menu.appendChild(b);
  }

  function onMain() {
    const all = choices();
    if (all.length === 1 && !all[0].entry.protected) { send(all[0]); return; }
    const menu = root.querySelector(".menu");
    if (!menu.hidden) { menu.hidden = true; return; }
    menu.innerHTML = "";
    const videos = entries.filter((e) => e.media !== "subtitle");
    for (const e of videos) {
      if (videos.length > 1 || e.duration > 0) {
        const h = document.createElement("div"); h.className = "head";
        h.textContent = t(e.media === "audio" ? "panelHeadAudio" : "panelHeadVideo") + (e.live ? t("panelLive") : e.duration > 0 ? t("panelDuration", L.formatDuration(e.duration)) : "");
        menu.appendChild(h);
      }
      if (e.protected) {
        const n = document.createElement("div"); n.className = "note";
        n.textContent = t("panelDrm");
        menu.appendChild(n);
        continue;
      }
      for (const it of e.items || []) addItem(menu, { entry: e, item: it });
    }
    // Subtítulos sueltos (pedidos por el reproductor o <track> del vídeo): "Subtítulos: Español (vtt)".
    const subs = [];
    for (const e of entries) if (e.media === "subtitle") for (const it of e.items || []) subs.push({ entry: e, item: it });
    const extra = subs.concat(pageTracks());
    if (extra.length) {
      menu.appendChild(document.createElement("hr"));
      for (const c of extra) addItem(menu, c);
    }
    menu.appendChild(document.createElement("hr"));
    const about = document.createElement("button"); about.className = "item"; about.textContent = t("panelAbout");
    about.addEventListener("click", (ev) => { ev.stopPropagation(); if (!ev.isTrusted) return; menu.hidden = true; aboutPanel(); });
    menu.appendChild(about);
    menu.hidden = false;
    const first = menu.querySelector(".item"); if (first) first.focus();
  }

  function aboutPanel() {
    alert(t("panelAboutText"));
  }

  // Al elegir: se avisa en el propio panel de si IDMClone la recibió (o del motivo si no).
  function send(c) {
    status(t("panelSending"), "");
    let p;
    try { p = B.runtime.sendMessage({ type: "media-download", entry: c.entry, item: c.item }); }
    catch (e) { status(t("panelSendFailReload", String(e && e.message || e)), "err"); return; }
    Promise.resolve(p).then((r) => {
      if (r && r.ok) status(t("panelSent"), "ok");
      else status((r && r.error) || t("panelNoReply"), "err");
    }).catch((e) => status(t("panelSendFailUpdated", String(e && e.message || e)), "err"));
  }

  let statusTimer = 0;
  function status(text, kind) {
    if (!root) return;
    let n = root.querySelector(".status");
    if (!n) { n = document.createElement("div"); n.className = "status"; root.appendChild(n); n.addEventListener("click", () => n.remove()); }
    n.textContent = text;
    n.className = "status " + kind;
    clearTimeout(statusTimer);
    if (kind === "ok") statusTimer = setTimeout(() => { if (n.parentNode) n.remove(); }, 4000);
  }

  function hide() {
    closed = true;
    if (host) { host.remove(); host = null; root = null; }
    clearInterval(timer);
  }

  // Encima del reproductor, arriba a la derecha (como IDM); sin reproductor visible, arriba a la derecha de la ventana.
  function place() {
    if (!host) return;
    // Extensión actualizada o recargada: esta copia ya no puede hablar con ella; el panel se quita (la nueva pone el suyo).
    let alive = false;
    try { alive = !!(B.runtime && B.runtime.id); } catch (e) { }
    if (!alive) { host.remove(); host = null; root = null; clearInterval(timer); return; }
    const fs = document.fullscreenElement;
    const parent = fs && fs.tagName !== "VIDEO" ? fs : (document.body || document.documentElement);
    const before = host.style.position + "|" + host.style.top + "|" + host.style.left;
    if (host.parentNode !== parent) { parent.appendChild(host); shownAt = Date.now(); }
    const a = anchor();
    const w = host.offsetWidth || 180;
    if (a && (!fs || fs.contains(a) || fs === a)) {
      const r = a.getBoundingClientRect();
      const fixed = !!fs;
      // Como IDM: encima del reproductor, pegado a su esquina superior derecha. Si no cabe encima (vídeo arriba del
      // todo, desplazado o en pantalla completa), dentro, en la parte visible del reproductor.
      const h = host.offsetHeight || 22;
      let top = r.top - h - 1, left = r.right - w;
      if (fixed || top < 0) { top = Math.min(Math.max(r.top, 0) + 4, Math.max(r.top, r.bottom - h)); left -= 4; }
      host.style.position = fixed ? "fixed" : "absolute";
      host.style.top = Math.max(0, top + (fixed ? 0 : scrollY)) + "px";
      host.style.left = Math.max(0, left + (fixed ? 0 : scrollX)) + "px";
    } else {
      host.style.position = "fixed";
      host.style.top = "8px";
      host.style.left = Math.max(0, innerWidth - w - 12) + "px";
    }
    // Si la página lo ha movido (moviendo el vídeo, pantalla completa...), un clic en el medio segundo siguiente no cuenta.
    if (host.style.position + "|" + host.style.top + "|" + host.style.left !== before) shownAt = Date.now();
  }
})();

// Panel "Descargar con IDMClone" para los enlaces seleccionados (como el de IDM, #11): al seleccionar un texto que
// contiene enlaces (o direcciones escritas), aparece junto a la selección; al pulsarlo, IDMClone abre la lista de
// esos enlaces para elegir cuáles descargar (el mismo camino que "Descargar los enlaces seleccionados" del menú).
// Privacidad: solo se mira la selección después de que el usuario suelte el ratón o una tecla, y no se manda nada
// hasta que pulsa el panel. El panel vive en un Shadow DOM cerrado y solo contiene texto propio (nada de la página).
// Se configura en IDMClone > Opciones > General > panel > "Para los archivos seleccionados".
(() => {
  "use strict";
  const B = (typeof browser !== "undefined" && browser.runtime) ? browser : chrome;
  const L = (typeof self !== "undefined" && self.IDMLogic) || null;
  if (!L || !/^https?:$/.test(location.protocol)) return;
  const t = L.t;
  let cfg = null, host = null, root = null, builtMini = null, links = [], closed = false, timer = 0, statusTimer = 0;

  function apply(a) { cfg = a || null; if (!enabled()) hide(); }
  try {
    Promise.resolve(B.storage.local.get({ appCfg: null })).then((s) => apply(s && s.appCfg)).catch(() => {});
    B.storage.onChanged.addListener((ch) => { if (ch.appCfg) apply(ch.appCfg.newValue); });
  } catch (e) { }

  // #1: con el navegador desmarcado en IDMClone no hay paneles (como IDM 6.43); el menú contextual sigue.
  function enabled() {
    let alive = false; // copia huérfana tras actualizar la extensión: sin panel (la nueva pone el suyo)
    try { alive = !!(B.runtime && B.runtime.id); } catch (e) { }
    return alive && !!(cfg && !cfg.captureOff && cfg.selectionPanel && !closed && L.selectionPanelAllowed(location.href, cfg.selectionPanel));
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

  const CSS = `
    :host { all: initial; position: absolute; z-index: 2147483647; font: 12px "Segoe UI", Arial, sans-serif;
            opacity: 1 !important; visibility: visible !important; filter: none !important; mask: none !important;
            -webkit-mask: none !important; mix-blend-mode: normal !important; } /* la página no puede volverlo invisible bajo el cursor */
    .bar { display: flex; align-items: center; gap: 2px; user-select: none; }
    button { all: unset; cursor: pointer; display: flex; align-items: center; box-sizing: border-box; }
    .bar button { color: #1e3c78; border: 1px solid #7ea2ce; border-radius: 2px; background: linear-gradient(#f6faff, #bed8f3); box-shadow: 0 1px 3px rgba(0,0,0,.35); }
    .bar button:hover { background: linear-gradient(#ffffff, #d4e6f8); border-color: #4f86c6; }
    .bar button:focus-visible { outline: 2px solid #1e3c78; outline-offset: 1px; }
    .main { gap: 6px; padding: 3px 9px 3px 6px; font: bold 13px Tahoma, "Segoe UI", Arial, sans-serif; white-space: nowrap; }
    .help, .x { justify-content: center; width: 18px; height: 18px; font: bold 12px Tahoma, Arial, sans-serif; }
    svg { width: 14px; height: 14px; flex: none; }
    .status { margin-top: 3px; padding: 4px 8px; border-radius: 3px; background: #fff; color: #1b1b1b; border: 1px solid #9aa7b6; max-width: 320px;
              box-shadow: 0 2px 6px rgba(0,0,0,.3); }
    .status.ok { border-color: #2e8b3e; color: #1d6b2b; }
    .status.err { border-color: #c0392b; color: #a12a1d; }
  `;
  const ICON = '<svg viewBox="0 0 16 16" aria-hidden="true"><defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#7ddc7d"/><stop offset="1" stop-color="#1a9a2c"/></linearGradient></defs><path d="M3 1.5 14.5 8 3 14.5z" fill="url(#g)" stroke="#16702a" stroke-width=".8" stroke-linejoin="round"/></svg>';

  function build(mini) {
    // Solo texto y marcas propias (constantes): nada que venga de la página.
    root.innerHTML = `<style>${CSS}</style>
      <div class="bar" role="toolbar">
        <button class="main">${ICON}${mini ? "" : '<span class="label"></span>'}</button>
        ${mini ? "" : '<button class="help">?</button>'}
        <button class="x">X</button>
      </div>`;
    // Los textos (traducidos), como texto y atributos: nunca como HTML.
    root.querySelector(".bar").setAttribute("aria-label", t("panelAria"));
    root.querySelector(".main").setAttribute("title", t("menuSelected"));
    const label = root.querySelector(".label");
    if (label) label.textContent = t("menuDownload");
    const h = root.querySelector(".help");
    if (h) { h.setAttribute("title", t("panelAbout")); h.setAttribute("aria-label", t("panelAbout")); }
    root.querySelector(".x").setAttribute("title", t("selClose"));
    root.querySelector(".x").setAttribute("aria-label", t("panelClose"));
    builtMini = mini;
    // Solo clics de verdad del usuario (isTrusted) envían o cierran algo.
    root.querySelector(".main").addEventListener("click", (e) => { e.stopPropagation(); if (e.isTrusted && !tooSoon()) send(); });
    root.querySelector(".x").addEventListener("click", (e) => { e.stopPropagation(); if (e.isTrusted) { closed = true; hide(); } });
    const help = root.querySelector(".help");
    if (help) help.addEventListener("click", (e) => {
      e.stopPropagation();
      if (!e.isTrusted) return;
      alert(t("selAboutText"));
    });
  }

  let shownAt = 0; // como en el panel de vídeo: un clic en el primer medio segundo no cuenta
  function tooSoon() { return Date.now() - shownAt < 500; }

  function show(range) {
    const mini = !!cfg.selectionPanel.mini;
    if (!host) {
      host = document.createElement("idmclone-selection-panel");
      root = host.attachShadow({ mode: "closed" });
      // Pulsar el panel no debe quitar la selección de la página.
      host.addEventListener("mousedown", (e) => { e.preventDefault(); e.stopPropagation(); });
      builtMini = null;
    }
    if (builtMini !== mini) build(mini);
    const old = root.querySelector(".status");
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
    try { p = B.runtime.sendMessage({ type: "selection-links", links: list }); }
    catch (e) { status(t("panelSendFailReload", String(e && e.message || e)), "err"); return; }
    Promise.resolve(p).then((r) => {
      if (r && r.ok) { status(list.length === 1 ? t("selSentOne") : t("selSentMany", String(list.length)), "ok"); statusTimer = setTimeout(hide, 2500); }
      else status((r && r.error) || t("panelNoReply"), "err");
    }).catch((e) => status(t("panelSendFailUpdated", String(e && e.message || e)), "err"));
  }

  function status(text, kind) {
    if (!root) return;
    let n = root.querySelector(".status");
    if (!n) { n = document.createElement("div"); n.className = "status"; root.appendChild(n); }
    n.textContent = text;
    n.className = "status " + kind;
  }
})();
