// IDMClone Integration — fondo: canal persistente con el host nativo (un solo proceso para muchas peticiones).
// Cada petición lleva un "rid" y el host lo devuelve en la respuesta (pueden llegar en cualquier orden). Si el canal se
// cae, se reabre en la siguiente petición. IDMClone también manda peticiones por su cuenta (sin rid): las de cookies.
(function (root) {
  "use strict";
  const G = root.IDMBG;
  const { B, t } = G;

  let port = null;
  let nextRid = 1;
  const pending = new Map();
  let hostDown = ""; // último error al hablar con el host ("" = responde)

  function noteHost(err) {
    const v = err || "";
    if (v === hostDown) return;
    hostDown = v;
    G.refreshIcon();
  }

  // "Specified native messaging host not found." -> sin el punto final (el texto traducido sigue con ". ¿Ejecutaste...?").
  function noDot(e) { return String(e).replace(/[.\s]+$/, ""); }

  function connect() {
    if (port) return port;
    const p = B.runtime.connectNative(G.HOST);
    p.onMessage.addListener((msg) => {
      noteHost("");
      // Petición de IDMClone (no una respuesta: no lleva "rid"). Solo llega por el canal nativo, que solo abre IDMClone.
      if (msg && msg.type === "getcookies" && msg.rid === undefined) { G.answerCookies(msg).catch(() => {}); return; }
      const w = msg && pending.get(msg.rid);
      if (!w) return;
      pending.delete(msg.rid);
      clearTimeout(w.timer);
      w.resolve(msg);
    });
    p.onDisconnect.addListener(() => {
      const e = (p.error && p.error.message) || (B.runtime.lastError && B.runtime.lastError.message) || t("hostClosed");
      if (port === p) port = null;
      // Se cerró con peticiones sin contestar (host no instalado, IDMClone movido...): error en el icono.
      // Si no esperaba nada (IDMClone se cerró), el canal se reabre en la siguiente petición.
      if (pending.size) noteHost(e);
      for (const [, w] of pending) { clearTimeout(w.timer); w.resolve({ ok: false, error: t("hostUnavailable", noDot(e)) }); }
      pending.clear();
    });
    port = p;
    return p;
  }

  function hostRequest(msg, timeoutMs) {
    return new Promise((resolve) => {
      let p;
      try { p = connect(); } catch (e) { noteHost(String(e && e.message || e)); resolve({ ok: false, error: t("hostUnavailable", noDot(e && e.message || e)) }); return; }
      const rid = nextRid++;
      const timer = setTimeout(() => { pending.delete(rid); resolve({ ok: false, error: t("hostTimeout") }); }, timeoutMs);
      pending.set(rid, { resolve, timer });
      try { p.postMessage(Object.assign({ rid }, msg)); }
      catch (e) { pending.delete(rid); clearTimeout(timer); if (port === p) port = null; resolve({ ok: false, error: String(e && e.message || e) }); }
    });
  }

  // Respuesta que no espera contestación (la de cookies a IDMClone).
  function hostSend(msg) { return hostRequest(msg, G.PING_REPLY_MS); }

  Object.assign(G, {
    hostRequest, hostSend, noteHost,
    hostState: () => ({ open: !!port, down: hostDown }),
    pendingCount: () => pending.size,
  });
})(typeof self !== "undefined" ? self : globalThis);
