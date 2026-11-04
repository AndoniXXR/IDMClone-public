// IDMClone Integration — teclas especiales (como IDM):
//   - mantener pulsada la tecla "para no descargar con IDMClone" (por defecto Supr): la descarga sigue en el navegador;
//   - mantener pulsada la tecla "para forzar la descarga" (por defecto Insert, desactivada): va a IDMClone sea del tipo
//     que sea.
// Las teclas se eligen en IDMClone (Opciones > General). Este script solo avisa al fondo de cuándo se pulsan y se
// sueltan ESAS teclas (nunca de lo que se escribe) y de qué enlace se pulsó con ellas; el fondo las aplica a cualquier
// descarga que empiece mientras tanto (enlace, formulario, descarga lanzada por la página, PDF o vídeo que el navegador
// abriría). El clic no se toca: la descarga sale del navegador tal cual (redirecciones, POST, referer).
(function (root) {
  "use strict";
  const U = root.IDMContent;
  if (!U) return;
  let keys = { prevent: "del", force: "" };   // valores de IDM hasta saber los de IDMClone
  const down = { del: false, ins: false };
  const NAMES = { Delete: "del", Insert: "ins", Alt: "alt", Control: "ctrl", Shift: "shift" };
  const sentAt = {};          // tecla -> último aviso de "pulsada" (los repetidos de una tecla mantenida: uno por segundo)
  const reported = new Set(); // teclas avisadas como pulsadas desde este marco (para soltarlas si pierde el foco)

  U.onConfig((a) => { if (a && typeof a.preventKeys === "string") keys = { prevent: a.preventKeys, force: a.forceKeys || "" }; });

  // ¿Forma parte de alguna de las combinaciones configuradas? Solo de esas teclas se avisa.
  function watched(name) { return (keys.prevent + "+" + keys.force).split("+").indexOf(name) >= 0; }

  // Solo teclas de verdad (isTrusted): una página no puede fingir que el usuario mantiene pulsada una tecla.
  function onKey(e) {
    if (!e.isTrusted) return;
    const name = NAMES[e.key];
    if (!name) return;
    const isDown = e.type === "keydown";
    if (name === "del" || name === "ins") down[name] = isDown;
    if (!watched(name)) return;
    const now = Date.now();
    if (isDown && e.repeat && now - (sentAt[name] || 0) < 1000) return; // sigue pulsada: basta un aviso por segundo
    sentAt[name] = isDown ? now : 0;
    if (isDown) reported.add(name); else reported.delete(name);
    U.send({ type: "idm-keys", key: name, down: isDown });
  }
  addEventListener("keydown", onKey, true);
  addEventListener("keyup", onKey, true);
  // Solo cuando la ventana pierde el foco (no al pasar el foco de un enlace a otro: eso ocurre justo en el clic): el
  // keyup llegará a otra ventana, así que se dan por sueltas (como IDM).
  addEventListener("blur", (e) => {
    if (e.target !== window) return;
    down.del = down.ins = false;
    if (reported.size) { reported.clear(); U.send({ type: "idm-keys", reset: true }); }
  });

  function held(combo, e) {
    if (!combo) return false;
    return combo.split("+").every((k) =>
      k === "alt" ? e.altKey : k === "ctrl" ? e.ctrlKey : k === "shift" ? e.shiftKey : k === "del" ? down.del : k === "ins" ? down.ins : false);
  }

  // El enlace pulsado con la tecla: el fondo lo recuerda unos segundos por si la descarga tarda en empezar
  // (redirecciones). Solo un clic del usuario (no un a.click() de la página). El clic sigue su curso: no se cancela.
  function onClick(e) {
    if (!e.isTrusted) return;
    const a = e.target && e.target.closest ? e.target.closest("a[href], area[href]") : null;
    if (!a || !/^(https?|ftps?):/i.test(a.href)) return;
    if (held(keys.prevent, e)) U.send({ type: "prevent-capture", url: a.href, referrer: location.href });
    else if (held(keys.force, e)) U.send({ type: "force-mark", url: a.href, referrer: location.href });
  }
  addEventListener("click", onClick, true);
})(typeof self !== "undefined" ? self : globalThis);
