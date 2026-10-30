// Pruebas de la extensión con Node (node --test): lógica de decisión y flujo de captura con las
// APIs del navegador simuladas (Chromium y Firefox).
"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");

const EXT = path.join(__dirname, "..", "extension");
globalThis.self = globalThis;
const L = require(path.join(EXT, "logic.js"));

// chrome.i18n.getMessage simulado con los _locales de verdad (marcadores $NOMBRE$ -> $1..$9, como el navegador).
function i18nFor(lang) {
  const msgs = JSON.parse(require("node:fs").readFileSync(path.join(EXT, "_locales", lang, "messages.json"), "utf8"));
  const byKey = {};
  for (const k of Object.keys(msgs)) byKey[k.toLowerCase()] = msgs[k];
  return {
    getUILanguage: () => lang.replace("_", "-"),
    getMessage(key, subs) {
      const m = byKey[String(key).toLowerCase()];
      if (!m) return "";
      const s = subs === undefined ? [] : [].concat(subs);
      const ph = {};
      for (const n of Object.keys(m.placeholders || {})) ph[n.toLowerCase()] = m.placeholders[n].content.replace(/\$(\d)/g, (_, i) => (s[i - 1] !== undefined ? String(s[i - 1]) : ""));
      return m.message.replace(/\$([A-Za-z0-9_@]+)\$/g, (_, n) => (ph[n.toLowerCase()] !== undefined ? ph[n.toLowerCase()] : "")).replace(/\$\$/g, "$");
    }
  };
}
const I18N_ES = i18nFor("es");
globalThis.chrome = { i18n: I18N_ES }; // para las pruebas de logic.js sin fondo cargado

// ------------------------------------------------------------------ lógica

test("tipos por defecto de IDM, con comodines r0*/r1*", () => {
  for (const e of ["zip", "rar", "7z", "exe", "msi", "iso", "mp3", "mp4", "mkv", "pdf", "r00", "r01", "r15"]) assert.ok(L.extMatches(e, L.DEFAULT_EXTS), e);
  for (const e of ["html", "htm", "js", "css", "png", "json", "r2x", ""]) assert.ok(!L.extMatches(e, L.DEFAULT_EXTS), e);
});

const S = { enabled: true, exts: L.DEFAULT_EXTS, minSizeKB: 0, exclude: L.DEFAULT_EXCLUDE };

test("decide por el nombre del navegador, Content-Disposition, URL o tipo MIME", () => {
  assert.equal(L.decide({ url: "https://x.com/a/setup.exe" }, S).capture, true);
  assert.equal(L.decide({ url: "https://x.com/descargar.php?id=5", contentDisposition: 'attachment; filename="pack.zip"' }, S).name, "pack.zip");
  assert.equal(L.decide({ url: "https://x.com/descargar.php?id=5", contentDisposition: 'attachment; filename="pack.zip"' }, S).capture, true);
  assert.equal(L.decide({ url: "https://x.com/get", mime: "application/pdf" }, S).capture, true);
  assert.equal(L.decide({ url: "https://x.com/get", filename: "C:\\Users\\yo\\Downloads\\informe.pdf" }, S).name, "informe.pdf");
  assert.equal(L.decide({ url: "https://x.com/index.html" }, S).capture, false);
  assert.equal(L.decide({ url: "https://x.com/foto.png", mime: "image/png" }, S).capture, false);
  assert.equal(L.decide({ url: "https://x.com/d", contentDisposition: "attachment; filename*=UTF-8''informe%20%E2%82%AC.pdf" }, S).name, "informe €.pdf");
});

test("no captura blob:, data:, desactivada, sitios excluidos ni archivos pequeños", () => {
  assert.equal(L.decide({ url: "blob:https://x.com/123" }, S).capture, false);
  assert.equal(L.decide({ url: "data:application/zip;base64,AAA" }, S).capture, false);
  assert.equal(L.decide({ url: "https://x.com/a.zip" }, Object.assign({}, S, { enabled: false })).capture, false);
  const ex = Object.assign({}, S, { exclude: ["descargas.ejemplo.com", "*.cdn.com/*.mp3"] });
  assert.equal(L.decide({ url: "https://descargas.ejemplo.com/a.zip" }, ex).capture, false);
  assert.equal(L.decide({ url: "https://www.descargas.ejemplo.com/a.zip" }, ex).capture, false);
  assert.equal(L.decide({ url: "https://a.cdn.com/s/tema.mp3" }, ex).capture, false);
  assert.equal(L.decide({ url: "https://a.cdn.com/s/tema.zip" }, ex).capture, true);
  const min = Object.assign({}, S, { minSizeKB: 100 });
  assert.equal(L.decide({ url: "https://x.com/a.zip", totalBytes: 50 * 1024 }, min).capture, false);
  assert.equal(L.decide({ url: "https://x.com/a.zip", totalBytes: 500 * 1024 }, min).capture, true);
  assert.equal(L.decide({ url: "https://x.com/a.zip", totalBytes: 0 }, min).capture, true); // tamaño desconocido: se captura
});

// ------------------------------------------------------------------ flujo con APIs simuladas

function loadBackground(opts) {
  const calls = [];
  const L2 = {};
  const ev = (name) => ({ addListener: (f) => { L2[name] = f; } });
  let portListeners = null;
  const store = {};                       // storage.local (persiste dentro de cada simulación)
  const menus = { ids: [], removeAll: 0, titles: {} }; // menú contextual actual
  const api = {
    runtime: {
      id: "mi-extension",
      lastError: null,
      onInstalled: ev("onInstalled"),
      onStartup: ev("onStartup"),
      onMessage: ev("onMessage"),
      sendMessage: async () => ({}),
      connectNative(host) {
        calls.push(["connectNative", host]);
        if (opts.noHost) throw new Error("Specified native messaging host not found.");
        const pl = { msg: null, disc: null };
        portListeners = pl;
        return {
          onMessage: { addListener: (f) => { pl.msg = f; } },
          onDisconnect: { addListener: (f) => { pl.disc = f; } },
          postMessage(m) {
            calls.push(["post", m]);
            setTimeout(() => {
              // Opciones > Tipos de archivo de IDMClone: por defecto el host no las da (versión antigua).
              const r = m.type === "getconfig" ? ((typeof opts.appConfig === "function" ? opts.appConfig(m) : opts.appConfig) || { ok: false, error: "tipo desconocido" })
                      : opts.hostReply ? opts.hostReply(m) : { ok: true };
              if (r === "disconnect") pl.disc && pl.disc();
              else if (r) pl.msg && pl.msg(Object.assign({ rid: m.rid }, r));
            }, typeof opts.delay === "function" ? opts.delay(m) : (opts.delay || 5));
          }
        };
      }
    },
    storage: { local: {
      get: async (d) => { const r = Object.assign({}, d, opts.settings || {}); for (const k of Object.keys(d || {})) if (k in store) r[k] = store[k]; return r; },
      set: async (o) => { Object.assign(store, JSON.parse(JSON.stringify(o))); } },
      // storage.session (memoria del navegador): opts.session lo comparte entre simulaciones (service worker que se duerme).
      session: (() => { const ss = opts.session || {}; return {
        get: async (d) => { const r = Object.assign({}, d); for (const k of Object.keys(d || {})) if (k in ss) r[k] = JSON.parse(JSON.stringify(ss[k])); return r; },
        set: async (o) => { Object.assign(ss, JSON.parse(JSON.stringify(o))); } }; })() },
    downloads: {
      onCreated: ev("onCreated"),
      onDeterminingFilename: opts.firefox ? undefined : ev("onDeterminingFilename"),
      pause: async (id) => { calls.push(["pause", id]); },
      resume: async (id) => { calls.push(["resume", id]); },
      cancel: async (id) => { calls.push(["cancel", id]); },
      erase: async (q) => { calls.push(["erase", q.id]); },
      search: async (q) => [{ id: q.id, state: opts.finishedBeforeCancel ? "complete" : "interrupted", exists: true }],
      removeFile: async (id) => { calls.push(["removeFile", id]); }
    },
    cookies: {
      getAll: async (q) => { calls.push(["cookies", q]); return opts.cookies ? opts.cookies(q) : [{ name: "sesion", value: "abc" }]; },
      getAllCookieStores: async () => [{ id: "0" }, { id: "1", incognito: true }]
    },
    contextMenus: { removeAll: async () => { menus.removeAll++; menus.ids = []; }, create: (o, cb) => { menus.ids.push(o.id); menus.titles[o.id] = o.title; if (cb) cb(); }, onClicked: ev("menuClicked") },
    i18n: opts.i18n || I18N_ES,
    notifications: { create: (id, o) => calls.push(["notify", o.title, o.message]) },
    webRequest: { onHeadersReceived: { addListener: (f) => { (L2.headersAll = L2.headersAll || []).push(f); L2.headers = (d) => { for (const x of L2.headersAll) x(d); }; } } },
    tabs: {
      get: async (id) => Object.assign({ id, url: "https://videos.ejemplo.com/ver/123", title: "Mi vídeo: «prueba»", incognito: false }, opts.tab || {}),
      sendMessage: async (tabId, msg, o) => { calls.push(["tabMsg", tabId, msg, o]); },
      // Pestañas abiertas (#18: las cookies solo se dan para sitios abiertos o recientes).
      query: async (q) => { calls.push(["tabsQuery", q]); return typeof opts.tabs === "function" ? opts.tabs() : (opts.tabs || []); },
      onUpdated: ev("tabUpdated"), onRemoved: ev("tabRemoved")
    }
  };
  api.runtime.reload = () => calls.push(["reload"]);
  api.runtime.getManifest = () => require(path.join(EXT, "manifest.json"));
  if (opts.installType) api.management = { getSelf: async () => ({ id: "mi-extension", installType: opts.installType }) };
  if (opts.firefox) api.runtime.getBrowserInfo = async () => ({ name: "Firefox", version: "128.0" });
  if (opts.proxySettings) api.proxy = { settings: { get: (q, cb) => { calls.push(["proxySettings"]); cb({ levelOfControl: "controlled_by_this_extension", value: opts.proxySettings }); } } };
  if (opts.firefox) { globalThis.browser = api; delete globalThis.chrome; } else { globalThis.chrome = api; delete globalThis.browser; }
  const file = path.join(EXT, "background.js");
  delete require.cache[require.resolve(file)];
  const bg = require(file);
  // Las peticiones de configuración no cuentan como envíos a IDMClone.
  return { bg, calls, L: L2, store, menus, names: () => calls.filter((c) => !(c[0] === "post" && c[1].type === "getconfig")).map((c) => c[0]),
           // Mensaje que manda el host por su cuenta (sin rid): p. ej. la petición de cookies de IDMClone.
           push: (m) => portListeners && portListeners.msg && portListeners.msg(m),
           posts: (type) => calls.filter((c) => c[0] === "post" && c[1].type === type).map((c) => c[1]) };
}

const wait = (ms) => new Promise((r) => setTimeout(r, ms));

test("Chromium: espera al nombre final, pausa, IDMClone acepta -> cancela y borra del navegador", async () => {
  const t = loadBackground({ hostReply: (m) => (m.type === "download" ? { ok: true, id: "x" } : null) });
  const item = { id: 7, url: "https://x.com/descargar.php?id=5", state: "in_progress", filename: "", referrer: "https://x.com/" };
  t.L.onCreated(item);
  assert.ok(!t.names().includes("pause"), "No debe decidir antes de conocer el nombre");
  let suggested = false;
  const held = t.L.onDeterminingFilename(Object.assign({}, item, { filename: "programa.exe", mime: "application/octet-stream" }), () => { suggested = true; });
  assert.equal(held, true, "Como IDM: el navegador espera a que se decida (sin 'Guardar como')");
  assert.ok(!suggested);
  await wait(50);
  assert.ok(!suggested, "Si va a IDMClone no se suelta: se cancela");
  const n = t.names();
  assert.deepEqual(n.filter((x) => ["pause", "cancel", "erase", "resume"].includes(x)), ["pause", "cancel", "erase"]);
  const post = t.calls.find((c) => c[0] === "post" && c[1].type !== "getconfig")[1];
  assert.equal(post.type, "download");
  assert.equal(post.url, "https://x.com/descargar.php?id=5");
  assert.equal(post.filename, "programa.exe");
  assert.equal(post.cookies, "sesion=abc");
  assert.equal(post.referrer, "https://x.com/");
});

test("Chromium: pausa en cuanto aparece una descarga dudosa y la reanuda si no era para IDMClone", async () => {
  const t = loadBackground({ hostReply: () => ({ ok: true }) });
  const item = { id: 20, url: "https://x.com/descargar.php?id=9", state: "in_progress", filename: "", mime: "application/octet-stream" };
  t.L.onCreated(item);
  await wait(20);
  assert.deepEqual(t.names().filter((x) => x === "pause"), ["pause"], "Debe pausar antes de conocer el nombre");
  let suggested = 0;
  t.L.onDeterminingFilename(Object.assign({}, item, { filename: "foto.png", mime: "image/png" }), () => { suggested++; });
  await wait(30);
  assert.equal(suggested, 1, "No era para IDMClone: el navegador sigue con su nombre (una sola vez)");
  assert.ok(t.names().includes("resume"), "No se reanudó una descarga que no era para IDMClone");
  assert.ok(!t.names().includes("post") && !t.names().includes("cancel"));
});

test("Chromium: lo que claramente no es para IDMClone ni se pausa", async () => {
  const t = loadBackground({});
  t.L.onCreated({ id: 21, url: "https://x.com/foto.png", state: "in_progress", filename: "", mime: "image/png" });
  await wait(20);
  assert.ok(!t.names().includes("pause"));
});

test("si el navegador terminó antes de cancelar, se borra su copia (no queda duplicada)", async () => {
  const t = loadBackground({ hostReply: () => ({ ok: true }), finishedBeforeCancel: true });
  t.L.onCreated({ id: 22, url: "https://x.com/a.zip", filename: "a.zip", state: "in_progress" });
  await wait(50);
  assert.deepEqual(t.names().filter((x) => ["cancel", "removeFile", "erase"].includes(x)), ["cancel", "removeFile", "erase"]);
});

test("IDMClone rechaza la descarga -> se reanuda en el navegador (no se pierde)", async () => {
  const t = loadBackground({ hostReply: () => ({ ok: false, error: "URL no válida" }) });
  t.L.onCreated({ id: 1, url: "https://x.com/a.zip", filename: "a.zip", state: "in_progress" });
  await wait(50);
  assert.deepEqual(t.names().filter((x) => ["pause", "cancel", "erase", "resume"].includes(x)), ["pause", "resume"]);
});

test("host no instalado o que se cae -> la descarga sigue en el navegador", async () => {
  const a = loadBackground({ noHost: true });
  a.L.onCreated({ id: 2, url: "https://x.com/a.zip", filename: "a.zip", state: "in_progress" });
  await wait(50);
  assert.deepEqual(a.names().filter((x) => ["pause", "cancel", "resume"].includes(x)), ["pause", "resume"]);

  const b = loadBackground({ hostReply: () => "disconnect" });
  b.L.onCreated({ id: 3, url: "https://x.com/a.zip", filename: "a.zip", state: "in_progress" });
  await wait(50);
  assert.deepEqual(b.names().filter((x) => ["pause", "cancel", "resume"].includes(x)), ["pause", "resume"]);
});

test("una página web o una imagen no se tocan", async () => {
  const t = loadBackground({});
  t.L.onCreated({ id: 4, url: "https://x.com/pagina.html", filename: "pagina.html", mime: "text/html", state: "in_progress" });
  t.L.onCreated({ id: 5, url: "https://x.com/foto.png", filename: "foto.png", mime: "image/png", state: "in_progress" });
  await wait(30);
  assert.ok(!t.names().includes("pause") && !t.names().includes("post"));
});

test("Firefox: decide en onCreated (ruta completa) y usa la cabecera Content-Disposition guardada", async () => {
  const t = loadBackground({ firefox: true, hostReply: () => ({ ok: true }) });
  t.L.headers({ url: "https://x.com/get?id=1", responseHeaders: [{ name: "Content-Disposition", value: 'attachment; filename="datos.7z"' }, { name: "Content-Type", value: "application/octet-stream" }] });
  t.L.onCreated({ id: 9, url: "https://x.com/get?id=1", filename: "", state: "in_progress" });
  await wait(50);
  const post = t.calls.find((c) => c[0] === "post" && c[1].type !== "getconfig");
  assert.ok(post, "No se envió a IDMClone");
  assert.equal(post[1].filename, "datos.7z");
  assert.ok(t.names().includes("cancel"));

  const t2 = loadBackground({ firefox: true, hostReply: () => ({ ok: true }) });
  t2.L.onCreated({ id: 10, url: "https://x.com/v.mp4", filename: "C:\\Users\\yo\\Downloads\\v.mp4", state: "in_progress" });
  await wait(50);
  assert.equal(t2.calls.find((c) => c[0] === "post" && c[1].type !== "getconfig")[1].filename, "v.mp4");
});

test("incógnito: cookies del almacén privado y aviso a IDMClone", async () => {
  const t = loadBackground({ hostReply: () => ({ ok: true }) });
  t.L.onCreated({ id: 11, url: "https://x.com/a.zip", filename: "a.zip", state: "in_progress", incognito: true });
  await wait(50);
  const q = t.calls.find((c) => c[0] === "cookies")[1];
  assert.equal(q.storeId, "1");
  assert.equal(t.calls.find((c) => c[0] === "post" && c[1].type !== "getconfig")[1].incognito, true);
});

test("canal persistente: un solo host para varias peticiones y respuestas en desorden", async () => {
  const t = loadBackground({ hostReply: (m) => ({ ok: true, echo: m.url }), delay: 5 });
  const r = await Promise.all([t.bg.hostRequest({ url: "u1" }, 1000), t.bg.hostRequest({ url: "u2" }, 1000), t.bg.hostRequest({ url: "u3" }, 1000)]);
  assert.deepEqual(r.map((x) => x.echo), ["u1", "u2", "u3"]);
  assert.equal(t.calls.filter((c) => c[0] === "connectNative").length, 1);
});

test("menú contextual: blob: avisa y no envía; http envía", async () => {
  const t = loadBackground({ hostReply: () => ({ ok: true }) });
  await t.L.menuClicked({ linkUrl: "blob:https://x.com/1" }, { url: "https://x.com" });
  assert.ok(!t.names().includes("post"));
  await t.L.menuClicked({ linkUrl: "https://x.com/a.iso" }, { url: "https://x.com/pag" });
  const post = t.calls.find((c) => c[0] === "post" && c[1].type !== "getconfig")[1];
  assert.equal(post.url, "https://x.com/a.iso");
  assert.equal(post.referrer, "https://x.com/pag");
});

