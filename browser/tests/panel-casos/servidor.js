// Página de pruebas a mano del panel "Descargar este video": un caso por página, con lo que debería pasar.
//   "C:\Program Files\nodejs\node.exe" browser\tests\panel-casos\servidor.js   ->   http://127.0.0.1:18790/
// Sirve listas HLS/DASH, archivos pedidos a trozos (206) y archivos enteros con las cabeceras de un sitio real; los
// trozos de vídeo son relleno: se prueba qué detecta la extensión (y qué calidades da IDMClone), no la descarga.
"use strict";
const http = require("node:http");

const PORT = Number(process.env.PORT) || 18790;
const MB = 1048576;

// ------------------------------------------------------------------ listas y manifiestos

// Lista de un flujo: n trozos de 4 s.
function media(seconds, seg, init) {
  const n = Math.max(1, Math.round(seconds / 4));
  let s = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:4\n#EXT-X-PLAYLIST-TYPE:VOD\n";
  if (init) s += '#EXT-X-MAP:URI="' + init + '"\n';
  for (let i = 0; i < n; i++) s += "#EXTINF:4.000,\n" + seg.replace("{i}", i) + "\n";
  return s + "#EXT-X-ENDLIST\n";
}

// Como la maestra de X (video.twimg.com): audio aparte por grupos (sin BANDWIDTH) y calidades avc1 con RESOLUTION.
function xMaster(base, variants) {
  let s = "#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-INDEPENDENT-SEGMENTS\n";
  s += '#EXT-X-MEDIA:NAME="Audio",TYPE=AUDIO,GROUP-ID="audio-128000",AUTOSELECT=YES,URI="' + base + '/pl/mp4a/128000/audio.m3u8"\n';
  for (const v of variants)
    s += "#EXT-X-STREAM-INF:AVERAGE-BANDWIDTH=" + Math.round(v.bw * 0.9) + ",BANDWIDTH=" + v.bw + ",RESOLUTION=" + v.res +
         ',CODECS="mp4a.40.2,avc1.4D401E",AUDIO="audio-128000"\n' + base + "/pl/avc1/" + v.res + "/video.m3u8\n";
  return s;
}

// Lista de cada caso: ruta -> texto. Las rutas de los trozos (.m4s/.ts/.aac) devuelven relleno.
const LISTS = {
  // 1. X normal: 60 s con 720p/480p/270p.
  "/hls/x-normal/master.m3u8": xMaster("/hls/x-normal", [{ bw: 2176000, res: "1280x720" }, { bw: 832000, res: "640x360" }, { bw: 256000, res: "480x270" }]),
  // 2. X a baja resolución: 20 s solo en 270p y 180p (todas las calidades < 1 MB). Antes: "solo audio".
  "/hls/x-baja/master.m3u8": xMaster("/hls/x-baja", [{ bw: 256000, res: "480x270" }, { bw: 180000, res: "320x180" }]),
  // 3. Solo audio de verdad (podcast/radio en HLS): una calidad mp4a sin RESOLUTION.
  "/hls/solo-audio/master.m3u8": '#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=128000,CODECS="mp4a.40.2"\n/hls/solo-audio/a.m3u8\n',
  // 4. Códecs que IDMClone no conocía (sin RESOLUTION): mp4v y vp8. Antes: "solo audio".
  "/hls/codecs/master.m3u8": '#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000,CODECS="mp4v.20.9,mp4a.40.2"\n/hls/codecs/mp4v.m3u8\n' +
                             '#EXT-X-STREAM-INF:BANDWIDTH=600000,CODECS="vp8,vorbis"\n/hls/codecs/vp8.m3u8\n',
  // 5. Lista suelta de audio (.aac) sin maestra. Antes: un "vídeo" sin resolución.
  "/hls/suelta-audio/audio.m3u8": media(60, "/hls/suelta-audio/s{i}.aac"),
  // 6. Lista suelta de vídeo (.ts) sin maestra: sigue siendo un vídeo.
  "/hls/suelta-video/video.m3u8": media(60, "/hls/suelta-video/s{i}.ts"),
  // 7. Hijas pedidas justo después de la maestra (como X), con IDMClone lento leyendo la maestra.
  "/hls/hijas/master.m3u8": xMaster("/hls/hijas", [{ bw: 2176000, res: "1280x720" }, { bw: 832000, res: "640x360" }]),
};
for (const id of ["x-normal", "x-baja", "hijas"]) {
  const secs = id === "x-baja" ? 20 : 60;
  LISTS["/hls/" + id + "/pl/mp4a/128000/audio.m3u8"] = media(secs, "/hls/" + id + "/pl/mp4a/128000/{i}.m4s", "/hls/" + id + "/pl/mp4a/128000/init.mp4");
  for (const res of ["1280x720", "640x360", "480x270", "320x180"])
    LISTS["/hls/" + id + "/pl/avc1/" + res + "/video.m3u8"] = media(secs, "/hls/" + id + "/pl/avc1/" + res + "/{i}.m4s", "/hls/" + id + "/pl/avc1/" + res + "/init.mp4");
}
LISTS["/hls/solo-audio/a.m3u8"] = media(120, "/hls/solo-audio/s{i}.aac");
LISTS["/hls/codecs/mp4v.m3u8"] = media(60, "/hls/codecs/mp4v{i}.ts");
LISTS["/hls/codecs/vp8.m3u8"] = media(60, "/hls/codecs/vp8{i}.ts");

