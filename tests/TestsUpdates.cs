using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using IDMClone.Setup;

namespace IDMClone.Tests
{
    // Actualizaciones (Ayuda > Comprobar actualizaciones... y la comprobación semanal de IDM): manifiesto, GitHub Releases,
    // descarga del instalador con SHA-256 y firma, ventana "Hay una nueva versión..." e instalador en modo /update.
    // Todo contra el servidor local de pruebas (S): nunca se consulta GitHub ni se ejecuta un instalador de verdad.
    public static partial class TestMain
    {
        static void RunUpdates()
        {
            Run("actualizaciones: versiones (v1.3.0, 1.3, -beta) y comparación", TU_Versions);
            Run("actualizaciones: manifiesto propio, respuesta de GitHub Releases y solo HTTPS", TU_Parse);
            Run("actualizaciones: servidor local con versión más nueva, igual y más vieja", TU_FetchNewerOlderSame);
            Run("actualizaciones: descarga el instalador y comprueba el SHA-256", TU_DownloadOk);
            Run("actualizaciones: SHA-256 distinto o ausente -> se descarta", TU_BadHash);
            Run("actualizaciones: redirecciones (local sí; a http de fuera no, y sin llegar a conectar)", TU_Redirects);
            Run("actualizaciones: comprobación automática sin red o con 404, en silencio", TU_AutoSilent);
            Run("actualizaciones: automática con versión nueva -> 'Actualizar ahora' lanza el instalador y cierra", TU_AutoNewerFlow);
            Run("actualizaciones: comprobación semanal (7 días, reintento al día, desactivable, se guarda)", TU_Weekly);
            Run("actualizaciones: Ayuda > Comprobar actualizaciones... (última versión y error a la vista)", TU_Manual);
            Run("actualizaciones: firma Authenticode del mismo editor (certificado de prueba en memoria)", TU_Signature);
            Run("actualizaciones: el instalador /update conserva lo elegido y vuelve a abrir IDMClone", TU_SetupUpdate);
            Run("actualizaciones: textos en inglés", TU_Lang);
        }

        static readonly Version TuCurrent = new Version(1, 2, 0, 0);

        // Configuración de prueba con el manifiesto dado (sin proxy).
        static AppConfig TuCfg(string feed)
        {
            AppConfig c = Cfg();
            c.ProxyMode = "none";
            c.UpdateFeedUrl = feed;
            return c;
        }

        static byte[] FakeInstaller(int size, int seed)
        {
            byte[] b = new byte[size];
            new Random(seed).NextBytes(b);
            b[0] = (byte)'M'; b[1] = (byte)'Z';
            return b;
        }

        static string Sha(byte[] b)
        {
            using (SHA256 h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(b)).Replace("-", "").ToLowerInvariant();
        }

        // Publica en el servidor de pruebas un instalador y su manifiesto; devuelve la URL del manifiesto.
        static string PutRelease(string tag, string version, byte[] installer, string sha256)
        {
            if (installer != null) S.Put("upd/" + tag + "/IDMClone-Setup.exe", installer, "application/octet-stream");
            Dictionary<string, object> m = new Dictionary<string, object>();
            m["version"] = version;
            m["url"] = "IDMClone-Setup.exe";  // relativa al manifiesto
            if (sha256 != null) m["sha256"] = sha256;
            m["notes"] = "Novedades de prueba " + version + "\n- Arreglos";
            return S.Put("upd/" + tag + "/manifest.json", new JavaScriptSerializer().Serialize(m), "application/json");
        }

        static void TU_Versions()
        {
            CheckEq(new Version(1, 3, 0, 0), Updater.ParseVersion("v1.3.0"), "v1.3.0");
            CheckEq(new Version(1, 3, 0, 0), Updater.ParseVersion("1.3"), "1.3");
            CheckEq(new Version(1, 3, 0, 0), Updater.ParseVersion("IDMClone 1.3.0-beta"), "con texto y sufijo");
            CheckEq(new Version(2, 0, 1, 7), Updater.ParseVersion("2.0.1.7"), "cuatro partes");
            Check(Updater.ParseVersion("sin versión") == null && Updater.ParseVersion("") == null && Updater.ParseVersion(null) == null, "Sin versión debería dar null");
            Func<string, UpdateInfo> v = delegate(string s) { UpdateInfo u = new UpdateInfo(); u.Version = Updater.ParseVersion(s); return u; };
            Check(Updater.IsNewer(v("1.3.0"), TuCurrent), "1.3.0 > 1.2.0");
            Check(Updater.IsNewer(v("1.2.0.1"), TuCurrent), "1.2.0.1 > 1.2.0");
            Check(!Updater.IsNewer(v("1.2"), TuCurrent), "1.2 = 1.2.0.0 (no es más nueva)");
            Check(!Updater.IsNewer(v("1.1.9"), TuCurrent), "1.1.9 < 1.2.0");
            Check(!Updater.IsNewer(null, TuCurrent), "null");
            CheckEq("1.3.0", Updater.Display(new Version(1, 3)), "Display");
            CheckEq("1.3.0.2", Updater.Display(new Version(1, 3, 0, 2)), "Display con revisión");
            // Sin sustituir: la del ensamblado (AssemblyInfo.cs), que es la del programa.
            Updater.CurrentVersionOverride = null;
            CheckEq(Updater.Normalize(typeof(Updater).Assembly.GetName().Version), Updater.CurrentVersion, "versión del programa");
            Check(Updater.CurrentVersion.Major >= 1, "Versión del programa: " + Updater.CurrentVersion);
        }