test("manifiesto válido para Chromium y Firefox", () => {
  const m = require(path.join(EXT, "manifest.json"));
  assert.equal(m.manifest_version, 3);
  assert.equal(m.background.service_worker, "background.js");
  assert.deepEqual(m.background.scripts, ["logic.js", "background.js"]);
  assert.ok(m.browser_specific_settings.gecko.id);
  for (const p of ["downloads", "cookies", "nativeMessaging", "webRequest", "storage", "scripting", "activeTab"]) assert.ok(m.permissions.includes(p), p);
  assert.deepEqual(m.content_scripts[0].js, ["logic.js", "content.js"]); // el panel de vídeo usa logic.js
});

test("tipos de archivo y sitios excluidos: manda la lista de IDMClone (Opciones > Tipos de archivo)", async () => {
  const appConfig = { ok: true, exts: ["xyz", "r0*"], exclude: ["*.bloqueado.com"] };
  const t = loadBackground({ appConfig, hostReply: () => ({ ok: true }) });
  const s = await t.bg.getSettings();
  assert.deepEqual(s.exts, ["xyz", "r0*"]);
  assert.equal(s.fromApp, true);
  t.L.onCreated({ id: 30, url: "https://x.com/datos.xyz", filename: "datos.xyz", state: "in_progress" });
  t.L.onCreated({ id: 31, url: "https://x.com/foto.zip", filename: "foto.zip", state: "in_progress" });
  t.L.onCreated({ id: 32, url: "https://cdn.bloqueado.com/b.xyz", filename: "b.xyz", state: "in_progress" });
  await wait(60);
  const sent = t.calls.filter((c) => c[0] === "post" && c[1].type === "download").map((c) => c[1].filename);
  assert.deepEqual(sent, ["datos.xyz"], "Solo el tipo de la lista de IDMClone y fuera de los sitios excluidos");
  // La lista se pide una vez y se reutiliza (un minuto).
  assert.equal(t.calls.filter((c) => c[0] === "post" && c[1].type === "getconfig").length, 1);
});

test("tipos de archivo: si IDMClone no da su lista, se usan los de la extensión", async () => {
  const t = loadBackground({ hostReply: () => ({ ok: true }) });
  const s = await t.bg.getSettings();
  assert.ok(!s.fromApp);
  assert.ok(s.exts.includes("zip"));
});

test("formulario POST: el cuerpo enviado viaja con la descarga", async () => {
  const t = loadBackground({ hostReply: () => ({ ok: true }) });
  t.bg.rememberPost({ method: "POST", url: "https://x.com/generar", requestBody: { formData: { token: ["secreto"], "año": ["2026"] } } });
  t.L.onCreated({ id: 40, url: "https://x.com/generar", filename: "informe.pdf", state: "in_progress" });
  await wait(60);
  const post = t.calls.find((c) => c[0] === "post" && c[1].type === "download");
  assert.ok(post, "No se envió");
  assert.equal(post[1].postData, "token=secreto&a%C3%B1o=2026");
});

test("tecla para no descargar con IDMClone: la descarga sigue en el navegador", async () => {
  const t = loadBackground({ hostReply: () => ({ ok: true }) });
  t.L.onMessage({ type: "prevent-capture", url: "https://x.com/a.zip", referrer: "https://x.com/" }, { tab: { id: 3, url: "https://x.com/" } }, () => {});
  t.L.onCreated({ id: 41, url: "https://x.com/a.zip", filename: "a.zip", state: "in_progress" });
  t.L.onCreated({ id: 42, url: "https://x.com/b.zip", filename: "b.zip", state: "in_progress" });
  await wait(60);
  const sent = t.calls.filter((c) => c[0] === "post" && c[1].type === "download").map((c) => c[1].url);
  assert.deepEqual(sent, ["https://x.com/b.zip"]);
});

test("tecla para forzar la descarga: un enlace pulsado con ella va a IDMClone aunque no sea de un tipo vigilado", async () => {
  const t = loadBackground({ appConfig: { ok: true, exts: ["zip"], preventKeys: "del", forceKeys: "ins" }, hostReply: () => ({ ok: true }) });
  await t.bg.getAppConfig();
  const keep = t.L.onMessage({ type: "force-mark", url: "https://x.com/informe.html", referrer: "https://x.com/" }, { tab: { id: 3, url: "https://x.com/" } }, () => {});
  assert.equal(keep, false);
  t.L.onCreated({ id: 43, url: "https://x.com/informe.html", filename: "informe.html", mime: "text/html", state: "in_progress", referrer: "https://x.com/" });
  await wait(60);
  const post = t.posts("download");
  assert.equal(post.length, 1);
  assert.equal(post[0].url, "https://x.com/informe.html");
  assert.ok(!post[0].auto, "Forzada por el usuario: no es captura automática");
  // Un aviso que no viene de una página (sin sender.tab) no cuenta.
  t.L.onMessage({ type: "force-mark", url: "https://x.com/otra.html" }, {}, () => {});
  t.L.onCreated({ id: 44, url: "https://x.com/otra.html", filename: "otra.html", mime: "text/html", state: "in_progress" });
  await wait(60);
  assert.equal(t.posts("download").length, 1);
});

// Documento mínimo hecho a mano (solo lo que usa collectLinks).
function fakeDoc(o) {
  const lists = { "a[href], area[href]": o.links || [], "img[src], video[src], audio[src], source[src]": o.media || [],
                  "textarea": o.textareas || [], "iframe, frame": o.frames || [] };
  return { activeElement: o.active || null, querySelectorAll: (q) => { if (!(q in lists)) throw new Error("selector inesperado: " + q); return lists[q]; } };
}
function runCollect(bg, doc, sel, selectionOnly) {
  globalThis.document = doc;
  globalThis.window = { getSelection: () => sel };
  globalThis.location = { origin: "https://x.com" };
  try { return bg.collectLinks(selectionOnly); } finally { delete globalThis.document; delete globalThis.window; delete globalThis.location; }
}

test("todos los enlaces: recoge enlaces, <area> e imágenes, sin repetir y solo http/ftp", () => {
  const bg = loadBackground({}).bg;
  const els = [
    { href: "https://x.com/a.zip", innerText: " Archivo  A ", title: "" },
    { href: "https://x.com/a.zip", innerText: "otra vez", title: "" },
    { href: "javascript:void(0)", innerText: "nada", title: "" },
    { href: "ftp://f.com/b.iso", innerText: "", title: "imagen ISO" },
    { href: "https://x.com/mapa.pdf", alt: "zona del mapa" } // <area>: sin innerText
  ];
  const media = [{ src: "https://x.com/foto.jpg", alt: "foto", title: "" }];
  const out = runCollect(bg, fakeDoc({ links: els, media }), null, false);
  assert.equal(out.origin, "https://x.com");
  assert.deepEqual(out.links, [
    { url: "https://x.com/a.zip", text: "Archivo A" },
    { url: "ftp://f.com/b.iso", text: "imagen ISO" },
    { url: "https://x.com/mapa.pdf", text: "zona del mapa" },
    { url: "https://x.com/foto.jpg", text: "foto" }
  ]);
});

test("todos los enlaces: también los iframes sin dirección propia del mismo origen; los de otro origen no se tocan", () => {
  const bg = loadBackground({}).bg;
  const inner = fakeDoc({ links: [{ href: "https://x.com/dentro.7z", innerText: "dentro" }] });
  const frames = [
    { getAttribute: () => "", contentDocument: inner, contentWindow: { getSelection: () => null } },                 // escrito por la página
    { getAttribute: () => "https://x.com/marco", contentDocument: fakeDoc({ links: [{ href: "https://x.com/dup.zip" }] }) }, // con dirección: lo lee allFrames
    { getAttribute: () => "", get contentDocument() { throw new Error("SecurityError"); } }                             // otro origen
  ];
  const out = runCollect(bg, fakeDoc({ links: [{ href: "https://x.com/a.zip", innerText: "A" }], frames }), null, false);
  assert.deepEqual(out.links.map((l) => l.url), ["https://x.com/a.zip", "https://x.com/dentro.7z"]);
});

test("enlaces seleccionados: solo los tocados por la selección, y las direcciones escritas en ella y en los <textarea>", () => {
  const bg = loadBackground({}).bg;
  const a1 = { href: "https://x.com/si.zip", innerText: "sí" }, a2 = { href: "https://x.com/no.zip", innerText: "no" };
  const ta = { tagName: "TEXTAREA", value: "lista: https://t.com/1.iso https://t.com/2.iso" };
  const active = { tagName: "INPUT", value: "xx https://i.com/sel.rar yy", selectionStart: 3, selectionEnd: 24 };
  const sel = { rangeCount: 1, isCollapsed: false, toString: () => "Mira https://s.com/texto.pdf.", containsNode: (n) => n === a1 || n === ta };
  const out = runCollect(bg, fakeDoc({ links: [a1, a2], textareas: [ta], active }), sel, true);
  assert.deepEqual(out.links.map((l) => l.url), ["https://x.com/si.zip"]);
  assert.ok(out.text.includes("https://i.com/sel.rar") && out.text.includes("https://s.com/texto.pdf") && out.text.includes("https://t.com/2.iso"));
  // El fondo saca las direcciones del texto (sin repetir las que ya son enlaces).
  const merged = bg.mergeFrameLinks([{ frameId: 0, result: out }], false, "");
  assert.deepEqual(merged.map((l) => l.url), ["https://x.com/si.zip", "https://i.com/sel.rar", "https://s.com/texto.pdf", "https://t.com/1.iso", "https://t.com/2.iso"]);
  // Sin selección en el documento: nada (salvo lo seleccionado dentro del cuadro con el foco).
  const none = runCollect(bg, fakeDoc({ links: [a1], active }), { rangeCount: 0, isCollapsed: true, toString: () => "" }, true);
  assert.deepEqual(none.links, []);
  assert.ok(none.text.includes("https://i.com/sel.rar"));
});

test("todos los enlaces: se mandan a IDMClone con las cookies de cada servidor (todos los marcos del mismo origen)", async () => {
  const t = loadBackground({ hostReply: () => ({ ok: true, count: 2 }) });
  const targets = [];
  globalThis.chrome.scripting = { executeScript: async (o) => { targets.push(o.target); return [
    { frameId: 7, result: { origin: "https://anuncios.com", links: [{ url: "https://anuncios.com/clic", text: "anuncio" }], text: "" } },
    { frameId: 0, result: { origin: "https://x.com", links: [{ url: "https://x.com/a.zip", text: "A" }, { url: "https://y.org/b.pdf", text: "B" }], text: "" } },
    { frameId: 3, result: { origin: "https://x.com", links: [{ url: "https://x.com/c.iso", text: "C" }, { url: "javascript:alert(1)" }], text: "" } }
  ]; } };
  const r = await t.bg.sendLinks({ id: 5, url: "https://x.com/pagina", incognito: false }, false);
  assert.ok(r && r.ok);
  assert.deepEqual(targets[0], { tabId: 5, allFrames: true });
  const post = t.posts("links")[0];
  assert.deepEqual(post.links.map((l) => l.url), ["https://x.com/a.zip", "https://y.org/b.pdf", "https://x.com/c.iso"], "Sin los marcos de otro origen ni esquemas raros");
  assert.equal(post.referrer, "https://x.com/pagina");
  assert.equal(post.cookies["x.com"], "sesion=abc");
  assert.equal(post.selection, false);
});

test("enlaces seleccionados (menú): el marco pulsado, con el texto seleccionado del menú; como mucho 1000", async () => {
  const t = loadBackground({ hostReply: () => ({ ok: true }) });
  const targets = [];
  const many = Array.from({ length: 1500 }, (_, i) => ({ url: "https://x.com/f" + i, text: "" }));
  globalThis.chrome.scripting = { executeScript: async (o) => { targets.push(o.target); return [{ frameId: 4, result: { origin: "https://x.com", links: [{ url: "https://x.com/a.zip", text: "A" }], text: "y https://u.com/g.iso" } }]; } };
  await t.L.menuClicked({ menuItemId: "idmclone-selected", frameId: 4, selectionText: "ver https://t.com/f.zip" }, { id: 5, url: "https://x.com/pagina", incognito: false });
  assert.deepEqual(targets[0], { tabId: 5, frameIds: [4] });
  let p = t.posts("links")[0];
  assert.equal(p.selection, true);
  assert.deepEqual(p.links.map((l) => l.url), ["https://x.com/a.zip", "https://t.com/f.zip", "https://u.com/g.iso"]);
  globalThis.chrome.scripting = { executeScript: async () => [{ frameId: 0, result: { origin: "https://x.com", links: many, text: "" } }] };
  await t.bg.sendLinks({ id: 5, url: "https://x.com/pagina", incognito: false }, false);
  p = t.posts("links")[1];
  assert.equal(p.links.length, 1000);
  // Formato antiguo (lista) también vale; sin enlaces, aviso y nada enviado.
  assert.equal(t.bg.mergeFrameLinks([{ result: [{ url: "https://x.com/v.zip", text: "" }] }], true).length, 1);
  globalThis.chrome.scripting = { executeScript: async () => [{ frameId: 0, result: { origin: "https://x.com", links: [], text: "" } }] };
  assert.equal(await t.bg.sendLinks({ id: 5, url: "https://x.com/" }, true, 0, ""), null);
  assert.ok(t.calls.some((c) => c[0] === "notify" && c[2] === "La selección no tiene enlaces."));
  assert.equal(t.posts("links").length, 2);
});

// ------------------------------------------------------------------ panel "Descargar este video"

test("panel: reconoce listas HLS/DASH y vídeos, no los trozos de los reproductores", () => {
  assert.equal(L.classifyMedia({ url: "https://a.com/v/master.m3u8?tok=1", type: "xmlhttprequest", statusCode: 200, contentType: "application/vnd.apple.mpegurl" }).kind, "hls");
  assert.equal(L.classifyMedia({ url: "https://a.com/play?id=5", type: "xmlhttprequest", statusCode: 200, contentType: "application/x-mpegURL" }).kind, "hls");
  assert.equal(L.classifyMedia({ url: "https://a.com/v/manifest.mpd", type: "xmlhttprequest", statusCode: 200, contentType: "application/dash+xml" }).kind, "dash");
  const f = L.classifyMedia({ url: "https://a.com/peli.mp4", type: "media", statusCode: 206, contentType: "video/mp4", contentRange: "bytes 0-1023/52428800" });
  assert.deepEqual(f, { kind: "file", media: "video", size: 52428800 });
  assert.equal(L.classifyMedia({ url: "https://a.com/tema.mp3", type: "media", statusCode: 200, contentType: "audio/mpeg", contentLength: 4000000 }).media, "audio");
  assert.equal(L.classifyMedia({ url: "https://a.com/seg-12.ts", type: "xmlhttprequest", statusCode: 200, contentType: "video/mp2t" }), null);
  assert.equal(L.classifyMedia({ url: "https://a.com/chunk-1.m4s", type: "xmlhttprequest", statusCode: 200, contentType: "video/mp4" }), null);
  assert.equal(L.classifyMedia({ url: "https://a.com/frag.mp4", type: "xmlhttprequest", statusCode: 200, contentType: "video/mp4" }), null);
  assert.equal(L.classifyMedia({ url: "https://a.com/a.m3u8", type: "xmlhttprequest", statusCode: 404, contentType: "text/html" }), null);
  assert.equal(L.classifyMedia({ url: "blob:https://a.com/1", type: "media", statusCode: 200, contentType: "video/mp4" }), null);
  assert.equal(L.mediaFileName('Mi vídeo: "prueba" | Sitio', ".mp4"), "Mi vídeo prueba Sitio.mp4");
  assert.equal(L.mediaFileName("", "webm"), "video.webm");
  assert.equal(L.mediaLabel({ kind: "video", ext: ".mp4", width: 1280, height: 720, size: 26500000 }), "MP4 1280x720  25.3 MB");
  assert.equal(L.mediaLabel({ kind: "audio", ext: ".m4a", language: "es", size: -1 }), "solo audio (es) M4A");
  assert.equal(L.formatDuration(3725), "1:02:05");
  assert.equal(L.formatDuration(125), "2:05");
});

const PROBE = { ok: true, kind: "hls", url: "https://cdn.ejemplo.com/v/master.m3u8", duration: 634.6, live: false, protected: false,
  children: ["https://cdn.ejemplo.com/v/720/index.m3u8", "https://cdn.ejemplo.com/v/360/index.m3u8"],
  items: [{ kind: "video", video: "https://cdn.ejemplo.com/v/720/index.m3u8", audio: null, width: 1280, height: 720, size: 170000000, ext: ".mp4" },
          { kind: "video", video: "https://cdn.ejemplo.com/v/360/index.m3u8", audio: null, width: 640, height: 360, size: 60000000, ext: ".mp4" }] };

function hdr(url, type, ct, extra) {
  return Object.assign({ url, tabId: 4, frameId: 2, type, statusCode: 200, responseHeaders: [{ name: "Content-Type", value: ct }] }, extra || {});
}

test("panel: una lista HLS -> IDMClone da las calidades -> se avisa al marco del reproductor; sus listas hijas no", async () => {
  const t = loadBackground({ appConfig: { ok: true, exts: ["zip"], mediaPanel: true, minVideoKB: 1024 }, hostReply: (m) => (m.type === "probe" ? PROBE : { ok: true }) });
  t.L.headers(hdr("https://cdn.ejemplo.com/v/master.m3u8", "xmlhttprequest", "application/vnd.apple.mpegurl"));
  await wait(60);
  const probe = t.calls.find((c) => c[0] === "post" && c[1].type === "probe")[1];
  assert.equal(probe.url, "https://cdn.ejemplo.com/v/master.m3u8");
  assert.equal(probe.cookies, "sesion=abc");
  assert.equal(probe.referrer, "https://videos.ejemplo.com/ver/123");
  const msg = t.calls.find((c) => c[0] === "tabMsg");
  assert.equal(msg[1], 4); assert.equal(msg[3].frameId, 2);
  assert.equal(msg[2].type, "idm-media"); assert.equal(msg[2].entry.items.length, 2); assert.equal(msg[2].entry.media, "video");
  // La lista de una calidad que pide después el reproductor, sus trozos y la misma maestra otra vez: nada nuevo.
  t.L.headers(hdr("https://cdn.ejemplo.com/v/720/index.m3u8", "xmlhttprequest", "application/vnd.apple.mpegurl"));
  t.L.headers(hdr("https://cdn.ejemplo.com/v/720/seg1.ts", "xmlhttprequest", "video/mp2t"));
  t.L.headers(hdr("https://cdn.ejemplo.com/v/master.m3u8", "xmlhttprequest", "application/vnd.apple.mpegurl"));
  await wait(60);
  assert.equal(t.calls.filter((c) => c[0] === "post" && c[1].type === "probe").length, 1);
  assert.equal(t.calls.filter((c) => c[0] === "tabMsg").length, 1);
});

