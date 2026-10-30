using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace IDMClone
{
    // Argumentos de línea de comandos al estilo IDM (IDMan.exe, PDF §8.4):
    //   idmclone /d URL [/p carpeta] [/f nombre] [/q] [/h] [/n] [/a]
    //   idmclone /s                inicia la cola principal
    //   idmclone /onboot           arranque con Windows (oculto en la bandeja)
    //   /d URL descargar · /p carpeta · /f nombre · /a solo añadir a la cola · /n silencioso (sin preguntas)
    //   /q salir de IDMClone al terminar · /h colgar al terminar (se acepta; no aplica a conexiones actuales)
    // Propio de IDMClone: /console descarga en la consola sin ventana · /tray abrir oculto en la bandeja.
    // Menú contextual del Explorador: /urlfile "x.url" descarga la dirección del acceso directo de Internet;
    // /linksfile "x.txt" abre la lista de enlaces con las direcciones del archivo (.txt, .htm, .html).
    public class CliArgs
    {
        public string Url;
        public string Path;
        public string FileName;
        public bool Silent;      // /n  -> sin preguntas ni ventanas
        public bool AddOnly;     // /a  -> solo añadir a la cola
        public bool StartQueue;  // /s
        public bool QuitWhenDone;// /q
        public bool HangUp;      // /h
        public bool OnBoot;      // /onboot
        public bool Tray;        // /tray
        public bool Console;     // /console
        public string UrlFile;   // /urlfile  -> acceso directo de Internet (.url)
        public string LinksFile; // /linksfile -> archivo de texto o página con enlaces
        public bool Help;

        public static CliArgs Parse(string commandLine)
        {
            // Divide respetando comillas dobles (sin escapes: las rutas acaban a menudo en "\").
            List<string> parts = new List<string>();
            if (!string.IsNullOrEmpty(commandLine))
            {
                bool inq = false; StringBuilder cur = new StringBuilder();
                foreach (char c in commandLine)
                {
                    if (c == '"') inq = !inq;
                    else if (c == ' ' && !inq) { if (cur.Length > 0) { parts.Add(cur.ToString()); cur.Length = 0; } }
                    else cur.Append(c);
                }
                if (cur.Length > 0) parts.Add(cur.ToString());
            }
            return Parse(parts.ToArray());
        }

        static bool IsSwitch(string s) { return s.Length > 1 && (s[0] == '/' || s[0] == '-') && !Util.LooksLikeUrl(s) && s.IndexOf('\\') < 0; }

        static string Value(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) return null;
            string v = args[i + 1];
            if (IsSwitch(v) && v.Length <= 8) return null; // "/d /n": falta el valor
            i++;
            // Windows lee "C:\x\" como C:\x" (la \ escapa la comilla): quitar la comilla sobrante.
            return v.TrimEnd('"').Trim();
        }

        public static CliArgs Parse(string[] args)
        {
            CliArgs a = new CliArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string s = args[i];
                if (s == null) continue;
                string low = s.ToLowerInvariant();
                switch (low)
                {
                    case "/d": case "-d": a.Url = Value(args, ref i); break;
                    case "/p": case "-p": a.Path = Value(args, ref i); break;
                    case "/f": case "-f": a.FileName = Value(args, ref i); break;
                    case "/n": case "-n": a.Silent = true; break;
                    case "/a": case "-a": a.AddOnly = true; break;
                    case "/s": case "-s": a.StartQueue = true; break;
                    case "/q": case "-q": a.QuitWhenDone = true; break;
                    case "/h": case "-h": a.HangUp = true; break;
                    case "/onboot": case "-onboot": a.OnBoot = true; break;
                    case "/tray": case "-tray": a.Tray = true; break;
                    case "/console": case "-console": a.Console = true; break;
                    case "/urlfile": case "-urlfile": a.UrlFile = Value(args, ref i); break;
                    case "/linksfile": case "-linksfile": a.LinksFile = Value(args, ref i); break;
                    case "/?": case "/help": case "--help": a.Help = true; break;
                    default: if (Util.LooksLikeUrl(s)) a.Url = s; break;
                }
            }
            return a;
        }

        // Para reenviarla a la instancia abierta (la carpeta ya en ruta absoluta).
        public string ToCommandLine()
        {
            StringBuilder sb = new StringBuilder();
            if (!string.IsNullOrEmpty(Url)) sb.Append("/d \"").Append(Url).Append("\" ");
            if (!string.IsNullOrEmpty(Path)) sb.Append("/p \"").Append(Path).Append("\" ");
            if (!string.IsNullOrEmpty(FileName)) sb.Append("/f \"").Append(FileName).Append("\" ");
            if (!string.IsNullOrEmpty(UrlFile)) sb.Append("/urlfile \"").Append(UrlFile).Append("\" ");
            if (!string.IsNullOrEmpty(LinksFile)) sb.Append("/linksfile \"").Append(LinksFile).Append("\" ");
            if (AddOnly) sb.Append("/a ");
            if (Silent) sb.Append("/n ");
            if (QuitWhenDone) sb.Append("/q ");
            if (HangUp) sb.Append("/h ");
            if (StartQueue) sb.Append("/s ");
            return sb.ToString().Trim();
        }

        // Solo pide arrancar oculto (/onboot, /tray), sin nada más que hacer.
        public bool OnlyStartsHidden
        {
            get { return (OnBoot || Tray) && string.IsNullOrEmpty(Url) && !StartQueue && !HasFiles; }
        }

        // Llega del menú contextual del Explorador (/urlfile o /linksfile).
        public bool HasFiles
        {
            get { return !string.IsNullOrEmpty(UrlFile) || !string.IsNullOrEmpty(LinksFile); }
        }

        // Las rutas de archivo, absolutas: son relativas a donde se ejecutó el comando, no a la instancia abierta.
        public void ResolvePaths()
        {
            if (!string.IsNullOrEmpty(Path)) { try { Path = System.IO.Path.GetFullPath(Path); } catch { } }
            if (!string.IsNullOrEmpty(UrlFile)) { try { UrlFile = System.IO.Path.GetFullPath(UrlFile); } catch { } }
            if (!string.IsNullOrEmpty(LinksFile)) { try { LinksFile = System.IO.Path.GetFullPath(LinksFile); } catch { } }
        }
    }

    static class Program
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int dwProcessId);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int dwProcessId);
        const int ASFW_ANY = -1;
        const int ATTACH_PARENT_PROCESS = -1;

        [STAThread]
        static int Main(string[] args)
        {
            // Un error sin capturar en un hilo de fondo cierra el proceso: al menos queda escrito en crash.log.
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { WriteCrashLog(e.ExceptionObject as Exception); };
            DownloadEngine.ConfigureNetwork();
            CleanOldCopies();

            // Modo host de mensajería nativa (lo lanza el navegador).
            if (IsNativeHost(args))
                return NativeHost.Run();

            CliArgs cli = CliArgs.Parse(args);
            // Carpeta y archivos relativos: relativos a donde se ejecutó el comando, no a la instancia abierta.
            cli.ResolvePaths();

            if (cli.Help)
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
                Console.WriteLine();
                Console.WriteLine("IDMClone - gestor de descargas");
                Console.WriteLine("Uso (como IDMan.exe):");
                Console.WriteLine("  idmclone /d URL [/p carpeta] [/f nombre] [/q] [/h] [/n] [/a]");
                Console.WriteLine("  idmclone /s                 inicia la cola principal");
                Console.WriteLine();
                Console.WriteLine("  /d URL     descargar               /p carpeta   dónde guardar");
                Console.WriteLine("  /f nombre  nombre de archivo       /a           solo añadir a la cola");
                Console.WriteLine("  /n         silencioso (sin preguntas ni ventanas)");
                Console.WriteLine("  /q         salir de IDMClone al terminar la descarga");
                Console.WriteLine("  /h         colgar la conexión al terminar (se acepta, sin efecto)");
                Console.WriteLine("  /onboot    arranque con Windows (oculto en la bandeja)");
                Console.WriteLine("  /console   descargar en esta consola, sin ventana (propio de IDMClone)");
                Console.WriteLine("  /urlfile archivo.url    descargar la dirección de un acceso directo de Internet");
                Console.WriteLine("  /linksfile archivo      abrir la lista de enlaces de un .txt, .htm o .html");
                return 0;
            }

            // Modo consola: descarga directa sin GUI.
            if (cli.Console && !string.IsNullOrEmpty(cli.Url))
                return ConsoleDownload(cli);

            // Instancia única: si ya hay una GUI, enviarle el comando.
            bool createdNew;
            Mutex mutex = new Mutex(true, Ipc.MutexName, out createdNew);
            if (!createdNew)
            {
                if (cli.OnlyStartsHidden) return 0; // ya está abierta: nada que hacer
                string command = cli.ToCommandLine();
                if (string.IsNullOrEmpty(command)) command = "/show";
                // Que la instancia abierta pueda poner delante sus ventanas (lista de enlaces, avisos): la orden
                // llega a menudo del Explorador, que es quien tiene el primer plano.
                AllowSetForegroundWindow(ASFW_ANY);
                // Varios archivos elegidos a la vez en el Explorador lanzan un proceso por archivo: si IDMClone
                // estaba cerrado, el primero tarda en abrir su canal y los demás reintentan hasta que conecta.
                DateTime until = DateTime.UtcNow.AddSeconds(cli.HasFiles ? 20 : 0);
                while (true)
                {
                    bool connected;
                    string reply = Ipc.Request(command, 1500, 15000, Ipc.PipeName, out connected);
                    if (connected || DateTime.UtcNow >= until)
                        return reply != null && reply.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0 ? 0 : 1;
                    Thread.Sleep(300);
                }
            }

            try
            {
                // Errores en el hilo de la interfaz: se anotan y se avisan sin cerrar IDMClone.
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    WriteCrashLog(e.Exception);
                    // Un solo aviso a la vez: si el error se repite (un temporizador), no se apilan ventanas.
                    if (_showingError) return;
                    _showingError = true;
                    try { MessageBox.Show(e.Exception.Message, "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    catch { }
                    finally { _showingError = false; }
                };
                try { Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException); }
                catch (InvalidOperationException) { } // ya había ventanas en este hilo: se queda el modo por defecto
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                AppConfig cfg = AppConfig.Load();
                if (!string.IsNullOrEmpty(cfg.Language)) Lang.Load(cfg.Language);
                // El instalador (también /update) escribe el menú del Explorador en el idioma de Windows: aquí, en el de IDMClone.
                ShellMenu.Relabel(Application.ExecutablePath);
                if (cfg.SpeedLimiterOnAtStart) cfg.SpeedLimiterOn = true; // Limitador: "Activar siempre al iniciar"
                DownloadManager mgr = new DownloadManager(cfg);
                mgr.Queues = QueueSet.Load();   // colas de descarga (principal, sincronización y propias)
                mgr.PersistQueues = true;
                Schedule sched = mgr.Schedule;
                mgr.LoadList();
                // Carpetas de categorías al idioma de IDMClone (las de una versión anterior estaban en inglés); antes de la
                // ventana y de las colas, sin ninguna descarga en marcha. Lo que no se pueda renombrar se reintenta otro día.
                bool foldersChanged;
                CategoryFolders.Sync(cfg, mgr, null, out foldersChanged);
                if (foldersChanged) cfg.Save();

                Updater.CleanFolder(Updater.DownloadFolder); // instaladores de actualizaciones ya hechas
                MainForm form = new MainForm(mgr, cfg, sched);
                if (cli.OnBoot || cli.Tray || (cli.Silent && (!string.IsNullOrEmpty(cli.Url) || cli.HasFiles))) form.StartHidden = true;

                // Avisar si algún archivo de datos estaba dañado (se recuperó o se apartó).
                string warn = JoinWarnings(AppConfig.LoadWarning, mgr.LoadWarning);
                if (warn != null)
                    form.Shown += delegate { Msg.Show(form, warn, "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); };

                // Servidor IPC para recibir órdenes de futuras instancias y del host nativo.
                // El handle de la ventana tiene que existir antes: las órdenes se pasan a su hilo.
                IntPtr handle = form.Handle;
                GC.KeepAlive(handle);
                Ipc.StartServer(delegate(string c) { return form.HandleCommand(c); });
                CookieBridge.Start(); // canal con la extensión para las cookies del Grabber (inicio de sesión manual)

                // Lo pedido en la línea de comandos (URL, /s, /q...), igual que si llegara de otra instancia.
                if (!string.IsNullOrEmpty(cli.Url) || cli.StartQueue || cli.HasFiles) form.ApplyCli(cli);
                foreach (QueueDef q in mgr.Queues.Queues) if (q.Schedule.StartAtStartup) mgr.StartQueue(q.Id);

                Application.Run(form);
                return 0;
            }
            finally
            {
                GC.KeepAlive(mutex);
                try { mutex.ReleaseMutex(); } catch { }
            }
        }

        static bool _showingError;
        static string _lastCrash;
        static DateTime _lastCrashAt;

        // Añade el error a crash.log, en la carpeta de datos (%APPDATA%\IDMClone o IDMCLONE_HOME).
        // El mismo error repetido en menos de un minuto se anota una sola vez.
        static void WriteCrashLog(Exception ex)
        {
            try
            {
                string text = ex != null ? ex.ToString() : null;
                lock (typeof(Program))
                {
                    if (text != null && text == _lastCrash && DateTime.UtcNow - _lastCrashAt < TimeSpan.FromMinutes(1)) return;
                    _lastCrash = text; _lastCrashAt = DateTime.UtcNow;
                }
                Directory.CreateDirectory(AppConfig.Dir);
                File.AppendAllText(Path.Combine(AppConfig.Dir, "crash.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + Thread.CurrentThread.Name + "] " + (ex != null ? ex.ToString() : "(error desconocido)") + "\r\n\r\n",
                    new UTF8Encoding(false));
            }
            catch { }
        }

        // El instalador renombra a "*.old-<n>" los archivos que estaban en uso al actualizar: se borran aquí.
        static void CleanOldCopies()
        {
            try
            {
                foreach (string f in Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "*.old-*"))
                    try { File.Delete(f); } catch { }
            }
            catch { }
        }

        // Chrome/Edge/Brave/Opera/Vivaldi lanzan el host con "chrome-extension://<id>/";
        // Firefox, con la ruta de su manifiesto (.json) y el id de la extensión.
        public static bool IsNativeHost(string[] args)
        {
            if (args == null) return false;
            foreach (string a in args)
            {
                if (a == null) continue;
                if (a == "/nativehost" || a == "--nativehost") return true;
                if (a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)) return true;
                if (a.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase)) return true;
            }
            if (args.Length >= 1 && args[0] != null && args[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(args[0]))
                return true;
            return false;
        }

        static string JoinWarnings(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return string.IsNullOrEmpty(b) ? null : b;
            if (string.IsNullOrEmpty(b)) return a;
            return a + "\n\n" + b;
        }

        static int ConsoleDownload(CliArgs cli)
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            AppConfig cfg = AppConfig.Load();
            // Modo consola: descarga independiente. No lee ni escribe la lista de la GUI.
            DownloadManager mgr = new DownloadManager(cfg);
            mgr.Persist = false;
            DownloadItem it;
            try { it = mgr.Add(cli.Url, cli.Path, cli.FileName, true); }
            catch (Exception ex) { Console.WriteLine("Error: " + ex.Message); return 1; }

            Console.WriteLine("Descargando: " + it.Url);
            int lastLen = 0;
            while (it.Status != DownloadStatus.Complete && it.Status != DownloadStatus.Error)
            {
                mgr.Tick();
                long done = it.Downloaded;
                string line = string.Format("  {0}  {1} / {2}  {3}  conx:{4}   ",
                    Fmt.Percent(done, it.Size), Fmt.Size(done), Fmt.Size(it.Size),
                    Fmt.Speed(it.SpeedBps), it.ActiveConnections);
                if (line.Length < lastLen) line = line.PadRight(lastLen);
                lastLen = line.Length;
                Console.Write("\r" + line);
                Thread.Sleep(500);
            }
            Console.WriteLine();
            if (it.Status == DownloadStatus.Complete) { Console.WriteLine("Completado: " + it.FilePath); return 0; }
            Console.WriteLine("Error: " + it.Error); return 1;
        }
    }
}
