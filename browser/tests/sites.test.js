// Pruebas de la 2.0: datos de los sitios (page/hooks.js en la página + bg/sites.js en el fondo).
// Las respuestas son ejemplos escritos a mano con la estructura documentada de cada sitio (no son datos reales).
// Ejecutar: node --test browser/tests/sites.test.js
"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const fs = require("node:fs");
const vm = require("node:vm");

const EXT = path.join(__dirname, "..", "extension");
const MANIFEST = JSON.parse(fs.readFileSync(path.join(EXT, "manifest.json"), "utf8"));
const ES = JSON.parse(fs.readFileSync(path.join(EXT, "_locales", "es", "messages.json"), "utf8"));
const I18N = { getMessage: (k, subs) => { const m = ES[k]; if (!m) return ""; let s = m.message; (subs || []).forEach((v, i) => { s = s.split("$" + (i + 1)).join(v); }); return s; }, getUILanguage: () => "es" };
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

// ---- Ejemplos ----
const XID = "1790000000000000001";
const X_TWEET = { data: { tweetResult: { result: { __typename: "Tweet", legacy: { extended_entities: { media: [{
  id_str: XID, type: "video", media_url_https: "https://pbs.twimg.com/ext_tw_video_thumb/" + XID + "/pu/img/a.jpg",
  video_info: { duration_millis: 12000, variants: [
    { content_type: "application/x-mpegURL", url: "https://video.twimg.com/ext_tw_video/" + XID + "/pu/pl/x.m3u8" },
    { bitrate: 832000, content_type: "video/mp4", url: "https://video.twimg.com/ext_tw_video/" + XID + "/pu/vid/avc1/640x360/a.mp4?tag=12" },
    { bitrate: 2176000, content_type: "video/mp4", url: "https://video.twimg.com/ext_tw_video/" + XID + "/pu/vid/avc1/1280x720/b.mp4?tag=12" },
    { bitrate: 9999999, content_type: "video/mp4", url: "https://malo.example.com/" + XID + "/1920x1080/x.mp4" }
  ] } }] } } } } } };
const FB_MPD = '<MPD><Period><AdaptationSet mimeType="video/mp4">' +
  '<Representation width="1280" height="720" bandwidth="2000000"><BaseURL>https://video.xx.fbcdn.net/v/720.mp4?a=1&amp;b=2</BaseURL></Representation>' +
  '<Representation width="640" height="360" bandwidth="800000"><BaseURL>https://video.xx.fbcdn.net/v/360.mp4</BaseURL></Representation>' +
  '</AdaptationSet><AdaptationSet mimeType="audio/mp4"><Representation bandwidth="128000"><BaseURL>https://video.xx.fbcdn.net/v/audio.mp4</BaseURL></Representation>' +
  '</AdaptationSet></Period></MPD>';
const FB_VIDEO = { data: { video: { id: "1234567890", playable_url: "https://video.xx.fbcdn.net/v/sd.mp4?x=1&bytestart=0&byteend=99",
  playable_url_quality_hd: "https://video.xx.fbcdn.net/v/hd.mp4?x=1", dash_manifest: FB_MPD, playable_duration_in_ms: 30000 } } };
const IG_ITEM = { items: [{ code: "Cabc123XYZ", pk: "3100000000000000001", video_duration: 9.5,
  video_versions: [{ url: "https://scontent.cdninstagram.com/v/t50/abc.mp4?x=1", width: 720, height: 1280 }] }] };
const VIMEO_CFG = { video: { id: 76979871, title: "Mi corto de Vimeo", duration: 62 }, request: { files: {
  progressive: [{ url: "https://vod-progressive.akamaized.net/x/360.mp4", width: 640, height: 360 }, { url: "https://vod-progressive.akamaized.net/x/720.mp4", width: 1280, height: 720 }],
  hls: { default_cdn: "akfire", cdns: { akfire: { url: "https://x.vimeocdn.com/master.m3u8" } } } } } };

