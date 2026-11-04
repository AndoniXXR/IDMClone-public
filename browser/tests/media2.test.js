// Pruebas de la 2.0: un panel por cada <video> (content/tracker.js + content/video-panel.js) y vídeos por documento.
// Ejecutar: node --test browser/tests/media2.test.js
"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const fs = require("node:fs");
const vm = require("node:vm");

const EXT = path.join(__dirname, "..", "extension");
const MANIFEST = JSON.parse(fs.readFileSync(path.join(EXT, "manifest.json"), "utf8"));
const CONTENT_FILES = MANIFEST.content_scripts.find((c) => !c.world).js;
const ES = JSON.parse(fs.readFileSync(path.join(EXT, "_locales", "es", "messages.json"), "utf8"));
const I18N = { getMessage: (k, subs) => { const m = ES[k]; if (!m) return ""; let s = m.message; (subs || []).forEach((v, i) => { s = s.split("$" + (i + 1)).join(v); }); return s; }, getUILanguage: () => "es" };
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

// ---- DOM simulado: lo justo para los paneles y los <video> ----
function el(tag, props) {
  const e = Object.assign({
    tagName: String(tag).toUpperCase(), nodeType: 1, children: [], listeners: {}, style: {}, attrs: {}, hidden: false, className: "",
    textContent: "", parentNode: null, offsetWidth: 180, offsetHeight: 22, src: "", currentSrc: "", poster: "", rect: null,
    addEventListener(type, f) { (this.listeners[type] = this.listeners[type] || []).push(f); },
    setAttribute(k, v) { this.attrs[k] = v; }, getAttribute(k) { return this.attrs[k]; },
    appendChild(c) { if (c.parentNode) c.remove(); c.parentNode = this; this.children.push(c); return c; },
    remove() { const p = this.parentNode; if (p) { p.children.splice(p.children.indexOf(this), 1); this.parentNode = null; } },
    get isConnected() { let n = this; while (n.parentNode) n = n.parentNode; return n.tagName === "HTML"; },
    focus() { }, contains(x) { return this.all().includes(x); }, closest() { return null; },
    getBoundingClientRect() { return this.rect || { top: 0, left: 0, right: 0, bottom: 0, width: 0, height: 0 }; },
    querySelectorAll(sel) { return this.all().filter((x) => matches(x, sel)); },
    querySelector(sel) { return this.querySelectorAll(sel)[0] || null; },
    all() { const out = []; for (const c of this.children) { out.push(c); out.push(...c.all()); } return out; },
    attachShadow(o) { this.shadowMode = o.mode; this._root = el("#shadow-root"); return this._root; },
    get shadowRoot() { return this.shadowMode === "open" ? this._root : null; },
    get innerHTML() { return ""; },
    set innerHTML(html) {
      for (const c of this.children.slice()) c.remove();
      for (const m of String(html).matchAll(/<(\w+)([^>]*)>/g)) {
        const cls = /class="([^"]*)"/.exec(m[2]);
        if (!cls) continue;
        const x = el(m[1]); x.className = cls[1]; x.hidden = /\shidden(\s|$|>)/.test(m[2] + ">");
        this.appendChild(x);
      }
    }
  }, props || {});
  return e;
}
function matches(e, sel) {
  if (sel === "video, audio") return e.tagName === "VIDEO" || e.tagName === "AUDIO";
  return sel.startsWith(".") ? e.className.split(/\s+/).includes(sel.substring(1)) : e.tagName === sel.toUpperCase();
}
function fire(x, type, isTrusted) {
  const ev = { type, isTrusted, target: x, stopPropagation() { }, preventDefault() { }, composedPath: () => [x] };
  for (const f of (x.listeners[type] || []).slice()) f(ev);
}
const R = (top, left, w, h) => ({ top, left, right: left + w, bottom: top + h, width: w, height: h });

