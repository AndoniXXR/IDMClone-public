// Prueba del bloque 4b en un navegador real (Edge/Brave) manejado por CDP:
//   - Supr + clic en un enlace: la descarga se queda en el navegador (tecla "para no descargar").
//   - Insert + clic: el enlace va a IDMClone aunque sea una página (tecla "para forzar"; el clic no se cancela: Chromium
//     abre la página y vuelve atrás en cuanto IDMClone la acepta).
//   - Formulario POST: IDMClone repite el envío y descarga lo que da el sitio.
//   - "Descargar todos los enlaces": IDMClone muestra la lista (dialogs.ps1 pulsa Aceptar).
//   node test4b.js <puerto> <carpeta de la prueba>
"use strict";
const { CDP, browserWs } = require("./cdp.js");
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const port = process.argv[2] || "9333";
const root = process.argv[3];
const EXT = path.resolve(__dirname, "..", "..", "extension");
const EXT_ID = "jolfcgooaockjhnalagfilpljkloallc";
const PAGE = "http://127.0.0.1:18765/page";
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let fails = 0;
const ok = (cond, msg) => { console.log((cond ? "   OK    " : "   FALLO ") + msg); if (!cond) fails++; };

function sha256(file) { return crypto.createHash("sha256").update(fs.readFileSync(file)).digest("hex"); }
function expectedSha(size) {
  const h = crypto.createHash("sha256"); const buf = Buffer.alloc(65536); let pos = 0n; const total = BigInt(size);
  while (pos < total) {
    const n = Number(total - pos < 65536n ? total - pos : 65536n);
    for (let k = 0; k < n; k++) { let x = BigInt.asUintN(64, (pos + BigInt(k)) * 0x9E3779B97F4A7C15n); x ^= x >> 29n; x ^= x >> 13n; buf[k] = Number(x & 0xFFn); }
    h.update(buf.subarray(0, n)); pos += BigInt(n);
  }
  return h.digest("hex");
}

(async () => {
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
  const errors = [];
  c.on((m) => { if (m.sessionId === swS && m.method === "Runtime.exceptionThrown") errors.push(JSON.stringify(m.params.exceptionDetails.exception && m.params.exceptionDetails.exception.description)); });
  const evalSw = async (expr) => (await c.send("Runtime.evaluate", { expression: expr, awaitPromise: true, returnByValue: true }, swS)).result.value;

  const cfg = await evalSw("getAppConfig().then(a => a && { prevent: a.preventKeys, force: a.forceKeys })");
  console.log("1) Teclas de IDMClone:", JSON.stringify(cfg));
  ok(cfg && cfg.prevent === "del" && cfg.force === "ins", "la extensión recibe las teclas especiales de IDMClone");

  const { targetId } = await c.send("Target.createTarget", { url: PAGE });
  const pg = (await c.send("Target.attachToTarget", { targetId, flatten: true })).sessionId;
  await c.send("Runtime.enable", {}, pg);
  await sleep(2000);
  const pos = async (id) => (await c.send("Runtime.evaluate", { expression: `(() => { const b = document.getElementById('${id}').getBoundingClientRect(); return [b.left + 10, b.top + b.height / 2]; })()`, returnByValue: true }, pg)).result.value;
  const click = async (id) => { const r = await pos(id); for (const type of ["mousePressed", "mouseReleased"]) await c.send("Input.dispatchMouseEvent", { type, x: r[0], y: r[1], button: "left", clickCount: 1 }, pg); };
  const key = async (type, k, vk) => c.send("Input.dispatchKeyEvent", { type, key: k, code: k, windowsVirtualKeyCode: vk, nativeVirtualKeyCode: vk }, pg);

  console.log("2) Supr + clic en paquete.zip (no descargar con IDMClone)");
  await key("rawKeyDown", "Delete", 46); await click("zip"); await key("keyUp", "Delete", 46);
  await sleep(6000);
  console.log("3) Insert + clic en «otra página» (forzar la descarga con IDMClone)");
  await key("rawKeyDown", "Insert", 45); await click("web"); await key("keyUp", "Insert", 45);
  await sleep(6000);
  const stillHere = (await c.send("Runtime.evaluate", { expression: "location.href", returnByValue: true }, pg)).result.value;
  ok(stillHere === PAGE, "con Insert el enlace se queda (o vuelve) en la página de partida (" + stillHere + ")");
  console.log("4) Clic en el botón del formulario (POST)");
  await click("enviar");
  await sleep(6000);
  console.log("5) «Descargar todos los enlaces con IDMClone»");
  const r = await evalSw(`chrome.tabs.query({}).then(ts => ts.find(t => t.url && t.url.startsWith('${PAGE}'))).then(tab => sendLinks(tab, false))`);
  console.log("   respuesta:", JSON.stringify(r));
  ok(r && r.ok && r.count >= 5, "IDMClone recibió los enlaces de la página");
  await sleep(8000); // dialogs.ps1 pulsa Aceptar en la lista

  const items = await evalSw("chrome.downloads.search({}).then(a => a.map(d => ({ name: d.filename.split(/[\\\\/]/).pop(), state: d.state, url: d.url })))");
  console.log("6) Navegador:", JSON.stringify(items.map((i) => i.name + " [" + i.state + "]")));
  ok(items.some((i) => i.name === "paquete.zip" && i.state === "complete"), "con Supr, paquete.zip se descargó en el navegador");
  ok(!items.some((i) => i.url.includes("/post/") || i.url.includes("informe-post")), "el formulario lo descargó IDMClone, no el navegador");

  const listFile = path.join(root, "idmclone", "downloads.json");
  let list = [];
  for (let i = 0; i < 40; i++) {
    try { list = JSON.parse(fs.readFileSync(listFile, "utf8").replace(/^﻿/, "")); } catch (e) { list = []; }
    const post = list.find((d) => path.basename(d.FilePath) === "informe-post.pdf");
    if (post && post.Status === 4 && list.length >= 5) break;
    await sleep(500);
  }
  console.log("7) IDMClone:");
  for (const d of list) console.log(`   ${path.basename(d.FilePath)}  estado=${d.Status}  cola=${d.Queue}`);
  const post = list.find((d) => path.basename(d.FilePath) === "informe-post.pdf");
  ok(post && post.Status === 4 && sha256(post.FilePath) === expectedSha(1048576), "formulario POST descargado e íntegro (SHA-256)");
  ok(list.some((d) => d.Url.includes("/page?otra=1")), "el enlace forzado con Insert llegó a IDMClone");
  const fromList = list.filter((d) => d.Queue === 0);
  const names = fromList.map((d) => path.basename(d.FilePath)).sort();
  ok(names.includes("paquete.zip") && names.includes("instalador.exe") && names.includes("manual.pdf"), "la lista añadió a la cola principal los tipos vigilados: " + names.join(", "));
  ok(!names.includes("imagen.png"), "la imagen (tipo no vigilado) no se marcó");
  ok(fromList.every((d) => d.Status === 0), "lo añadido a la cola espera a que se inicie la cola");
  ok(errors.length === 0, "sin errores en el service worker" + (errors.length ? ": " + errors.join(" | ") : ""));
  c.close();
  console.log(fails === 0 ? "\nTODO CORRECTO" : "\n" + fails + " FALLO(S)");
  process.exit(fails === 0 ? 0 : 1);
})().catch((e) => { console.error("ERROR:", e); process.exit(1); });