// ---- page/hooks.js en una página simulada ----
function pageHooks(hostname, responses, setup) {
  const events = [], docListeners = {};
  let cloned = 0;
  const fakeFetch = async (input) => {
    const url = typeof input === "string" ? input : input.url;
    const body = responses[new URL(url, "https://" + hostname).pathname];
    const r = { ok: true, url, clone() { cloned++; return { text: async () => (typeof body === "string" ? body : JSON.stringify(body || {})) }; } };
    return r;
  };
  function FakeXHR() { this.listeners = {}; this.status = 0; this.responseType = ""; }
  FakeXHR.prototype.open = function (m, u) { this.url = u; };
  FakeXHR.prototype.addEventListener = function (t, f) { (this.listeners[t] = this.listeners[t] || []).push(f); };
  FakeXHR.prototype.send = function () {
    const body = responses[new URL(this.url, "https://" + hostname).pathname];
    setTimeout(() => { this.status = 200; this.responseText = typeof body === "string" ? body : JSON.stringify(body || {}); for (const f of this.listeners.load || []) f(); }, 1);
  };
  const document = {
    readyState: "complete", querySelectorAll: () => [],
    addEventListener: (t, f) => { (docListeners[t] = docListeners[t] || []).push(f); },
    dispatchEvent: (e) => { events.push(e); return true; }
  };
  class CustomEvent { constructor(type, o) { this.type = type; this.detail = o && o.detail; } }
  const win = { location: { hostname, href: "https://" + hostname + "/", pathname: "/" }, document, fetch: fakeFetch, XMLHttpRequest: FakeXHR,
                CustomEvent, URL, JSON, addEventListener() { }, setInterval() { }, setTimeout };
  win.window = win; win.self = win;
  if (setup) setup(win);
  vm.createContext(win);
  vm.runInContext(fs.readFileSync(path.join(EXT, "page", "hooks.js"), "utf8"), win, { filename: "page/hooks.js" });
  return { win, data: () => events.filter((e) => e.type === "idmclone-data").map((e) => JSON.parse(e.detail)), cloned: () => cloned };
}

test("página: X — solo lee las respuestas de datos y saca las variantes de cada medio", async () => {
  const p = pageHooks("x.com", { "/i/api/graphql/abc/TweetDetail": X_TWEET, "/otra/cosa": { nada: 1 } });
  await p.win.fetch("https://x.com/otra/cosa");
  await p.win.fetch("/i/api/graphql/abc/TweetDetail?variables=1");
  await wait(10);
  assert.equal(p.cloned(), 1, "Solo la respuesta de datos (las demás ni se copian)");
  const d = p.data();
  assert.equal(d.length, 1);
  assert.equal(d[0].site, "x");
  assert.equal(d[0].kind, "x-media");
  assert.equal(d[0].media[0].id, XID);
  assert.equal(d[0].media[0].variants.length, 4, "La página manda lo que hay; el fondo filtra");
  assert.equal(d[0].media[0].duration, 12);
});

test("página: Facebook por XMLHttpRequest, con el «for (;;);» delante", async () => {
  const p = pageHooks("www.facebook.com", { "/api/graphql/": "for (;;);" + JSON.stringify(FB_VIDEO) });
  const x = new p.win.XMLHttpRequest();
  x.open("POST", "/api/graphql/");
  x.send("q=1");
  await wait(20);
  const d = p.data();
  assert.equal(d.length, 1);
  assert.equal(d[0].kind, "fb-media");
  assert.equal(d[0].media[0].id, "1234567890");
  assert.ok(d[0].media[0].dash.includes("<MPD>"));
});

test("página: no hace nada fuera de los sitios soportados", () => {
  const p = pageHooks("ejemplo.com", {});
  assert.equal(typeof p.win.fetch, "function");
  assert.equal(p.win.fetch.name, "fakeFetch", "fetch sin tocar");
});

