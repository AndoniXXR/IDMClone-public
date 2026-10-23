// Cliente mínimo del protocolo DevTools (CDP) sobre WebSocket (Node 22+ trae WebSocket).
"use strict";
class CDP {
  static async connect(wsUrl) {
    const c = new CDP();
    c.ws = new WebSocket(wsUrl);
    c.id = 1; c.pending = new Map(); c.events = [];
    c.ws.onmessage = (ev) => {
      const m = JSON.parse(ev.data);
      if (m.id && c.pending.has(m.id)) { const p = c.pending.get(m.id); c.pending.delete(m.id); m.error ? p.reject(new Error(JSON.stringify(m.error))) : p.resolve(m.result); }
      else if (m.method) { c.events.push(m); for (const l of c.listeners) l(m); }
    };
    c.listeners = [];
    await new Promise((res, rej) => { c.ws.onopen = res; c.ws.onerror = rej; });
    return c;
  }
  send(method, params, sessionId) {
    const id = this.id++;
    const msg = { id, method, params: params || {} };
    if (sessionId) msg.sessionId = sessionId;
    return new Promise((resolve, reject) => { this.pending.set(id, { resolve, reject }); this.ws.send(JSON.stringify(msg)); });
  }
  on(f) { this.listeners.push(f); }
  close() { try { this.ws.close(); } catch (e) {} }
}
async function browserWs(port) { const r = await fetch(`http://127.0.0.1:${port}/json/version`); return (await r.json()).webSocketDebuggerUrl; }
module.exports = { CDP, browserWs };

if (require.main === module) {
  (async () => {
    const [port, ext] = process.argv.slice(2);
    const c = await CDP.connect(await browserWs(port));
    try {
      const r = await c.send("Extensions.loadUnpacked", { path: ext });
      console.log("loadUnpacked ->", JSON.stringify(r));
    } catch (e) { console.log("loadUnpacked ERROR:", e.message); }
    await new Promise((r) => setTimeout(r, 2000));
    const t = await c.send("Target.getTargets");
    for (const x of t.targetInfos) console.log(x.type, x.url);
    c.close();
  })();
}