test("panel: elegir una calidad manda la lista, la calidad y el título de la página a IDMClone", async () => {
  const t = loadBackground({ hostReply: (m) => ({ ok: true }) });
  let reply = null;
  const entry = { kind: "hls", url: "https://cdn.ejemplo.com/v/master.m3u8", items: PROBE.items };
  t.L.onMessage({ type: "media-download", entry, item: PROBE.items[1] }, { tab: { id: 4, url: "https://videos.ejemplo.com/ver/123", title: "Mi vídeo: «prueba»" } }, (r) => { reply = r; });
  await wait(60);
  const d = t.calls.find((c) => c[0] === "post" && c[1].type === "download")[1];
  assert.equal(d.url, "https://cdn.ejemplo.com/v/master.m3u8");
  assert.equal(d.mediaKind, "hls");
  assert.equal(d.mediaVideo, "https://cdn.ejemplo.com/v/360/index.m3u8");
  assert.equal(d.filename, "Mi vídeo «prueba».mp4");
  assert.equal(d.referrer, "https://videos.ejemplo.com/ver/123");
  assert.equal(d.cookies, "sesion=abc");
  assert.ok(reply && reply.ok);
});

test("panel: vídeo directo grande sí, pequeño no; desactivado o sitio excluido, nada", async () => {
  const cfg = { ok: true, exts: ["zip"], mediaPanel: true, minVideoKB: 1024, minAudioKB: 300 };
  const big = (url) => hdr(url, "media", "video/mp4", { statusCode: 206, responseHeaders: [{ name: "content-type", value: "video/mp4" }, { name: "content-range", value: "bytes 0-1/52428800" }] });
  let t = loadBackground({ appConfig: cfg });
  t.L.headers(big("https://a.com/peli.mp4"));
  t.L.headers(hdr("https://a.com/anuncio.mp4", "media", "video/mp4", { statusCode: 206, responseHeaders: [{ name: "content-type", value: "video/mp4" }, { name: "content-range", value: "bytes 0-1/400000" }] }));
  await wait(60);
  const msgs = t.calls.filter((c) => c[0] === "tabMsg");
  assert.equal(msgs.length, 1);
  assert.equal(msgs[0][2].entry.kind, "file"); assert.equal(msgs[0][2].entry.items[0].size, 52428800);

  t = loadBackground({ appConfig: Object.assign({}, cfg, { mediaPanel: false }) });
  t.L.headers(big("https://a.com/peli.mp4"));
  await wait(60);
  assert.equal(t.calls.filter((c) => c[0] === "tabMsg").length, 0);

  t = loadBackground({ appConfig: Object.assign({}, cfg, { mediaExclude: ["videos.ejemplo.com"] }) });
  t.L.headers(big("https://a.com/peli.mp4"));
  await wait(60);
  assert.equal(t.calls.filter((c) => c[0] === "tabMsg").length, 0);
});

test("panel: si el reproductor pide una calidad antes de que IDMClone lea la maestra, espera a la maestra y no sale", async () => {
  const variant = { ok: true, kind: "hls", url: PROBE.children[0], duration: 634.6, children: [],
                    items: [{ kind: "video", video: PROBE.children[0], width: 0, height: 0, size: -1, ext: ".mp4" }] };
  const t = loadBackground({ appConfig: { ok: true, exts: ["zip"], mediaPanel: true },
    hostReply: (m) => (m.type === "probe" ? (m.url === PROBE.children[0] ? variant : PROBE) : { ok: true }),
    delay: (m) => (m.type === "probe" && m.url.endsWith("master.m3u8") ? 80 : 5) });
  t.L.headers(hdr("https://cdn.ejemplo.com/v/master.m3u8", "xmlhttprequest", "application/vnd.apple.mpegurl"));
  await wait(10);
  t.L.headers(hdr(PROBE.children[0], "xmlhttprequest", "application/vnd.apple.mpegurl"));
  await wait(200);
  const msgs = t.calls.filter((c) => c[0] === "tabMsg").map((c) => c[2].type + ":" + (c[2].entry ? c[2].entry.url : c[2].url));
  // Sin aparecer un momento y desaparecer (lo que se veía en X): solo la maestra.
  assert.deepEqual(msgs, ["idm-media:https://cdn.ejemplo.com/v/master.m3u8"]);
});

test("panel: lista de una sola calidad sin maestra en la pestaña -> se ofrece sin esperar", async () => {
  const lone = { ok: true, kind: "hls", url: "https://cdn.ejemplo.com/v/solo.m3u8", duration: 60, children: [],
                 items: [{ kind: "video", video: "https://cdn.ejemplo.com/v/solo.m3u8", width: 0, height: 0, size: -1, ext: ".mp4" }] };
  const t = loadBackground({ appConfig: { ok: true, exts: ["zip"], mediaPanel: true }, hostReply: (m) => (m.type === "probe" ? lone : { ok: true }) });
  t.L.headers(hdr("https://cdn.ejemplo.com/v/solo.m3u8", "xmlhttprequest", "application/vnd.apple.mpegurl"));
  await wait(60);
  const msgs = t.calls.filter((c) => c[0] === "tabMsg").map((c) => c[2].type + ":" + (c[2].entry ? c[2].entry.url : c[2].url));
  assert.deepEqual(msgs, ["idm-media:https://cdn.ejemplo.com/v/solo.m3u8"]);
});

// ------------------------------------------------------------------ directos y archivos pedidos a trozos

test("panel: archivo pedido a trozos (Range o ?bytestart=) -> el archivo entero; trozos de lista, no", () => {
  const a = L.classifyMedia({ url: "https://cdn.x.com/v/clip.mp4?tok=1&bytestart=0&byteend=99999", type: "xmlhttprequest", statusCode: 200, contentType: "video/mp4" });
  assert.equal(a.kind, "file"); assert.equal(a.ranged, true); assert.equal(a.url, "https://cdn.x.com/v/clip.mp4?tok=1");
  const b = L.classifyMedia({ url: "https://cdn.x.com/v/audio.mp4", type: "xmlhttprequest", statusCode: 206, contentType: "audio/mp4", contentRange: "bytes 0-999/5000000" });
  assert.deepEqual([b.kind, b.media, b.size], ["file", "audio", 5000000]);
  assert.equal(L.classifyMedia({ url: "https://cdn.x.com/v/seg-3.mp4", type: "xmlhttprequest", statusCode: 200, contentType: "video/mp4" }), null);
  assert.equal(L.classifyMedia({ url: "https://cdn.x.com/v/chunk.m4s", type: "xmlhttprequest", statusCode: 206, contentType: "video/mp4", contentRange: "bytes 0-9/99" }), null);
  assert.equal(L.stripRange("https://a.com/x.mp4?range=0-100&a=1"), "https://a.com/x.mp4?a=1");
});

function ranged(url, ct, total) {
  return hdr(url, "xmlhttprequest", ct, { statusCode: 206, responseHeaders: [{ name: "content-type", value: ct }, { name: "content-range", value: "bytes 0-1/" + total }] });
}

test("panel: vídeo y audio en archivos aparte -> un solo elemento que IDMClone mezcla", async () => {
  const t = loadBackground({ appConfig: { ok: true, exts: ["zip"], mediaPanel: true, minVideoKB: 1024, minAudioKB: 300 }, hostReply: (m) => ({ ok: true }) });
  t.L.headers(ranged("https://cdn.x.com/v/video_720.mp4?bytestart=0&byteend=9", "video/mp4", 40000000));
  await wait(40);
  t.L.headers(ranged("https://cdn.x.com/v/video_720.mp4?bytestart=10&byteend=99", "video/mp4", 40000000)); // otro trozo del mismo
  t.L.headers(ranged("https://cdn.x.com/v/audio_128.mp4?bytestart=0&byteend=9", "audio/mp4", 5000000));
  await wait(60);
  const msgs = t.calls.filter((c) => c[0] === "tabMsg").map((c) => c[2]);
  const pair = msgs.filter((x) => x.type === "idm-media" && x.entry.kind === "pair").pop();
  assert.ok(pair, "falta el par: " + JSON.stringify(msgs.map((x) => x.type + ":" + (x.entry ? x.entry.kind : x.url))));
  assert.equal(pair.entry.items[0].video, "https://cdn.x.com/v/video_720.mp4");
  assert.equal(pair.entry.items[0].audio, "https://cdn.x.com/v/audio_128.mp4");
  assert.equal(pair.entry.items[0].size, 45000000);
  assert.ok(msgs.some((x) => x.type === "idm-media-remove" && x.url === "https://cdn.x.com/v/video_720.mp4"), "el vídeo suelto no se quitó");
  assert.equal(msgs.filter((x) => x.type === "idm-media" && x.entry.kind === "file").length, 1, "el mismo archivo se ofreció dos veces");
  // Elegirlo: IDMClone recibe los dos archivos para mezclarlos.
  t.L.onMessage({ type: "media-download", entry: pair.entry, item: pair.entry.items[0] }, { tab: { id: 4, url: "https://videos.ejemplo.com/ver/123", title: "Clip" } }, () => {});
  await wait(60);
  const d = t.calls.find((c) => c[0] === "post" && c[1].type === "download")[1];
  assert.deepEqual([d.mediaKind, d.url, d.mediaVideo, d.mediaAudio, d.filename], ["pair", "https://cdn.x.com/v/video_720.mp4", "https://cdn.x.com/v/video_720.mp4", "https://cdn.x.com/v/audio_128.mp4", "Clip.mp4"]);
});

test("panel: emisión en directo -> se ofrece (para grabarla); trozos de su lista no", async () => {
  const live = Object.assign({}, PROBE, { live: true, duration: 0 });
  const t = loadBackground({ appConfig: { ok: true, exts: ["zip"], mediaPanel: true }, hostReply: (m) => (m.type === "probe" ? live : { ok: true }) });
  t.L.headers(hdr("https://cdn.ejemplo.com/v/master.m3u8", "xmlhttprequest", "application/vnd.apple.mpegurl"));
  await wait(60);
  t.L.headers(ranged("https://cdn.ejemplo.com/v/todo.mp4?range=0-99", "video/mp4", 90000000));
  await wait(60);
  const msgs = t.calls.filter((c) => c[0] === "tabMsg").map((c) => c[2]);
  assert.equal(msgs.length, 1);
  assert.equal(msgs[0].entry.live, true);
});

test("panel: el audio pequeño de un vídeo pedido a trozos no se ofrece solo, pero se junta con su vídeo", async () => {
  const t = loadBackground({ appConfig: { ok: true, exts: ["zip"], mediaPanel: true, minVideoKB: 1024, minAudioKB: 300 }, hostReply: (m) => ({ ok: true }) });
  t.L.headers(ranged("https://cdn.x.com/v/audio_64.mp4?bytestart=0&byteend=9", "audio/mp4", 200000));
  await wait(40);
  assert.equal(t.calls.filter((c) => c[0] === "tabMsg").length, 0, "el audio pequeño no debe salir solo");
  t.L.headers(ranged("https://cdn.x.com/v/video_360.mp4?bytestart=0&byteend=9", "video/mp4", 9000000));
  await wait(60);
  const pair = t.calls.filter((c) => c[0] === "tabMsg" && c[2].type === "idm-media" && c[2].entry.kind === "pair");
  assert.equal(pair.length, 1);
  assert.equal(pair[0][2].entry.items[0].audio, "https://cdn.x.com/v/audio_64.mp4");
});

// Mínimos por defecto de IDMClone (1 MB vídeo, 300 KB audio); mp4/m4a no están en la lista: cuentan los generales.
const CFG_MIN = { ok: true, exts: ["zip"], mediaPanel: true, minVideoKB: 1024, minAudioKB: 300, mediaTypes: [{ ext: "flv", minKB: 0, on: true }] };
const shown = (t) => panelMsgs(t).filter((x) => x.type === "idm-media").map((x) => x.entry);
const panelMsgs = (t) => t.calls.filter((c) => c[0] === "tabMsg").map((c) => c[2]);
function hlsWith(items) { return { ok: true, kind: "hls", url: "https://cdn.x.com/v/master.m3u8", duration: 20, live: false, protected: false, children: [], items }; }
const X_AUDIO = { kind: "audio", audio: "https://cdn.x.com/v/a128.m3u8", size: 320000, ext: ".m4a" };

test("panel: lista HLS de baja resolución (todos los vídeos pequeños) -> se ofrece el vídeo con su audio, no el audio solo", async () => {
  const probe = hlsWith([{ kind: "video", video: "https://cdn.x.com/v/270.m3u8", width: 480, height: 270, size: 700000, ext: ".mp4" },
                         { kind: "video", video: "https://cdn.x.com/v/180.m3u8", width: 320, height: 180, size: 400000, ext: ".mp4" }, X_AUDIO]);
  const t = loadBackground({ appConfig: CFG_MIN, hostReply: (m) => (m.type === "probe" ? probe : { ok: true }) });
  t.L.headers(hdr("https://cdn.x.com/v/master.m3u8", "xmlhttprequest", "application/vnd.apple.mpegurl"));
  await wait(60);
  t.L.headers(ranged("https://cdn.x.com/v/todo.mp4?range=0-99", "video/mp4", 90000000)); // trozo de la lista: no es otro vídeo
  await wait(40);
  const e = shown(t);
  assert.equal(e.length, 1, JSON.stringify(panelMsgs(t)));
  assert.equal(e[0].media, "video");
  assert.deepEqual(e[0].items.map((i) => i.kind + ":" + (i.height || 0)), ["video:270", "video:180", "audio:0"]);
  // Con el tipo mp4 desmarcado, esas calidades no salen (decisión del usuario): solo el audio, como hasta ahora.
  const t2 = loadBackground({ appConfig: Object.assign({}, CFG_MIN, { mediaTypes: [{ ext: "mp4", minKB: 0, on: false }] }),
                              hostReply: (m) => (m.type === "probe" ? probe : { ok: true }) });
  t2.L.headers(hdr("https://cdn.x.com/v/master.m3u8", "xmlhttprequest", "application/vnd.apple.mpegurl"));
  await wait(60);
  assert.deepEqual(shown(t2).map((x) => x.media), ["audio"]);
});

test("panel: lista HLS solo de audio sí se ofrece; con los tipos de vídeo desmarcados, el audio también (como hasta ahora)", async () => {
  let t = loadBackground({ appConfig: CFG_MIN, hostReply: (m) => (m.type === "probe" ? hlsWith([X_AUDIO]) : { ok: true }) });
  t.L.headers(hdr("https://cdn.x.com/v/master.m3u8", "xmlhttprequest", "application/vnd.apple.mpegurl"));
  await wait(60);
  let e = shown(t);
  assert.equal(e.length, 1); assert.equal(e[0].media, "audio");
  const probe = hlsWith([{ kind: "video", video: "https://cdn.x.com/v/720.m3u8", width: 1280, height: 720, size: 9000000, ext: ".mp4" }, X_AUDIO]);
  t = loadBackground({ appConfig: Object.assign({}, CFG_MIN, { mediaTypes: [{ ext: "mp4", minKB: 0, on: false }] }), hostReply: (m) => (m.type === "probe" ? probe : { ok: true }) });
  t.L.headers(hdr("https://cdn.x.com/v/master.m3u8", "xmlhttprequest", "application/vnd.apple.mpegurl"));
  await wait(60);
  e = shown(t);
  assert.equal(e.length, 1); assert.equal(e[0].media, "audio");
});

test("panel: lista HLS con un vídeo grande y otro pequeño -> el grande, con su audio", async () => {
  const probe = hlsWith([{ kind: "video", video: "https://cdn.x.com/v/720.m3u8", width: 1280, height: 720, size: 9000000, ext: ".mp4" },
                         { kind: "video", video: "https://cdn.x.com/v/180.m3u8", width: 320, height: 180, size: 400000, ext: ".mp4" }, X_AUDIO]);
  const t = loadBackground({ appConfig: CFG_MIN, hostReply: (m) => (m.type === "probe" ? probe : { ok: true }) });
  t.L.headers(hdr("https://cdn.x.com/v/master.m3u8", "xmlhttprequest", "application/vnd.apple.mpegurl"));
  await wait(60);
  const e = shown(t);
  assert.equal(e.length, 1); assert.equal(e[0].media, "video");
  assert.deepEqual(e[0].items.map((i) => i.kind + ":" + i.size), ["video:9000000", "audio:320000"]);
});

test("panel: audio a trozos y luego un vídeo pequeño -> nada (el audio no sale solo como música)", async () => {
  let t = loadBackground({ appConfig: CFG_MIN, hostReply: () => ({ ok: true }) });
  t.bg.MEDIA_TIMING.audioWait = 40;
  t.L.headers(ranged("https://cdn.x.com/v/audio_128.mp4?bytestart=0&byteend=9", "audio/mp4", 5000000));
  await wait(15);
  t.L.headers(ranged("https://cdn.x.com/v/video_270.mp4?bytestart=0&byteend=9", "video/mp4", 400000));
  await wait(100);
  assert.equal(panelMsgs(t).filter((x) => x.type === "idm-media").length, 0, JSON.stringify(panelMsgs(t)));
  // Si el vídeo pequeño llega cuando el audio ya salía solo, se quita del panel.
  t = loadBackground({ appConfig: CFG_MIN, hostReply: () => ({ ok: true }) });
  t.bg.MEDIA_TIMING.audioWait = 20;
  t.L.headers(ranged("https://cdn.x.com/v/audio_128.mp4?bytestart=0&byteend=9", "audio/mp4", 5000000));
  await wait(80);
  assert.equal(shown(t).length, 1);
  t.L.headers(ranged("https://cdn.x.com/v/video_270.mp4?bytestart=0&byteend=9", "video/mp4", 400000));
  await wait(40);
  assert.ok(panelMsgs(t).some((x) => x.type === "idm-media-remove" && x.url === "https://cdn.x.com/v/audio_128.mp4"), "el audio solo no se quitó");
  assert.equal(shown(t).length, 1, "nada nuevo en el panel");
});

test("panel: audio a trozos antes que su vídeo -> un solo par, sin enseñar antes el audio solo", async () => {
  const t = loadBackground({ appConfig: CFG_MIN, hostReply: () => ({ ok: true }) });
  t.bg.MEDIA_TIMING.audioWait = 40;
  t.L.headers(ranged("https://cdn.x.com/v/audio_128.mp4?bytestart=0&byteend=9", "audio/mp4", 5000000));
  await wait(15);
  assert.equal(shown(t).length, 0, "el audio no debe salir antes de esperar a su vídeo");
  t.L.headers(ranged("https://cdn.x.com/v/video_720.mp4?bytestart=0&byteend=9", "video/mp4", 40000000));
  await wait(100);
  const e = shown(t);
  assert.deepEqual(e.map((x) => x.kind), ["pair"], JSON.stringify(e));
  assert.deepEqual([e[0].items[0].video, e[0].items[0].audio, e[0].items[0].size], ["https://cdn.x.com/v/video_720.mp4", "https://cdn.x.com/v/audio_128.mp4", 45000000]);
});