        static void TU_Parse()
        {
            Uri feed = new Uri("https://descargas.ejemplo.com/idmclone/latest.json");
            string sha = new string('a', 64);
            UpdateInfo m = Updater.Parse("{\"version\":\"1.4.0\",\"url\":\"setup/IDMClone-Setup.exe\",\"sha256\":\"" + sha.ToUpperInvariant() + "\",\"notes\":\"Uno\\nDos\"}", feed);
            CheckEq(new Version(1, 4, 0, 0), m.Version, "versión");
            CheckEq("https://descargas.ejemplo.com/idmclone/setup/IDMClone-Setup.exe", m.InstallerUrl, "URL relativa al manifiesto");
            CheckEq(sha, m.Sha256, "SHA-256 en minúsculas");
            CheckEq("Uno\r\nDos", m.Notes, "notas");

            // Respuesta de la API de GitHub (lo que devuelve /releases/latest): digest calculado por GitHub.
            string sha2 = new string('b', 64);
            string gh = "{\"tag_name\":\"v1.5.0\",\"name\":\"IDMClone 1.5.0\",\"html_url\":\"https://github.com/AndoniXXR/IDMClone/releases/tag/v1.5.0\",\"draft\":false,\"prerelease\":false," +
                        "\"body\":\"## Novedades\\r\\n- Actualizaciones automáticas\",\"assets\":[" +
                        "{\"name\":\"IDMClone-Setup.exe.sha256\",\"browser_download_url\":\"https://github.com/AndoniXXR/IDMClone/releases/download/v1.5.0/IDMClone-Setup.exe.sha256\"}," +
                        "{\"name\":\"otro.zip\",\"browser_download_url\":\"https://github.com/x/otro.zip\"}," +
                        "{\"name\":\"IDMClone-Setup.exe\",\"size\":123,\"digest\":\"sha256:" + sha2 + "\",\"browser_download_url\":\"https://github.com/AndoniXXR/IDMClone/releases/download/v1.5.0/IDMClone-Setup.exe\"}]}";
            UpdateInfo g = Updater.Parse(gh, new Uri(Updater.DefaultFeedUrl));
            CheckEq(new Version(1, 5, 0, 0), g.Version, "tag_name");
            CheckEq("https://github.com/AndoniXXR/IDMClone/releases/download/v1.5.0/IDMClone-Setup.exe", g.InstallerUrl, "asset del instalador");
            CheckEq(sha2, g.Sha256, "digest del asset");
            Check(g.Notes.Contains("Actualizaciones automáticas"), "notas (body)");
            CheckEq("https://github.com/AndoniXXR/IDMClone/releases/download/v1.5.0/IDMClone-Setup.exe.sha256", g.Sha256Url, "asset .sha256");
            // Sin digest: el SHA-256 escrito en las notas.
            UpdateInfo g2 = Updater.Parse("{\"tag_name\":\"1.6\",\"body\":\"Cambios\\nSHA-256: " + sha2.ToUpperInvariant() + "\",\"assets\":[{\"name\":\"IDMClone-Setup-1.6.exe\",\"browser_download_url\":\"https://github.com/a/IDMClone-Setup-1.6.exe\"}]}", null);
            CheckEq(sha2, g2.Sha256, "SHA-256 de las notas");
            CheckEq("https://github.com/a/IDMClone-Setup-1.6.exe", g2.InstallerUrl, "instalador con la versión en el nombre");
            UpdateInfo g3 = Updater.Parse("{\"tag_name\":\"v1.7\",\"assets\":[{\"name\":\"IDMClone-portable.exe\",\"browser_download_url\":\"https://github.com/a/IDMClone-portable.exe\"}]}", null);
            Check(g3.InstallerUrl == null, "Se tomó como instalador otro .exe: " + g3.InstallerUrl);

            foreach (string bad in new string[] { "no es json", "[1,2]", "{\"notes\":\"sin versión\"}", "" })
            {
                bool threw = false;
                try { Updater.Parse(bad, feed); } catch (UpdateException) { threw = true; }
                Check(threw, "Respuesta inválida aceptada: " + bad);
            }
            Check(Updater.IsAllowed(new Uri("https://x.com/a.exe")), "https");
            Check(Updater.IsAllowed(new Uri("http://127.0.0.1:8080/a.exe")) && Updater.IsAllowed(new Uri("http://localhost/a")), "http local (pruebas)");
            Check(!Updater.IsAllowed(new Uri("http://x.com/a.exe")) && !Updater.IsAllowed(new Uri("ftp://x.com/a.exe")), "http/ftp de fuera");
            CheckEq(Updater.DefaultFeedUrl, Updater.FeedUrl(AppConfig.CreateDefault()), "URL por defecto (GitHub Releases)");
            CheckEq("https://api.github.com/repos/AndoniXXR/IDMClone-public/releases/latest", Updater.DefaultFeedUrl, "Repositorio público");
        }

