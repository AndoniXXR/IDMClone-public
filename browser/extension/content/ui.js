// IDMClone Integration — lo común de los scripts de cada página (el primero de content/): acceso a la extensión y el
// aspecto de los paneles, como los de IDM (botón claro con degradado azul y triángulo verde, más "?" y "X").
// Cada panel vive en un Shadow DOM CERRADO (la página no llega a sus botones; solo estos scripts guardan la referencia)
// para que los estilos de la página no le afecten, y solo contiene marcas propias: los textos van como texto.
(function (root) {
  "use strict";
  const B = (typeof browser !== "undefined" && browser.runtime) ? browser : chrome;
  const U = root.IDMContent = root.IDMContent || {};

  // background lo mira antes de poner estos scripts en las pestañas abiertas al instalar o actualizar: si la copia
  // anterior se quedó huérfana (extensión actualizada), su runtime ya no tiene id y se pone otra.
  const alive = () => { try { return !!(B.runtime && B.runtime.id); } catch (e) { return false; } };
  root.__idmcloneAlive = alive;

  function send(m) {
    if (!alive()) return Promise.resolve(null); // copia huérfana: ya no puede hablar con el fondo
    try { return Promise.resolve(B.runtime.sendMessage(m)).catch(() => null); } catch (e) { return Promise.resolve(null); }
  }

  // Configuración de IDMClone que el fondo guarda en storage (appCfg), al cargar y cada vez que cambia.
  const cfgListeners = [];
  let appCfg = null;
  function onConfig(f) { cfgListeners.push(f); f(appCfg); }
  function setConfig(a) { appCfg = a || null; for (const f of cfgListeners) { try { f(appCfg); } catch (e) { } } }
  try {
    Promise.resolve(B.storage.local.get({ appCfg: null })).then((s) => setConfig(s && s.appCfg)).catch(() => {});
    B.storage.onChanged.addListener((ch) => { if (ch.appCfg) setConfig(ch.appCfg.newValue); });
  } catch (e) { }

  const BASE_CSS = `
    :host { all: initial; position: absolute; z-index: 2147483647; font: 12px "Segoe UI", Arial, sans-serif;
            opacity: 1 !important; visibility: visible !important; filter: none !important; mask: none !important;
            -webkit-mask: none !important; mix-blend-mode: normal !important; } /* la página no puede volverlo invisible bajo el cursor */
    .bar { display: flex; align-items: center; gap: 2px; user-select: none; }
    button { all: unset; cursor: pointer; display: flex; align-items: center; box-sizing: border-box; }
    .bar button { color: #1e3c78; border: 1px solid #7ea2ce; border-radius: 2px; background: linear-gradient(#f6faff, #bed8f3); box-shadow: 0 1px 3px rgba(0,0,0,.35); }
    .bar button:hover { background: linear-gradient(#ffffff, #d4e6f8); border-color: #4f86c6; }
    .bar button:focus-visible { outline: 2px solid #1e3c78; outline-offset: 1px; }
    .help, .x { justify-content: center; width: 18px; height: 18px; font: bold 12px Tahoma, Arial, sans-serif; }
    svg { width: 14px; height: 14px; flex: none; }
    .status { margin-top: 3px; padding: 4px 8px; border-radius: 3px; background: #fff; color: #1b1b1b; border: 1px solid #9aa7b6; max-width: 320px;
              box-shadow: 0 2px 6px rgba(0,0,0,.3); }
    .status.ok { border-color: #2e8b3e; color: #1d6b2b; }
    .status.err { border-color: #c0392b; color: #a12a1d; }
  `;
  const ICON = '<svg viewBox="0 0 16 16" aria-hidden="true"><defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#7ddc7d"/><stop offset="1" stop-color="#1a9a2c"/></linearGradient></defs><path d="M3 1.5 14.5 8 3 14.5z" fill="url(#g)" stroke="#16702a" stroke-width=".8" stroke-linejoin="round"/></svg>';

  // Un clic en el primer medio segundo tras aparecer (o moverse) un panel no cuenta: evita que una página lo ponga bajo
  // el cursor justo antes de un clic del usuario.
  const CLICK_GUARD_MS = 500;

  Object.assign(U, { B, alive, send, onConfig, config: () => appCfg, BASE_CSS, ICON, CLICK_GUARD_MS });
})(typeof self !== "undefined" ? self : globalThis);