test("panel: audio a trozos sin ningún vídeo -> se ofrece solo tras la espera; si la página cambia antes, no", async () => {
  let t = loadBackground({ appConfig: CFG_MIN, hostReply: () => ({ ok: true }) });
  t.bg.MEDIA_TIMING.audioWait = 40;
  t.L.headers(ranged("https://cdn.x.com/v/audio_128.mp4?bytestart=0&byteend=9", "audio/mp4", 5000000));
  await wait(15);
  assert.equal(shown(t).length, 0);
  await wait(80);
  const e = shown(t);
  assert.equal(e.length, 1); assert.deepEqual([e[0].kind, e[0].media, e[0].url], ["file", "audio", "https://cdn.x.com/v/audio_128.mp4"]);
  // Página nueva mientras espera: el audio era de la anterior.
  t = loadBackground({ appConfig: CFG_MIN, hostReply: () => ({ ok: true }) });
  t.bg.MEDIA_TIMING.audioWait = 40;
  t.L.headers(ranged("https://cdn.x.com/v/audio_128.mp4?bytestart=0&byteend=9", "audio/mp4", 5000000));
  await wait(15);
  t.L.tabUpdated(4, { url: "https://videos.ejemplo.com/ver/456", status: "loading" }, { id: 4, url: "https://videos.ejemplo.com/ver/456" });
  await wait(80);
  assert.equal(shown(t).length, 0);
});

// ------------------------------------------------------------------ opción 3: integración con el navegador (1.4)

const CFG14 = { ok: true, exts: ["zip", "mp3"], exclude: [], mediaPanel: true, minVideoKB: 0, minAudioKB: 0 };
function big(url, ct, size) {
  return hdr(url, "media", ct, { statusCode: 200, responseHeaders: [{ name: "content-type", value: ct }, { name: "content-length", value: String(size) }] });
}
const tabMsgs = (t) => t.calls.filter((c) => c[0] === "tabMsg" && c[2].type === "idm-media").map((c) => c[2].entry);

test("opción 3: manifiesto 1.5 sin permisos nuevos", () => {
  const m = require(path.join(EXT, "manifest.json"));
  assert.equal(m.version, "1.5");
  assert.ok(!m.permissions.includes("management"), "management.getSelf no necesita el permiso");
  assert.ok(!m.permissions.includes("proxy"), "El permiso proxy no se pide (ver nota de #17)");
});

test("opción 3: getconfig anuncia la versión y que la extensión sabe contestar a las cookies", async () => {
  const t = loadBackground({ appConfig: CFG14 });
  await t.bg.getAppConfig();
  const g = t.posts("getconfig")[0];
  assert.deepEqual(g.caps, ["getcookies"]);
});

test("opción 3: #1 navegador desmarcado -> sin captura automática ni panel de vídeo; el menú contextual sigue", async () => {
  const t = loadBackground({ appConfig: Object.assign({}, CFG14, { browser: "Brave", captureOff: true }), hostReply: () => ({ ok: true }) });
  const s = await t.bg.getSettings();
  assert.equal(s.enabled, false);
  assert.equal(s.captureOff, true);
  assert.equal(s.userEnabled, true, "El ajuste propio del usuario no se pierde");
  assert.ok(/Brave/.test(s.offReason));
  t.L.onCreated({ id: 50, url: "https://x.com/a.zip", filename: "a.zip", state: "in_progress" });
  t.L.headers(big("https://a.com/peli.mp4", "video/mp4", 50000000));
  await wait(60);
  assert.equal(t.posts("download").length, 0, "No debe capturar");
  assert.ok(!t.names().includes("pause"));
  assert.equal(tabMsgs(t).length, 0, "No debe haber panel de vídeo");
  await t.L.menuClicked({ menuItemId: "idmclone-link", linkUrl: "https://x.com/b.iso" }, { url: "https://x.com/pag" });
  const d = t.posts("download");
  assert.equal(d.length, 1, "El menú contextual sigue funcionando");
  assert.equal(d[0].url, "https://x.com/b.iso");
  assert.ok(!d[0].auto, "Un clic del usuario no es captura automática");
});

test("opción 3: #1 captura normal marca auto:true (el host la rechaza si el navegador está desmarcado)", async () => {
  const t = loadBackground({ appConfig: CFG14, hostReply: () => ({ ok: false, error: "La captura está desactivada para Brave" }) });
  t.L.onCreated({ id: 51, url: "https://x.com/a.zip", filename: "a.zip", state: "in_progress" });
  await wait(60);
  const d = t.posts("download");
  assert.equal(d.length, 1);
  assert.equal(d[0].auto, true);
  assert.deepEqual(t.names().filter((x) => ["pause", "cancel", "resume"].includes(x)), ["pause", "resume"], "Rechazada: sigue en el navegador");
});

test("opción 3: #2 menú de Chromium según IDMClone; solo se rehace si cambian las opciones", async () => {
  let menu = { link: true, all: false };
  const t = loadBackground({ appConfig: () => Object.assign({}, CFG14, { menuChrome: menu, menuFirefox: { link: false, all: true } }) });
  await t.bg.fetchAppConfig(); await t.bg.menuDone();
  assert.deepEqual(t.menus.ids, ["idmclone-link", "idmclone-media", "idmclone-page", "idmclone-selected"]);
  assert.equal(t.menus.removeAll, 1);
  await t.bg.fetchAppConfig(); await t.bg.menuDone();
  assert.equal(t.menus.removeAll, 1, "Mismas opciones: no se rehace");
  menu = { link: false, all: true };
  await t.bg.fetchAppConfig(); await t.bg.menuDone();
  assert.deepEqual(t.menus.ids, ["idmclone-all"]);
  // Al arrancar el navegador se rehace con las guardadas.
  t.L.onStartup(); await wait(10); await t.bg.menuDone();
  assert.deepEqual(t.menus.ids, ["idmclone-all"]);
});

test("opción 3: #2 Firefox usa sus propias opciones; IDMClone antiguo deja los cuatro", async () => {
  const f = loadBackground({ firefox: true, appConfig: Object.assign({}, CFG14, { menuChrome: { link: true, all: true }, menuFirefox: { link: true, all: false } }) });
  assert.equal(f.bg.IS_FIREFOX, true);
  await f.bg.fetchAppConfig(); await f.bg.menuDone();
  assert.deepEqual(f.menus.ids, ["idmclone-link", "idmclone-media", "idmclone-page", "idmclone-selected"]);

  const old = loadBackground({ appConfig: CFG14 });
  old.L.onInstalled(); await wait(10); await old.bg.menuDone();
  assert.deepEqual(old.menus.ids, ["idmclone-link", "idmclone-media", "idmclone-page", "idmclone-selected", "idmclone-all"]);
  await old.bg.fetchAppConfig(); await old.bg.menuDone();
  assert.equal(old.menus.removeAll, 1, "Sin opciones de IDMClone no se toca");
});

test("opción 3: #8 lógica de la lista de tipos del panel", () => {
  const cfg = { minVideoKB: 1024, minAudioKB: 300, mediaTypes: [{ ext: "mp4", minKB: 0, on: false }, { ext: "mp3", minKB: 50, on: true }] };
  assert.equal(L.mediaTypeAllowed("mp4", 9e6, "video", cfg).off, true);
  assert.equal(L.mediaTypeAllowed("mp3", 40 * 1024, "audio", cfg).show, false);
  assert.equal(L.mediaTypeAllowed("mp3", 60 * 1024, "audio", cfg).show, true);
  assert.equal(L.mediaTypeAllowed("xyz", 500 * 1024, "video", cfg).show, false, "Tipo fuera de la lista: mínimo general");
  assert.equal(L.mediaTypeAllowed("", 2e6, "video", cfg).show, true, "Sin extensión: se muestra");
  assert.equal(L.mediaTypeAllowed("mp4", 9e6, "video", { minVideoKB: 1024 }).show, true, "IDMClone antiguo: solo los mínimos");
  assert.equal(L.mediaExt("https://r1.googlevideo.com/videoplayback?mime=video%2Fwebm", "video/webm"), "webm");
  assert.equal(L.mediaExt("https://a.com/ver.php?id=5", "video/mp4"), "mp4");
  assert.equal(L.mediaExt("https://a.com/x.flv", "application/octet-stream"), "flv");
  assert.equal(L.MIME_EXT["video/x-flv"], "flv");
});

test("opción 3: #8 el panel solo aparece para los tipos marcados y con su tamaño mínimo", async () => {
  const types = [{ ext: "mp4", minKB: 0, on: false }, { ext: "webm", minKB: 0, on: true }, { ext: "mp3", minKB: 50, on: true }];
  const t = loadBackground({ appConfig: Object.assign({}, CFG14, { mediaTypes: types }) });
  t.L.headers(big("https://a.com/peli.mp4", "video/mp4", 50000000));
  t.L.headers(big("https://a.com/play?id=1", "video/mp4", 50000000)); // sin extensión: cuenta el tipo MIME
  t.L.headers(big("https://a.com/peli.webm", "video/webm", 50000000));
  t.L.headers(big("https://a.com/corto.mp3", "audio/mpeg", 40 * 1024));
  t.L.headers(big("https://a.com/tema.mp3", "audio/mpeg", 60 * 1024));
  await wait(80);
  assert.deepEqual(tabMsgs(t).map((e) => e.url).sort(), ["https://a.com/peli.webm", "https://a.com/tema.mp3"]);
  assert.equal(t.posts("download").length, 0, "Con la casilla de #9 marcada (por defecto en IDMClone antiguo) no se descarga nada");
});

test("opción 3: #8 HLS/DASH: solo las calidades de tipos marcados; ninguna -> sin panel", async () => {
  const probe = Object.assign({}, PROBE, { children: [], items: [PROBE.items[0], { kind: "video", video: "https://cdn.ejemplo.com/v/w.m3u8", width: 640, height: 360, size: 1, ext: ".webm" }] });
  let types = [{ ext: "mp4", minKB: 0, on: false }, { ext: "webm", minKB: 0, on: true }];
  let t = loadBackground({ appConfig: () => Object.assign({}, CFG14, { mediaTypes: types }), hostReply: (m) => (m.type === "probe" ? probe : { ok: true }) });
  t.L.headers(hdr("https://cdn.ejemplo.com/v/master.m3u8", "xmlhttprequest", "application/vnd.apple.mpegurl"));
  await wait(60);
  let e = tabMsgs(t);
  assert.equal(e.length, 1);
  assert.deepEqual(e[0].items.map((i) => i.ext), [".webm"]);
  types = [{ ext: "mp4", minKB: 0, on: false }, { ext: "webm", minKB: 0, on: false }];
  t = loadBackground({ appConfig: () => Object.assign({}, CFG14, { mediaTypes: types }), hostReply: (m) => (m.type === "probe" ? probe : { ok: true }) });
  t.L.headers(hdr("https://cdn.ejemplo.com/v/master.m3u8", "xmlhttprequest", "application/vnd.apple.mpegurl"));
  t.L.headers(ranged("https://cdn.ejemplo.com/v/todo.mp4?range=0-99", "video/mp4", 90000000)); // trozo de la lista: tampoco
  await wait(80);
  assert.equal(tabMsgs(t).length, 0);
});

test("opción 3: #9 tipo desmarcado que está en Tipos de archivo -> IDMClone lo descarga solo (una vez)", async () => {
  const base = Object.assign({}, CFG14, { exts: ["mp3"], mediaTypes: [{ ext: "mp3", minKB: 0, on: false }] });
  let t = loadBackground({ appConfig: Object.assign({}, base, { mediaNoAutoCapture: false }), hostReply: () => ({ ok: true }) });
  t.L.headers(big("https://a.com/tema.mp3", "audio/mpeg", 4000000));
  t.L.headers(big("https://a.com/tema.mp3", "audio/mpeg", 4000000));
  t.L.headers(big("https://a.com/otro.ogg", "audio/ogg", 4000000)); // tipo no listado: panel, no descarga
  await wait(80);
  const d = t.posts("download");
  assert.equal(d.length, 1);
  assert.equal(d[0].url, "https://a.com/tema.mp3");
  assert.equal(d[0].auto, true);
  assert.equal(d[0].referrer, "https://videos.ejemplo.com/ver/123");
  assert.deepEqual(tabMsgs(t).map((e) => e.url), ["https://a.com/otro.ogg"]);

  t = loadBackground({ appConfig: Object.assign({}, base, { mediaNoAutoCapture: true }), hostReply: () => ({ ok: true }) });
  t.L.headers(big("https://a.com/tema.mp3", "audio/mpeg", 4000000));
  await wait(60);
  assert.equal(t.posts("download").length, 0, "Casilla marcada: nada");

  t = loadBackground({ appConfig: Object.assign({}, base, { mediaNoAutoCapture: false, captureOff: true, browser: "Brave" }), hostReply: () => ({ ok: true }) });
  t.L.headers(big("https://a.com/tema.mp3", "audio/mpeg", 4000000));
  await wait(60);
  assert.equal(t.posts("download").length, 0, "Navegador desmarcado: nada");

  t = loadBackground({ appConfig: Object.assign({}, base, { mediaNoAutoCapture: false, exts: ["zip"] }), hostReply: () => ({ ok: true }) });
  t.L.headers(big("https://a.com/tema.mp3", "audio/mpeg", 4000000));
  await wait(60);
  assert.equal(t.posts("download").length, 0, "No está en Tipos de archivo: nada");
});

test("opción 3: #10 contenido protegido: sin panel si la opción está desmarcada (como IDM)", async () => {
  const prot = Object.assign({}, PROBE, { protected: true });
  let t = loadBackground({ appConfig: Object.assign({}, CFG14, { mediaProtected: false }), hostReply: (m) => (m.type === "probe" ? prot : { ok: true }) });
  t.L.headers(hdr("https://cdn.ejemplo.com/v/master.m3u8", "xmlhttprequest", "application/vnd.apple.mpegurl"));
  await wait(60);
  assert.equal(tabMsgs(t).length, 0);
  t = loadBackground({ appConfig: Object.assign({}, CFG14, { mediaProtected: true }), hostReply: (m) => (m.type === "probe" ? prot : { ok: true }) });
  t.L.headers(hdr("https://cdn.ejemplo.com/v/master.m3u8", "xmlhttprequest", "application/vnd.apple.mpegurl"));
  await wait(60);
  assert.equal(tabMsgs(t).length, 1);
  assert.equal(tabMsgs(t)[0].protected, true);
  // IDMClone antiguo (sin la clave): como antes, se muestra.
  t = loadBackground({ appConfig: CFG14, hostReply: (m) => (m.type === "probe" ? prot : { ok: true }) });
  t.L.headers(hdr("https://cdn.ejemplo.com/v/master.m3u8", "xmlhttprequest", "application/vnd.apple.mpegurl"));
  await wait(60);
  assert.equal(tabMsgs(t).length, 1);
});

test("opción 3: #11 lógica del panel de selección: modos, sitios, direcciones en el texto y validación", () => {
  assert.equal(L.selectionPanelAllowed("https://x.com/a", { mode: 0, sites: [] }), true);
  assert.equal(L.selectionPanelAllowed("https://x.com/a", { mode: 1, sites: [] }), false);
  assert.equal(L.selectionPanelAllowed("https://www.mediafire.com/f", { mode: 2, sites: ["mediafire.com"] }), true);
  assert.equal(L.selectionPanelAllowed("https://x.com/a", { mode: 2, sites: ["mediafire.com", "*.mega.nz"] }), false);
  assert.equal(L.selectionPanelAllowed("file:///C:/a.html", { mode: 0 }), false);
  assert.equal(L.selectionPanelAllowed("https://x.com/a", null), false, "IDMClone antiguo: sin panel");
  assert.deepEqual(L.textUrls("Bájalo de https://x.com/a.zip, o (https://y.org/b_(1).pdf). También ftp://f.com/c.iso; fin."),
    ["https://x.com/a.zip", "https://y.org/b_(1).pdf", "ftp://f.com/c.iso"]);
  assert.deepEqual(L.textUrls("sin enlaces: javascript:alert(1) data:text/html,x"), []);
  assert.equal(L.textUrls(Array.from({ length: 50 }, (_, i) => "https://x.com/" + i).join(" "), 10).length, 10);
  const bad = L.sanitizeLinks([{ url: "javascript:alert(1)" }, { url: "data:text/html,hola" }, { url: "https://x.com/a", text: "  A\n  b " }, "https://x.com/a", { url: 5 }, null, { url: "https://" + "x".repeat(5000) }]);
  assert.deepEqual(bad, [{ url: "https://x.com/a", text: "A b" }]);
  assert.equal(L.sanitizeLinks(Array.from({ length: 3000 }, (_, i) => "https://x.com/" + i)).length, 1000);
  assert.equal(L.sanitizeLinks("no es una lista").length, 0);
});

test("opción 3: #11 al pulsar el panel, los enlaces seleccionados llegan a IDMClone (selection:true) con cookies", async () => {
  const t = loadBackground({ hostReply: () => ({ ok: true, count: 2 }) });
  let reply = null;
  const links = [{ url: "https://x.com/a.zip", text: "A".repeat(500) }, { url: "javascript:alert(1)" }, { url: "https://y.org/b.pdf" }]
    .concat(Array.from({ length: 2000 }, (_, i) => ({ url: "https://x.com/f" + i })));
  const keep = t.L.onMessage({ type: "selection-links", links }, { tab: { id: 5, url: "https://x.com/pagina", incognito: false }, url: "https://x.com/marco" }, (r) => { reply = r; });
  assert.equal(keep, true);
  await wait(60);
  const p = t.posts("links");
  assert.equal(p.length, 1);
  assert.equal(p[0].selection, true);
  assert.equal(p[0].links.length, 1000);
  assert.ok(p[0].links.every((l) => /^https?:/.test(l.url) && l.text.length <= 200));
  assert.equal(p[0].referrer, "https://x.com/marco");
  assert.equal(p[0].cookies["x.com"], "sesion=abc");
  assert.ok(reply && reply.ok);
  // Sin pestaña (no viene de una página) o sin enlaces válidos: nada.
  assert.equal(t.L.onMessage({ type: "selection-links", links }, {}, () => {}), false);
  let r2 = null;
  t.L.onMessage({ type: "selection-links", links: [{ url: "javascript:x" }] }, { tab: { id: 5, url: "https://x.com/" } }, (r) => { r2 = r; });
  await wait(20);
  assert.equal(r2.ok, false);
  assert.equal(t.posts("links").length, 1);
});

test("opción 3: #11 el panel de selección existe en content.js y no mete HTML de la página", () => {
  const src = require("node:fs").readFileSync(path.join(EXT, "content.js"), "utf8");
  assert.ok(src.includes('type: "selection-links"'));
  assert.ok(src.includes("selectionPanelAllowed"));
  assert.ok(src.includes("captureOff"), "Respeta el navegador desmarcado (#1)");
  assert.ok(/addEventListener\("mouseup"/.test(src) && !/selectionchange/.test(src), "Solo tras soltar el ratón o una tecla");
  // El único innerHTML del panel de selección se construye con constantes (CSS, ICON y textos fijos).
  const sel = src.substring(src.indexOf("Panel \"Descargar con IDMClone\" para los enlaces seleccionados"));
  const html = sel.match(/innerHTML\s*=\s*`[\s\S]*?`;/g) || [];
  assert.equal(html.length, 1);
  assert.ok(!/\$\{(?!CSS\}|ICON\}|mini\s*\?)/.test(html[0]), "Interpolación inesperada en el panel: " + html[0]);
});

