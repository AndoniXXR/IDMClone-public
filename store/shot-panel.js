// Captura de 1280x800 para las tiendas: el panel "Descargar este video" con la lista de calidades abierta,
// sobre store\demo\video.html, en un navegador aparte (start.ps1) con la extensión cargada.
//   node store\shot-panel.js <puerto de depuración> <salida.png>
"use strict";
const http = require("http");
const fs = require("fs");
const path = require("path");
const { CDP, browserWs } = require(path.join(__dirname, "..", "browser", "tests", "real-browser", "cdp.js"));
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

(async () => {
  const port = process.argv[2], out = process.argv[3];
  const page = fs.readFileSync(path.join(__dirname, "demo", "video.html"));
  const server = http.createServer((q, r) => { r.writeHead(200, { "Content-Type": "text/html; charset=utf-8" }); r.end(page); }).listen(0, "127.0.0.1");
  await new Promise((r) => server.once("listening", r));
  const url = "http://127.0.0.1:" + server.address().port + "/video.html";

  const c = await CDP.connect(await browserWs(port));
  const { targetId } = await c.send("Target.createTarget", { url: "about:blank" });
  const s = (await c.send("Target.attachToTarget", { targetId, flatten: true })).sessionId;
  await c.send("Page.enable", {}, s);
  await c.send("Emulation.setDeviceMetricsOverride", { width: 1280, height: 800, deviceScaleFactor: 1, mobile: false }, s);
  await c.send("Page.navigate", { url }, s);
  const ev = async (e) => (await c.send("Runtime.evaluate", { expression: e, awaitPromise: true, returnByValue: true }, s)).result.value;
  let ok = false;
  for (let i = 0; i < 60 && !ok; i++) { await sleep(500); ok = await ev("!!document.querySelector('idmclone-panel')"); }
  if (!ok) { console.log("no apareció el panel"); process.exit(1); }
  await sleep(3000); // que llegue la lista maestra con todas las calidades y avance un poco el vídeo
  await ev("document.querySelector('idmclone-panel').shadowRoot.querySelector('.main').click()");
  await sleep(800);
  const r = await c.send("Page.captureScreenshot", { format: "png" }, s);
  fs.writeFileSync(out, Buffer.from(r.data, "base64"));
  console.log("OK " + out);
  try { await c.send("Browser.close"); } catch (e) { }
  c.close(); server.close();
  process.exit(0);
})().catch((e) => { console.error(e); process.exit(2); });
