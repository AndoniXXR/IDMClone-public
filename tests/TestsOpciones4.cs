using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace IDMClone.Tests
{
    // Opciones que antes solo se guardaban (área 4): ver scratchpad\opciones-adorno.md.
    // Proxy/Socks (SOCKS 4/4a/5 por el puente local, casillas http/https, PASV, proxy del navegador),
    // aviso de la cuota y página Discado / VPN.
    public static partial class TestMain
    {
        static TestSocksServer _socks;

        static void RunOpciones4()
        {
            Run("opción 4: proxy SOCKS5 sin usuario (http por el puente local)", O4_Socks5);
            Run("opción 4: proxy SOCKS5 con usuario y contraseña, pausa y reanudación", O4_Socks5Auth);
            Run("opción 4: proxy SOCKS5 con contraseña incorrecta -> error claro", O4_Socks5BadPassword);
            Run("opción 4: proxy SOCKS4a (el nombre lo resuelve el servidor SOCKS)", O4_Socks4a);
            Run("opción 4: proxy SOCKS4 (dirección IPv4)", O4_Socks4);
            Run("opción 4: el puente SOCKS rechaza peticiones sin el token (407)", O4_BridgeToken);
            Run("opción 4: el puente SOCKS hace túneles CONNECT (https)", O4_BridgeConnect);
            Run("opción 4: proxy: cabeceras X-IDMClone-Socks falsas de un servidor no dan un error permanente (directo y por el puente, http y https)", O4_SpoofedSocksHeaders);
            Run("opción 4: proxy: 70 conexiones lentas sin token no bloquean el puente SOCKS", O4_BridgePreAuthFlood);
            Run("opción 4: proxy: lista de excepciones y casillas http/https", O4_BypassAndSchemes);
            Run("opción 4: límites de descarga avisan una vez por periodo", O4_QuotaWarnOnce);
            Run("opción 4: opciones Proxy/Socks se guardan (SOCKS, PASV, proxy del navegador)", O4_OptionsRoundTrip);
            Run("opción 4: diálogo Avanzado / Socks sin controles superpuestos", O4_AdvancedDialog);
        }

        static TestSocksServer Socks()
        {
            if (_socks == null) { _socks = new TestSocksServer(); _socks.Start(); }
            _socks.User = null; _socks.Password = null;
            return _socks;
        }

        static AppConfig SocksCfg(string type, string user, string pass)
        {
            AppConfig c = Cfg();
            c.ProxyMode = "manual"; c.ProxyHost = ""; c.SocksEnabled = true; c.SocksType = type;
            c.SocksProxyDns = type != "4";   // los destinos "*.invalid" solo los resuelve el servidor SOCKS de pruebas
            if (_socks == null) Socks();
            c.SocksHost = "127.0.0.1"; c.SocksPort = _socks.Port;   // sin Socks(): no borra el usuario exigido
            c.SocksUser = user; c.SocksPasswordEnc = pass != null ? Util.Protect(pass) : null;
            return c;
        }

        // URL de un servidor que solo existe para el servidor SOCKS de pruebas (que lo lleva a TestServer).
        static string SocksUrl(string host, string modes, string name, long size, string id)
        {
            return string.Format("http://{0}:{1}/f/{2}/{3}?size={4}&id={5}", host, S.Port, modes, name, size, id);
        }

        // Tras terminar, el puente no deja conexiones abiertas (http: Connection: close en las dos direcciones).
        static void CheckBridgeIdle()
        {
            DateTime limit = DateTime.Now.AddSeconds(10);
            while (SocksBridge.ActiveConnections > 0 && DateTime.Now < limit) Thread.Sleep(50);
            Check(SocksBridge.ActiveConnections == 0, "El puente SOCKS dejó " + SocksBridge.ActiveConnections + " conexiones abiertas");
        }

        static void O4_Socks5()
        {
            TestSocksServer x = Socks();
            int v5 = x.Count("v5"), atyp3 = x.Count("atyp3");
            AppConfig c = SocksCfg("5", null, null);
            string id = NewId("socks5");
            DownloadManager m = new DownloadManager(c);
            DownloadItem it = m.Add(SocksUrl("socks5.invalid", "range,slow", "s5.bin", 2 * 1024 * 1024 + 7, id), Dl, null, true);
            WaitEnd(it, 60);
            AssertComplete(it, 2 * 1024 * 1024 + 7);
            Check(x.Count("v5") > v5, "No pasó por el servidor SOCKS5");
            Check(x.Count("atyp3") > atyp3, "SOCKS5 debería mandar el nombre (ATYP 3) para que lo resuelva el servidor");
            Check(x.Count("host:socks5.invalid") >= 1, "El servidor SOCKS no recibió el nombre del destino");
            Check(S.Count(id + ":ranged") >= 1, "No se usaron varias conexiones por SOCKS");
            CheckBridgeIdle();
        }

        static void O4_Socks5Auth()
        {
            TestSocksServer x = Socks();
            x.User = "ana"; x.Password = "clave ñ 1";
            try
            {
                int ok0 = x.Count("auth-ok");
                AppConfig c = SocksCfg("5", "ana", "clave ñ 1");
                Check(c.SocksPasswordEnc != "clave ñ 1" && Util.Unprotect(c.SocksPasswordEnc) == "clave ñ 1", "La contraseña SOCKS debe guardarse cifrada");
                DownloadManager m = new DownloadManager(c);
                long size = 2 * 1024 * 1024;
                string id = NewId("socks5a");
                DownloadItem it = m.Add(SocksUrl("auth.invalid", "range,veryslow", "s5a.bin", size, id), Dl, null, true);
                WaitProgress(it, 300 * 1024, 30);
                m.StopItem(it);
                WaitEnd(it, 10);
                Check(it.Status == DownloadStatus.Paused, "Estado tras pausar: " + it.Status);
                m.StartItem(it);
                WaitEnd(it, 90);
                AssertComplete(it, size);
                Check(x.Count("auth-ok") > ok0, "El servidor SOCKS no recibió el usuario y la contraseña");
                CheckBridgeIdle();
            }
            finally { x.User = null; x.Password = null; }
        }

        static void O4_Socks5BadPassword()
        {
            TestSocksServer x = Socks();
            x.User = "ana"; x.Password = "buena";
            try
            {
                // Cliente SOCKS directo: mensaje claro y marcado como permanente.
                SocksSettings s = new SocksSettings(); s.Type = "5"; s.Host = "127.0.0.1"; s.Port = x.Port; s.User = "ana"; s.Password = "mala";
                try { SocksClient.Connect("mal.invalid", S.Port, s, 10000).Close(); throw new Exception("Debería fallar con la contraseña mala"); }
                catch (SocksException ex)
                {
                    Check(ex.Code == "auth" && ex.Permanent, "Código " + ex.Code);
                    Check(ex.Message.IndexOf("contraseña", StringComparison.OrdinalIgnoreCase) >= 0, "Mensaje poco claro: " + ex.Message);
                }
                // Sin usuario contra un servidor que lo exige.
                s.User = null;
                try { SocksClient.Connect("mal.invalid", S.Port, s, 10000).Close(); throw new Exception("Debería pedir usuario"); }
                catch (SocksException ex) { Check(ex.Code == "auth", "Sin usuario: código " + ex.Code); }

                // Por HttpWebRequest: la respuesta del puente explica el fallo.
                AppConfig c = SocksCfg("5", "ana", "mala");
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(SocksUrl("mal.invalid", "range", "x.bin", 100, NewId("bad")));
                req.Proxy = NetSettings.ProxyFor(c, req.RequestUri);
                req.Timeout = 15000;
                try { using (req.GetResponse()) { } throw new Exception("La petición debería fallar"); }
                catch (WebException wex)
                {
                    string why = SocksBridge.Explain(wex);
                    Check(why != null && why.IndexOf("contraseña", StringComparison.OrdinalIgnoreCase) >= 0, "Explain: " + (why ?? "(null)") + " / " + wex.Message);
                    Check(SocksBridge.IsPermanent(wex), "Un fallo de contraseña SOCKS no se arregla reintentando");
                }
                // https (túnel CONNECT): .NET no deja ver la respuesta del puente, pero el motivo se puede explicar igual.
                req = (HttpWebRequest)WebRequest.Create("https://mal.invalid:" + S.Port + "/x");
                req.Proxy = NetSettings.ProxyFor(c, req.RequestUri);
                req.Timeout = 15000;
                try { using (req.GetResponse()) { } throw new Exception("La petición https debería fallar"); }
                catch (WebException wex)
                {
                    string why = SocksBridge.Explain(wex);
                    Check(why != null && why.IndexOf("contraseña", StringComparison.OrdinalIgnoreCase) >= 0, "Explain https: " + (why ?? "(null)") + " / " + wex.Status + " " + wex.Message);
                    Check(SocksBridge.IsPermanent(wex), "https: un fallo de contraseña SOCKS no se arregla reintentando");
                }

                // Descarga: termina en error (no se queda reintentando para siempre).
                c.RetryCount = 1;
                int bad0 = x.Count("auth-bad");
                DownloadManager m = new DownloadManager(c);
                DownloadItem it = m.Add(SocksUrl("mal.invalid", "range", "bad.bin", 1000, NewId("bad")), Dl, null, true);
                WaitEnd(it, 60);
                Check(it.Status == DownloadStatus.Error, "Estado " + it.Status + ", se esperaba Error");
                Check(x.Count("auth-bad") > bad0, "El servidor SOCKS no vio el intento de la descarga con la contraseña mala");
                // DownloadEngine.Describe/PermanentError usan SocksBridge.Explain/IsPermanent: el mensaje es el de SOCKS, no "(502)".
                Check(it.Error != null && it.Error.IndexOf("contraseña", StringComparison.OrdinalIgnoreCase) >= 0, "Mensaje: " + it.Error);
                // El 502 del puente lleva la marca secreta de la sesión (la que hace que Explain/IsPermanent lo crean).
                string body;
                string h = RawBridge("GET " + SocksUrl("mal.invalid", "range", "x.bin", 100, NewId("bad")) + " HTTP/1.1\r\nHost: mal.invalid\r\nProxy-Authorization: " +
                                     Basic(SocksBridge.CredentialFor(SocksSettings.From(c))) + "\r\n\r\n", out body);
                Check(h.StartsWith("HTTP/1.1 502") && h.IndexOf("\r\n" + SocksBridge.ErrorHeader + ": auth", StringComparison.OrdinalIgnoreCase) >= 0 &&
                      h.IndexOf("\r\n" + SocksBridge.MarkerHeader + ": ", StringComparison.OrdinalIgnoreCase) >= 0, "502 del puente: " + h);
                CheckBridgeIdle();
            }
            finally { x.User = null; x.Password = null; }
        }

        static void O4_Socks4a()
        {
            TestSocksServer x = Socks();
            int n = x.Count("v4a");
            AppConfig c = SocksCfg("4a", "pepe", null);
            string id = NewId("socks4a");
            DownloadItem it = new DownloadManager(c).Add(SocksUrl("socks4a.invalid", "range", "s4a.bin", 1024 * 1024, id), Dl, null, true);
            WaitEnd(it, 60);
            AssertComplete(it, 1024 * 1024);
            Check(x.Count("v4a") > n, "No pasó por SOCKS4a");
            Check(x.Count("host:socks4a.invalid") >= 1 && x.Count("user:pepe") >= 1, "SOCKS4a debe mandar el nombre y el usuario");
            CheckBridgeIdle();
        }

        static void O4_Socks4()
        {
            TestSocksServer x = Socks();
            int n = x.Count("v4");
            AppConfig c = SocksCfg("4", null, null);
            // 192.0.2.10 (red de documentación): una IPv4 que .NET no considera local; el servidor de pruebas la
            // lleva a TestServer.
            string id = NewId("socks4");
            DownloadItem it = new DownloadManager(c).Add(SocksUrl("192.0.2.10", "range", "s4.bin", 768 * 1024, id), Dl, null, true);
            WaitEnd(it, 60);
            AssertComplete(it, 768 * 1024);
            Check(x.Count("v4") > n && x.Count("host:192.0.2.10") >= 1, "No pasó por SOCKS4");
            // SOCKS4 no puede con nombres que este equipo no resuelve: error claro, no un cuelgue.
            SocksSettings s = new SocksSettings(); s.Type = "4"; s.Host = "127.0.0.1"; s.Port = x.Port;
            try { SocksClient.Connect("no-existe.invalid", 80, s, 10000).Close(); throw new Exception("Debería fallar"); }
            catch (SocksException ex) { Check(ex.Code == "dns", "Código " + ex.Code + ": " + ex.Message); }
            CheckBridgeIdle();
        }

        // Petición en bruto al puente: devuelve la cabecera de la respuesta (y el cuerpo en body).
        static string RawBridge(string request, out string body)
        {
            Uri b = SocksBridge.Address;
            using (TcpClient t = new TcpClient())
            {
                t.Connect(IPAddress.Loopback, b.Port);
                t.ReceiveTimeout = 15000;
                NetworkStream ns = t.GetStream();
                byte[] q = Encoding.ASCII.GetBytes(request);
                try { ns.Write(q, 0, q.Length); } catch (IOException) { }   // el puente puede cortar una cabecera enorme
                MemoryStream ms = new MemoryStream();
                byte[] buf = new byte[8192];
                int n;
                try { while ((n = ns.Read(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, n); if (request.StartsWith("CONNECT") || Encoding.ASCII.GetString(ms.ToArray()).Contains(" 407 ")) break; } }
                catch (IOException) { }
                string all = Encoding.GetEncoding(28591).GetString(ms.ToArray());   // byte a byte
                int e = all.IndexOf("\r\n\r\n");
                body = e >= 0 ? all.Substring(e + 4) : "";
                return e >= 0 ? all.Substring(0, e) : all;
            }
        }

        static string Basic(NetworkCredential nc) { return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(nc.UserName + ":" + nc.Password)); }

        static void O4_BridgeToken()
        {
            TestSocksServer x = Socks();
            SocksSettings s = new SocksSettings(); s.Type = "5"; s.Host = "127.0.0.1"; s.Port = x.Port;
            NetworkCredential good = SocksBridge.CredentialFor(s);
            Check(good.Password.Length >= 24, "Token demasiado corto");
            Check(SocksBridge.CredentialFor(s).Password == good.Password, "La misma configuración debe usar la misma ruta");
            string url = SocksUrl("token.invalid", "range", "t.bin", 1000, NewId("token"));
            int conns = x.Count("conn");
            string body;
            string h = RawBridge("GET " + url + " HTTP/1.1\r\nHost: token.invalid\r\n\r\n", out body);
            Check(h.StartsWith("HTTP/1.1 407"), "Sin token: " + h);
            Check(h.IndexOf("Proxy-Authenticate: Basic", StringComparison.OrdinalIgnoreCase) >= 0, "Falta Proxy-Authenticate");
            h = RawBridge("GET " + url + " HTTP/1.1\r\nHost: token.invalid\r\nProxy-Authorization: " + Basic(new NetworkCredential(good.UserName, good.Password + "x")) + "\r\n\r\n", out body);
            Check(h.StartsWith("HTTP/1.1 407"), "Token malo: " + h);
            h = RawBridge("GET " + url + " HTTP/1.1\r\nHost: token.invalid\r\nProxy-Authorization: " + Basic(new NetworkCredential("s999", good.Password)) + "\r\n\r\n", out body);
            Check(h.StartsWith("HTTP/1.1 407"), "Ruta inexistente: " + h);
            h = RawBridge("CONNECT token.invalid:" + S.Port + " HTTP/1.1\r\nHost: token.invalid\r\n\r\n", out body);
            Check(h.StartsWith("HTTP/1.1 407"), "CONNECT sin token: " + h);
            Check(x.Count("conn") == conns, "Sin token no debe abrirse ninguna conexión SOCKS");
            // Con el token, sí (y la respuesta llega con Connection: close).
            h = RawBridge("GET " + url + " HTTP/1.1\r\nHost: token.invalid\r\nProxy-Connection: keep-alive\r\nProxy-Authorization: " + Basic(good) + "\r\n\r\n", out body);
            Check(h.StartsWith("HTTP/1.1 200"), "Con token: " + h);
            Check(h.IndexOf("Connection: close", StringComparison.OrdinalIgnoreCase) >= 0, "La respuesta debe llevar Connection: close: " + h);
            Check(body.Length == 1000, "Cuerpo de " + body.Length + " bytes");
            Check(x.Count("conn") == conns + 1, "Con token debe abrirse una conexión SOCKS");
            // Cabecera enorme sin token: 431, sin abrir nada.
            h = RawBridge("GET " + url + " HTTP/1.1\r\nHost: token.invalid\r\nX-Relleno: " + new string('a', 70000) + "\r\n\r\n", out body);
            Check(h.StartsWith("HTTP/1.1 431"), "Cabecera enorme: " + (h.Length > 80 ? h.Substring(0, 80) : h));
            Check(x.Count("conn") == conns + 1, "Una cabecera enorme no debe abrir conexiones SOCKS");
            // El puente solo escucha en 127.0.0.1.
            Check(SocksBridge.Address.Host == "127.0.0.1", "El puente debe escuchar solo en 127.0.0.1");
            CheckBridgeIdle();
        }

        static void O4_BridgeConnect()
        {
            TestSocksServer x = Socks();
            SocksSettings s = new SocksSettings(); s.Type = "5"; s.Host = "127.0.0.1"; s.Port = x.Port;
            NetworkCredential good = SocksBridge.CredentialFor(s);
            using (TcpClient t0 = new TcpClient())
            {
                t0.Connect(IPAddress.Loopback, SocksBridge.Address.Port);
                t0.ReceiveTimeout = 15000;
                NetworkStream n0 = t0.GetStream();
                byte[] q0 = Encoding.ASCII.GetBytes("CONNECT tunel.invalid:" + S.Port + " HTTP/1.1\r\nHost: tunel.invalid:" + S.Port + "\r\n\r\n");
                n0.Write(q0, 0, q0.Length);
                string h0 = ReadHeadRaw(n0);
                Check(h0.StartsWith("HTTP/1.1 407") && h0.IndexOf("Connection: close", StringComparison.OrdinalIgnoreCase) >= 0, "CONNECT sin token: " + h0);
                Check(n0.ReadByte() < 0, "Tras el 407 el puente cierra la conexión");
            }
            using (TcpClient t = new TcpClient())
            {
                // .NET repite la petición con el token en una conexión nueva.
                t.Connect(IPAddress.Loopback, SocksBridge.Address.Port);
                t.ReceiveTimeout = 15000;
                NetworkStream ns = t.GetStream();
                string h;
                byte[] q = Encoding.ASCII.GetBytes("CONNECT tunel.invalid:" + S.Port + " HTTP/1.1\r\nHost: tunel.invalid:" + S.Port + "\r\nProxy-Authorization: " + Basic(good) + "\r\n\r\n");
                ns.Write(q, 0, q.Length);
                h = ReadHeadRaw(ns);
                Check(h.StartsWith("HTTP/1.1 200"), "CONNECT con token: " + h);
                // Dentro del túnel van bytes en bruto (aquí, HTTP sin cifrar hacia TestServer).
                q = Encoding.ASCII.GetBytes("GET /f/range/c.bin?size=500&id=" + NewId("tunel") + " HTTP/1.1\r\nHost: tunel.invalid\r\nConnection: close\r\n\r\n");
                ns.Write(q, 0, q.Length);
                h = ReadHeadRaw(ns);
                Check(h.StartsWith("HTTP/1.1 200"), "Respuesta por el túnel: " + h);
            }
            Check(x.Count("host:tunel.invalid") >= 1, "El túnel no pasó por SOCKS");
            // https de verdad con HttpWebRequest: el TLS va de extremo a extremo dentro del túnel.
            TcpListener tls = new TcpListener(IPAddress.Loopback, 0);
            tls.Start();
            int tlsPort = ((IPEndPoint)tls.LocalEndpoint).Port;
            X509Certificate2 cert = TestFtpServer.MakeCert();
            int served = 0;
            Thread srv = new Thread(delegate()
            {
                for (int i = 0; i < 3; i++)
                {
                    TcpClient cl;
                    try { cl = tls.AcceptTcpClient(); } catch { return; }
                    try
                    {
                        using (cl)
                        using (SslStream ssl = new SslStream(cl.GetStream(), false))
                        {
                            ssl.AuthenticateAsServer(cert, false, SslProtocols.Tls12, false);
                            StringBuilder sb = new StringBuilder();
                            int ch;
                            while ((ch = ssl.ReadByte()) >= 0) { sb.Append((char)ch); if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n") break; }
                            byte[] r = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 13\r\nConnection: close\r\n\r\nhola por TLS!");
                            ssl.Write(r, 0, r.Length); ssl.Flush();
                            Interlocked.Increment(ref served);
                        }
                    }
                    catch { }
                }
            });
            srv.IsBackground = true; srv.Start();
            try
            {
                AppConfig c = SocksCfg("5", null, null);
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://tls.invalid:" + tlsPort + "/hola");
                req.Proxy = NetSettings.ProxyFor(c, req.RequestUri);
                req.Timeout = 15000;
                req.ServerCertificateValidationCallback = delegate { return true; };   // certificado de prueba autofirmado
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader rd = new StreamReader(resp.GetResponseStream()))
                {
                    string text = rd.ReadToEnd();
                    Check(text == "hola por TLS!", "Respuesta https: " + text);
                }
                Check(served >= 1 && x.Count("host:tls.invalid") >= 1, "https no pasó por SOCKS");
            }
            finally { tls.Stop(); }
            CheckBridgeIdle();
        }

        // Servidor HTTP (o HTTPS si hay cert) que responde lo mismo a todo; se para con Stop().
        static TcpListener RawHttpServer(X509Certificate2 cert, string response)
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            Thread t = new Thread(delegate()
            {
                while (true)
                {
                    TcpClient cl;
                    try { cl = l.AcceptTcpClient(); } catch { return; }
                    Thread h = new Thread(delegate()
                    {
                        try
                        {
                            using (cl)
                            {
                                cl.ReceiveTimeout = 15000;
                                Stream st = cl.GetStream();
                                if (cert != null)
                                {
                                    SslStream ssl = new SslStream(st, false);
                                    ssl.AuthenticateAsServer(cert, false, SslProtocols.Tls12, false);
                                    st = ssl;
                                }
                                StringBuilder sb = new StringBuilder();
                                int ch;
                                while ((ch = st.ReadByte()) >= 0) { sb.Append((char)ch); if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n") break; }
                                byte[] r = Encoding.ASCII.GetBytes(response);
                                st.Write(r, 0, r.Length); st.Flush();
                                st.Close();
                            }
                        }
                        catch { }
                    });
                    h.IsBackground = true; h.Start();
                }
            });
            t.IsBackground = true; t.Start();
            return l;
        }

        static string ReadHeadRaw(NetworkStream ns)
        {
            StringBuilder sb = new StringBuilder();
            int b;
            while ((b = ns.ReadByte()) >= 0)
            {
                sb.Append((char)b);
                if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n") break;
            }
            return sb.ToString();
        }

        // Un servidor cualquiera manda las cabeceras de error del puente (con una marca inventada): ni directo, ni
        // por el puente (http: las quita; https: van dentro del túnel y no las puede quitar) se convierten en un
        // error permanente ni muestran su texto.
        static void O4_SpoofedSocksHeaders()
        {
            TestSocksServer x = Socks();
            const string fake = "HTTP/1.1 502 Bad Gateway\r\nX-IDMClone-Socks: auth\r\nX-IDMClone-Socks-Message: Texto%20falso%20del%20servidor%20(contrase%C3%B1a)\r\n" +
                                "X-IDMClone-Socks-Auth: marca-inventada\r\nContent-Type: text/plain\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            X509Certificate2 cert = TestFtpServer.MakeCert();
            TcpListener plain = RawHttpServer(null, fake), tls = RawHttpServer(cert, fake);
            int pp = ((IPEndPoint)plain.LocalEndpoint).Port, tp = ((IPEndPoint)tls.LocalEndpoint).Port;
            MethodInfo permanent = typeof(DownloadEngine).GetMethod("PermanentError", BindingFlags.NonPublic | BindingFlags.Static);
            Check(permanent != null, "No se encontró DownloadEngine.PermanentError");
            try
            {
                AppConfig c = SocksCfg("5", null, null);
                string[] urls = { "http://127.0.0.1:" + pp + "/f", "https://127.0.0.1:" + tp + "/f", "http://falso.invalid:" + pp + "/f", "https://falso.invalid:" + tp + "/f" };
                for (int i = 0; i < urls.Length; i++)
                {
                    bool viaBridge = i >= 2;
                    string what = (viaBridge ? "por el puente " : "directo ") + urls[i];
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(urls[i]);
                    req.Proxy = viaBridge ? NetSettings.ProxyFor(c, req.RequestUri) : null;
                    req.Timeout = 15000;
                    req.ServerCertificateValidationCallback = delegate { return true; };   // certificado de prueba autofirmado
                    try { using (req.GetResponse()) { } throw new Exception(what + ": la petición debería fallar con 502"); }
                    catch (WebException wex)
                    {
                        HttpWebResponse hr = wex.Response as HttpWebResponse;
                        Check(hr != null && (int)hr.StatusCode == 502, what + ": se esperaba el 502 del servidor: " + wex.Status + " " + wex.Message);
                        string why = SocksBridge.Explain(wex);
                        Check(why == null, what + ": Explain creyó las cabeceras del servidor: " + why);
                        Check(!SocksBridge.IsPermanent(wex), what + ": IsPermanent creyó las cabeceras del servidor");
                        string perm = (string)permanent.Invoke(null, new object[] { wex });
                        Check(perm == null, what + ": PermanentError lo da por permanente: " + perm);
                        if (i == 2) Check(hr.Headers[SocksBridge.ErrorHeader] == null && hr.Headers[SocksBridge.MarkerHeader] == null && hr.Headers[SocksBridge.MessageHeader] == null,
                                          what + ": el puente debe quitar las cabeceras X-IDMClone-Socks* del servidor");
                    }
                }
                Check(x.Count("host:falso.invalid") >= 2, "Las peticiones al servidor falso no pasaron por SOCKS");
                // En bruto: el puente quita las cabeceras del servidor en http.
                string body;
                string h = RawBridge("GET http://falso.invalid:" + pp + "/f HTTP/1.1\r\nHost: falso.invalid\r\nProxy-Authorization: " +
                                     Basic(SocksBridge.CredentialFor(SocksSettings.From(c))) + "\r\n\r\n", out body);
                Check(h.StartsWith("HTTP/1.1 502") && h.IndexOf("X-IDMClone-Socks", StringComparison.OrdinalIgnoreCase) < 0, "Respuesta del servidor falso por el puente: " + h);
            }
            finally { plain.Stop(); tls.Stop(); }
            CheckBridgeIdle();
        }

        // Otro proceso abre muchas conexiones sin token y manda un byte: el puente cierra las más antiguas (y las
        // que no mandan la cabecera en 3 s) y atiende las peticiones de verdad.
        static void O4_BridgePreAuthFlood()
        {
            TestSocksServer x = Socks();
            AppConfig c = SocksCfg("5", null, null);
            NetworkCredential good = SocksBridge.CredentialFor(SocksSettings.From(c));
            int port = SocksBridge.Address.Port;
            List<TcpClient> slow = new List<TcpClient>();
            DateTime t0 = DateTime.UtcNow;
            try
            {
                for (int i = 0; i < 70; i++)
                {
                    TcpClient t = new TcpClient();
                    t.Connect(IPAddress.Loopback, port);
                    t.GetStream().Write(new byte[] { (byte)'G' }, 0, 1);
                    slow.Add(t);
                }
                Thread.Sleep(300);   // que el puente las tenga todas aceptadas
                string body;
                string h = RawBridge("GET " + SocksUrl("flood.invalid", "range", "f.bin", 1000, NewId("flood")) + " HTTP/1.1\r\nHost: flood.invalid\r\nProxy-Authorization: " +
                                     Basic(good) + "\r\n\r\n", out body);
                Check(h.StartsWith("HTTP/1.1 200") && body.Length == 1000, "Petición con token tras 70 conexiones lentas: " + h);
                // Y con HttpWebRequest (dos conexiones: la del 407 y la del token).
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(SocksUrl("flood.invalid", "range", "g.bin", 2000, NewId("flood")));
                req.Proxy = NetSettings.ProxyFor(c, req.RequestUri);
                req.Timeout = 15000;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (MemoryStream ms = new MemoryStream())
                {
                    resp.GetResponseStream().CopyTo(ms);
                    Check(ms.Length == 2000, "HttpWebRequest tras 70 conexiones lentas: " + ms.Length + " bytes");
                }
                double secs = (DateTime.UtcNow - t0).TotalSeconds;
                if (secs >= 3) Console.WriteLine("    (aviso: las peticiones acabaron a los " + secs.ToString("0.0") + " s, después del plazo de las lentas)");
                // La más antigua ya la había cerrado el puente para hacer sitio.
                bool closed;
                TcpClient first = slow[0];
                first.ReceiveTimeout = 2000;
                try { closed = first.GetStream().Read(new byte[16], 0, 16) <= 0; } catch (IOException) { closed = true; }
                Check(closed, "La conexión lenta más antigua debería estar cerrada");
                // Las lentas que quedan se cierran por el plazo de la cabecera (unos 3 s), no a los 15.
                TcpClient last = slow[slow.Count - 1];
                last.ReceiveTimeout = 8000;
                try { closed = last.GetStream().Read(new byte[16], 0, 16) <= 0; } catch (IOException) { closed = true; }
                double at = (DateTime.UtcNow - t0).TotalSeconds;
                Check(closed && at < 6.5, "Una conexión sin cabecera debe cerrarse a los ~3 s (cerrada " + closed + " a los " + at.ToString("0.0") + " s)");
            }
            finally { foreach (TcpClient t in slow) { try { t.Close(); } catch { } } }
            Check(x.Count("host:flood.invalid") >= 2, "Las peticiones no pasaron por SOCKS");
            CheckBridgeIdle();
        }

        static void O4_BypassAndSchemes()
        {
            AppConfig c = SocksCfg("5", null, null);
            c.ProxyBypass = "directo.invalid; *.intranet.invalid";
            Uri a = new Uri("http://directo.invalid/x"), b = new Uri("https://srv.intranet.invalid:8443/x"), o = new Uri("http://otro.invalid/x");
            IWebProxy p = NetSettings.ProxyFor(c, o);
            Check(p != null && !p.IsBypassed(o) && p.GetProxy(o).Port == SocksBridge.Address.Port, "otro.invalid debe ir por el puente");
            Check(p.IsBypassed(a), "directo.invalid está en la lista de excepciones");
            Check(p.IsBypassed(b), "*.intranet.invalid está en la lista de excepciones (con puerto y https)");
            foreach (string no in new string[] { "http://nodirecto.invalid/", "http://directo.invalid.otro/", "http://intranet.invalid/", "http://directo.invalidx/" })
                Check(!p.IsBypassed(new Uri(no)), no + " no debería coincidir con la lista de excepciones");
            AppConfig lc = SocksCfg("5", null, null); lc.ProxyBypass = "<local>";
            IWebProxy lp = NetSettings.ProxyFor(lc, o);
            Check(lp.IsBypassed(new Uri("http://servidorlocal/x")) && !lp.IsBypassed(o), "<local>: los nombres sin punto van directos");
            // Proxy HTTP normal con la misma lista (antes "ejemplo.com" sin comodín nunca coincidía).
            AppConfig h = Cfg(); h.ProxyMode = "manual"; h.ProxyHost = "proxy.invalid"; h.ProxyPort = 3128; h.ProxyBypass = "directo.invalid";
            IWebProxy hp = NetSettings.ProxyFor(h, a);
            Check(hp.IsBypassed(a) && !hp.IsBypassed(o) && hp.GetProxy(o).Host == "proxy.invalid", "Lista de excepciones del proxy HTTP");
            // Casillas de protocolos.
            h.ProxyHttps = false;
            IWebProxy only = NetSettings.ProxyFor(h, o);
            Uri s = new Uri("https://seguro.invalid/x");
            Check(!only.IsBypassed(o) && only.IsBypassed(s) && only.GetProxy(s) == s, "Con https desmarcado, https va directo");
            h.ProxyHttps = true; h.ProxyHttp = false;
            only = NetSettings.ProxyFor(h, o);
            Check(only.IsBypassed(o) && !only.IsBypassed(s), "Con http desmarcado, http va directo");
            h.ProxyHttps = false;
            Check(NetSettings.ProxyFor(h, o) == null, "Sin protocolos marcados no hay proxy");
            // SOCKS desactivado: vuelve al proxy HTTP.
            c.SocksEnabled = false; c.ProxyHost = "proxy.invalid"; c.ProxyPort = 3128;
            Check(NetSettings.ProxyFor(c, o).GetProxy(o).Host == "proxy.invalid", "Sin SOCKS se usa el proxy HTTP");
            // Normalización de la configuración.
            AppConfig n = Cfg();
            n.SocksType = "SOCKS4a"; Check(n.SocksType == "4a", "Tipo " + n.SocksType);
            n.SocksType = "raro"; Check(n.SocksType == "5", "Tipo por defecto " + n.SocksType);
            n.SocksPort = 0; Check(n.SocksPort == 1080, "Puerto por defecto " + n.SocksPort);
            Check(Cfg().ProxyHttp && Cfg().ProxyHttps && Cfg().UseBrowserProxyOnError && !Cfg().SocksEnabled, "Valores por defecto");
            // "Obtener del sistema": formatos de ProxyServer de Windows.
            string ht, so;
            OptionsForm.ParseWindowsProxy("proxy.invalid:3128", out ht, out so);
            Check(ht == "proxy.invalid:3128" && so == null, "Un solo proxy: " + ht + " / " + so);
            OptionsForm.ParseWindowsProxy("https=s.invalid:443;socks=k.invalid:1080", out ht, out so);
            Check(ht == "s.invalid:443" && so == "k.invalid:1080", "https + socks: " + ht + " / " + so);
            OptionsForm.ParseWindowsProxy("socks=k.invalid:1080", out ht, out so);
            Check(ht == null && so == "k.invalid:1080", "Solo SOCKS no es un proxy HTTP: " + ht + " / " + so);
            OptionsForm.ParseWindowsProxy("http=h.invalid:80;https=s.invalid:443;ftp=f.invalid:21", out ht, out so);
            Check(ht == "h.invalid:80" && so == null, "http por protocolo: " + ht);
            // Patrones que parecen una IPv4: el comodín solo vale por cifras (no por "5.evil.io"); por SOCKS y por proxy HTTP.
            foreach (AppConfig ipc in new AppConfig[] { SocksCfg("5", null, null), Cfg() })
            {
                if (!ipc.SocksEnabled) { ipc.ProxyMode = "manual"; ipc.ProxyHost = "proxy.invalid"; ipc.ProxyPort = 3128; }
                ipc.ProxyBypass = "10.0.0.*; 192.168.*; 172.16.?.1";
                IWebProxy ip = NetSettings.ProxyFor(ipc, o);
                foreach (string yes in new string[] { "http://10.0.0.5/x", "https://10.0.0.123:8443/x", "http://192.168.1.20/", "http://172.16.3.1/" })
                    Check(ip.IsBypassed(new Uri(yes)), yes + " debería ir directo (" + ipc.ProxyBypass + ")");
                foreach (string no in new string[] { "http://10.0.0.5.evil.io/x", "https://10.0.0.5.evil.io:8443/x", "http://10.0.0.evil.io/", "http://10.0.0.1x/",
                                                     "http://192.168.1.20.evil.io/", "http://192.168.evil.io/", "http://172.16.33.1/", "http://172.16.a.1/" })
                    Check(!ip.IsBypassed(new Uri(no)), no + " no debería coincidir con " + ipc.ProxyBypass);
            }
        }

        static void O4_QuotaWarnOnce()
        {
            AppConfig c = Cfg(); c.QuotaEnabled = true; c.QuotaMB = 1; c.MaxConnections = 8;
            DownloadQuota.Reset();
            DownloadQuota.WindowSecondsOverride = 2;
            List<DateTime> seen = new List<DateTime>();
            Action<DateTime> h = delegate(DateTime until) { lock (seen) seen.Add(until); };
            DownloadQuota.LimitReached += h;
            try
            {
                DownloadItem it = Download(new DownloadManager(c), "range", 3 * 1024 * 1024, "quotawarn", 60);
                AssertComplete(it, 3 * 1024 * 1024);
                List<DateTime> distinct = new List<DateTime>();
                lock (seen) foreach (DateTime d in seen) if (!distinct.Contains(d)) distinct.Add(d);
                Check(seen.Count >= 2 && seen.Count <= 3, "Avisos: " + seen.Count + " (3 MB con 1 MB por periodo: 2 o 3)");
                Check(distinct.Count == seen.Count, "Más de un aviso en el mismo periodo (" + seen.Count + " avisos, " + distinct.Count + " periodos)");
                foreach (DateTime d in seen) Check(d.Kind == DateTimeKind.Utc, "La hora del aviso debe ir en UTC");
            }
            finally { DownloadQuota.LimitReached -= h; DownloadQuota.WindowSecondsOverride = 0; DownloadQuota.Reset(); }
            // Sin límites, nunca avisa.
            int before = seen.Count;
            DownloadQuota.LimitReached += h;
            try { AssertComplete(Download(NewMgr(), "range", 512 * 1024, "quotaoff", 30), 512 * 1024); }
            finally { DownloadQuota.LimitReached -= h; }
            Check(seen.Count == before, "Avisó sin límites activos");
        }

        static void O4_OptionsRoundTrip()
        {
            UseFreshHome("opc4-proxy");
            AppConfig c = Cfg();
            c.ProxyMode = "manual"; c.ProxyHost = "proxy.invalid";
            c.SocksEnabled = true; c.SocksType = "4a"; c.SocksHost = "socks.invalid"; c.SocksPort = 1081; c.SocksUser = "u"; c.SocksPasswordEnc = Util.Protect("p1");
            c.FtpPassive = true; c.UseBrowserProxyOnError = true;
            using (OptionsForm o = new OptionsForm(c))
            {
                IntPtr hnd = o.Handle;
                CheckBox pasv = Field<CheckBox>(o, "_pasv"), browser = Field<CheckBox>(o, "_pxBrowser"), http = Field<CheckBox>(o, "_pxHttp"), https = Field<CheckBox>(o, "_pxHttps");
                Check(pasv.Enabled && pasv.Checked, "La casilla PASV debe estar activa y marcada");
                Check(browser.Enabled && browser.Checked, "La casilla del proxy del navegador debe estar activa y marcada");
                Check(http.Enabled && https.Enabled && http.Checked && https.Checked, "Casillas http/https activas en modo manual");
                Check(Field<bool>(o, "_sxOn") && Field<string>(o, "_sxType") == "4a" && Field<string>(o, "_sxPass") == "p1", "No cargó el SOCKS");
                // Fuera del modo manual, las casillas de protocolos y "Avanzado... / Socks..." se desactivan.
                Field<RadioButton>(o, "_pxNone").Checked = true;
                Check(!http.Enabled && !https.Enabled && pasv.Enabled && browser.Enabled, "Con \"No hay proxy\" las casillas http/https deben desactivarse");
                Field<RadioButton>(o, "_pxManual").Checked = true;
                Check(http.Enabled && https.Enabled, "Al volver a manual se activan");
                pasv.Checked = false; browser.Checked = false; https.Checked = false;
                typeof(OptionsForm).GetField("_sxType", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(o, "5");
                typeof(OptionsForm).GetField("_sxPass", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(o, "p2");
                typeof(OptionsForm).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(o, null);
            }
            Check(!c.FtpPassive && !c.UseBrowserProxyOnError && c.ProxyHttp && !c.ProxyHttps, "No se guardaron las casillas");
            Check(c.SocksEnabled && c.SocksType == "5" && c.SocksHost == "socks.invalid" && c.SocksPort == 1081 && c.SocksUser == "u", "No se guardó el SOCKS");
            Check(c.SocksPasswordEnc != "p2" && Util.Unprotect(c.SocksPasswordEnc) == "p2", "La contraseña SOCKS debe guardarse cifrada");
            // Y sobrevive a guardar y cargar el archivo.
            AppConfig back = AppConfig.Load();
            Check(!back.FtpPassive && !back.UseBrowserProxyOnError && !back.ProxyHttps && back.SocksEnabled && back.SocksType == "5" && back.SocksPort == 1081
                  && Util.Unprotect(back.SocksPasswordEnc) == "p2", "No se conservó al recargar config.json");
            // Una configuración antigua (sin estas claves) carga con los valores por defecto.
            AppConfig old = AppConfig.NewSerializer().Deserialize<AppConfig>("{\"ProxyMode\":\"manual\",\"ProxyHost\":\"p\",\"ProxyPort\":8080}");
            Check(old.FtpPassive && old.UseBrowserProxyOnError && old.ProxyHttp && old.ProxyHttps && !old.SocksEnabled && old.SocksType == "5" && old.SocksPort == 1080,
                  "Valores por defecto al cargar una configuración antigua");
            // Página Discado / VPN: sigue informativa, con la nota nueva.
            using (OptionsForm o = new OptionsForm(Cfg()))
            {
                IntPtr hnd = o.Handle;
                TabPage dial = null;
                foreach (TabPage p in Field<TabControl>(o, "_tabs").TabPages) if (p.Name == "dialup") dial = p;
                bool note = false, anyOn = false;
                foreach (Control k in dial.Controls)
                {
                    if (k is Label && k.Text.IndexOf("VPN", StringComparison.Ordinal) >= 0 && k.Text.IndexOf("Proxy/Socks", StringComparison.Ordinal) >= 0) note = true;
                    if ((k is CheckBox || k is GroupBox) && k.Enabled) anyOn = true;
                }
                Check(note, "Falta la nota de Discado / VPN");
                Check(!anyOn, "La página Discado / VPN no debe tener opciones activas (no hace nada)");
            }
        }

        static void O4_AdvancedDialog()
        {
            using (ProxyAdvancedForm f = new ProxyAdvancedForm("a.invalid", true, "4", "socks.invalid", 9050, "yo", "pw"))
            {
                CheckNoOverlap(f, "Avanzado / Socks");
                Check(f.SocksEnabled && f.SocksType == "4" && f.SocksHost == "socks.invalid" && f.SocksPort == 9050 && f.SocksUser == "yo" && f.SocksPassword == "pw", "Valores del diálogo");
                Check(f.Bypass == "a.invalid", "Lista de excepciones");
            }
            using (ProxyAdvancedForm f = new ProxyAdvancedForm("x"))
            {
                IntPtr hnd = f.Handle;
                Check(!f.SocksEnabled && f.SocksType == "5" && f.SocksPort == 1080, "Valores por defecto del diálogo");
            }
            using (ProxyAdvancedForm f = new ProxyAdvancedForm("", true, "SOCKS4A", "h.invalid", 1080, "", "")) Check(f.SocksType == "4a", "SOCKS4A -> " + f.SocksType);
            using (ProxyAdvancedForm f = new ProxyAdvancedForm("", true, "raro", "h.invalid", 1080, "", "")) Check(f.SocksType == "5", "Tipo desconocido -> " + f.SocksType);
        }
    }
}