test("opción 3: #17 proxy del navegador: Firefox (proxyInfo) y Chromium (proxy.settings, si hubiera permiso)", async () => {
  assert.equal(L.proxyFromInfo({ type: "http", host: "10.0.0.1", port: 3128 }), "10.0.0.1:3128", "HTTP sin esquema (IDMClone antiguo)");
  assert.equal(L.proxyFromInfo({ type: "socks", host: "10.0.0.1", port: 1080 }), "socks5://10.0.0.1:1080");
  assert.equal(L.proxyFromInfo({ type: "socks", host: "10.0.0.1", port: 1080, proxyDNS: true }), "socks5://10.0.0.1:1080");
  assert.equal(L.proxyFromInfo({ type: "socks4", host: "10.0.0.1", port: 1080 }), "socks4://10.0.0.1:1080");
  assert.equal(L.proxyFromInfo({ type: "socks4", host: "p.lan", port: 1080, proxyDNS: true }), "socks4a://p.lan:1080", "SOCKS4 con DNS remoto");
  assert.equal(L.proxyFromInfo({ type: "https", host: "p.com", port: 443 }), "", "TLS hasta el proxy: IDMClone no lo admite");
  assert.equal(L.proxyFromInfo({ type: "direct" }), "");
  assert.equal(L.proxyFromInfo({ type: "socks", host: "a b", port: 1080 }), "");
  assert.equal(L.proxyFromInfo(null), "");
  assert.equal(L.proxyFromInfo({ type: "http", host: "::1", port: 8080 }), "[::1]:8080");

  let t = loadBackground({ firefox: true, hostReply: () => ({ ok: true }) });
  t.L.headers({ url: "https://x.com/a.zip", type: "main_frame", proxyInfo: { type: "http", host: "proxy.lan", port: 8080, username: "yo" }, responseHeaders: [{ name: "Content-Type", value: "application/zip" }] });
  t.L.headers({ url: "https://y.com/b.zip", type: "main_frame", proxyInfo: { type: "socks", host: "proxy.lan", port: 1080 }, responseHeaders: [{ name: "Content-Type", value: "application/zip" }] });
  t.L.onCreated({ id: 60, url: "https://x.com/a.zip", filename: "", state: "in_progress" });
  t.L.onCreated({ id: 61, url: "https://y.com/b.zip", filename: "", state: "in_progress" });
  await wait(60);
  let d = t.posts("download");
  assert.equal(d.find((x) => x.url === "https://x.com/a.zip").browserProxy, "proxy.lan:8080");
  assert.equal(d.find((x) => x.url === "https://y.com/b.zip").browserProxy, "socks5://proxy.lan:1080", "SOCKS también (IDMClone sale por su puente SOCKS)");
  assert.ok(!JSON.stringify(d).includes("yo"), "Nunca se manda el usuario del proxy");

  t = loadBackground({ hostReply: () => ({ ok: true }), proxySettings: { mode: "fixed_servers", rules: { singleProxy: { scheme: "http", host: "10.1.1.1", port: 3128 }, bypassList: ["<local>"] } } });
  await t.L.menuClicked({ menuItemId: "idmclone-link", linkUrl: "https://x.com/a.iso" }, { url: "https://x.com/" });
  await t.L.menuClicked({ menuItemId: "idmclone-link", linkUrl: "http://intranet/a.iso" }, { url: "https://x.com/" });
  d = t.posts("download");
  assert.equal(d[0].browserProxy, "10.1.1.1:3128");
  assert.ok(!("browserProxy" in d[1]), "<local> va directo");

  t = loadBackground({ hostReply: () => ({ ok: true }), proxySettings: { mode: "fixed_servers", rules: { singleProxy: { scheme: "socks5", host: "10.1.1.1", port: 1080 } } } });
  await t.L.menuClicked({ menuItemId: "idmclone-link", linkUrl: "https://x.com/a.iso" }, { url: "https://x.com/" });
  assert.equal(t.posts("download")[0].browserProxy, "socks5://10.1.1.1:1080");
  t = loadBackground({ hostReply: () => ({ ok: true }), proxySettings: { mode: "fixed_servers", rules: { singleProxy: { scheme: "https", host: "10.1.1.1", port: 443 } } } });
  await t.L.menuClicked({ menuItemId: "idmclone-link", linkUrl: "https://x.com/a.iso" }, { url: "https://x.com/" });
  assert.ok(!("browserProxy" in t.posts("download")[0]), "Proxy con TLS: no");

  t = loadBackground({ hostReply: () => ({ ok: true }) }); // Chromium sin permiso "proxy": no se sabe
  await t.L.menuClicked({ menuItemId: "idmclone-link", linkUrl: "https://x.com/a.iso" }, { url: "https://x.com/" });
  assert.ok(!("browserProxy" in t.posts("download")[0]));
});

test("opción 3: #18 IDMClone pide las cookies de una dirección -> la extensión contesta solo esas", async () => {
  const t = loadBackground({ appConfig: CFG14, hostReply: () => ({ ok: true }), tabs: [{ id: 1, url: "https://x.com/inicio", incognito: false }],
    cookies: (q) => (q.url === "https://x.com/privado/" && !q.storeId ? [{ name: "sid", value: "ok", httpOnly: true }, { name: "tema", value: "oscuro" }] : []) });
  await t.bg.getAppConfig(); // abre el canal nativo
  t.push({ type: "getcookies", req: 7, url: "https://x.com/privado/" });
  await wait(40);
  const c = t.posts("cookies");
  assert.equal(c.length, 1);
  assert.equal(c[0].req, 7);
  assert.equal(c[0].cookies, "sid=ok; tema=oscuro");
  assert.equal(c[0].url, "https://x.com/privado/");
  assert.ok(typeof c[0].rid === "number", "La respuesta es una petición normal con rid");
  assert.ok(t.calls.filter((x) => x[0] === "cookies").every((x) => x[1].url === "https://x.com/privado/"), "Solo se leen las de esa dirección");
  // Direcciones que no son http(s), "req" no válido, o un mensaje con rid (respuesta a otra cosa): no se contesta.
  t.push({ type: "getcookies", req: 8, url: "javascript:alert(1)" });
  t.push({ type: "getcookies", req: "x", url: "https://x.com/" });
  t.push({ type: "getcookies", rid: 99, req: 9, url: "https://x.com/" });
  await wait(40);
  assert.equal(t.posts("cookies").length, 1);
});

test("opción 3: #18 sin cookies en el almacén normal se miran los contenedores, nunca el privado", async () => {
  const t = loadBackground({ appConfig: CFG14, hostReply: () => ({ ok: true }),
    cookies: (q) => (q.storeId === "firefox-container-2" ? [{ name: "c", value: "1" }] : q.storeId === "firefox-private" ? [{ name: "priv", value: "x" }] : []) });
  globalThis.chrome.cookies.getAllCookieStores = async () => [{ id: "firefox-default" }, { id: "firefox-private", incognito: true }, { id: "firefox-container-2" }];
  assert.equal(await t.bg.cookiesForUrl("https://x.com/"), "c=1");
  assert.ok(!t.calls.some((x) => x[0] === "cookies" && x[1].storeId === "firefox-private"));
});

test("opción 3: #11 lista de sitios (con https://, ruta, puerto, subdominios, comodines y acentos) y textos difíciles", () => {
  const sp = (sites) => ({ mode: 2, sites });
  assert.equal(L.selectionPanelAllowed("https://mega.nz/file/x", sp(["https://mega.nz/"])), true);
  assert.equal(L.selectionPanelAllowed("https://mega.nz:8443/x", sp(["mega.nz"])), true);
  assert.equal(L.selectionPanelAllowed("https://dl.mega.nz/x", sp(["mega.nz"])), true);
  assert.equal(L.selectionPanelAllowed("https://notmega.nz/x", sp(["mega.nz"])), false);
  assert.equal(L.selectionPanelAllowed("https://mega.nz/x", sp(["*.mega.nz"])), true);
  assert.equal(L.selectionPanelAllowed("https://xn--and-6ma2c.com/x", sp(["ñandú.com"])), true);
  assert.deepEqual(L.textUrls("下载https://a.com/x.zip。谢谢 y https://b.com/y.pdf…"), ["https://a.com/x.zip", "https://b.com/y.pdf"]);
  const t0 = Date.now();
  assert.deepEqual(L.textUrls("https://a.com/" + ")".repeat(200000)), ["https://a.com/"]);
  assert.ok(Date.now() - t0 < 1000, "textUrls debe ser lineal: " + (Date.now() - t0) + " ms");
  assert.equal(L.sanitizeLinks(["https://a.com/\nx", "https://a.com/\u0000x"]).length, 0);
});

test("opción 3: #18 cookies solo para un sitio abierto en una pestaña o abierto hace poco (y su gemelo www); nunca incógnito", async () => {
  const session = {};
  const t = loadBackground({ appConfig: CFG14, hostReply: () => ({ ok: true }), session,
    tabs: [{ id: 1, url: "https://www.abierto.com/cuenta", incognito: false }, { id: 2, url: "https://privado.com/", incognito: true }],
    cookies: (q) => [{ name: "sid", value: "1", httpOnly: true }] });
  await t.bg.getAppConfig(); // abre el canal nativo
  const ask = async (req, url) => { t.push({ type: "getcookies", req, url }); await wait(40); return t.posts("cookies").find((c) => c.req === req); };
  const denied = (c, why) => { assert.ok(c, why); assert.equal(c.cookies, "", why); assert.equal(c.ok, false, why); assert.equal(c.reason, "notopen", why); };
  const given = (c, why) => { assert.ok(c, why); assert.equal(c.cookies, "sid=1", why); assert.equal(c.ok, true, why); assert.ok(!("reason" in c), why); };

  given(await ask(1, "https://www.abierto.com/x"), "Pestaña abierta en ese servidor");
  given(await ask(2, "https://abierto.com/x"), "Gemelo sin www de una pestaña abierta");
  denied(await ask(3, "https://otro.com/"), "Sitio que no está abierto");
  denied(await ask(4, "https://sub.abierto.com/"), "Un subdominio no es el mismo sitio");
  denied(await ask(5, "https://privado.com/"), "Solo abierto en incógnito");
  denied(await ask(6, "https://abierto.com.evil.net/"), "Un sufijo no cuenta");
  assert.ok(!t.calls.some((c) => c[0] === "cookies" && /otro\.com|privado\.com|sub\.abierto|evil/.test(c[1].url)), "Ni se leen las cookies de un sitio denegado");

  // Navegación reciente en una pestaña normal (ya cerrada): vale durante 15 minutos, también para su gemelo con www.
  t.L.tabUpdated(7, { url: "https://reciente.com/login" }, { id: 7, url: "https://reciente.com/login", incognito: false });
  given(await ask(7, "https://www.reciente.com/inicio"), "Abierto hace poco (gemelo www)");
  // Navegación en incógnito: no cuenta.
  t.L.tabUpdated(8, { url: "https://secreto.com/" }, { id: 8, url: "https://secreto.com/", incognito: true });
  denied(await ask(8, "https://secreto.com/"), "Navegación de incógnito");
  // Pasados 15 minutos, ya no.
  t.bg.recentHosts.set("viejo.com", Date.now() - 16 * 60 * 1000);
  t.bg.recentHosts.set("casi.com", Date.now() - 14 * 60 * 1000);
  denied(await ask(9, "https://viejo.com/"), "Navegación de hace más de 15 minutos");
  given(await ask(10, "https://casi.com/"), "Navegación de hace 14 minutos");

  // El service worker se duerme: la lista reciente sigue en storage.session (nunca en storage.local).
  assert.ok(JSON.stringify(session.recentHosts).includes("reciente.com"));
  assert.ok(!JSON.stringify(session.recentHosts).includes("secreto.com"));
  assert.ok(!("recentHosts" in t.store), "No se guarda en disco");
  const t2 = loadBackground({ appConfig: CFG14, hostReply: () => ({ ok: true }), session, tabs: [], cookies: () => [{ name: "sid", value: "1" }] });
  assert.equal(await t2.bg.cookieHostAllowed("https://reciente.com/"), true);
  assert.equal(await t2.bg.cookieHostAllowed("https://abierto.com/"), false, "Ya no hay pestaña abierta");
  assert.equal(await t2.bg.cookieHostAllowed("javascript:alert(1)"), false);
});

// ------------------------------------------------------------------ content.js con un DOM simulado

function fakeEl(tag) {
  const el = {
    tagName: String(tag).toUpperCase(), nodeType: 1, children: [], listeners: {}, style: {}, attrs: {}, hidden: false, className: "", textContent: "",
    parentNode: null, offsetWidth: 180, href: "",
    addEventListener(type, f) { (this.listeners[type] = this.listeners[type] || []).push(f); },
    setAttribute(k, v) { this.attrs[k] = v; },
    appendChild(c) { if (c.parentNode) c.remove(); c.parentNode = this; this.children.push(c); return c; },
    remove() { const p = this.parentNode; if (p) { p.children.splice(p.children.indexOf(this), 1); this.parentNode = null; } },
    focus() { }, contains() { return false; }, closest() { return null; },
    getBoundingClientRect: () => ({ top: 10, left: 10, right: 300, bottom: 40, width: 290, height: 30 }),
    getClientRects: () => [],
    querySelectorAll(sel) { return this.all().filter((e) => matches(e, sel)); },
    querySelector(sel) { return this.querySelectorAll(sel)[0] || null; },
    all() { const out = []; for (const c of this.children) { out.push(c); out.push(...c.all()); } return out; },
    attachShadow(o) { this.shadowMode = o.mode; this._root = fakeEl("#shadow-root"); return this._root; },
    get shadowRoot() { return this.shadowMode === "open" ? this._root : null; },
    get innerHTML() { return ""; },
    // Basta con crear los elementos con clase (y "hidden") del HTML propio del panel.
    set innerHTML(html) {
      for (const c of this.children.slice()) c.remove();
      for (const m of String(html).matchAll(/<(\w+)([^>]*)>/g)) {
        const cls = /class="([^"]*)"/.exec(m[2]);
        if (!cls) continue;
        const e = fakeEl(m[1]); e.className = cls[1]; e.hidden = /\shidden(\s|$|>)/.test(m[2] + ">");
        this.appendChild(e);
      }
    }
  };
  return el;
}
function matches(e, sel) {
  if (sel === "a[href]") return e.tagName === "A" && !!e.href;
  return sel.startsWith(".") ? e.className.split(/\s+/).includes(sel.substring(1)) : e.tagName === sel.toUpperCase();
}
function fire(el, type, isTrusted, extra) {
  const ev = Object.assign({ type, isTrusted, target: el, stopPropagation() { }, preventDefault() { }, stopImmediatePropagation() { }, composedPath: () => [el] }, extra || {});
  for (const f of (el.listeners[type] || []).slice()) f(ev);
  return ev;
}

function loadContent(appCfg, i18n, pageMedia) {
  const vm = require("node:vm");
  const sent = [], win = { listeners: {} }, timers = new Set();
  let onMsg = null;
  const docEl = fakeEl("html"), body = fakeEl("body");
  docEl.appendChild(body);
  const created = [];
  const document = {
    documentElement: docEl, body, fullscreenElement: null, activeElement: body,
    createElement: (tag) => { const e = fakeEl(tag); created.push(e); return e; },
    querySelectorAll: (sel) => (sel === "video, audio" ? (pageMedia || []) : []), addEventListener() { }
  };
  const api = {
    runtime: { id: "mi-extension", onMessage: { addListener: (f) => { onMsg = f; } }, sendMessage: (m) => { sent.push(m); return Promise.resolve({ ok: true }); } },
    storage: { local: { get: async () => ({ appCfg }) }, onChanged: { addListener() { } } },
    i18n: i18n || I18N_ES
  };
  // logic.js (IDMLogic) busca chrome.i18n en el global de Node: el de esta página.
  globalThis.chrome = api;
  const ctx = {
    chrome: api, document, location: { protocol: "https:", href: "https://x.com/pagina" }, IDMLogic: L,
    scrollX: 0, scrollY: 0, innerWidth: 1200, innerHeight: 800, alert() { }, getComputedStyle: () => ({ visibility: "visible", display: "block" }),
    addEventListener: (type, f) => { (win.listeners[type] = win.listeners[type] || []).push(f); },
    setTimeout: (f, ms) => { const h = setTimeout(() => { timers.delete(h); f(); }, ms); timers.add(h); return h; },
    clearTimeout: (h) => { clearTimeout(h); timers.delete(h); },
    setInterval: (f, ms) => { const h = setInterval(f, ms); timers.add(h); return h; }, clearInterval: (h) => { clearInterval(h); timers.delete(h); },
    getSelection: () => null
  };
  ctx.self = ctx; ctx.window = ctx; ctx.top = ctx;
  vm.createContext(ctx);
  vm.runInContext(require("node:fs").readFileSync(path.join(EXT, "content.js"), "utf8"), ctx);
  return {
    sent, ctx, created, media: (entry) => onMsg({ type: "idm-media", entry }),
    winFire: (type, ev) => { for (const f of (win.listeners[type] || []).slice()) f(ev); },
    stop: () => { for (const h of timers) { clearTimeout(h); clearInterval(h); } timers.clear(); }
  };
}

test("opción 3: panel de vídeo en un Shadow DOM cerrado; un clic simulado por la página no envía nada, uno de verdad sí", async () => {
  const c = loadContent({ mediaMini: false });
  try {
    await wait(10);
    c.media({ url: "https://v.com/a.mp4", media: "video", items: [{ kind: "video", ext: ".mp4", size: 5000000 }] });
    const host = c.created.find((e) => e.tagName === "IDMCLONE-PANEL");
    assert.ok(host, "Aparece el panel");
    assert.equal(host.shadowMode, "closed");
    assert.equal(host.shadowRoot, null, "La página no llega a los botones");
    const root = host._root; // referencia privada del script (la prueba la mira por dentro)
    const main = root.querySelector(".main");
    assert.ok(main && main.listeners.click, "Botón principal con su clic");
    fire(main, "click", false);
    assert.equal(c.sent.length, 0, "Clic sintético (isTrusted=false): nada");
    fire(root.querySelector(".x"), "click", false);
    assert.ok(host.parentNode, "Un clic sintético tampoco lo cierra");
    fire(main, "click", true);
    assert.equal(c.sent.length, 0, "Un clic de verdad en el primer medio segundo no cuenta (panel puesto bajo el cursor)");
    await wait(550);
    fire(main, "click", true);
    assert.equal(c.sent.length, 1, "Clic del usuario: se envía");
    assert.equal(c.sent[0].type, "media-download");
    assert.equal(c.sent[0].entry.url, "https://v.com/a.mp4");

    // Varias calidades: el botón abre la lista; los elementos de la lista también exigen un clic de verdad.
    c.media({ url: "https://v.com/b.m3u8", media: "video", items: [{ kind: "video", ext: ".mp4", width: 1280, height: 720 }, { kind: "video", ext: ".mp4", width: 640, height: 360 }] });
    const main2 = root.querySelector(".main");
    const menu = root.querySelector(".menu");
    fire(main2, "click", false);
    assert.equal(menu.hidden, true, "Un clic sintético no abre la lista");
    fire(main2, "click", true);
    assert.equal(menu.hidden, false, "Un clic de verdad la abre");
    const items = menu.querySelectorAll(".item").filter((b) => b.attrs.role === "menuitem");
    assert.ok(items.length >= 2);
    for (const b of items) fire(b, "click", false);
    assert.equal(c.sent.length, 1, "Elegir una calidad con un clic sintético: nada");
    fire(items[0], "click", true);
    assert.equal(c.sent.length, 2);
    assert.equal(c.sent[1].type, "media-download");
  } finally { c.stop(); }
});

