// Panel "Descargar este video" en un navegador real (Edge/Brave) manejado por CDP, con reproductores reales:
//   - demo de hls.js (HLS de test-streams.mux.dev), reproductor de referencia de DASH-IF (Akamai) y un <video> MP4.
// Para cada uno: espera al panel encima del reproductor, abre la lista de calidades, elige la más ligera
// y comprueba que IDMClone la baja, la une y que el archivo se reproduce (playcheck.js).
//   node testvideo.js <puerto> <carpeta de la prueba>
"use strict";
const { CDP, browserWs } = require("./cdp.js");
const fs = require("fs");
const path = require("path");
const { execFileSync } = require("child_process");

const port = process.argv[2] || "9333";
const root = process.argv[3];
const EXT = path.resolve(__dirname, "..", "..", "extension");
const EXT_ID = "jolfcgooaockjhnalagfilpljkloallc";
const shots = path.join(process.env.TEMP, "idmclone-video-" + port);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let fails = 0;
const ok = (cond, msg) => { console.log((cond ? "   OK    " : "   FALLO ") + msg); if (!cond) fails++; };

const PAGES = [
  { name: "HLS (hls.js)", url: "https://hlsjs.video-dev.org/demo/?src=" + encodeURIComponent("https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8"), expect: "MP4" },
  { name: "DASH (dash.js)", url: "https://reference.dashif.org/dash.js/latest/samples/dash-if-reference-player/index.html?url=" +
      encodeURIComponent("https://dash.akamaized.net/akamai/bbb_30fps/bbb_30fps.mpd") + "&autoLoad=true&muted=true", expect: "MP4" },
  { name: "MP4 directo", url: "http://127.0.0.1:18765/video?src=" + encodeURIComponent("https://www.learningcontainer.com/wp-content/uploads/2020/05/sample-mp4-file.mp4"), expect: "MP4" },
];

function completed() {
  const f = path.join(root, "idmclone", "downloads.json");
  if (!fs.existsSync(f)) return [];
  try { return JSON.parse(fs.readFileSync(f, "utf8")).filter((d) => d.Status === 4); } catch (e) { return []; }
}