const MPD = `<?xml version="1.0" encoding="UTF-8"?>
<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT1M0S" minBufferTime="PT2S" profiles="urn:mpeg:dash:profile:isoff-live:2011">
  <Period>
    <AdaptationSet contentType="video" mimeType="video/mp4" segmentAlignment="true">
      <SegmentTemplate timescale="1000" duration="4000" initialization="$RepresentationID$/init.mp4" media="$RepresentationID$/$Number$.m4s" startNumber="1"/>
      <Representation id="v720" bandwidth="2500000" width="1280" height="720" codecs="avc1.64001f"/>
      <Representation id="v360" bandwidth="800000" width="640" height="360" codecs="avc1.4d401e"/>
    </AdaptationSet>
    <AdaptationSet contentType="audio" mimeType="audio/mp4" lang="es">
      <SegmentTemplate timescale="1000" duration="4000" initialization="$RepresentationID$/init.mp4" media="$RepresentationID$/$Number$.m4s" startNumber="1"/>
      <Representation id="a128" bandwidth="128000" codecs="mp4a.40.2" audioSamplingRate="44100"/>
    </AdaptationSet>
  </Period>
</MPD>
`;

// Archivos "enteros" (tamaño total): ruta -> [tipo, bytes].
const FILES = {
  "/f/grande.mp4": ["video/mp4", 25 * MB],
  "/f/anuncio.mp4": ["video/mp4", 300 * 1024],
  "/f/cancion.mp3": ["audio/mpeg", 6 * MB],
  "/r/video_720.mp4": ["video/mp4", 30 * MB],
  "/r/audio_128.mp4": ["audio/mp4", 2 * MB],
  "/r/video_anuncio.mp4": ["video/mp4", 400 * 1024],
  "/r/solo_audio.m4a": ["audio/mp4", 4 * MB],
};

// ------------------------------------------------------------------ casos (lo que hace la página y lo que debería verse)