        static void TU_FetchNewerOlderSame()
        {
            Updater.CurrentVersionOverride = TuCurrent;
            try
            {
                string tag = NewId("ver");
                byte[] inst = FakeInstaller(2048, 1);
                UpdateInfo newer = Updater.Fetch(TuCfg(PutRelease(tag + "n", "1.3.0", inst, Sha(inst))));
                Check(Updater.IsNewer(newer) && newer.InstallerUrl == S.Base + "/s/upd/" + tag + "n/IDMClone-Setup.exe", "Más nueva: " + newer.Version + " " + newer.InstallerUrl);
                Check(newer.Notes.StartsWith("Novedades de prueba 1.3.0"), "Notas: " + newer.Notes);
                UpdateInfo same = Updater.Fetch(TuCfg(PutRelease(tag + "s", "1.2.0", inst, Sha(inst))));
                Check(!Updater.IsNewer(same), "Misma versión ofrecida como nueva");
                UpdateInfo older = Updater.Fetch(TuCfg(PutRelease(tag + "o", "v1.1.5", inst, Sha(inst))));
                Check(!Updater.IsNewer(older), "Versión vieja ofrecida como nueva");
                // Formato GitHub servido localmente, sin digest: el SHA-256 sale del asset .sha256.
                string b = S.Base + "/s/upd/" + tag + "g/";
                S.Put("upd/" + tag + "g/IDMClone-Setup.exe", inst, "application/octet-stream");
                S.Put("upd/" + tag + "g/IDMClone-Setup.exe.sha256", Sha(inst).ToUpperInvariant() + "  IDMClone-Setup.exe\n", "text/plain");
                string gh = "{\"tag_name\":\"v2.0.0\",\"body\":\"Grande\",\"assets\":[{\"name\":\"IDMClone-Setup.exe\",\"browser_download_url\":\"" + b + "IDMClone-Setup.exe\"}," +
                            "{\"name\":\"IDMClone-Setup.exe.sha256\",\"browser_download_url\":\"" + b + "IDMClone-Setup.exe.sha256\"}]}";
                UpdateInfo g = Updater.Fetch(TuCfg(S.Put("upd/" + tag + "g/latest", gh, "application/json")));
                Check(Updater.IsNewer(g), "GitHub 2.0.0");
                CheckEq(Sha(inst), g.Sha256, "SHA-256 del archivo .sha256");
            }
            finally { Updater.CurrentVersionOverride = null; }
        }

