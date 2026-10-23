// Ajustes de la integración. Los tipos de archivo y los sitios excluidos son los de IDMClone
// (Opciones > Tipos de archivo), como en IDM; si IDMClone no responde, se editan aquí.
const L = self.IDMLogic;
const B = (typeof browser !== "undefined" && browser.runtime) ? browser : chrome;

function $(id) { return document.getElementById(id); }

// Textos traducidos (_locales). Sin traducción se queda el español del HTML.
function tr(key, subs) { const r = L.t(key, subs); return r === key ? "" : r; }
function translate() {
  for (const el of document.querySelectorAll("[data-i18n]")) { const v = tr(el.getAttribute("data-i18n")); if (v) el.textContent = v; }
  try { const lang = (B.i18n && B.i18n.getUILanguage && B.i18n.getUILanguage()) || ""; if (lang) document.documentElement.lang = lang; } catch (e) { }
}
const T = (key, subs) => L.t(key, subs);

let fromApp = false;

function fill(s) {
  // IDMClone no captura en este navegador (Opciones > General): se enseña por qué y no se toca el ajuste propio.
  const off = !!s.captureOff;
  $("enabled").checked = off ? s.userEnabled !== false : !!s.enabled;
  $("enabled").disabled = off;
  $("offNote").hidden = !off;
  $("offNote").textContent = off ? (s.offReason || T("popupOffDefault")) : "";
  $("minSize").value = s.minSizeKB || 0;
  $("exts").value = (Array.isArray(s.exts) && s.exts.length ? s.exts : L.DEFAULT_EXTS).join(" ");
  $("exclude").value = (Array.isArray(s.exclude) ? s.exclude : L.DEFAULT_EXCLUDE).join("\n");
  fromApp = !!s.fromApp;
  $("exts").readOnly = $("exclude").readOnly = fromApp;
  $("fromApp").hidden = !fromApp;
  $("options").hidden = !fromApp;
  $("defaults").hidden = fromApp;
}

async function load() {
  let s = null;
  try { s = await B.runtime.sendMessage({ type: "get-settings" }); } catch (e) { }
  fill(s || await B.storage.local.get({ enabled: true, exts: L.DEFAULT_EXTS, minSizeKB: 0, exclude: L.DEFAULT_EXCLUDE }));
  setStatus(T("popupReady"));
}

function setStatus(text, cls) {
  const el = $("status");
  el.textContent = text;
  el.className = "status" + (cls ? " " + cls : "");
}

$("defaults").addEventListener("click", () => {
  fill({ enabled: $("enabled").checked, minSizeKB: parseInt($("minSize").value, 10) || 0, exts: L.DEFAULT_EXTS, exclude: L.DEFAULT_EXCLUDE });
  setStatus(T("popupDefaultsLoaded"));
});

$("save").addEventListener("click", async () => {
  const minSizeKB = Math.max(0, parseInt($("minSize").value, 10) || 0);
  const data = { enabled: $("enabled").checked, minSizeKB };
  if (!fromApp) {
    data.exts = $("exts").value.toLowerCase().split(/[\s,;]+/).map((x) => x.replace(/^\./, "").trim()).filter(Boolean);
    data.exclude = $("exclude").value.split(/\r?\n/).map((x) => x.trim()).filter(Boolean);
  }
  await B.storage.local.set(data);
  setStatus(T("popupSaved"), "ok");
});

$("options").addEventListener("click", async () => {
  setStatus(T("popupOpening"));
  let r;
  try { r = await B.runtime.sendMessage({ type: "open-options" }); } catch (e) { r = { ok: false, error: String(e && e.message || e) }; }
  if (r && r.ok) { setStatus(T("popupOptionsOpened"), "ok"); }
  else setStatus((r && r.error) || T("popupOptionsFail"), "bad");
});

$("test").addEventListener("click", async () => {
  setStatus(T("popupTesting"));
  let r;
  try { r = await B.runtime.sendMessage({ type: "ping-host" }); }
  catch (e) { setStatus(T("popupError", String(e && e.message || e)), "bad"); return; }
  if (!r || !r.ok) setStatus((r && r.error) || T("popupNoHost"), "bad");
  else if (r.gui) setStatus(T("popupConnected"), "ok");
  else setStatus(T("popupHostOnly"), "warn");
});

translate();
load();
