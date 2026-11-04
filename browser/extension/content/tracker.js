// IDMClone Integration — seguimiento de los <video> y <audio> de la página, como IDM: cada uno con su número, si se ve
// (al menos un 20 %), dónde está y qué pistas tiene para unirlo con lo que detecta el fondo (su dirección, su póster, los
// datos del sitio). Si la página sustituye un elemento por otro (X, Facebook... lo hacen al redibujar), el nuevo hereda el
// número del viejo cuando comparte pista o sitio en pantalla: así su panel no desaparece.
// Solo lee de la página la posición de esos elementos, su dirección, su póster y si se están reproduciendo.
(function (root) {
  "use strict";
  const U = root.IDMContent;
  const L = root.IDMLogic;
  if (!U || !L) return;

  const VISIBLE_RATIO = 0.2;   // como IDM: se ve al menos un 20 %
  const SETTLE_MS = 200;       // como IDM: los cambios se juntan 200 ms antes de avisar (sin parpadeos)
  const GHOST_MS = 3000;       // un elemento quitado puede volver (redibujado) durante 3 s con el mismo número
  const POLL_MS = 1000;        // sin observadores (o por si la página mueve algo sin avisar): se mira cada segundo

  let seq = 1;
  const recs = new Map();      // elemento -> registro
  let ghosts = [];             // registros de elementos quitados hace poco
  const listeners = [];
  let settleTimer = 0, pollTimer = 0;
  let mouseX = -1, mouseY = -1;

  const hasIO = typeof IntersectionObserver === "function";
  const hasRO = typeof ResizeObserver === "function";
  const hasMO = typeof MutationObserver === "function";
  const io = hasIO ? new IntersectionObserver((list) => {
    for (const x of list) { const r = recs.get(x.target); if (r) r.ratio = x.isIntersecting ? x.intersectionRatio : 0; }
    changed();
  }, { threshold: [0, VISIBLE_RATIO, 0.5, 1] }) : null;
  const ro = hasRO ? new ResizeObserver(() => changed()) : null;

  function noHash(u) { return String(u || "").split("#")[0]; }

  // Direcciones http(s) del elemento: src, currentSrc y sus <source> (un blob: de MSE no dice nada).
  function urlsOf(el) {
    const out = new Set();
    const add = (u) => { if (u && /^https?:/i.test(u)) out.add(noHash(u)); };
    try { add(el.currentSrc); add(el.src); for (const s of el.querySelectorAll ? el.querySelectorAll("source") : []) add(s.src); } catch (e) { }
    return out;
  }

  function hintsOf(rec) {
    const hs = new Set(rec.pageHints);
    try { for (const h of L.posterHints(rec.el.poster)) hs.add(h); } catch (e) { }
    for (const u of urlsOf(rec.el)) for (const h of L.urlHints(u)) hs.add(h);
    return hs;
  }

  function rectOf(el) {
    try { const r = el.getBoundingClientRect(); return { top: r.top, left: r.left, right: r.right, bottom: r.bottom, width: r.width, height: r.height }; }
    catch (e) { return null; }
  }

  function shown(el) {
    try {
      const st = getComputedStyle(el);
      return st.visibility !== "hidden" && st.display !== "none" && st.opacity !== "0";
    } catch (e) { return true; }
  }

  function sameSpot(a, b) {
    return !!(a && b) && Math.abs(a.top - b.top) < 12 && Math.abs(a.left - b.left) < 12 &&
      Math.abs(a.width - b.width) < 24 && Math.abs(a.height - b.height) < 24;
  }

  function adopt(el) {
    const rec = { id: 0, el, ratio: hasIO ? 0 : 1, rect: rectOf(el), pageHints: new Set(), hints: new Set(), playedAt: 0, closed: false, gone: false };
    rec.hints = hintsOf(rec);
    // ¿Es el sustituto de uno que acaba de irse? Misma pista, o mismo sitio en pantalla.
    const now = Date.now();
    ghosts = ghosts.filter((g) => now - g.goneAt < GHOST_MS);
    const g = ghosts.find((x) => [...x.hints].some((h) => rec.hints.has(h))) || ghosts.find((x) => sameSpot(x.rect, rec.rect));
    if (g) {
      ghosts.splice(ghosts.indexOf(g), 1);
      rec.id = g.id; rec.closed = g.closed; rec.playedAt = g.playedAt;
      for (const h of g.pageHints) rec.pageHints.add(h);
      rec.hints = hintsOf(rec);
    } else rec.id = seq++;
    recs.set(el, rec);
    if (io) io.observe(el);
    if (ro) ro.observe(el);
    return rec;
  }

  function drop(rec) {
    recs.delete(rec.el);
    if (io) { try { io.unobserve(rec.el); } catch (e) { } }
    if (ro) { try { ro.unobserve(rec.el); } catch (e) { } }
    rec.gone = true; rec.goneAt = Date.now();
    ghosts.push(rec);
    if (ghosts.length > 50) ghosts.shift();
  }

  // Repaso completo: elementos nuevos, quitados, su posición y si se ven.
  function scan() {
    let list = [];
    try { list = document.querySelectorAll("video, audio"); } catch (e) { }
    const present = new Set(list);
    // Primero los que se fueron: así un sustituto que llega en el mismo repaso los encuentra y hereda su número.
    for (const rec of [...recs.values()]) if (!present.has(rec.el) || rec.el.isConnected === false) drop(rec);
    for (const el of list) {
      if (el.isConnected === false) continue;
      const rec = recs.get(el) || adopt(el);
      rec.rect = rectOf(el);
      rec.hints = hintsOf(rec);
      if (!hasIO) rec.ratio = visibleRatio(rec.rect);
      rec.shown = shown(el);
    }
  }

  // Sin IntersectionObserver: la parte del rectángulo que cae dentro de la ventana.
  function visibleRatio(r) {
    if (!r || r.width <= 0 || r.height <= 0) return 0;
    const w = Math.max(0, Math.min(r.right, innerWidth) - Math.max(r.left, 0));
    const h = Math.max(0, Math.min(r.bottom, innerHeight) - Math.max(r.top, 0));
    return (w * h) / (r.width * r.height);
  }

  function changed() {
    if (settleTimer) return;
    settleTimer = setTimeout(() => {
      settleTimer = 0;
      scan();
      for (const f of listeners) { try { f(); } catch (e) { } }
    }, SETTLE_MS);
  }

  function start() {
    scan();
    if (hasMO) {
      // Elementos que se añaden o se quitan, y cambios de estilo (ocultar, mover) en cualquier sitio de la página.
      try { new MutationObserver(changed).observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ["style", "class", "src", "poster", "hidden"] }); } catch (e) { }
    }
    addEventListener("scroll", changed, { capture: true, passive: true });
    addEventListener("resize", changed, { passive: true });
    document.addEventListener("fullscreenchange", changed);
    // Reproducción: el que empieza a reproducirse es casi siempre el del vídeo recién detectado.
    const onPlay = (e) => { const r = e && e.target && recs.get(e.target); if (r) { r.playedAt = Date.now(); changed(); } };
    document.addEventListener("play", onPlay, true);
    document.addEventListener("playing", onPlay, true);
    addEventListener("mousemove", (e) => { mouseX = e.clientX; mouseY = e.clientY; }, { capture: true, passive: true });
    // Pistas que da el script de página de algunos sitios (page/hooks.js): un evento en el propio elemento.
    document.addEventListener("idmclone-hint", (e) => {
      const el = e && e.target;
      if (!el || !/^(VIDEO|AUDIO)$/.test(el.tagName || "")) return;
      const h = L.cleanHint(typeof e.detail === "string" ? e.detail : "");
      if (!h) return;
      const rec = recs.get(el) || adopt(el);
      // Un elemento reproduce un vídeo a la vez: la pista nueva de un sitio sustituye a la anterior del mismo sitio
      // (YouTube usa el mismo <video> para la vista previa de cada miniatura y al cambiar de vídeo).
      const prefix = h.substring(0, h.indexOf(":") + 1);
      for (const x of [...rec.pageHints]) if (x.indexOf(prefix) === 0) rec.pageHints.delete(x);
      rec.pageHints.add(h);
      rec.hints = hintsOf(rec);
      changed();
    }, true);
  }

  function ensurePolling(on) {
    if (on && !pollTimer) pollTimer = setInterval(changed, POLL_MS);
    else if (!on && pollTimer) { clearInterval(pollTimer); pollTimer = 0; }
  }

  // ¿Está el ratón encima de este elemento?
  function underMouse(rec) {
    const r = rec.rect;
    return !!r && mouseX >= r.left && mouseX <= r.right && mouseY >= r.top && mouseY <= r.bottom;
  }

  U.tracker = {
    records: () => [...recs.values()],
    byId: (id) => { for (const r of recs.values()) if (r.id === id) return r; return null; },
    urlsOf, underMouse, onChange: (f) => listeners.push(f), refresh: () => { scan(); }, changed, ensurePolling,
    visible: (rec) => !!rec && !rec.gone && rec.shown !== false && rec.ratio >= VISIBLE_RATIO && !!rec.rect && rec.rect.width >= 15 && rec.rect.height >= 10,
  };
  start();
})(typeof self !== "undefined" ? self : globalThis);
