using System;
using System.Diagnostics;
using System.Net;
using System.Reflection;

namespace IDMClone.Tests
{
    // Proxy del navegador más allá de HTTP (#17, extensión): Firefox dice en cada petición si salió por un SOCKS y la
    // extensión lo manda como "socks5://host:puerto", "socks4://..." o "socks4a://..." (un proxy HTTP sigue yendo como
    // "host:puerto"). IDMClone lo acepta y, si no puede bajar directamente, sale por él con su puente SOCKS local (el
    // mismo de Opciones > Proxy/Socks). El servidor SOCKS de pruebas (TestSocksServer) lleva "*.invalid" a TestServer.
    public static partial class TestMain
    {
        static void RunExtProxy()
        {
            Run("proxy del navegador: el host acepta http, socks5, socks4 y socks4a (sin usuario, ruta ni TLS)", EP_Validate);
            Run("proxy del navegador: socks5/socks4/socks4a -> puente SOCKS local; host:puerto y http:// -> proxy HTTP", EP_Parse);
            Run("motor, proxy SOCKS5 del navegador: sin conexión directa -> se baja por él (el nombre lo resuelve el SOCKS)", EP_Socks5Fallback);
            Run("motor, proxy SOCKS4a del navegador: sin conexión directa -> se baja por él", EP_Socks4aFallback);
            Run("motor, proxy SOCKS del navegador: con conexión directa no se usa; un 404 tampoco lo activa", EP_SocksNotNeeded);
            Run("motor, proxy SOCKS del navegador que no responde: error claro con su dirección", EP_SocksDown);
            Run("mensaje de la extensión con browserProxy socks5:// -> la descarga lo guarda y lo usa; https:// se descarta", EP_FromExtension);
        }

        static void EP_Validate()
        {
            foreach (string ok in new string[] { "socks5://127.0.0.1:1080", "SOCKS5://proxy.lan:1080", "socks5h://p:1", "socks://p:1080",
                                                 "socks4://10.0.0.1:1080", "socks4a://p.lan:1080", "socks5://[::1]:1080", "http://proxy:3128", "proxy:3128" })
                Check(NativeHost.ValidBrowserProxy(ok), "Rechazó '" + ok + "'");
            foreach (string bad in new string[] { "https://proxy:443", "socks5://u:p@proxy:1080", "socks5://proxy", "socks5://proxy:0", "socks5://proxy:1080/x",
                                                  "socks6://p:1", "ftp://p:21", "socks5://", "socks5://:1080", "http://socks5://p:1", "socks5://pröxy:1080", "socks5://::1:1080" })
                Check(!NativeHost.ValidBrowserProxy(bad), "Aceptó '" + bad + "'");
        }

        static void EP_Parse()
        {
            string[] socks = { "socks5://127.0.0.1:1080", "socks5h://proxy.lan:1080", "socks://p:1", "socks4://10.0.0.1:1080", "socks4a://p.lan:1080", "socks5://[::1]:1080" };
            string[] types = { "5", "5", "5", "4", "4a", "5" };
            for (int i = 0; i < socks.Length; i++)
            {
                SocksSettings s = SocksSettings.FromBrowserProxy(socks[i]);
                Check(s != null && s.Type == types[i] && s.User == null, socks[i] + " -> " + (s == null ? "null" : s.ToString()));
                WebProxy p = DownloadEngine.ParseBrowserProxy(socks[i]);
                Check(p != null && p.Address == SocksBridge.Address && p.Credentials != null, socks[i] + ": no va por el puente SOCKS");
            }
            Check(SocksSettings.FromBrowserProxy("socks5://[::1]:1080").Host == "::1", "IPv6 entre corchetes");
            Check(SocksSettings.FromBrowserProxy("socks5://proxy.lan:1080").Port == 1080, "Puerto");
            // Mismo servidor -> misma ruta del puente (no crece con cada descarga).
            Check(SocksBridge.CredentialFor(SocksSettings.FromBrowserProxy("socks5://p.lan:1080")).UserName ==
                  SocksBridge.CredentialFor(SocksSettings.FromBrowserProxy("socks5://p.lan:1080")).UserName, "Una ruta por servidor");
            foreach (string bad in new string[] { "socks5://u@p:1", "socks5://p", "socks5://p:70000", "socks4://p:1/x", "socks9://p:1", "socks5://:1" })
            {
                Check(SocksSettings.FromBrowserProxy(bad) == null, "FromBrowserProxy aceptó '" + bad + "'");
                Check(DownloadEngine.ParseBrowserProxy(bad) == null, "ParseBrowserProxy aceptó '" + bad + "'");
            }
            WebProxy h = DownloadEngine.ParseBrowserProxy("http://127.0.0.1:8080");
            Check(h != null && h.Address.Port == 8080 && h.Address != SocksBridge.Address, "http:// sigue siendo un proxy HTTP");
            Check(DownloadEngine.ParseBrowserProxy("https://127.0.0.1:8080") == null, "Proxy con TLS: no");
        }

