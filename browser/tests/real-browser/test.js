// Prueba de la extensión de IDMClone en un navegador real (Edge/Brave) manejado por CDP.
//   node test.js <puerto> <carpeta de la prueba> [normal|rechazo]
"use strict";
const { CDP, browserWs } = require("./cdp.js");
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const port = process.argv[2] || "9333";
const root = process.argv[3];
const mode = process.argv[4] || "normal";
const EXT = path.resolve(__dirname, "..", "..", "extension");
const EXT_ID = "jolfcgooaockjhnalagfilpljkloallc";
const PAGE = "http://127.0.0.1:18765/page";
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function sha256(file) { return crypto.createHash("sha256").update(fs.readFileSync(file)).digest("hex"); }
function expectedSha(size) { // mismo contenido que TestServer.ByteAt (versión 1)
  const h = crypto.createHash("sha256"); const buf = Buffer.alloc(65536); let pos = 0n; const total = BigInt(size);
  while (pos < total) {
    const n = Number(total - pos < 65536n ? total - pos : 65536n);
    for (let k = 0; k < n; k++) { let x = BigInt.asUintN(64, (pos + BigInt(k)) * 0x9E3779B97F4A7C15n); x ^= x >> 29n; x ^= x >> 13n; buf[k] = Number(x & 0xFFn); }
    h.update(buf.subarray(0, n)); pos += BigInt(n);
  }
  return h.digest("hex");
}
const SIZES = { "paquete.zip": 3145728, "informe final €.pdf": 1048576, "instalador.exe": 2097152, "imagen.png": 20480 };

(async () => {
  const c = await CDP.connect(await browserWs(port));
  const log = (...a) => console.log(...a);
  log("Navegador:", (await c.send("Browser.getVersion")).product);

  // --- 0) Cargar la extensión (sin interfaz: Extensions.loadUnpacked) ---
  let sw = (await c.send("Target.getTargets")).targetInfos.find((t) => t.type === "service_worker" && t.url.includes(EXT_ID));
  if (!sw) {
    const r = await c.send("Extensions.loadUnpacked", { path: EXT });
    log("0) Extensión cargada, id:", r.id, r.id === EXT_ID ? "(el esperado)" : "(DISTINTO del esperado)");
    for (let i = 0; i < 20 && !sw; i++) { await sleep(250); sw = (await c.send("Target.getTargets")).targetInfos.find((t) => t.type === "service_worker" && t.url.includes(EXT_ID)); }
  }
  if (!sw) { log("FALLO: el service worker de la extensión no arrancó"); process.exit(1); }
  const swS = (await c.send("Target.attachToTarget", { targetId: sw.targetId, flatten: true })).sessionId;
  const swLogs = [];
  c.on((m) => {
    if (m.sessionId !== swS) return;
    if (m.method === "Runtime.exceptionThrown") swLogs.push("EXCEPCIÓN: " + JSON.stringify(m.params.exceptionDetails.exception && m.params.exceptionDetails.exception.description));
    if (m.method === "Runtime.consoleAPICalled" && (m.params.type === "error" || m.params.type === "warning")) swLogs.push(m.params.type + ": " + m.params.args.map((a) => a.value || a.description).join(" "));
  });
  await c.send("Runtime.enable", {}, swS);
  const evalSw = async (expr) => (await c.send("Runtime.evaluate", { expression: expr, awaitPromise: true, returnByValue: true }, swS)).result.value;
  // Registro de lo que hace la extensión con cada descarga (sin cambiar su comportamiento).
  await evalSw(`self.__t = []; for (const f of ['pause','resume','cancel','erase','removeFile']) { const o = chrome.downloads[f].bind(chrome.downloads);
      chrome.downloads[f] = async (a) => { self.__t.push(f + '#' + (a && a.id !== undefined ? a.id : a)); return o(a); }; } 'ok'`);

  log("1) Probar conexión:", JSON.stringify(await evalSw("hostRequest({type:'ping'}, 8000)")));
  log("   Tipos de archivo:", JSON.stringify(await evalSw("getSettings().then(s => ({ deIDMClone: !!s.fromApp, tipos: s.exts.length, png: s.exts.includes('png'), zip: s.exts.includes('zip') }))")));

  // --- 2) Página de pruebas y clics ---
  const { targetId } = await c.send("Target.createTarget", { url: PAGE });
  const pg = (await c.send("Target.attachToTarget", { targetId, flatten: true })).sessionId;
  await c.send("Runtime.enable", {}, pg);
  await sleep(1500);
  log("2) Página:", (await c.send("Runtime.evaluate", { expression: "document.title", returnByValue: true }, pg)).result.value);
  const links = mode === "normal" ? ["zip", "php", "png", "exe"] : ["zip"];
  for (const id of links) {
    // Clic de ratón real (con gesto de usuario): un .click() por script no cuenta y el navegador
    // bloquea las descargas automáticas a partir de la segunda.
    const r = (await c.send("Runtime.evaluate", { expression: `(() => { const b = document.getElementById('${id}').getBoundingClientRect(); return [b.left + 10, b.top + b.height / 2]; })()`, returnByValue: true }, pg)).result.value;
    for (const type of ["mousePressed", "mouseReleased"])
      await c.send("Input.dispatchMouseEvent", { type, x: r[0], y: r[1], button: "left", clickCount: 1 }, pg);
    log("   clic en", id);
    await sleep(id === "zip" ? 5000 : 2500);
  }
  await sleep(mode === "rechazo" ? 8000 : 5000);

  // --- 3) Navegador ---
  const items = await evalSw("chrome.downloads.search({}).then(a => a.map(d => d.filename.split(/[\\\\/]/).pop() + ' [' + d.state + (d.paused ? ', pausada' : '') + ']'))");
  log("3) Descargas que quedan en el navegador:", JSON.stringify(items));
  log("   Llamadas de la extensión (id = descarga):", (await evalSw("self.__t")).join(", "));
  const bdl = path.join(root, "descargas-navegador");
  const files = fs.existsSync(bdl) ? fs.readdirSync(bdl) : [];
  log("   Archivos en la carpeta del navegador:", JSON.stringify(files));
  for (const f of files) if (SIZES[f]) log("     " + f + ": " + (sha256(path.join(bdl, f)) === expectedSha(SIZES[f]) ? "completo y correcto" : "INCOMPLETO/CORRUPTO"));

  // --- 4) IDMClone ---
  if (mode !== "rechazo") {
    const listFile = path.join(root, "idmclone", "downloads.json");
    let list = [];
    for (let i = 0; i < 40; i++) {
      try { list = JSON.parse(fs.readFileSync(listFile, "utf8").replace(/^\uFEFF/, "")); } catch (e) { list = []; }
      if (list.length && list.every((d) => d.Status === 4 || d.Status === 5)) break;
      await sleep(500);
    }
    log("4) Lo que recibió IDMClone:");
    for (const d of list) {
      const name = path.basename(d.FilePath);
      const st = ["En cola", "Conectando", "Descargando", "Pausado", "Completado", "Error"][d.Status];
      const integ = d.Status === 4 && SIZES[name] ? (sha256(d.FilePath) === expectedSha(SIZES[name]) ? "SHA-256 correcto" : "CORRUPTO") : "";
      log(`   ${name}  [${st}]  ${integ}  referer=${d.Referrer || "-"}`);
    }
  }
  log("5) Errores del service worker:", swLogs.length ? "\n   " + swLogs.join("\n   ") : "ninguno");
  c.close();
})().catch((e) => { console.error("ERROR:", e); process.exit(1); });