// run: código de la página. req(url, rango) pide como un reproductor (fetch = XHR para el navegador), con registro.
const CASES = [
  { id: "x-normal", grupo: "HLS (como X)", titulo: "Vídeo de X normal (720p, 360p, 270p)",
    espera: "Panel con las calidades MP4 1280x720, 640x360 y 480x270, y «solo audio».",
    run: `await req("/hls/x-normal/master.m3u8"); await sleep(50); await req("/hls/x-normal/pl/avc1/1280x720/video.m3u8"); await req("/hls/x-normal/pl/mp4a/128000/audio.m3u8");` },
  { id: "x-baja", grupo: "HLS (como X)", titulo: "Vídeo de X a baja resolución (20 s, solo 270p y 180p)",
    espera: "Panel con las calidades de VÍDEO 480x270 y 320x180 (con su audio). NO debe salir solo «solo audio» como si fuera música, ni aparecer y desaparecer.",
    antes: "Antes del arreglo: solo «solo audio» (lo que veías en X). Con el primer arreglo: aparecía un momento y desaparecía.",
    run: `await req("/hls/x-baja/master.m3u8"); await sleep(50); await req("/hls/x-baja/pl/avc1/480x270/video.m3u8"); await req("/hls/x-baja/pl/mp4a/128000/audio.m3u8");` },
  { id: "hijas", grupo: "HLS (como X)", titulo: "Calidades pedidas mientras IDMClone aún lee la maestra",
    espera: "Una sola entrada (la maestra, con 1280x720 y 640x360). Nada debe aparecer un instante y desaparecer.",
    antes: "Antes: la calidad suelta salía un momento como «MP4» sin resolución y luego se quitaba.",
    run: `await req("/hls/hijas/master.m3u8"); await req("/hls/hijas/pl/avc1/1280x720/video.m3u8"); await req("/hls/hijas/pl/mp4a/128000/audio.m3u8");` },
  { id: "solo-audio", grupo: "HLS", titulo: "Emisión solo de audio (radio/podcast)",
    espera: "Panel de AUDIO con «solo audio». Aquí sí es correcto, porque no hay vídeo.",
    run: `await req("/hls/solo-audio/master.m3u8");` },
  { id: "codecs", grupo: "HLS", titulo: "Calidades sin RESOLUTION con códecs mp4v y vp8", nuevoExe: true,
    espera: "Panel de VÍDEO con dos calidades (por kbps, sin resolución).",
    antes: "Con el IDMClone.exe antiguo: las dos salen como «solo audio».",
    run: `await req("/hls/codecs/master.m3u8");` },
  { id: "suelta-audio", grupo: "HLS", titulo: "Lista suelta de audio (.aac) sin su maestra", nuevoExe: true,
    espera: "Panel de AUDIO: «solo audio AAC».",
    antes: "Con el IDMClone.exe antiguo: sale como «MP4» (vídeo) sin resolución.",
    run: `await req("/hls/suelta-audio/audio.m3u8");` },
  { id: "suelta-video", grupo: "HLS", titulo: "Lista suelta de vídeo (.ts) sin su maestra",
    espera: "Panel de VÍDEO: «MP4».",
    run: `await req("/hls/suelta-video/video.m3u8");` },
  { id: "dash", grupo: "DASH", titulo: "DASH con 720p, 360p y audio aparte",
    espera: "Panel con MP4 1280x720 y 640x360, y «solo audio (es)».",
    run: `await req("/dash/manifest.mpd");` },
  { id: "par-audio-primero", grupo: "Vídeo y audio a trozos (Facebook/Instagram)", titulo: "El audio llega antes que el vídeo",
    espera: "Una sola entrada de VÍDEO (vídeo + audio juntos, ~32 MB). El audio no debe salir solo, ni un momento.",
    antes: "Antes: el audio salía un instante solo y luego se cambiaba por el par.",
    run: `await req("/r/audio_128.mp4", "bytes=0-999"); await sleep(400); await req("/r/video_720.mp4", "bytes=0-999");` },
  { id: "par-video-primero", grupo: "Vídeo y audio a trozos (Facebook/Instagram)", titulo: "El vídeo llega antes que el audio",
    espera: "Una sola entrada de VÍDEO (vídeo + audio juntos, ~32 MB).",
    run: `await req("/r/video_720.mp4", "bytes=0-999"); await sleep(400); await req("/r/audio_128.mp4", "bytes=0-999");` },
  { id: "par-anuncio", grupo: "Vídeo y audio a trozos (Facebook/Instagram)", titulo: "Audio y luego un vídeo diminuto (anuncio, 400 KB)",
    espera: "Nada en el panel: el vídeo es menor que el mínimo y su audio no se ofrece como música.",
    antes: "Antes: quedaba «solo audio».",
    run: `await req("/r/audio_128.mp4", "bytes=0-999"); await sleep(300); await req("/r/video_anuncio.mp4", "bytes=0-999");` },
  { id: "trozos-solo-audio", grupo: "Vídeo y audio a trozos (Facebook/Instagram)", titulo: "Audio a trozos sin ningún vídeo",
    espera: "A los ~2,5 s aparece el panel de AUDIO (es un audio de verdad).",
    run: `await req("/r/solo_audio.m4a", "bytes=0-999");` },
  { id: "mp4-grande", grupo: "Archivos enteros", titulo: "<video> con un MP4 de 25 MB",
    espera: "Panel de VÍDEO con «MP4 25.0 MB».", tag: `<video controls src="/f/grande.mp4"></video>` },
  { id: "mp4-anuncio", grupo: "Archivos enteros", titulo: "<video> con un MP4 de 300 KB (anuncio)",
    espera: "Nada en el panel (menor que el mínimo de 1 MB).", tag: `<video controls src="/f/anuncio.mp4"></video>` },
  { id: "mp3", grupo: "Archivos enteros", titulo: "<audio> con un MP3 de 6 MB",
    espera: "Panel de AUDIO con «solo audio MP3 6.0 MB».", tag: `<audio controls src="/f/cancion.mp3" style="width:640px"></audio>` },
  { id: "subtitulos", grupo: "Archivos enteros", titulo: "<video> de 25 MB con subtítulos <track>",
    espera: "Panel de VÍDEO con «MP4 25.0 MB» y «Subtítulos: Español (vtt)».",
    tag: `<video controls src="/f/grande.mp4"><track kind="subtitles" srclang="es" label="Español" src="/subs/es.vtt" default></video>` },
  { id: "real", grupo: "Reproductor real (necesita internet)", titulo: "HLS real con hls.js (Big Buck Bunny, test-streams.mux.dev)",
    espera: "Panel de VÍDEO con varias calidades. Este sí se puede descargar de verdad.",
    real: true },
];