        static string TuFolder()
        {
            string d = Path.Combine(Home, "upd-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(d);
            return d;
        }

        static string UnsignedExe { get { return typeof(TestMain).Assembly.Location; } }

        static void TU_DownloadOk()
        {
            Updater.CurrentVersionOverride = TuCurrent;
            try
            {
                byte[] inst = FakeInstaller(300 * 1024 + 17, 2);
                AppConfig c = TuCfg(PutRelease(NewId("ok"), "1.3.0", inst, Sha(inst)));
                UpdateInfo u = Updater.Fetch(c);
                string folder = TuFolder();
                long lastDone = 0, lastTotal = 0;
                string path = Updater.Download(c, u, folder, UnsignedExe, delegate(long d, long t) { lastDone = d; lastTotal = t; }, null);
                CheckEq(Path.Combine(folder, "IDMClone-Setup-1.3.0.exe"), path, "ruta del instalador");
                CheckEq(Sha(inst), FileSha(path).ToLowerInvariant(), "contenido");
                Check(lastDone == inst.Length && lastTotal == inst.Length, "Progreso: " + lastDone + "/" + lastTotal);
                CheckEq(1, Directory.GetFiles(folder).Length, "Archivos en la carpeta (sin .part)");
                // Cancelar a mitad no deja nada.
                string folder2 = TuFolder();
                bool threw = false;
                try { Updater.Download(c, u, folder2, UnsignedExe, null, delegate { return true; }); }
                catch (UpdateException ex) { threw = ex.Message.Contains("cancelada"); }
                Check(threw, "Cancelar no detuvo la descarga");
                CheckEq(0, Directory.GetFiles(folder2).Length, "Restos tras cancelar");
            }
            finally { Updater.CurrentVersionOverride = null; }
        }

        static string DownloadError(AppConfig c, UpdateInfo u, string folder)
        {
            try { Updater.Download(c, u, folder, UnsignedExe, null, null); return null; }
            catch (UpdateException ex) { return ex.Message; }
        }

        static void TU_BadHash()
        {
            Updater.CurrentVersionOverride = TuCurrent;
            try
            {
                byte[] inst = FakeInstaller(64 * 1024, 3);
                string tag = NewId("bad");
                AppConfig c = TuCfg(PutRelease(tag, "1.3.0", inst, Sha(FakeInstaller(64 * 1024, 4))));
                UpdateInfo u = Updater.Fetch(c);
                string folder = TuFolder();
                string err = DownloadError(c, u, folder);
                Check(err != null && err.Contains("SHA-256 distinto"), "SHA-256 equivocado aceptado: " + err);
                CheckEq(0, Directory.GetFiles(folder).Length, "Quedó el instalador malo en la carpeta");

                // Sin SHA-256 publicado: ni se descarga.
                string tag2 = NewId("nosha");
                AppConfig c2 = TuCfg(PutRelease(tag2, "1.3.0", inst, null));
                UpdateInfo u2 = Updater.Fetch(c2);
                Check(u2.Sha256 == null, "SHA-256 inventado");
                err = DownloadError(c2, u2, folder);
                Check(err != null && err.Contains("SHA-256"), "Sin SHA-256 se instaló igual: " + err);
                CheckEq(0, S.StaticHits("upd/" + tag2 + "/IDMClone-Setup.exe"), "Se descargó el instalador sin poder comprobarlo");

                // Instalador por http de fuera: rechazado antes de conectar.
                UpdateInfo u3 = new UpdateInfo(); u3.Version = new Version(1, 3, 0, 0); u3.Sha256 = Sha(inst); u3.InstallerUrl = "http://descargas.ejemplo.invalid/IDMClone-Setup.exe";
                err = DownloadError(c, u3, folder);
                Check(err != null && err.Contains("HTTPS"), "http aceptado: " + err);
                // El servidor corta o falla: error legible, sin restos.
                S.FailStatic("upd/" + tag + "/IDMClone-Setup.exe", 500);
                err = DownloadError(c, u, folder);
                Check(err != null && err.Contains("500"), "Error 500: " + err);
                CheckEq(0, Directory.GetFiles(folder).Length, "Restos tras el error");
            }
            finally { Updater.CurrentVersionOverride = null; }
        }

        static void TU_Redirects()
        {
            byte[] inst = FakeInstaller(4096, 7);
            string real = PutRelease(NewId("redir"), "1.3.0", inst, Sha(inst));
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            List<string> seen = new List<string>();
            // Servidor que solo redirige: /afuera* a un http de fuera; lo demás, al manifiesto de verdad.
            Thread th = new Thread(delegate()
            {
                while (true)
                {
                    TcpClient cl;
                    try { cl = l.AcceptTcpClient(); } catch { return; }
                    try
                    {
                        using (cl)
                        {
                            NetworkStream ns = cl.GetStream();
                            StreamReader rd = new StreamReader(ns, Encoding.ASCII);
                            string first = rd.ReadLine() ?? "", line;
                            while (!string.IsNullOrEmpty(line = rd.ReadLine())) { }
                            lock (seen) seen.Add(first);
                            string[] p = first.Split(' ');
                            string loc = p.Length > 1 && p[1].StartsWith("/afuera") ? "http://descargas.ejemplo.invalid/latest.json" : real;
                            byte[] resp = Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: " + loc + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                            ns.Write(resp, 0, resp.Length);
                        }
                    }
                    catch { }
                }
            });
            th.IsBackground = true;
            th.Start();
            Updater.CurrentVersionOverride = TuCurrent;
            try
            {
                UpdateInfo u = Updater.Fetch(TuCfg("http://127.0.0.1:" + port + "/dentro.json"));
                Check(Updater.IsNewer(u) && u.Sha256 == Sha(inst), "La redirección local no se siguió");
                // Con un proxy que apunta a este mismo servidor se vería si la petición en claro llega a salir.
                AppConfig c = TuCfg("http://127.0.0.1:" + port + "/afuera.json");
                c.ProxyMode = "manual"; c.ProxyHost = "127.0.0.1"; c.ProxyPort = port;
                lock (seen) seen.Clear();
                string err = null;
                try { Updater.Fetch(c); } catch (UpdateException ex) { err = ex.Message; }
                Check(err != null && err.Contains("HTTPS"), "Redirección a http de fuera aceptada: " + err);
                lock (seen) Check(seen.Count == 1 && !string.Join(" ", seen.ToArray()).Contains("ejemplo.invalid"), "Salió una petición en claro: " + string.Join(" | ", seen.ToArray()));
            }
            finally { Updater.CurrentVersionOverride = null; l.Stop(); }
        }
        static void TU_AutoSilent()
        {
            MainForm f = NewMainForm();
            Func<UpdateForm, DialogResult> old = MainForm.UpdateFormHook;
            int shown = 0;
            try
            {
                MainForm.UpdateFormHook = delegate(UpdateForm d) { shown++; return DialogResult.Cancel; };
                AppConfig cfg = Field<AppConfig>(f, "_cfg");
                cfg.AutoCheckUpdates = true;
                foreach (string feed in new string[] { "http://127.0.0.1:" + FreePort() + "/nada.json", S.Base + "/s/upd/no-existe.json", S.Put("upd/" + NewId("roto") + ".json", "<html>no</html>", "text/html") })
                {
                    cfg.UpdateFeedUrl = feed; cfg.LastUpdateCheck = null; cfg.LastUpdateFailure = null;
                    f.AutoCheckForUpdates();
                    Check(PumpUntil(delegate { return !f.UpdateCheckRunning; }, 30), "La comprobación no terminó: " + feed);
                    CheckEq(0, shown, "Se mostró algo con " + feed);
                    CheckEq(0, CountOpenForms(typeof(UpdateForm)), "Ventanas abiertas");
                    Check(cfg.LastUpdateFailure != null && cfg.LastUpdateCheck == null, "No se anotó el fallo: " + feed);
                    DateTime now = DateTime.UtcNow;
                    Check(!Updater.IsCheckDue(cfg, now.AddHours(1)), "Tras un fallo se reintentó enseguida");
                    Check(Updater.IsCheckDue(cfg, now.AddHours(25)), "Tras un fallo no se reintenta al día siguiente");
                }
                Check(AppConfig.Load().LastUpdateFailure != null, "El fallo no se guardó en config.json");
            }
            finally { MainForm.UpdateFormHook = old; DisposeMainForm(f); }
        }

        static void TU_AutoNewerFlow()
        {
            MainForm f = NewMainForm();
            Func<UpdateForm, DialogResult> old = MainForm.UpdateFormHook;
            int shown = 0; ProcessStartInfo launched = null; bool exited = false;
            string title = null, notes = null;
            Updater.CurrentVersionOverride = TuCurrent;
            Updater.DownloadFolderOverride = TuFolder();
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            Updater.InstalledDirHook = delegate { return exeDir + "\\"; };
            Updater.LaunchHook = delegate(ProcessStartInfo p) { launched = p; return true; };
            try
            {
                byte[] inst = FakeInstaller(200 * 1024, 5);
                AppConfig cfg = Field<AppConfig>(f, "_cfg");
                cfg.AutoCheckUpdates = true;
                cfg.UpdateFeedUrl = PutRelease(NewId("nueva"), "9.0.0", inst, Sha(inst));
                f.ExitHook = delegate { exited = true; };
                MainForm.UpdateFormHook = delegate(UpdateForm d)
                {
                    shown++;
                    IntPtr h = d.Handle;
                    title = Field<Label>(d, "_title").Text; notes = Field<TextBox>(d, "_notes").Text;
                    Check(d.HasUpdate, "La ventana no ofrece la versión nueva");
                    d.BeginDownload();
                    PumpUntil(delegate { return !d.Busy; }, 30);
                    Check(d.Error == null, "Error al descargar: " + d.Error);
                    return d.DialogResult;
                };
                f.AutoCheckForUpdates();
                PumpUntil(delegate { return shown > 0 && launched != null; }, 30);
                CheckEq(1, shown, "Veces que salió 'Hay una nueva versión'");
                Check(title != null && title.Contains("Hay una nueva versión") && title.Contains("9.0.0"), "Título: " + title);
                Check(notes != null && notes.Contains("Novedades de prueba 9.0.0"), "Notas: " + notes);
                Check(launched != null, "No se lanzó el instalador");
                CheckEq(Path.Combine(Updater.DownloadFolder, "IDMClone-Setup-9.0.0.exe"), launched.FileName, "instalador lanzado");
                CheckEq(Sha(inst), FileSha(launched.FileName).ToLowerInvariant(), "instalador comprobado");
                CheckEq("/update \"/D=" + exeDir.TrimEnd('\\') + "\"", launched.Arguments, "modo actualización en la carpeta instalada");
                Check(exited, "IDMClone no se cerró para dejar actualizar");
                Check(cfg.LastUpdateCheck != null && cfg.LastUpdateFailure == null, "No se anotó la comprobación");

                // Una copia que no es la instalada (portátil/desarrollo): el asistente normal, sin /update.
                Updater.InstalledDirHook = delegate { return Path.Combine(Home, "otra-carpeta"); };
                CheckEq("", Updater.InstallerStart(launched.FileName, Application.ExecutablePath).Arguments ?? "", "copia no instalada");
                Updater.InstalledDirHook = delegate { return null; };
                CheckEq("", Updater.InstallerStart(launched.FileName, Application.ExecutablePath).Arguments ?? "", "sin instalación registrada");
            }
            finally
            {
                MainForm.UpdateFormHook = old; Updater.CurrentVersionOverride = null; Updater.DownloadFolderOverride = null;
                Updater.InstalledDirHook = null; Updater.LaunchHook = null;
                DisposeMainForm(f);
            }
        }

        static void TU_Weekly()
        {
            UseFreshHome("upd-sem-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            AppConfig c = AppConfig.CreateDefault();
            Check(c.AutoCheckUpdates, "Por defecto debe comprobar cada semana (como IDM)");
            Check(AppConfig.NewSerializer().Deserialize<AppConfig>("{\"MaxConnections\":4}").AutoCheckUpdates, "Una configuración antigua debe tenerla activada");
            DateTime t0 = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
            Check(Updater.IsCheckDue(c, t0), "Nunca comprobada: toca");
            Updater.RecordCheck(c, t0, true);
            Check(!Updater.IsCheckDue(c, t0.AddMinutes(1)), "Recién comprobada");
            Check(!Updater.IsCheckDue(c, t0.AddDays(7).AddMinutes(-1)), "A los 6 días y pico");
            Check(Updater.IsCheckDue(c, t0.AddDays(7)), "A los 7 días");
            // Falla a los 7 días: no se insiste hasta el día siguiente.
            Updater.RecordCheck(c, t0.AddDays(7), false);
            Check(!Updater.IsCheckDue(c, t0.AddDays(7).AddHours(12)), "Reintento antes de un día");
            Check(Updater.IsCheckDue(c, t0.AddDays(8).AddMinutes(1)), "Reintento al día siguiente");
            Updater.RecordCheck(c, t0.AddDays(8), true);
            Check(c.LastUpdateFailure == null && !Updater.IsCheckDue(c, t0.AddDays(9)), "Tras una correcta, otra semana");
            // El reloj se atrasó (la última comprobación está "en el futuro"): se comprueba.
            Check(Updater.IsCheckDue(c, t0), "Fecha futura");
            c.AutoCheckUpdates = false;
            Check(!Updater.IsCheckDue(c, t0.AddDays(60)), "Desactivada");
            c.Save();
            AppConfig back = AppConfig.Load();
            Check(!back.AutoCheckUpdates && back.LastUpdateCheck == c.LastUpdateCheck, "No se guardó: " + back.LastUpdateCheck);
            // La ventana principal mira cada hora si toca (la primera vez, a los 2 minutos).
            MainForm f = NewMainForm();
            try
            {
                System.Windows.Forms.Timer t = Field<System.Windows.Forms.Timer>(f, "_updTimer");
                Check(t != null && t.Enabled && t.Interval == 2 * 60 * 1000, "Temporizador de actualizaciones");
            }
            finally { DisposeMainForm(f); }
        }

        static void TU_Manual()
        {
            MainForm f = NewMainForm();
            Func<UpdateForm, DialogResult> old = MainForm.UpdateFormHook;
            Updater.CurrentVersionOverride = TuCurrent;
            List<string> seen = new List<string>();
            try
            {
                AppConfig cfg = Field<AppConfig>(f, "_cfg");
                byte[] inst = FakeInstaller(1024, 6);
                cfg.UpdateFeedUrl = PutRelease(NewId("igual"), "1.2.0", inst, Sha(inst));
                MainForm.UpdateFormHook = delegate(UpdateForm d)
                {
                    IntPtr h = d.Handle;
                    Check(d.Info == null, "Manual: la ventana debe comprobar ella misma");
                    d.BeginCheck();
                    Check(Field<Label>(d, "_title").Text.StartsWith("Comprobando"), "Mientras comprueba: " + Field<Label>(d, "_title").Text);
                    PumpUntil(delegate { return !d.Busy; }, 30);
                    seen.Add(Field<Label>(d, "_title").Text + "|" + Field<Button>(d, "_later").Text + "|" + Field<TextBox>(d, "_notes").Text);
                    // La casilla de la comprobación semanal está en esta ventana y se guarda al momento.
                    CheckBox auto = Field<CheckBox>(d, "_auto");
                    Check(auto.Text == "Comprobar actualizaciones automáticamente cada semana", auto.Text);
                    bool was = auto.Checked; auto.Checked = !was;
                    Check(AppConfig.Load().AutoCheckUpdates == !was, "La casilla no se guardó");
                    auto.Checked = was;
                    return DialogResult.Cancel;
                };
                // Por el menú, como el usuario.
                MenuStrip menu = Field<MenuStrip>(f, "_menu");
                ToolStripMenuItem help = (ToolStripMenuItem)menu.Items[menu.Items.Count - 1];
                ToolStripMenuItem item = null;
                foreach (ToolStripItem i in help.DropDownItems) if (i.Text == "Comprobar actualizaciones...") item = (ToolStripMenuItem)i;
                Check(item != null, "Falta Ayuda > Comprobar actualizaciones...");
                item.PerformClick();
                CheckEq(1, seen.Count, "ventanas");
                Check(seen[0].StartsWith("Tienes la última versión de IDMClone.|Cerrar|Novedades de prueba 1.2.0"), "Misma versión: " + seen[0]);
                Check(cfg.LastUpdateCheck != null, "La comprobación manual también cuenta para la semanal");
                // Si falla, la manual sí lo dice (la automática no).
                cfg.UpdateFeedUrl = S.Base + "/s/upd/" + NewId("falta") + ".json";
                f.CheckForUpdates();
                CheckEq(2, seen.Count, "ventanas");
                Check(seen[1].StartsWith("No se pudo comprobar si hay actualizaciones.|Cerrar|") && seen[1].Contains("404"), "Error: " + seen[1]);
            }
            finally { MainForm.UpdateFormHook = old; Updater.CurrentVersionOverride = null; DisposeMainForm(f); }
        }

        // ------------------------------------------------------------------ firma

        // Certificado de firma de código autofirmado, solo en memoria (no se importa en ningún almacén), exportado a .pfx.
        static string TestPfx(string path, string subject, string password)
        {
            using (RSA rsa = RSA.Create())
            {
                rsa.KeySize = 2048;
                CertificateRequest req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                OidCollection eku = new OidCollection(); eku.Add(new Oid("1.3.6.1.5.5.7.3.3")); // firma de código
                req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, false));
                req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
                using (X509Certificate2 c = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1)))
                    File.WriteAllBytes(path, c.Export(X509ContentType.Pfx, password));
            }
            return path;
        }

        // Firma con Set-AuthenticodeSignature (PowerShell de Windows) cargando el .pfx en memoria.
        static void SignWith(string pfx, string password, params string[] files)
        {
            StringBuilder sc = new StringBuilder();
            sc.Append("$ErrorActionPreference='Stop';");
            sc.Append("$c=New-Object System.Security.Cryptography.X509Certificates.X509Certificate2('" + pfx.Replace("'", "''") + "','" + password + "');");
            foreach (string f in files)
                sc.Append("$r=Set-AuthenticodeSignature -FilePath '" + f.Replace("'", "''") + "' -Certificate $c -HashAlgorithm SHA256; if(-not $r.SignerCertificate){throw 'sin firma'};");
            sc.Append("$c.Dispose()");
            ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + sc.ToString().Replace("\"", "\\\"") + "\"");
            psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardError = true; psi.RedirectStandardOutput = true;
            string err;
            int code;
            try
            {
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    err = p.StandardError.ReadToEnd();
                    p.WaitForExit(60000);
                    code = p.ExitCode;
                }
            }
            catch (Exception ex) { throw new SkipException("No se pudo ejecutar PowerShell para firmar: " + ex.Message); }
            if (code != 0) throw new SkipException("PowerShell no pudo firmar: " + err.Trim());
        }

        static void TU_Signature()
        {
            string d = TuFolder();
            string src = ExePath != null && File.Exists(ExePath) ? ExePath : UnsignedExe;
            string pfxA = TestPfx(Path.Combine(d, "a.pfx"), "CN=IDMClone Pruebas", "prueba");
            string pfxB = TestPfx(Path.Combine(d, "b.pfx"), "CN=IDMClone Pruebas", "prueba"); // mismo nombre, otro certificado
            string cur = Path.Combine(d, "IDMClone.exe"), instA = Path.Combine(d, "setup-a.exe"), instB = Path.Combine(d, "setup-b.exe"),
                   instNone = Path.Combine(d, "setup-sin.exe"), instBad = Path.Combine(d, "setup-tocado.exe");
            foreach (string f in new string[] { cur, instA, instB, instNone, instBad }) File.Copy(src, f);
            SignWith(pfxA, "prueba", cur, instA, instBad);
            SignWith(pfxB, "prueba", instB);
            // Tocar un byte de la firma ya hecha (en la cabecera DOS, que entra en el hash).
            byte[] bytes = File.ReadAllBytes(instBad); bytes[0x40] ^= 0xFF; File.WriteAllBytes(instBad, bytes);

            X509Certificate2 signer = Updater.SignerOf(instA);
            Check(signer != null && signer.Subject == "CN=IDMClone Pruebas", "Firmante: " + (signer != null ? signer.Subject : "ninguno"));
            Check(Updater.SignerOf(instNone) == null, "Un archivo sin firmar tiene firmante");
            uint vA = (uint)Updater.VerifyTrust(instA), vNone = (uint)Updater.VerifyTrust(instNone), vBad = (uint)Updater.VerifyTrust(instBad);
            Check(vA == 0x800B0109, "Certificado de prueba: se esperaba raíz no reconocida (0x800B0109) y es 0x" + vA.ToString("X8"));
            Check(vNone == 0x800B0100, "Sin firma: 0x" + vNone.ToString("X8"));
            Check(vBad != 0 && vBad != 0x800B0109, "Archivo tocado tras firmar: 0x" + vBad.ToString("X8"));

            Check(Updater.CheckSignature(instA, instNone) == null && Updater.CheckSignature(instBad, instNone) == null, "IDMClone sin firmar: basta el SHA-256");
            CheckEq<string>(null, Updater.CheckSignature(instA, cur), "mismo certificado");
            string e1 = Updater.CheckSignature(instNone, cur), e2 = Updater.CheckSignature(instB, cur), e3 = Updater.CheckSignature(instBad, cur);
            Check(e1 != null && e1.Contains("no tiene firma"), "Instalador sin firma aceptado: " + e1);
            Check(e2 != null && e2.Contains("no es de confianza"), "Otro certificado con el mismo nombre aceptado: " + e2);
            Check(e3 != null && e3.Contains("no es válida"), "Instalador modificado aceptado: " + e3);

            // Y en la descarga completa: este IDMClone firmado no acepta un instalador sin firmar aunque el SHA-256 cuadre.
            // (Las copias firmadas son de la versión del programa: la "instalada" se finge más vieja.)
            Version fileVer = Updater.Normalize(new Version(FileVersionInfo.GetVersionInfo(instA).FileVersion));
            Updater.CurrentVersionOverride = new Version(fileVer.Major, fileVer.Minor > 0 ? fileVer.Minor - 1 : 0, 0, 0);
            if (Updater.CurrentVersionOverride.CompareTo(fileVer) >= 0) Updater.CurrentVersionOverride = new Version(0, 1, 0, 0);
            try
            {
                byte[] inst = File.ReadAllBytes(instNone);
                AppConfig c = TuCfg(PutRelease(NewId("firma"), "1.3.0", inst, Sha(inst)));
                UpdateInfo u = Updater.Fetch(c);
                string folder = TuFolder();
                string err = null;
                try { Updater.Download(c, u, folder, cur, null, null); } catch (UpdateException ex) { err = ex.Message; }
                Check(err != null && err.Contains("no tiene firma"), "Descarga sin firma aceptada: " + err);
                CheckEq(0, Directory.GetFiles(folder).Length, "Quedó el instalador sin firma");
                byte[] signedA = File.ReadAllBytes(instA);
                c = TuCfg(PutRelease(NewId("firma-ok"), "1.3.0", signedA, Sha(signedA)));
                string ok = Updater.Download(c, Updater.Fetch(c), folder, cur, null, null);
                Check(File.Exists(ok), "El instalador firmado por el mismo editor no se aceptó");
                // Vuelta atrás: el mismo instalador firmado, pero la versión instalada ya es la suya (o más nueva).
                Updater.CurrentVersionOverride = fileVer;
                c = TuCfg(PutRelease(NewId("firma-vieja"), "99.0.0", signedA, Sha(signedA)));
                err = null;
                try { Updater.Download(c, Updater.Fetch(c), TuFolder(), cur, null, null); } catch (UpdateException ex) { err = ex.Message; }
                Check(err != null && err.Contains("más nueva"), "Instalador firmado de una versión no más nueva aceptado: " + err);
            }
            finally { Updater.CurrentVersionOverride = null; }
        }

        // ------------------------------------------------------------------ instalador /update

        static void TU_SetupUpdate()
        {
            string id = Guid.NewGuid().ToString("N").Substring(0, 8);
            string root = Path.Combine(Home, "upd-setup-" + id);
            Directory.CreateDirectory(root);
            Installer.RegRoot = @"Software\IDMCloneTests\" + id;
            Installer.StartMenuDirOverride = Path.Combine(root, @"menu\IDMClone");
            Installer.DesktopDirOverride = Path.Combine(root, "escritorio");
            Directory.CreateDirectory(Installer.DesktopDirOverride);
            Installer.PayloadOverride = MakePayload(Path.Combine(root, "payload.zip"));
            Installer.SelfPathOverride = Path.Combine(root, "IDMClone-Setup.exe");
            File.Copy(Installer.PayloadOverride, Installer.SelfPathOverride);
            Installer.NoSelfDelete = true;
            List<string> restarted = new List<string>();
            SetupProgram.RestartHook = delegate(string exe) { restarted.Add(exe); };
            try
            {
                string desk = Path.Combine(Installer.DesktopDirOverride, "IDMClone.lnk");
                // 1) Instalado sin acceso directo en el escritorio ni inicio con Windows: la actualización los respeta.
                string dir = Path.Combine(root, "programa");
                InstallOptions o = new InstallOptions(); o.Dir = dir; o.DesktopShortcut = false; o.AutoStart = false;
                Installer.Install(o, null);
                Check(!File.Exists(desk), "Escritorio (instalación)");
                InstallOptions u = new InstallOptions(); u.Dir = dir;
                CheckEq(0, SetupProgram.RunUpdate(u, true), "código de salida");
                Check(!File.Exists(desk), "La actualización volvió a poner el acceso directo del escritorio");
                Check(RegValue(Installer.RunKey, Startup.ValueName) == null, "La actualización volvió a poner el inicio con Windows");
                CheckEq(1, restarted.Count, "IDMClone vuelto a abrir");
                CheckEq(Path.Combine(dir, Installer.ExeName), restarted[0], "exe vuelto a abrir");
                Check(File.Exists(Path.Combine(dir, Installer.ExeName)) && File.Exists(Path.Combine(dir, Installer.UninstallerName)), "Archivos tras actualizar");

                // 2) Con todo puesto (lo de siempre) y sin /D: la carpeta registrada, y lo conserva.
                Installer.Install(new InstallOptions { Dir = dir }, null);
                Check(File.Exists(desk) && RegValue(Installer.RunKey, Startup.ValueName) != null, "Instalación con todo");
                CheckEq(0, SetupProgram.RunUpdate(new InstallOptions(), true), "código de salida (sin /D)");
                Check(File.Exists(desk) && RegValue(Installer.RunKey, Startup.ValueName) != null, "La actualización quitó lo elegido");
                CheckEq(2, restarted.Count, "IDMClone vuelto a abrir (2)");
                CheckEq(Path.GetFullPath(dir), Path.GetFullPath(Path.GetDirectoryName(restarted[1])), "Carpeta registrada");
                CheckEq(Installer.Version, (string)RegValue(Installer.UninstallKey, "DisplayVersion"), "versión registrada");

                // 3) Si la instalación falla: código 1, pero se vuelve a abrir el IDMClone que haya (el anterior), que ya
                //    se había cerrado para dejar actualizar.
                Installer.PayloadOverride = Path.Combine(root, "no-existe.zip");
                CheckEq(1, SetupProgram.RunUpdate(new InstallOptions { Dir = dir }, true), "código de salida con error");
                CheckEq(3, restarted.Count, "No se volvió a abrir el IDMClone anterior tras el error");
                // 4) Sin nada instalado y con error: no se abre nada.
                CheckEq(1, SetupProgram.RunUpdate(new InstallOptions { Dir = Path.Combine(root, "vacia") }, true), "código (vacía)");
                CheckEq(3, restarted.Count, "Se abrió algo sin IDMClone");
            }
            finally
            {
                SetupProgram.RestartHook = null;
                try { Registry.CurrentUser.DeleteSubKeyTree(Installer.RegRoot, false); } catch { }
                try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\IDMCloneTests")) if (k != null && k.SubKeyCount == 0) Registry.CurrentUser.DeleteSubKey(@"Software\IDMCloneTests", false); } catch { }
                Installer.RegRoot = ""; Installer.StartMenuDirOverride = null; Installer.DesktopDirOverride = null;
                Installer.PayloadOverride = null; Installer.SelfPathOverride = null;
            }
        }

        static void TU_Lang()
        {
            List<string> texts = new List<string>();
            UpdateInfo nueva = new UpdateInfo(); nueva.Version = new Version(9, 0, 0, 0); nueva.Notes = "x";
            UpdateInfo igual = new UpdateInfo(); igual.Version = Updater.CurrentVersion;
            List<Form> forms = new List<Form>();
            AppConfig c = Cfg();
            forms.Add(new UpdateForm(c, nueva)); forms.Add(new UpdateForm(c, igual)); forms.Add(new UpdateProgressForm(new InstallOptions()));
            foreach (Form f in forms) { IntPtr h = f.Handle; Lang.Collect(f, texts); f.Dispose(); }
            texts.Add("Comprobar actualizaciones...");
            texts.Add("Comprobando si hay una versión nueva...");
            texts.Add("No se pudo comprobar si hay actualizaciones.");
            texts.Add("No se pudo actualizar: El instalador descargado no coincide con el publicado (SHA-256 distinto): se ha descartado.");
            texts.Add("No se pudo conectar con el servidor de actualizaciones: Tiempo de espera agotado");
            texts.Add("No hay ninguna versión publicada en el servidor de actualizaciones (404).");
            texts.Add("El servidor de actualizaciones respondió con un error (503).");
            texts.Add("La firma digital del instalador no es válida (0x80096010): se ha descartado.");
            texts.Add("No se pudo actualizar IDMClone: x");
            Lang.LoadFile(EnglishFile());
            try
            {
                List<string> missing = new List<string>();
                foreach (string t in texts)
                    if (!Ignorable(t) && Lang.T(t) == t && !missing.Contains(t)) missing.Add(t);
                Check(missing.Count == 0, missing.Count + " textos sin traducir: " + string.Join(" | ", missing.ToArray()));
                CheckEq("A new version of IDMClone is available: 9.0.0", Lang.T("Hay una nueva versión de IDMClone: 9.0.0"), "título");
            }
            finally { Lang.Reset(); }
        }
    }
}