function page(appCfg) {
  const sent = [], win = { listeners: {} }, timers = new Set(), cfgListeners = [], docListeners = {};
  let onMsg = null;
  const html = el("html"), body = el("body");
  html.appendChild(body);
  const document = {
    documentElement: html, body, fullscreenElement: null, activeElement: body,
    createElement: (tag) => el(tag),
    querySelectorAll: (sel) => html.querySelectorAll(sel),
    addEventListener: (type, f) => { (docListeners[type] = docListeners[type] || []).push(f); }
  };
  const api = {
    runtime: { id: "mi-extension", onMessage: { addListener: (f) => { onMsg = f; } },
               sendMessage: (m) => { sent.push(m); return Promise.resolve({ ok: true }); } },
    storage: { local: { get: async () => ({ appCfg }) }, onChanged: { addListener: (f) => cfgListeners.push(f) } },
    i18n: I18N
  };
  const ctx = {
    chrome: api, document, location: { protocol: "https:", href: "https://x.com/home" }, URL,
    scrollX: 0, scrollY: 0, innerWidth: 1200, innerHeight: 800, alert() { },
    getComputedStyle: () => ({ visibility: "visible", display: "block", opacity: "1" }),
    addEventListener: (type, f) => { (win.listeners[type] = win.listeners[type] || []).push(f); },
    setTimeout: (f, ms) => { const h = setTimeout(() => { timers.delete(h); f(); }, ms); timers.add(h); return h; },
    clearTimeout: (h) => { clearTimeout(h); timers.delete(h); },
    setInterval: (f, ms) => { const h = setInterval(f, ms); timers.add(h); return h; }, clearInterval: (h) => { clearInterval(h); timers.delete(h); },
    getSelection: () => null
  };
  ctx.self = ctx; ctx.window = ctx; ctx.top = ctx;
  vm.createContext(ctx);
  for (const f of CONTENT_FILES) vm.runInContext(fs.readFileSync(path.join(EXT, f), "utf8"), ctx, { filename: f });
  const panels = () => body.querySelectorAll("idmclone-panel").filter((p) => p.parentNode);
  return {
    ctx, body, sent: () => sent.filter((m) => m.type !== "media-hello"), hello: () => sent.find((m) => m.type === "media-hello"),
    video(props) { const v = el("video", props); body.appendChild(v); return v; },
    media: (entry) => onMsg({ type: "idm-media", entry: Object.assign({ at: Date.now() }, entry) }),
    msg: (m) => onMsg(m),
    // La página se desplaza o cambia algo: el seguimiento lo junta 200 ms antes de avisar.
    scroll: async () => { for (const f of win.listeners.scroll || []) f({}); await wait(260); },
    config: (a) => { for (const f of cfgListeners) f({ appCfg: { newValue: a } }); },
    panels,
    // Pista que da el script de página (page/hooks.js) sobre un <video>.
    hint: async (v, h) => { for (const f of docListeners["idmclone-hint"] || []) f({ target: v, detail: h }); await wait(260); },
    stop: () => { for (const h of timers) { clearTimeout(h); clearInterval(h); } timers.clear(); }
  };
}
const MP4 = (url) => ({ url, kind: "file", media: "video", items: [{ kind: "video", ext: ".mp4", size: 5000000 }] });

test("2.0: un panel por vídeo, encima de SU reproductor (por la dirección del vídeo)", async () => {
  const p = page({});
  try {
    await wait(5);
    const a = p.video({ src: "https://cdn.ej.com/a.mp4", currentSrc: "https://cdn.ej.com/a.mp4", rect: R(100, 50, 400, 225) });
    const b = p.video({ src: "https://cdn.ej.com/b.mp4", currentSrc: "https://cdn.ej.com/b.mp4", rect: R(400, 600, 500, 280) });
    p.media(MP4("https://cdn.ej.com/a.mp4"));
    p.media(MP4("https://cdn.ej.com/b.mp4"));
    const ps = p.panels();
    assert.equal(ps.length, 2, "Dos vídeos, dos paneles");
    const tops = ps.map((x) => parseInt(x.style.top, 10)).sort((x, y) => x - y);
    assert.deepEqual(tops, [100 - 22 - 1, 400 - 22 - 1], "Cada uno justo encima de su vídeo");
    const lefts = ps.map((x) => parseInt(x.style.left, 10)).sort((x, y) => x - y);
    assert.deepEqual(lefts, [450 - 180, 1100 - 180], "Pegado a la esquina superior derecha");
    void a; void b;
  } finally { p.stop(); }
});

