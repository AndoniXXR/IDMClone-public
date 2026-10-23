using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace IDMClone.Tests
{
    // Servidor HTTP mínimo (TcpListener) para probar el motor contra servidores "raros".
    // URL: /f/<modos>/<nombre>?size=N&id=X   (modos separados por comas)
    //   range       normal: admite Range, Content-Range, Accept-Ranges, ETag
    //   norange     ignora Range: siempre 200 con el archivo entero
    //   ignorerange 206 solo para el sondeo 0-0; el resto de rangos -> 200 entero
    //   shift       rangos que no empiezan en 0 -> 206 con un byte de desfase
    //   nocr        206 sin cabecera Content-Range
    //   html        tras el sondeo, todo devuelve una página HTML ("enlace caducado")
    //   cut         las 2 primeras respuestas se cortan al 40 %
    //   cutall      TODAS las respuestas de más de 1000 bytes se cortan tras 8 KB (o al 40 % si es menos)
    //   slow        16 KB cada 30 ms (~530 KB/s)      veryslow  8 KB cada 50 ms (~160 KB/s)
    //   nolen       200 sin Content-Length (fin = cierre de conexión); implica norange
    //   needauth    403 salvo Cookie sess=abc, Referer http://ref.example/page y UA con TestUA/1
    //   notfound    404
    //   lmflap      Last-Modified y ETag distintos en CADA petición (mismo contenido y tamaño), como algunos servidores
    //               dinámicos: cualquier If-Range falla
    //   nolm        sin Last-Modified      badlm  Last-Modified ilegible      futurelm  Last-Modified en el año 2100
    //   oldlm       Last-Modified en formato RFC 850 (2015)   asclm  en formato asctime (2015)
    //   proxyonly   403 salvo que la petición llegue por proxy (forma absoluta): sitio bloqueado sin la VPN del navegador
    //   flaky503    las 2 primeras peticiones -> 503 sin Retry-After      ra503  la primera -> 503 con "Retry-After: 2"
    public class TestServer
    {
        TcpListener _listener;
        volatile bool _stopped;
        public int Port;
        readonly Dictionary<string, int> _counters = new Dictionary<string, int>();

        public string Base { get { return "http://127.0.0.1:" + Port; } }

        public string Url(string modes, string name, long size, string id)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}/f/{1}/{2}?size={3}&id={4}", Base, modes, name, size, id);
        }

        public void Start() { Start(0); }

        public void Start(int port)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Thread t = new Thread(AcceptLoop);
            t.IsBackground = true;
            t.Start();
        }

        public void Stop()
        {
            _stopped = true;
            try { _listener.Stop(); } catch { }
        }

        // Archivos fijos (listas HLS/DASH y trozos de vídeo de las pruebas): /s/<ruta>, con Range.
        class StaticFile { public byte[] Data; public string Type; public int Status = 200; public int Busy; public string RetryAfter; }
        readonly Dictionary<string, StaticFile> _static = new Dictionary<string, StaticFile>();
        public string Put(string path, byte[] data, string type)
        {
            lock (_static) { StaticFile f = new StaticFile(); f.Data = data; f.Type = type; _static["/s/" + path] = f; }
            return Base + "/s/" + path;
        }
        public string Put(string path, string text, string type) { return Put(path, Encoding.UTF8.GetBytes(text), type); }
        public void FailStatic(string path, int status) { lock (_static) _static["/s/" + path].Status = status; }
        public int StaticHits(string path) { return Count("s:/s/" + path); }
        // Las próximas "times" peticiones responden 429 con ese Retry-After (como un servidor que limita).
        public void BusyStatic(string path, int times, string retryAfter) { lock (_static) { StaticFile f = _static["/s/" + path]; f.Busy = times; f.RetryAfter = retryAfter; } }
        public int StaticProxied(string path) { return Count("sp:/s/" + path); }

        void ServeStatic(NetworkStream ns, string path, Dictionary<string, string> h, bool proxied)
        {
            StaticFile f;
            bool busy = false;
            lock (_static) { _static.TryGetValue(path, out f); if (f != null && f.Busy > 0) { f.Busy--; busy = true; } }
            Bump("s:" + path);
            if (proxied) Bump("sp:" + path);
            if (f == null) { Simple(ns, 404, "Not Found", "text/plain", "no"); return; }
            if (busy) { WriteHead(ns, 429, "Too Many Requests", new string[] { "Retry-After: " + f.RetryAfter, "Content-Length: 0" }); return; }
            if (f.Status != 200) { Simple(ns, f.Status, "Error", "text/plain", "error " + f.Status); return; }
            long a = 0, b = f.Data.Length - 1;
            string range;
            bool partial = h.TryGetValue("Range", out range) && range.StartsWith("bytes=");
            if (partial)
            {
                string[] r = range.Substring(6).Split('-');
                a = long.Parse(r[0], CultureInfo.InvariantCulture);
                if (r.Length > 1 && r[1].Length > 0) b = Math.Min(b, long.Parse(r[1], CultureInfo.InvariantCulture));
            }
            int n = (int)(b - a + 1);
            List<string> hd = new List<string>();
            hd.Add("Content-Type: " + f.Type);
            hd.Add("Content-Length: " + n);
            hd.Add("Accept-Ranges: bytes");
            if (partial) hd.Add("Content-Range: bytes " + a + "-" + b + "/" + f.Data.Length);
            WriteHead(ns, partial ? 206 : 200, partial ? "Partial Content" : "OK", hd.ToArray());
            ns.Write(f.Data, (int)a, n);
        }

        public int Count(string key)
        {
            lock (_counters) { int v; return _counters.TryGetValue(key, out v) ? v : 0; }
        }

        int Bump(string key)
        {
            lock (_counters) { int v; _counters.TryGetValue(key, out v); v++; _counters[key] = v; return v; }
        }

        // Versión del archivo por id (simula que cambia en el servidor): otro contenido y otro ETag.
        readonly Dictionary<string, int> _versions = new Dictionary<string, int>();
        public void SetVersion(string id, int v) { lock (_versions) _versions[id] = v; }
        int VersionOf(string id) { lock (_versions) { int v; return _versions.TryGetValue(id, out v) ? v : 1; } }
        // Tamaño por id que sustituye al de la URL (el archivo crece o encoge en el servidor).
        readonly Dictionary<string, long> _sizes = new Dictionary<string, long>();
        public void SetSize(string id, long size) { lock (_sizes) _sizes[id] = size; }

        // Contenido determinista: el byte i se puede calcular en cualquier posición.
        public static byte ByteAt(long i)
        {
            unchecked
            {
                ulong x = (ulong)i * 0x9E3779B97F4A7C15UL;
                x ^= x >> 29;
                x ^= x >> 13;
                return (byte)x;
            }
        }

        static long VersionShift(int version) { return (version - 1) * 1000003L; }

        public static void Fill(byte[] buf, long offset, int count) { Fill(buf, offset, count, 1); }

        public static void Fill(byte[] buf, long offset, int count, int version)
        {
            long shift = VersionShift(version);
            for (int k = 0; k < count; k++) buf[k] = ByteAt(offset + k + shift);
        }

        public static string Sha256Of(long size) { return Sha256Of(size, 1); }

        public static string Sha256Of(long size, int version)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] buf = new byte[64 * 1024];
                long pos = 0;
                while (pos < size)
                {
                    int n = (int)Math.Min(buf.Length, size - pos);
                    Fill(buf, pos, n, version);
                    sha.TransformBlock(buf, 0, n, null, 0);
                    pos += n;
                }
                sha.TransformFinalBlock(buf, 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }

        void AcceptLoop()
        {
            while (!_stopped)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); }
                catch { if (_stopped) return; continue; }
                Thread t = new Thread(delegate() { try { Handle(c); } catch { } finally { try { c.Close(); } catch { } } });
                t.IsBackground = true;
                t.Start();
            }
        }

        static string ReadHead(NetworkStream ns)
        {
            StringBuilder sb = new StringBuilder();
            int b, state = 0;
            while ((b = ns.ReadByte()) >= 0)
            {
                sb.Append((char)b);
                if (b == '\r' && (state == 0 || state == 2)) state++;
                else if (b == '\n' && (state == 1 || state == 3)) { state++; if (state == 4) break; }
                else state = 0;
                if (sb.Length > 65536) return null;
            }
            return state == 4 ? sb.ToString() : null;
        }

        void Handle(TcpClient client)
        {
            client.NoDelay = true;
            NetworkStream ns = client.GetStream();
            string head = ReadHead(ns);
            if (head == null) return;
            string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            string[] rl = lines[0].Split(' ');
            if (rl.Length < 2) return;
            string target = rl[1];
            Dictionary<string, string> h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c > 0) h[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
            }
            // Cuerpo de un POST (formularios).
            string body = "";
            string clen;
            if (rl[0] == "POST" && h.TryGetValue("Content-Length", out clen))
            {
                int n = int.Parse(clen, CultureInfo.InvariantCulture), got = 0;
                byte[] bb = new byte[n];
                while (got < n) { int r = ns.Read(bb, got, n - got); if (r <= 0) break; got += r; }
                body = Encoding.UTF8.GetString(bb, 0, got);
            }

            // Petición a través de proxy: "GET http://servidor:puerto/ruta HTTP/1.1" (forma absoluta).
            bool proxied = false;
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                int slash = target.IndexOf('/', 7);
                string viaHost = slash > 0 ? target.Substring(7, slash - 7) : target.Substring(7);
                h["Host"] = viaHost;
                target = slash > 0 ? target.Substring(slash) : "/";
                proxied = true;
            }

            // /f/<modos>/<nombre>?size=..&id=..
            string path = target, query = "";
            int q = target.IndexOf('?');
            if (q >= 0) { path = target.Substring(0, q); query = target.Substring(q + 1); }
            string[] parts = path.Split('/');
            if (path == "/page") { Simple(ns, 200, "OK", "text/html; charset=utf-8", TestPage()); return; }
            if (path.StartsWith("/s/")) { ServeStatic(ns, path, h, proxied); return; }
            if (path == "/video")
            {
                // Página con un reproductor <video> (para el panel "Descargar este video" en un navegador real).
                string src = Uri.UnescapeDataString(query.StartsWith("src=") ? query.Substring(4) : "");
                Simple(ns, 200, "OK", "text/html; charset=utf-8", "<!doctype html><meta charset=\"utf-8\"><title>Vídeo de prueba de IDMClone</title>" +
                    "<h1>Reproductor de prueba</h1><video src=\"" + System.Net.WebUtility.HtmlEncode(src) + "\" width=\"640\" height=\"360\" controls muted autoplay></video>");
                return;
            }
            if (path == "/proxy.pac")
            {
                // Script PAC: el servidor ficticio "descargas.invalid" solo se alcanza por este proxy.
                Simple(ns, 200, "OK", "application/x-ns-proxy-autoconfig",
                    "function FindProxyForURL(url, host) { if (host == 'descargas.invalid') return 'PROXY 127.0.0.1:" + Port + "'; return 'DIRECT'; }");
                return;
            }
            if (parts.Length >= 3 && parts[1] == "r308")
            {
                // Redirección 308 (permanente, conserva el método): .NET Framework no la sigue solo.
                string rest = string.Join("/", parts, 2, parts.Length - 2);
                string to = rest.StartsWith("s/") ? "/" + rest : "/f/" + rest;
                WriteHead(ns, 308, "Permanent Redirect", new string[] { "Location: " + to + (query.Length > 0 ? "?" + query : ""), "Content-Type: text/html", "Content-Length: 0" });
                return;
            }
            if (parts.Length >= 3 && parts[1] == "r")
            {
                // Redirección a OTRO servidor (localhost en vez de 127.0.0.1) para ver si se filtran cookies.
                WriteHead(ns, 302, "Found", new string[] { "Location: http://localhost:" + Port + "/f/" + string.Join("/", parts, 2, parts.Length - 2) + (query.Length > 0 ? "?" + query : ""), "Content-Length: 0" });
                return;
            }
            if (parts.Length < 4 || parts[1] != "f") { Simple(ns, 404, "Not Found", "text/plain", "no"); return; }
            HashSet<string> modes = new HashSet<string>(parts[2].Split(','), StringComparer.OrdinalIgnoreCase);
            long size = 0; string id = "x";
            foreach (string kv in query.Split('&'))
            {
                int e = kv.IndexOf('=');
                if (e < 0) continue;
                string k = kv.Substring(0, e), v = kv.Substring(e + 1);
                if (k == "size") size = long.Parse(v, CultureInfo.InvariantCulture);
                else if (k == "id") id = v;
            }
            if (modes.Contains("nolen")) modes.Add("norange");
            lock (_sizes) { long ov; if (_sizes.TryGetValue(id, out ov)) size = ov; }

            int reqNo = Bump(id + ":req");
            if (proxied) Bump(id + ":proxied");
            string hostHdr, cookieHdr;
            h.TryGetValue("Host", out hostHdr); h.TryGetValue("Cookie", out cookieHdr);
            if (hostHdr != null && hostHdr.StartsWith("localhost") && cookieHdr != null && cookieHdr.Contains("sess=abc")) Bump(id + ":leak");
            // Usuario y contraseña enviados a OTRO servidor (localhost en vez de 127.0.0.1).
            string authHdr;
            if (hostHdr != null && hostHdr.StartsWith("localhost") && h.TryGetValue("Authorization", out authHdr) && !string.IsNullOrEmpty(authHdr)) Bump(id + ":authleak");
            int version = VersionOf(id);
            string etag = "\"v" + version + "\"";
            // Como el CDN de arXiv: nodos que dan ETags distintos para los mismos bytes.
            if (modes.Contains("etagflap")) etag = (reqNo % 2 == 0) ? "\"nodoA\"" : "\"nodoB\"";
            string lastMod = new DateTime(2026, 1, version, 10, 0, 0, DateTimeKind.Utc).ToString("R", CultureInfo.InvariantCulture);
            if (modes.Contains("lmflap"))
            {
                lastMod = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc).AddSeconds(reqNo).ToString("R", CultureInfo.InvariantCulture);
                etag = "\"v" + version + "-" + reqNo + "\""; // como Apache: el ETag lleva la fecha
            }
            if (modes.Contains("badlm")) lastMod = "ayer por la tarde";
            if (modes.Contains("futurelm")) lastMod = new DateTime(2100, 1, 1, 10, 0, 0, DateTimeKind.Utc).ToString("R", CultureInfo.InvariantCulture);
            if (modes.Contains("oldlm")) lastMod = "Thursday, 05-Mar-15 08:09:10 GMT";
            if (modes.Contains("asclm")) lastMod = "Thu Mar  5 08:09:10 2015";

            if (modes.Contains("slowhead")) Thread.Sleep(3000);
            if (modes.Contains("proxyonly") && !proxied) { Bump(id + ":direct403"); Simple(ns, 403, "Forbidden", "text/html", "<html>bloqueado en tu país</html>"); return; }
            if (modes.Contains("notfound")) { Simple(ns, 404, "Not Found", "text/html", "<html>404</html>"); return; }
            if (modes.Contains("forbidden")) { Simple(ns, 403, "Forbidden", "text/html", "<html>403</html>"); return; }
            if (modes.Contains("flaky503") && reqNo <= 2) { Simple(ns, 503, "Service Unavailable", "text/html", "<html>503</html>"); return; }
            if (modes.Contains("ra503") && reqNo <= 1) { WriteHead(ns, 503, "Service Unavailable", new string[] { "Retry-After: 2", "Content-Length: 0" }); return; }
            if (modes.Contains("ban"))
            {
                WriteHead(ns, 429, "Too Many Requests", new string[] { "Retry-After: 600", "Content-Length: 0" });
                return;
            }
            // Descarga que el sitio solo da tras enviar el formulario (POST con token=secreto).
            if (modes.Contains("post"))
            {
                if (rl[0] != "POST" || body.IndexOf("token=secreto", StringComparison.Ordinal) < 0)
                {
                    Bump(id + ":nopost");
                    Simple(ns, 403, "Forbidden", "text/html", "<html>formulario requerido</html>");
                    return;
                }
                Bump(id + ":post");
            }
            if (modes.Contains("needauth"))
            {
                string cookie, referer, ua;
                h.TryGetValue("Cookie", out cookie); h.TryGetValue("Referer", out referer); h.TryGetValue("User-Agent", out ua);
                bool ok = cookie != null && cookie.Contains("sess=abc") && referer == "http://ref.example/page" && ua != null && ua.Contains("TestUA/1");
                if (!ok) { Bump(id + ":denied"); Simple(ns, 403, "Forbidden", "text/html", "<html>login</html>"); return; }
            }
            if (modes.Contains("basicauth"))
            {
                string auth;
                h.TryGetValue("Authorization", out auth);
                if (auth != "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("usuario:clave")))
                {
                    Bump(id + ":401");
                    WriteHead(ns, 401, "Unauthorized", new string[] { "WWW-Authenticate: Basic realm=\"pruebas\"", "Content-Length: 0" });
                    return;
                }
            }
            if (modes.Contains("setcookie") && reqNo > 1 && (cookieHdr == null || !cookieHdr.Contains("token=xyz")))
            {
                Bump(id + ":nocookie");
                Simple(ns, 403, "Forbidden", "text/html", "<html>falta la cookie</html>");
                return;
            }

            // Rango pedido.
            long a = -1, b = -1;
            string range;
            if (h.TryGetValue("Range", out range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                string r = range.Substring(6);
                int dash = r.IndexOf('-');
                if (dash > 0)
                {
                    a = long.Parse(r.Substring(0, dash), CultureInfo.InvariantCulture);
                    string bs = r.Substring(dash + 1);
                    b = bs.Length > 0 ? long.Parse(bs, CultureInfo.InvariantCulture) : size - 1;
                    if (b > size - 1) b = size - 1;
                }
            }
            bool isProbe = a == 0 && b == 0;
            if (isProbe) Bump(id + ":probe");
            if (a > 0) Bump(id + ":ranged");
            if (range != null && range.Trim() == "bytes=0-") Bump(id + ":first"); // primera conexión al estilo IDM

            bool honorRange = a >= 0 && !modes.Contains("norange");
            // "Deja de respetar Range a mitad": solo la primera petición (o el sondeo 0-0) recibe 206.
            if (modes.Contains("ignorerange") && !isProbe && reqNo > 1) honorRange = false;
            // If-Range: si el validador no coincide con la versión actual, se manda el archivo entero (RFC 7233).
            string ifRange;
            if (honorRange && h.TryGetValue("If-Range", out ifRange) && ifRange != etag && ifRange != lastMod)
            {
                honorRange = false;
                Bump(id + ":ifrange-miss");
            }

            // Enlace que caduca a mitad: la primera petición (o el sondeo) recibe el archivo; las demás, una página.
            if (modes.Contains("html") && !isProbe && reqNo > 1)
            {
                Simple(ns, 200, "OK", "text/html; charset=utf-8", "<html><body>Su enlace ha caducado. Inicie sesión.</body></html>");
                return;
            }

            if (honorRange && (a >= size || size == 0))
            {
                WriteHead(ns, 416, "Range Not Satisfiable", new string[] { "Content-Range: bytes */" + size, "Content-Length: 0" });
                return;
            }

            long bodyStart, bodyLen;
            List<string> hdr = new List<string>();
            hdr.Add("Content-Type: application/octet-stream");
            hdr.Add("ETag: " + etag);
            if (!modes.Contains("nolm")) hdr.Add("Last-Modified: " + lastMod);
            if (modes.Contains("cd"))
                hdr.Add("Content-Disposition: attachment; filename*=UTF-8''informe%20final%20%E2%82%AC.pdf; filename=\"fallback.pdf\"");
            if (modes.Contains("setcookie") && reqNo == 1) hdr.Add("Set-Cookie: token=xyz; Path=/");
            int status;
            if (honorRange)
            {
                long start = a;
                if (modes.Contains("shift") && a > 0) start = Math.Min(a + 1, size - 1);
                bodyStart = start; bodyLen = b - start + 1;
                status = 206;
                if (!modes.Contains("nocr"))
                    hdr.Add(string.Format(CultureInfo.InvariantCulture, "Content-Range: bytes {0}-{1}/{2}", start, b, size));
                hdr.Add("Accept-Ranges: bytes");
            }
            else
            {
                bodyStart = 0; bodyLen = size;
                status = 200;
                if (!modes.Contains("norange")) hdr.Add("Accept-Ranges: bytes");
            }
            if (!modes.Contains("nolen")) hdr.Add("Content-Length: " + bodyLen.ToString(CultureInfo.InvariantCulture));

            long limit = bodyLen;
            if (modes.Contains("cut") && bodyLen > 1000)
            {
                int n = Bump(id + ":cutcount");
                if (n <= 2) limit = bodyLen * 4 / 10;
            }
            if (modes.Contains("cutall") && bodyLen > 1000)
            {
                Bump(id + ":cutcount");
                limit = Math.Min(bodyLen * 4 / 10, 8192);
            }

            // Como Wikimedia: más de 2 conexiones a la vez -> 429 con Retry-After.
            bool limited = modes.Contains("limit2") && !isProbe;
            if (limited)
            {
                int now = Bump(id + ":concurrent");
                lock (_counters) { int mx; _counters.TryGetValue(id + ":maxconcurrent", out mx); if (now > mx) _counters[id + ":maxconcurrent"] = Math.Min(now, 2); }
                if (now > 2)
                {
                    lock (_counters) _counters[id + ":concurrent"]--;
                    Bump(id + ":429");
                    WriteHead(ns, 429, "Too Many Requests", new string[] { "Retry-After: 1", "Content-Length: 0" });
                    return;
                }
            }
            // Conexiones simultáneas enviando datos (para comprobar límites por servidor).
            int conc = Bump(id + ":conc");
            lock (_counters) { int mx; _counters.TryGetValue(id + ":maxconc", out mx); if (conc > mx) _counters[id + ":maxconc"] = conc; }
            try
            {
                WriteHead(ns, status, status == 206 ? "Partial Content" : "OK", hdr.ToArray());
                WriteBody(ns, client, modes, bodyStart, bodyLen, limit, version);
            }
            finally
            {
                lock (_counters) _counters[id + ":conc"]--;
                if (limited) lock (_counters) _counters[id + ":concurrent"]--;
            }
        }

        void WriteBody(NetworkStream ns, TcpClient client, HashSet<string> modes, long bodyStart, long bodyLen, long limit, int version)
        {
            int chunk = 64 * 1024, delay = 0;
            if (modes.Contains("slow")) { chunk = 16 * 1024; delay = 30; }
            if (modes.Contains("veryslow")) { chunk = 8 * 1024; delay = 50; }
            byte[] buf = new byte[chunk];
            long sent = 0;
            try
            {
                while (sent < limit)
                {
                    int n = (int)Math.Min(chunk, limit - sent);
                    Fill(buf, bodyStart + sent, n, version);
                    ns.Write(buf, 0, n);
                    sent += n;
                    if (delay > 0) Thread.Sleep(delay);
                }
                ns.Flush();
            }
            catch (IOException) { }
            if (limit < bodyLen) client.Client.LingerState = new LingerOption(true, 0); // corte brusco
        }

        // Página con enlaces de descarga para probar la extensión en un navegador real.
        string TestPage()
        {
            string ts = DateTime.Now.Ticks.ToString("x");
            return "<!doctype html><html><head><meta charset='utf-8'><title>Pruebas IDMClone</title></head><body style='font:16px Segoe UI'>" +
                "<h1>Página de pruebas de IDMClone</h1><ul>" +
                "<li><a id='zip' href='/f/range,slow/paquete.zip?size=3145728&id=zip" + ts + "'>paquete.zip (3 MB, se debe capturar)</a></li>" +
                "<li><a id='php' href='/f/range,slow,cd/descargar.php?size=1048576&id=php" + ts + "'>descargar.php (el servidor lo llama «informe final €.pdf»)</a></li>" +
                "<li><a id='png' href='/f/range/imagen.png?size=20480&id=png" + ts + "' download>imagen.png (no se debe capturar)</a></li>" +
                "<li><a id='exe' href='/f/range,slow/instalador.exe?size=2097152&id=exe" + ts + "'>instalador.exe (2 MB)</a></li>" +
                "<li><a id='doc' href='/f/range/manual.pdf?size=524288&id=doc" + ts + "'>manual.pdf</a></li>" +
                "<li><a id='web' href='/page?otra=1'>otra página (html)</a></li>" +
                "</ul>" +
                "<form id='form' method='post' action='/f/range,slow,post/informe-post.pdf?size=1048576&id=post" + ts + "'>" +
                "<input type='hidden' name='token' value='secreto'><button id='enviar' type='submit'>Generar informe (POST)</button></form>" +
                "</body></html>";
        }

        static void WriteHead(NetworkStream ns, int status, string reason, string[] headers)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
            foreach (string x in headers) sb.Append(x).Append("\r\n");
            sb.Append("Connection: close\r\n\r\n");
            byte[] data = Encoding.ASCII.GetBytes(sb.ToString());
            ns.Write(data, 0, data.Length);
        }

        static void Simple(NetworkStream ns, int status, string reason, string type, string body)
        {
            byte[] b = Encoding.UTF8.GetBytes(body);
            WriteHead(ns, status, reason, new string[] { "Content-Type: " + type, "Content-Length: " + b.Length });
            ns.Write(b, 0, b.Length);
        }
    }
}