// ---- bg/sites.js ----
function background(opts) {
  opts = opts || {};
  const tabMsgs = [], host = [];
  let onMessage = null;
  const headers = [];
  const ev = () => ({ addListener() { } });
  globalThis.chrome = {
    runtime: { id: "x", onInstalled: ev(), onStartup: ev(), onMessage: { addListener: (f) => { onMessage = f; } }, getManifest: () => MANIFEST,
               connectNative: () => { let reply = null; return { onMessage: { addListener: (f) => { reply = f; } }, onDisconnect: ev(),
                 postMessage(m) { host.push(m); setTimeout(() => reply && reply(Object.assign({ rid: m.rid }, m.type === "getconfig" ? (opts.config || { ok: false }) : opts.reply ? opts.reply(m) : { ok: true, id: "d1" })), 1); } }; } },
    storage: { local: { get: async (d) => Object.assign({}, d), set: async () => {} }, onChanged: ev() },
    downloads: { onCreated: ev() }, cookies: { getAll: async () => [] },
    contextMenus: { removeAll: async () => {}, create() { }, onClicked: ev() }, i18n: I18N, notifications: { create() { } },
    webRequest: { onHeadersReceived: { addListener: (f) => headers.push(f) } },
    tabs: { get: async (id) => ({ id, url: "https://x.com/u/status/1", title: "(2) Ana en X: \"mi vídeo\" / X" }),
            sendMessage: async (tabId, msg) => { tabMsgs.push(msg); }, onUpdated: ev(), onRemoved: ev() }
  };
  delete globalThis.browser;
  const file = path.join(EXT, "background.js");
  delete require.cache[require.resolve(file)];
  const bg = require(file);
  const send = (data, url, extra) => onMessage(Object.assign({ type: "site-data", data }, extra || {}), { tab: { id: 7, url }, url, frameId: 0 }, () => {});
  return { bg, host, tabMsgs, send, onMessage, headers,
           shown: () => { const live = []; for (const m of tabMsgs) { if (m.type === "idm-media") live.push(m.entry); else if (m.type === "idm-media-remove") { const i = live.findIndex((e) => e.url === m.url); if (i >= 0) live.splice(i, 1); } } return live; } };
}
const xData = () => ({ site: "x", kind: "x-media", media: X_TWEET.data.tweetResult.result.legacy.extended_entities.media.map((m) => ({
  id: m.id_str, type: m.type, duration: 12, variants: m.video_info.variants.map((v) => ({ url: v.url, type: v.content_type, bitrate: v.bitrate | 0 })) })) });

test("fondo: X — las variantes MP4 de video.twimg.com, de más a menos calidad, con su pista; lo de otros servidores, no", async () => {
  const t = background();
  t.send(xData(), "https://x.com/u/status/1");
  await wait(30);
  const e = t.shown();
  assert.equal(e.length, 1);
  assert.equal(e[0].kind, "site");
  assert.deepEqual(e[0].items.map((i) => i.height), [720, 360]);
  assert.ok(e[0].items.every((i) => i.url.startsWith("https://video.twimg.com/")));
  assert.ok(e[0].hints.includes("x:" + XID));
  // La misma respuesta otra vez (la web la pide de nuevo): nada nuevo.
  t.send(xData(), "https://x.com/u/status/1");
  await wait(20);
  assert.equal(t.tabMsgs.filter((m) => m.type === "idm-media").length, 1);
});

test("fondo: los datos solo valen desde una página del propio sitio (lo dice el navegador)", async () => {
  const t = background();
  t.send(xData(), "https://ejemplo.com/");
  t.send(Object.assign(xData(), { site: "facebook" }), "https://x.com/");
  await wait(30);
  assert.equal(t.shown().length, 0);
});

test("fondo: X — la lista HLS que detecta la red sobra si la web ya dio el vídeo (y al revés se quita)", async () => {
  const t = background();
  const hls = { tabId: 7, frameId: 0, type: "xmlhttprequest", statusCode: 200, url: "https://video.twimg.com/ext_tw_video/" + XID + "/pu/pl/x.m3u8",
                responseHeaders: [{ name: "content-type", value: "application/x-mpegURL" }] };
  // La red primero (como si IDMClone ya hubiera leído la lista); luego llegan los datos de la web y la sustituyen.
  const items = [{ kind: "video", ext: ".mp4", width: 1280, height: 720 }];
  const fromNet = { id: 900, kind: "hls", url: hls.url, media: "video", items };
  t.bg.mediaState(7).items.set(hls.url, fromNet);
  await t.bg.sendEntry({ tabId: 7, frameId: 0 }, fromNet);
  assert.deepEqual(t.shown().map((e) => e.kind), ["hls"]);
  t.send(xData(), "https://x.com/u/status/1");
  await wait(30);
  assert.deepEqual(t.shown().map((e) => e.kind), ["site"], "La de la red se quita");
  // Y lo que llegue después por la red del mismo vídeo ya no sale.
  await t.bg.sendEntry({ tabId: 7, frameId: 0 }, { id: 901, kind: "hls", url: hls.url + "?otra=1", media: "video", items });
  assert.deepEqual(t.shown().map((e) => e.kind), ["site"]);
});