test("2.0: el panel se oculta cuando su vídeo sale de pantalla y vuelve cuando reaparece", async () => {
  const p = page({});
  try {
    await wait(5);
    const v = p.video({ src: "https://cdn.ej.com/a.mp4", rect: R(100, 50, 400, 225) });
    p.media(MP4("https://cdn.ej.com/a.mp4"));
    assert.equal(p.panels().length, 1);
    v.rect = R(-1000, 50, 400, 225); // desplazado fuera de la ventana
    await p.scroll();
    assert.equal(p.panels().length, 0, "Fuera de pantalla: sin panel (como IDM)");
    v.rect = R(-200, 50, 400, 225); // asoma algo (menos del 20 %): sigue sin panel
    await p.scroll();
    assert.equal(p.panels().length, 0);
    v.rect = R(300, 50, 400, 225);
    await p.scroll();
    assert.equal(p.panels().length, 1, "Vuelve a verse: vuelve el panel");
  } finally { p.stop(); }
});

test("2.0: X — la lista HLS se une a su vídeo por el número del medio (póster), no al más grande ni al que suena", async () => {
  const p = page({});
  try {
    await wait(5);
    const grande = p.video({ src: "blob:https://x.com/1", poster: "https://pbs.twimg.com/ext_tw_video_thumb/999/pu/img/x.jpg", rect: R(50, 50, 800, 450) });
    const suyo = p.video({ src: "blob:https://x.com/2", poster: "https://pbs.twimg.com/ext_tw_video_thumb/1234567/pu/img/y.jpg", rect: R(600, 50, 400, 225) });
    void grande;
    p.media({ url: "https://video.twimg.com/ext_tw_video/1234567/pu/pl/abc.m3u8", kind: "hls", media: "video",
              hints: ["x:1234567"], items: [{ kind: "video", ext: ".mp4", width: 1280, height: 720 }] });
    const ps = p.panels();
    assert.equal(ps.length, 1);
    assert.equal(parseInt(ps[0].style.top, 10), 600 - 22 - 1, "Encima del vídeo de su publicación");
    void suyo;
  } finally { p.stop(); }
});

test("2.0: si la página sustituye el <video> (redibujado), el panel lo sigue y la X sigue valiendo", async () => {
  const p = page({});
  try {
    await wait(5);
    const poster = "https://pbs.twimg.com/amplify_video_thumb/7777777/img/z.jpg";
    const v = p.video({ src: "blob:https://x.com/1", poster, rect: R(100, 50, 400, 225) });
    p.media({ url: "https://video.twimg.com/amplify_video/7777777/pl/m.m3u8", kind: "hls", media: "video", hints: ["x:7777777"],
              items: [{ kind: "video", ext: ".mp4", width: 640, height: 360 }] });
    assert.equal(p.panels().length, 1);
    v.remove();
    const v2 = p.video({ src: "blob:https://x.com/9", poster, rect: R(140, 50, 400, 225) });
    await p.scroll();
    const ps = p.panels();
    assert.equal(ps.length, 1, "Sigue habiendo panel");
    assert.equal(parseInt(ps[0].style.top, 10), 140 - 22 - 1, "Encima del elemento nuevo");
    // La X lo cierra para ese vídeo; otro sustituto del mismo vídeo tampoco lo enseña.
    await wait(520);
    fire(ps[0]._root.querySelector(".x"), "click", true);
    assert.equal(p.panels().length, 0);
    v2.remove();
    p.video({ src: "blob:https://x.com/10", poster, rect: R(140, 50, 400, 225) });
    await p.scroll();
    assert.equal(p.panels().length, 0, "Cerrado para ese vídeo");
  } finally { p.stop(); }
});

