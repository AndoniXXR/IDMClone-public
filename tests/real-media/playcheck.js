// Comprueba que un vídeo descargado se reproduce de verdad en Edge (sin interfaz, perfil temporal):
// duración, tamaño de imagen, bytes de vídeo y de audio decodificados, y un salto a la mitad.
//   node playcheck.js <archivo> [<archivo> ...]
"use strict";
const { spawn } = require("child_process");
const fs = require("fs"), os = require("os"), path = require("path");
const { CDP, browserWs } = require("../../browser/tests/real-browser/cdp.js");

const EDGE = "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";
const PORT = 9447;
const fileUrl = (p) => "file:///" + p.split(path.sep).join("/");

async function main() {
  const files = process.argv.slice(2).map((f) => path.resolve(f));
  const prof = fs.mkdtempSync(path.join(os.tmpdir(), "idmclone-play-"));
  const edge = spawn(EDGE, [`--user-data-dir=${prof}`, `--remote-debugging-port=${PORT}`, "--headless=new", "--no-first-run",
    "--autoplay-policy=no-user-gesture-required", "--allow-file-access-from-files", "--mute-audio", "about:blank"], { stdio: "ignore" });
  let ok = true;
  try {
    let ws = null;
    for (let i = 0; i < 50 && !ws; i++) { try { ws = await browserWs(PORT); } catch (e) { await new Promise((r) => setTimeout(r, 200)); } }
    const c = await CDP.connect(ws);
    const { targetId } = await c.send("Target.createTarget", { url: "about:blank" });
    const { sessionId } = await c.send("Target.attachToTarget", { targetId, flatten: true });
    for (const f of files) {
      const page = fileUrl(path.join(__dirname, "check.html")) + "?src=" + encodeURIComponent(fileUrl(f));
      await c.send("Page.navigate", { url: page }, sessionId);
      let r = null;
      for (let i = 0; i < 120 && !r; i++) {
        await new Promise((res) => setTimeout(res, 250));
        const v = await c.send("Runtime.evaluate", { expression: "JSON.stringify(window.result || null)", returnByValue: true }, sessionId);
        r = JSON.parse(v.result.value || "null");
      }
      const good = !!r && !r.error && r.duration > 0 && r.decodedVideo > 0 && r.seekOk && r.decodedAudio > 0;
      if (!good) ok = false;
      console.log((good ? "OK   " : "MAL  ") + path.basename(f) + "  " + JSON.stringify(r));
    }
    c.close();
  } finally {
    edge.kill();
    await new Promise((r) => setTimeout(r, 800));
    try { fs.rmSync(prof, { recursive: true, force: true }); } catch (e) {}
  }
  process.exit(ok ? 0 : 1);
}
main().catch((e) => { console.error(e); process.exit(2); });