test("opción 3: panel de selección y tecla de forzar: solo los actos del usuario (isTrusted) cuentan", async () => {
  const c = loadContent({ selectionPanel: { mode: 0, mini: false, sites: [] }, preventKeys: "del", forceKeys: "ins" });
  try {
    await wait(10);
    const a = fakeEl("a"); a.href = "https://x.com/archivo.zip"; a.textContent = "Archivo";
    const base = fakeEl("div"); base.appendChild(a);
    const range = { commonAncestorContainer: base, getBoundingClientRect: () => ({ top: 0, left: 0, right: 100, bottom: 20 }), getClientRects: () => [] };
    c.ctx.getSelection = () => ({ isCollapsed: false, rangeCount: 1, anchorNode: base, focusNode: base, getRangeAt: () => range, containsNode: () => true, toString: () => "Archivo" });
    const panel = () => c.created.find((e) => e.tagName === "IDMCLONE-SELECTION-PANEL");
    c.winFire("mouseup", { isTrusted: false, composedPath: () => [] });
    await wait(260);
    assert.equal(panel(), undefined, "Un mouseup sintético no saca el panel");
    c.winFire("mouseup", { isTrusted: true, composedPath: () => [] });
    await wait(260);
    const host = panel();
    assert.ok(host && host.parentNode, "Tras soltar el ratón de verdad, el panel aparece");
    assert.equal(host.shadowMode, "closed");
    const main = host._root.querySelector(".main");
    fire(main, "click", false);
    assert.equal(c.sent.length, 0, "Clic sintético en el panel: nada");
    fire(main, "click", true);
    assert.equal(c.sent.length, 0, "Un clic de verdad en el primer medio segundo no cuenta");
    await wait(550); // un clic en el primer medio segundo tras aparecer no cuenta
    fire(main, "click", true);
    assert.equal(c.sent.length, 1);
    assert.equal(c.sent[0].type, "selection-links");
    assert.equal(c.sent[0].links[0].url, "https://x.com/archivo.zip");

    // Tecla "forzar la descarga" (Insert): ni una tecla ni un clic simulados por la página la disparan.
    const link = { closest: () => ({ href: "https://x.com/cosa.bin" }) };
    let blocked = 0;
    const click = (isTrusted) => c.winFire("click", { isTrusted, target: link, altKey: false, ctrlKey: false, shiftKey: false, preventDefault() { blocked++; }, stopImmediatePropagation() { blocked++; } });
    c.winFire("keydown", { type: "keydown", isTrusted: false, key: "Insert" });
    click(true);
    assert.ok(!c.sent.some((m) => m.type === "idm-keys"), "Una tecla sintética no se avisa");
    c.winFire("keydown", { type: "keydown", isTrusted: true, key: "Insert" });
    click(false);
    assert.ok(!c.sent.some((m) => m.type === "force-mark"), "Tecla o clic sintéticos: no se fuerza nada");
    click(true);
    assert.equal(c.sent.filter((m) => m.type === "force-mark").length, 1, "Tecla y clic de verdad: se marca el enlace");
    assert.equal(c.sent.find((m) => m.type === "force-mark").url, "https://x.com/cosa.bin");
    assert.equal(blocked, 0, "El clic sigue su curso (la descarga sale del navegador con su POST y sus redirecciones)");
  } finally { c.stop(); }
});

// ------------------------------------------------------------------ comparación con la extensión de IDM 6.43 (Sandbox)

test("IDM: título limpio para el nombre del vídeo ('(3) ' delante y ' - Sitio' detrás)", () => {
  assert.equal(L.cleanTitle("(3) Mi vídeo - Dailymotion", "www.dailymotion.com"), "Mi vídeo");
  assert.equal(L.cleanTitle("Concierto | Vimeo", "vimeo.com"), "Concierto");
  assert.equal(L.cleanTitle("Noticias de hoy - BBC News", "www.bbc.co.uk"), "Noticias de hoy");
  assert.equal(L.cleanTitle("Capítulo 1 - El comienzo", "series.ejemplo.com"), "Capítulo 1 - El comienzo", "El final no es el sitio: se queda");
  assert.equal(L.cleanTitle("Prueba de integracion", "127.0.0.1:18766"), "Prueba de integracion");
  assert.equal(L.cleanTitle("", "x.com"), "");
  assert.equal(L.cleanTitle("Receta de tacos - Comida Mexicana", "www.recetas.com.mx"), "Receta de tacos - Comida Mexicana", "'com' no es el sitio");
  assert.equal(L.cleanTitle("Gol de Messi - Noticias", "noticias.gob.ar"), "Gol de Messi", "'noticias' sí es el sitio");
  assert.equal(L.cleanTitle("Gol - Gobierno", "prensa.gob.ar"), "Gol - Gobierno");
});

test("IDM: el nombre del vídeo usa el título limpio", async () => {
  const t = loadBackground({ hostReply: () => ({ ok: true }), tab: { url: "https://www.ejemplo.com/v/1", title: "(2) Gatos - Ejemplo" } });
  await t.bg.downloadMedia({ kind: "hls", url: "https://cdn.ejemplo.com/m.m3u8" }, { ext: ".mp4" }, { url: "https://www.ejemplo.com/v/1", title: "(2) Gatos - Ejemplo" });
  assert.equal(t.posts("download")[0].filename, "Gatos.mp4");
});

test("IDM: 'Descargar con IDMClone' sobre la página o un marco descarga esa dirección", async () => {
  const t = loadBackground({ hostReply: () => ({ ok: true }) });
  await t.L.menuClicked({ menuItemId: "idmclone-page", pageUrl: "https://x.com/doc.pdf" }, { url: "https://x.com/doc.pdf" });
  await t.L.menuClicked({ menuItemId: "idmclone-page", pageUrl: "https://x.com/", frameUrl: "https://y.com/marco.pdf" }, { url: "https://x.com/" });
  assert.deepEqual(t.posts("download").map((m) => m.url), ["https://x.com/doc.pdf", "https://y.com/marco.pdf"]);
});

test("IDM: navegación sin recarga (pushState) vacía el panel; un cambio de '#' no", async () => {
  const t = loadBackground({});
  t.bg.tabMedia.set(5, { items: new Map([["a", {}]]), children: new Set() });
  t.L.tabUpdated(5, { url: "https://x.com/v/1", status: "loading" }, { id: 5, incognito: false });
  t.bg.tabMedia.set(5, { items: new Map([["a", {}]]), children: new Set() });
  t.L.tabUpdated(5, { url: "https://x.com/v/1#t=10" }, { id: 5, incognito: false });
  assert.ok(t.bg.tabMedia.has(5), "Solo cambió el '#'");
  t.L.tabUpdated(5, { url: "https://x.com/v/2" }, { id: 5, incognito: false });
  assert.ok(!t.bg.tabMedia.has(5));
  assert.ok(t.calls.some((c) => c[0] === "tabMsg" && c[1] === 5 && c[2].type === "idm-media-reset"));
});

test("IDM: al instalar pone content.js en las pestañas abiertas que no lo tienen (Chromium)", async () => {
  const t = loadBackground({ tabs: [{ id: 1, url: "https://a.com/" }, { id: 2, url: "https://b.com/" }, { id: 3, url: "https://c.com/", discarded: true }] });
  const done = [];
  globalThis.chrome.scripting = { executeScript: async (o) => {
    // La 2 ya lo tiene funcionando en todos sus marcos; la 1, solo en un iframe cargado después (marco 7).
    if (o.func) return o.target.tabId === 2 ? [{ frameId: 0, result: true }] : [{ frameId: 0, result: false }, { frameId: 7, result: true }];
    done.push([o.target.tabId, o.files.join(","), o.target.frameIds]); return [{}];
  } };
  t.L.onInstalled(); await wait(30);
  assert.deepEqual(done, [[1, "logic.js,content.js", [0]]], "Solo donde falta: nunca dos copias en un marco");
  const f = loadBackground({ firefox: true, tabs: [{ id: 1, url: "https://a.com/" }] });
  let used = false;
  globalThis.browser.scripting = { executeScript: async () => { used = true; return [{}]; } };
  f.L.onInstalled(); await wait(20);
  assert.ok(!used, "Firefox ya lo hace solo");
});

test("IDM: icono con 'X' y [ERROR] sin host; [DESACTIVADA] con la captura desactivada; limpio si responde", async () => {
  const mkAction = () => { const a = { badge: null, title: null };
    return { a, api: { setBadgeText: async (o) => { a.badge = o.text; }, setBadgeBackgroundColor: async () => {}, setTitle: async (o) => { a.title = o.title; } } }; };
  const t = loadBackground({ noHost: true });
  const m = mkAction(); globalThis.chrome.action = m.api;
  await t.bg.hostRequest({ type: "ping" }, 100); await wait(10);
  assert.equal(m.a.badge, "X");
  assert.match(m.a.title, /\[ERROR\]/);

  const ok = loadBackground({ appConfig: CFG14 });
  const m2 = mkAction(); globalThis.chrome.action = m2.api;
  await ok.bg.fetchAppConfig(); await wait(10);
  assert.equal(m2.a.badge, "");
  assert.equal(m2.a.title, "IDMClone Integration");

  const off = loadBackground({ appConfig: CFG14, settings: { enabled: false } });
  const m3 = mkAction(); globalThis.chrome.action = m3.api;
  await off.bg.fetchAppConfig(); await wait(10);
  assert.equal(m3.a.badge, "X");
  assert.match(m3.a.title, /\[DESACTIVADA\]/);
});
test("IDM: un PDF o un vídeo que el navegador abriría él mismo va a IDMClone (Chromium vuelve atrás)", async () => {
  const t = loadBackground({ appConfig: Object.assign({}, CFG14, { exts: ["pdf", "mp4", "zip"] }), hostReply: () => ({ ok: true }),
                             tab: { url: "https://x.com/doc.pdf", title: "doc.pdf" } });
  const hdr = (ct, extra) => ({ type: "main_frame", tabId: 4, statusCode: 200, method: "GET", url: "https://x.com/doc.pdf",
                                responseHeaders: [{ name: "Content-Type", value: ct }].concat(extra || []) });
  assert.equal(await t.bg.considerInline(hdr("text/html")), null, "Una página: nada");
  assert.equal(await t.bg.considerInline(hdr("application/pdf", [{ name: "Content-Disposition", value: "attachment" }])), null, "attachment: va por la API de descargas");
  assert.equal(await t.bg.considerInline(Object.assign(hdr("application/pdf"), { type: "sub_frame" })), null, "PDF incrustado en un marco: nada");
  const x = await t.bg.considerInline(hdr("application/pdf"));
  assert.ok(x, "PDF de la lista: a IDMClone");
  assert.equal(t.posts("download").length, 1);
  assert.equal(t.posts("download")[0].auto, true);
  assert.equal(await t.bg.considerInline(hdr("application/pdf")), null, "La misma respuesta no se manda dos veces");
  // Si además el navegador crea la descarga (visor de PDF desactivado), se cancela sin volver a mandarla.
  const dec = await t.bg.consider({ id: 90, url: "https://x.com/doc.pdf", filename: "doc.pdf", mime: "application/pdf", state: "in_progress" });
  assert.equal(dec.sent, true);
  assert.equal(t.posts("download").length, 1);
  assert.ok(t.names().includes("cancel"));
  // Chromium: cuando la pestaña ya lo muestra, vuelve atrás; sin historial, se cierra.
  const acts = [];
  globalThis.chrome.tabs.goBack = async (id) => { acts.push("back"); throw new Error("sin historial"); };
  globalThis.chrome.tabs.remove = async (id) => { acts.push("remove"); };
  await t.bg.leaveInline({ tabId: 4, url: "https://x.com/doc.pdf" });
  assert.deepEqual(acts, ["back", "remove"]);
});

test("IDM: un PDF que no está en la lista, o con la tecla para no descargar, se queda en el navegador", async () => {
  const t = loadBackground({ appConfig: Object.assign({}, CFG14, { exts: ["zip"] }), hostReply: () => ({ ok: true }) });
  const d = { type: "main_frame", tabId: 4, statusCode: 200, method: "GET", url: "https://x.com/doc.pdf", responseHeaders: [{ name: "Content-Type", value: "application/pdf" }] };
  assert.equal(await t.bg.considerInline(d), null);
  const t2 = loadBackground({ appConfig: Object.assign({}, CFG14, { exts: ["pdf"] }), hostReply: () => ({ ok: false, error: "no" }) });
  assert.equal(await t2.bg.considerInline(d), null, "IDMClone no la acepta: sigue en el navegador");
  assert.equal(await t2.bg.considerInline(d), null);
  assert.equal(t2.posts("download").length, 2, "Rechazada: se puede volver a intentar");
});
test("IDM: Firefox también corta un .pdf servido como octet-stream (lo abre en su visor); Chromium lo deja a la API de descargas", async () => {
  const d = { type: "main_frame", tabId: 4, statusCode: 200, method: "GET", url: "https://x.com/f/documento.pdf?id=3",
              responseHeaders: [{ name: "Content-Type", value: "application/octet-stream" }] };
  const f = loadBackground({ firefox: true, appConfig: Object.assign({}, CFG14, { exts: ["pdf"] }), hostReply: () => ({ ok: true }) });
  assert.ok(await f.bg.considerInline(d));
  const c = loadBackground({ appConfig: Object.assign({}, CFG14, { exts: ["pdf"] }), hostReply: () => ({ ok: true }) });
  assert.equal(await c.bg.considerInline(d), null);
});
test("IDM (revisión): POST sin cuerpo guardado, '#...' y rechazo de IDMClone no pierden nada", async () => {
  const base = { type: "main_frame", tabId: 4, statusCode: 200, responseHeaders: [{ name: "Content-Type", value: "application/pdf" }] };
  const t = loadBackground({ appConfig: Object.assign({}, CFG14, { exts: ["pdf"] }), hostReply: () => ({ ok: true }) });
  assert.equal(await t.bg.considerInline(Object.assign({ method: "POST", url: "https://x.com/informe.pdf" }, base)), null, "POST sin cuerpo: se queda");
  // Chromium da la URL sin '#page=3' y la pestaña con él: aun así vuelve atrás.
  const acts = [];
  globalThis.chrome.tabs.get = async () => ({ id: 4, url: "https://x.com/doc.pdf#page=3" });
  globalThis.chrome.tabs.goBack = async () => { acts.push("back"); };
  await t.bg.leaveInline({ tabId: 4, url: "https://x.com/doc.pdf" });
  assert.deepEqual(acts, ["back"]);
  // IDMClone no acepta la del visor; si el navegador además la descarga, va por el camino normal (no se borra).
  let n = 0;
  const r = loadBackground({ appConfig: Object.assign({}, CFG14, { exts: ["pdf"] }), hostReply: (m) => (m.type === "download" ? (++n === 1 ? { ok: false, error: "no" } : { ok: true }) : { ok: true }) });
  assert.equal(await r.bg.considerInline(Object.assign({ method: "GET", url: "https://x.com/b.pdf" }, base)), null);
  const dec = await r.bg.consider({ id: 91, url: "https://x.com/b.pdf", filename: "b.pdf", mime: "application/pdf", state: "in_progress" });
  assert.equal(dec.sent, true);
  assert.equal(r.posts("download").length, 2, "La descarga del navegador se mandó de verdad");
});
test("IDM: las excepciones que añade 'Cancelando una descarga iniciada automáticamente' se cumplen (sitio sin puerto, patrón con puerto)", () => {
  assert.ok(L.urlExcluded("http://127.0.0.1:18765/f/range/a.zip", ["127.0.0.1"]), "Sitio sin puerto: cualquier puerto");
  assert.ok(L.urlExcluded("http://127.0.0.1:18765/f/range/a.zip", ["http://127.0.0.1:18765/f/range/*"]));
  assert.ok(!L.urlExcluded("http://127.0.0.1:18765/otra/a.zip", ["http://127.0.0.1:18765/f/range/*"]), "Otra carpeta: no");
  assert.ok(!L.urlExcluded("http://127.0.0.1:9000/f/range/a.zip", ["http://127.0.0.1:18765/f/range/*"]), "Otro puerto: no");
  assert.ok(L.urlExcluded("https://dl.sitio.com/x.zip", ["sitio.com"]), "Subdominios, como antes");
});

// ------------------------------------------------------------------ subtítulos (como IDM)

test("subtítulos: archivos .vtt/.srt/.ttml sueltos, idioma, texto del menú y nombre del archivo", () => {
  const vtt = L.classifySubtitle({ url: "https://cdn.a.com/subs/peli.es.vtt?t=1#x", type: "other", statusCode: 200, contentType: "text/vtt" });
  assert.deepEqual(vtt, { kind: "subtitle", format: "vtt", ext: ".vtt", language: "es", url: "https://cdn.a.com/subs/peli.es.vtt?t=1" });
  assert.equal(L.classifySubtitle({ url: "https://a.com/get?id=4", type: "xmlhttprequest", statusCode: 200, contentType: "text/vtt; charset=utf-8" }).format, "vtt");
  assert.equal(L.classifySubtitle({ url: "https://a.com/s/en_US.srt", type: "xmlhttprequest", statusCode: 200, contentType: "application/octet-stream" }).language, "en-US");
  const ttml = L.classifySubtitle({ url: "https://a.com/s/sub.dfxp", type: "xmlhttprequest", statusCode: 200, contentType: "application/ttml+xml" });
  assert.equal(ttml.format, "ttml"); assert.equal(ttml.ext, ".srt", "TTML: IDMClone lo pasa a SRT");
  assert.equal(L.classifySubtitle({ url: "https://a.com/s/x.vtt", type: "main_frame", statusCode: 200, contentType: "text/vtt" }), null, "Una pestaña no es un subtítulo del reproductor");
  assert.equal(L.classifySubtitle({ url: "https://a.com/s/x.vtt", type: "other", statusCode: 404, contentType: "text/html" }), null);
  assert.equal(L.classifySubtitle({ url: "https://a.com/s/x.vtt", type: "other", statusCode: 200, contentType: "text/html" }), null, "Una página de error con nombre .vtt");
  assert.equal(L.classifySubtitle({ url: "blob:https://a.com/1", type: "other", statusCode: 200, contentType: "text/vtt" }), null);
  assert.equal(L.classifySubtitle({ url: "https://a.com/v.mp4", type: "media", statusCode: 200, contentType: "video/mp4" }), null);
  assert.equal(L.subtitleLang("https://a.com/x.vtt?lang=pt_BR"), "pt-BR");
  assert.equal(L.subtitleLang("https://a.com/capitulo-1.vtt"), "", "'-1' no es un idioma");
  assert.equal(L.subtitleLang("https://a.com/the.end.vtt"), "", "'end' no es un idioma conocido");
  assert.equal(L.mediaLabel({ kind: "subtitle", language: "es", ext: ".vtt" }), "Subtítulos: Español (vtt)");
  assert.equal(L.mediaLabel({ kind: "subtitle", language: "en", name: "English (CC)", ext: ".srt" }), "Subtítulos: English (CC) (srt)");
  assert.equal(L.mediaLabel({ kind: "subtitle", language: "", ext: ".vtt" }), "Subtítulos: sin idioma (vtt)");
  assert.equal(L.mediaLabel({ kind: "subtitle", language: "x-klingon", ext: ".vtt" }), "Subtítulos: x-klingon (vtt)");
  assert.equal(L.subtitleFileName('Mi vídeo: "prueba"', { language: "es", ext: ".vtt" }), "Mi vídeo prueba.es.vtt");
  assert.equal(L.subtitleFileName("", { language: "../en", ext: ".srt" }), "video.en.srt", "El idioma no puede meter rutas");
  assert.equal(L.subtitleFileName("Clip", { ext: ".vtt" }), "Clip.vtt");
});

