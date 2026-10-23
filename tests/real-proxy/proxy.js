// Proxy HTTP de reenvío real (peticiones en forma absoluta y túneles CONNECT para HTTPS),
// con autenticación Basic opcional. Sirve para probar IDMClone con tráfico real de internet.
//   node proxy.js <puerto> [usuario:clave] [archivo-log]
// Cada petición o túnel deja una línea en el log: "HTTP GET <url> <estado> <bytes>" o
// "CONNECT <host:puerto> <bytes recibidos del servidor>", y "407 ..." cuando falta la clave.
'use strict';
const http = require('http');
const net = require('net');
const fs = require('fs');

const port = parseInt(process.argv[2] || '18080', 10);
const auth = process.argv[3] && process.argv[3] !== '-' ? process.argv[3] : null;
const logFile = process.argv[4] || null;
const expected = auth ? 'Basic ' + Buffer.from(auth).toString('base64') : null;

function log(line) {
  const l = new Date().toISOString() + ' ' + line;
  console.log(l);
  if (logFile) fs.appendFileSync(logFile, l + '\n');
}

function authorized(headers) {
  return !expected || headers['proxy-authorization'] === expected;
}

const server = http.createServer((req, res) => {
  if (!authorized(req.headers)) {
    log('407 HTTP ' + req.method + ' ' + req.url);
    res.writeHead(407, { 'Proxy-Authenticate': 'Basic realm="proxy de pruebas"', 'Content-Length': '0' });
    return res.end();
  }
  let target;
  try { target = new URL(req.url); } catch (e) { res.writeHead(400); return res.end(); }
  const headers = Object.assign({}, req.headers);
  delete headers['proxy-authorization'];
  delete headers['proxy-connection'];
  const up = http.request({
    host: target.hostname, port: target.port || 80, method: req.method,
    path: target.pathname + target.search, headers,
  }, (ur) => {
    let bytes = 0;
    ur.on('data', (c) => { bytes += c.length; });
    ur.on('end', () => log('HTTP ' + req.method + ' ' + req.url + ' ' + ur.statusCode + ' ' + bytes + (req.headers.range ? ' ' + req.headers.range : '')));
    ur.on('aborted', () => log('HTTP ' + req.method + ' ' + req.url + ' ' + ur.statusCode + ' ' + bytes + ' (cortada por el cliente)'));
    res.writeHead(ur.statusCode, ur.headers);
    ur.pipe(res);
  });
  up.on('error', (e) => { log('ERROR ' + req.url + ' ' + e.message); try { res.writeHead(502); res.end(); } catch (_) {} });
  res.on('close', () => up.destroy());
  req.pipe(up);
});

server.on('connect', (req, client, head) => {
  if (!authorized(req.headers)) {
    log('407 CONNECT ' + req.url);
    client.end('HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm="proxy de pruebas"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n');
    return;
  }
  const [host, p] = req.url.split(':');
  let bytes = 0;
  const up = net.connect(parseInt(p || '443', 10), host, () => {
    client.write('HTTP/1.1 200 Connection Established\r\n\r\n');
    if (head && head.length) up.write(head);
    up.pipe(client);
    client.pipe(up);
  });
  up.on('data', (c) => { bytes += c.length; });
  const done = () => { if (!done.x) { done.x = 1; log('CONNECT ' + req.url + ' ' + bytes); } };
  up.on('close', done);
  up.on('error', () => client.destroy());
  client.on('error', () => up.destroy());
  client.on('close', () => up.destroy());
});

server.listen(port, '127.0.0.1', () => log('proxy escuchando en 127.0.0.1:' + port + (auth ? ' (con clave)' : '')));