test("2.0: cambio de dirección sin recarga — lo que tiene vídeo sigue; el panel general se queda con lo nuevo", async () => {
  const p = page({});
  try {
    await wait(5);
    p.video({ src: "https://cdn.ej.com/a.mp4", rect: R(100, 50, 400, 225) });
    p.media(MP4("https://cdn.ej.com/a.mp4"));
    p.media({ url: "https://cdn.ej.com/musica.mp3", kind: "file", media: "audio", at: Date.now() - 10000, items: [{ kind: "audio", ext: ".mp3", size: 3000000 }] });
    assert.equal(p.panels().length, 2, "El del vídeo y el general (audio sin <audio> en la página)");
    p.msg({ type: "idm-media-nav", url: "https://x.com/otra" });
    await wait(5);
    assert.equal(p.panels().length, 1, "El audio era de la página anterior; el vídeo sigue en pantalla con su panel");
    // Detectado justo antes del aviso (X pide el vídeo nuevo antes de que llegue el cambio de dirección): es de la nueva.
    p.media({ url: "https://cdn.ej.com/nuevo.mp3", kind: "file", media: "audio", at: Date.now() - 1000, items: [{ kind: "audio", ext: ".mp3", size: 3000000 }] });
    assert.equal(p.panels().length, 2);
  } finally { p.stop(); }
});

test("2.0: la configuración llega en vivo (captura desactivada, panel desactivado, modo mini)", async () => {
  const p = page({ mediaMini: false });
  try {
    await wait(5);
    p.video({ src: "https://cdn.ej.com/a.mp4", rect: R(100, 50, 400, 225) });
    p.media(MP4("https://cdn.ej.com/a.mp4"));
    const root = p.panels()[0]._root;
    assert.ok(root.querySelector(".label"), "Con texto");
    p.config({ mediaMini: true });
    await wait(5);
    assert.equal(p.panels()[0]._root.querySelector(".label"), null, "Mini: solo el icono");
    p.config({ captureOff: true });
    await wait(5);
    assert.equal(p.panels().length, 0, "#1: navegador desmarcado, sin paneles");
    p.config({ mediaPanel: false });
    await wait(5);
    assert.equal(p.panels().length, 0);
    p.config({});
    await wait(5);
    assert.equal(p.panels().length, 1, "Reactivado: vuelve");
  } finally { p.stop(); }
});


test("2.0: YouTube — el panel pasa de la vista previa (oculta) al reproductor del vídeo con la misma pista", async () => {
  const p = page({});
  try {
    await wait(5);
    const previa = p.video({ src: "blob:https://www.youtube.com/1", rect: R(100, 50, 360, 200) });
    await p.hint(previa, "yt:AAAAAAAAAAA");
    p.media({ url: "https://www.youtube.com/watch?v=AAAAAAAAAAA", kind: "youtube", source: "site", media: "video", hints: ["yt:AAAAAAAAAAA"],
              items: [], note: "Para descargar de YouTube hace falta IDMClone 1.5" });
    assert.equal(p.panels().length, 1);
    assert.equal(parseInt(p.panels()[0].style.top, 10), 100 - 22 - 1, "Sobre la vista previa");
    // Se entra al vídeo: la vista previa queda oculta en la página y el reproductor grande tiene ese mismo vídeo.
    previa.rect = R(-2000, 50, 360, 200);
    const principal = p.video({ src: "blob:https://www.youtube.com/2", rect: R(80, 20, 900, 506) });
    await p.hint(principal, "yt:AAAAAAAAAAA");
    await p.scroll();
    const ps = p.panels();
    assert.equal(ps.length, 1, "Sigue habiendo panel");
    assert.equal(parseInt(ps[0].style.top, 10), 80 - 22 - 1, "Ahora sobre el reproductor del vídeo");
  } finally { p.stop(); }
});

test("2.0: YouTube — el mismo <video> cambia de vídeo: el panel es solo del vídeo que reproduce ahora", async () => {
  const p = page({});
  try {
    await wait(5);
    const v = p.video({ src: "blob:https://www.youtube.com/1", rect: R(100, 50, 900, 506) });
    await p.hint(v, "yt:AAAAAAAAAAA");
    const yt = (id) => ({ url: "https://www.youtube.com/watch?v=" + id, kind: "youtube", source: "site", media: "video", hints: ["yt:" + id],
                          items: [{ kind: "video", itag: 22, video: "22", ext: ".mp4", width: 1280, height: 720 }] });
    p.media(yt("AAAAAAAAAAA"));
    await p.hint(v, "yt:BBBBBBBBBBB"); // otra miniatura / otro vídeo, en el mismo elemento
    p.media(yt("BBBBBBBBBBB"));
    await p.scroll();
    const ps = p.panels();
    assert.equal(ps.length, 1, "Un solo panel, sin el general con el vídeo anterior");
    fire(ps[0]._root.querySelector(".main"), "click", true); // antes del medio segundo: no cuenta
    await wait(520);
    fire(ps[0]._root.querySelector(".main"), "click", true);
    const sent = p.sent().filter((m) => m.type === "media-download");
    assert.equal(sent.length, 1, "Una sola calidad: se envía directa (no hay otra entrada en el panel)");
    assert.equal(sent[0].entry.url, "https://www.youtube.com/watch?v=BBBBBBBBBBB");
  } finally { p.stop(); }
});