test("fondo: Facebook — DASH (cada calidad con su audio) y los MP4 HD/SD; descargar cada uno", async () => {
  const t = background();
  const d = { site: "facebook", kind: "fb-media", media: [{ id: "1234567890", sd: FB_VIDEO.data.video.playable_url, hd: FB_VIDEO.data.video.playable_url_quality_hd,
              dash: FB_MPD, duration: 30, progressive: [] }] };
  t.send(d, "https://www.facebook.com/watch/?v=1234567890");
  await wait(30);
  const e = t.shown()[0];
  assert.ok(e, "Sale");
  assert.ok(e.hints.includes("fb:1234567890"));
  assert.deepEqual(e.items.slice(0, 2).map((i) => [i.height, !!i.audio]), [[720, true], [360, true]]);
  assert.equal(e.items[0].video, "https://video.xx.fbcdn.net/v/720.mp4?a=1&b=2", "&amp; del XML decodificado");
  assert.ok(e.items.some((i) => i.quality === "SD" && !/bytestart/.test(i.url)), "El MP4 entero, sin el trozo");
  // Elegir la de 720p: IDMClone junta vídeo y audio ("pair").
  t.onMessage({ type: "media-download", entry: e, item: e.items[0], index: 0 }, { tab: { id: 7, url: "https://www.facebook.com/watch/?v=1234567890", title: "Vídeo | Facebook" } }, () => {});
  const hd = e.items.findIndex((i) => i.quality === "HD");
  t.onMessage({ type: "media-download", entry: e, item: e.items[hd], index: hd }, { tab: { id: 7, url: "https://www.facebook.com/watch/?v=1234567890", title: "Vídeo | Facebook" } }, () => {});
  await wait(30);
  const dl = t.host.filter((m) => m.type === "download");
  assert.equal(dl.length, 2);
  assert.deepEqual([dl[0].mediaKind, dl[0].url, dl[0].mediaAudio], ["pair", "https://video.xx.fbcdn.net/v/720.mp4?a=1&b=2", "https://video.xx.fbcdn.net/v/audio.mp4"]);
  assert.equal(dl[0].filename, "Vídeo.mp4");
  assert.equal(dl[1].mediaKind, undefined, "El MP4 con audio: descarga normal");
  assert.equal(dl[1].url, "https://video.xx.fbcdn.net/v/hd.mp4?x=1");
});

test("fondo: Instagram y Vimeo (Vimeo da el título del vídeo, que manda en el nombre)", async () => {
  const t = background();
  t.send({ site: "instagram", kind: "ig-media", media: IG_ITEM.items.map((m) => ({ code: m.code, id: m.pk, duration: m.video_duration, dash: "", versions: m.video_versions })) },
         "https://www.instagram.com/reel/Cabc123XYZ/");
  t.send({ site: "vimeo", kind: "vimeo-config", id: "76979871", title: VIMEO_CFG.video.title, duration: 62, hls: "", dash: "",
           progressive: VIMEO_CFG.request.files.progressive }, "https://player.vimeo.com/video/76979871");
  await wait(40);
  const e = t.shown();
  assert.equal(e.length, 2);
  const ig = e.find((x) => x.site === "instagram"), vi = e.find((x) => x.site === "vimeo");
  assert.ok(ig.hints.includes("ig:Cabc123XYZ"));
  assert.deepEqual(vi.items.map((i) => i.height), [720, 360]);
  t.onMessage({ type: "media-download", entry: vi, item: vi.items[0], index: 0 }, { tab: { id: 7, url: "https://vimeo.com/76979871", title: "algo - Vimeo" } }, () => {});
  await wait(30);
  const dl = t.host.filter((m) => m.type === "download");
  assert.equal(dl[0].filename, "Mi corto de Vimeo.mp4");
  assert.equal(dl[0].url, "https://vod-progressive.akamaized.net/x/720.mp4");
});

// ---- YouTube: la extensión solo pasa el vídeo a IDMClone (ytinfo); IDMClone 1.5 lee las calidades ----
const YT = { site: "youtube", kind: "yt-video", videoId: "dQw4w9WgXcQ", title: "Mi vídeo de YouTube", duration: 213, live: false,
             visitorData: "Cgt2aXNpdG9y", playerJsUrl: "/s/player/abc123/player_ias.vflset/es_ES/base.js", sts: 20000, loggedIn: false };
