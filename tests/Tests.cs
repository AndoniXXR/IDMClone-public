using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace IDMClone.Tests
{
    // Banco de pruebas de IDMClone: usa el código real del programa contra TestServer.
    // Uso: IDMCloneTests.exe --exe <ruta a IDMClone.exe> [filtro]
    public static partial class TestMain
    {
        static TestServer S;
        static string Home, Dl, ExePath, Filter;
        static int _pass, _fail, _skip;
        static readonly List<string> _failures = new List<string>();
        static readonly DateTime _started = DateTime.Now;

        [STAThread]
        public static int Main(string[] args)
        {
            // Las pruebas NUNCA apagan el equipo ni abren programas: el apagado y "Abrir el siguiente
            // archivo" se sustituyen por versiones simuladas antes de crear ninguna ventana.
            MainForm.ShutdownAction = delegate { Console.WriteLine("  (apagado del equipo simulado: no se apaga)"); };
            FinishOptions.Hook = delegate(string mode, bool force) { Console.WriteLine("  (" + mode + (force ? ", forzado" : "") + " simulado: el equipo no se toca)"); };
            MainForm.RunFileHook = delegate(string p) { };
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--exe" && i + 1 < args.Length) ExePath = args[++i];
                else if (args[i] == "--serve") return Serve(args);
                else if (args[i] == "--real") return RunReal(args);
                else if (args[i] == "--screens") return RunScreens(args);
                else if (args[i] == "--media") return RunMediaReal(args);
                else if (args[i] == "--grabber") return RunGrabberReal(args);
                else if (args[i] == "--fakegui" && i + 1 < args.Length) return FakeGui(args[i + 1]);
                else Filter = args[i];
            }

            Home = Path.Combine(Path.GetTempPath(), "idmclone-tests-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Dl = Path.Combine(Home, "dl");
            Directory.CreateDirectory(Dl);
            AppConfig.HomeOverride = Home;
            Environment.SetEnvironmentVariable("IDMCLONE_HOME", Home);
            Cfg().Save(); // config.json de la carpeta de pruebas (la usa también la GUI en los e2e)

            S = new TestServer();
            S.Start();
            Console.WriteLine("Servidor de pruebas: " + S.Base);
            Console.WriteLine("Datos de prueba:     " + Home);
            Console.WriteLine();

            // --- Fase 0/1: motor ---
            Run("motor: descarga normal multi-conexión", T_Normal);
            Run("motor: archivo de 5 bytes (#12)", T_Tiny);
            Run("motor: archivo vacío", T_Empty);
            Run("motor: servidor sin rangos", T_NoRange);
            Run("motor: sin rangos + corte -> reintento desde 0 (#2)", T_NoRangeCut);
            Run("motor: con rangos + cortes -> reintento", T_RangeCut);
            Run("motor: tamaño desconocido (sin Content-Length)", T_NoLength);
            Run("motor: servidor deja de respetar Range (200) (#1)", T_IgnoreRange);
            Run("motor: 206 con inicio desplazado (#1)", T_Shift);
            Run("motor: 206 sin Content-Range (#1)", T_NoContentRange);
            Run("motor: enlace caducado devuelve HTML (#1)", T_HtmlExpired);
            Run("motor: límite de velocidad no se congela (#3)", T_SpeedLimit);
            Run("motor: límite de velocidad es global (#3)", T_SpeedLimitGlobal);
            Run("motor: pausar y reanudar", T_PauseResume);
            Run("motor: pausar y reanudar al instante", T_PauseResumeImmediate);
            Run("motor: reanudar sin archivo parcial -> empieza de 0 (#4)", T_ResumeMissingFile);
            Run("motor: reanudar con archivo parcial truncado (#4)", T_ResumeTruncatedFile);

            // --- Gestor ---
            Run("gestor: StartItem simultáneo -> un solo motor (#9)", T_DoubleStart);
            Run("gestor: misma URL dos veces -> rutas distintas (#8)", T_SameUrlTwice);
            Run("gestor: cookies/referer/UA persisten y se usan al reanudar (#7)", T_PersistAuth);
            Run("gestor: eliminar durante la descarga borra archivo y estado", T_RemoveWhileDownloading);
            Run("gestor: modo consola no toca downloads.json (#5)", T_ConsoleNoPersist);

            // --- Guardado seguro ---
            Run("guardado: lista dañada -> se recupera de .bak (#6)", T_RecoverFromBak);
            Run("guardado: lista y .bak dañados -> se aparta, no se pierde (#6)", T_CorruptPreserved);
            Run("guardado: SaveList concurrente no corrompe (#6)", T_ConcurrentSave);
            Run("guardado: config dañada no se sobrescribe y se normaliza (#6)", T_ConfigCorrupt);

            // --- IPC / GUI ---
            Run("ipc: petición con respuesta (#10)", T_IpcRequestReply);
            Run("gui: diálogo eliminar no borra del disco por defecto (#11)", T_DeleteDialog);

            // --- Extremo a extremo con IDMClone.exe ---
            Run("e2e: IDMClone.exe /console no borra la lista (#5)", T_E2E_ConsoleMode);
            Run("e2e: host nativo arranca la GUI y confirma la descarga (#10)", T_E2E_NativeHost);

            RunPhase2();
            RunPhase3();
            RunPhase4c();
            RunPhase4a();
            RunPhase4b();
            RunDpi();
            RunCategories();
            RunMedia();
            RunLive();
            RunGrabber();
            RunFtps();
            RunLang();
            RunNet();
            RunPhase6();
            RunSetup();
            RunFixesA();
            RunFixesB();
            RunOpciones1();
            RunOpciones2();
            RunOpciones3();
            RunOpciones4();
            RunOpciones5();
            RunFixesF();
            RunFixesG();
            RunVentanaA();
            RunAutoCancel();
            RunSubtitles();
            RunExtProxy();
            RunUpdates();
            RunUiDetails();
            RunNet2();
            RunIntegracion();

            S.Stop();
            ReleaseMenuHook();   // si no, a veces sale con 0xC000041D (ver ReleaseMenuHook)
            Console.WriteLine();
            Console.WriteLine(string.Format("Resultado: {0} correctos, {1} fallidos, {2} omitidos", _pass, _fail, _skip));
            foreach (string f in _failures) Console.WriteLine("  FALLO: " + f);
            if (_fail == 0) { try { Directory.Delete(Home, true); } catch { } }
            else Console.WriteLine("Datos conservados para revisar: " + Home);
            return _fail == 0 ? 0 : 1;
        }

        // Gancho WH_GETMESSAGE que WinForms pone al abrir un menú fuera de Application.Run (ToolStripManager.ModalMenuFilter
        // .HostedWindowsFormsMessageHook). IntPtr.Zero si no está puesto.
        const System.Reflection.BindingFlags AnyMember = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public |
                                                         System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;

        static object MenuHookObject()
        {
            Type mf = typeof(System.Windows.Forms.ToolStripManager).GetNestedType("ModalMenuFilter", AnyMember);
            System.Reflection.FieldInfo inst = mf != null ? mf.GetField("_instance", AnyMember) : null, hookF = mf != null ? mf.GetField("messageHook", AnyMember) : null;
            object filter = inst != null ? inst.GetValue(null) : null;
            return filter != null && hookF != null ? hookF.GetValue(filter) : null;
        }

        static IntPtr MenuMessageHook()
        {
            object hook = MenuHookObject();
            System.Reflection.FieldInfo h = hook != null ? hook.GetType().GetField("messageHookHandle", AnyMember) : null;
            return h != null ? (IntPtr)h.GetValue(hook) : IntPtr.Zero;
        }

        // Salida con 0xC000041D (STATUS_FATAL_USER_CALLBACK_EXCEPTION; antes, 0xC0020001: código nativo que llama a
        // código .NET con el CLR ya cerrado, dentro de una llamada de user32). La causa: las pruebas abren menús
        // (ShowDropDown/HideDropDown en "idiomas" y en la ventana principal) sin Application.Run, y entonces WinForms
        // (ToolStripManager.ModalMenuFilter) pone un gancho WH_GETMESSAGE con un procedimiento .NET que no quita al
        // cerrarlos. Al volver de Main, si el hilo principal lee algún mensaje mientras se cierra el proceso, Windows
        // llama al gancho con el CLR ya cerrado y mata el proceso: por eso pasaba solo "a veces" (con las 4 pruebas de
        // "idiomas" solas, siempre). Se sale del modo menú y, si aun así sigue puesto, se quita el gancho.
        static void ReleaseMenuHook()
        {
            try
            {
                if (MenuMessageHook() == IntPtr.Zero) return;
                Type mf = typeof(System.Windows.Forms.ToolStripManager).GetNestedType("ModalMenuFilter", AnyMember);
                System.Reflection.MethodInfo exit = mf.GetMethod("ExitMenuMode", AnyMember, null, Type.EmptyTypes, null);
                if (exit != null) exit.Invoke(null, null);
                object hook = MenuHookObject();
                System.Reflection.PropertyInfo on = hook != null ? hook.GetType().GetProperty("HookMessages", AnyMember) : null;
                if (MenuMessageHook() != IntPtr.Zero && on != null) on.SetValue(hook, false, null);
            }
            catch (Exception ex) { Console.WriteLine("(no se pudo quitar el gancho de los menús: " + ex.Message + ")"); }
            if (MenuMessageHook() != IntPtr.Zero) Console.WriteLine("(el gancho de los menús de WinForms sigue puesto)");
        }


        // Modo servidor para pruebas manuales/visuales: IDMCloneTests.exe --serve [puerto]
        static int Serve(string[] args)
        {
            TestServer s = new TestServer();
            int port = 0;
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--serve") int.TryParse(args[i + 1], out port);
            s.Start(port);
            Console.WriteLine("Servidor de pruebas en " + s.Base + "  (Ctrl+C para salir)");
            Console.WriteLine("  " + s.Url("range,veryslow", "video-lento.mp4", 40L * 1024 * 1024, "v1"));
            Console.WriteLine("  " + s.Url("range,slow", "programa.exe", 25L * 1024 * 1024, "p1"));
            Console.WriteLine("  " + s.Url("norange,veryslow", "sin-rangos.zip", 8L * 1024 * 1024, "z1"));
            Console.WriteLine("  " + s.Url("html", "caducado.pdf", 3L * 1024 * 1024, "h1"));
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        // Ventana de IDMClone falsa que rechaza todas las descargas (para probar que la extensión
        // devuelve la descarga al navegador). Usa la misma tubería que la GUI de esa carpeta de datos.
        static int FakeGui(string home)
        {
            AppConfig.HomeOverride = home;
            Ipc.StartServer(delegate(string req)
            {
                Console.WriteLine("petición recibida: " + req.Substring(0, Math.Min(120, req.Length)));
                if (req.Contains("\"type\":\"ping\"")) return "{\"ok\":true,\"gui\":true}";
                return Ipc.ErrorJson("Rechazada por la prueba");
            });
            Console.WriteLine("GUI falsa escuchando en " + Ipc.PipeName);
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        // ---------------------------------------------------------------- infraestructura

        class SkipException : Exception { public SkipException(string m) : base(m) { } }

        static void Run(string name, Action test)
        {
            if (!string.IsNullOrEmpty(Filter) && name.IndexOf(Filter, StringComparison.OrdinalIgnoreCase) < 0) return;
            Stopwatch sw = Stopwatch.StartNew();
            string savedHome = AppConfig.HomeOverride;
            try
            {
                test();
                _pass++;
                Console.WriteLine(string.Format("  OK    {0}  ({1:0.0} s)", name, sw.Elapsed.TotalSeconds));
            }
            catch (SkipException ex)
            {
                _skip++;
                Console.WriteLine("  SKIP  " + name + "  -> " + ex.Message);
            }
            catch (Exception ex)
            {
                _fail++;
                _failures.Add(name + ": " + ex.Message);
                Console.WriteLine("  FALLO " + name + "  -> " + ex.Message);
            }
            finally { AppConfig.HomeOverride = savedHome; }
        }

        static void Check(bool cond, string msg) { if (!cond) throw new Exception(msg); }

        static AppConfig Cfg()
        {
            AppConfig c = AppConfig.CreateDefault();
            c.DefaultFolder = Dl;
            foreach (Category cat in c.Categories) cat.Folder = Dl;
            c.RetryCount = 5;
            c.RetryDelaySec = 1;
            c.MaxConnections = 8;
            c.MinSplitSize = 128 * 1024;
            // Los tests no muestran ventanas salvo que las pidan (las de 4a las activan a propósito).
            c.ShowFileInfoDialog = false;
            c.ShowCompleteDialog = false;
            c.ProgressDialogMode = "none";
            c.AutoCheckUpdates = false; // ni la GUI de las pruebas ni los e2e consultan el servidor de actualizaciones
            return c;
        }

        static DownloadManager NewMgr() { return new DownloadManager(Cfg()); }

        static string NewId(string tag) { return tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 8); }

        static void WaitEnd(DownloadItem it, int seconds)
        {
            DateTime limit = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < limit)
            {
                bool running = it.Engine != null && it.Engine.IsRunning;
                if (!running && (it.Status == DownloadStatus.Complete || it.Status == DownloadStatus.Error || it.Status == DownloadStatus.Paused))
                    return;
                Thread.Sleep(50);
            }
            throw new Exception("Tiempo agotado esperando el final (estado " + it.Status + ", " + it.Downloaded + " bytes)");
        }

        static void WaitProgress(DownloadItem it, long bytes, int seconds)
        {
            DateTime limit = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < limit)
            {
                if (it.Downloaded >= bytes) return;
                if (it.Status == DownloadStatus.Complete || it.Status == DownloadStatus.Error) throw new Exception("Terminó antes de poder pausar: " + it.Status + " " + it.Error);
                Thread.Sleep(20);
            }
            throw new Exception("Tiempo agotado esperando progreso");
        }

        // Lee un archivo que la aplicación puede estar guardando en ese momento (reintenta).
        static string ReadShared(string path)
        {
            for (int i = 0; ; i++)
            {
                try
                {
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (StreamReader sr = new StreamReader(fs)) return sr.ReadToEnd();
                }
                catch (IOException) { if (i >= 20) throw; Thread.Sleep(50); }
            }
        }

        static string FileSha(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
        }

        static void AssertComplete(DownloadItem it, long size)
        {
            Check(it.Status == DownloadStatus.Complete, "Estado " + it.Status + (it.Error != null ? " (" + it.Error + ")" : "") + ", se esperaba Completado");
            Check(File.Exists(it.FilePath), "No existe el archivo " + it.FilePath);
            long len = new FileInfo(it.FilePath).Length;
            Check(len == size, "Tamaño " + len + ", se esperaba " + size);
            Check(it.Size == size, "Size del item " + it.Size + ", se esperaba " + size);
            Check(FileSha(it.FilePath) == TestServer.Sha256Of(size), "SHA-256 distinto: el archivo está corrupto");
        }

        static DownloadItem Download(DownloadManager m, string modes, long size, string tag, int timeoutSec)
        {
            string id = NewId(tag);
            DownloadItem it = m.Add(S.Url(modes, tag + ".bin", size, id), Dl, null, true);
            WaitEnd(it, timeoutSec);
            return it;
        }

        static string IdOf(DownloadItem it)
        {
            int i = it.Url.IndexOf("&id=");
            return it.Url.Substring(i + 4);
        }

        static void UseFreshHome(string tag)
        {
            string h = Path.Combine(Home, "home-" + tag);
            Directory.CreateDirectory(h);
            AppConfig.HomeOverride = h;
        }

        // ---------------------------------------------------------------- motor

        static void T_Normal()
        {
            DownloadItem it = Download(NewMgr(), "range,slow", 3 * 1024 * 1024 + 123, "normal", 60);
            AssertComplete(it, 3 * 1024 * 1024 + 123);
            Check(S.Count(IdOf(it) + ":ranged") >= 1, "No se usaron varias conexiones");
            Check(!File.Exists(it.StatePath), "El estado debería borrarse al completar");
        }

        static void T_Tiny() { AssertComplete(Download(NewMgr(), "range", 5, "tiny", 30), 5); }

        static void T_Empty() { AssertComplete(Download(NewMgr(), "range", 0, "empty", 30), 0); }

        static void T_NoRange()
        {
            DownloadItem it = Download(NewMgr(), "norange", 1024 * 1024, "norange", 60);
            AssertComplete(it, 1024 * 1024);
            Check(!it.Resumable, "No debería marcarse como reanudable");
        }

        static void T_NoRangeCut()
        {
            DownloadItem it = Download(NewMgr(), "norange,cut", 1024 * 1024, "norangecut", 60);
            AssertComplete(it, 1024 * 1024);
            Check(S.Count(IdOf(it) + ":cutcount") >= 2, "El servidor no llegó a cortar (la prueba no probó nada)");
        }

        static void T_RangeCut()
        {
            DownloadItem it = Download(NewMgr(), "range,cut", 2 * 1024 * 1024, "rangecut", 60);
            AssertComplete(it, 2 * 1024 * 1024);
        }

        static void T_NoLength()
        {
            DownloadItem it = Download(NewMgr(), "nolen", 700 * 1024 + 7, "nolen", 60);
            AssertComplete(it, 700 * 1024 + 7);
        }

        static void T_IgnoreRange()
        {
            DownloadItem it = Download(NewMgr(), "ignorerange", 2 * 1024 * 1024, "ignorerange", 60);
            AssertComplete(it, 2 * 1024 * 1024);
        }

        static void T_Shift()
        {
            DownloadItem it = Download(NewMgr(), "shift", 2 * 1024 * 1024, "shift", 60);
            AssertComplete(it, 2 * 1024 * 1024);
        }

        static void T_NoContentRange()
        {
            DownloadItem it = Download(NewMgr(), "nocr", 1024 * 1024, "nocr", 60);
            AssertComplete(it, 1024 * 1024);
        }

        static void T_HtmlExpired()
        {
            DownloadItem it = Download(NewMgr(), "range,slow,html", 2 * 1024 * 1024, "html", 60);
            Check(it.Status == DownloadStatus.Error, "Estado " + it.Status + ": no debe darse por completada una página HTML");
            Check(it.Error != null && it.Error.IndexOf("página", StringComparison.OrdinalIgnoreCase) >= 0
                  || it.Error != null && it.Error.IndexOf("distinto", StringComparison.OrdinalIgnoreCase) >= 0,
                  "Mensaje de error poco claro: " + it.Error);
        }

        static void T_SpeedLimit()
        {
            AppConfig c = Cfg(); c.SpeedLimitKBps = 256;
            DownloadManager m = new DownloadManager(c);
            Stopwatch sw = Stopwatch.StartNew();
            DownloadItem it = Download(m, "range", 1024 * 1024, "limit", 40);
            double s = sw.Elapsed.TotalSeconds;
            AssertComplete(it, 1024 * 1024);
            Check(s >= 2.4, string.Format("Demasiado rápido ({0:0.0} s): el límite no se aplica", s));
            Check(s <= 15, string.Format("Demasiado lento ({0:0.0} s)", s));
        }

        static void T_SpeedLimitGlobal()
        {
            AppConfig c = Cfg(); c.SpeedLimitKBps = 256;
            DownloadManager m = new DownloadManager(c);
            Stopwatch sw = Stopwatch.StartNew();
            DownloadItem a = m.Add(S.Url("range", "g1.bin", 512 * 1024, NewId("g1")), Dl, null, true);
            DownloadItem b = m.Add(S.Url("range", "g2.bin", 512 * 1024, NewId("g2")), Dl, null, true);
            WaitEnd(a, 40); WaitEnd(b, 40);
            double s = sw.Elapsed.TotalSeconds;
            AssertComplete(a, 512 * 1024); AssertComplete(b, 512 * 1024);
            // 1 MB en total a 256 KB/s con 1 s de ráfaga: ~3 s. Si el límite fuera por descarga, ~1 s.
            Check(s >= 2.4, string.Format("Demasiado rápido ({0:0.0} s): el límite no es global", s));
        }

        static void T_PauseResume()
        {
            DownloadManager m = NewMgr();
            long size = 2 * 1024 * 1024;
            DownloadItem it = m.Add(S.Url("range,veryslow", "pause.bin", size, NewId("pause")), Dl, null, true);
            WaitProgress(it, 300 * 1024, 30);
            Stopwatch sw = Stopwatch.StartNew();
            m.StopItem(it);
            WaitEnd(it, 10);
            Check(sw.Elapsed.TotalSeconds < 3, "La pausa tardó " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s (debería ser inmediata)");
            Check(it.Status == DownloadStatus.Paused, "Estado tras pausar: " + it.Status);
            long before = it.Downloaded;
            m.StartItem(it);
            WaitEnd(it, 60);
            AssertComplete(it, size);
            Check(before > 0, "No había progreso antes de pausar");
        }

        static void T_PauseResumeImmediate()
        {
            DownloadManager m = NewMgr();
            long size = 2 * 1024 * 1024;
            DownloadItem it = m.Add(S.Url("range,veryslow", "pause2.bin", size, NewId("pause2")), Dl, null, true);
            WaitProgress(it, 200 * 1024, 30);
            m.StopItem(it);
            m.StartItem(it); // sin esperar: antes se ignoraba en silencio
            Thread.Sleep(300);
            Check(it.Engine != null && it.Engine.IsRunning && !it.Engine.StopRequested, "Reanudar justo después de pausar no arrancó la descarga");
            WaitEnd(it, 60);
            AssertComplete(it, size);
        }

        static void T_ResumeMissingFile()
        {
            DownloadManager m = NewMgr();
            long size = 2 * 1024 * 1024;
            DownloadItem it = m.Add(S.Url("range,veryslow", "missing.bin", size, NewId("missing")), Dl, null, true);
            WaitProgress(it, 300 * 1024, 30);
            m.StopItem(it); WaitEnd(it, 10);
            File.Delete(it.WorkingPath);
            m.StartItem(it);
            WaitEnd(it, 60);
            AssertComplete(it, size);
        }

        static void T_ResumeTruncatedFile()
        {
            DownloadManager m = NewMgr();
            long size = 2 * 1024 * 1024;
            DownloadItem it = m.Add(S.Url("range,veryslow", "trunc.bin", size, NewId("trunc")), Dl, null, true);
            WaitProgress(it, 300 * 1024, 30);
            m.StopItem(it); WaitEnd(it, 10);
            using (FileStream fs = new FileStream(it.WorkingPath, FileMode.Open)) fs.SetLength(1000);
            m.StartItem(it);
            WaitEnd(it, 60);
            AssertComplete(it, size);
        }

        // ---------------------------------------------------------------- gestor

        static void T_DoubleStart()
        {
            DownloadManager m = NewMgr();
            long size = 1024 * 1024;
            string id = NewId("double");
            DownloadItem it = m.Add(S.Url("range,slow", "double.bin", size, id), Dl, null, false);
            ManualResetEvent go = new ManualResetEvent(false);
            List<Thread> ts = new List<Thread>();
            for (int i = 0; i < 6; i++)
            {
                Thread t = new Thread(delegate() { go.WaitOne(); m.StartItem(it); });
                t.Start(); ts.Add(t);
            }
            go.Set();
            foreach (Thread t in ts) t.Join();
            Thread.Sleep(200);
            WaitEnd(it, 60);
            AssertComplete(it, size);
            Check(S.Count(id + ":first") == 1, "Se lanzaron " + S.Count(id + ":first") + " motores para la misma descarga");
        }

        static void T_SameUrlTwice()
        {
            DownloadManager m = NewMgr();
            long size = 1024 * 1024;
            string url = S.Url("range,slow", "same.bin", size, NewId("same"));
            DownloadItem a = m.Add(url, Dl, null, true);
            DownloadItem b = m.Add(url, Dl, null, true);
            Check(!string.Equals(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase), "Las dos descargas tienen la misma ruta: " + a.FilePath);
            WaitEnd(a, 60); WaitEnd(b, 60);
            AssertComplete(a, size); AssertComplete(b, size);
        }

        static void T_PersistAuth()
        {
            UseFreshHome("auth");
            long size = 2 * 1024 * 1024;
            string id = NewId("auth");
            DownloadManager m1 = NewMgr();
            DownloadItem it = m1.Add(S.Url("range,veryslow,needauth", "auth.bin", size, id), Dl, null, true,
                                     "sess=abc; theme=dark", "http://ref.example/page", "Mozilla/5.0 TestUA/1");
            WaitProgress(it, 300 * 1024, 30);
            m1.StopItem(it); WaitEnd(it, 10);
            Check(it.Status == DownloadStatus.Paused, "Estado tras pausar: " + it.Status);

            string raw = ReadShared(AppConfig.DownloadsPath);
            Check(raw.IndexOf("sess=abc", StringComparison.Ordinal) < 0, "Las cookies se guardaron en claro en downloads.json");
            Check(raw.IndexOf("ref.example", StringComparison.Ordinal) >= 0, "El referer no se guardó");

            // "Reiniciar la app": gestor nuevo que carga la lista del disco.
            DownloadManager m2 = NewMgr();
            m2.LoadList();
            DownloadItem it2 = m2.Items.Find(delegate(DownloadItem x) { return x.Id == it.Id; });
            Check(it2 != null, "La descarga no está en la lista recargada");
            Check(it2.Cookies == "sess=abc; theme=dark", "Cookies recargadas: " + it2.Cookies);
            Check(it2.Segments.Count > 0, "No se recargaron los segmentos");
            int deniedBefore = S.Count(id + ":denied");
            m2.StartItem(it2);
            WaitEnd(it2, 60);
            AssertComplete(it2, size);
            Check(S.Count(id + ":denied") == deniedBefore, "El servidor rechazó peticiones sin cookies/referer/UA");
        }

        static void T_RemoveWhileDownloading()
        {
            DownloadManager m = NewMgr();
            DownloadItem it = m.Add(S.Url("range,veryslow", "remove.bin", 2 * 1024 * 1024, NewId("remove")), Dl, null, true);
            WaitProgress(it, 200 * 1024, 30);
            string working = it.WorkingPath;
            m.Remove(it, true);
            Thread.Sleep(1500); // dar tiempo a que un motor "zombi" volviera a escribir
            Check(!File.Exists(it.FilePath), "El archivo sigue en disco");
            Check(!File.Exists(working), "El archivo temporal sigue en disco: " + working);
            Check(!File.Exists(it.StatePath), "El estado se volvió a crear tras eliminar");
            Check(!m.Items.Contains(it), "Sigue en la lista");
        }

        static void T_ConsoleNoPersist()
        {
            UseFreshHome("console");
            DownloadManager gui = NewMgr();
            gui.Add(S.Url("range", "keep1.bin", 10, NewId("k1")), Dl, null, false);
            gui.Add(S.Url("range", "keep2.bin", 10, NewId("k2")), Dl, null, false);
            byte[] before = File.ReadAllBytes(AppConfig.DownloadsPath);

            DownloadManager console = NewMgr();
            console.Persist = false;
            DownloadItem it = console.Add(S.Url("range", "cons.bin", 300 * 1024, NewId("cons")), Dl, null, true);
            WaitEnd(it, 30);
            AssertComplete(it, 300 * 1024);
            byte[] after = File.ReadAllBytes(AppConfig.DownloadsPath);
            Check(Convert.ToBase64String(before) == Convert.ToBase64String(after), "downloads.json cambió");
        }

        // ---------------------------------------------------------------- guardado

        static void T_RecoverFromBak()
        {
            UseFreshHome("bak");
            DownloadManager m = NewMgr();
            m.Add(S.Url("range", "r1.bin", 10, NewId("r1")), Dl, null, false);
            m.Add(S.Url("range", "r2.bin", 10, NewId("r2")), Dl, null, false); // el 2º guardado deja .bak con 1 item
            File.WriteAllText(AppConfig.DownloadsPath, "[{\"Id\":\"roto"); // corte a mitad
            DownloadManager m2 = NewMgr();
            m2.LoadList();
            Check(m2.Items.Count == 1, "Se esperaba recuperar 1 descarga del .bak, hay " + m2.Items.Count);
            Check(m2.LoadWarning != null, "No se avisó de la recuperación");
        }

        static void T_CorruptPreserved()
        {
            UseFreshHome("corrupt");
            Directory.CreateDirectory(AppConfig.Dir);
            File.WriteAllText(AppConfig.DownloadsPath, "basura{{{");
            File.WriteAllText(AppConfig.DownloadsPath + ".bak", "más basura");
            DownloadManager m = NewMgr();
            m.LoadList();
            Check(m.Items.Count == 0, "No debería cargar nada");
            Check(m.LoadWarning != null, "No se avisó del archivo dañado");
            string[] corrupt = Directory.GetFiles(AppConfig.Dir, "downloads.json.corrupt-*");
            Check(corrupt.Length == 1, "No se apartó el archivo dañado");
            Check(File.ReadAllText(corrupt[0]) == "basura{{{", "El contenido apartado no es el original");
            m.Add(S.Url("range", "c1.bin", 10, NewId("c1")), Dl, null, false);
            Check(File.Exists(corrupt[0]), "El archivo apartado se perdió al guardar");
        }

        static void T_ConcurrentSave()
        {
            UseFreshHome("concurrent");
            DownloadManager m = NewMgr();
            for (int i = 0; i < 20; i++) m.Add(S.Url("range", "cc" + i + ".bin", 10, NewId("cc")), Dl, null, false);
            List<Thread> ts = new List<Thread>();
            int errors = 0;
            for (int t = 0; t < 8; t++)
            {
                Thread th = new Thread(delegate() { try { for (int k = 0; k < 60; k++) m.SaveList(); } catch { Interlocked.Increment(ref errors); } });
                th.Start(); ts.Add(th);
            }
            foreach (Thread th in ts) th.Join();
            Check(errors == 0, "Excepciones al guardar");
            DownloadManager m2 = NewMgr();
            m2.LoadList();
            Check(m2.Items.Count == 20, "Tras guardar en paralelo hay " + m2.Items.Count + " descargas (esperadas 20)");
            Check(m2.LoadWarning == null, "El archivo quedó dañado: " + m2.LoadWarning);
            Check(!File.Exists(AppConfig.DownloadsPath + ".tmp"), "Quedó un .tmp");
        }

        static void T_ConfigCorrupt()
        {
            UseFreshHome("config");
            Directory.CreateDirectory(AppConfig.Dir);
            File.WriteAllText(AppConfig.ConfigPath, "{\"MaxConnections\":");
            AppConfig c = AppConfig.Load();
            Check(c.MaxConnections == 8, "No se usaron los valores por defecto");
            Check(AppConfig.LoadWarning != null, "No se avisó de la config dañada");
            Check(Directory.GetFiles(AppConfig.Dir, "config.json.corrupt-*").Length == 1, "La config dañada no se apartó");

            // Valores absurdos de una config editada a mano.
            File.WriteAllText(AppConfig.ConfigPath, "{\"MaxConnections\":500,\"MinSplitSize\":0,\"RetryDelaySec\":0,\"RetryCount\":-3,\"SpeedLimitKBps\":-1,\"DefaultFolder\":null,\"Categories\":[{\"Name\":\"X\",\"Folder\":null,\"Extensions\":\"zip\"}]}");
            AppConfig d = AppConfig.Load();
            Check(d.MaxConnections == 32, "MaxConnections=" + d.MaxConnections);
            Check(d.MinSplitSize >= AppConfig.MinAllowedSplit, "MinSplitSize=" + d.MinSplitSize);
            Check(d.RetryDelaySec >= 1, "RetryDelaySec=" + d.RetryDelaySec);
            Check(d.RetryCount >= 0, "RetryCount=" + d.RetryCount);
            Check(d.SpeedLimitKBps == 0, "SpeedLimitKBps=" + d.SpeedLimitKBps);
            Check(!string.IsNullOrEmpty(d.DefaultFolder), "DefaultFolder vacío");
            Check(!string.IsNullOrEmpty(d.Categories[0].Folder), "Carpeta de categoría vacía");
        }

        // ---------------------------------------------------------------- IPC / GUI

        static void T_IpcRequestReply()
        {
            string pipe = "IDMClone_Test_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Ipc.Server srv = Ipc.StartServer(delegate(string req)
            {
                if (req == "boom") throw new InvalidOperationException("falló el manejador");
                return "{\"ok\":true,\"echo\":" + Ipc.JsonQuote(req) + "}";
            }, pipe);
            try
            {
                Thread.Sleep(100);
                string r = Ipc.Request("hola \"mundo\"", 2000, 5000, pipe);
                Check(r != null && r.Contains("\"ok\":true") && r.Contains("mundo"), "Respuesta: " + r);
                string e = Ipc.Request("boom", 2000, 5000, pipe);
                Check(e != null && e.Contains("\"ok\":false") && e.Contains("falló"), "Un error del manejador no llegó al cliente: " + e);

                // Varias peticiones a la vez: todas reciben su respuesta.
                int okCount = 0;
                List<Thread> ts = new List<Thread>();
                for (int i = 0; i < 8; i++)
                {
                    int n = i;
                    Thread t = new Thread(delegate() { string x = Ipc.Request("p" + n, 3000, 5000, pipe); if (x != null && x.Contains("p" + n)) Interlocked.Increment(ref okCount); });
                    t.Start(); ts.Add(t);
                }
                foreach (Thread t in ts) t.Join();
                Check(okCount == 8, "Solo " + okCount + " de 8 peticiones simultáneas recibieron respuesta");
            }
            finally { srv.Stop(); }

            Stopwatch sw = Stopwatch.StartNew();
            string none = Ipc.Request("x", 500, 1000, pipe + "_nadie");
            Check(none == null, "Sin servidor debería devolver null");
            Check(sw.Elapsed.TotalSeconds < 3, "Sin servidor tardó demasiado");
        }

        static void T_DeleteDialog()
        {
            using (DeleteConfirmForm f = new DeleteConfirmForm(3))
            {
                Check(!f.DeleteFiles, "La casilla de borrar del disco está marcada por defecto");
                Check(f.AcceptButton != null && f.CancelButton != null, "Faltan los botones Aceptar/Cancelar");
            }
        }

        // ---------------------------------------------------------------- extremo a extremo

        static void RequireExe()
        {
            if (string.IsNullOrEmpty(ExePath) || !File.Exists(ExePath)) throw new SkipException("falta --exe IDMClone.exe");
        }

        static void T_E2E_ConsoleMode()
        {
            RequireExe();
            UseFreshHome("e2e-console");
            string home = AppConfig.Dir;
            new DownloadManager(Cfg()) { }.Add(S.Url("range", "e2ekeep.bin", 10, NewId("ek")), Dl, null, false);
            Cfg().Save();
            byte[] before = File.ReadAllBytes(AppConfig.DownloadsPath);

            string outDir = Path.Combine(Home, "e2e-console-out");
            Directory.CreateDirectory(outDir);
            long size = 600 * 1024;
            ProcessStartInfo psi = new ProcessStartInfo(ExePath,
                "/console /d \"" + S.Url("range", "cli.bin", size, NewId("cli")) + "\" /p \"" + outDir + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.EnvironmentVariables["IDMCLONE_HOME"] = home;
            using (Process p = Process.Start(psi))
            {
                Check(p.WaitForExit(60000), "IDMClone.exe /n no terminó");
                Check(p.ExitCode == 0, "Código de salida " + p.ExitCode);
            }
            string file = Path.Combine(outDir, "cli.bin");
            Check(File.Exists(file), "No se descargó " + file);
            Check(FileSha(file) == TestServer.Sha256Of(size), "Archivo corrupto");
            byte[] after = File.ReadAllBytes(AppConfig.DownloadsPath);
            Check(Convert.ToBase64String(before) == Convert.ToBase64String(after), "/n modificó downloads.json");
        }

        static byte[] Frame(string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] r = new byte[4 + body.Length];
            Array.Copy(BitConverter.GetBytes(body.Length), r, 4);
            Array.Copy(body, 0, r, 4, body.Length);
            return r;
        }

        static string ReadFrame(Stream s, int timeoutMs)
        {
            string result = null;
            Thread t = new Thread(delegate()
            {
                try
                {
                    byte[] len = new byte[4]; int got = 0;
                    while (got < 4) { int r = s.Read(len, got, 4 - got); if (r <= 0) return; got += r; }
                    int n = BitConverter.ToInt32(len, 0);
                    byte[] body = new byte[n]; got = 0;
                    while (got < n) { int r = s.Read(body, got, n - got); if (r <= 0) return; got += r; }
                    result = Encoding.UTF8.GetString(body);
                }
                catch { }
            });
            t.IsBackground = true; t.Start();
            t.Join(timeoutMs);
            return result;
        }

        static void KillTestGuis()
        {
            foreach (Process p in Process.GetProcessesByName("IDMClone"))
            {
                try
                {
                    if (string.Equals(p.MainModule.FileName, Path.GetFullPath(ExePath), StringComparison.OrdinalIgnoreCase) && p.StartTime >= _started)
                    {
                        p.Kill(); p.WaitForExit(5000);
                    }
                }
                catch { }
            }
        }

        static void T_E2E_NativeHost()
        {
            RequireExe();
            UseFreshHome("e2e-host");
            string home = AppConfig.Dir;
            Cfg().Save(); // DefaultFolder = carpeta de pruebas
            long size = 1024 * 1024 + 77;
            string id = NewId("host");
            Process host = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(ExePath, "chrome-extension://jolfcgooaockjhnalagfilpljkloallc/");
                psi.UseShellExecute = false;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                psi.EnvironmentVariables["IDMCLONE_HOME"] = home;
                // .NET Framework escribe el BOM de Console.InputEncoding en el stdin del hijo si la
                // consola está en UTF-8 (Chrome nunca lo hace): usar UTF-8 sin BOM mientras se lanza.
                System.Text.Encoding savedIn = null;
                try { savedIn = Console.InputEncoding; Console.InputEncoding = new UTF8Encoding(false); } catch { }
                try { host = Process.Start(psi); }
                finally { if (savedIn != null) { try { Console.InputEncoding = savedIn; } catch { } } }
                Stream stdin = host.StandardInput.BaseStream;
                Stream stdout = host.StandardOutput.BaseStream;

                // 1) Arranque en frío: la GUI no está abierta; el host la lanza y espera su confirmación.
                string msg = "{\"type\":\"download\",\"url\":" + Ipc.JsonQuote(S.Url("range", "host.bin", size, id)) +
                             ",\"filename\":\"host.bin\",\"referrer\":\"\",\"cookies\":\"\",\"userAgent\":\"TestUA/1\"}";
                byte[] f = Frame(msg); stdin.Write(f, 0, f.Length); stdin.Flush();
                string r1 = ReadFrame(stdout, 60000);
                Check(r1 != null, "El host no respondió");
                Check(r1.Contains("\"ok\":true") && r1.Contains("\"id\""), "Respuesta del host: " + r1);

                // 2) URL no válida: la GUI la rechaza y el host lo dice (la extensión NO cancelará).
                f = Frame("{\"type\":\"download\",\"url\":\"esto no es una url\"}"); stdin.Write(f, 0, f.Length); stdin.Flush();
                string r2 = ReadFrame(stdout, 30000);
                Check(r2 != null && r2.Contains("\"ok\":false"), "Una URL no válida debería devolver ok:false, devolvió: " + r2);

                // 3) La descarga se completa en la GUI.
                string file = Path.Combine(Dl, "host.bin");
                DateTime limit = DateTime.Now.AddSeconds(60);
                bool done = false;
                while (DateTime.Now < limit && !done)
                {
                    Thread.Sleep(300);
                    try
                    {
                        string list = File.ReadAllText(AppConfig.DownloadsPath);
                        done = list.Contains("host.bin") && list.Contains("\"Status\":4") && File.Exists(file) && FileSha(file) == TestServer.Sha256Of(size);
                    }
                    catch { }
                }
                Check(done, "La GUI no completó la descarga correctamente");
                stdin.Close();
                Check(host.WaitForExit(10000), "El host no terminó al cerrar stdin");
            }
            finally
            {
                if (host != null) { try { if (!host.HasExited) host.Kill(); } catch { } }
                KillTestGuis();
            }
        }
    }
}