test("2.0: el documento manda su ficha al fondo (solo el marco principal)", async () => {
  const p = page({});
  try {
    const h = p.hello();
    assert.ok(h && typeof h.doc === "string" && h.doc.length >= 8);
  } finally { p.stop(); }
});

// ---- Fondo: la descarga sale de la entrada guardada (la página solo dice cuál) ----
test("2.0: el fondo descarga la entrada que guardó él, no la que le devuelve la página", async () => {
  const sentHost = [];
  let onMessage = null, onHeaders = [];
  const ev = () => ({ addListener() { } });
  globalThis.chrome = {
    runtime: { id: "x", onInstalled: ev(), onStartup: ev(), onMessage: { addListener: (f) => { onMessage = f; } },
               getManifest: () => MANIFEST,
               connectNative: () => {
                 let reply = null;
                 return { onMessage: { addListener: (f) => { reply = f; } }, onDisconnect: ev(),
                          postMessage(m) { sentHost.push(m); setTimeout(() => reply && reply({ rid: m.rid, ok: m.type !== "getconfig" }), 1); } };
               } },
    storage: { local: { get: async (d) => Object.assign({}, d), set: async () => {} }, onChanged: ev() },
    downloads: { onCreated: ev() },
    cookies: { getAll: async () => [] },
    contextMenus: { removeAll: async () => {}, create() { }, onClicked: ev() },
    i18n: I18N,
    notifications: { create() { } },
    webRequest: { onHeadersReceived: { addListener: (f) => onHeaders.push(f) } },
    tabs: { get: async (id) => ({ id, url: "https://ej.com/ver", title: "Mi vídeo - Ej" }), sendMessage: async () => {}, onUpdated: ev(), onRemoved: ev() }
  };
  delete globalThis.browser;
  const file = path.join(EXT, "background.js");
  delete require.cache[require.resolve(file)];
  const bg = require(file);
  const big = { url: "https://cdn.ej.com/peli.mp4", type: "media", tabId: 3, frameId: 0, statusCode: 200,
                responseHeaders: [{ name: "content-type", value: "video/mp4" }, { name: "content-length", value: "9000000" }] };
  for (const f of onHeaders) f(big);
  await wait(30);
  const stored = [...bg.tabMedia.get(3).items.values()].find((e) => e && e.url === big.url);
  assert.ok(stored, "Detectado");
  // La página (o un script que la imitara) devuelve otra dirección con el mismo número: se usa la guardada.
  onMessage({ type: "media-download", entry: Object.assign({}, stored, { kind: "file", url: big.url }), item: { kind: "video", ext: ".mp4" }, index: 0 },
            { tab: { id: 3, url: "https://ej.com/ver", title: "Mi vídeo - Ej" } }, () => {});
  onMessage({ type: "media-download", entry: { id: stored.id, kind: "file", url: "https://malo.com/otro.exe" }, item: { kind: "video", ext: ".mp4" }, index: 0 },
            { tab: { id: 3, url: "https://ej.com/ver", title: "Mi vídeo - Ej" } }, () => {});
  await wait(30);
  const dl = sentHost.filter((m) => m.type === "download");
  assert.equal(dl.length, 2);
  assert.equal(dl[0].url, big.url);
  assert.equal(dl[0].filename, "Mi vídeo.mp4");
  assert.equal(dl[1].url, "https://malo.com/otro.exe", "Sin entrada guardada con ese número y dirección: la de la petición (como la 1.7)");
});