// ------------------------------------------------------------------ HTML

const CSS = `
  :root { --bg:#f6f7f9; --fg:#1c1f24; --mut:#5d6672; --card:#fff; --line:#dde1e7; --acc:#2456b3; --warn:#9a5b00; --warnbg:#fff4e0; }
  @media (prefers-color-scheme: dark) { :root { --bg:#111317; --fg:#e7e9ee; --mut:#9aa3b2; --card:#1a1d23; --line:#2a2f38; --acc:#7aa7ff; --warn:#ffcf7a; --warnbg:#3a2c12; } }
  body { margin:0; background:var(--bg); color:var(--fg); font:15px/1.5 "Segoe UI", system-ui, sans-serif; }
  main { max-width:960px; margin:0 auto; padding:24px 16px 60px; }
  h1 { font-size:22px; margin:0 0 6px; } h2 { font-size:15px; margin:26px 0 8px; color:var(--mut); text-transform:uppercase; letter-spacing:.04em; }
  p { margin:6px 0; } a { color:var(--acc); }
  .nota { color:var(--mut); font-size:14px; }
  ol { margin:0; padding-left:22px; } li { margin:4px 0; }
  .tag { display:inline-block; font-size:12px; padding:1px 7px; border-radius:9px; background:var(--warnbg); color:var(--warn); margin-left:6px; }
  .box { background:var(--card); border:1px solid var(--line); border-radius:8px; padding:12px 14px; margin:12px 0; }
  .box b { display:block; font-size:13px; color:var(--mut); margin-bottom:2px; }
  video, .ph { display:block; width:640px; max-width:100%; aspect-ratio:16/9; background:#000; border-radius:8px; }
  pre { background:var(--card); border:1px solid var(--line); border-radius:8px; padding:10px; font:12px/1.5 Consolas, monospace; white-space:pre-wrap; margin:0; }
  button { font:inherit; padding:5px 12px; border-radius:6px; border:1px solid var(--line); background:var(--card); color:var(--fg); cursor:pointer; }
`;

function page(title, body) {
  return `<!doctype html><html lang="es"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>${title}</title><style>${CSS}</style></head><body><main>${body}</main></body></html>`;
}

function indexPage() {
  let groups = "", last = "";
  for (const c of CASES) {
    if (c.grupo !== last) { if (last) groups += "</ol>"; groups += "<h2>" + c.grupo + "</h2><ol>"; last = c.grupo; }
    groups += `<li><a href="/caso/${c.id}">${esc(c.titulo)}</a>${c.nuevoExe ? '<span class="tag">IDMClone.exe nuevo</span>' : ""}</li>`;
  }
  return page("Casos del panel", `<h1>Casos del panel «Descargar este video»</h1>
<p class="nota">Abre cada caso en la misma pestaña (cada página nueva vacía el panel). Debajo del reproductor está lo que debería verse
y el registro de lo que pidió la página. Los trozos de vídeo son relleno: se prueba qué ofrece el panel, no la descarga.</p>
<p class="nota">Mínimos por defecto de IDMClone: vídeo 1 MB, audio 300 KB. Los casos marcados con «IDMClone.exe nuevo» dependen del arreglo
del programa; con el instalado dan el resultado de «antes».</p>${groups}</ol>`);
}

