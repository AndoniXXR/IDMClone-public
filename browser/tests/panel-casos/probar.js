// Recorre los casos de servidor.js en un navegador de verdad (el que abre real-browser\start.ps1, con su perfil
// temporal) y comprueba qué llega al panel de cada página, también lo que aparece un momento y se quita.
//   1) node browser\tests\panel-casos\servidor.js
//   2) powershell -ExecutionPolicy Bypass -File browser\tests\real-browser\start.ps1 edge 9333
//   3) node browser\tests\panel-casos\probar.js [puerto] [carpeta de capturas] [caso...]
"use strict";
const fs = require("node:fs");
const path = require("node:path");
const { CDP, browserWs } = require("../real-browser/cdp.js");

const PORT = Number(process.argv[2]) || 9333;
const SHOTS = process.argv[3] || path.join(require("node:os").tmpdir(), "idmclone-panel-casos");
const ONLY = process.argv.slice(4);
const BASE = "http://127.0.0.1:18790/caso/";
const EXT_ID = "jolfcgooaockjhnalagfilpljkloallc";
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Lo que debe quedar en el panel: una línea por entrada ("vídeo: 1280x720 640x360 audio"). flash: si puede quitarse
// algo por el camino (el vídeo que sale solo hasta que llega su audio).
const V = (...q) => "video: " + q.join(" ");
const CASOS = {
  "x-normal": [V("1280x720", "640x360", "480x270", "audio")],
  "x-baja": [V("480x270", "320x180", "audio")],
  "hijas": [V("1280x720", "640x360", "audio")],
  "solo-audio": ["audio: audio"],
  "codecs": [V("video", "video")],
  "suelta-audio": ["audio: audio"],
  "suelta-video": [V("video")],
  "dash": [V("1280x720", "640x360", "audio")],
  "par-audio-primero": ["video: par"],
  "par-video-primero": { want: ["video: par"], flash: true },
  "par-anuncio": [],
  "trozos-solo-audio": ["audio: audio"],
  "mp4-grande": [V("video")],
  "mp4-anuncio": [],
  "mp3": ["audio: audio"],
  "subtitulos": [V("video"), "subtitle: subtítulos"],
  "real": { want: null, wait: 9000 },
};

function describe(entry) {
  const parts = entry.items.map((i) => i.kind === "subtitle" ? "subtítulos" : entry.kind === "pair" ? "par"
    : i.kind === "audio" ? "audio" : i.height > 0 ? i.width + "x" + i.height : "video");
  return entry.media + ": " + parts.join(" ");
}

(async () => {
  fs.mkdirSync(SHOTS, { recursive: true });
  const c = await CDP.connect(await browserWs(PORT));
  const findSw = async () => (await c.send("Target.getTargets")).targetInfos.find((t) => t.type === "service_worker" && t.url.includes(EXT_ID));
  let sw = null;
  for (let i = 0; i < 40 && !sw; i++) { sw = await findSw(); if (!sw) await sleep(250); }
  if (!sw) throw new Error("No aparece el service worker de la extensión (¿se cargó?)");
  const swS = (await c.send("Target.attachToTarget", { targetId: sw.targetId, flatten: true })).sessionId;
  const swEval = async (expr) => (await c.send("Runtime.evaluate", { expression: expr, awaitPromise: true, returnByValue: true }, swS)).result.value;
  // Registro de todo lo que la extensión manda a las páginas (idm-media / idm-media-remove / idm-media-reset).
  await swEval(`(() => { if (globalThis.__panelLog) return "ya";
    globalThis.__panelLog = [];
    const orig = chrome.tabs.sendMessage.bind(chrome.tabs);
    chrome.tabs.sendMessage = (tabId, msg, opts) => { __panelLog.push({ tabId, t: Date.now(), type: msg && msg.type, entry: msg && msg.entry, url: msg && msg.url }); return orig(tabId, msg, opts); };
    return "ok"; })()`);
  const cfg = await swEval("getAppConfig().then(a => a && { panel: a.mediaPanel, minVideoKB: a.minVideoKB, minAudioKB: a.minAudioKB })");
  console.log("IDMClone responde:", JSON.stringify(cfg));

  let ok = 0, bad = 0;
  for (const [id, spec0] of Object.entries(CASOS)) {
    if (ONLY.length && !ONLY.includes(id)) continue;
    const spec = Array.isArray(spec0) ? { want: spec0 } : spec0;
    const { targetId } = await c.send("Target.createTarget", { url: "about:blank" });
    const s = (await c.send("Target.attachToTarget", { targetId, flatten: true })).sessionId;
    await c.send("Page.enable", {}, s);
    await swEval("__panelLog.length = 0, 0");
    await c.send("Page.navigate", { url: BASE + id }, s);
    await sleep(spec.wait || 5000); // la espera del audio a trozos es de 2,5 s
    const log = (await swEval("__panelLog")).filter((m) => m.type === "idm-media" || m.type === "idm-media-remove");
    // Lo que quedó: lo enviado menos lo quitado (por dirección, como content.js).
    // Solo cuenta como "quitado" lo que de verdad llegó a verse (sendPair avisa también de los sueltos que nunca salieron).
    const live = [];
    let removed = 0;
    for (const m of log) {
      if (m.type === "idm-media") { if (!live.some((e) => e.url === m.entry.url)) live.push(m.entry); }
      else { const i = live.findIndex((e) => e.url === m.url); if (i >= 0) { live.splice(i, 1); removed++; } }
    }
    const got = live.map(describe);
    const shot = await c.send("Page.captureScreenshot", { format: "png" }, s);
    fs.writeFileSync(path.join(SHOTS, id + ".png"), Buffer.from(shot.data, "base64"));
    let pass;
    if (spec.want === null) pass = got.some((g) => g.startsWith("video:"));
    else pass = JSON.stringify(got.slice().sort()) === JSON.stringify(spec.want.slice().sort()) && (spec.flash || removed === 0);
    pass ? ok++ : bad++;
    console.log((pass ? "OK    " : "FALLA ") + id.padEnd(20) + " -> " + (got.length ? got.join(" | ") : "(nada)") +
      (removed ? "   [quitado " + removed + " por el camino]" : "") +
      (pass ? "" : "\n        esperado: " + (spec.want === null ? "algún vídeo" : spec.want.length ? spec.want.join(" | ") : "(nada)") +
                   "\n        mensajes: " + log.map((m) => m.type === "idm-media" ? "+" + describe(m.entry) : "-quitar").join(", ")));
    await c.send("Target.closeTarget", { targetId });
  }
  console.log(`\n${ok} correctos, ${bad} fallidos. Capturas: ${SHOTS}`);
  c.close();
  process.exit(bad ? 1 : 0);
})().catch((e) => { console.error(e); process.exit(2); });