const CFG15 = { ok: true, exts: ["zip"], exclude: [], mediaPanel: true, features: ["ytinfo"] };

test("página: YouTube — al cargar manda el vídeo y los datos de la página que necesita IDMClone", () => {
  const p = pageHooks("www.youtube.com", {}, (win) => {
    win.ytInitialPlayerResponse = { videoDetails: { videoId: YT.videoId, title: YT.title, lengthSeconds: "213" }, playabilityStatus: { status: "OK" } };
    win.ytcfg = { get: (k) => ({ VISITOR_DATA: YT.visitorData, PLAYER_JS_URL: YT.playerJsUrl, STS: YT.sts, LOGGED_IN: false })[k] };
  });
  const d = p.data();
  assert.equal(d.length, 1);
  assert.deepEqual([d[0].kind, d[0].videoId, d[0].title, d[0].duration, d[0].visitorData, d[0].playerJsUrl, d[0].sts],
                   ["yt-video", YT.videoId, YT.title, 213, YT.visitorData, YT.playerJsUrl, YT.sts]);
});

test("fondo: YouTube con un IDMClone sin motor — no se le pregunta nada; el panel dice que hay que actualizarlo", async () => {
  const t = background({ config: { ok: true, exts: ["zip"], exclude: [], mediaPanel: true } });
  t.send(YT, "https://www.youtube.com/watch?v=dQw4w9WgXcQ");
  await wait(30);
  assert.equal(t.host.filter((m) => m.type === "ytinfo").length, 0, "Un IDMClone 1.4 abriría su ventana en cada vídeo");
  const e = t.shown()[0];
  assert.ok(e && e.note && /1\.5/.test(e.note));
  assert.deepEqual(e.hints, ["yt:dQw4w9WgXcQ"]);
});

test("fondo: YouTube con IDMClone 1.5 — ytinfo, calidades en el panel y descarga con mediaKind youtube", async () => {
  const items = [{ kind: "video", itag: 137, video: "137", audio: "140", width: 1920, height: 1080, bandwidth: 4000000, size: 90000000, ext: ".mp4" },
                 { kind: "video", itag: 22, video: "22", audio: null, width: 1280, height: 720, bandwidth: 2000000, size: 50000000, ext: ".mp4" },
                 { kind: "audio", itag: 140, audio: "140", language: "es", bandwidth: 128000, size: 3400000, ext: ".m4a" }];
  const t = background({ config: CFG15, reply: (m) => (m.type === "ytinfo" ? { ok: true, kind: "youtube", title: "Título de IDMClone", duration: 213, live: false, items } : { ok: true, id: "d1" }) });
  t.send(Object.assign({}, YT, { playerJsUrl: "https://malo.com/x.js" }), "https://www.youtube.com/watch?v=dQw4w9WgXcQ");
  await wait(40);
  const q = t.host.find((m) => m.type === "ytinfo");
  assert.ok(q, "Se pregunta a IDMClone");
  assert.deepEqual([q.videoId, q.visitorData, q.sts, q.playerJsUrl, q.url], [YT.videoId, YT.visitorData, YT.sts, "", undefined], "Sin «url» (un IDMClone antiguo lo tomaría por una descarga) ni un player de otro sitio");
  const e = t.shown()[0];
  assert.equal(e.kind, "youtube");
  assert.equal(e.items.length, 3);
  t.onMessage({ type: "media-download", entry: e, item: e.items[0], index: 0 }, { tab: { id: 7, url: "https://www.youtube.com/watch?v=dQw4w9WgXcQ", title: "x - YouTube" } }, () => {});
  t.onMessage({ type: "media-download", entry: e, item: e.items[2], index: 2 }, { tab: { id: 7, url: "https://www.youtube.com/watch?v=dQw4w9WgXcQ", title: "x - YouTube" } }, () => {});
  await wait(30);
  const dl = t.host.filter((m) => m.type === "download");
  assert.deepEqual([dl[0].mediaKind, dl[0].url, dl[0].mediaVideo, dl[0].mediaAudio, dl[0].filename],
                   ["youtube", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "137", "140", "Título de IDMClone.mp4"]);
  assert.deepEqual([dl[1].mediaVideo, dl[1].mediaAudio, dl[1].filename], ["", "140", "Título de IDMClone.m4a"], "Solo el audio");
});