const PROBE_SUBS = Object.assign({}, PROBE, {
  children: PROBE.children.concat(["https://cdn.ejemplo.com/v/subs/es.m3u8"]),
  items: PROBE.items.concat([{ kind: "subtitle", subtitle: "https://cdn.ejemplo.com/v/subs/es.m3u8", language: "es", name: "Español", ext: ".vtt", size: -1 },
                             { kind: "subtitle", subtitle: "https://cdn.ejemplo.com/v/subs/en.m3u8", language: "en", name: null, ext: ".vtt", size: -1 }]) });

test("subtítulos: los de una lista HLS van al panel tras las calidades (aunque los tipos del panel filtren) y no solos", async () => {
  const types = [{ ext: "mp4", minKB: 0, on: true }];
  let t = loadBackground({ appConfig: () => Object.assign({}, CFG14, { mediaTypes: types }), hostReply: (m) => (m.type === "probe" ? PROBE_SUBS : { ok: true }) });
  t.L.headers(hdr("https://cdn.ejemplo.com/v/master.m3u8", "xmlhttprequest", "application/vnd.apple.mpegurl"));
  await wait(60);
  // La lista de los subtítulos y sus trozos .vtt que pide luego el reproductor: nada nuevo.
  t.L.headers(hdr("https://cdn.ejemplo.com/v/subs/es.m3u8", "xmlhttprequest", "application/vnd.apple.mpegurl"));
  t.L.headers(hdr("https://cdn.ejemplo.com/v/subs/es/seg1.vtt", "xmlhttprequest", "text/vtt"));
  await wait(60);
  const e = tabMsgs(t);
  assert.equal(e.length, 1);
  assert.deepEqual(e[0].items.map((i) => i.kind), ["video", "video", "subtitle", "subtitle"]);
  assert.equal(e[0].media, "video");
  assert.equal(t.posts("probe").length, 1);
  // Una lista que solo tiene subtítulos (la de los subtítulos, leída antes que la maestra): sin panel.
  const only = Object.assign({}, PROBE_SUBS, { children: [], items: PROBE_SUBS.items.filter((i) => i.kind === "subtitle") });
  t = loadBackground({ appConfig: CFG14, hostReply: (m) => (m.type === "probe" ? only : { ok: true }) });
  t.L.headers(hdr("https://cdn.ejemplo.com/v/subs/en.m3u8", "xmlhttprequest", "application/vnd.apple.mpegurl"));
  await wait(60);
  assert.equal(tabMsgs(t).length, 0);
});

test("subtítulos: elegir los de una lista manda la lista, la pista y 'Título.es.vtt' (mediaKind subs)", async () => {
  const t = loadBackground({ hostReply: () => ({ ok: true }) });
  const entry = { kind: "hls", url: "https://cdn.ejemplo.com/v/master.m3u8", items: PROBE_SUBS.items };
  let reply = null;
  t.L.onMessage({ type: "media-download", entry, item: PROBE_SUBS.items[2] }, { tab: { id: 4, url: "https://videos.ejemplo.com/ver/123", title: "Mi vídeo: «prueba»" } }, (r) => { reply = r; });
  await wait(60);
  const d = t.posts("download")[0];
  assert.equal(d.url, "https://cdn.ejemplo.com/v/master.m3u8");
  assert.equal(d.mediaKind, "subs");
  assert.equal(d.mediaVideo, "https://cdn.ejemplo.com/v/subs/es.m3u8");
  assert.equal(d.filename, "Mi vídeo «prueba».es.vtt", "Mismo nombre que el vídeo, con el idioma");
  assert.equal(d.referrer, "https://videos.ejemplo.com/ver/123");
  assert.equal(d.cookies, "sesion=abc");
  assert.ok(reply && reply.ok);
});

test("subtítulos: .vtt suelto que pide el reproductor -> junto al vídeo (espera si aún no hay vídeo); con lista HLS, no", async () => {
  const video = (url) => big(url, "video/mp4", 52428800);
  // Primero el vídeo, luego los subtítulos.
  let t = loadBackground({ appConfig: CFG14, hostReply: () => ({ ok: true }) });
  t.L.headers(video("https://a.com/peli.mp4"));
  await wait(40);
  t.L.headers(hdr("https://a.com/subs/peli.es.vtt", "other", "text/vtt"));
  t.L.headers(hdr("https://a.com/subs/peli.es.vtt", "other", "text/vtt")); // otra vez: nada nuevo
  await wait(60);
  let e = tabMsgs(t);
  assert.equal(e.length, 2);
  assert.equal(e[1].kind, "subs"); assert.equal(e[1].media, "subtitle");
  assert.deepEqual(e[1].items, [{ kind: "subtitle", ext: ".vtt", language: "es", url: "https://a.com/subs/peli.es.vtt" }]);
  // Al elegirlos: el archivo mismo, sin pista.
  t.L.onMessage({ type: "media-download", entry: e[1], item: e[1].items[0] }, { tab: { id: 4, url: "https://a.com/ver", title: "Peli" } }, () => {});
  await wait(60);
  const d = t.posts("download")[0];
  assert.equal(d.url, "https://a.com/subs/peli.es.vtt"); assert.equal(d.mediaKind, "subs"); assert.equal(d.mediaVideo, ""); assert.equal(d.filename, "Peli.es.vtt");

  // Primero los subtítulos (sin vídeo aún): esperan; con el vídeo, llegan los dos.
  t = loadBackground({ appConfig: CFG14, hostReply: () => ({ ok: true }) });
  t.L.headers(hdr("https://a.com/s/x.srt", "xmlhttprequest", "application/x-subrip"));
  await wait(40);
  assert.equal(tabMsgs(t).length, 0, "Subtítulos sin vídeo: sin panel");
  t.L.headers(video("https://a.com/peli.mp4"));
  await wait(60);
  e = tabMsgs(t);
  assert.deepEqual(e.map((x) => x.kind), ["file", "subs"]);
  assert.equal(e[1].items[0].ext, ".srt");

  // Página con lista HLS: sus .vtt son trozos de sus subtítulos.
  t = loadBackground({ appConfig: CFG14, hostReply: (m) => (m.type === "probe" ? PROBE : { ok: true }) });
  t.L.headers(hdr("https://cdn.ejemplo.com/v/master.m3u8", "xmlhttprequest", "application/vnd.apple.mpegurl"));
  t.L.headers(hdr("https://cdn.ejemplo.com/v/subs/0.vtt", "xmlhttprequest", "text/vtt"));
  await wait(60);
  assert.deepEqual(tabMsgs(t).map((x) => x.kind), ["hls"]);

  // Panel desactivado: tampoco.
  t = loadBackground({ appConfig: Object.assign({}, CFG14, { mediaPanel: false }), hostReply: () => ({ ok: true }) });
  t.L.headers(video("https://a.com/peli.mp4"));
  t.L.headers(hdr("https://a.com/subs/peli.es.vtt", "other", "text/vtt"));
  await wait(60);
  assert.equal(tabMsgs(t).length, 0);
});

test("subtítulos: un vídeo pedido a trozos sigue ofreciéndose aunque antes llegaran unos subtítulos", async () => {
  const t = loadBackground({ appConfig: CFG14, hostReply: () => ({ ok: true }) });
  t.L.headers(hdr("https://a.com/s/x.vtt", "other", "text/vtt"));
  await wait(30);
  t.L.headers(ranged("https://a.com/v/peli.mp4?bytestart=0&byteend=99", "video/mp4", 90000000));
  await wait(60);
  assert.deepEqual(tabMsgs(t).map((x) => x.kind), ["file", "subs"]);
});

test("subtítulos: dirección que no es http(s) -> error, nada a IDMClone", async () => {
  const t = loadBackground({ hostReply: () => ({ ok: true }) });
  let reply = null;
  t.L.onMessage({ type: "media-download", entry: { kind: "subs", url: "data:text/vtt,WEBVTT", media: "subtitle" }, item: { kind: "subtitle", ext: ".vtt", url: "data:text/vtt,WEBVTT" } },
    { tab: { id: 4, url: "https://a.com/", title: "x" } }, (r) => { reply = r; });
  await wait(40);
  assert.equal(t.posts("download").length, 0);
  assert.ok(reply && reply.ok === false);
});

function fakeTrack(props) { return Object.assign(fakeEl("track"), props); }

test("subtítulos: el panel lista 'Subtítulos: ...' (de la lista, sueltos y <track> del vídeo) y elegir uno lo envía", async () => {
  const v = fakeEl("video");
  v.appendChild(fakeTrack({ kind: "subtitles", src: "https://v.com/t/es.vtt", srclang: "es", label: "Español" }));
  v.appendChild(fakeTrack({ kind: "captions", src: "https://v.com/t/en.srt", srclang: "en", label: "" }));
  v.appendChild(fakeTrack({ kind: "metadata", src: "https://v.com/t/chapters.vtt", srclang: "", label: "" }));    // no son subtítulos
  v.appendChild(fakeTrack({ kind: "subtitles", src: "blob:https://v.com/1", srclang: "fr", label: "" }));        // no se puede bajar
  v.appendChild(fakeTrack({ kind: "subtitles", src: "https://v.com/suelto.vtt", srclang: "it", label: "" }));    // ya avisado
  const c = loadContent({ mediaMini: false }, undefined, [v]);
  try {
    await wait(10);
    c.media({ url: "https://v.com/a.mp4", media: "video", items: [{ kind: "video", ext: ".mp4", size: 5000000 }] });
    c.media({ url: "https://v.com/suelto.vtt", kind: "subs", media: "subtitle", items: [{ kind: "subtitle", ext: ".vtt", language: "it", url: "https://v.com/suelto.vtt" }] });
    const host = c.created.find((e) => e.tagName === "IDMCLONE-PANEL");
    const root = host._root;
    await wait(550);
    fire(root.querySelector(".main"), "click", true);
    const menu = root.querySelector(".menu");
    assert.equal(menu.hidden, false, "Con subtítulos hay más de una opción: se abre la lista");
    const items = menu.querySelectorAll(".item").filter((b) => b.attrs.role === "menuitem");
    const labels = items.map((b) => b.children[0].textContent);
    assert.deepEqual(labels, ["MP4", "Subtítulos: Italiano (vtt)", "Subtítulos: Español (vtt)", "Subtítulos: Inglés (srt)"]);
    assert.equal(menu.querySelectorAll(".head").length, 0, "Los subtítulos no cuentan como otro vídeo");
    fire(items[2], "click", true);
    assert.equal(c.sent.length, 1);
    assert.equal(c.sent[0].type, "media-download");
    assert.equal(c.sent[0].entry.kind, "subs");
    assert.deepEqual(JSON.parse(JSON.stringify(c.sent[0].item)), { kind: "subtitle", ext: ".vtt", url: "https://v.com/t/es.vtt", language: "es", name: "Español" });
  } finally { c.stop(); }
});

// ------------------------------------------------------------------ teclas especiales en cualquier descarga (como IDM)

const KEYCFG = Object.assign({}, CFG14, { exts: ["zip", "pdf"], preventKeys: "del", forceKeys: "ins" });
const PAGE = { tab: { id: 3, url: "https://x.com/" } };
const key = (t, name, down) => t.L.onMessage({ type: "idm-keys", key: name, down }, PAGE, () => {});

test("teclas: lógica de la combinación (a la vez, margen tras soltar, tecla olvidada)", () => {
  const st = { ctrl: { down: true, from: 100, to: 0, seen: 900 }, alt: { down: true, from: 500, to: 0, seen: 950 } };
  assert.equal(L.keysHeld("ctrl+alt", st, 1000, 3000, 5000), true);
  assert.equal(L.keysHeld("ctrl", st, 1000, 3000, 5000), true);
  assert.equal(L.keysHeld("ins", st, 1000, 3000, 5000), false);
  assert.equal(L.keysHeld("", st, 1000, 3000, 5000), false);
  assert.equal(L.keysHeld("ctrl+tecla", st, 1000, 3000, 5000), false, "Nombre desconocido");
  // Suelta hace 2 s: aún vale; hace 4 s: ya no.
  const up = { del: { down: false, from: 1000, to: 3000, seen: 3000 } };
  assert.equal(L.keysHeld("del", up, 5000, 3000, 5000), true);
  assert.equal(L.keysHeld("del", up, 7000, 3000, 5000), false);
  // Ctrl soltada antes de pulsar Alt: no estuvieron pulsadas a la vez.
  const apart = { ctrl: { down: false, from: 100, to: 200, seen: 200 }, alt: { down: true, from: 300, to: 0, seen: 400 } };
  assert.equal(L.keysHeld("ctrl+alt", apart, 450, 3000, 5000), false);
  // "Pulsada" sin noticias desde hace más de 5 s (keyup perdido): suelta en el último aviso.
  const stale = { del: { down: true, from: 0 + 1, to: 0, seen: 1000 } };
  assert.equal(L.keysHeld("del", stale, 5500, 3000, 5000), true);
  assert.equal(L.keysHeld("del", stale, 9000, 3000, 5000), false);
});

test("teclas: con 'forzar' pulsada, CUALQUIER descarga va a IDMClone tal cual (formulario con su POST); al soltar, no", async () => {
  const t = loadBackground({ appConfig: KEYCFG, hostReply: () => ({ ok: true }) });
  await t.bg.getAppConfig();
  key(t, "ins", true);
  // Un formulario que devuelve un CSV (tipo no vigilado), descargado por el navegador.
  t.bg.rememberPost({ method: "POST", url: "https://x.com/exportar", requestBody: { formData: { q: ["1"] } } });
  t.L.onCreated({ id: 70, url: "https://x.com/exportar", finalUrl: "https://cdn.x.com/tmp/datos.csv", filename: "datos.csv", mime: "text/csv", state: "in_progress", referrer: "https://x.com/" });
  await wait(60);
  let d = t.posts("download");
  assert.equal(d.length, 1);
  assert.equal(d[0].url, "https://x.com/exportar", "La petición original (IDMClone sigue las redirecciones)");
  assert.equal(d[0].postData, "q=1", "Con el cuerpo del formulario");
  assert.equal(d[0].filename, "datos.csv");
  assert.equal(d[0].referrer, "https://x.com/");
  assert.ok(!d[0].auto, "Orden del usuario, no captura automática");
  assert.ok(t.names().includes("cancel"));
  // Recién soltada (la descarga empieza un momento después): aún vale.
  key(t, "ins", false);
  t.L.onCreated({ id: 71, url: "https://x.com/js-descarga", filename: "notas.txt", mime: "text/plain", state: "in_progress" });
  await wait(60);
  assert.equal(t.posts("download").length, 2, "Descarga lanzada por la página justo después de soltar");
  // Pasado el margen, ya no.
  t.bg.KEY_TIMING.grace = 20;
  await wait(40);
  t.L.onCreated({ id: 72, url: "https://x.com/otro.txt", filename: "otro.txt", mime: "text/plain", state: "in_progress" });
  await wait(60);
  assert.equal(t.posts("download").length, 2);
});

test("teclas: 'no descargar' pulsada -> ni se pausa ni se envía; Chromium tampoco pausa antes de saber el nombre", async () => {
  const t = loadBackground({ appConfig: KEYCFG, hostReply: () => ({ ok: true }) });
  await t.bg.getAppConfig();
  key(t, "del", true);
  t.L.onCreated({ id: 73, url: "https://x.com/a.zip", filename: "a.zip", state: "in_progress" });
  t.L.onCreated({ id: 74, url: "https://x.com/descargar.php?id=1", filename: "", state: "in_progress" });
  await wait(60);
  assert.equal(t.posts("download").length, 0);
  assert.ok(!t.names().includes("pause"));
  // Las dos a la vez: gana "no descargar".
  key(t, "ins", true);
  t.L.onCreated({ id: 75, url: "https://x.com/b.zip", filename: "b.zip", state: "in_progress" });
  await wait(60);
  assert.equal(t.posts("download").length, 0);
  // La ventana pierde el foco (content.js manda "reset"): se sueltan; pasado el margen, todo normal.
  t.bg.KEY_TIMING.grace = 20;
  t.L.onMessage({ type: "idm-keys", reset: true }, PAGE, () => {});
  await wait(40);
  t.L.onCreated({ id: 76, url: "https://x.com/c.zip", filename: "c.zip", state: "in_progress" });
  await wait(60);
  assert.deepEqual(t.posts("download").map((m) => m.url), ["https://x.com/c.zip"]);
  assert.equal(t.posts("download")[0].auto, true);
});

test("teclas: avisos que no cuentan (sin pestaña, tecla desconocida, 'forzar' desactivada) y tecla olvidada", async () => {
  let t = loadBackground({ appConfig: KEYCFG, hostReply: () => ({ ok: true }) });
  await t.bg.getAppConfig();
  t.L.onMessage({ type: "idm-keys", key: "ins", down: true }, {}, () => {});            // no viene de una página
  t.L.onMessage({ type: "idm-keys", key: "a", down: true }, PAGE, () => {});           // no es una tecla especial
  t.L.onMessage({ type: "idm-keys", key: "ins", down: "sí" }, PAGE, () => {});         // mal formado
  t.L.onCreated({ id: 77, url: "https://x.com/p.txt", filename: "p.txt", mime: "text/plain", state: "in_progress" });
  await wait(60);
  assert.equal(t.posts("download").length, 0);
  assert.deepEqual(Object.keys(t.bg.keyState), []);

  t = loadBackground({ appConfig: Object.assign({}, KEYCFG, { forceKeys: "" }), hostReply: () => ({ ok: true }) }); // "forzar" desmarcada en IDMClone
  await t.bg.getAppConfig();
  key(t, "ins", true);
  t.L.onCreated({ id: 78, url: "https://x.com/p.txt", filename: "p.txt", mime: "text/plain", state: "in_progress" });
  await wait(60);
  assert.equal(t.posts("download").length, 0);

  // keyup perdido (p. ej. se soltó en el visor de PDF, sin content.js): sin avisos repetidos, se da por suelta.
  t = loadBackground({ appConfig: KEYCFG, hostReply: () => ({ ok: true }) });
  await t.bg.getAppConfig();
  t.bg.KEY_TIMING.stale = 20; t.bg.KEY_TIMING.grace = 20;
  key(t, "ins", true);
  await wait(80);
  t.L.onCreated({ id: 79, url: "https://x.com/p.txt", filename: "p.txt", mime: "text/plain", state: "in_progress" });
  await wait(60);
  assert.equal(t.posts("download").length, 0);
});

