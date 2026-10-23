using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace IDMClone.Tests
{
    // Tests de la fase 3: integración con los navegadores, host nativo, tubería y línea de comandos.
    // (La lógica de la extensión se prueba con Node: browser\tests\extension.test.js.)
    public static partial class TestMain
    {
        static void RunPhase3()
        {
            Run("f3 cli: parámetros como IDMan.exe (/d /p /f /a /n /q /h /s)", T3_CliParse);
            Run("f3 cli: ruta \"C:\\x\\\" (comilla escapada por Windows) y valores ausentes", T3_CliQuirks);
            Run("f3 cli: /s se aplica después de añadir y /n no muestra la ventana", T3_ApplyCliOrder);
            Run("f3 host: detecta Chrome (chrome-extension://) y Firefox (manifiesto .json)", T3_DetectHost);
            Run("f3 host: ping sin arrancar IDMClone y devolviendo el rid", T3_HostPingNoLaunch);
            Run("f3 tubería: nombre por usuario y sesión, permisos solo para el usuario", T3_PipeSecurity);
            Run("f3 navegador privado: la descarga no queda en la lista guardada", T3_IncognitoTransient);
            Run("f3 e2e: host con IDMClone cerrado -> lo abre oculto y responde con rid", T3_E2E_HostColdStartHidden);
            Run("f3 e2e: /n silencioso y /q sale al terminar", T3_E2E_SilentAndQuit);
        }

        static void T3_CliParse()
        {
            CliArgs a = CliArgs.Parse(new string[] { "/d", "https://x.com/a.zip", "/p", @"C:\Descargas", "/f", "b.zip", "/a", "/n", "/q", "/h", "/s" });
            Check(a.Url == "https://x.com/a.zip" && a.Path == @"C:\Descargas" && a.FileName == "b.zip", "Valores: " + a.Url + " | " + a.Path + " | " + a.FileName);
            Check(a.AddOnly && a.Silent && a.QuitWhenDone && a.HangUp && a.StartQueue && !a.Help, "Indicadores mal leídos");
            Check(!a.Console, "/n ya no es el modo consola (en IDM es silencioso)");
            Check(CliArgs.Parse(new string[] { "/console", "/d", "http://x/y" }).Console, "/console");
            Check(CliArgs.Parse(new string[] { "/?" }).Help && !CliArgs.Parse(new string[] { "/h" }).Help, "/h es colgar (IDM), no ayuda");
            // Reenvío a la instancia abierta: misma información y /s al final (después de añadir).
            string cmd = a.ToCommandLine();
            Check(cmd.EndsWith("/s"), "La orden reenviada no termina con /s: " + cmd);
            CliArgs b = CliArgs.Parse(cmd);
            Check(b.Url == a.Url && b.Path == a.Path && b.FileName == a.FileName && b.Silent && b.QuitWhenDone && b.AddOnly && b.StartQueue,
                  "La orden reenviada pierde datos: " + cmd);
            Check(CliArgs.Parse(new string[] { "/onboot" }).OnlyStartsHidden, "/onboot solo");
        }

        static void T3_CliQuirks()
        {
            // Lo que llega a Main con:  /p "C:\x\" /f a.zip   (CommandLineToArgvW convierte \" en ")
            CliArgs a = CliArgs.Parse(new string[] { "/p", "C:\\x\"", "/f", "a.zip" });
            Check(a.Path == @"C:\x", "Ruta: «" + a.Path + "»");
            Check(a.FileName == "a.zip", "El nombre se perdió: " + a.FileName);
            CliArgs b = CliArgs.Parse(new string[] { "/d", "/n" });
            Check(b.Url == null && b.Silent, "/d sin valor se comió el /n");
            CliArgs c = CliArgs.Parse("/d \"https://x.com/a b.zip\" /p \"C:\\Mis descargas\\\" /n");
            Check(c.Url == "https://x.com/a b.zip" && c.Path == @"C:\Mis descargas\" && c.Silent, "Orden por tubería: " + c.Url + " | " + c.Path);
        }

        static void T3_ApplyCliOrder()
        {
            MainForm f = NewMainForm();
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                m.Schedule.MaxActive = 1;
                string url = S.Url("range,slow", "cliq.bin", 400 * 1024, NewId("cliq"));
                string r = f.ApplyCli(CliArgs.Parse("/d \"" + url + "\" /p \"" + Dl + "\" /a /n /s"));
                Check(r.Contains("\"ok\":true"), "Respuesta: " + r);
                Check(m.QueueRunning, "/s no inició la cola");
                DownloadItem it = m.Snapshot().Find(delegate(DownloadItem x) { return x.Url == url; });
                Check(it != null, "/d no añadió la descarga");
                Check(WaitUntil(delegate { return it.Status == DownloadStatus.Complete; }, 30), "La cola no la descargó (estado " + it.Status + ")");
                Check(!f.Visible, "/n mostró la ventana");
            }
            finally { DisposeMainForm(f); }
        }

        static void T3_DetectHost()
        {
            Check(Program.IsNativeHost(new string[] { "chrome-extension://jolfcgooaockjhnalagfilpljkloallc/", "--parent-window=0" }), "Chrome");
            string manifest = Path.Combine(Home, "com.idmclone.host.firefox.json");
            File.WriteAllText(manifest, "{}");
            Check(Program.IsNativeHost(new string[] { manifest, "idmclone@idmclone.local" }), "Firefox");
            Check(!Program.IsNativeHost(new string[] { "/d", "https://x.com/a.json" }), "Una URL .json no es el host");
            Check(!Program.IsNativeHost(new string[] { @"C:\no\existe.json" }), "Un .json que no existe no es el host");
        }

        static int CountGuis()
        {
            int n = 0;
            foreach (Process p in Process.GetProcessesByName("IDMClone"))
                try { if (string.Equals(p.MainModule.FileName, Path.GetFullPath(ExePath ?? ""), StringComparison.OrdinalIgnoreCase)) n++; } catch { }
            return n;
        }

        static void T3_HostPingNoLaunch()
        {
            UseFreshHome("ping");
            int before = CountGuis();
            string r = NativeHost.HandleMessage("{\"type\":\"ping\",\"rid\":7}");
            Check(r.StartsWith("{\"rid\":7,"), "No devolvió el rid: " + r);
            Check(r.Contains("\"ok\":true") && r.Contains("\"gui\":false"), "Respuesta: " + r);
            Thread.Sleep(500);
            Check(CountGuis() == before, "El ping arrancó IDMClone");
        }

        static void T3_PipeSecurity()
        {
            string name = Ipc.PipeName;
            Check(name.Contains("_" + Process.GetCurrentProcess().SessionId + "_"), "El nombre no lleva la sesión: " + name);
            Check(Ipc.MutexName.StartsWith(@"Local\"), "El mutex no es por sesión: " + Ipc.MutexName);
            string pipe = "IDMClone_Test_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            // Mismo constructor que usa la aplicación (vía StartServer), y lectura de la DACL desde un cliente.
            Ipc.Server srv = Ipc.StartServer(delegate(string x) { return "{\"ok\":true}"; }, pipe);
            try
            {
                Thread.Sleep(150);
                using (NamedPipeClientStream c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut))
                {
                    c.Connect(2000);
                    Check(Ipc.ServerIsTrusted(c), "El servidor propio no se reconoce como de confianza");
                    PipeSecurity sec = c.GetAccessControl();
                    AuthorizationRuleCollection rules = sec.GetAccessRules(true, true, typeof(SecurityIdentifier));
                    SecurityIdentifier me = WindowsIdentity.GetCurrent().User;
                    foreach (PipeAccessRule rule in rules)
                        Check(rule.IdentityReference.Equals(me), "La tubería da acceso a otra cuenta: " + rule.IdentityReference);
                    Check(rules.Count >= 1, "La tubería no tiene reglas de acceso");
                }
            }
            finally { srv.Stop(); }
        }

        static void T3_IncognitoTransient()
        {
            MainForm f = NewMainForm();
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                string url = S.Url("range", "privado.bin", 50 * 1024, NewId("inc"));
                string r = f.HandleCommand("{\"type\":\"download\",\"url\":" + Ipc.JsonQuote(url) + ",\"filename\":\"privado.bin\",\"cookies\":\"sesion=secreta\",\"incognito\":true}");
                Check(r.Contains("\"ok\":true"), "Respuesta: " + r);
                DownloadItem it = m.Snapshot().Find(delegate(DownloadItem x) { return x.Url == url; });
                Check(it != null && it.Transient, "No se marcó como privada");
                WaitEnd(it, 30);
                AssertComplete(it, 50 * 1024);
                m.SaveList();
                string list = File.Exists(AppConfig.DownloadsPath) ? File.ReadAllText(AppConfig.DownloadsPath) : "";
                Check(list.IndexOf("privado.bin", StringComparison.Ordinal) < 0, "La descarga privada quedó en downloads.json");
            }
            finally { DisposeMainForm(f); }
        }

        static Process StartHost(string home)
        {
            ProcessStartInfo psi = new ProcessStartInfo(ExePath, "chrome-extension://jolfcgooaockjhnalagfilpljkloallc/");
            psi.UseShellExecute = false;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.CreateNoWindow = true;
            psi.EnvironmentVariables["IDMCLONE_HOME"] = home;
            System.Text.Encoding savedIn = null;
            try { savedIn = Console.InputEncoding; Console.InputEncoding = new System.Text.UTF8Encoding(false); } catch { }
            try { return Process.Start(psi); }
            finally { if (savedIn != null) { try { Console.InputEncoding = savedIn; } catch { } } }
        }

        static Process FindGui(DateTime after)
        {
            foreach (Process p in Process.GetProcessesByName("IDMClone"))
                try
                {
                    if (string.Equals(p.MainModule.FileName, Path.GetFullPath(ExePath), StringComparison.OrdinalIgnoreCase) && p.StartTime >= after)
                        return p;
                }
                catch { }
            return null;
        }

        static void T3_E2E_HostColdStartHidden()
        {
            RequireExe();
            UseFreshHome("e2e-host3");
            Cfg().Save();
            DateTime t0 = DateTime.Now.AddSeconds(-1);
            Process host = StartHost(AppConfig.Dir);
            try
            {
                Stream stdin = host.StandardInput.BaseStream, stdout = host.StandardOutput.BaseStream;
                string url = S.Url("range", "frio.bin", 300 * 1024, NewId("frio"));
                byte[] f = Frame("{\"rid\":41,\"type\":\"download\",\"url\":" + Ipc.JsonQuote(url) + ",\"filename\":\"frio.bin\"}");
                stdin.Write(f, 0, f.Length); stdin.Flush();
                // Mientras arranca, un ping no debe quedarse bloqueado detrás de la descarga.
                byte[] p = Frame("{\"rid\":42,\"type\":\"ping\"}");
                stdin.Write(p, 0, p.Length); stdin.Flush();
                string r1 = ReadFrame(stdout, 40000), r2 = ReadFrame(stdout, 40000);
                Check(r1 != null && r2 != null, "Faltan respuestas: " + r1 + " / " + r2);
                string dl = r1.Contains("\"rid\":41") ? r1 : r2, ping = r1.Contains("\"rid\":42") ? r1 : r2;
                Check(dl.Contains("\"rid\":41") && dl.Contains("\"ok\":true"), "Respuesta de la descarga: " + dl);
                Check(ping.Contains("\"rid\":42"), "Respuesta del ping: " + ping);
                Process gui = null;
                Check(WaitUntil(delegate { gui = FindGui(t0); return gui != null; }, 10), "No se arrancó IDMClone");
                gui.Refresh();
                Check(gui.MainWindowHandle == IntPtr.Zero, "IDMClone se abrió con la ventana visible (debe ir a la bandeja)");
                stdin.Close();
                Check(host.WaitForExit(15000), "El host no terminó al cerrar el navegador el canal");
            }
            finally
            {
                try { if (!host.HasExited) host.Kill(); } catch { }
                KillTestGuis();
            }
        }

        static void T3_E2E_SilentAndQuit()
        {
            RequireExe();
            UseFreshHome("e2e-silent");
            Cfg().Save();
            DateTime t0 = DateTime.Now.AddSeconds(-1);
            ProcessStartInfo psi = new ProcessStartInfo(ExePath, "/tray");
            psi.UseShellExecute = false;
            psi.EnvironmentVariables["IDMCLONE_HOME"] = AppConfig.Dir;
            Process gui = Process.Start(psi);
            try
            {
                Check(WaitUntil(delegate { string x = Ipc.Request("{\"type\":\"ping\"}", 300, 2000); return x != null && x.Contains("\"gui\":true"); }, 20), "La GUI no arrancó");
                // /n: añadir sin mostrar nada.
                string u1 = S.Url("range", "silencio.bin", 100 * 1024, NewId("sil"));
                Process c1 = RunCli("/n /d \"" + u1 + "\" /p \"" + Dl + "\"");
                Check(c1.ExitCode == 0, "Código de salida " + c1.ExitCode);
                Thread.Sleep(500);
                gui.Refresh();
                Check(gui.MainWindowHandle == IntPtr.Zero, "/n mostró la ventana");
                Check(WaitUntil(delegate { return File.Exists(Path.Combine(Dl, "silencio.bin")) && new FileInfo(Path.Combine(Dl, "silencio.bin")).Length == 100 * 1024 && File.ReadAllText(AppConfig.DownloadsPath).Contains("silencio.bin"); }, 20),
                      "La descarga silenciosa no se añadió/completó");
                // /q: salir al terminar.
                string u2 = S.Url("range,slow", "salir.bin", 300 * 1024, NewId("q"));
                Process c2 = RunCli("/n /q /d \"" + u2 + "\" /p \"" + Dl + "\"");
                Check(c2.ExitCode == 0, "Código de salida " + c2.ExitCode);
                Check(gui.WaitForExit(30000), "Con /q IDMClone no se cerró al terminar");
                Check(FileSha(Path.Combine(Dl, "salir.bin")) == TestServer.Sha256Of(300 * 1024), "El archivo de /q quedó mal");
            }
            finally { try { if (!gui.HasExited) gui.Kill(); } catch { } }
        }

        static Process RunCli(string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo(ExePath, args);
            psi.UseShellExecute = false;
            psi.EnvironmentVariables["IDMCLONE_HOME"] = AppConfig.Dir;
            Process p = Process.Start(psi);
            p.WaitForExit(20000);
            return p;
        }
    }
}
