using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace IDMClone
{
    // Host de mensajería nativa (equivalente legítimo y propio de IDMMsgHost del análisis):
    // el navegador lo lanza, le manda mensajes JSON por stdin/stdout con prefijo de 4 bytes,
    // y este los reenvía a la ventana de IDMClone por la tubería con nombre. Si IDMClone no
    // está abierto, lo abre (oculto en la bandeja). NO interpreta tráfico ni toca otras aplicaciones.
    //
    // La extensión mantiene el canal abierto y puede mandar varias peticiones seguidas; cada una
    // lleva un "rid" que se devuelve en su respuesta, así pueden contestarse en cualquier orden.
    public static class NativeHost
    {
        static readonly object _outLock = new object();

        // Navegador que lanzó este host ("Brave", "Google Chrome", "Mozilla Firefox"... o "?"), por el proceso padre.
        // Sirve para "Capturar descargas para los siguientes navegadores" (#1). Las pruebas lo fijan a mano.
        public static string Browser { get; internal set; }

        // Escribe un mensaje a la extensión (solo mientras corre Run): lo usa CookieBridge para pedir cookies.
        static Action<string> _toExtension;

        // Registro del host (%APPDATA%\IDMClone\host.log): qué pide el navegador y qué se le contesta.
        // Sirve para saber dónde se queda una descarga si "no pasa nada" (sin datos privados: ni cookies ni formularios).
        public static void Log(string text)
        {
            try
            {
                string p = Path.Combine(AppConfig.Dir, "host.log");
                Directory.CreateDirectory(AppConfig.Dir);
                FileInfo fi = new FileInfo(p);
                if (fi.Exists && fi.Length > 256 * 1024) { File.Delete(p + ".old"); File.Move(p, p + ".old"); }
                File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + text + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        static string Short(string s) { return s == null ? "" : s.Length > 300 ? s.Substring(0, 300) + "..." : s; }

        // Quién lanzó el host: "chrome-extension://<id>/" (Chromium) o el id de la extensión (Firefox).
        static string Origin(string[] args)
        {
            for (int i = 1; i < args.Length; i++)
                if (args[i].StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) || args[i].StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase)) return args[i];
            return args.Length > 2 ? args[2] : args.Length > 1 ? args[1] : "?"; // Firefox: <manifiesto.json> <id>
        }

        public static int Run()
        {
            string origin = Origin(Environment.GetCommandLineArgs());
            Log("host iniciado por " + origin);
            Browser = ExtensionSeen.Record(origin);
            Log("navegador: " + Browser);
            Stream stdin = Console.OpenStandardInput();
            Stream stdout = Console.OpenStandardOutput();
            _toExtension = delegate(string m) { Write(stdout, m); };
            int inFlight = 0;

            while (true)
            {
                byte[] lenBuf = ReadExact(stdin, 4);
                if (lenBuf == null) break;
                int len = BitConverter.ToInt32(lenBuf, 0); // orden nativo (little-endian en Windows)
                if (len <= 0 || len > 64 * 1024 * 1024) break;
                byte[] data = ReadExact(stdin, len);
                if (data == null) break;

                string msg = Encoding.UTF8.GetString(data);
                Interlocked.Increment(ref inFlight);
                // Cada petición en su hilo: un arranque en frío de IDMClone no bloquea un "ping".
                Thread t = new Thread(delegate()
                {
                    try
                    {
                        string reply;
                        try { reply = HandleMessage(msg); }
                        catch (Exception ex) { reply = Ipc.ErrorJson(ex.Message); }
                        Write(stdout, reply);
                    }
                    finally { Interlocked.Decrement(ref inFlight); }
                });
                t.IsBackground = true;
                t.Start();
            }
            // El navegador cerró el canal: terminar lo que estaba en marcha (como mucho 30 s).
            for (int i = 0; i < 300 && inFlight > 0; i++) Thread.Sleep(100);
            return 0;
        }

        static void Write(Stream stdout, string reply)
        {
            byte[] rb = Encoding.UTF8.GetBytes(reply);
            lock (_outLock)
            {
                try
                {
                    stdout.Write(BitConverter.GetBytes(rb.Length), 0, 4);
                    stdout.Write(rb, 0, rb.Length);
                    stdout.Flush();
                }
                catch (IOException) { }
            }
        }

        public static string HandleMessage(string msg)
        {
            string type = "", rid = null;
            bool auto = false, cookieCaps = false;
            try
            {
                Dictionary<string, object> d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(msg);
                if (d != null)
                {
                    object v;
                    if (d.TryGetValue("type", out v) && v != null) type = v.ToString();
                    if (d.TryGetValue("rid", out v) && v != null) rid = new JavaScriptSerializer().Serialize(v);
                    // Captura automática (no un clic del usuario en el menú o el panel): extensión 1.4+.
                    if (d.TryGetValue("auto", out v) && v is bool) auto = (bool)v;
                    // La extensión sabe contestar a "getcookies" (CookieBridge): extensión 1.4+.
                    System.Collections.IEnumerable caps;
                    if (d.TryGetValue("caps", out v) && !(v is string) && (caps = v as System.Collections.IEnumerable) != null)
                        foreach (object c in caps) if (c != null && c.ToString() == "getcookies") cookieCaps = true;
                }
            }
            catch { }

            string url = null;
            try
            {
                Dictionary<string, object> d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(msg);
                object u; if (d != null && d.TryGetValue("url", out u) && u != null) url = u.ToString();
            }
            catch { }
            Log("<- " + type + (url != null ? " " + Short(url) : ""));
            string reply;
            if (type == "ping")
            {
                // Comprobar sin arrancar IDMClone: el popup solo quiere saber si todo está en su sitio.
                string r = Ipc.Request("{\"type\":\"ping\"}", 300, 3000);
                bool gui = r != null && r.IndexOf("\"gui\":true", StringComparison.Ordinal) >= 0;
                reply = "{\"ok\":true,\"host\":\"IDMClone\",\"gui\":" + (gui ? "true" : "false") + "}";
            }
            else if (type == "probe")
            {
                // Panel "Descargar este video": calidades de una lista HLS/DASH que está reproduciendo la página.
                // Lo resuelve el propio host (lee la lista con las cookies del navegador) sin abrir la ventana.
                reply = ProbeReply(msg);
            }
            else if (type == "getconfig")
            {
                // Opciones > Tipos de archivo: la extensión captura lo que diga IDMClone (como IDM).
                // Se lee el archivo de configuración sin abrir la ventana.
                reply = ConfigReply();
                if (cookieCaps && _toExtension != null) CookieBridge.StartHostPolling(Browser, _toExtension);
            }
            else if (type == "cookies")
            {
                // Respuesta de la extensión a una petición de cookies de IDMClone (Grabber): se pasa a la GUI.
                reply = CookieBridge.HostDeliver(msg);
            }
            else if (type == "download" && auto && CaptureOff(ReadConfig(), Browser))
            {
                // Opciones > General: este navegador está desmarcado. La extensión 1.4 ya no lo envía; esto cubre
                // el caso de que su configuración aún no se hubiera refrescado. El navegador la reanuda.
                reply = Ipc.ErrorJson("La captura está desactivada para " + Browser + " en IDMClone (Opciones > General).");
            }
            else
            {
                // Descarga: reenviar el JSON a la GUI y devolver SU respuesta. Solo si la GUI confirma
                // que la añadió ("ok":true) la extensión quitará la descarga del navegador.
                reply = EnsureGuiAndRequest(msg) ?? Ipc.ErrorJson("No se pudo contactar con IDMClone.");
            }
            Log("-> " + type + ": " + Short(reply.StartsWith("{\"ok\":true,\"exts\"") ? "{ok:true, configuración}" : reply));
            return WithRid(reply, rid);
        }

        public static string ConfigReply()
        {
            AppConfig c = null;
            try
            {
                if (File.Exists(AppConfig.ConfigPath))
                {
                    string json = File.ReadAllText(AppConfig.ConfigPath);
                    c = AppConfig.CreateDefaultForUpgrade(AppConfig.NewSerializer().Deserialize<AppConfig>(json), json);
                }
            }
            catch { c = null; }
            if (c == null) c = AppConfig.CreateDefault();
            c.Normalize();
            List<string> exts = new List<string>();
            foreach (string e in (c.CaptureExts ?? "").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) exts.Add(e.ToLowerInvariant());
            List<string> exclude = new List<string>((c.CaptureExcludeSites ?? "").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            Dictionary<string, object> o = new Dictionary<string, object>();
            o["ok"] = true; o["exts"] = exts; o["exclude"] = exclude;
            // Teclas especiales (Opciones > General): la extensión las vigila en las páginas.
            o["preventKeys"] = c.UseKeyToPrevent ? c.PreventKeys : "";
            o["forceKeys"] = c.UseKeyToForce ? c.ForceKeys : "";
            // Panel de descarga para reproductores web (Opciones > General).
            o["mediaPanel"] = c.MediaPanel; o["mediaMini"] = c.MediaPanelMini;
            o["mediaExclude"] = new List<string>((c.MediaPanelExcludeSites ?? "").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            o["minVideoKB"] = c.MediaPanelMinVideoKB; o["minAudioKB"] = c.MediaPanelMinAudioKB;   // generales: tipos fuera de "mediaTypes" (0 de serie, como IDM)
            // ---- Claves de la versión 1.4 (todas opcionales: una extensión antigua las ignora). Siempre DESPUÉS de "exts":
            // el registro reconoce esta respuesta por su comienzo.
            // #1 Capturar descargas para los siguientes navegadores: el navegador que llama y si está desmarcado.
            string browser = Browser ?? "?";
            o["browser"] = browser;
            o["captureOff"] = CaptureOff(c, browser);
            // #2 Menú contextual: la extensión elige la pareja según sea Firefox o Chromium.
            o["menuChrome"] = Flags(c.BrowserMenuDownload, c.BrowserMenuDownloadAll);
            o["menuFirefox"] = Flags(c.FirefoxMenuDownload, c.FirefoxMenuDownloadAll);
            // #8 Tipos de archivo del panel, con su mínimo; #9 y #10.
            List<object> types = new List<object>();
            foreach (AppConfig.MediaTypeRule r in AppConfig.ParseMediaTypes(c))
            {
                Dictionary<string, object> t = new Dictionary<string, object>();
                t["ext"] = r.Ext; t["minKB"] = r.MinKB; t["on"] = r.On;
                types.Add(t);
            }
            o["mediaTypes"] = types;
            o["mediaNoAutoCapture"] = c.MediaNoAutoCapture;
            o["mediaProtected"] = c.MediaPanelProtected;
            // #11 Panel para los enlaces seleccionados.
            Dictionary<string, object> sel = new Dictionary<string, object>();
            sel["mode"] = c.SelectionPanelMode == 1 || c.SelectionPanelMode == 2 ? c.SelectionPanelMode : 0;
            sel["mini"] = c.SelectionPanelMini;
            sel["sites"] = new List<string>((c.SelectionPanelSites ?? "").Split(new char[] { ' ', '\r', '\n', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries));
            o["selectionPanel"] = sel;
            // Versión de la extensión que trae esta copia de IDMClone (browser\extension): tras actualizar IDMClone, una
            // extensión cargada a mano desde esa carpeta ve que hay una más nueva y se recarga sola (extensión 1.5+).
            string ev = ExtensionVersionOnDisk(ExtensionFolder);
            if (ev != null) o["extVersion"] = ev;
            return new JavaScriptSerializer().Serialize(o);
        }

        // Carpeta de la extensión junto a IDMClone.exe (la que se carga en el navegador); las pruebas la cambian.
        public static string ExtensionFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"browser\extension");

        // "version" del manifest.json de esa carpeta, o null si no está o no se puede leer.
        public static string ExtensionVersionOnDisk(string folder)
        {
            try
            {
                string p = Path.Combine(folder, "manifest.json");
                if (!File.Exists(p)) return null;
                Dictionary<string, object> m = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(p));
                object v;
                return m != null && m.TryGetValue("version", out v) && v is string && ((string)v).Length > 0 ? (string)v : null;
            }
            catch { return null; }
        }

        static Dictionary<string, object> Flags(bool link, bool all)
        {
            Dictionary<string, object> f = new Dictionary<string, object>();
            f["link"] = link; f["all"] = all;
            return f;
        }

        // ¿Está desmarcado este navegador en "Capturar descargas para los siguientes navegadores"? Desconocido ("?"): no.
        public static bool CaptureOff(AppConfig c, string browser)
        {
            if (c == null || string.IsNullOrEmpty(browser) || browser == "?") return false;
            foreach (string b in (c.CaptureBrowsersOff ?? "").Split(';'))
                if (string.Equals(b.Trim(), browser.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // "browserProxy" de un mensaje de descarga (#17): "host:puerto" de un proxy HTTP (también con "http://") o de
        // un SOCKS con su esquema ("socks5://", "socks5h://", "socks://", "socks4://", "socks4a://"); sin usuario, ruta
        // ni otros esquemas (un proxy con TLS, "https://", no lo habla IDMClone).
        // MainForm lo valida con esto antes de pasarlo a AddOptions.BrowserProxy.
        public static bool ValidBrowserProxy(string p)
        {
            if (string.IsNullOrEmpty(p) || p.Length > 300) return false;
            foreach (string scheme in new string[] { "http://", "socks5h://", "socks5://", "socks4a://", "socks4://", "socks://" })
                if (p.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) { p = p.Substring(scheme.Length); break; }
            int colon = p.LastIndexOf(':');
            if (colon <= 0 || colon == p.Length - 1) return false;
            string host = p.Substring(0, colon), port = p.Substring(colon + 1);
            int n;
            if (!int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out n) || n < 1 || n > 65535) return false;
            if (host.StartsWith("[") && host.EndsWith("]")) host = host.Substring(1, host.Length - 2); // IPv6
            else if (host.IndexOf(':') >= 0) return false;                                            // IPv6 sin corchetes: ambiguo
            if (host.Length == 0) return false;
            foreach (char ch in host)
                if (ch > 127 || !(char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' || ch == '_' || ch == ':')) return false;
            return Uri.CheckHostName(host) != UriHostNameType.Unknown;
        }

        static AppConfig ReadConfig()
        {
            AppConfig c = null;
            try
            {
                if (File.Exists(AppConfig.ConfigPath))
                {
                    string json = File.ReadAllText(AppConfig.ConfigPath);
                    c = AppConfig.CreateDefaultForUpgrade(AppConfig.NewSerializer().Deserialize<AppConfig>(json), json);
                }
            }
            catch { c = null; }
            if (c == null) c = AppConfig.CreateDefault();
            c.Normalize();
            return c;
        }

        public static string ProbeReply(string msg)
        {
            Dictionary<string, object> d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(msg);
            Func<string, string> get = delegate(string k) { object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : null; };
            string url = get("url");
            if (!Util.LooksLikeUrl(url)) return Ipc.ErrorJson("URL no válida.");
            try { return DownloadEngine.ProbeMedia(ReadConfig(), url, get("cookies"), get("referrer"), get("userAgent")); }
            catch (Exception ex) { return Ipc.ErrorJson(ex.Message); }
        }

        // Añade "rid" a la respuesta JSON de la GUI ({...} -> {"rid":N,...}).
        static string WithRid(string reply, string rid)
        {
            if (rid == null) return reply;
            string r = reply.Trim();
            if (!r.StartsWith("{")) return "{\"rid\":" + rid + ",\"ok\":false,\"error\":" + Ipc.JsonQuote(r) + "}";
            return "{\"rid\":" + rid + (r.Length > 2 ? "," : "") + r.Substring(1);
        }

        const int ReplyTimeoutMs = 20000;
        const int LaunchWaitMs = 18000;

        static string EnsureGuiAndRequest(string json)
        {
            bool connected;
            string reply = Ipc.Request(json, 1000, ReplyTimeoutMs, Ipc.PipeName, out connected);
            if (reply != null) return reply;
            // La GUI está abierta pero no contestó: no reintentar (podría añadirse dos veces).
            if (connected) return Ipc.ErrorJson("IDMClone no respondió a tiempo.");

            // Arrancar IDMClone oculto en la bandeja. ShellExecute: el proceso nuevo no hereda las
            // tuberías stdin/stdout del navegador (si las heredara, el navegador no vería cerrarse el host).
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                ProcessStartInfo psi = new ProcessStartInfo(exe, "/tray");
                psi.UseShellExecute = true;
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                Process.Start(psi);
            }
            catch (Exception ex) { Log("no se pudo abrir IDMClone: " + ex.Message); return Ipc.ErrorJson("No se pudo abrir IDMClone: " + ex.Message); }
            Log("IDMClone no estaba abierto: se abre en la bandeja");

            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < LaunchWaitMs)
            {
                Thread.Sleep(200);
                reply = Ipc.Request(json, 300, ReplyTimeoutMs, Ipc.PipeName, out connected);
                if (reply != null) return reply;
                if (connected) return Ipc.ErrorJson("IDMClone no respondió a tiempo.");
            }
            return Ipc.ErrorJson("IDMClone no arrancó a tiempo.");
        }

        static byte[] ReadExact(Stream s, int count)
        {
            byte[] buf = new byte[count];
            int off = 0;
            while (off < count)
            {
                int r = s.Read(buf, off, count - off);
                if (r <= 0) return null; // EOF: el navegador cerró la conexión
                off += r;
            }
            return buf;
        }
    }
}
