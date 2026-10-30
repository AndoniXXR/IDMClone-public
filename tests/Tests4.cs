using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace IDMClone.Tests
{
    // Tests del bloque 4c: motor al estilo IDM (primera conexión, carpeta temporal, límites por servidor,
    // cuotas, proxy, inicios de sesión y cookies del servidor).
    public static partial class TestMain
    {
        static void RunPhase4c()
        {
            Run("4c motor: la primera conexión ya descarga (sin sondeo) y las demás parten por la mitad", T4_FirstConnection);
            Run("4c motor: servidor sin rangos -> una sola petición en total", T4_FirstConnectionNoRange);
            Run("4c temporal: el archivo no aparece en el destino hasta terminar", T4_TempFolder);
            Run("4c temporal: reanudar tras reiniciar la app usa el temporal guardado", T4_TempResumeAfterRestart);
            Run("4c temporal: si el destino se ocupa mientras descarga, no se pisa", T4_TempDestinationTaken);
            Run("4c temporal: desactivada -> se escribe directamente en el destino", T4_NoTempFolder);
            Run("4c temporal: en otra unidad (se copia y se renombra al terminar)", T4_TempOtherDrive);
            Run("4c conexión: excepción por servidor limita las conexiones", T4_ServerLimit);
            Run("4c conexión: cuota de descarga (N MB cada periodo)", T4_Quota);
            Run("4c proxy: manual", T4_ProxyManual);
            Run("4c proxy: script PAC", T4_ProxyPac);
            Run("4c inicios de sesión: usuario y contraseña por sitio (401 Basic)", T4_SiteLogin);
            Run("4c inicios de sesión: NTLMv2 (respuesta de Windows verificada) con varias conexiones", T4_Ntlm);
            Run("4c inicios de sesión: NTLM con contraseña incorrecta -> error 401 al momento", T4_NtlmWrong);
            Run("4c inicios de sesión: Digest MD5 y SHA-256 con varias conexiones", T4_Digest);
            Run("4c inicios de sesión: servidor NTLM de Windows (http.sys) rechaza credenciales falsas", T4_WindowsNtlm);
            Run("4c cookies: las que fija el servidor durante la descarga se usan en las demás conexiones", T4_ServerSetsCookie);
            Run("4c cookies: no se envían a otro dominio tras una redirección", T4_CookiesNoLeakOnRedirect);
        }

        static void T4_FirstConnection()
        {
            long size = 4 * 1024 * 1024;
            DownloadItem it = Download(NewMgr(), "range,slow", size, "first", 60);
            AssertComplete(it, size);
            string id = IdOf(it);
            Check(S.Count(id + ":probe") == 0, "Hubo " + S.Count(id + ":probe") + " peticiones de sondeo (0-0)");
            Check(S.Count(id + ":first") == 1, "Primeras conexiones (bytes=0-): " + S.Count(id + ":first"));
            Check(S.Count(id + ":ranged") >= 3, "Pocas conexiones por rangos: " + S.Count(id + ":ranged"));
            Check(S.Count(id + ":maxconc") >= 4, "Conexiones simultáneas: " + S.Count(id + ":maxconc"));
        }

        static void T4_FirstConnectionNoRange()
        {
            DownloadItem it = Download(NewMgr(), "norange", 1024 * 1024, "first-nr", 60);
            AssertComplete(it, 1024 * 1024);
            Check(S.Count(IdOf(it) + ":req") == 1, "Peticiones: " + S.Count(IdOf(it) + ":req") + " (antes eran 2: sondeo + descarga)");
        }

        static void T4_TempFolder()
        {
            AppConfig c = Cfg(); c.UseTempFolder = true; c.TempFolder = Path.Combine(Home, "temporal");
            DownloadManager m = new DownloadManager(c);
            long size = 2 * 1024 * 1024;
            DownloadItem it = m.Add(S.Url("range,veryslow", "temp.bin", size, NewId("temp")), Dl, null, true);
            WaitProgress(it, 300 * 1024, 30);
            Check(!File.Exists(it.FilePath), "El archivo ya está en el destino a medio descargar: " + it.FilePath);
            Check(!string.IsNullOrEmpty(it.TempPath) && it.TempPath.StartsWith(c.TempFolder, StringComparison.OrdinalIgnoreCase) && File.Exists(it.TempPath),
                  "No se escribe en la carpeta temporal: " + it.TempPath);
            string temp = it.TempPath;
            WaitEnd(it, 60);
            AssertComplete(it, size);
            Check(!File.Exists(temp), "El temporal sigue ahí");
            Check(it.TempPath == null, "TempPath no se limpió");
            Check(!Directory.Exists(Path.GetDirectoryName(temp)), "Quedó la carpeta temporal de la descarga");
        }

        static void T4_TempOtherDrive()
        {
            string home = Path.GetPathRoot(Home);
            string other = null;
            foreach (DriveInfo d in DriveInfo.GetDrives())
                if (d.DriveType == DriveType.Fixed && d.IsReady && !string.Equals(d.RootDirectory.FullName, home, StringComparison.OrdinalIgnoreCase))
                { other = d.RootDirectory.FullName; break; }
            if (other == null) throw new SkipException("No hay otra unidad fija en este equipo");
            string tempDir = Path.Combine(other, "IDMClone-tests-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            try
            {
                AppConfig c = Cfg(); c.UseTempFolder = true; c.TempFolder = tempDir;
                DownloadManager m = new DownloadManager(c);
                long size = 2 * 1024 * 1024;
                DownloadItem it = m.Add(S.Url("range,slow", "otra-unidad.bin", size, NewId("drive")), Dl, null, true);
                WaitProgress(it, 200 * 1024, 30);
                Check(it.TempPath != null && it.TempPath.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase) && File.Exists(it.TempPath), "No usa la otra unidad: " + it.TempPath);
                WaitEnd(it, 60);
                AssertComplete(it, size);
                Check(Path.GetPathRoot(it.FilePath).Equals(home, StringComparison.OrdinalIgnoreCase), "Destino inesperado: " + it.FilePath);
                Check(Directory.GetFiles(Path.GetDirectoryName(it.FilePath), "*.idmclone-mover").Length == 0, "Quedó el archivo provisional de la copia");
                Check(Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories).Length == 0, "Quedaron archivos en el temporal de la otra unidad");
            }
            finally { try { Directory.Delete(tempDir, true); } catch { } }
        }

        static void T4_TempResumeAfterRestart()
        {
            UseFreshHome("temp-restart");
            long size = 2 * 1024 * 1024;
            DownloadManager m1 = NewMgr();
            DownloadItem it = m1.Add(S.Url("range,veryslow", "temp2.bin", size, NewId("temp2")), Dl, null, true);
            WaitProgress(it, 300 * 1024, 30);
            m1.StopItem(it); WaitEnd(it, 10);
            long before = it.Downloaded;
            DownloadManager m2 = NewMgr();
            m2.LoadList();
            DownloadItem it2 = m2.Items.Find(delegate(DownloadItem x) { return x.Id == it.Id; });
            Check(it2 != null && it2.TempPath == it.TempPath, "No se recuperó la ruta temporal: " + (it2 == null ? "(sin item)" : it2.TempPath));
            Check(it2.Downloaded == before, "Se perdió lo descargado: " + it2.Downloaded + " de " + before);
            m2.StartItem(it2);
            WaitEnd(it2, 60);
            AssertComplete(it2, size);
            Check(S.Count(IdOf(it2) + ":first") == 1, "Al reanudar se volvió a empezar desde 0");
        }

        static void T4_TempDestinationTaken()
        {
            DownloadManager m = NewMgr();
            long size = 1024 * 1024;
            DownloadItem it = m.Add(S.Url("range,veryslow", "ocupado.bin", size, NewId("occ")), Dl, null, true);
            WaitProgress(it, 100 * 1024, 30);
            string wanted = it.FilePath;
            File.WriteAllText(wanted, "archivo del usuario");
            WaitEnd(it, 60);
            AssertComplete(it, size);
            Check(!string.Equals(it.FilePath, wanted, StringComparison.OrdinalIgnoreCase), "Se usó la ruta ocupada");
            Check(File.ReadAllText(wanted) == "archivo del usuario", "Se pisó el archivo del usuario");
        }

        static void T4_NoTempFolder()
        {
            AppConfig c = Cfg(); c.UseTempFolder = false;
            DownloadManager m = new DownloadManager(c);
            DownloadItem it = m.Add(S.Url("range,veryslow", "directo.bin", 1024 * 1024, NewId("dir")), Dl, null, true);
            WaitProgress(it, 100 * 1024, 30);
            Check(it.TempPath == null && File.Exists(it.FilePath), "Sin carpeta temporal debería escribir en el destino");
            WaitEnd(it, 60);
            AssertComplete(it, 1024 * 1024);
        }

        static void T4_ServerLimit()
        {
            AppConfig c = Cfg();
            c.ServerLimits.Add(new ServerLimit { Host = "127.0.0.1", MaxConnections = 2 });
            DownloadItem it = Download(new DownloadManager(c), "range,slow", 3 * 1024 * 1024, "slimit", 60);
            AssertComplete(it, 3 * 1024 * 1024);
            Check(S.Count(IdOf(it) + ":maxconc") <= 2, "Conexiones simultáneas: " + S.Count(IdOf(it) + ":maxconc") + " (límite 2)");
            Check(c.ConnectionsFor("descargas.ejemplo.com") == 8 && c.ConnectionsFor("127.0.0.1") == 2, "ConnectionsFor");
            AppConfig d = Cfg(); d.ServerLimits.Add(new ServerLimit { Host = "ejemplo.com", MaxConnections = 1 });
            Check(d.ConnectionsFor("cdn.ejemplo.com") == 1 && d.ConnectionsFor("otroejemplo.com") == 8, "Los subdominios cuentan; los parecidos no");
        }

        static void T4_Quota()
        {
            AppConfig c = Cfg(); c.QuotaEnabled = true; c.QuotaMB = 1;
            DownloadQuota.Reset();
            DownloadQuota.WindowSecondsOverride = 2;
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                DownloadItem it = Download(new DownloadManager(c), "range", 3 * 1024 * 1024, "quota", 60);
                double s = sw.Elapsed.TotalSeconds;
                AssertComplete(it, 3 * 1024 * 1024);
                // 3 MB con 1 MB por periodo de 2 s: al menos 2 esperas (~4 s).
                Check(s >= 3.5, string.Format("Demasiado rápido ({0:0.0} s): la cuota no se aplica", s));
            }
            finally { DownloadQuota.WindowSecondsOverride = 0; DownloadQuota.Reset(); }
        }

        // URL de un servidor ficticio que solo se alcanza a través del proxy (el servidor de pruebas).
        static string ProxiedUrl(string modes, string name, long size, string id)
        {
            return string.Format("http://descargas.invalid:{0}/f/{1}/{2}?size={3}&id={4}", S.Port, modes, name, size, id);
        }

        static void T4_ProxyManual()
        {
            AppConfig c = Cfg(); c.ProxyMode = "manual"; c.ProxyHost = "127.0.0.1"; c.ProxyPort = S.Port;
            string id = NewId("proxy");
            DownloadManager m = new DownloadManager(c);
            DownloadItem it = m.Add(ProxiedUrl("range", "proxy.bin", 1024 * 1024, id), Dl, null, true);
            WaitEnd(it, 60);
            AssertComplete(it, 1024 * 1024);
            Check(S.Count(id + ":proxied") >= 1, "No pasó por el proxy");
        }

        static void T4_ProxyPac()
        {
            AppConfig c = Cfg(); c.ProxyMode = "pac"; c.PacUrl = S.Base + "/proxy.pac";
            string id = NewId("pac");
            DownloadManager m = new DownloadManager(c);
            DownloadItem it = m.Add(ProxiedUrl("range", "pac.bin", 512 * 1024, id), Dl, null, true);
            WaitEnd(it, 60);
            AssertComplete(it, 512 * 1024);
            Check(S.Count(id + ":proxied") >= 1, "No pasó por el proxy del script PAC");
            Check(Pac.ProxyForUrl(c.PacUrl, "http://otro.sitio/") == null, "Para otros sitios el PAC dice DIRECT");
        }

        static void T4_SiteLogin()
        {
            Stopwatch sw = Stopwatch.StartNew();
            DownloadItem bad = Download(NewMgr(), "range,basicauth", 100 * 1024, "noauth", 30);
            Check(bad.Status == DownloadStatus.Error && bad.Error.Contains("401") && sw.Elapsed.TotalSeconds < 3, "Sin credenciales: " + bad.Status + " " + bad.Error);

            AppConfig c = Cfg();
            SiteLogin l = new SiteLogin(); l.Site = "127.0.0.1/f/*"; l.User = "usuario"; l.Password = "clave";
            c.SiteLogins.Add(l);
            Check(!c.SiteLogins[0].PasswordEnc.Contains("clave"), "La contraseña se guarda en claro");
            DownloadItem ok = Download(new DownloadManager(c), "range,slow,basicauth", 2 * 1024 * 1024, "auth4", 60);
            AssertComplete(ok, 2 * 1024 * 1024);
            Check(S.Count(IdOf(ok) + ":ranged") >= 2, "Con inicio de sesión no usó varias conexiones");
        }

        static AuthServer _auth;
        static AuthServer Auth
        {
            get { if (_auth == null) { _auth = new AuthServer(); _auth.Start(); } return _auth; }
        }

        static AppConfig LoginCfg(string password)
        {
            AppConfig c = Cfg();
            SiteLogin l = new SiteLogin(); l.Site = "127.0.0.1"; l.User = AuthServer.User; l.Password = password;
            c.SiteLogins.Add(l);
            return c;
        }

        static void T4_Ntlm()
        {
            Check(BitConverter.ToString(Ntlm.Md4(System.Text.Encoding.ASCII.GetBytes("abc"))).Replace("-", "") == "A448017AAF21D8525FC10AE87AA6729D", "MD4 mal implementado");
            long size = 3 * 1024 * 1024;
            string id = NewId("ntlm");
            DownloadItem it = new DownloadManager(LoginCfg(AuthServer.Password)).Add(Auth.Url("ntlm", "ntlm.bin", size, id), Dl, null, true);
            WaitEnd(it, 60);
            AssertComplete(it, size);
            Check(Auth.Count(id + ":ntlm-fail") == 0, "Respuestas NTLM incorrectas: " + Auth.Count(id + ":ntlm-fail"));
            Check(Auth.Count(id + ":ntlm-ok") >= 2 && Auth.Count(id + ":ranged") >= 1, "Conexiones autenticadas: " + Auth.Count(id + ":ntlm-ok"));
        }

        static void T4_NtlmWrong()
        {
            string id = NewId("ntlmbad");
            Stopwatch sw = Stopwatch.StartNew();
            DownloadItem it = new DownloadManager(LoginCfg("otra")).Add(Auth.Url("ntlm", "ntlmbad.bin", 100 * 1024, id), Dl, null, true);
            WaitEnd(it, 30);
            Check(it.Status == DownloadStatus.Error && it.Error.Contains("401") && sw.Elapsed.TotalSeconds < 5, it.Status + " " + it.Error + " " + sw.Elapsed.TotalSeconds);
            Check(Auth.Count(id + ":ntlm-fail") >= 1, "El servidor no recibió la respuesta NTLM");
        }

        static void T4_Digest()
        {
            foreach (string algo in new string[] { "MD5", "SHA-256" })
            {
                long size = 3 * 1024 * 1024;
                string id = NewId("dg" + algo);
                DownloadItem it = new DownloadManager(LoginCfg(AuthServer.Password)).Add(Auth.Url("digest/" + algo, "digest.bin", size, id), Dl, null, true);
                WaitEnd(it, 60);
                AssertComplete(it, size);
                Check(Auth.Count(id + ":digest-fail") == 0, algo + ": respuestas Digest incorrectas: " + Auth.Count(id + ":digest-fail"));
                Check(Auth.Count(id + ":digest-ok") >= 2, algo + ": conexiones autenticadas: " + Auth.Count(id + ":digest-ok"));
            }
        }

        // NTLM de verdad de Windows: http.sys valida contra las cuentas del equipo. Sin la contraseña real
        // del usuario solo se puede probar que el intercambio se hace y que el rechazo da un error claro.
        static void T4_WindowsNtlm()
        {
            System.Net.HttpListener l = new System.Net.HttpListener();
            int port = FreePort();
            l.Prefixes.Add("http://localhost:" + port + "/win/");
            l.AuthenticationSchemes = System.Net.AuthenticationSchemes.Ntlm;
            try { l.Start(); }
            catch (Exception ex) { throw new SkipException("No se puede abrir http.sys: " + ex.Message); }
            int served = 0;
            // HttpListener hace el intercambio NTLM dentro de GetContext: solo llegan aquí las peticiones autenticadas.
            Thread loop = new Thread(delegate()
            {
                while (l.IsListening)
                {
                    try
                    {
                        System.Net.HttpListenerContext ctx = l.GetContext();
                        Interlocked.Increment(ref served);
                        byte[] body = new byte[1000];
                        ctx.Response.ContentLength64 = body.Length;
                        ctx.Response.OutputStream.Write(body, 0, body.Length);
                        ctx.Response.Close();
                    }
                    catch { }
                }
            });
            loop.IsBackground = true;
            loop.Start();
            try
            {
                AppConfig c = Cfg();
                SiteLogin s = new SiteLogin(); s.Site = "localhost"; s.User = "idmclone-no-existe"; s.Password = "x";
                c.SiteLogins.Add(s);
                Stopwatch sw = Stopwatch.StartNew();
                DownloadItem it = new DownloadManager(c).Add("http://localhost:" + port + "/win/archivo.bin", Dl, null, true);
                WaitEnd(it, 30);
                Check(it.Status == DownloadStatus.Error && it.Error.Contains("401") && sw.Elapsed.TotalSeconds < 10, it.Status + " " + it.Error);
                Check(served == 0, "Windows aceptó un usuario que no existe");
            }
            finally { l.Close(); }
        }

        static int FreePort()
        {
            System.Net.Sockets.TcpListener t = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            t.Start();
            int p = ((System.Net.IPEndPoint)t.LocalEndpoint).Port;
            t.Stop();
            return p;
        }

        static void T4_ServerSetsCookie()
        {
            DownloadItem it = Download(NewMgr(), "range,slow,setcookie", 2 * 1024 * 1024, "setck", 60);
            AssertComplete(it, 2 * 1024 * 1024);
            Check(S.Count(IdOf(it) + ":nocookie") == 0, "Conexiones sin la cookie del servidor: " + S.Count(IdOf(it) + ":nocookie"));
            Check(S.Count(IdOf(it) + ":ranged") >= 2, "No usó varias conexiones");
        }

        static void T4_CookiesNoLeakOnRedirect()
        {
            string id = NewId("leak");
            string url = string.Format("{0}/r/range/leak.bin?size={1}&id={2}", S.Base, 512 * 1024, id);
            DownloadManager m = NewMgr();
            DownloadItem it = m.Add(url, Dl, null, true, "sess=abc", "http://127.0.0.1/", null);
            WaitEnd(it, 60);
            AssertComplete(it, 512 * 1024);
            Check(S.Count(id + ":leak") == 0, "La cookie de 127.0.0.1 se envió a localhost " + S.Count(id + ":leak") + " veces");
        }
    }
}
