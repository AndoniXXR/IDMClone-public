using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace IDMClone.Setup
{
    public class InstallOptions
    {
        public string Dir;                 // carpeta del programa
        public bool DesktopShortcut = true;
        public bool StartMenuShortcut = true;
        public bool AutoStart = true;      // "Iniciar IDMClone al iniciar Windows" (como IDM, activado)
        public bool KeepUserData = true;   // al desinstalar: conservar la lista y la configuración
        public bool ExplorerMenu = true;   // menú contextual del Explorador (como la extensión del shell de IDM)
    }

    // Lo que hace el instalador (y el desinstalador), sin ventanas: lo usan el asistente, el modo
    // silencioso (/S) y las pruebas. Todo es por usuario (HKCU y carpetas del usuario): no pide administrador.
    public static class Installer
    {
        public const string AppName = "IDMClone";
        public const string ExeName = "IDMClone.exe";
        public const string UninstallerName = "Uninstall.exe";
        public const string HostName = "com.idmclone.host";
        public const string ChromeId = "jolfcgooaockjhnalagfilpljkloallc";   // fijo por la "key" del manifiesto
        public const string FirefoxId = "idmclone@idmclone.local";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\IDMClone";
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string ClassesKey = @"Software\Classes";

        // Mismas claves que browser\install.ps1: cada navegador de Chromium lee la suya.
        public static readonly string[] ChromiumHostKeys =
        {
            @"Software\Google\Chrome\NativeMessagingHosts",
            @"Software\Chromium\NativeMessagingHosts",
            @"Software\Microsoft\Edge\NativeMessagingHosts",
            @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts",
            @"Software\Opera Software\NativeMessagingHosts",
            @"Software\Vivaldi\NativeMessagingHosts",
            @"Software\Yandex\YandexBrowser\NativeMessagingHosts",
            @"Software\CocCoc\Browser\NativeMessagingHosts",
        };
        public const string FirefoxHostKey = @"Software\Mozilla\NativeMessagingHosts";

        // Para las pruebas: todo el registro bajo HKCU\<RegRoot> y accesos directos en carpetas propias.
        public static string RegRoot = "";
        public static string StartMenuDirOverride, DesktopDirOverride;
        public static string PayloadOverride;          // .zip a usar en vez del incrustado
        public static string SelfPathOverride;         // "este" instalador (para copiarlo como Uninstall.exe)
        public static bool NoSelfDelete;               // las pruebas no lanzan el borrado diferido

        static string K(string path) { return string.IsNullOrEmpty(RegRoot) ? path : RegRoot + "\\" + path; }

        public static string DefaultDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\IDMClone"); }
        }

        public static string StartMenuDir
        {
            get { return StartMenuDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName); }
        }

        public static string DesktopDir
        {
            get { return DesktopDirOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
        }

        public static string SelfPath { get { return SelfPathOverride ?? Assembly.GetExecutingAssembly().Location; } }

        // ¿Se está ejecutando el Uninstall.exe que copia Install en la carpeta del programa?
        public static bool RunningAsUninstaller
        {
            get { return string.Equals(Path.GetFileName(SelfPath), UninstallerName, StringComparison.OrdinalIgnoreCase); }
        }

        public static string Version
        {
            get { Version v = Assembly.GetExecutingAssembly().GetName().Version; return v.Major + "." + v.Minor + "." + v.Build; }
        }

        // Carpeta donde está instalado (según "Aplicaciones instaladas"), o null.
        public static string InstalledDir()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(K(UninstallKey)))
                {
                    string d = k != null ? k.GetValue("InstallLocation") as string : null;
                    return !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, ExeName)) ? d : null;
                }
            }
            catch { return null; }
        }

        // Carpeta donde instalar: la elegida si está vacía o ya es de IDMClone; si tiene otras cosas, su subcarpeta IDMClone.
        public static string OwnFolder(string dir)
        {
            try
            {
                if (!Directory.Exists(dir) || Directory.GetFileSystemEntries(dir).Length == 0) return dir;
                if (File.Exists(Path.Combine(dir, ExeName))) return dir; // "Uninstall.exe" solo puede ser de otro programa
                return Path.Combine(dir, AppName);
            }
            catch { return dir; }
        }

        static Stream OpenPayload()
        {
            if (PayloadOverride != null) return File.OpenRead(PayloadOverride);
            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("IDMClone.payload.zip");
            if (s == null) throw new InvalidOperationException("Este instalador no contiene los archivos de IDMClone (compílalo con build-setup.ps1).");
            return s;
        }

        // Texto de un archivo del paquete (p. ej. el idioma inglés para el propio instalador), o null.
        public static string PayloadText(string name)
        {
            try
            {
                using (Stream s = OpenPayload())
                using (ZipArchive z = new ZipArchive(s, ZipArchiveMode.Read))
                {
                    ZipArchiveEntry e = z.GetEntry(name);
                    if (e == null) return null;
                    using (StreamReader r = new StreamReader(e.Open(), Encoding.UTF8)) return r.ReadToEnd();
                }
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ instalar

        public static void Install(InstallOptions o, Action<int, string> progress)
        {
            if (progress == null) progress = delegate { };
            string dir = Path.GetFullPath(o.Dir);
            Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, ExeName);

            progress(5, "Cerrando IDMClone si está abierto...");
            CloseRunning(dir, false);

            progress(10, "Copiando archivos...");
            using (Stream s = OpenPayload())
            using (ZipArchive z = new ZipArchive(s, ZipArchiveMode.Read))
            {
                int n = 0, total = Math.Max(1, z.Entries.Count);
                foreach (ZipArchiveEntry e in z.Entries)
                {
                    n++;
                    if (string.IsNullOrEmpty(e.Name)) continue; // carpeta
                    string target = Path.GetFullPath(Path.Combine(dir, e.FullName.Replace('/', '\\')));
                    if (!target.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) continue; // nada fuera de la carpeta
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    WriteFile(target, e);
                    progress(10 + 60 * n / total, "Copiando " + e.FullName.Replace('/', '\\'));
                }
            }

            WriteGuide(dir);

            progress(72, "Registrando IDMClone en los navegadores...");
            string browserDir = Path.Combine(dir, "browser");
            Directory.CreateDirectory(browserDir);
            string chromiumManifest = Path.Combine(browserDir, HostName + ".json");
            string firefoxManifest = Path.Combine(browserDir, HostName + ".firefox.json");
            File.WriteAllText(chromiumManifest, HostManifest(exe, "allowed_origins", ChromiumOrigins()), new UTF8Encoding(false));
            File.WriteAllText(firefoxManifest, HostManifest(exe, "allowed_extensions", new string[] { FirefoxId }), new UTF8Encoding(false));
            foreach (string b in ChromiumHostKeys) SetDefault(K(b + "\\" + HostName), chromiumManifest);
            SetDefault(K(FirefoxHostKey + "\\" + HostName), firefoxManifest);
            // Publicada en la tienda: el navegador la descarga solo y pide activarla ("Se necesita acción").
            SetExternal(StoreIds.ChromeExternalKey, StoreIds.ChromeWebStore, StoreIds.ChromeUpdateUrl);
            SetExternal(StoreIds.EdgeExternalKey, StoreIds.EdgeAddons, StoreIds.EdgeUpdateUrl);

            progress(80, "Creando accesos directos...");
            string uninstaller = Path.Combine(dir, UninstallerName);
            if (!string.Equals(Path.GetFullPath(SelfPath), uninstaller, StringComparison.OrdinalIgnoreCase))
                CopyReplacing(SelfPath, uninstaller);
            if (o.StartMenuShortcut)
            {
                Directory.CreateDirectory(StartMenuDir);
                ShellShortcut.Create(Path.Combine(StartMenuDir, "IDMClone.lnk"), exe, "", dir, "Gestor de descargas IDMClone");
                ShellShortcut.Create(Path.Combine(StartMenuDir, "Desinstalar IDMClone.lnk"), uninstaller, "", dir, "Desinstalar IDMClone");
            }
            string desk = Path.Combine(DesktopDir, "IDMClone.lnk");
            if (o.DesktopShortcut) ShellShortcut.Create(desk, exe, "", dir, "Gestor de descargas IDMClone");
            else if (File.Exists(desk) && SameTarget(desk, exe)) TryDelete(desk);

            progress(88, "Configurando el inicio con Windows...");
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(K(RunKey)))
            {
                if (o.AutoStart) k.SetValue(Startup.ValueName, Startup.Command(exe));
                else
                {
                    string v = k.GetValue(Startup.ValueName) as string;
                    if (v != null && v.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0) k.DeleteValue(Startup.ValueName, false);
                }
            }

            progress(91, "Añadiendo IDMClone al menú contextual del Explorador...");
            // "Descargar con IDMClone" en los .url y "Descargar los enlaces con IDMClone" en .txt/.htm/.html
            // (por usuario, en HKCU\Software\Classes; se puede quitar en Opciones > General).
            // Opcional: si el registro falla (directivas, antivirus), la instalación sigue.
            try { if (o.ExplorerMenu) ShellMenu.Register(K(ClassesKey), exe); else ShellMenu.Unregister(K(ClassesKey), exe); }
            catch { }

            progress(94, "Registrando en Aplicaciones instaladas...");
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(K(UninstallKey)))
            {
                k.SetValue("DisplayName", "IDMClone");
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "IDMClone");
                k.SetValue("DisplayIcon", exe + ",0");
                k.SetValue("InstallLocation", dir);
                k.SetValue("UninstallString", "\"" + uninstaller + "\"");
                k.SetValue("QuietUninstallString", "\"" + uninstaller + "\" /S");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("EstimatedSize", (int)(FolderSize(dir) / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                // Menú del Explorador quitado a propósito (casilla del asistente o /noexplorermenu): lo mira /update.
                if (o.ExplorerMenu) k.DeleteValue(ShellMenu.OffValue, false); else k.SetValue(ShellMenu.OffValue, 1, RegistryValueKind.DWord);
            }
            progress(100, "IDMClone se ha instalado.");
        }

        // Actualización sin asistente (/update): si ya está instalado en o.Dir, lo que el usuario dejó la otra vez.
        // Si quitó el acceso directo del escritorio o el inicio con Windows, la actualización no los vuelve a poner.
        public static void KeepChoices(InstallOptions o)
        {
            string exe = Path.Combine(Path.GetFullPath(o.Dir), ExeName);
            if (!File.Exists(exe)) return; // instalación nueva: lo de siempre
            string desk = Path.Combine(DesktopDir, "IDMClone.lnk");
            o.DesktopShortcut = o.DesktopShortcut && File.Exists(desk) && SameTarget(desk, exe); // /nodesktop sigue mandando
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(K(RunKey)))
                {
                    string v = k != null ? k.GetValue(Startup.ValueName) as string : null;
                    o.AutoStart = o.AutoStart && v != null && v.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { }
            o.StartMenuShortcut = Directory.Exists(StartMenuDir);
            // Menú contextual del Explorador: si el usuario lo quitó (Opciones > General o /noexplorermenu), la actualización
            // no lo vuelve a poner. Quien viene de una versión sin este menú (sin la marca) lo recibe, como una instalación nueva.
            o.ExplorerMenu = o.ExplorerMenu && !ShellMenu.IsMarkedOff(K(UninstallKey));
        }

        // Guía para añadir la extensión (browser\guia.html) con la ruta real y el navegador predeterminado.
        public static string GuidePath(string dir) { return Path.Combine(dir, @"browser\guia.html"); }
        public static string ExtensionDir(string dir) { return Path.Combine(dir, @"browser\extension"); }

        static void WriteGuide(string dir)
        {
            string t = PayloadText("browser/guia.html");
            if (t == null) return;
            string ext = ExtensionDir(dir).Replace("\\", "\\\\").Replace("\"", "\\\"");
            t = t.Replace("{{RUTA}}", ext).Replace("{{NAVEGADOR}}", DefaultBrowserId())
                 .Replace("{{TIENDA_CHROME}}", string.IsNullOrEmpty(StoreIds.ChromeWebStore) ? "" : "1")
                 .Replace("{{TIENDA_EDGE}}", string.IsNullOrEmpty(StoreIds.EdgeAddons) ? "" : "1");
            File.WriteAllText(GuidePath(dir), t, new UTF8Encoding(false));
        }

        // Navegador predeterminado de Windows ("brave", "chrome", "edge", "opera", "vivaldi", "firefox" o "").
        public static string DefaultBrowserId()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice"))
                {
                    string id = ((k != null ? k.GetValue("ProgId") as string : null) ?? "").ToLowerInvariant();
                    if (id.StartsWith("brave")) return "brave";
                    if (id.StartsWith("msedge")) return "edge";
                    if (id.StartsWith("chrome")) return "chrome";
                    if (id.StartsWith("firefox")) return "firefox";
                    if (id.StartsWith("opera")) return "opera";
                    if (id.StartsWith("vivaldi")) return "vivaldi";
                }
            }
            catch { }
            return "";
        }

        // La extensión de desarrollo y, si ya están publicadas, las de las tiendas (cada una con su ID).
        public static string[] ChromiumOrigins()
        {
            List<string> r = new List<string>();
            foreach (string id in new string[] { ChromeId, StoreIds.ChromeWebStore, StoreIds.EdgeAddons })
                if (!string.IsNullOrEmpty(id) && !r.Contains("chrome-extension://" + id + "/")) r.Add("chrome-extension://" + id + "/");
            return r.ToArray();
        }

        static string HostManifest(string exe, string listName, string[] allowed)
        {
            List<string> q = new List<string>();
            foreach (string a in allowed) q.Add(Ipc.JsonQuote(a));
            return "{\r\n" +
                   "  \"name\": \"" + HostName + "\",\r\n" +
                   "  \"description\": \"IDMClone native messaging host\",\r\n" +
                   "  \"path\": " + Ipc.JsonQuote(exe) + ",\r\n" +
                   "  \"type\": \"stdio\",\r\n" +
                   "  \"" + listName + "\": [ " + string.Join(", ", q.ToArray()) + " ]\r\n" +
                   "}\r\n";
        }

        static void SetExternal(string baseKey, string id, string updateUrl)
        {
            if (string.IsNullOrEmpty(id)) return;
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(K(baseKey + "\\" + id))) k.SetValue("update_url", updateUrl);
        }

        static void RemoveExternal(string baseKey, string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            try { Registry.CurrentUser.DeleteSubKeyTree(K(baseKey + "\\" + id), false); } catch { }
        }

        static void SetDefault(string key, string value)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(key)) k.SetValue("", value);
        }

        // Escribe el archivo; si está en uso (un host abierto por el navegador), aparta el viejo con otro
        // nombre (Windows lo permite) y lo borra IDMClone la próxima vez que arranque.
        static void WriteFile(string target, ZipArchiveEntry e)
        {
            string tmp = target + ".new";
            using (Stream src = e.Open())
            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write)) src.CopyTo(fs);
            Replace(tmp, target);
        }

        static void CopyReplacing(string from, string to)
        {
            string tmp = to + ".new";
            File.Copy(from, tmp, true);
            Replace(tmp, to);
        }

        static void Replace(string tmp, string target)
        {
            if (File.Exists(target))
            {
                try { File.Delete(target); }
                catch (IOException) { File.Move(target, target + ".old-" + DateTime.Now.Ticks); }
                catch (UnauthorizedAccessException) { File.Move(target, target + ".old-" + DateTime.Now.Ticks); }
            }
            File.Move(tmp, target);
        }

        static long FolderSize(string dir)
        {
            long t = 0;
            try { foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) try { t += new FileInfo(f).Length; } catch { } } catch { }
            return t;
        }

        // Pide a IDMClone que se cierre (orden /exit) y espera. Al desinstalar, lo que siga abierto de esa
        // carpeta (también el host que tenga abierto un navegador) se cierra a la fuerza.
        public static void CloseRunning(string dir, bool force)
        {
            try { Ipc.Request("/exit", 800, 3000); } catch { }
            string exe = Path.Combine(dir, ExeName);
            DateTime until = DateTime.Now.AddSeconds(8);
            while (DateTime.Now < until && RunningFrom(exe).Count > 0) Thread.Sleep(200);
            if (!force) return;
            foreach (Process p in RunningFrom(exe)) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
        }

        static List<Process> RunningFrom(string exe)
        {
            List<Process> r = new List<Process>();
            foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            {
                try { if (string.Equals(p.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase)) r.Add(p); }
                catch { }
            }
            return r;
        }

        // ------------------------------------------------------------------ desinstalar

        public static void Uninstall(InstallOptions o, Action<int, string> progress)
        {
            if (progress == null) progress = delegate { };
            string dir = o.Dir ?? InstalledDir() ?? Path.GetDirectoryName(SelfPath);
            dir = Path.GetFullPath(dir);
            string exe = Path.Combine(dir, ExeName);
            string browserDir = Path.Combine(dir, "browser");
            // Sin IDMClone en esa carpeta no se toca nada (p. ej. "/uninstall /S" lanzado desde Descargas).
            // "Uninstall.exe" es un nombre corriente: solo cuenta si es este mismo desinstalador.
            string ownUninstaller = Path.Combine(dir, UninstallerName);
            if (!File.Exists(exe) && !string.Equals(Path.GetFullPath(SelfPath), ownUninstaller, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("IDMClone no está instalado en " + dir);

            progress(5, "Cerrando IDMClone...");
            CloseRunning(dir, true);

            progress(30, "Quitando IDMClone de los navegadores...");
            // Solo las claves que apuntan a esta instalación (una copia de desarrollo en otra carpeta se respeta).
            foreach (string b in ChromiumHostKeys) RemoveIfPointsInto(K(b), HostName, browserDir);
            RemoveIfPointsInto(K(FirefoxHostKey), HostName, browserDir);
            RemoveExternal(StoreIds.ChromeExternalKey, StoreIds.ChromeWebStore);
            RemoveExternal(StoreIds.EdgeExternalKey, StoreIds.EdgeAddons);

            progress(45, "Quitando los accesos directos y el inicio con Windows...");
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(K(RunKey), true))
            {
                string v = k != null ? k.GetValue(Startup.ValueName) as string : null;
                if (v != null && v.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0) k.DeleteValue(Startup.ValueName, false);
            }
            if (Directory.Exists(StartMenuDir)) { try { Directory.Delete(StartMenuDir, true); } catch { } }
            string desk = Path.Combine(DesktopDir, "IDMClone.lnk");
            if (File.Exists(desk) && SameTarget(desk, exe)) TryDelete(desk);
            try { Registry.CurrentUser.DeleteSubKeyTree(K(UninstallKey), false); } catch { }
            // Menú contextual del Explorador: solo lo que lanza un programa de esta carpeta.
            ShellMenu.UnregisterFolder(K(ClassesKey), dir);

            if (!o.KeepUserData)
            {
                progress(60, "Borrando la lista de descargas y la configuración...");
                try { if (Directory.Exists(AppConfig.Dir)) Directory.Delete(AppConfig.Dir, true); } catch { }
            }

            progress(75, "Borrando los archivos del programa...");
            string self = Path.GetFullPath(SelfPath);
            string root = dir.TrimEnd('\\') + "\\";
            // Solo lo que instaló IDMClone (y lo que apartó al actualizar): lo demás de la carpeta es del usuario.
            List<string> dirs = new List<string>();
            foreach (KeyValuePair<string, long> own in OwnFiles())
            {
                string f = Path.GetFullPath(Path.Combine(dir, own.Key));
                if (!f.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // nada fuera de la carpeta
                // Un archivo del paquete solo si sigue siendo el nuestro (mismo tamaño): un README.md o un .lng
                // del usuario con el mismo nombre se queda.
                foreach (string v in WithLeftovers(f))
                {
                    if (string.Equals(v, self, StringComparison.OrdinalIgnoreCase)) continue;
                    if (v == f && own.Value >= 0 && !SameLength(f, own.Value)) continue;
                    TryDelete(v);
                }
                for (string d = Path.GetDirectoryName(f); d.Length > root.Length; d = Path.GetDirectoryName(d))
                    if (!dirs.Contains(d)) dirs.Add(d);
            }
            // Sus carpetas, de la más honda a la más alta, solo si se han quedado vacías.
            dirs.Sort(delegate(string a, string b) { return b.Length.CompareTo(a.Length); });
            foreach (string d in dirs) DeleteIfEmpty(d);
            // El desinstalador no puede borrarse a sí mismo mientras se ejecuta: lo hace cmd al cerrarse
            // (solo el Uninstall.exe de la instalación; un IDMClone-Setup.exe que esté ahí no se toca).
            if (string.Equals(self, ownUninstaller, StringComparison.OrdinalIgnoreCase) && !NoSelfDelete) ScheduleDelete(self, dir);
            else DeleteIfEmpty(dir);
            progress(100, "IDMClone se ha desinstalado.");
        }

        // Archivos de IDMClone (rutas relativas a la carpeta) y su tamaño en el paquete (-1: cualquiera):
        // los del paquete más los que crea Install.
        static Dictionary<string, long> OwnFiles()
        {
            Dictionary<string, long> r = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            r[ExeName] = -1; r[UninstallerName] = -1;
            r[@"browser\" + HostName + ".json"] = -1; r[@"browser\" + HostName + ".firefox.json"] = -1; r[@"browser\guia.html"] = -1;
            try
            {
                using (Stream s = OpenPayload())
                using (ZipArchive z = new ZipArchive(s, ZipArchiveMode.Read))
                    foreach (ZipArchiveEntry e in z.Entries)
                    {
                        string rel = e.FullName.Replace('/', '\\');
                        if (!string.IsNullOrEmpty(e.Name) && !r.ContainsKey(rel)) r[rel] = e.Length;
                    }
            }
            catch { } // sin paquete: solo los nombres fijos
            return r;
        }

        static bool SameLength(string f, long length)
        {
            try { return new FileInfo(f).Length == length; } catch { return false; }
        }

        // El archivo, su ".new" a medio copiar y los "*.old-<n>" que apartó una actualización.
        static List<string> WithLeftovers(string f)
        {
            List<string> r = new List<string>();
            r.Add(f); r.Add(f + ".new");
            try { r.AddRange(Directory.GetFiles(Path.GetDirectoryName(f), Path.GetFileName(f) + ".old-*")); } catch { }
            return r;
        }

        static void DeleteIfEmpty(string d)
        {
            try { if (Directory.Exists(d) && Directory.GetFileSystemEntries(d).Length == 0) Directory.Delete(d); } catch { }
        }

        static void RemoveIfPointsInto(string parent, string name, string dir)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(parent + "\\" + name))
                {
                    string v = k != null ? k.GetValue("") as string : null;
                    if (v == null || !v.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return;
                }
                Registry.CurrentUser.DeleteSubKeyTree(parent + "\\" + name, false);
            }
            catch { }
        }

        static void TryDelete(string f) { try { File.Delete(f); } catch { } }

        static void ScheduleDelete(string self, string dir)
        {
            // Reintenta cada 2 s durante un minuto: el desinstalador se borra en cuanto se cierra, y la carpeta
            // solo si se ha quedado vacía (rd sin /s: lo que el usuario guardó allí no se toca).
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                "/d /c for /l %i in (1,1,30) do (ping 127.0.0.1 -n 3 > nul & del /f /q \"" + self + "\" 2> nul & if not exist \"" + self + "\" (rd \"" + dir + "\" 2> nul & exit))");
            psi.CreateNoWindow = true; psi.UseShellExecute = false;
            psi.WorkingDirectory = Path.GetTempPath();
            try { Process.Start(psi); } catch { }
        }

        static bool SameTarget(string lnk, string exe)
        {
            string t = ShellShortcut.Target(lnk);
            return t != null && string.Equals(Path.GetFullPath(t), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase);
        }

        // Navegadores instalados (para la página final del asistente).
        public static List<string> InstalledBrowsers()
        {
            List<string> r = new List<string>();
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[,] known =
            {
                { "Google Chrome", @"Google\Chrome\Application\chrome.exe" },
                { "Microsoft Edge", @"Microsoft\Edge\Application\msedge.exe" },
                { "Brave", @"BraveSoftware\Brave-Browser\Application\brave.exe" },
                { "Opera", @"Programs\Opera\opera.exe" },
                { "Vivaldi", @"Vivaldi\Application\vivaldi.exe" },
                { "Mozilla Firefox", @"Mozilla Firefox\firefox.exe" },
            };
            for (int i = 0; i < known.GetLength(0); i++)
                foreach (string root in new string[] { pf, pf86, la })
                    if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, known[i, 1]))) { r.Add(known[i, 0]); break; }
            return r;
        }
    }

    // Accesos directos (.lnk) con la interfaz IShellLink de Windows.
    public static class ShellShortcut
    {
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
        interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        public static void Create(string lnk, string target, string args, string workDir, string description)
        {
            IShellLinkW l = (IShellLinkW)new ShellLink();
            try
            {
                l.SetPath(target);
                l.SetArguments(args ?? "");
                l.SetWorkingDirectory(workDir ?? "");
                l.SetDescription(description ?? "");
                l.SetIconLocation(target, 0);
                ((IPersistFile)l).Save(lnk, true);
            }
            finally { Marshal.FinalReleaseComObject(l); }
        }

        public static string Target(string lnk)
        {
            IShellLinkW l = (IShellLinkW)new ShellLink();
            try
            {
                ((IPersistFile)l).Load(lnk, 0);
                StringBuilder sb = new StringBuilder(1024);
                l.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                return sb.ToString();
            }
            catch { return null; }
            finally { Marshal.FinalReleaseComObject(l); }
        }
    }
}
