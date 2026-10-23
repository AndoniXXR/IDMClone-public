// Panel "Descargar este video" en un navegador real: emisión en directo (se graba y se detiene) y un
// reproductor que pide vídeo y audio a trozos (Range) de archivos aparte, como Facebook o Instagram.
//   node testlive.js <puerto> <carpeta de la prueba>
"use strict";
const { CDP, browserWs } = require("./cdp.js");
const fs = require("fs"), path = require("path"), http = require("http");
const { execFileSync } = require("child_process");

const port = process.argv[2] || "9333";
const root = process.argv[3];
const EXT = path.resolve(__dirname, "..", "..", "extension");
const EXT_ID = "jolfcgooaockjhnalagfilpljkloallc";
const shots = path.join(process.env.TEMP, "idmclone-live-" + port);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let fails = 0;
const ok = (cond, msg) => { console.log((cond ? "   OK    " : "   FALLO ") + msg); if (!cond) fails++; };
const LIVE = "https://demo.unified-streaming.com/k8s/live/stable/live.isml/.m3u8";

function completed() {
  const f = path.join(root, "idmclone", "downloads.json");
  try { return JSON.parse(fs.readFileSync(f, "utf8")).filter((d) => d.Status === 4); } catch (e) { return []; }
}

// Vídeo y audio reales (primeros trozos del DASH de Akamai) unidos en dos archivos MP4.
async function fetchBuf(u) { const r = await fetch(u); if (!r.ok) throw new Error(u + " " + r.status); return Buffer.from(await r.arrayBuffer()); }
async function makeFiles() {
  const base = "https://dash.akamaized.net/akamai/bbb_30fps/";
  const v = [await fetchBuf(base + "bbb_30fps_640x360_800k/bbb_30fps_640x360_800k_0.m4v")];
  const a = [await fetchBuf(base + "bbb_a64k/bbb_a64k_0.m4a")];
  for (let i = 1; i <= 8; i++) { v.push(await fetchBuf(base + `bbb_30fps_640x360_800k/bbb_30fps_640x360_800k_${i}.m4v`)); a.push(await fetchBuf(base + `bbb_a64k/bbb_a64k_${i}.m4a`)); }
  return { "/media/video_360.mp4": Buffer.concat(v), "/media/audio_64.mp4": Buffer.concat(a) };
}