function casePage(c) {
  let player;
  if (c.tag) player = c.tag;
  else if (c.real) player = `<video id="v" controls muted autoplay playsinline></video>`;
  else player = `<video id="v" class="ph" muted playsinline></video>`;
  const script = c.real
    ? `<script src="https://cdn.jsdelivr.net/npm/hls.js@1.5.17/dist/hls.min.js"></script>
<script>var src="https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8",v=document.getElementById("v");
if(window.Hls&&Hls.isSupported()){var h=new Hls();h.loadSource(src);h.attachMedia(v);}else v.src=src;</script>`
    : c.run ? `<script>
const t0 = performance.now(), logEl = document.getElementById("log");
const log = (s) => { logEl.textContent += ((performance.now() - t0) / 1000).toFixed(2) + " s  " + s + "\\n"; };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function req(url, range) {
  try {
    const r = await fetch(url, range ? { headers: { Range: range } } : {});
    log(r.status + "  " + url + (range ? "  (" + range + ")" : ""));
    await r.arrayBuffer();
  } catch (e) { log("error  " + url + "  " + e); }
}
async function caso() { ${c.run} log("listo"); }
document.getElementById("again").onclick = () => location.reload();
setTimeout(caso, 300);
</script>` : `<script>document.getElementById("again").onclick = () => location.reload();</script>`;
  return page(c.titulo, `<p><a href="/">← Todos los casos</a></p>
<h1>${esc(c.titulo)}${c.nuevoExe ? '<span class="tag">IDMClone.exe nuevo</span>' : ""}</h1>
${player}
<div class="box"><b>Debería verse</b>${esc(c.espera)}</div>
${c.antes ? `<div class="box"><b>Antes del arreglo</b>${esc(c.antes)}</div>` : ""}
<p><button id="again">Repetir el caso</button></p>
${c.run ? '<pre id="log"></pre>' : ""}
${script}`);
}

function esc(s) { return String(s).replace(/[&<>"]/g, (ch) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[ch])); }

// ------------------------------------------------------------------ servidor

// Relleno de un archivo: solo lo pedido (o 64 KB), sin mandar los megas que dice tener.
function sendFile(req, res, type, total) {
  const m = /bytes=(\d+)-(\d*)/.exec(req.headers.range || "");
  if (m) {
    const start = +m[1], end = Math.min(m[2] ? +m[2] : total - 1, total - 1);
    const len = end - start + 1;
    res.writeHead(206, { "Content-Type": type, "Content-Range": `bytes ${start}-${end}/${total}`, "Content-Length": len, "Accept-Ranges": "bytes" });
    if (len <= 65536) return res.end(Buffer.alloc(len));
    res.write(Buffer.alloc(65536)); return setTimeout(() => res.destroy(), 200);
  }
  res.writeHead(200, { "Content-Type": type, "Content-Length": total, "Accept-Ranges": "bytes" });
  res.write(Buffer.alloc(65536)); setTimeout(() => res.destroy(), 200);
}

http.createServer((req, res) => {
  const path = decodeURIComponent(req.url.split("?")[0]);
  const fromBrowser = !!req.headers["sec-fetch-mode"]; // IDMClone (la sonda) no manda Sec-Fetch-*
  if (path === "/" || path === "/index.html") { res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" }); return res.end(indexPage()); }
  if (path.startsWith("/caso/")) {
    const c = CASES.find((x) => x.id === path.slice(6));
    if (c) { res.writeHead(200, { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" }); return res.end(casePage(c)); }
  }
  if (LISTS[path] !== undefined) {
    const out = () => { res.writeHead(200, { "Content-Type": "application/vnd.apple.mpegurl", "Cache-Control": "no-store" }); res.end(LISTS[path]); };
    // Caso «hijas»: IDMClone tarda en leer la maestra, como con una conexión lenta.
    return path === "/hls/hijas/master.m3u8" && !fromBrowser ? setTimeout(out, 1500) : out();
  }
  if (path === "/dash/manifest.mpd") { res.writeHead(200, { "Content-Type": "application/dash+xml", "Cache-Control": "no-store" }); return res.end(MPD); }
  if (FILES[path]) return sendFile(req, res, FILES[path][0], FILES[path][1]);
  if (path === "/subs/es.vtt") { res.writeHead(200, { "Content-Type": "text/vtt; charset=utf-8" }); return res.end("WEBVTT\n\n00:00:01.000 --> 00:00:04.000\nHola\n"); }
  if (/\.(m4s|ts|aac|mp4)$/.test(path)) { res.writeHead(200, { "Content-Type": "application/octet-stream" }); return res.end(Buffer.alloc(1024)); }
  res.writeHead(404, { "Content-Type": "text/plain" }); res.end("no existe");
}).listen(PORT, "127.0.0.1", () => console.log("Casos del panel: http://127.0.0.1:" + PORT + "/"));
