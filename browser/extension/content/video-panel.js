// Panel "Descargar este video" (como el de IDM): uno por cada vídeo de la página que IDMClone puede bajar, encima de su
// reproductor. Con varias calidades, el botón abre la lista para elegir.
// Lo detectado lo avisa el fondo (bg/media-*.js); aquí se une cada cosa con su <video> (content/tracker.js): por su
// dirección, por las pistas del sitio (en X, el número del medio que llevan también el póster), por el que se está
// reproduciendo al detectarlo o, si no, por el más grande que se ve. Cada panel aparece y se oculta con su vídeo (se ve
// al menos un 20 %, como IDM) y lo sigue si la página lo mueve o lo sustituye. Lo que no tiene <video> conocido (un audio
// suelto...) va a un panel general arriba a la derecha de la ventana, como antes.
// Solo un clic de verdad del usuario (isTrusted) envía algo: un clic simulado por un script de la página no hace nada.
(function (root) {
  "use strict";
  const U = root.IDMContent;
  const L = root.IDMLogic;
  if (!U || !L || !U.tracker) return;
  const T = U.tracker;
  const t = L.t;

  const RECENT_MS = 5000;      // lo recién detectado puede unirse al vídeo que se está reproduciendo o al más grande
  const NAV_MARGIN_MS = 2500;  // tras cambiar de dirección: lo detectado poco antes del aviso ya es de la página nueva
  const MAX_ENTRIES = 200;

  const entries = [];          // { entry, recId } — recId: 0 sin elemento (panel general)
  const panels = new Map();    // recId (0 = general) -> panel
  let cfg = null, navAt = 0, generalClosed = false, timer = 0;

  U.onConfig((a) => { cfg = a || null; refresh(); });

  function off() {
    // #1: navegador desmarcado en IDMClone; o panel desactivado (los dos llegan en vivo con la configuración).
    return !U.alive() || !!(cfg && (cfg.captureOff || cfg.mediaPanel === false));
  }

  const noHash = (u) => String(u || "").split("#")[0];
  function entryUrls(e) {
    const s = new Set([noHash(e.url)]);
    for (const it of e.items || []) for (const u of [it && it.url, it && it.video, it && it.audio]) if (u && /^https?:/i.test(u)) s.add(noHash(u));
    return s;
  }

  // Pistas de la entrada que tiene este elemento ("yt:abc" en las dos) y si el elemento es claramente de OTRO vídeo
  // del mismo sitio (tiene una pista "yt:..." distinta).
  const hasHint = (rec, e) => (e.hints || []).some((h) => rec.hints.has(h));
  const otherVideo = (rec, e) => (e.hints || []).some((h) => {
    const p = h.substring(0, h.indexOf(":") + 1);
    return [...rec.hints].some((x) => x !== h && x.indexOf(p) === 0);
  });

  // ¿A qué <video> pertenece? { id: 0 si a ninguno conocido, how: "url" | "hint" | "guess" }.
  function bindEntry(x) {
    const e = x.entry;
    const recs = T.records();
    const urls = entryUrls(e);
    // Por la pista, mejor uno que se ve: YouTube tiene oculto el reproductor de la vista previa de la portada.
    const byHint = (e.hints && e.hints.length) ? recs.filter((rec) => hasHint(rec, e)).sort((a, b) => T.visible(b) - T.visible(a))[0] : null;
    let r = recs.find((rec) => [...T.urlsOf(rec.el)].some((u) => urls.has(u)));
    if (r) return { id: r.id, how: "url" };
    if (byHint) return { id: byHint.id, how: "hint" };
    if (e.media === "subtitle") {
      // Subtítulos sueltos: con el vídeo de lo último que se detectó.
      const last = entries.filter((y) => y.recId && y.entry.media !== "subtitle").pop();
      if (last) return { id: last.recId, how: "guess" };
    }
    if (Date.now() - (e.at || 0) < RECENT_MS && e.media !== "audio") {
      const vis = recs.filter((rec) => T.visible(rec) && rec.el.tagName === "VIDEO" && !otherVideo(rec, e));
      r = vis.filter((rec) => rec.playedAt && Date.now() - rec.playedAt < RECENT_MS).sort((a, b) => b.playedAt - a.playedAt)[0] ||
          vis.find((rec) => T.underMouse(rec)) ||
          vis.sort((a, b) => area(b) - area(a))[0];
    }
    return { id: r ? r.id : 0, how: "guess" };
  }
  const area = (rec) => (rec.rect ? rec.rect.width * rec.rect.height : 0);

  // ¿Sigue valiendo la unión de antes? No si su vídeo ya no está (se quitó hace más de unos segundos), si el elemento
  // ya reproduce otro vídeo (YouTube usa el mismo <video> para todo), o si se unió por la pista y ese elemento está
  // oculto mientras otro con la misma pista se ve (la vista previa de la portada frente al reproductor del vídeo).
  // Una unión "a ojo" cede ante la pista en cuanto algún elemento la tiene.
  function stillBound(x) {
    const rec = T.byId(x.recId);
    if (!rec) return false;
    const e = x.entry;
    if (x.how === "url") return true;
    if (otherVideo(rec, e) && !hasHint(rec, e)) return false;
    if (e.hints && e.hints.length) {
      const better = T.records().find((r) => r !== rec && hasHint(r, e) && T.visible(r));
      if (better && (x.how === "guess" || !T.visible(rec))) return false;
    }
    return true;
  }

  // Lo que va en cada panel: sus entradas (las del general, solo las de la página actual).
  function groups() {
    const out = new Map();
    for (const x of entries) {
      if (x.recId && !stillBound(x)) x.recId = 0;
      if (!x.recId) { const b = bindEntry(x); x.recId = b.id; x.how = b.how; if (b.id) x.wasBound = true; }
      const k = x.recId;
      if (!k && (x.entry.at || 0) < navAt - NAV_MARGIN_MS) continue; // de una página anterior y sin vídeo en esta
      // Con pista del sitio pero sin su vídeo en la página (otra vista previa, otra publicación, o su elemento ya
      // reproduce otro vídeo): no va al general. Solo un momento al llegar, por si su <video> aún no tiene la pista.
      if (!k && x.entry.source === "site" && x.entry.hints && x.entry.hints.length && (x.wasBound || Date.now() - (x.entry.at || 0) > RECENT_MS)) continue;
      // Su elemento suena sin imagen (soundOnly): al panel general, como audio.
      const sound = !!k && T.soundOnly(T.byId(k));
      const g = sound ? 0 : k;
      if (!out.has(g)) out.set(g, []);
      out.get(g).push(sound ? audioView(x.entry) : x.entry);
    }
    return out;
  }

  // Lo que suena sin imagen se ofrece como audio: si trae calidades de vídeo y de audio (YouTube), solo las de audio.
  // Cada una guarda su número en la entrada de verdad (itemIndex), que es lo que se envía al elegirla.
  function audioView(e) {
    if (e.media !== "video") return e;
    const idx = [];
    (e.items || []).forEach((it, i) => { if (it && it.kind === "audio") idx.push(i); });
    if (!idx.length) return e;
    return Object.assign({}, e, { media: "audio", items: idx.map((i) => e.items[i]), itemIndex: idx, viewOf: e });
  }
  // Una opción del panel: la entrada y el número de la calidad tal como los guardó el fondo.
  const choice = (e, it, i) => ({ entry: e.viewOf || e, item: it, index: e.itemIndex ? e.itemIndex[i] : i });

  function refresh() {
    if (timer) return;
    timer = setTimeout(() => { timer = 0; update(); }, 0);
  }

  function update() {
    if (off()) { for (const p of panels.values()) p.destroy(); panels.clear(); T.ensurePolling(false); return; }
    const g = groups();
    for (const [k, p] of [...panels]) if (!g.has(k)) { p.destroy(); panels.delete(k); }
    for (const [k, list] of g) {
      const rec = k ? T.byId(k) : null;
      const closed = k ? !!(rec && rec.closed) : generalClosed;
      const visible = !closed && list.some((e) => e.media !== "subtitle") && (!k || T.visible(rec));
      let p = panels.get(k);
      if (!visible) { if (p) p.hide(); continue; }
      if (!p) { p = makePanel(k); panels.set(k, p); }
      p.set(list);
      p.show(rec);
    }
    T.ensurePolling(panels.size > 0);
  }
  T.onChange(update);

  // ---- Mensajes del fondo ----
  U.B.runtime.onMessage.addListener((msg) => {
    if (!msg || typeof msg.type !== "string") return;
    if (msg.type === "idm-media" && msg.entry && typeof msg.entry.url === "string") {
      if (entries.some((x) => x.entry.url === msg.entry.url)) return;
      const e = msg.entry;
      if (!e.at) e.at = Date.now();
      T.refresh();
      entries.push({ entry: e, recId: 0 });
      if (entries.length > MAX_ENTRIES) entries.shift();
      update(); // en el acto (como IDM): el aviso llega cuando el vídeo ya se está pidiendo
    } else if (msg.type === "idm-media-remove" && typeof msg.url === "string") {
      const i = entries.findIndex((x) => x.entry.url === msg.url);
      if (i >= 0) { entries.splice(i, 1); update(); }
    } else if (msg.type === "idm-media-nav") {
      // La página cambió de dirección sin recargarse: el panel general se queda con lo nuevo (como IDM, que lo vacía);
      // los vídeos que siguen en pantalla conservan su panel.
      navAt = Date.now();
      generalClosed = false;
      refresh();
    }
  });

  // ---- Un panel ----
  const CSS = U.BASE_CSS + `
    .main { gap: 6px; padding: 3px 9px 3px 6px; font: bold 14px Tahoma, "Segoe UI", Arial, sans-serif; white-space: nowrap; }
    .menu { position: absolute; right: 0; top: calc(100% + 3px); min-width: 230px; max-height: 320px; overflow: auto; background: #fff; color: #1b1b1b;
            border: 1px solid #9aa7b6; border-radius: 3px; box-shadow: 0 3px 10px rgba(0,0,0,.35); padding: 3px 0; }
    .menu[hidden] { display: none; }
    .item { display: flex; justify-content: space-between; gap: 14px; width: 100%; box-sizing: border-box; padding: 5px 12px; color: #1b1b1b; }
    .item:hover, .item:focus-visible { background: #cfe3fb; outline: none; }
    .item .size { color: #5d6670; }
    .head { padding: 4px 12px 2px; color: #5d6670; font-size: 11px; }
    hr { border: 0; border-top: 1px solid #dde3ea; margin: 3px 0; }
    .note { padding: 6px 12px; color: #8a2b14; max-width: 300px; white-space: normal; }
    .status { cursor: pointer; }
  `;
  const ICON = U.ICON;

  function makePanel(recId) {
    let host = null, shadow = null, list = [], key = "", builtMini = null, shownAt = 0, statusTimer = 0, rec = null;
    const tooSoon = () => Date.now() - shownAt < U.CLICK_GUARD_MS;
    const isMini = () => !!(cfg && cfg.mediaMini);
    const types = () => (cfg && Array.isArray(cfg.mediaTypes) ? cfg.mediaTypes : null);

    function render() {
      const video = list.some((e) => e.media === "video");
      const mini = isMini();
      // Solo marcas propias en el HTML; los textos (traducidos) van como texto, nunca como HTML.
      shadow.innerHTML = `<style>${CSS}</style>
      <div class="bar" role="toolbar">
        <button class="main">${ICON}${mini ? "" : '<span class="label"></span>'}</button>
        ${mini ? "" : '<button class="help">?</button>'}
        <button class="x">X</button>
      </div>
      <div class="menu" role="menu" hidden></div>`;
      builtMini = mini;
      labels(video ? "panelDownloadVideo" : "panelDownloadAudio", video ? "panelDownloadVideoTitle" : "panelDownloadAudioTitle");
      shadow.querySelector(".main").addEventListener("click", (e) => { e.stopPropagation(); if (e.isTrusted && !tooSoon()) onMain(); });
      shadow.querySelector(".x").addEventListener("click", (e) => { e.stopPropagation(); if (e.isTrusted) close(); });
      const help = shadow.querySelector(".help");
      if (help) help.addEventListener("click", (e) => { e.stopPropagation(); if (e.isTrusted) alert(t("panelAboutText")); });
    }

    // Textos del panel: título y nombre accesible de cada botón.
    function labels(text, title) {
      shadow.querySelector(".bar").setAttribute("aria-label", t("panelAria"));
      shadow.querySelector(".main").setAttribute("title", t(title));
      const label = shadow.querySelector(".label");
      if (label) label.textContent = t(text);
      const help = shadow.querySelector(".help");
      if (help) { help.setAttribute("title", t("panelAbout")); help.setAttribute("aria-label", t("panelAbout")); }
      const x = shadow.querySelector(".x");
      x.setAttribute("title", t("panelClose")); x.setAttribute("aria-label", t("panelClose"));
    }

    function close() {
      if (recId) { const r = T.byId(recId); if (r) r.closed = true; } else generalClosed = true;
      hide();
    }

    function choices() {
      const out = [];
      for (const e of list) (e.items || []).forEach((it, index) => out.push(choice(e, it, index)));
      return out.concat(pageTracks());
    }

    // Subtítulos del vídeo (<track kind="subtitles|captions">), como IDM: se ofrecen en la lista para bajarlos junto
    // al vídeo. Solo direcciones http(s); los que ya avisó el fondo no se repiten. El panel general mira los de todos
    // los <video>/<audio> de la página.
    function pageTracks() {
      const out = [], seen = new Set(list.map((e) => noHash(e.url)));
      const r = recId ? T.byId(recId) : null;
      let media = [];
      try { media = r ? [r.el] : document.querySelectorAll("video, audio"); } catch (e) { }
      for (const m of media) {
        for (const tk of m.querySelectorAll ? m.querySelectorAll("track") : []) {
          const kind = String(tk.kind || (tk.getAttribute && tk.getAttribute("kind")) || "subtitles").toLowerCase();
          const src = noHash(tk.src);
          if ((kind !== "subtitles" && kind !== "captions") || !/^https?:\/\//i.test(src) || seen.has(src) || out.length >= 20) continue;
          seen.add(src);
          // Tipo desmarcado en la lista del panel (como IDM). Un <track> sin extensión conocida es WebVTT (el único que
          // lee el navegador).
          if (!L.subtitleAllowed({ url: src, format: "vtt" }, { mediaTypes: types() })) continue;
          const ext = /\.(srt|ttml|dfxp)(?:[?]|$)/i.test(src) ? ".srt" : ".vtt"; // TTML: IDMClone lo pasa a SRT
          out.push({ entry: { kind: "subs", url: src, media: "subtitle" }, index: -1,
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
      if (all.length === 1 && !all[0].entry.protected && !all[0].entry.note) { send(all[0]); return; }
      const menu = shadow.querySelector(".menu");
      if (!menu.hidden) { menu.hidden = true; return; }
      menu.innerHTML = "";
      const videos = list.filter((e) => e.media !== "subtitle");
      for (const e of videos) {
        if (videos.length > 1 || e.duration > 0) {
          const h = document.createElement("div"); h.className = "head";
          h.textContent = t(e.media === "audio" ? "panelHeadAudio" : "panelHeadVideo") + (e.live ? t("panelLive") : e.duration > 0 ? t("panelDuration", L.formatDuration(e.duration)) : "");
          menu.appendChild(h);
        }
        if (e.note) { // aviso en lugar de calidades (YouTube sin el motor de IDMClone, o su error)
          const n = document.createElement("div"); n.className = "note";
          n.textContent = String(e.note);
          menu.appendChild(n);
          continue;
        }
        if (e.protected) {
          const n = document.createElement("div"); n.className = "note";
          n.textContent = t("panelDrm");
          menu.appendChild(n);
          continue;
        }
        (e.items || []).forEach((it, index) => addItem(menu, choice(e, it, index)));
      }
      // Subtítulos sueltos (pedidos por el reproductor o <track> del vídeo): "Subtítulos: Español (vtt)".
      const subs = [];
      for (const e of list) if (e.media === "subtitle") (e.items || []).forEach((it, index) => subs.push(choice(e, it, index)));
      const extra = subs.concat(pageTracks());
      if (extra.length) {
        menu.appendChild(document.createElement("hr"));
        for (const c of extra) addItem(menu, c);
      }
      menu.appendChild(document.createElement("hr"));
      const about = document.createElement("button"); about.className = "item"; about.textContent = t("panelAbout");
      about.addEventListener("click", (ev) => { ev.stopPropagation(); if (!ev.isTrusted) return; menu.hidden = true; alert(t("panelAboutText")); });
      menu.appendChild(about);
      menu.hidden = false;
      const first = menu.querySelector(".item"); if (first) first.focus();
    }

    // Al elegir: se avisa en el propio panel de si IDMClone la recibió (o del motivo si no). El fondo busca la entrada
    // por su número (la guarda él): de aquí solo sale cuál se eligió.
    function send(c) {
      status(t("panelSending"), "");
      let p;
      try { p = U.B.runtime.sendMessage({ type: "media-download", entry: c.entry, item: c.item, index: c.index }); }
      catch (e) { status(t("panelSendFailReload", String(e && e.message || e)), "err"); return; }
      Promise.resolve(p).then((r) => {
        if (r && r.ok) status(t("panelSent"), "ok");
        else status((r && r.error) || t("panelNoReply"), "err");
      }).catch((e) => status(t("panelSendFailUpdated", String(e && e.message || e)), "err"));
    }

    function status(text, kind) {
      if (!shadow) return;
      let n = shadow.querySelector(".status");
      if (!n) { n = document.createElement("div"); n.className = "status"; shadow.appendChild(n); n.addEventListener("click", () => n.remove()); }
      n.textContent = text;
      n.className = "status " + kind;
      clearTimeout(statusTimer);
      if (kind === "ok") statusTimer = setTimeout(() => { if (n.parentNode) n.remove(); }, 4000);
    }

    // Encima del reproductor, arriba a la derecha (como IDM); si no cabe encima (vídeo arriba del todo, desplazado o en
    // pantalla completa), dentro, en la parte visible del reproductor. El panel general, arriba a la derecha de la ventana.
    function place() {
      const fs = document.fullscreenElement;
      const parent = fs && fs.tagName !== "VIDEO" ? fs : (document.body || document.documentElement);
      const before = host.style.position + "|" + host.style.top + "|" + host.style.left;
      if (host.parentNode !== parent) { parent.appendChild(host); shownAt = Date.now(); }
      const w = host.offsetWidth || 180;
      const el = rec && rec.el;
      if (el && (!fs || fs === el || (fs.contains && fs.contains(el)))) {
        const r = el.getBoundingClientRect();
        const fixed = !!fs;
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

    function hide() {
      if (host && host.parentNode) host.remove();
    }

    return {
      set(next) {
        // Con el tipo: lo que pasa de vídeo a audio (suena sin imagen) cambia el texto del botón.
        const k = next.map((e) => e.media + " " + e.url).join("\n") + "|" + isMini();
        list = next;
        if (!host) {
          host = document.createElement("idmclone-panel");
          shadow = host.attachShadow({ mode: "closed" }); // referencia privada: host.shadowRoot es null para la página
          shownAt = Date.now();
        }
        if (k !== key || builtMini !== isMini()) { key = k; render(); }
      },
      show(r) { rec = r; place(); },
      hide,
      destroy() { clearTimeout(statusTimer); hide(); host = null; shadow = null; },
    };
  }
})(typeof self !== "undefined" ? self : globalThis);