(async () => {
  fs.mkdirSync(shots, { recursive: true });
  const c = await CDP.connect(await browserWs(port));
  console.log("Navegador:", (await c.send("Browser.getVersion")).product);
  let sw = (await c.send("Target.getTargets")).targetInfos.find((t) => t.type === "service_worker" && t.url.includes(EXT_ID));
  if (!sw) {
    await c.send("Extensions.loadUnpacked", { path: EXT });
    for (let i = 0; i < 20 && !sw; i++) { await sleep(250); sw = (await c.send("Target.getTargets")).targetInfos.find((t) => t.type === "service_worker" && t.url.includes(EXT_ID)); }
  }
  if (!sw) { console.log("FALLO: el service worker no arrancó"); process.exit(1); }
  const swS = (await c.send("Target.attachToTarget", { targetId: sw.targetId, flatten: true })).sessionId;
  await c.send("Runtime.enable", {}, swS);
  const cfg = (await c.send("Runtime.evaluate", { expression: "getAppConfig().then(a => a && { panel: a.mediaPanel, minVideoKB: a.minVideoKB })", awaitPromise: true, returnByValue: true }, swS)).result.value;
  ok(cfg && cfg.panel === true, "la extensión sabe que el panel está activado en IDMClone " + JSON.stringify(cfg));

  let step = 0;
  for (const pg of PAGES) {
    step++;
    console.log(`${step}) ${pg.name}: ${pg.url}`);
    const before = completed().length;
    const { targetId } = await c.send("Target.createTarget", { url: "about:blank" });
    const s = (await c.send("Target.attachToTarget", { targetId, flatten: true })).sessionId;
    await c.send("Page.enable", {}, s);
    await c.send("Page.navigate", { url: pg.url }, s);
    const ev = async (expr) => (await c.send("Runtime.evaluate", { expression: expr, awaitPromise: true, returnByValue: true }, s)).result.value;
    // Que el reproductor arranque (sin sonido) por si la página no lo hace sola.
    let panel = null;
    for (let i = 0; i < 80 && !panel; i++) {
      await sleep(500);
      if (i % 6 === 2) await ev("(() => { const v = document.querySelector('video'); if (v) { v.muted = true; v.play().catch(() => {}); } })()");
      panel = await ev("(() => { const p = document.querySelector('idmclone-panel'); if (!p) return null; const r = p.getBoundingClientRect(); const v = document.querySelector('video'); const vr = v && v.getBoundingClientRect(); return { text: p.shadowRoot.querySelector('.main').textContent, x: r.x, y: r.y, w: r.width, h: r.height, overVideo: !!vr && r.y >= vr.y - 2 && r.y < vr.y + 60 && r.x + r.width <= vr.right + 2 && r.x >= vr.x }; })()");
    }
    ok(!!panel, "aparece el panel" + (panel ? ": «" + panel.text.trim() + "»" + (panel.overVideo ? ", encima del reproductor (arriba a la derecha)" : " (fuera del reproductor)") : ""));
    if (!panel) { await c.send("Target.closeTarget", { targetId }); continue; }
    ok(panel.text.indexOf("Descargar este video") >= 0, "texto del panel como el de IDM");
    await sleep(1500); // por si llega la lista maestra después de la de una calidad
    const seen = (await c.send("Runtime.evaluate", { expression: "JSON.stringify([...tabMedia.values()].map(v => ({ items: [...v.items.entries()].map(([k, e]) => k.slice(-45) + ' -> ' + (e ? e.items.length + ' opciones' : 'pendiente')), children: v.children.size })))", returnByValue: true }, swS)).result.value;
    console.log("      detectado por la extensión: " + seen);
    const shot = async (name) => { const r = await c.send("Page.captureScreenshot", { format: "png" }, s); fs.writeFileSync(path.join(shots, step + "-" + name + ".png"), Buffer.from(r.data, "base64")); };
    await shot("panel");
    // Abrir la lista (o descargar directamente si solo hay una opción).
    await ev("document.querySelector('idmclone-panel').shadowRoot.querySelector('.main').click()");
    await sleep(700);
    const items = await ev("[...document.querySelector('idmclone-panel').shadowRoot.querySelectorAll('.menu:not([hidden]) .item')].map(b => b.textContent)");
    let chosen = "(única)";
    if (items && items.length) {
      console.log("      calidades: " + items.join(" | "));
      await shot("calidades");
      // La de menor resolución (las calidades van de mayor a menor; la última de vídeo).
      const idx = items.map((t, i) => [t, i]).filter(([t]) => /^(MP4|WEBM) \d+x\d+/.test(t)).map(([, i]) => i).pop();
      ok(idx !== undefined, "la lista ofrece calidades con su resolución");
      chosen = items[idx];
      await ev(`document.querySelector('idmclone-panel').shadowRoot.querySelectorAll('.menu .item')[${idx}].click()`);
    }
    console.log("      elegida: " + chosen);
    await sleep(1500);
    const st = await ev("(() => { const n = document.querySelector('idmclone-panel').shadowRoot.querySelector('.status'); return n ? n.className + ': ' + n.textContent : null; })()");
    ok(st && st.indexOf("ok") >= 0, "el panel avisa de que IDMClone la recibió: " + st);
    // IDMClone baja, une y deja el archivo.
    let done = null;
    for (let i = 0; i < 360 && !done; i++) { await sleep(500); const l = completed(); if (l.length > before) done = l[l.length - 1]; }
    ok(!!done, "IDMClone terminó la descarga" + (done ? ": " + path.basename(done.FilePath) + " (" + (done.Size / 1048576).toFixed(1) + " MB)" : ""));
    if (done) {
      ok(/\.mp4$/i.test(done.FilePath) && fs.existsSync(done.FilePath), "archivo .mp4 en la carpeta de descargas");
      try {
        const out = execFileSync("node", [path.resolve(__dirname, "..", "..", "..", "tests", "real-media", "playcheck.js"), done.FilePath], { encoding: "utf8" });
        console.log("      " + out.trim());
        ok(out.startsWith("OK"), "se reproduce (vídeo y audio) y se puede saltar");
      } catch (e) { ok(false, "se reproduce: " + (e.stdout || e.message)); }
    }
    await c.send("Target.closeTarget", { targetId });
  }
  c.close();
  console.log(fails === 0 ? "\nTODO BIEN" : `\n${fails} FALLOS`);
  console.log("Capturas en " + shots);
  process.exit(fails ? 1 : 0);
})().catch((e) => { console.error(e); process.exit(2); });