        // Descarga de un servidor al que no se llega directamente ("*.invalid"), con el proxy SOCKS del navegador.
        static DownloadItem EP_ViaBrowserSocks(string scheme, string host, long size, string id)
        {
            TestSocksServer x = Socks();
            AppConfig c = Cfg(); c.ProxyMode = "none";
            DownloadManager m = new DownloadManager(c);
            AddOptions o = new AddOptions();
            o.Url = SocksUrl(host, "range", "ep.bin", size, id);
            o.Folder = Dl; o.StartNow = true;
            o.BrowserProxy = scheme + "127.0.0.1:" + x.Port;
            DownloadItem it = m.Add(o);
            WaitEnd(it, 60);
            return it;
        }

        static void EP_Socks5Fallback()
        {
            TestSocksServer x = Socks();
            int v5 = x.Count("v5"), atyp3 = x.Count("atyp3");
            string id = NewId("epsocks5");
            long size = 1024 * 1024 + 11;
            DownloadItem it = EP_ViaBrowserSocks("socks5://", "navegador5.invalid", size, id);
            AssertComplete(it, size);
            Check(x.Count("v5") > v5, "No pasó por el SOCKS5 del navegador");
            Check(x.Count("atyp3") > atyp3 && x.Count("host:navegador5.invalid") >= 1, "SOCKS5 debe mandar el nombre (lo resuelve el servidor SOCKS)");
            Check(S.Count(id + ":ranged") >= 1, "No se usaron varias conexiones por el SOCKS del navegador");
            Check(it.BrowserProxy == "socks5://127.0.0.1:" + x.Port, "BrowserProxy: " + it.BrowserProxy);
            CheckBridgeIdle();
        }

        static void EP_Socks4aFallback()
        {
            TestSocksServer x = Socks();
            int v4a = x.Count("v4a");
            string id = NewId("epsocks4a");
            DownloadItem it = EP_ViaBrowserSocks("socks4a://", "navegador4a.invalid", 300 * 1024, id);
            AssertComplete(it, 300 * 1024);
            Check(x.Count("v4a") > v4a && x.Count("host:navegador4a.invalid") >= 1, "No pasó por el SOCKS4a del navegador");
            CheckBridgeIdle();
        }

