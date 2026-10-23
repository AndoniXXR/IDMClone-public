using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace IDMClone.Tests
{
    // Opciones que antes solo se guardaban (área 1, motor): ver scratchpad\opciones-adorno.md.
    //   #5  Ignorar los cambios de la hora del archivo al continuar     #6  Fecha del archivo = la del servidor
    //   #15 FTP en modo PASV / activo (con paso al otro modo si falla)   #17 Proxy del navegador si hay errores
    public static partial class TestMain
    {
        static void RunOpciones1()
        {
            // --- #5 ---
            Run("opción 1: motor, ignorar fecha DESACTIVADA: servidor que cambia la fecha en cada petición -> se reinicia (como siempre)", O1_IgnoreDateOff);
            Run("opción 1: motor, ignorar fecha ACTIVADA: varias conexiones + pausa/reanudación sin reiniciar y archivo íntegro", O1_IgnoreDateOn);
            Run("opción 1: motor, ignorar fecha ACTIVADA: si cambia el TAMAÑO se detecta y se baja entero", O1_IgnoreDateSizeChange);
            Run("opción 1: motor, ignorar fecha ACTIVADA: servidor sin fecha (solo ETag) sigue detectando cambios", O1_IgnoreDateEtagOnly);
            Run("opción 1: motor, ignorar fecha ACTIVADA: mismo tamaño y otro contenido no se detecta (riesgo aceptado, como IDM)", O1_IgnoreDateRisk);
            // --- #6 ---
            Run("opción 1: fecha HTTP: RFC 1123, RFC 850 y asctime; otras zonas y basura no", O1_HttpDateParser);
            Run("opción 1: motor, fecha del servidor sin carpeta temporal (creación y modificación)", O1_FileDateNoTemp);
            Run("opción 1: motor, fecha del servidor con carpeta temporal (tras mover)", O1_FileDateTemp);
            Run("opción 1: motor, fecha del servidor DESACTIVADA -> fecha de ahora", O1_FileDateOff);
            Run("opción 1: motor, fecha del servidor ausente, ilegible o futura -> se ignora y completa", O1_FileDateBad);
            Run("opción 1: motor, fecha del servidor en formato RFC 850 y asctime", O1_FileDateOldFormats);
            Run("opción 1: ftp, fecha del servidor con MDTM (solo si la opción está activa)", O1_FtpDate);
            // --- #15 ---
            Run("opción 1: ftp, configuración antigua sin FtpPassive -> pasivo", O1_FtpPassiveDefault);
            Run("opción 1: ftp, modo pasivo -> solo PASV", O1_FtpPassive);
            Run("opción 1: ftp, modo activo -> PORT, varias conexiones con REST", O1_FtpActive);
            Run("opción 1: ftp, modo activo que no funciona -> pasa a pasivo y completa", O1_FtpActiveFallsBack);
            Run("opción 1: ftp, modo pasivo que no funciona -> pasa a activo y completa", O1_FtpPassiveFallsBack);
            Run("opción 1: ftps en modo activo (PROT P) -> completo y cifrado", O1_FtpsActive);
            // --- #17 ---
            Run("opción 1: proxy del navegador: formato host:puerto y qué errores lo activan", O1_BrowserProxyUnit);
            Run("opción 1: motor, proxy del navegador: sin conexión directa -> se baja por el proxy del navegador", O1_BrowserProxyFallback);
            Run("opción 1: motor, proxy del navegador: un 404 no lo usa y falla enseguida", O1_BrowserProxyNot404);
            Run("opción 1: motor, proxy del navegador: un 403 del servidor no lo usa y falla enseguida", O1_BrowserProxyNot403);
        }

        // ------------------------------------------------------------ #5

        static void O1_IgnoreDateOff()
        {
            long size = 2 * 1024 * 1024;
            DownloadItem it = Download(NewMgr(), "range,slow,lmflap", size, "lmoff", 90);
            string id = IdOf(it);
            Check(S.Count(id + ":ifrange-miss") >= 1, "No se envió If-Range con la fecha (el servidor no vio el cambio)");
            Check(S.Count(id + ":first") >= 2, "No reinició la descarga: " + S.Count(id + ":first") + " primeras conexiones");
            Check(it.Status == DownloadStatus.Error && it.Error != null && it.Error.Contains("cambia continuamente"),
                  "Se esperaba el error de siempre ('cambia continuamente'): " + it.Status + " " + it.Error);
        }

        static void O1_IgnoreDateOn()
        {
            AppConfig c = Cfg(); c.IgnoreDateChangesOnResume = true;
            DownloadManager m = new DownloadManager(c);
            long size = 2 * 1024 * 1024;
            string id = NewId("lmon");
            DownloadItem it = m.Add(S.Url("range,veryslow,lmflap", "lmon.bin", size, id), Dl, null, true);
            WaitProgress(it, 300 * 1024, 30);
            m.StopItem(it); WaitEnd(it, 10);
            Check(it.Status == DownloadStatus.Paused, "Tras pausar: " + it.Status + " " + it.Error);
            m.StartItem(it); WaitEnd(it, 90);
            AssertComplete(it, size);
            Check(S.Count(id + ":ifrange-miss") == 0, "Se envió If-Range: " + S.Count(id + ":ifrange-miss"));
            Check(S.Count(id + ":first") == 1, "Reinició la descarga: " + S.Count(id + ":first") + " primeras conexiones");
            Check(S.Count(id + ":ranged") >= 2, "No usó varias conexiones");
        }

        static void O1_IgnoreDateSizeChange()
        {
            AppConfig c = Cfg(); c.IgnoreDateChangesOnResume = true;
            DownloadManager m = new DownloadManager(c);
            long size = 2 * 1024 * 1024, newSize = 1536 * 1024 + 77;
            string id = NewId("lmsize");
            DownloadItem it = m.Add(S.Url("range,veryslow", "lmsize.bin", size, id), Dl, null, true);
            WaitProgress(it, 300 * 1024, 30);
            m.StopItem(it); WaitEnd(it, 10);
            S.SetSize(id, newSize); // el archivo cambia de tamaño en el servidor mientras está en pausa
            m.StartItem(it); WaitEnd(it, 90);
            AssertComplete(it, newSize);
            Check(S.Count(id + ":first") >= 2, "No volvió a empezar al cambiar el tamaño");
        }

        static void O1_IgnoreDateEtagOnly()
        {
            AppConfig c = Cfg(); c.IgnoreDateChangesOnResume = true;
            DownloadManager m = new DownloadManager(c);
            long size = 2 * 1024 * 1024;
            string id = NewId("lmetag");
            DownloadItem it = m.Add(S.Url("range,veryslow,nolm", "lmetag.bin", size, id), Dl, null, true);
            WaitProgress(it, 300 * 1024, 30);
            m.StopItem(it); WaitEnd(it, 10);
            Check(it.LastModified == null && it.ETag != null, "El servidor no debía dar fecha: " + it.LastModified);
            S.SetVersion(id, 2);
            m.StartItem(it); WaitEnd(it, 90);
            Check(it.Status == DownloadStatus.Complete, "Estado " + it.Status + " " + it.Error);
            Check(FileSha(it.FilePath) == TestServer.Sha256Of(size, 2), "Mezcla de versiones: sin fecha, el ETag debe seguir mandando");
        }

        static void O1_IgnoreDateRisk()
        {
            AppConfig c = Cfg(); c.IgnoreDateChangesOnResume = true;
            DownloadManager m = new DownloadManager(c);
            long size = 2 * 1024 * 1024;
            string id = NewId("lmrisk");
            DownloadItem it = m.Add(S.Url("range,veryslow", "lmrisk.bin", size, id), Dl, null, true);
            WaitProgress(it, 300 * 1024, 30);
            m.StopItem(it); WaitEnd(it, 10);
            S.SetVersion(id, 2); // otra fecha y otro contenido, MISMO tamaño
            m.StartItem(it); WaitEnd(it, 90);
            Check(it.Status == DownloadStatus.Complete, "Estado " + it.Status + " " + it.Error);
            Check(S.Count(id + ":first") == 1 && S.Count(id + ":ifrange-miss") == 0, "Con la opción no debía comprobar la fecha");
            string sha = FileSha(it.FilePath);
            Console.WriteLine("        (documentado: con la opción marcada, un cambio real con el mismo tamaño deja el archivo mezclado: " +
                              (sha != TestServer.Sha256Of(size, 1) && sha != TestServer.Sha256Of(size, 2) ? "mezclado" : "no mezclado") + ")");
        }

        // ------------------------------------------------------------ #6

        static void O1_HttpDateParser()
        {
            DateTime want = new DateTime(1994, 11, 6, 8, 49, 37, DateTimeKind.Utc), d;
            string[] ok = { "Sun, 06 Nov 1994 08:49:37 GMT", "Sunday, 06-Nov-94 08:49:37 GMT", "Sun Nov  6 08:49:37 1994", "Sun Nov 6 08:49:37 1994", " Sun, 06 Nov 1994 08:49:37 GMT " };
            foreach (string s in ok)
            {
                Check(DownloadEngine.TryParseHttpDate(s, out d), "No entiende: '" + s + "'");
                Check(d == want && d.Kind == DateTimeKind.Utc, "'" + s + "' -> " + d.ToString("o"));
            }
            string[] bad = { null, "", "ayer", "Sun, 06 Nov 1994 08:49:37 +0200", "Sun, 06 Nov 1994 08:49:37 CET", "2026-01-01T10:00:00Z", "Mon, 06 Nov 1994 08:49:37 GMT" };
            foreach (string s in bad) Check(!DownloadEngine.TryParseHttpDate(s, out d), "Aceptó: '" + s + "' -> " + d.ToString("o"));
            Check(!DownloadEngine.ServerDateUsable("Fri, 01 Jan 2100 10:00:00 GMT", out d), "Aceptó una fecha futura");
            Check(!DownloadEngine.ServerDateUsable("Thu, 01 Jan 1970 00:00:00 GMT", out d), "Aceptó 1970 (fecha 'cero' de servidores rotos)");
            Check(DownloadEngine.ServerDateUsable("Thu, 01 Jan 2026 10:00:00 GMT", out d), "No aceptó una fecha normal");
            // Independiente de la referencia cultural del equipo.
            System.Globalization.CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("es-ES");
                Check(DownloadEngine.TryParseHttpDate("Sun, 06 Nov 1994 08:49:37 GMT", out d) && d == want, "Falla con la cultura es-ES");
            }
            finally { Thread.CurrentThread.CurrentCulture = saved; }
        }

        static readonly DateTime ServerDate = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc); // la que da TestServer (versión 1)

        static void CheckServerDate(string path, DateTime want)
        {
            DateTime w = File.GetLastWriteTimeUtc(path), cr = File.GetCreationTimeUtc(path);
            Check(w == want, "Fecha de modificación " + w.ToString("u") + ", se esperaba " + want.ToString("u"));
            Check(cr == want, "Fecha de creación " + cr.ToString("u") + ", se esperaba " + want.ToString("u"));
        }

        static void CheckDateIsNow(string path)
        {
            DateTime w = File.GetLastWriteTimeUtc(path);
            Check(Math.Abs((DateTime.UtcNow - w).TotalMinutes) < 2, "La fecha debía ser la de ahora: " + w.ToString("u"));
        }

        static void O1_FileDateNoTemp()
        {
            AppConfig c = Cfg(); c.SetFileDateFromServer = true; c.UseTempFolder = false;
            DownloadItem it = Download(new DownloadManager(c), "range,slow", 1024 * 1024 + 5, "fdnotemp", 60);
            AssertComplete(it, 1024 * 1024 + 5);
            CheckServerDate(it.FilePath, ServerDate);
        }

        static void O1_FileDateTemp()
        {
            AppConfig c = Cfg(); c.SetFileDateFromServer = true; c.UseTempFolder = true;
            c.TempFolder = Path.Combine(Home, "tmp-fecha");
            DownloadItem it = Download(new DownloadManager(c), "range,slow", 1024 * 1024 + 6, "fdtemp", 60);
            AssertComplete(it, 1024 * 1024 + 6);
            Check(it.TempPath == null && !it.FilePath.StartsWith(c.TempFolder, StringComparison.OrdinalIgnoreCase), "No se movió al destino: " + it.FilePath);
            CheckServerDate(it.FilePath, ServerDate);
        }

        static void O1_FileDateOff()
        {
            AppConfig c = Cfg(); c.SetFileDateFromServer = false;
            DownloadItem it = Download(new DownloadManager(c), "range", 300 * 1024, "fdoff", 60);
            AssertComplete(it, 300 * 1024);
            CheckDateIsNow(it.FilePath);
        }

        static void O1_FileDateBad()
        {
            foreach (string mode in new string[] { "nolm", "badlm", "futurelm" })
            {
                AppConfig c = Cfg(); c.SetFileDateFromServer = true;
                DownloadItem it = Download(new DownloadManager(c), "range," + mode, 200 * 1024, "fd" + mode, 60);
                AssertComplete(it, 200 * 1024);
                CheckDateIsNow(it.FilePath);
            }
        }

        static void O1_FileDateOldFormats()
        {
            DateTime want = new DateTime(2015, 3, 5, 8, 9, 10, DateTimeKind.Utc);
            foreach (string mode in new string[] { "oldlm", "asclm" })
            {
                AppConfig c = Cfg(); c.SetFileDateFromServer = true;
                DownloadItem it = Download(new DownloadManager(c), "range," + mode, 200 * 1024, "fd" + mode, 60);
                AssertComplete(it, 200 * 1024);
                CheckServerDate(it.FilePath, want);
            }
        }

        static void O1_FtpDate()
        {
            TestFtpServer ftp = new TestFtpServer(); ftp.Start();
            try
            {
                long size = 600 * 1024;
                AppConfig c = Cfg(); c.SetFileDateFromServer = true;
                DownloadItem it = new DownloadManager(c).Add(ftp.Url("normal", "fecha.bin", size, NewId("ftpdate")), Dl, null, true);
                WaitEnd(it, 40);
                AssertComplete(it, size);
                Check(ftp.Count("mdtm") >= 1, "No pidió MDTM");
                CheckServerDate(it.FilePath, TestFtpServer.MdtmUtc);

                AppConfig off = Cfg(); off.SetFileDateFromServer = false;
                int before = ftp.Count("mdtm");
                DownloadItem it2 = new DownloadManager(off).Add(ftp.Url("normal", "sinfecha.bin", size, NewId("ftpnodate")), Dl, null, true);
                WaitEnd(it2, 40);
                AssertComplete(it2, size);
                Check(ftp.Count("mdtm") == before, "Pidió MDTM sin necesitarlo");
                CheckDateIsNow(it2.FilePath);
            }
            finally { ftp.Stop(); }
        }

        // ------------------------------------------------------------ #15

        static void O1_FtpPassiveDefault()
        {
            Check(AppConfig.CreateDefault().FtpPassive, "Por defecto debía ser pasivo");
            AppConfig old = AppConfig.NewSerializer().Deserialize<AppConfig>("{\"MaxConnections\":4}");
            Check(old.FtpPassive, "Una configuración antigua (sin la clave) debía cargar en pasivo");
            AppConfig act = AppConfig.NewSerializer().Deserialize<AppConfig>("{\"FtpPassive\":false}");
            Check(!act.FtpPassive, "No conserva el modo activo guardado");
        }

        static DownloadItem FtpDownload(TestFtpServer ftp, bool passive, string tag, long size)
        {
            AppConfig c = Cfg(); c.FtpPassive = passive;
            DownloadItem it = new DownloadManager(c).Add(ftp.Url("normal", tag + ".bin", size, NewId(tag)), Dl, null, true);
            WaitEnd(it, 60);
            return it;
        }

        static string FtpIdOf(DownloadItem it) { string[] p = new Uri(it.Url).AbsolutePath.Split('/'); return p[p.Length - 2]; }

        static void O1_FtpPassive()
        {
            TestFtpServer ftp = new TestFtpServer(); ftp.Start();
            try
            {
                long size = 3L * 1024 * 1024;
                DownloadItem it = FtpDownload(ftp, true, "ftppasv", size);
                AssertComplete(it, size);
                Check(ftp.Count("pasv") >= 2 && ftp.Count("port") == 0, "PASV " + ftp.Count("pasv") + ", PORT " + ftp.Count("port"));
            }
            finally { ftp.Stop(); }
        }

        static void O1_FtpActive()
        {
            TestFtpServer ftp = new TestFtpServer(); ftp.Start();
            try
            {
                long size = 3L * 1024 * 1024;
                DownloadItem it = FtpDownload(ftp, false, "ftpport", size);
                AssertComplete(it, size);
                string id = FtpIdOf(it);
                Check(ftp.Count("port") >= 2 && ftp.Count("pasv") == 0, "PORT " + ftp.Count("port") + ", PASV " + ftp.Count("pasv"));
                Check(ftp.Count(id + ":active") >= 2 && ftp.Count(id + ":rest") >= 1, "Conexiones activas " + ftp.Count(id + ":active") + ", REST " + ftp.Count(id + ":rest"));
            }
            finally { ftp.Stop(); }
        }

        static void O1_FtpActiveFallsBack()
        {
            TestFtpServer ftp = new TestFtpServer(); ftp.RejectActive = true; ftp.Start();
            try
            {
                long size = 2L * 1024 * 1024;
                Stopwatch sw = Stopwatch.StartNew();
                DownloadItem it = FtpDownload(ftp, false, "ftpnoport", size);
                AssertComplete(it, size);
                string id = FtpIdOf(it);
                Check(ftp.Count("port") >= 1, "No probó el modo activo");
                Check(ftp.Count(id + ":passive") >= 1 && ftp.Count(id + ":active") == 0, "Pasivas " + ftp.Count(id + ":passive") + ", activas " + ftp.Count(id + ":active"));
                Check(sw.Elapsed.TotalSeconds < 30, "Tardó " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s en cambiar de modo");
            }
            finally { ftp.Stop(); }
        }

        static void O1_FtpPassiveFallsBack()
        {
            TestFtpServer ftp = new TestFtpServer(); ftp.RejectPassive = true; ftp.Start();
            try
            {
                long size = 2L * 1024 * 1024;
                Stopwatch sw = Stopwatch.StartNew();
                DownloadItem it = FtpDownload(ftp, true, "ftpnopasv", size);
                AssertComplete(it, size);
                string id = FtpIdOf(it);
                Check(ftp.Count("pasv") >= 1, "No probó el modo pasivo");
                Check(ftp.Count(id + ":active") >= 1 && ftp.Count(id + ":passive") == 0, "Activas " + ftp.Count(id + ":active") + ", pasivas " + ftp.Count(id + ":passive"));
                Check(sw.Elapsed.TotalSeconds < 40, "Tardó " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s en cambiar de modo");
            }
            finally { ftp.Stop(); }
        }

        static void O1_FtpsActive()
        {
            if (FtpsCert == null) FtpsCert = TestFtpServer.MakeCert();
            TestFtpServer ftp = new TestFtpServer(); ftp.Cert = FtpsCert; ftp.Start();
            try
            {
                using (TrustTestCert())
                {
                    long size = 2L * 1024 * 1024;
                    AppConfig c = Cfg(); c.FtpPassive = false;
                    string id = NewId("ftpsport");
                    DownloadItem it = new DownloadManager(c).Add(ftp.FtpsUrl("normal", "ftpsport.bin", size, id), Dl, null, true);
                    WaitEnd(it, 60);
                    AssertComplete(it, size);
                    Check(ftp.Count(id + ":active") >= 1 && ftp.Count(id + ":tls") == ftp.Count(id + ":retr"),
                          "Activas " + ftp.Count(id + ":active") + ", cifradas " + ftp.Count(id + ":tls") + " de " + ftp.Count(id + ":retr"));
                }
            }
            finally { ftp.Stop(); }
        }

        // ------------------------------------------------------------ #17

        static WebException RealError(string modes)
        {
            try
            {
                HttpWebRequest r = (HttpWebRequest)WebRequest.Create(S.Url(modes, "e.bin", 10, NewId("err")));
                r.Proxy = null;
                using (r.GetResponse()) { }
            }
            catch (WebException ex) { return ex; }
            throw new Exception("La petición no falló");
        }

        // 407 de verdad: un proxy de pruebas que pide usuario y contraseña a todo.
        static WebException ProxyAuthError()
        {
            TcpListener px = RawHttpServer(null, "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"pruebas\"\r\n" +
                                                 "Content-Length: 0\r\nConnection: close\r\n\r\n");
            try
            {
                // Destino que no es 127.0.0.1 (.NET no manda por proxy las direcciones locales); no se llega a él.
                HttpWebRequest r = (HttpWebRequest)WebRequest.Create("http://destino407.invalid/e.bin");
                r.Proxy = new WebProxy("127.0.0.1", ((IPEndPoint)px.LocalEndpoint).Port);
                r.Timeout = 15000;
                try { using (r.GetResponse()) { } }
                catch (WebException ex)
                {
                    Check(HttpCodeOf(ex) == 407, "Se esperaba un 407: " + ex.Status + " " + ex.Message);
                    return ex;
                }
                throw new Exception("La petición no falló");
            }
            finally { px.Stop(); }
        }

        static int HttpCodeOf(WebException ex)
        {
            HttpWebResponse h = ex.Response as HttpWebResponse;
            return h != null ? (int)h.StatusCode : 0;
        }

        static void O1_BrowserProxyUnit()
        {
            WebProxy p = DownloadEngine.ParseBrowserProxy("127.0.0.1:8080");
            Check(p != null && p.Address.Host == "127.0.0.1" && p.Address.Port == 8080, "host:puerto");
            Check(DownloadEngine.ParseBrowserProxy("proxy.ejemplo.com:3128") != null, "nombre:puerto");
            Check(DownloadEngine.ParseBrowserProxy("[::1]:8080") != null, "[ipv6]:puerto");
            foreach (string bad in new string[] { null, "", "proxy", "proxy:", "proxy:0", "proxy:70000", "proxy:80x", "a b:80", "u:p@proxy:80", "proxy:80/ruta", ":80", "socks4://proxy:1080x" })
                Check(DownloadEngine.ParseBrowserProxy(bad) == null, "Aceptó '" + bad + "'");

            Check(DownloadEngine.IsBrowserProxyWorthy(new WebException("x", WebExceptionStatus.ConnectFailure)), "ConnectFailure");
            Check(DownloadEngine.IsBrowserProxyWorthy(new WebException("x", WebExceptionStatus.NameResolutionFailure)), "NameResolutionFailure");
            Check(DownloadEngine.IsBrowserProxyWorthy(new WebException("x", WebExceptionStatus.ProxyNameResolutionFailure)), "ProxyNameResolutionFailure");
            Check(DownloadEngine.IsBrowserProxyWorthy(new WebException("x", WebExceptionStatus.Timeout)), "Timeout");
            Check(!DownloadEngine.IsBrowserProxyWorthy(new WebException("x", WebExceptionStatus.TrustFailure)), "TrustFailure no");
            Check(!DownloadEngine.IsBrowserProxyWorthy(null), "null");
            // Un 403 lo manda el propio servidor (cualquiera puede): no desvía la descarga al proxy del navegador.
            Check(!DownloadEngine.IsBrowserProxyWorthy(RealError("forbidden")), "403 del servidor no");
            Check(DownloadEngine.IsBrowserProxyWorthy(ProxyAuthError()), "407 del proxy de las opciones sí");
            Check(!DownloadEngine.IsBrowserProxyWorthy(RealError("notfound")), "404 no");
            Check(!DownloadEngine.IsBrowserProxyWorthy(RealError("ban")), "429 no");
        }

        static void O1_BrowserProxyFallback()
        {
            AppConfig c = Cfg(); c.ProxyMode = "none";
            DownloadManager m = new DownloadManager(c);
            string id = NewId("bproxy");
            AddOptions o = new AddOptions();
            o.Url = ProxiedUrl("range,slow", "bproxy.bin", 1024 * 1024, id); // descargas.invalid: no se llega directamente
            o.Folder = Dl; o.StartNow = true;
            o.BrowserProxy = "127.0.0.1:" + S.Port;
            DownloadItem it = m.Add(o);
            WaitEnd(it, 60);
            AssertComplete(it, 1024 * 1024);
            Check(S.Count(id + ":proxied") >= 1 && S.Count(id + ":proxied") == S.Count(id + ":req"), "No fue por el proxy del navegador: " + S.Count(id + ":proxied") + " de " + S.Count(id + ":req"));
        }

        static void O1_BrowserProxyNot404()
        {
            AppConfig c = Cfg();
            DownloadManager m = new DownloadManager(c);
            AddOptions o = new AddOptions();
            o.Url = S.Url("notfound", "bp404.bin", 1000, NewId("bp404"));
            o.Folder = Dl; o.StartNow = true;
            o.BrowserProxy = "127.0.0.1:1"; // si lo usara, el error sería del proxy
            Stopwatch sw = Stopwatch.StartNew();
            DownloadItem it = m.Add(o);
            WaitEnd(it, 30);
            Check(it.Status == DownloadStatus.Error && it.Error != null && it.Error.Contains("404"), "Error: " + it.Status + " " + it.Error);
            Check(sw.Elapsed.TotalSeconds < 3, "Tardó " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
        }

        // Un 403 lo puede mandar cualquier servidor: no debe desviar la descarga al proxy del navegador.
        static void O1_BrowserProxyNot403()
        {
            AppConfig c = Cfg();
            DownloadManager m = new DownloadManager(c);
            AddOptions o = new AddOptions();
            string id = NewId("bp403");
            o.Url = S.Url("proxyonly", "bp403.bin", 1000, id); // 403 directo; por el proxy del navegador se bajaría
            o.Folder = Dl; o.StartNow = true;
            o.BrowserProxy = "127.0.0.1:" + S.Port; // TestServer hace también de proxy (y cuenta ":proxied")
            Stopwatch sw = Stopwatch.StartNew();
            DownloadItem it = m.Add(o);
            WaitEnd(it, 30);
            Check(it.Status == DownloadStatus.Error && it.Error != null && it.Error.Contains("403"), "Error: " + it.Status + " " + it.Error);
            Check(S.Count(id + ":direct403") >= 1 && S.Count(id + ":proxied") == 0, "Un 403 del servidor no debe pasar al proxy del navegador (directas " +
                  S.Count(id + ":direct403") + ", por proxy " + S.Count(id + ":proxied") + ")");
            Check(sw.Elapsed.TotalSeconds < 3, "Tardó " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
        }
    }
}