function serve(files) {
  const page = `<!doctype html><meta charset="utf-8"><title>Clip de red social</title><h1>Reproductor por trozos</h1>
    <video width="640" height="360" controls muted></video>
    <script>
      // Como los reproductores de las redes sociales: pide trozos de cada archivo con Range.
      async function piece(u, a, b) { const r = await fetch(u, { headers: { Range: "bytes=" + a + "-" + b } }); return r.arrayBuffer(); }
      (async () => { await piece("/media/video_360.mp4", 0, 99999); await piece("/media/audio_64.mp4", 0, 49999); await piece("/media/video_360.mp4", 100000, 199999); })();
    </script>`;
  const srv = http.createServer((req, res) => {
    if (req.url === "/" ) { res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" }); res.end(page); return; }
    const f = files[req.url.split("?")[0]];
    if (!f) { res.writeHead(404); res.end(); return; }
    const type = req.url.includes("audio") ? "audio/mp4" : "video/mp4";
    const m = /bytes=(\d+)-(\d*)/.exec(req.headers.range || "");
    if (m) {
      const a = +m[1], b = m[2] ? Math.min(+m[2], f.length - 1) : f.length - 1;
      res.writeHead(206, { "Content-Type": type, "Content-Length": b - a + 1, "Content-Range": `bytes ${a}-${b}/${f.length}`, "Accept-Ranges": "bytes" });
      res.end(f.subarray(a, b + 1));
    } else { res.writeHead(200, { "Content-Type": type, "Content-Length": f.length, "Accept-Ranges": "bytes" }); res.end(f); }
  });
  return new Promise((r) => srv.listen(18766, "127.0.0.1", () => r(srv)));
}

(async () => {
  fs.mkdirSync(shots, { recursive: true });
  const files = await makeFiles();
  const srv = await serve(files);
  const c = await CDP.connect(await browserWs(port));
  console.log("Navegador:", (await c.send("Browser.getVersion")).product);
  let sw = (await c.send("Target.getTargets")).targetInfos.find((t) => t.type === "service_worker" && t.url.includes(EXT_ID));
  if (!sw) {
    await c.send("Extensions.loadUnpacked", { path: EXT });
    for (let i = 0; i < 20 && !sw; i++) { await sleep(250); sw = (await c.send("Target.getTargets")).targetInfos.find((t) => t.type === "service_worker" && t.url.includes(EXT_ID)); }
  }
  const swS = (await c.send("Target.attachToTarget", { targetId: sw.targetId, flatten: true })).sessionId;
  await c.send("Runtime.enable", {}, swS);
  const evalSw = async (expr) => (await c.send("Runtime.evaluate", { expression: expr, awaitPromise: true, returnByValue: true }, swS)).result.value;

  async function openPanel(url, name) {
    const { targetId } = await c.send("Target.createTarget", { url: "about:blank" });
    const s = (await c.send("Target.attachToTarget", { targetId, flatten: true })).sessionId;
    await c.send("Page.enable", {}, s);
    await c.send("Page.navigate", { url }, s);
    const ev = async (expr) => (await c.send("Runtime.evaluate", { expression: expr, awaitPromise: true, returnByValue: true }, s)).result.value;
    let panel = null;
    for (let i = 0; i < 60 && !panel; i++) {
      await sleep(500);
      if (i % 6 === 2) await ev("(() => { const v = document.querySelector('video'); if (v) { v.muted = true; v.play().catch(() => {}); } })()");
      panel = await ev("(() => { const p = document.querySelector('idmclone-panel'); return p ? p.shadowRoot.querySelector('.main').textContent : null; })()");
    }
    await sleep(1500);
    await ev("document.querySelector('idmclone-panel') && document.querySelector('idmclone-panel').shadowRoot.querySelector('.main').click()");
    await sleep(700);
    const head = await ev("[...(document.querySelector('idmclone-panel')||{shadowRoot:document}).shadowRoot.querySelectorAll('.menu:not([hidden]) .head')].map(h => h.textContent)");
    const items = await ev("[...(document.querySelector('idmclone-panel')||{shadowRoot:document}).shadowRoot.querySelectorAll('.menu:not([hidden]) .item')].map(b => b.textContent)");
    const r = await c.send("Page.captureScreenshot", { format: "png" }, s);
    fs.writeFileSync(path.join(shots, name + ".png"), Buffer.from(r.data, "base64"));
    return { targetId, ev, panel, head, items };
  }

  // 1) Directo: se ofrece, se graba unos segundos y "Detener todas" lo termina.
  console.log("1) Emisión en directo (hls.js): " + LIVE);
  let before = completed().length;
  const live = await openPanel("https://hlsjs.video-dev.org/demo/?src=" + encodeURIComponent(LIVE), "1-directo");
  ok(!!live.panel, "aparece el panel sobre el directo");
  ok(live.head && live.head.some((h) => h.includes("en directo")), "la lista dice que es en directo: " + JSON.stringify(live.head));
  const idx = live.items ? live.items.map((t, i) => [t, i]).filter(([t]) => /^MP4 \d+x\d+/.test(t)).map(([, i]) => i).pop() : undefined;
  ok(idx !== undefined, "calidades: " + JSON.stringify(live.items));
  if (idx !== undefined) {
    await live.ev(`document.querySelector('idmclone-panel').shadowRoot.querySelectorAll('.menu .item')[${idx}].click()`);
    await sleep(15000);
    const r = await evalSw("hostRequest({ type: 'stopall' }, 10000)");
    ok(r && r.ok, "detener todas");
    let done = null;
    for (let i = 0; i < 60 && !done; i++) { await sleep(500); const l = completed(); if (l.length > before) done = l[l.length - 1]; }
    ok(!!done, "la grabación terminó y se guardó" + (done ? ": " + path.basename(done.FilePath) + " (" + (done.Size / 1048576).toFixed(1) + " MB)" : ""));
    if (done) {
      const out = execFileSync("node", [path.resolve(__dirname, "..", "..", "..", "tests", "real-media", "playcheck.js"), done.FilePath], { encoding: "utf8" });
      console.log("      " + out.trim());
      const dur = JSON.parse(out.substring(out.indexOf("{"))).duration;
      ok(out.startsWith("OK") && dur > 12 && dur < 60, "se reproduce y dura lo grabado (" + dur + " s)");
    }
  }
  await c.send("Target.closeTarget", { targetId: live.targetId });

  // 2) Vídeo y audio pedidos a trozos de archivos aparte: un elemento que se baja y se mezcla.
  console.log("2) Reproductor que pide vídeo y audio a trozos (http://127.0.0.1:18766/)");
  before = completed().length;
  const pair = await openPanel("http://127.0.0.1:18766/", "2-trozos");
  ok(!!pair.panel, "aparece el panel");
  const tabState = await evalSw("JSON.stringify([...tabMedia.values()].map(v => [...v.items.values()].filter(Boolean).map(e => e.kind + ':' + e.items.length)))");
  console.log("      detectado: " + tabState + "  opciones: " + JSON.stringify(pair.items));
  ok(tabState.includes("pair:1"), "vídeo y audio juntos en un solo elemento");
  // Una sola opción (el par): el botón ya lo descarga; si hay lista, se elige la primera.
  if (pair.items && pair.items.length > 1) await pair.ev("document.querySelector('idmclone-panel').shadowRoot.querySelectorAll('.menu .item')[0].click()");
  let done2 = null;
  for (let i = 0; i < 60 && !done2; i++) { await sleep(500); const l = completed(); if (l.length > before) done2 = l[l.length - 1]; }
  ok(!!done2, "IDMClone bajó los dos archivos y los unió" + (done2 ? ": " + path.basename(done2.FilePath) : ""));
  if (done2) {
    const out = execFileSync("node", [path.resolve(__dirname, "..", "..", "..", "tests", "real-media", "playcheck.js"), done2.FilePath], { encoding: "utf8" });
    console.log("      " + out.trim());
    ok(out.startsWith("OK"), "se reproduce con vídeo y audio");
  }
  await c.send("Target.closeTarget", { targetId: pair.targetId });
  srv.close();
  c.close();
  console.log(fails === 0 ? "\nTODO BIEN" : `\n${fails} FALLOS`);
  console.log("Capturas en " + shots);
  process.exit(fails ? 1 : 0);
})().catch((e) => { console.error(e); process.exit(2); });
