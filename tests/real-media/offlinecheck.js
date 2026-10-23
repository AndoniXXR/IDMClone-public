// Comprueba en Edge (sin ventana, sin red) que una página guardada por el Grabber para navegar sin
// conexión se ve bien: imágenes cargadas, hojas de estilo aplicadas y enlaces a archivos locales.
//   node offlinecheck.js <index.html>
"use strict";
const { spawn } = require("child_process");
const fs = require("fs"), os = require("os"), path = require("path");
const { CDP, browserWs } = require("../../browser/tests/real-browser/cdp.js");

const EDGE = "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";
const PORT = 9448;

(async () => {
  const file = path.resolve(process.argv[2]);
  const prof = fs.mkdtempSync(path.join(os.tmpdir(), "idmclone-offline-"));
  const edge = spawn(EDGE, [`--user-data-dir=${prof}`, `--remote-debugging-port=${PORT}`, "--headless=new", "--no-first-run",
    "--allow-file-access-from-files", "about:blank"], { stdio: "ignore" });
  let code = 1;
  try {
    let ws = null;
    for (let i = 0; i < 50 && !ws; i++) { try { ws = await browserWs(PORT); } catch (e) { await new Promise((r) => setTimeout(r, 200)); } }
    const c = await CDP.connect(ws);
    const { targetId } = await c.send("Target.createTarget", { url: "about:blank" });
    const { sessionId: s } = await c.send("Target.attachToTarget", { targetId, flatten: true });
    await c.send("Network.enable", {}, s);
    const remote = [];
    c.on((m) => { if (m.sessionId === s && m.method === "Network.requestWillBeSent" && /^https?:/.test(m.params.request.url)) remote.push(m.params.request.url); });
    // Sin red: lo que no esté en disco no carga.
    await c.send("Network.emulateNetworkConditions", { offline: true, latency: 0, downloadThroughput: -1, uploadThroughput: -1 }, s);
    await c.send("Page.enable", {}, s);
    await c.send("Page.navigate", { url: "file:///" + file.split(path.sep).join("/") }, s);
    await new Promise((r) => setTimeout(r, 3000));
    const r = (await c.send("Runtime.evaluate", { returnByValue: true, expression: `(() => {
      const imgs = [...document.images];
      const sheets = [...document.styleSheets].filter(x => x.href);
      let rules = 0; for (const x of sheets) { try { rules += x.cssRules.length; } catch (e) {} }
      const links = [...document.querySelectorAll('a[href]')].map(a => a.getAttribute('href'));
      return { title: document.title, images: imgs.length, loaded: imgs.filter(i => i.complete && i.naturalWidth > 0).length,
               stylesheets: sheets.length, cssRules: rules, bodyFont: getComputedStyle(document.body).fontFamily,
               localLinks: links.filter(h => !/^[a-z]+:/i.test(h) && !h.startsWith('#')).length,
               internetLinks: links.filter(h => /^https?:/i.test(h)).length };
    })()` }, s)).result.value;
    console.log(JSON.stringify(r, null, 1));
    console.log("peticiones a internet: " + remote.length);
    const ok = r.loaded === r.images && r.stylesheets > 0 && r.cssRules > 0;
    console.log(ok ? "OK: la página se ve sin conexión (imágenes y estilos desde el disco)" : "MAL");
    code = ok ? 0 : 1;
    c.close();
  } finally {
    edge.kill();
    await new Promise((r) => setTimeout(r, 800));
    try { fs.rmSync(prof, { recursive: true, force: true }); } catch (e) {}
  }
  process.exit(code);
})().catch((e) => { console.error(e); process.exit(2); });