        static void EP_SocksNotNeeded()
        {
            TestSocksServer x = Socks();
            int conn = x.Count("conn");
            AppConfig c = Cfg(); c.ProxyMode = "none";
            DownloadManager m = new DownloadManager(c);
            AddOptions o = new AddOptions();
            string id = NewId("epdirect");
            o.Url = S.Url("range", "epdirect.bin", 200 * 1024, id);   // 127.0.0.1: se llega directamente
            o.Folder = Dl; o.StartNow = true;
            o.BrowserProxy = "socks5://127.0.0.1:" + x.Port;
            DownloadItem it = m.Add(o);
            WaitEnd(it, 60);
            AssertComplete(it, 200 * 1024);
            AddOptions o2 = new AddOptions();
            o2.Url = S.Url("notfound", "ep404.bin", 1000, NewId("ep404"));
            o2.Folder = Dl; o2.StartNow = true;
            o2.BrowserProxy = "socks5://127.0.0.1:" + x.Port;
            Stopwatch sw = Stopwatch.StartNew();
            DownloadItem it2 = m.Add(o2);
            WaitEnd(it2, 30);
            Check(it2.Status == DownloadStatus.Error && it2.Error != null && it2.Error.Contains("404"), "Error: " + it2.Status + " " + it2.Error);
            Check(sw.Elapsed.TotalSeconds < 3, "Tardó " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
            Check(x.Count("conn") == conn, "Se usó el SOCKS del navegador sin necesidad (" + (x.Count("conn") - conn) + " conexiones)");
        }

        static void EP_SocksDown()
        {
            // Un puerto donde no escucha nadie: el puente explica que no llega al servidor SOCKS (no "error 502").
            AppConfig c = Cfg(); c.ProxyMode = "none"; c.RetryCount = 1;
            DownloadManager m = new DownloadManager(c);
            AddOptions o = new AddOptions();
            o.Url = SocksUrl("caido.invalid", "range", "caido.bin", 1000, NewId("epdown"));
            o.Folder = Dl; o.StartNow = true;
            o.BrowserProxy = "socks5://127.0.0.1:1";
            DownloadItem it = m.Add(o);
            WaitEnd(it, 60);
            Check(it.Status == DownloadStatus.Error, "Estado " + it.Status);
            Check(it.Error != null && it.Error.Contains("SOCKS") && it.Error.Contains("127.0.0.1:1") && !it.Error.Contains("502"), "Mensaje: " + it.Error);
        }

        static void EP_FromExtension()
        {
            MainForm f = NewMainForm();
            Action<string> savedNotice = MainForm.NoticeHook;
            try
            {
                TestSocksServer x = Socks();
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                m.Config.ProxyMode = "none";
                MainForm.NoticeHook = delegate(string t) { };
                MethodInfo h = typeof(MainForm).GetMethod("HandleJsonCommand", BindingFlags.NonPublic | BindingFlags.Instance);
                long size = 256 * 1024;
                string id = NewId("epjson");
                string url = SocksUrl("extension.invalid", "range", "ext.bin", size, id);
                string bp = "socks5://127.0.0.1:" + x.Port;
                string reply = (string)h.Invoke(f, new object[] { "{\"type\":\"download\",\"url\":\"" + url + "\",\"filename\":\"ext.bin\",\"browserProxy\":\"" + bp + "\"}" });
                Check(reply.Contains("\"ok\":true"), "Respuesta: " + reply);
                string url2 = S.Url("range", "tls.bin", 1000, NewId("eptls"));
                reply = (string)h.Invoke(f, new object[] { "{\"type\":\"download\",\"url\":\"" + url2 + "\",\"filename\":\"tls.bin\",\"browserProxy\":\"https://p.lan:443\"}" });
                Check(reply.Contains("\"ok\":true"), "Respuesta: " + reply);
                DownloadItem a = null, b = null;
                foreach (DownloadItem it in m.Snapshot()) { if (it.Url == url) a = it; if (it.Url == url2) b = it; }
                Check(a != null && a.BrowserProxy == bp, "No se guardó el proxy SOCKS del navegador: " + (a == null ? "sin descarga" : a.BrowserProxy));
                Check(b != null && b.BrowserProxy == null, "Un proxy con TLS no debe guardarse: " + (b == null ? "sin descarga" : b.BrowserProxy));
                WaitEnd(a, 60);
                AssertComplete(a, size);
                Check(x.Count("host:extension.invalid") >= 1, "No salió por el SOCKS del navegador");
            }
            finally { MainForm.NoticeHook = savedNotice; CloseOpenForms(typeof(ProgressForm)); DisposeMainForm(f); }
        }
    }
}
