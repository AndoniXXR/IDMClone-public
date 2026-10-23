using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace IDMClone
{
    // Pide al navegador (por la extensión) las cookies de un sitio: lo usa el Grabber tras "Iniciar sesión
    // manualmente en una web" (#18), para explorar con la sesión que el usuario abrió en su navegador
    // (incluidas las cookies HttpOnly, que una página no puede leer pero la API chrome.cookies sí).
    //
    // Protocolo (canal GUI -> host nativo -> extensión; el canal nativo solo lo puede abrir el navegador):
    //   1. La GUI de IDMClone atiende una tubería propia "<Ipc.PipeName>_cookies" (solo el usuario actual;
    //      además comprueba que quien se conecta es IDMClone.exe: misma ruta o mismo contenido).
    //   2. Cada host nativo cuya extensión dice saber responder (getconfig con caps:["getcookies"], extensión 1.4+)
    //      mantiene una espera larga: {"type":"waitcmd","host":"<pid>","browser":"Brave"}. La GUI contesta a los
    //      LongPollMs con {"type":"none"} (y el host vuelve a esperar) o, si alguien llamó a GetCookies, con
    //      {"type":"getcookies","req":N,"url":"https://..."}. El host solo acepta órdenes de una GUI que sea
    //      IDMClone.exe del mismo usuario y sesión (Ipc.ServerIsTrusted + misma imagen).
    //   3. El host escribe a la extensión, sin "rid" (no es respuesta a nada suyo): {"type":"getcookies","req":N,"url":...}.
    //   4. La extensión contesta con una petición normal (con "rid"): {"type":"cookies","req":N,"url":...,
    //      "cookies":"a=1; b=2","userAgent":"..."}: solo las cookies que el navegador mandaría a ESA dirección
    //      (cookies.getAll({url})). El host la reenvía a la GUI solo si el "req" es uno que él mismo pidió,
    //      añadiendo "host"; la GUI la acepta solo si mandó ese "req" a ese host.
    //      Regla de la extensión (para que el canal no sea un "oráculo" de cookies HttpOnly si otro programa del
    //      usuario se hiciera pasar por IDMClone): solo da cookies de un servidor que el usuario tiene abierto en una
    //      pestaña normal, o que abrió o al que navegó en los últimos 15 minutos (el mismo servidor o su gemelo con/sin
    //      "www."; nunca cuentan las pestañas de incógnito). Si no, contesta {"ok":false,"cookies":"","reason":"notopen"}
    //      y la GUI se queda sin cookies (el Grabber ofrece "Pegar cookies"). El inicio de sesión manual cumple la regla:
    //      IDMClone abre la página en el navegador, el usuario inicia sesión y pulsa Continuar. Un proyecto programado
    //      solo recibe la sesión si el sitio está abierto o se visitó hace poco; si no, explora sin ella.
    //   5. GetCookies devuelve la primera respuesta con cookies; "" si los navegadores contestaron sin cookies;
    //      null si nadie contestó a tiempo. Nada se guarda en disco ni en el registro del host (host.log solo
    //      anota el tipo de mensaje y la dirección); la petición se olvida al terminar.
    // Compatibilidad: una extensión antigua no anuncia "getcookies" y su host no espera órdenes (GetCookies -> null);
    // un host antiguo nunca abre la tubería.
    public static class CookieBridge
    {
        public static string PipeName { get { return Ipc.PipeName + "_cookies"; } }
        internal static int LongPollMs = 25000;
        internal static int PollIdleMs = 2000;   // sin GUI: cada cuánto mira el host si ya está abierta
        const int MaxCookieChars = 64 * 1024;

        // ================================================================== lado de la GUI

        class Waiter { public string HostId, Browser, Command; public readonly ManualResetEvent Ready = new ManualResetEvent(false); }
        class Pending
        {
            public long Id; public string Command, Url;
            public readonly HashSet<string> SentTo = new HashSet<string>();
            public readonly HashSet<string> Replied = new HashSet<string>();
            public string Cookies, UserAgent;
        }

        static readonly object _lock = new object();
        static readonly List<Waiter> _waiters = new List<Waiter>();
        static readonly Dictionary<long, Pending> _pending = new Dictionary<long, Pending>();
        static readonly Dictionary<string, DateTime> _seen = new Dictionary<string, DateTime>(); // host -> última espera
        // Números de petición al azar: tras reiniciar IDMClone no se reutilizan los de antes (una respuesta tardía
        // de entonces no puede colarse en una petición nueva).
        static long _nextReq = new Random().Next(1, int.MaxValue / 2) * 1000L;
        static Thread _server;
        static volatile int _serverGen; // cada Start, una generación: al parar, el hilo viejo termina solo
        static string _serverPipe;
        static DateTime _startedUtc = DateTime.MinValue;

        // Arranca la tubería de la GUI (lo hace solo la primera llamada a Available o GetCookies; MainForm puede
        // llamarlo al arrancar para que los hosts ya estén esperando cuando el Grabber lo necesite).
        public static void Start()
        {
            lock (_lock)
            {
                if (_server != null) return;
                _serverPipe = PipeName;
                int gen = ++_serverGen;
                _startedUtc = DateTime.UtcNow;
                string name = _serverPipe;
                _server = new Thread(delegate() { ServerLoop(gen, name); });
                _server.IsBackground = true;
                _server.Name = "cookie-bridge";
                _server.Start();
            }
        }

        // Para las pruebas: cierra la tubería y suelta las esperas.
        internal static void Stop()
        {
            string name;
            lock (_lock)
            {
                if (_server == null) return;
                _serverGen++;
                _server = null;
                name = _serverPipe;
                foreach (Waiter w in _waiters) { w.Command = "{\"type\":\"none\"}"; w.Ready.Set(); }
                _waiters.Clear();
                _seen.Clear();
            }
            try { using (NamedPipeClientStream c = new NamedPipeClientStream(".", name, PipeDirection.InOut)) c.Connect(200); } catch { }
        }

        // ¿Hay algún navegador con la extensión 1.4+ conectado ahora (esperando órdenes)? Si la tubería acaba de
        // abrirse, se espera un momento a que los hosts la vean (la miran cada PollIdleMs).
        public static bool Available
        {
            get
            {
                Start();
                lock (_lock)
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    int wait = (DateTime.UtcNow - _startedUtc).TotalMilliseconds < PollIdleMs + 1500 ? PollIdleMs + 1500 : 0;
                    while (true)
                    {
                        Prune();
                        if (_waiters.Count > 0 || _seen.Count > 0) return true;
                        int left = wait - (int)sw.ElapsedMilliseconds;
                        if (left <= 0) return false;
                        Monitor.Wait(_lock, Math.Min(left, 100));
                    }
                }
            }
        }

        // Cabecera Cookie ("a=1; b=2") para esa dirección, o null si no se pudo obtener a tiempo.
        public static string GetCookies(string url, int timeoutMs)
        {
            string ua;
            return GetCookies(url, timeoutMs, out ua);
        }

        // Igual, y el User-Agent del navegador que las dio (algunos sitios ligan la sesión a él).
        public static string GetCookies(string url, int timeoutMs, out string userAgent)
        {
            userAgent = null;
            if (!ValidUrl(url)) return null;
            Start();
            Pending p = new Pending();
            Stopwatch sw = Stopwatch.StartNew();
            lock (_lock)
            {
                p.Id = ++_nextReq;
                p.Url = url;
                Dictionary<string, object> c = new Dictionary<string, object>();
                c["type"] = "getcookies"; c["req"] = p.Id; c["url"] = url;
                p.Command = new JavaScriptSerializer().Serialize(c);
                _pending[p.Id] = p;
                try
                {
                    while (true)
                    {
                        foreach (Waiter w in _waiters.ToArray())
                        {
                            if (w.Command != null || p.SentTo.Contains(w.HostId)) continue;
                            w.Command = p.Command; p.SentTo.Add(w.HostId);
                            _waiters.Remove(w);
                            w.Ready.Set();
                        }
                        if (!string.IsNullOrEmpty(p.Cookies)) break;
                        if (p.SentTo.Count > 0 && p.Replied.Count >= p.SentTo.Count)
                        {
                            // Todos los que se preguntaron contestaron (sin cookies): ¿queda algún navegador por preguntar?
                            Prune();
                            bool others = false;
                            foreach (string h in _seen.Keys) if (!p.SentTo.Contains(h)) { others = true; break; }
                            if (!others) break;
                        }
                        int left = timeoutMs - (int)sw.ElapsedMilliseconds;
                        if (left <= 0) break;
                        Monitor.Wait(_lock, Math.Min(left, 250));
                    }
                    userAgent = p.UserAgent;
                    if (!string.IsNullOrEmpty(p.Cookies)) return p.Cookies;
                    return p.Replied.Count > 0 ? "" : null;
                }
                finally { _pending.Remove(p.Id); }
            }
        }

        static void Prune()
        {
            DateTime limit = DateTime.UtcNow.AddMilliseconds(-(LongPollMs + 10000));
            List<string> old = new List<string>();
            foreach (KeyValuePair<string, DateTime> kv in _seen) if (kv.Value < limit) old.Add(kv.Key);
            foreach (string k in old) _seen.Remove(k);
        }

        static void ServerLoop(int gen, string name)
        {
            while (gen == _serverGen)
            {
                NamedPipeServerStream s = null;
                try
                {
                    s = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, CurrentUserOnly());
                    s.WaitForConnection();
                    if (gen != _serverGen) { s.Dispose(); break; }
                    NamedPipeServerStream conn = s;
                    s = null;
                    Thread h = new Thread(delegate() { Serve(conn); });
                    h.IsBackground = true;
                    h.Name = "cookie-bridge-conn";
                    h.Start();
                }
                catch
                {
                    if (s != null) { try { s.Dispose(); } catch { } }
                    Thread.Sleep(1000); // p. ej. otro proceso ocupa el nombre: no gastar CPU
                }
            }
        }

        static void Serve(NamedPipeServerStream conn)
        {
            try
            {
                using (conn)
                {
                    uint pid;
                    // Solo IDMClone (el host nativo) puede hablar por aquí.
                    if (!GetNamedPipeClientProcessId(conn.SafePipeHandle, out pid) || !IsIdmCloneProcess(pid)) return;
                    StreamReader sr = new StreamReader(conn, new UTF8Encoding(false));
                    StreamWriter sw = new StreamWriter(conn, new UTF8Encoding(false));
                    string line = sr.ReadLine();
                    if (line == null || line.Length > 2 * MaxCookieChars) return;
                    string reply = OnRequest(line);
                    try
                    {
                        sw.WriteLine(reply.Replace("\r", "").Replace("\n", " "));
                        sw.Flush();
                    }
                    catch
                    {
                        // El host se fue (navegador cerrado) con una petición ya asignada: cuenta como respondida
                        // sin cookies, para no esperar a que se agote el tiempo.
                        Undelivered(line, reply);
                        return;
                    }
                    try { conn.WaitForPipeDrain(); } catch { }
                }
            }
            catch { }
        }

        internal static void Undelivered(string request, string reply)
        {
            try
            {
                Dictionary<string, object> q = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(request);
                Dictionary<string, object> r = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reply);
                long req;
                if (q == null || r == null || Str(r, "type") != "getcookies" || !long.TryParse(Str(r, "req"), out req)) return;
                lock (_lock)
                {
                    Pending p;
                    if (_pending.TryGetValue(req, out p)) { p.Replied.Add(Str(q, "host")); Monitor.PulseAll(_lock); }
                }
            }
            catch { }
        }

        // Lo que llega de un host (público para las pruebas: no comprueba quién llama).
        internal static string OnRequest(string line)
        {
            Dictionary<string, object> d;
            try { d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(line); }
            catch { return Ipc.ErrorJson("Mensaje no válido."); }
            if (d == null) return Ipc.ErrorJson("Mensaje vacío.");
            string type = Str(d, "type"), host = Str(d, "host");
            if (host.Length == 0 || host.Length > 40) return Ipc.ErrorJson("Falta el host.");
            if (type == "waitcmd")
            {
                Waiter w = new Waiter(); w.HostId = host; w.Browser = Str(d, "browser");
                lock (_lock)
                {
                    _seen[host] = DateTime.UtcNow;
                    // Una petición en curso que este host aún no ha visto (p. ej. se reconectaba): se le da ya.
                    foreach (Pending p in _pending.Values)
                        if (!p.SentTo.Contains(host) && string.IsNullOrEmpty(p.Cookies)) { p.SentTo.Add(host); Monitor.PulseAll(_lock); w.Ready.Close(); return p.Command; }
                    _waiters.Add(w);
                    Monitor.PulseAll(_lock);
                }
                w.Ready.WaitOne(LongPollMs);
                string cmd;
                lock (_lock)
                {
                    _waiters.Remove(w);
                    _seen[host] = DateTime.UtcNow;
                    cmd = w.Command;
                }
                w.Ready.Close();
                return cmd ?? "{\"type\":\"none\"}";
            }
            if (type == "cookies")
            {
                long req;
                if (!long.TryParse(Str(d, "req"), out req)) return Ipc.ErrorJson("Petición no válida.");
                string cookies = Str(d, "cookies");
                if (cookies.Length > MaxCookieChars || cookies.IndexOfAny(new char[] { '\r', '\n' }) >= 0) cookies = "";
                lock (_lock)
                {
                    Pending p;
                    if (!_pending.TryGetValue(req, out p) || !p.SentTo.Contains(host)) return Ipc.ErrorJson("Nadie pidió esas cookies.");
                    // La dirección que devuelve la extensión tiene que ser la pedida.
                    string echoed = Str(d, "url");
                    if (echoed != p.Url) return Ipc.ErrorJson("Cookies de otra dirección.");
                    p.Replied.Add(host);
                    if (cookies.Length > 0 && string.IsNullOrEmpty(p.Cookies)) { p.Cookies = cookies; p.UserAgent = Str(d, "userAgent"); }
                    Monitor.PulseAll(_lock);
                }
                return Ipc.OkJson();
            }
            return Ipc.ErrorJson("Orden desconocida.");
        }

        // ================================================================== lado del host nativo

        static readonly object _hostLock = new object();
        static Thread _poller;
        static volatile int _pollGen; // cada arranque, una generación: al parar, el hilo viejo termina solo
        static Action<string> _toExtension;
        static string _hostBrowser;
        static readonly Dictionary<long, DateTime> _asked = new Dictionary<long, DateTime>(); // "req" que este host pasó a la extensión
        internal static string HostId = Process.GetCurrentProcess().Id.ToString();

        // El host empieza a esperar órdenes de la GUI (solo si su extensión sabe contestar).
        public static void StartHostPolling(string browser, Action<string> toExtension)
        {
            if (toExtension == null) return;
            lock (_hostLock)
            {
                if (_poller != null) return;
                _toExtension = toExtension; _hostBrowser = browser ?? "?";
                int gen = ++_pollGen;
                _poller = new Thread(delegate() { PollLoop(gen); });
                _poller.IsBackground = true;
                _poller.Name = "cookie-bridge-poll";
                _poller.Start();
            }
        }

        internal static void StopHostPolling()
        {
            lock (_hostLock) { _pollGen++; _poller = null; _asked.Clear(); }
        }

        static void PollLoop(int gen)
        {
            while (gen == _pollGen)
            {
                // Sin GUI abierta no se intenta conectar (comprobarlo cuesta un instante); se mira cada 2 s.
                if (!PipeExists(PipeName)) { Thread.Sleep(PollIdleMs); continue; }
                Dictionary<string, object> w = new Dictionary<string, object>();
                w["type"] = "waitcmd"; w["host"] = HostId; w["browser"] = _hostBrowser;
                string reply = TrustedRequest(new JavaScriptSerializer().Serialize(w), 500, LongPollMs + 5000);
                if (gen != _pollGen) break;
                if (reply == null) { Thread.Sleep(PollIdleMs); continue; }
                try { HostCommand(reply); } catch { }
            }
        }

        static void HostCommand(string reply)
        {
            Dictionary<string, object> d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reply);
            if (d == null || Str(d, "type") != "getcookies") return;
            long req; string url = Str(d, "url");
            if (!long.TryParse(Str(d, "req"), out req) || !ValidUrl(url)) return;
            Action<string> send;
            lock (_hostLock)
            {
                DateTime old = DateTime.UtcNow.AddMinutes(-2);
                List<long> stale = new List<long>();
                foreach (KeyValuePair<long, DateTime> kv in _asked) if (kv.Value < old) stale.Add(kv.Key);
                foreach (long k in stale) _asked.Remove(k);
                _asked[req] = DateTime.UtcNow;
                send = _toExtension;
            }
            Dictionary<string, object> m = new Dictionary<string, object>();
            m["type"] = "getcookies"; m["req"] = req; m["url"] = url;
            NativeHost.Log("-> getcookies " + url);
            if (send != null) send(new JavaScriptSerializer().Serialize(m));
        }

        // Respuesta de la extensión ({"type":"cookies","req":N,"cookies":...}): se pasa a la GUI.
        public static string HostDeliver(string msg)
        {
            Dictionary<string, object> d;
            try { d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(msg); }
            catch { return Ipc.ErrorJson("Mensaje no válido."); }
            long req;
            if (d == null || !long.TryParse(Str(d, "req"), out req)) return Ipc.ErrorJson("Petición no válida.");
            lock (_hostLock) { if (!_asked.Remove(req)) return Ipc.ErrorJson("Nadie pidió esas cookies."); }
            string cookies = Str(d, "cookies");
            if (cookies.Length > MaxCookieChars) cookies = "";
            Dictionary<string, object> o = new Dictionary<string, object>();
            o["type"] = "cookies"; o["req"] = req; o["host"] = HostId; o["url"] = Str(d, "url");
            o["cookies"] = cookies.Replace("\r", "").Replace("\n", "");
            o["userAgent"] = Str(d, "userAgent");
            string r = TrustedRequest(new JavaScriptSerializer().Serialize(o), 1000, 5000);
            if (r == null) return Ipc.ErrorJson("IDMClone no recibió las cookies.");
            return r.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0 ? Ipc.OkJson() : r;
        }

        // Petición a la GUI solo si quien atiende la tubería es IDMClone del mismo usuario y sesión.
        static string TrustedRequest(string line, int connectMs, int replyMs)
        {
            try
            {
                using (NamedPipeClientStream c = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut))
                {
                    c.Connect(connectMs);
                    uint pid;
                    if (!Ipc.ServerIsTrusted(c) || !GetNamedPipeServerProcessId(c.SafePipeHandle, out pid) || !IsIdmCloneProcess(pid)) return null;
                    StreamWriter w = new StreamWriter(c, new UTF8Encoding(false));
                    w.WriteLine(line.Replace("\r", "").Replace("\n", " "));
                    w.Flush();
                    string reply = null;
                    StreamReader r = new StreamReader(c, new UTF8Encoding(false));
                    Thread t = new Thread(delegate() { try { reply = r.ReadLine(); } catch { } });
                    t.IsBackground = true;
                    t.Start();
                    if (!t.Join(replyMs)) return null;
                    return reply;
                }
            }
            catch { return null; }
        }

        // ================================================================== utilidades

        internal static bool ValidUrl(string url)
        {
            Uri u;
            if (string.IsNullOrEmpty(url) || url.Length > 4096 || !Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            return (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && u.Host.Length > 0;
        }

        static string Str(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : ""; }

        static PipeSecurity CurrentUserOnly()
        {
            PipeSecurity ps = new PipeSecurity();
            ps.SetAccessRuleProtection(true, false);
            ps.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            return ps;
        }

        static bool PipeExists(string name)
        {
            if (WaitNamedPipe(@"\\.\pipe\" + name, 1)) return true;
            return Marshal.GetLastWin32Error() != 2; // ERROR_FILE_NOT_FOUND: no hay GUI; otro error (ocupada): existe
        }

        // ¿Ese proceso es IDMClone? El mismo proceso, o uno de esta sesión cuyo ejecutable es el mismo archivo que el
        // nuestro (misma ruta o mismo contenido: la GUI y el host pueden arrancarse desde copias idénticas).
        static readonly Dictionary<string, string> _hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static bool IsIdmCloneProcess(uint pid)
        {
            try
            {
                uint me = (uint)Process.GetCurrentProcess().Id;
                if (pid == me) return true;
                if (Process.GetProcessById((int)pid).SessionId != Process.GetCurrentProcess().SessionId) return false;
                string other = ImagePath(pid), mine = ImagePath(me);
                if (other == null || mine == null) return false;
                if (string.Equals(other, mine, StringComparison.OrdinalIgnoreCase)) return true;
                string a = FileHash(other), b = FileHash(mine);
                return a != null && a == b;
            }
            catch { return false; }
        }

        static string FileHash(string path)
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                string key = path + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
                lock (_hashes)
                {
                    string h;
                    if (_hashes.TryGetValue(key, out h)) return h;
                    using (SHA256 sha = SHA256.Create())
                    using (FileStream fs = File.OpenRead(path))
                        h = Convert.ToBase64String(sha.ComputeHash(fs));
                    _hashes[key] = h;
                    return h;
                }
            }
            catch { return null; }
        }

        static string ImagePath(uint pid)
        {
            const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally { CloseHandle(h); }
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool WaitNamedPipe(string name, uint timeoutMs);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    }
}