test("teclas: lo que abre el navegador (PDF, JSON, imagen) también; una página web solo si es el enlace pulsado", async () => {
  const t = loadBackground({ appConfig: Object.assign({}, KEYCFG, { exts: ["zip"] }), hostReply: () => ({ ok: true }), tab: { url: "https://x.com/lista" } });
  await t.bg.getAppConfig();
  const nav = (url, ct, extra) => Object.assign({ type: "main_frame", tabId: 4, statusCode: 200, method: "GET", url, responseHeaders: [{ name: "Content-Type", value: ct }] }, extra || {});
  assert.equal(await t.bg.considerInline(nav("https://x.com/datos.json", "application/json")), null, "Sin tecla: nada");
  key(t, "ins", true);
  assert.ok(await t.bg.considerInline(nav("https://x.com/datos.json", "application/json")), "JSON con 'forzar'");
  assert.ok(await t.bg.considerInline(nav("https://x.com/doc.pdf", "application/pdf")), "PDF que no está en la lista, con 'forzar'");
  assert.equal(await t.bg.considerInline(nav("https://x.com/otra-pagina", "text/html; charset=utf-8")), null, "Una página web no (como IDM)");
  assert.equal(await t.bg.considerInline(nav("https://x.com/b.bin", "application/octet-stream")), null, "Lo que el navegador descarga va por la API de descargas");
  // Formulario POST que devuelve una imagen: con su cuerpo.
  t.bg.rememberPost({ method: "POST", url: "https://x.com/grafico", requestBody: { formData: { mes: ["5"] } } });
  assert.ok(await t.bg.considerInline(nav("https://x.com/grafico", "image/png", { method: "POST" })));
  // El enlace pulsado con la tecla: aunque sea una página.
  t.L.onMessage({ type: "force-mark", url: "https://x.com/articulo.html", referrer: "https://x.com/lista" }, PAGE, () => {});
  assert.ok(await t.bg.considerInline(nav("https://x.com/articulo.html", "text/html")));
  const d = t.posts("download");
  assert.deepEqual(d.map((m) => m.url), ["https://x.com/datos.json", "https://x.com/doc.pdf", "https://x.com/grafico", "https://x.com/articulo.html"]);
  assert.equal(d[2].postData, "mes=5");
  assert.ok(d.every((m) => !m.auto));
  // 'No descargar': un PDF de la lista se queda en el navegador.
  const p = loadBackground({ appConfig: KEYCFG, hostReply: () => ({ ok: true }) });
  await p.bg.getAppConfig();
  key(p, "del", true);
  assert.equal(await p.bg.considerInline(nav("https://x.com/doc.pdf", "application/pdf")), null);
  assert.equal(p.posts("download").length, 0);
});

test("teclas: content.js solo avisa de las teclas configuradas, de verdad, una vez por segundo, y las suelta al perder el foco", async () => {
  const c = loadContent({ preventKeys: "del", forceKeys: "ctrl+ins" });
  try {
    await wait(10);
    const k = (type, key, extra) => c.winFire(type, Object.assign({ type, key, isTrusted: true }, extra || {}));
    const msgs = () => JSON.parse(JSON.stringify(c.sent.filter((m) => m.type === "idm-keys"))); // objetos de otro "realm" (vm)
    k("keydown", "a"); k("keydown", "Shift"); k("keyup", "Shift");
    assert.equal(msgs().length, 0, "Ni lo que se escribe ni teclas que no están en las combinaciones");
    k("keydown", "Delete", { isTrusted: false });
    assert.equal(msgs().length, 0, "Tecla simulada por la página");
    k("keydown", "Delete");
    k("keydown", "Delete", { repeat: true }); k("keydown", "Delete", { repeat: true });
    assert.deepEqual(msgs(), [{ type: "idm-keys", key: "del", down: true }], "Los repetidos de una tecla mantenida: uno por segundo");
    k("keydown", "Control");
    k("keyup", "Delete");
    assert.deepEqual(msgs().slice(1), [{ type: "idm-keys", key: "ctrl", down: true }, { type: "idm-keys", key: "del", down: false }]);
    c.winFire("blur", { type: "blur", target: {} });
    assert.equal(msgs().length, 3, "El foco pasó a otro elemento: nada");
    const win = require("node:vm").runInContext("window", c.ctx); // la ventana tal como la ve content.js
    c.winFire("blur", { type: "blur", target: win });
    assert.deepEqual(msgs()[3], { type: "idm-keys", reset: true }, "La ventana perdió el foco: todas sueltas");
    c.winFire("blur", { type: "blur", target: win });
    assert.equal(msgs().length, 4, "Sin teclas pulsadas no se repite");
    // Clic con "no descargar": se marca el enlace (también <area>), sin tocar el clic.
    k("keydown", "Delete");
    const area = { closest: (q) => (q.includes("area[href]") ? { href: "https://x.com/mapa.zip" } : null) };
    c.winFire("click", { type: "click", isTrusted: true, target: area, preventDefault() { throw new Error("no"); } });
    assert.deepEqual(c.sent.filter((m) => m.type === "prevent-capture").map((m) => m.url), ["https://x.com/mapa.zip"]);
  } finally { c.stop(); }
});

// ------------------------------------------------------------------ idiomas (_locales)

const LOCALES = ["es", "en", "zh_CN", "ja"];
const readJson = (p) => JSON.parse(require("node:fs").readFileSync(p, "utf8"));

test("idiomas: es (por defecto), en, zh_CN y ja con las mismas claves y marcadores; el manifiesto usa __MSG_", () => {
  const m = readJson(path.join(EXT, "manifest.json"));
  assert.equal(m.default_locale, "es");
  const es = readJson(path.join(EXT, "_locales", "es", "messages.json"));
  for (const k of [m.name, m.description, m.action.default_title]) {
    const mm = /^__MSG_(\w+)__$/.exec(k);
    assert.ok(mm && es[mm[1]], "Del manifiesto: " + k);
  }
  assert.ok(es.extDesc.message.length <= 132, "Descripción demasiado larga para las tiendas");
  for (const lang of LOCALES) {
    const msgs = readJson(path.join(EXT, "_locales", lang, "messages.json"));
    assert.deepEqual(Object.keys(msgs).sort(), Object.keys(es).sort(), lang + ": claves distintas de es");
    for (const k of Object.keys(es)) {
      assert.ok(msgs[k].message && msgs[k].message.trim(), lang + "." + k + " vacío");
      assert.deepEqual(Object.keys(msgs[k].placeholders || {}).sort(), Object.keys(es[k].placeholders || {}).sort(), lang + "." + k + ": marcadores");
      for (const ph of Object.keys(msgs[k].placeholders || {})) assert.ok(msgs[k].message.includes("$" + ph.toUpperCase() + "$"), lang + "." + k + " no usa $" + ph.toUpperCase() + "$");
      assert.ok(/^[A-Za-z0-9_@]+$/.test(k), "Nombre de clave no válido: " + k);
    }
    assert.equal(msgs.extDesc.message.length <= 132, true, lang + ": descripción larga");
  }
});

test("idiomas: todas las claves que usa el código existen, y todas las de _locales se usan", () => {
  const fs = require("node:fs");
  const es = readJson(path.join(EXT, "_locales", "es", "messages.json"));
  const src = ["background.js", "content.js", "logic.js", "popup.js", "popup.html", "manifest.json"].map((f) => fs.readFileSync(path.join(EXT, f), "utf8")).join("\n");
  const used = new Set();
  for (const m of src.matchAll(/\b(?:t|T|tr)\(\s*"(\w+)"/g)) used.add(m[1]);
  for (const m of src.matchAll(/data-i18n="(\w+)"/g)) used.add(m[1]);
  for (const k of used) assert.ok(es[k], "Falta en _locales: " + k);
  for (const k of Object.keys(es)) assert.ok(src.includes('"' + k + '"') || src.includes("__MSG_" + k + "__"), "Clave sin usar: " + k);
  // Ningún texto del panel se mete como HTML (las traducciones pueden llevar comillas o "<").
  const content = fs.readFileSync(path.join(EXT, "content.js"), "utf8");
  for (const html of content.match(/innerHTML\s*=\s*`[\s\S]*?`;/g) || []) assert.ok(!/\$\{(?!CSS\}|ICON\}|mini\s*\?)/.test(html), "Interpolación en innerHTML: " + html);
});

test("idiomas: menú contextual, avisos, botón y paneles en el idioma del navegador (inglés y japonés)", async () => {
  const en = i18nFor("en");
  const t = loadBackground({ i18n: en, appConfig: Object.assign({}, CFG14, { browser: "Brave", captureOff: true }), hostReply: () => ({ ok: true }) });
  t.L.onInstalled(); await wait(10); await t.bg.menuDone();
  assert.equal(t.menus.titles["idmclone-link"], "Download with IDMClone");
  assert.equal(t.menus.titles["idmclone-selected"], "Download selected links with IDMClone");
  assert.equal(t.menus.titles["idmclone-all"], "Download all links with IDMClone");
  const s = await t.bg.getSettings();
  assert.equal(s.offReason, "IDMClone does not capture downloads from Brave (IDMClone Options > General).");
  await t.L.menuClicked({ menuItemId: "idmclone-link", linkUrl: "blob:https://x.com/1" }, { url: "https://x.com/" });
  assert.ok(t.calls.some((c) => c[0] === "notify" && c[2] === "That link cannot be downloaded (blob)."));
  const off = loadBackground({ i18n: en, noHost: true });
  const r = await off.bg.hostRequest({ type: "ping" }, 100);
  assert.match(r.error, /^Native host not available: .*Did you run install\.ps1\?$/);
  // Español por defecto.
  const es = loadBackground({});
  es.L.onInstalled(); await wait(10); await es.bg.menuDone();
  assert.equal(es.menus.titles["idmclone-link"], "Descargar con IDMClone");
  // Panel de vídeo en japonés (textos como texto, no como HTML).
  const c = loadContent({ mediaMini: false }, i18nFor("ja"));
  try {
    await wait(10);
    c.media({ url: "https://v.com/a.mp4", media: "video", items: [{ kind: "audio", ext: ".m4a", size: 5000000 }] });
    const host = c.created.find((e) => e.tagName === "IDMCLONE-PANEL");
    const root = host._root;
    assert.equal(root.querySelector(".label").textContent, "この動画をダウンロード");
    assert.equal(root.querySelector(".main").attrs.title, "この動画を IDMClone でダウンロード");
    assert.equal(root.querySelector(".x").attrs["aria-label"], "閉じる");
    assert.equal(L.mediaLabel({ kind: "audio", ext: ".m4a" }), "音声のみ M4A");
  } finally { c.stop(); }
  globalThis.chrome = { i18n: I18N_ES };
  assert.equal(L.mediaLabel({ kind: "audio", ext: ".m4a" }), "solo audio M4A");
  // Sin chrome.i18n (copia huérfana...): la clave, sin romper nada.
  delete globalThis.chrome;
  assert.equal(L.t("panelSent"), "panelSent");
  globalThis.chrome = { i18n: I18N_ES };
});

test("idiomas: subtítulos del panel en inglés, chino y japonés (nombres de idioma y avisos)", async () => {
  const CODES = ["es", "en", "fr", "de", "it", "pt", "ca", "gl", "eu", "nl", "ru", "ja", "zh", "ko", "ar", "pl", "tr", "sv", "no", "nb", "da", "fi",
                 "el", "he", "hi", "cs", "hu", "ro", "uk", "id", "th", "vi", "spa", "eng", "fra", "fre", "deu", "ger", "ita", "por"];
  const want = { es: ["Subtítulos: Español (vtt)", "Subtítulos: sin idioma (srt)"], en: ["Subtitles: Spanish (vtt)", "Subtitles: no language (srt)"],
                 zh_CN: ["字幕: 西班牙语 (vtt)", "字幕: 未知语言 (srt)"], ja: ["字幕: スペイン語 (vtt)", "字幕: 言語不明 (srt)"] };
  try {
    for (const lang of LOCALES) {
      globalThis.chrome = { i18n: i18nFor(lang) };
      assert.equal(L.mediaLabel({ kind: "subtitle", language: "es-ES", ext: ".vtt" }), want[lang][0], lang);
      assert.equal(L.mediaLabel({ kind: "subtitle", language: "", ext: ".srt" }), want[lang][1], lang);
      assert.equal(L.mediaLabel({ kind: "subtitle", language: "en", name: "English (CC)", ext: ".vtt" }).includes("English (CC)"), true, lang + ": el nombre del sitio manda");
      for (const c of CODES) { const n = L.languageName(c); assert.ok(n && !/^lang[A-Z]/.test(n) && n !== c, lang + ": sin nombre para " + c); }
      assert.equal(L.languageName("x-klingon"), "x-klingon");
    }
  } finally { globalThis.chrome = { i18n: I18N_ES }; }
  // El aviso de background.js, también traducido.
  const t = loadBackground({ i18n: i18nFor("en"), hostReply: () => ({ ok: true }) });
  let reply = null;
  t.L.onMessage({ type: "media-download", entry: { kind: "subs", url: "data:text/vtt,WEBVTT", media: "subtitle" }, item: { kind: "subtitle", ext: ".vtt", url: "data:text/vtt,WEBVTT" } },
    { tab: { id: 4, url: "https://a.com/", title: "x" } }, (r) => { reply = r; });
  await wait(40);
  assert.equal(reply && reply.error, "These subtitles do not have an address IDMClone can download.");
  globalThis.chrome = { i18n: I18N_ES };
});

test("idiomas: package.ps1 mete _locales en los dos paquetes y conserva default_locale", { skip: process.platform !== "win32" }, () => {
  const cp = require("node:child_process"), os = require("node:os"), fs = require("node:fs");
  const out = fs.mkdtempSync(path.join(os.tmpdir(), "idmclone-pkg-"));
  try {
    const ps = `& '${path.join(__dirname, "..", "package.ps1")}' -Out '${out}' 6>$null | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$r = @{}
foreach ($z in Get-ChildItem '${out}' -Filter *.zip) {
  $a = [System.IO.Compression.ZipFile]::OpenRead($z.FullName)
  try {
    $e = $a.GetEntry("manifest.json"); $sr = New-Object System.IO.StreamReader($e.Open())
    $r[$z.Name] = @{ entries = @($a.Entries | ForEach-Object { $_.FullName }); manifest = $sr.ReadToEnd() }; $sr.Dispose()
  } finally { $a.Dispose() }
}
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$r | ConvertTo-Json -Depth 5 -Compress`;
    const res = cp.spawnSync("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", ps], { encoding: "utf8", timeout: 120000 });
    assert.equal(res.status, 0, res.stderr);
    const r = JSON.parse(res.stdout.trim());
    const names = Object.keys(r);
    assert.equal(names.length, 2, names.join(", "));
    for (const n of names) {
      for (const lang of LOCALES) assert.ok(r[n].entries.includes("_locales/" + lang + "/messages.json"), n + ": falta " + lang);
      const m = JSON.parse(r[n].manifest);
      assert.equal(m.default_locale, "es", n);
      assert.equal(m.name, "__MSG_extName__", n);
    }
  } finally { fs.rmSync(out, { recursive: true, force: true }); }
});

// ------------------------------------------------------------------ recarga tras actualizar IDMClone (/update)

test("versiones: por números", () => {
  assert.equal(L.newerVersion("1.5", "1.4"), true);
  assert.equal(L.newerVersion("1.10", "1.9"), true);
  assert.equal(L.newerVersion("1.4.1", "1.4"), true);
  assert.equal(L.newerVersion("1.4", "1.4.0"), false);
  assert.equal(L.newerVersion("1.4", "1.5"), false);
  assert.equal(L.newerVersion("", "1.4"), false);
  assert.equal(L.newerVersion("1.x", "1.4"), false);
});

test("tras /update: la extensión desempaquetada vieja se recarga sola, una vez por versión", async () => {
  const cfg = Object.assign({}, CFG14, { extVersion: "9.0" });
  const t = loadBackground({ appConfig: cfg, installType: "development" });
  await t.bg.getAppConfig();
  await wait(20);
  assert.equal(t.calls.filter((c) => c[0] === "reload").length, 1, "Debía recargarse");
  assert.equal(t.store.reloadedFor, "9.0");
  // Si tras recargar sigue vieja (cargada desde otra carpeta): no se repite.
  assert.equal(await t.bg.checkExtUpdate("9.0"), false);
  assert.equal(t.calls.filter((c) => c[0] === "reload").length, 1, "No debe recargarse en bucle");
});

test("tras /update: no se recarga si es igual, más vieja, de una tienda o sin el dato (IDMClone antiguo)", async () => {
  const same = loadBackground({ appConfig: Object.assign({}, CFG14, { extVersion: "1.5" }), installType: "development" });
  await same.bg.getAppConfig(); await wait(20);
  const store = loadBackground({ appConfig: Object.assign({}, CFG14, { extVersion: "9.0" }), installType: "normal" });
  await store.bg.getAppConfig(); await wait(20);
  const old = loadBackground({ appConfig: CFG14, installType: "development" });
  await old.bg.getAppConfig(); await wait(20);
  const noApi = loadBackground({ appConfig: Object.assign({}, CFG14, { extVersion: "9.0" }) });
  await noApi.bg.getAppConfig(); await wait(20);
  for (const x of [same, store, old, noApi]) assert.equal(x.calls.filter((c) => c[0] === "reload").length, 0);
});

test("tras /update: la recarga espera a que termine la captura en curso (no deja la descarga pausada)", async () => {
  const t = loadBackground({ appConfig: CFG14, installType: "development", hostReply: () => ({ ok: true }),
                             delay: (m) => (m.type === "download" ? 400 : 5) });
  const c = t.bg.consider({ id: 77, url: "https://x.com/grande.zip", filename: "grande.zip", state: "in_progress" });
  await wait(60);
  const u = t.bg.checkExtUpdate("9.0");
  await wait(150);
  assert.equal(t.calls.filter((x) => x[0] === "reload").length, 0, "No debe recargarse con la descarga pausada");
  assert.equal(await u, true);
  const names = t.calls.map((x) => x[0]);
  assert.ok(names.indexOf("cancel") >= 0 && names.indexOf("cancel") < names.indexOf("reload"), "Primero se termina la captura: " + names.join(","));
  await c;
});
