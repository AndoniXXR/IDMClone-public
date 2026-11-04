// IDMClone Integration — fondo: lo común a todos los módulos de bg/ (el primero que se carga).
// Chrome/Edge/Brave/Opera/Vivaldi: service worker (Manifest V3). Firefox: página de fondo (background.scripts).
// Captura descargas del navegador y las envía a IDMClone por el host de mensajería nativa com.idmclone.host. Solo usa
// las APIs que el navegador da a las extensiones: nada de drivers ni de interceptar el tráfico de otras aplicaciones.
//
// Los módulos de bg/ comparten el objeto IDMBG (G): cada uno pone ahí sus funciones y las de los demás se buscan en G
// al llamarlas (no al cargar), así el orden de carga solo importa para este archivo. Las APIs del navegador también se
// leen al usarlas: algunas (scripting, action, tabs.goBack) pueden no existir al arrancar.
(function (root) {
  "use strict";
  const G = root.IDMBG = root.IDMBG || {};
  const L = root.IDMLogic;
  const B = (typeof browser !== "undefined" && browser.runtime) ? browser : chrome;

  // Firefox (y sus derivados) o Chromium: el menú contextual tiene opciones distintas para cada uno (#2).
  const IS_FIREFOX = typeof browser !== "undefined" && !!browser.runtime && typeof browser.runtime.getBrowserInfo === "function";
  let EXT_VERSION = "";
  try { EXT_VERSION = (B.runtime.getManifest && B.runtime.getManifest().version) || ""; } catch (e) { }

  // Promesa o no (Chrome antiguo con callbacks, Firefox con promesas): sin errores sin capturar.
  function quiet(r) { if (r && typeof r.then === "function") r.then(null, () => {}); return r; }
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  function hostOf(url) { try { return new URL(url).host.toLowerCase(); } catch (e) { return ""; } } // con puerto
  // Notificación del sistema (avisos de error: nunca para lo que va bien).
  function notify(title, message) {
    try { B.notifications.create("", { type: "basic", iconUrl: "icons/icon48.png", title, message: String(message || "").substring(0, 200) }); }
    catch (e) { }
  }
  // Mapa con tope: al pasarse, se quitan los más antiguos (el orden de inserción de Map).
  function trimMap(map, max, keep) { if (map.size <= max) return; for (const k of map.keys()) { if (map.size <= keep) break; map.delete(k); } }

  Object.assign(G, {
    L, B, t: L.t, IS_FIREFOX, EXT_VERSION, HOST: "com.idmclone.host",
    DOWNLOAD_REPLY_MS: 25000, // con IDMClone cerrado, el host lo arranca y espera su respuesta
    PING_REPLY_MS: 5000,
    PROBE_REPLY_MS: 20000,
    YT_REPLY_MS: 30000,
    quiet, sleep, hostOf, trimMap, notify,
    resets: [], // cada módulo pone aquí cómo vaciar su estado (pruebas: _reset)
    // Mensajes de las páginas y del popup: tipo -> (req, sender, sendResponse) => true si contestará más tarde.
    // Los atiende una sola escucha (bg/main.js).
    onMessage: {},
  });
})(typeof self !== "undefined" ? self : globalThis);
