using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace IDMClone.Tests
{
    // Opciones que antes solo se guardaban (área 5): ver scratchpad\opciones-adorno.md.
    // #18 Grabber: "Ingresa el usuario y contraseña manualmente en la siguiente web".
    public static partial class TestMain
    {
        static void RunOpciones5()
        {
            Run("opción 5: grabber con las cookies del navegador (solo a su sitio, también al descargar)", TO5_BrowserCookies);
            Run("opción 5: grabber con cookies pegadas (cabecera Cookie, https solo por https)", TO5_PastedCookies);
            Run("opción 5: grabber asistente: abre la web de inicio de sesión, pide pegar si no hay sesión y no guarda cookies", TO5_WizardLogin);
            Run("opción 5: grabber programado: pide la sesión en silencio y explora sin ella si no hay", TO5_Scheduled);
        }

        // Sitio de pruebas con inicio de sesión: /privado/ solo con la cookie sid=ok; /otro/ se sirve también
        // como "localhost" (otro servidor) para comprobar que no le llegan las cookies.
        class Op5Site
        {
            TcpListener _l;
            public int Port;
            public readonly List<string> Log = new List<string>(); // host|ruta|cookie
            public string Base { get { return "http://127.0.0.1:" + Port; } }
            public string Other { get { return "http://localhost:" + Port; } }

            public void Start()
            {
                _l = new TcpListener(IPAddress.Loopback, 0);
                _l.Start();
                Port = ((IPEndPoint)_l.LocalEndpoint).Port;
                Thread t = new Thread(delegate()
                {
                    while (true)
                    {
                        TcpClient c;
                        try { c = _l.AcceptTcpClient(); } catch { return; }
                        ThreadPool.QueueUserWorkItem(delegate { Serve(c); });
                    }
                });
                t.IsBackground = true; t.Start();
            }

            public void Stop() { try { _l.Stop(); } catch { } }

            public List<string> Requests(string hostPrefix)
            {
                List<string> r = new List<string>();
                lock (Log) foreach (string x in Log) if (x.StartsWith(hostPrefix, StringComparison.OrdinalIgnoreCase)) r.Add(x);
                return r;
            }

            void Serve(TcpClient c)
            {
                try
                {
                    using (c)
                    using (NetworkStream ns = c.GetStream())
                    {
                        StreamReader r = new StreamReader(ns, Encoding.ASCII);
                        string first = r.ReadLine();
                        if (first == null) return;
                        string[] rl = first.Split(' ');
                        string method = rl[0], path = rl.Length > 1 ? rl[1] : "/";
                        string host = "", cookie = "";
                        string line;
                        while (!string.IsNullOrEmpty(line = r.ReadLine()))
                        {
                            int i = line.IndexOf(':');
                            if (i <= 0) continue;
                            string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                            if (k.Equals("Host", StringComparison.OrdinalIgnoreCase)) host = v;
                            else if (k.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) cookie = v;
                        }
                        lock (Log) Log.Add(host + "|" + path + "|" + cookie);
                        bool ok = cookie.Contains("sid=ok");
                        int status = 200; string type = "application/octet-stream"; byte[] body;
                        if (path.StartsWith("/privado/index.html"))
                        {
                            if (ok)
                            {
                                type = "text/html; charset=utf-8";
                                body = Encoding.UTF8.GetBytes("<html><body><a href=\"a.bin\">A</a> <a href=\"b.bin\">B</a> <a href=\"c.bin\">C</a> " +
                                    "<a href=\"" + Other + "/otro/d.bin\">D</a></body></html>");
                            }
                            else { status = 403; type = "text/html"; body = Encoding.UTF8.GetBytes("<html>inicia sesión</html>"); }
                        }
                        else if (path.StartsWith("/privado/") && path.EndsWith(".bin"))
                        {
                            if (ok) body = new byte[1500];
                            else { status = 403; type = "text/html"; body = Encoding.UTF8.GetBytes("<html>inicia sesión</html>"); }
                        }
                        else if (path.StartsWith("/otro/")) body = new byte[1200];
                        else if (path.StartsWith("/login")) { type = "text/html"; body = Encoding.UTF8.GetBytes("<html>formulario</html>"); }
                        else { status = 404; type = "text/plain"; body = Encoding.ASCII.GetBytes("no"); }
                        string head = "HTTP/1.1 " + status + (status == 200 ? " OK" : status == 403 ? " Forbidden" : " Not Found") + "\r\n" +
                            "Content-Type: " + type + "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
                        byte[] hb = Encoding.ASCII.GetBytes(head);
                        ns.Write(hb, 0, hb.Length);
                        if (method != "HEAD") ns.Write(body, 0, body.Length);
                    }
                }
                catch { }
            }
        }

        static GrabberProject Op5Project(Op5Site site)
        {
            GrabberProject p = GrabberProject.CreateDefault();
            p.StartUrl = site.Base + "/privado/index.html";
            p.IncludeFilter = GrabberFilters.All; p.ThisSiteOnly = false; p.HideDuplicates = false;
            p.WholeSite = false; p.LevelsSite = 1; p.LevelsOther = 0;
            p.ManualLogin = true; p.ManualLoginUrl = site.Base + "/login";
            return p;
        }

        static void Op5Explore(GrabberEngine e)
        {
            ManualResetEvent done = new ManualResetEvent(false);
            e.ExploreFinished += delegate { done.Set(); };
            e.StartExploring();
            Check(done.WaitOne(30000), "La exploración no terminó");
        }

        static int Op5Bins(GrabberProject p, string prefix)
        {
            int n = 0;
            foreach (GrabberFile f in p.Files) if (f.Name.EndsWith(".bin") && f.Url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }

        static void TO5_BrowserCookies()
        {
            UseFreshHome("op5-cookies");
            GrabberFilters.Reload();
            Op5Site site = new Op5Site(); site.Start();
            try
            {
                // Sin sesión el sitio no deja ver nada.
                GrabberProject p = Op5Project(site);
                GrabberEngine e = new GrabberEngine(p, Cfg());
                Op5Explore(e);
                Check(p.Files.Count == 0, "Sin sesión no debería encontrar nada: " + Show(Names(p)));

                // Con la sesión del navegador (proveedor falso en lugar de CookieBridge).
                List<string> asked = new List<string>();
                GrabberEngine.CookieProvider = delegate(string url, int ms)
                {
                    lock (asked) asked.Add(url);
                    return new Uri(url).Host == "127.0.0.1" ? "sid=ok; tema=oscuro" : null;
                };
                p = Op5Project(site);
                p.SaveMode = 2; p.SaveFolder = Path.Combine(Dl, "op5-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                e = new GrabberEngine(p, Cfg());
                int n = e.ImportBrowserCookies(p.ManualLoginUrl, 2000);
                Check(n == 2, "Cookies importadas: " + n);
                Check(asked.Count == 1 && asked[0] == p.StartUrl, "Pidió al navegador: " + Show(asked));
                Op5Explore(e);
                Check(Op5Bins(p, site.Base + "/privado/") == 3, "Con sesión no encontró los 3 archivos privados: " + Show(Names(p)));
                Check(Op5Bins(p, site.Other) == 1, "Falta el archivo del otro servidor: " + Show(Names(p)));
                Check((e.CookiesFor(site.Base + "/privado/a.bin") ?? "").Contains("sid=ok"), "Las descargas no heredan la sesión");
                Check(string.IsNullOrEmpty(e.CookiesFor(site.Other + "/otro/d.bin")), "La sesión se manda a otro servidor: " + e.CookiesFor(site.Other + "/otro/d.bin"));
                Check(string.IsNullOrEmpty(e.CookiesFor("http://sub.127.0.0.1.invalid/x")) && string.IsNullOrEmpty(e.CookiesFor("http://127.0.0.2:" + site.Port + "/x")), "Cookies fuera de su servidor");
                // Descargar todo: los privados llegan (con la cookie) y al otro servidor no le llega la sesión.
                foreach (GrabberFile f in p.Files) f.Checked = true;
                ManualResetEvent done = new ManualResetEvent(false);
                e.DownloadFinished += delegate { done.Set(); };
                e.StartDownloading(null);
                Check(done.WaitOne(30000), "No terminó de descargar");
                foreach (GrabberFile f in p.Files) Check(f.Status == "Completo", f.Name + ": " + f.Status);
                List<string> other = site.Requests("localhost");
                Check(other.Count > 0, "No se pidió nada al otro servidor");
                foreach (string x in other) Check(!x.Contains("sid=ok"), "Cookie filtrada a otro servidor: " + x);
                Check(site.Requests("127.0.0.1").FindAll(delegate(string x) { return x.Contains("/privado/a.bin") && x.Contains("sid=ok"); }).Count >= 1, "La descarga no llevó la cookie");
            }
            finally { GrabberEngine.CookieProvider = null; site.Stop(); }
        }

        static void TO5_PastedCookies()
        {
            UseFreshHome("op5-pegar");
            GrabberFilters.Reload();
            Op5Site site = new Op5Site(); site.Start();
            try
            {
                GrabberProject p = Op5Project(site);
                GrabberEngine e = new GrabberEngine(p, Cfg());
                Check(e.ImportCookieHeader(p.StartUrl, "Cookie: sid=ok; preferencia=1; sin-igual; ") == 2, "Cookies pegadas mal leídas");
                Op5Explore(e);
                Check(Op5Bins(p, site.Base + "/privado/") == 3, "Con las cookies pegadas no encontró los archivos: " + Show(Names(p)));
                foreach (string x in site.Requests("localhost")) Check(!x.Contains("sid=ok"), "Cookie pegada filtrada a otro servidor: " + x);
                // https: las cookies solo viajan por https; con y sin "www." es el mismo sitio, otros subdominios no.
                GrabberProject q = GrabberProject.CreateDefault(); q.StartUrl = "https://ejemplo.invalid/";
                GrabberEngine eq = new GrabberEngine(q, Cfg());
                Check(eq.ImportCookieHeader(q.StartUrl, "sid=ok") == 1, "No importó la cookie https");
                Check((eq.CookiesFor("https://ejemplo.invalid/a") ?? "").Contains("sid=ok") && (eq.CookiesFor("https://www.ejemplo.invalid/a") ?? "").Contains("sid=ok"), "Falta la cookie en el sitio (con/sin www)");
                Check(string.IsNullOrEmpty(eq.CookiesFor("http://ejemplo.invalid/a")), "Cookie de https mandada por http");
                Check(string.IsNullOrEmpty(eq.CookiesFor("https://cdn.ejemplo.invalid/a")) && string.IsNullOrEmpty(eq.CookiesFor("https://otro.invalid/a")), "Cookie mandada a otro servidor");
                Check(eq.ImportCookieHeader("file:///C:/x", "a=1") == 0 && eq.ImportCookieHeader(q.StartUrl, "") == 0, "Direcciones o texto no válidos");
            }
            finally { site.Stop(); }
        }

        static GrabberForm Op5Form(Op5Site site, string name)
        {
            GrabberForm g = new GrabberForm(Cfg(), NewMgr(), null, null);
            g.Show();
            g.SetStart(site.Base + "/privado/index.html", 0);
            g.SetManualLogin(true, site.Base + "/login");
            g.SetName(name);
            return g;
        }

        static void Op5Close(GrabberForm g)
        {
            if (g == null || g.IsDisposed) return;
            if (g.Engine != null) { g.Engine.StopExploring(); PumpUntil(delegate { return !g.Engine.Exploring; }, 10); }
            g.Close();
            PumpUntil(delegate { return g.IsDisposed; }, 5);
            if (!g.IsDisposed) g.Dispose();
        }

        static void TO5_WizardLogin()
        {
            UseFreshHome("op5-asistente");
            GrabberFilters.Reload();
            Op5Site site = new Op5Site(); site.Start();
            List<string> said = new List<string>(), opened = new List<string>(), asks = new List<string>();
            GrabberForm.MessageHook = delegate(string t) { said.Add(t); };
            GrabberForm.AskHook = delegate(string t) { return System.Windows.Forms.DialogResult.No; };
            GrabberForm.BrowserHook = delegate(string u) { opened.Add(u); };
            string login = site.Base + "/login";
            GrabberForm g = null;
            try
            {
                // Validación: la página de inicio de sesión tiene que ser http(s).
                g = Op5Form(site, "op5 sesión");
                g.SetManualLogin(true, "ftp://127.0.0.1/");
                Check(!g.GoNext() && said.Count > 0 && said[said.Count - 1].Contains("donde iniciar sesión"), "Sin página http(s) debería avisar");
                Op5Close(g);

                // La extensión no da la sesión: se ofrece pegar las cookies; lo pegado vale y no se guarda.
                GrabberEngine.CookieProvider = delegate(string url, int ms) { return null; };
                GrabberForm.LoginHook = delegate(string url, bool paste) { asks.Add((paste ? "pegar " : "continuar ") + url); return paste ? "sid=ok; extra=1" : ""; };
                g = Op5Form(site, "op5 sesión");
                for (int i = 0; i < 3; i++) Check(g.GoNext(), "Paso " + (i + 1));
                Check(opened.Count == 0 && asks.Count == 0, "Abrió el navegador antes del paso 4");
                Check(g.GoNext() && g.Step == 5, "Ventana principal");
                Check(opened.Count == 1 && opened[0] == login, "No abrió la página de inicio de sesión: " + Show(opened));
                Check(asks.Count == 2 && asks[0] == "continuar " + login && asks[1] == "pegar " + login, "Diálogo: " + Show(asks));
                Check(PumpUntil(delegate { return !g.Engine.Exploring; }, 30), "No terminó de explorar");
                Check(Op5Bins(g.Project, site.Base + "/privado/") == 3, "Con las cookies pegadas no encontró los archivos: " + Show(Names(g.Project)));
                Check(g.StatusText.Contains("Cookies pegadas: 2"), "Estado: " + g.StatusText);
                // Explorar otra vez no vuelve a pedir la sesión.
                g.ClickTool("explore");
                Check(opened.Count == 1 && asks.Count == 2, "Volvió a pedir la sesión");
                Check(g.SaveProject(false), "No guardó");
                string json = File.ReadAllText(GrabberProject.PathFor("op5 sesión"));
                Check(json.Contains("\"ManualLogin\":true") && json.Contains(login), "No guardó la página de inicio de sesión: " + json.Substring(0, Math.Min(300, json.Length)));
                Check(!json.Contains("sid") && !json.Contains("extra"), "¡Las cookies se guardaron en el proyecto!");
                GrabberProject back = GrabberProject.Load("op5 sesión");
                Check(back.ManualLogin && back.ManualLoginUrl == login, "Al cargar: " + back.ManualLogin + " " + back.ManualLoginUrl);
                Op5Close(g);

                // Con la sesión del navegador: un solo diálogo y sin caja de pegar.
                opened.Clear(); asks.Clear();
                GrabberEngine.CookieProvider = delegate(string url, int ms) { return "sid=ok"; };
                g = Op5Form(site, "op5 sesión 2");
                for (int i = 0; i < 4; i++) g.GoNext();
                Check(g.Step == 5 && opened.Count == 1 && asks.Count == 1 && asks[0].StartsWith("continuar "), "Diálogo con sesión: " + Show(asks));
                Check(PumpUntil(delegate { return !g.Engine.Exploring; }, 30) && Op5Bins(g.Project, site.Base + "/privado/") == 3, "Con la sesión del navegador: " + Show(Names(g.Project)));
                Op5Close(g);

                // Cancelar: no explora.
                opened.Clear(); asks.Clear();
                GrabberForm.LoginHook = delegate(string url, bool paste) { asks.Add("cancelar"); return null; };
                g = Op5Form(site, "op5 sesión 3");
                for (int i = 0; i < 4; i++) g.GoNext();
                Check(g.Step == 5 && asks.Count == 1 && !g.Engine.Exploring && g.Engine.LoginDoneFor == null && g.Project.Files.Count == 0, "Cancelar debería no explorar");
                Op5Close(g);

                // Sin la casilla: ni navegador ni diálogo (y la página de desconexión sigue funcionando).
                opened.Clear(); asks.Clear();
                g = Op5Form(site, "op5 sesión 4");
                g.SetManualLogin(false, "");
                for (int i = 0; i < 4; i++) g.GoNext();
                Check(opened.Count == 0 && asks.Count == 0, "Sin la casilla no debería abrir nada");
                Check(PumpUntil(delegate { return !g.Engine.Exploring; }, 30) && g.Project.Files.Count == 0, "Sin sesión: " + Show(Names(g.Project)));
                Op5Close(g);
            }
            finally
            {
                Op5Close(g);
                GrabberForm.MessageHook = null; GrabberForm.AskHook = null; GrabberForm.BrowserHook = null; GrabberForm.LoginHook = null;
                GrabberEngine.CookieProvider = null;
                site.Stop();
            }
        }

        static void TO5_Scheduled()
        {
            UseFreshHome("op5-programado");
            GrabberFilters.Reload();
            Op5Site site = new Op5Site(); site.Start();
            List<string> opened = new List<string>(), asks = new List<string>(), asked = new List<string>();
            GrabberForm.AskHook = delegate(string t) { return System.Windows.Forms.DialogResult.No; };
            GrabberForm.MessageHook = delegate(string t) { };
            GrabberForm.BrowserHook = delegate(string u) { opened.Add(u); };
            GrabberForm.LoginHook = delegate(string url, bool paste) { asks.Add(url); return ""; };
            GrabberForm g = null;
            try
            {
                GrabberEngine.CookieProvider = delegate(string url, int ms) { lock (asked) asked.Add(url); return "sid=ok"; };
                g = Op5Form(site, "op5 programado");
                g.RunScheduledNow();
                Check(g.Step == 5 && opened.Count == 0 && asks.Count == 0 && asked.Count == 1, "Programado: navegador " + opened.Count + ", diálogos " + asks.Count + ", peticiones " + asked.Count);
                Check(PumpUntil(delegate { return !g.Engine.Exploring; }, 30) && Op5Bins(g.Project, site.Base + "/privado/") == 3, "Programado con sesión: " + Show(Names(g.Project)));
                Op5Close(g);

                // Sin extensión: explora sin la sesión y lo apunta en el estado.
                GrabberEngine.CookieProvider = delegate(string url, int ms) { return null; };
                g = Op5Form(site, "op5 programado 2");
                g.RunScheduledNow();
                Check(g.Step == 5 && opened.Count == 0 && asks.Count == 0, "Programado sin sesión mostró algo");
                Check(PumpUntil(delegate { return !g.Engine.Exploring; }, 30), "No terminó de explorar");
                Check(g.Project.Pages.Count > 0 && g.Project.Files.Count == 0, "Sin sesión: " + Show(Names(g.Project)));
                Check(g.StatusText.Contains("Sin la sesión del navegador"), "Estado: " + g.StatusText);
            }
            finally
            {
                Op5Close(g);
                GrabberForm.MessageHook = null; GrabberForm.AskHook = null; GrabberForm.BrowserHook = null; GrabberForm.LoginHook = null;
                GrabberEngine.CookieProvider = null;
                site.Stop();
            }
        }
    }
}
