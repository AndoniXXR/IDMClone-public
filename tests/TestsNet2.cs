using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace IDMClone.Tests
{
    // Red 2: proxy por protocolo como IDM (http, https y ftp, cada uno con su servidor; FTP por proxy HTTP con
    // "GET ftp://"), "Proxy DNS" de SOCKS y la espera ante un 503 sin Retry-After.
    public static partial class TestMain
    {
        static TestHttpProxy _proxyA, _proxyB;
        static TestFtpServer Ftp2;   // el de la fase 2 ya está parado

        static void RunNet2()
        {
            Ftp2 = new TestFtpServer();
            Ftp2.Start();
            try { RunNet2Tests(); }
            finally
            {
                Ftp2.Stop();
                if (_proxyA != null) { _proxyA.Stop(); _proxyA = null; }
                if (_proxyB != null) { _proxyB.Stop(); _proxyB = null; }
            }
        }

        static void RunNet2Tests()
        {
            Run("red 2: proxy por protocolo: http, https y ftp con su servidor, credenciales y SOCKS solo para los desmarcados", N2_ProtocolProxies);
            Run("red 2: http por el proxy A y https (CONNECT) por el proxy B, de verdad", N2_HttpAndHttpsByDifferentProxies);
            Run("red 2: FTP por proxy HTTP (GET ftp://) con varias conexiones, usuario de la URL y SHA", N2_FtpViaProxy);
            Run("red 2: FTP por su propio proxy con contraseña (407) y casilla ftp desmarcada -> directo", N2_FtpProxyAuthAndDirect);
            Run("red 2: Proxy DNS de SOCKS: con él manda el nombre; sin él, la IP resuelta aquí", N2_SocksProxyDns);
            Run("red 2: 503 sin Retry-After se reintenta enseguida y cada vez un poco más tarde", N2_Busy503Backoff);
            Run("red 2: 503 con Retry-After espera lo que pide el servidor", N2_Busy503RetryAfter);
            Run("red 2: opciones Avanzado / Socks (por protocolo y Proxy DNS) se guardan y el diálogo no se superpone", N2_OptionsRoundTrip);
        }

        static TestHttpProxy ProxyA() { if (_proxyA == null) { _proxyA = new TestHttpProxy(); _proxyA.Start(); } _proxyA.User = null; return _proxyA; }
        static TestHttpProxy ProxyB() { if (_proxyB == null) { _proxyB = new TestHttpProxy(); _proxyB.Start(); } _proxyB.User = null; return _proxyB; }

        static AppConfig ManualProxyCfg(int port)
        {
            AppConfig c = Cfg();
            c.ProxyMode = "manual"; c.ProxyHost = "127.0.0.1"; c.ProxyPort = port;
            return c;
        }

        static void N2_ProtocolProxies()
        {
            AppConfig c = Cfg();
            c.ProxyMode = "manual"; c.ProxyHost = "h.invalid"; c.ProxyPort = 3128; c.ProxyUser = "hu"; c.ProxyPasswordEnc = Util.Protect("hp");
            c.ProxyHttpsHost = "s.invalid"; c.ProxyHttpsPort = 3129; c.ProxyHttpsUser = "su"; c.ProxyHttpsPasswordEnc = Util.Protect("sp");
            c.ProxyFtpHost = "f.invalid"; c.ProxyFtpPort = 2121;
            Uri http = new Uri("http://web.invalid/a.zip"), https = new Uri("https://web.invalid/a.zip"), ftp = new Uri("ftp://web.invalid/a.zip");
            IWebProxy p = NetSettings.ProxyFor(c, http);
            Check(p.GetProxy(http).Authority == "h.invalid:3128", "http: " + p.GetProxy(http));
            Check(p.GetProxy(https).Authority == "s.invalid:3129", "https (también tras una redirección desde http): " + p.GetProxy(https));
            NetworkCredential hc = p.Credentials.GetCredential(new Uri("http://h.invalid:3128/"), "Basic"), sc = p.Credentials.GetCredential(new Uri("http://s.invalid:3129/"), "Basic");
            Check(hc != null && hc.UserName == "hu" && hc.Password == "hp", "Credenciales del proxy http");
            Check(sc != null && sc.UserName == "su" && sc.Password == "sp", "Credenciales del proxy https");
            Check(NetSettings.FtpProxyFor(c, ftp).Address.Authority == "f.invalid:2121", "ftp: su proxy");
            Check(NetSettings.ProxyFor(c, ftp).GetProxy(ftp).Authority == "f.invalid:2121", "ProxyFor con ftp://");
            // Sin servidor propio, https y ftp usan el de la página (el de http).
            c.ProxyHttpsHost = ""; c.ProxyFtpHost = "";
            p = NetSettings.ProxyFor(c, http);
            Check(p.GetProxy(https).Authority == "h.invalid:3128" && NetSettings.FtpProxyFor(c, ftp).Address.Authority == "h.invalid:3128", "Sin servidor propio se usa el de la página");
            // ftps:// nunca por proxy HTTP; la lista de excepciones vale también para ftp; casilla ftp desmarcada = directo.
            DownloadItem fi = new DownloadItem(); fi.Url = "ftps://web.invalid/a.zip";
            Check(Field<WebProxy>(new DownloadEngine(fi, c), "_ftpProxy") == null, "ftps:// no debe ir por el proxy HTTP");
            fi.Url = "ftp://web.invalid/a.zip";
            Check(Field<WebProxy>(new DownloadEngine(fi, c), "_ftpProxy") != null && !Field<bool>(new DownloadEngine(fi, c), "_ftp"), "ftp:// por proxy: camino HTTP");
            c.ProxyBypass = "*.invalid";
            Check(NetSettings.FtpProxyFor(c, ftp) == null, "ftp en la lista de excepciones va directo");
            c.ProxyBypass = ""; c.ProxyFtp = false;
            Check(NetSettings.FtpProxyFor(c, ftp) == null && Field<bool>(new DownloadEngine(fi, c), "_ftp"), "Casilla ftp desmarcada: FTP directo");
            c.ProxyMode = "system"; c.ProxyFtp = true;
            Check(NetSettings.FtpProxyFor(c, ftp) == null, "Fuera del modo manual el FTP va directo, como antes");
            // Como IDM: SOCKS solo para los protocolos que no van por un proxy HTTP.
            c.ProxyMode = "manual"; c.SocksEnabled = true; c.SocksHost = "127.0.0.1"; c.SocksPort = 1080;
            c.ProxyHttp = true; c.ProxyHttps = false;
            p = NetSettings.ProxyFor(c, http);
            Check(p.GetProxy(http).Authority == "h.invalid:3128", "http marcado: su proxy HTTP aunque haya SOCKS");
            Check(p.GetProxy(https).Port == SocksBridge.Address.Port, "https desmarcado con SOCKS: por el puente SOCKS");
            c.ProxyHost = "";   // http marcado pero sin servidor: SOCKS
            Check(NetSettings.ProxyFor(c, http).GetProxy(http).Port == SocksBridge.Address.Port, "http sin servidor con SOCKS: por el puente SOCKS");
            c.SocksEnabled = false; c.ProxyHttp = false;
            Check(NetSettings.ProxyFor(c, http) == null, "Sin protocolos marcados ni SOCKS no hay proxy");
            // "Obtener del sistema": un servidor por protocolo.
            string a, b, d, k;
            OptionsForm.ParseWindowsProxy("http=h.invalid:80;https=s.invalid:443;ftp=f.invalid:21;socks=k.invalid:1080", out a, out b, out d, out k);
            Check(a == "h.invalid:80" && b == "s.invalid:443" && d == "f.invalid:21" && k == "k.invalid:1080", "Por protocolo: " + a + " " + b + " " + d + " " + k);
            OptionsForm.ParseWindowsProxy("p.invalid:8080", out a, out b, out d, out k);
            Check(a == "p.invalid:8080" && b == a && d == a && k == null, "Uno para todos: " + a + " " + b + " " + d + " " + k);
        }

        static void N2_HttpAndHttpsByDifferentProxies()
        {
            TestHttpProxy pa = ProxyA(), pb = ProxyB();
            AppConfig c = ManualProxyCfg(pa.Port);
            c.ProxyHttpsHost = "127.0.0.1"; c.ProxyHttpsPort = pb.Port;
            int aHttp = pa.Count("http"), bHttp = pb.Count("http"), aConn = pa.Count("connect"), bConn = pb.Count("connect");
            // http: descarga completa por el proxy A (web.invalid solo existe para los proxys de prueba).
            string id = NewId("n2http");
            long size = 1536 * 1024 + 9;
            DownloadItem it = new DownloadManager(c).Add(string.Format("http://web.invalid:{0}/f/range,slow/n2.bin?size={1}&id={2}", S.Port, size, id), Dl, null, true);
            WaitEnd(it, 60);
            AssertComplete(it, size);
            Check(pa.Count("http") - aHttp >= 2 && pb.Count("http") == bHttp, "http debe ir solo por el proxy A (A " + (pa.Count("http") - aHttp) + ", B " + (pb.Count("http") - bHttp) + ")");
            Check(S.Count(id + ":ranged") >= 1, "No se usaron varias conexiones por el proxy");
            // https: túnel CONNECT por el proxy B (TLS de extremo a extremo).
            X509Certificate2 cert = TestFtpServer.MakeCert();
            TcpListener tls = RawHttpServer(cert, "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 6\r\nConnection: close\r\n\r\nhola B");
            try
            {
                int tp = ((IPEndPoint)tls.LocalEndpoint).Port;
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://seguro.invalid:" + tp + "/x");
                req.Proxy = NetSettings.ProxyFor(c, req.RequestUri);
                req.Timeout = 15000;
                req.ServerCertificateValidationCallback = delegate { return true; };   // certificado de prueba autofirmado
                using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
                using (StreamReader rd = new StreamReader(r.GetResponseStream()))
                    Check(rd.ReadToEnd() == "hola B", "Respuesta https");
            }
            finally { tls.Stop(); }
            Check(pb.Count("connect") > bConn && pa.Count("connect") == aConn, "https debe ir por el proxy B con CONNECT");
        }

        static void N2_FtpViaProxy()
        {
            TestHttpProxy pa = ProxyA();
            AppConfig c = ManualProxyCfg(pa.Port);
            long size = 3L * 1024 * 1024 + 5;
            string id = NewId("n2ftp");
            // ftpproxy.invalid solo lo conoce el proxy de pruebas (lo lleva a TestFtpServer): si llega, fue por el proxy.
            string url = string.Format(CultureInfo.InvariantCulture, "ftp://ana:secreto@ftpproxy.invalid:{0}/f/slow/{1}/{2}/ftp-proxy.bin", Ftp2.Port, size, id);
            int ftpReq = pa.Count("ftp"), ranged = pa.Count("ftp-range");
            DownloadManager m = new DownloadManager(c);
            DownloadItem it = m.Add(url, Dl, null, true);
            WaitEnd(it, 60);
            AssertComplete(it, size);
            Check(pa.Count("ftp") - ftpReq >= 2 && pa.Count("ftp-range") > ranged, "Por el proxy debe ir con varias conexiones (GET ftp:// con Range): " +
                  (pa.Count("ftp") - ftpReq) + " peticiones, " + (pa.Count("ftp-range") - ranged) + " con rango");
            Check(pa.Count("ftpuser:ana:secreto") >= 1, "El proxy no recibió el usuario y la contraseña de la URL");
            Check(Ftp2.Count(id + ":rest") >= 1, "El servidor FTP no recibió REST (trozos) a través del proxy");
            Check(it.FileName == "ftp-proxy.bin", "Nombre: " + it.FileName);
            // Pausa y reanudación por el proxy.
            string id2 = NewId("n2ftp2");
            long size2 = 4L * 1024 * 1024 + 3;
            DownloadItem it2 = m.Add(string.Format(CultureInfo.InvariantCulture, "ftp://ftpproxy.invalid:{0}/f/slow/{1}/{2}/ftp-proxy2.bin", Ftp2.Port, size2, id2), Dl, null, true);
            WaitProgress(it2, size2 / 3, 30);
            m.StopItem(it2);
            WaitEnd(it2, 15);
            Check(it2.Status == DownloadStatus.Paused, "Estado tras parar: " + it2.Status);
            m.StartItem(it2);
            WaitEnd(it2, 60);
            AssertComplete(it2, size2);
            Check(pa.Count("ftpuser:anonymous") >= 1, "Sin usuario, anónimo");
        }

        static void N2_FtpProxyAuthAndDirect()
        {
            TestHttpProxy pa = ProxyA(), pb = ProxyB();
            pb.User = "pf"; pb.Password = "clave ftp";
            try
            {
                // http por A; ftp por B, que pide contraseña (la de "Usar Proxy ftp").
                AppConfig c = ManualProxyCfg(pa.Port);
                c.ProxyFtpHost = "127.0.0.1"; c.ProxyFtpPort = pb.Port; c.ProxyFtpUser = "pf"; c.ProxyFtpPasswordEnc = Util.Protect("clave ftp");
                long size = 700 * 1024 + 1;
                string id = NewId("n2ftpauth");
                int aFtp = pa.Count("ftp"), b407 = pb.Count("407"), bFtp = pb.Count("ftp");
                DownloadItem it = new DownloadManager(c).Add(string.Format(CultureInfo.InvariantCulture, "ftp://ftpproxy.invalid:{0}/f/normal/{1}/{2}/auth.bin", Ftp2.Port, size, id), Dl, null, true);
                WaitEnd(it, 60);
                AssertComplete(it, size);
                Check(pb.Count("ftp") > bFtp && pa.Count("ftp") == aFtp, "ftp debe ir por su propio proxy (B)");
                Check(pb.Count("407") > b407, "El proxy B debía pedir la contraseña (407) y recibirla");
                // Contraseña mala: error claro y sin insistir.
                c.ProxyFtpPasswordEnc = Util.Protect("mala");
                Stopwatch sw = Stopwatch.StartNew();
                DownloadItem bad = new DownloadManager(c).Add(string.Format(CultureInfo.InvariantCulture, "ftp://ftpproxy.invalid:{0}/f/normal/{1}/{2}/mala.bin", Ftp2.Port, 1000, NewId("n2bad")), Dl, null, true);
                WaitEnd(bad, 30);
                Check(bad.Status == DownloadStatus.Error && bad.Error != null && bad.Error.Contains("407"), "Contraseña del proxy mala: " + bad.Status + " " + bad.Error);
                Check(sw.Elapsed.TotalSeconds < 5, "No debe insistir con una contraseña mala (" + sw.Elapsed.TotalSeconds.ToString("0.0") + " s)");
                // Casilla ftp desmarcada: FTP directo con FtpWebRequest (127.0.0.1), sin pasar por ningún proxy.
                c.ProxyFtp = false;
                string id2 = NewId("n2ftpdir");
                int aAll = pa.Count("ftp"), bAll = pb.Count("ftp");
                DownloadItem d = new DownloadManager(c).Add(Ftp2.Url("normal", "directo.bin", 600 * 1024, id2), Dl, null, true);
                WaitEnd(d, 60);
                AssertComplete(d, 600 * 1024);
                Check(pa.Count("ftp") == aAll && pb.Count("ftp") == bAll && Ftp2.Count(id2 + ":retr") >= 1, "Con ftp desmarcado no debe pasar por los proxys");
            }
            finally { pb.User = null; pb.Password = null; }
        }

        static void N2_SocksProxyDns()
        {
            TestSocksServer x = Socks();
            // Configuración -> ajustes SOCKS: Socks 5 / Socks 4 y Proxy DNS (Socks 4 + Proxy DNS = SOCKS4a).
            AppConfig c = SocksCfg("5", null, null);
            c.SocksProxyDns = false;
            SocksSettings s = SocksSettings.From(c);
            Check(s.Type == "5" && !s.RemoteDns, "Socks 5 sin Proxy DNS");
            c.SocksProxyDns = true; s = SocksSettings.From(c);
            Check(s.Type == "5" && s.RemoteDns, "Socks 5 con Proxy DNS");
            c.SocksType = "4"; c.SocksProxyDns = false; s = SocksSettings.From(c);
            Check(s.Type == "4" && !s.RemoteDns, "Socks 4 sin Proxy DNS");
            c.SocksProxyDns = true; s = SocksSettings.From(c);
            Check(s.Type == "4a" && s.RemoteDns, "Socks 4 con Proxy DNS = SOCKS4a");
            c.SocksType = "4a"; c.SocksProxyDns = false; s = SocksSettings.From(c);
            Check(s.Type == "4a" && s.RemoteDns, "Tipo 4a antiguo: el nombre lo resuelve el servidor");
            Check(Cfg().SocksProxyDns == false, "Proxy DNS desmarcado por defecto, como en IDM");

            // SOCKS5 con Proxy DNS: ATYP 3 con el nombre.
            SocksSettings rem = new SocksSettings(); rem.Type = "5"; rem.Host = "127.0.0.1"; rem.Port = x.Port; rem.RemoteDns = true;
            int a3 = x.Count("atyp3"), a1 = x.Count("atyp1");
            using (Socket k = SocksClient.Connect("dns-remoto.invalid", S.Port, rem, 10000)) { }
            Check(x.Count("atyp3") > a3 && x.Count("host:dns-remoto.invalid") >= 1, "Con Proxy DNS debe mandar el nombre (ATYP 3)");
            // Sin Proxy DNS: el nombre se resuelve aquí y va la IP (ATYP 1). "localhost" -> 127.0.0.1.
            SocksSettings loc = new SocksSettings(); loc.Type = "5"; loc.Host = "127.0.0.1"; loc.Port = x.Port; loc.RemoteDns = false;
            a3 = x.Count("atyp3"); a1 = x.Count("atyp1");
            int h127 = x.Count("host:127.0.0.1");
            using (Socket k = SocksClient.Connect("localhost", S.Port, loc, 10000)) { }
            Check(x.Count("atyp1") > a1 && x.Count("atyp3") == a3 && x.Count("host:127.0.0.1") > h127, "Sin Proxy DNS debe mandar la IP (ATYP 1), no el nombre");
            // Un nombre que este equipo no resuelve: error "dns" claro (no se manda el nombre al servidor SOCKS).
            try { SocksClient.Connect("no-existe-aqui.invalid", S.Port, loc, 10000).Close(); throw new Exception("Debería fallar al resolver"); }
            catch (SocksException ex) { Check(ex.Code == "dns" && ex.Message.Contains("Proxy DNS"), "Código " + ex.Code + ": " + ex.Message); }
            Check(x.Count("atyp3") == a3, "Sin Proxy DNS nunca se manda un nombre");

            // De punta a punta por el puente local (lo que usan las descargas): GET por la ruta sin Proxy DNS.
            NetworkCredential cred = SocksBridge.CredentialFor(loc);
            NetworkCredential credRem = SocksBridge.CredentialFor(rem);
            Check(cred.UserName != credRem.UserName, "Con y sin Proxy DNS deben ser rutas distintas del puente");
            a1 = x.Count("atyp1");
            string body;
            string head = RawBridge("GET http://localhost:" + S.Port + "/f/range/dns.bin?size=1000&id=" + NewId("n2dns") + " HTTP/1.1\r\nHost: localhost\r\nProxy-Authorization: " +
                                    Basic(cred) + "\r\n\r\n", out body);
            Check(head.StartsWith("HTTP/1.1 200") && body.Length == 1000, "Por el puente sin Proxy DNS: " + head);
            Check(x.Count("atyp1") > a1, "El puente sin Proxy DNS debe mandar la IP");
            // Una descarga real con Proxy DNS (los "*.invalid" solo los resuelve el servidor SOCKS).
            AppConfig d = SocksCfg("5", null, null); d.SocksProxyDns = true;
            string id = NewId("n2dns");
            DownloadItem it = new DownloadManager(d).Add(SocksUrl("descarga-dns.invalid", "range", "dns.bin", 900 * 1024, id), Dl, null, true);
            WaitEnd(it, 60);
            AssertComplete(it, 900 * 1024);
            Check(x.Count("host:descarga-dns.invalid") >= 1, "Con Proxy DNS la descarga manda el nombre");
            // Y sin Proxy DNS esa misma dirección no se puede resolver aquí: error claro, no un cuelgue.
            d.SocksProxyDns = false;
            DownloadItem bad = new DownloadManager(d).Add(SocksUrl("descarga-dns.invalid", "range", "dns2.bin", 1000, NewId("n2dns2")), Dl, null, true);
            WaitEnd(bad, 60);
            Check(bad.Status == DownloadStatus.Error && bad.Error != null && bad.Error.Contains("Proxy DNS"), "Sin Proxy DNS: " + bad.Status + " " + bad.Error);
            CheckBridgeIdle();
        }

        static void N2_Busy503Backoff()
        {
            // Reintentar cada 5 s (lo de fábrica): antes, dos 503 seguidos costaban 10 s.
            Check(DownloadEngine.BusyBackoffMs(1, 5) == 500 && DownloadEngine.BusyBackoffMs(2, 5) == 1000 && DownloadEngine.BusyBackoffMs(3, 5) == 2000 &&
                  DownloadEngine.BusyBackoffMs(4, 5) == 4000 && DownloadEngine.BusyBackoffMs(5, 5) == 5000 && DownloadEngine.BusyBackoffMs(30, 5) == 5000,
                  "Esperas: " + DownloadEngine.BusyBackoffMs(1, 5) + ", " + DownloadEngine.BusyBackoffMs(2, 5) + ", " + DownloadEngine.BusyBackoffMs(3, 5) + "...");
            Check(DownloadEngine.BusyBackoffMs(3, 1) == 1000, "Con 'Reintentar cada' 1 s no se espera más de 1 s");
            AppConfig c = Cfg(); c.RetryDelaySec = 5;
            Stopwatch sw = Stopwatch.StartNew();
            DownloadItem it = Download(new DownloadManager(c), "range,flaky503", 800 * 1024, "n2flaky", 30);
            double t = sw.Elapsed.TotalSeconds;
            AssertComplete(it, 800 * 1024);
            Console.WriteLine("        (dos 503 sin Retry-After: " + t.ToString("0.0") + " s; antes ~10 s)");
            Check(S.Count(IdOf(it) + ":req") >= 3, "Debía recibir los dos 503");
            Check(t >= 1.3 && t < 4, "Tardó " + t.ToString("0.0") + " s (esperado ~1,5 s: 0,5 + 1)");
        }

        static void N2_Busy503RetryAfter()
        {
            AppConfig c = Cfg(); c.RetryDelaySec = 5;
            Stopwatch sw = Stopwatch.StartNew();
            DownloadItem it = Download(new DownloadManager(c), "range,ra503", 400 * 1024, "n2ra", 30);
            double t = sw.Elapsed.TotalSeconds;
            AssertComplete(it, 400 * 1024);
            Check(t >= 1.9 && t < 4.5, "Con Retry-After: 2 tardó " + t.ToString("0.0") + " s");
        }

        static void N2_OptionsRoundTrip()
        {
            UseFreshHome("net2-proxy");
            AppConfig c = Cfg();
            c.ProxyMode = "manual"; c.ProxyHost = "h.invalid"; c.ProxyPort = 3128; c.ProxyFtp = false;
            c.ProxyHttpsHost = "s.invalid"; c.ProxyHttpsPort = 3129; c.ProxyHttpsUser = "su"; c.ProxyHttpsPasswordEnc = Util.Protect("sp");
            c.SocksEnabled = true; c.SocksType = "4"; c.SocksProxyDns = true; c.SocksHost = "k.invalid"; c.SocksPort = 1081;
            using (OptionsForm o = new OptionsForm(c))
            {
                IntPtr hnd = o.Handle;
                CheckBox ftp = Field<CheckBox>(o, "_pxFtp");
                Check(ftp.Enabled && !ftp.Checked, "La casilla ftp debe estar activa (modo manual) y desmarcada");
                Check(Field<ProxyServerFields>(o, "_pxS").Host == "s.invalid" && Field<ProxyServerFields>(o, "_pxS").Password == "sp", "No cargó el proxy https");
                Check(Field<bool>(o, "_sxDns"), "No cargó Proxy DNS");
                Field<RadioButton>(o, "_pxNone").Checked = true;
                Check(!ftp.Enabled, "Fuera del modo manual la casilla ftp se desactiva");
                Field<RadioButton>(o, "_pxManual").Checked = true;
                // Lo que devuelve "Avanzado...": https igual que http (-> sin servidor propio), ftp con el suyo.
                o.ApplyAdvanced("x.invalid", new ProxyServerFields(true, "h.invalid", 3128, "", ""), new ProxyServerFields(true, "h.invalid", 3128, "", ""),
                                new ProxyServerFields(true, "f.invalid", 2121, "fu", "fp"));
                Check(ftp.Checked, "La casilla ftp de la página es la de \"Usar Proxy ftp\"");
                typeof(OptionsForm).GetMethod("Apply", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(o, null);
            }
            Check(c.ProxyFtp && c.ProxyFtpHost == "f.invalid" && c.ProxyFtpPort == 2121 && c.ProxyFtpUser == "fu", "No se guardó el proxy ftp");
            Check(c.ProxyFtpPasswordEnc != "fp" && Util.Unprotect(c.ProxyFtpPasswordEnc) == "fp", "La contraseña del proxy ftp debe guardarse cifrada");
            Check(c.ProxyHttpsHost == "" && c.ProxyBypass == "x.invalid", "https igual que http debe quedar sin servidor propio: " + c.ProxyHttpsHost);
            Check(c.SocksType == "4" && c.SocksProxyDns && SocksSettings.From(c).Type == "4a", "No se guardó Socks 4 + Proxy DNS");
            AppConfig back = AppConfig.Load();
            Check(back.ProxyFtp && back.ProxyFtpHost == "f.invalid" && back.ProxyFtpPort == 2121 && Util.Unprotect(back.ProxyFtpPasswordEnc) == "fp" && back.SocksProxyDns,
                  "No se conservó al recargar config.json");
            AppConfig old = AppConfig.NewSerializer().Deserialize<AppConfig>("{\"ProxyMode\":\"manual\",\"ProxyHost\":\"p\",\"ProxyPort\":8080,\"SocksType\":\"4a\"}");
            Check(old.ProxyFtp && old.ProxyHttpsHost == "" && old.ProxyFtpHost == "" && old.ProxyFtpPort == 8080 && SocksSettings.From(old) == null,
                  "Valores por defecto al cargar una configuración antigua");

            // El diálogo "Configuración avanzada del Proxy" como el de IDM.
            using (ProxyAdvancedForm f = new ProxyAdvancedForm("a.invalid", new ProxyServerFields(true, "h.invalid", 3128, "u", "p"),
                       new ProxyServerFields(false, "s.invalid", 3129, "", ""), new ProxyServerFields(true, "f.invalid", 2121, "", ""),
                       true, "4", true, "k.invalid", 9050, "yo", "pw"))
            {
                CheckNoOverlap(f, "Configuración avanzada del Proxy");
                Check(f.Http.On && f.Http.Host == "h.invalid" && f.Http.Port == 3128 && f.Http.User == "u" && f.Http.Password == "p", "Proxy HTTP del diálogo");
                Check(!f.Https.On && f.Https.Host == "s.invalid" && f.Ftp.On && f.Ftp.Port == 2121, "Proxys HTTPS y ftp del diálogo");
                Check(f.SocksEnabled && f.SocksType == "4a" && f.SocksProxyDns && f.SocksPort == 9050 && f.Bypass == "a.invalid", "Socks del diálogo");
                Field<RadioButton>(f, "_s5").Checked = true;
                Check(f.SocksType == "5" && f.SocksProxyDns, "Socks 5 con Proxy DNS");
                Field<CheckBox>(f, "_sDns").Checked = false;
                Check(f.SocksType == "5" && !f.SocksProxyDns, "Socks 5 sin Proxy DNS");
                TextBox httpsHost = Field<TextBox[]>(f, "_pHost")[1];
                Check(!httpsHost.Enabled, "Con \"Usar Proxy HTTPS\" desmarcado sus campos se desactivan");
            }
        }
    }

    // Proxy HTTP mínimo para las pruebas: CONNECT (túnel), "GET http://..." (reenvía) y "GET ftp://..." (proxy FTP al
    // estilo de Squid: baja el archivo de TestFtpServer con FtpWebRequest y respeta Range). Los nombres "*.invalid" y
    // 127.0.0.1 se llevan a 127.0.0.1; cualquier otro destino se rechaza. Con User, exige Proxy-Authorization (407).
    // Contadores: conn, connect, http, ftp, ftp-range, 407, ftpuser:<usuario:clave> (o ftpuser:anonymous).
    public class TestHttpProxy
    {
        TcpListener _listener;
        volatile bool _stopped;
        public int Port;
        public volatile string User, Password;
        readonly Dictionary<string, int> _counters = new Dictionary<string, int>();

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Thread t = new Thread(delegate()
            {
                while (!_stopped)
                {
                    TcpClient c;
                    try { c = _listener.AcceptTcpClient(); }
                    catch { if (_stopped) return; continue; }
                    Thread h = new Thread(delegate() { try { Handle(c); } catch { } finally { try { c.Close(); } catch { } } });
                    h.IsBackground = true;
                    h.Start();
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        public void Stop() { _stopped = true; try { _listener.Stop(); } catch { } }

        public int Count(string key) { lock (_counters) { int v; return _counters.TryGetValue(key, out v) ? v : 0; } }
        void Bump(string key) { lock (_counters) { int v; _counters.TryGetValue(key, out v); _counters[key] = v + 1; } }

        static string ReadHead(Stream s)
        {
            StringBuilder sb = new StringBuilder();
            int b;
            while ((b = s.ReadByte()) >= 0)
            {
                sb.Append((char)b);
                if (sb.Length >= 4 && sb[sb.Length - 1] == '\n' && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n") return sb.ToString();
                if (sb.Length > 65536) return null;
            }
            return null;
        }

        static void Send(Stream s, string text) { byte[] b = Encoding.ASCII.GetBytes(text); s.Write(b, 0, b.Length); s.Flush(); }

        static IPAddress Map(string host)
        {
            host = host.Trim('[', ']');
            if (host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1") return IPAddress.Loopback;
            return null;
        }

        void Handle(TcpClient client)
        {
            client.NoDelay = true;
            NetworkStream ns = client.GetStream();
            Bump("conn");
            string head = ReadHead(ns);
            if (head == null) return;
            if (Environment.GetEnvironmentVariable("IDMCLONE_TRACE") == "1") Console.Error.WriteLine("[proxy " + Port + "] " + head.Replace("\r\n", " | "));
            string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            string[] rl = lines[0].Split(' ');
            if (rl.Length != 3) return;
            List<KeyValuePair<string, string>> hdrs = new List<KeyValuePair<string, string>>();
            string auth = null, range = null;
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c <= 0) continue;
                string k = lines[i].Substring(0, c).Trim(), v = lines[i].Substring(c + 1).Trim();
                if (k.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)) auth = v;
                if (k.Equals("Range", StringComparison.OrdinalIgnoreCase)) range = v;
                hdrs.Add(new KeyValuePair<string, string>(k, v));
            }
            string user = User;
            if (user != null && auth != "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + Password)))
            {
                Bump("407");
                Send(ns, "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"pruebas\"\r\nContent-Length: 0\r\nConnection: close\r\nProxy-Connection: close\r\n\r\n");
                return;
            }
            if (rl[0] == "CONNECT")
            {
                Bump("connect");
                int colon = rl[1].LastIndexOf(':');
                IPAddress ip = colon > 0 ? Map(rl[1].Substring(0, colon)) : null;
                if (ip == null) { Send(ns, "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"); return; }
                TcpClient up = new TcpClient();
                up.Connect(ip, int.Parse(rl[1].Substring(colon + 1), CultureInfo.InvariantCulture));
                Send(ns, "HTTP/1.1 200 Connection established\r\n\r\n");
                Pump(client, up);
                return;
            }
            Uri target;
            if (!Uri.TryCreate(rl[1], UriKind.Absolute, out target) || Map(target.Host) == null)
            {
                Send(ns, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }
            if (target.Scheme == Uri.UriSchemeFtp) { Ftp(ns, target, range); return; }
            // http: reenvío en forma de origen, una petición por conexión.
            Bump("http");
            using (TcpClient up = new TcpClient())
            {
                up.Connect(IPAddress.Loopback, target.Port);
                NetworkStream us = up.GetStream();
                StringBuilder sb = new StringBuilder(rl[0] + " " + target.PathAndQuery + " " + rl[2] + "\r\n");
                foreach (KeyValuePair<string, string> h in hdrs)
                {
                    if (h.Key.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
                    sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
                }
                sb.Append("Connection: close\r\n\r\n");
                Send(us, sb.ToString());
                byte[] buf = new byte[65536];
                int n;
                try { while ((n = us.Read(buf, 0, buf.Length)) > 0) ns.Write(buf, 0, n); } catch (IOException) { }
            }
        }

        // "GET ftp://[usuario:clave@]servidor:puerto/ruta" con Range opcional: lo baja por FTP y lo devuelve por HTTP.
        void Ftp(NetworkStream ns, Uri target, string range)
        {
            Bump("ftp");
            string userInfo = Uri.UnescapeDataString(target.UserInfo ?? "");
            Bump("ftpuser:" + (userInfo.Length > 0 ? userInfo : "anonymous"));
            string url = "ftp://127.0.0.1:" + target.Port + target.AbsolutePath;
            NetworkCredential cred = null;
            if (userInfo.Length > 0)
            {
                int c = userInfo.IndexOf(':');
                cred = new NetworkCredential(c >= 0 ? userInfo.Substring(0, c) : userInfo, c >= 0 ? userInfo.Substring(c + 1) : "");
            }
            long size;
            try
            {
                FtpWebRequest sq = NewFtp(url, cred);
                sq.Method = WebRequestMethods.Ftp.GetFileSize;
                using (FtpWebResponse r = (FtpWebResponse)sq.GetResponse()) size = r.ContentLength;
            }
            catch (WebException ex)
            {
                if (Environment.GetEnvironmentVariable("IDMCLONE_TRACE") == "1") Console.Error.WriteLine("[proxy " + Port + "] SIZE " + url + ": " + ex.Message);
                Send(ns, "HTTP/1.1 404 Not Found\r\nContent-Type: text/html\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }
            long a = 0, b = size - 1;
            bool ranged = false;
            if (range != null && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                string r = range.Substring(6);
                int dash = r.IndexOf('-');
                if (dash > 0)
                {
                    a = long.Parse(r.Substring(0, dash), CultureInfo.InvariantCulture);
                    string bs = r.Substring(dash + 1);
                    if (bs.Length > 0) b = Math.Min(size - 1, long.Parse(bs, CultureInfo.InvariantCulture));
                    ranged = a < size;
                    if (!ranged) { a = 0; b = size - 1; }
                }
            }
            if (ranged) Bump("ftp-range");
            long len = b - a + 1;
            string name = Path.GetFileName(target.AbsolutePath);
            Send(ns, (ranged ? "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes " + a + "-" + b + "/" + size + "\r\n" : "HTTP/1.1 200 OK\r\n") +
                     "Content-Type: application/octet-stream\r\nAccept-Ranges: bytes\r\nContent-Length: " + len + "\r\nConnection: close\r\nProxy-Connection: close\r\n" +
                     "Last-Modified: " + TestFtpServer.MdtmUtc.ToString("R", CultureInfo.InvariantCulture) + "\r\n\r\n");
            FtpWebRequest q = NewFtp(url, cred);
            q.Method = WebRequestMethods.Ftp.DownloadFile;
            if (a > 0) q.ContentOffset = a;
            try
            {
                using (FtpWebResponse r = (FtpWebResponse)q.GetResponse())
                using (Stream s = r.GetResponseStream())
                {
                    byte[] buf = new byte[65536];
                    long left = len;
                    while (left > 0)
                    {
                        int n = s.Read(buf, 0, (int)Math.Min(buf.Length, left));
                        if (n <= 0) break;
                        ns.Write(buf, 0, n);
                        left -= n;
                    }
                    q.Abort();   // trozo servido: no esperar al resto del archivo
                }
            }
            catch (IOException) { try { q.Abort(); } catch { } }
            catch (WebException) { try { q.Abort(); } catch { } }
        }

        static FtpWebRequest NewFtp(string url, NetworkCredential cred)
        {
            FtpWebRequest f = (FtpWebRequest)WebRequest.Create(url);
            f.Proxy = null; f.KeepAlive = false; f.UseBinary = true; f.UsePassive = true; f.Timeout = 15000;
            if (cred != null) f.Credentials = cred;
            return f;
        }

        static void Pump(TcpClient a, TcpClient b)
        {
            NetworkStream sa = a.GetStream(), sb = b.GetStream();
            Thread t = new Thread(delegate() { Copy(sb, sa); Close(a, b); });
            t.IsBackground = true;
            t.Start();
            Copy(sa, sb);
            Close(a, b);
            t.Join(5000);
        }

        static void Copy(NetworkStream from, NetworkStream to)
        {
            byte[] buf = new byte[65536];
            try { int n; while ((n = from.Read(buf, 0, buf.Length)) > 0) to.Write(buf, 0, n); } catch { }
        }

        static void Close(TcpClient a, TcpClient b)
        {
            try { a.Client.Shutdown(SocketShutdown.Send); } catch { }
            try { b.Client.Shutdown(SocketShutdown.Send); } catch { }
            try { a.Close(); } catch { }
            try { b.Close(); } catch { }
        }
    }
}
