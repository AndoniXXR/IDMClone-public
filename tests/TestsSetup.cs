using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using IDMClone.Setup;

namespace IDMClone.Tests
{
    // Instalador y desinstalador. Todo va a un registro de pruebas (HKCU\Software\IDMCloneTests\<id>) y a
    // carpetas temporales: no toca los navegadores, los accesos directos ni el arranque de verdad.
    public static partial class TestMain
    {
        static string SetupRoot;

        static void RunSetup()
        {
            string id = Guid.NewGuid().ToString("N").Substring(0, 8);
            SetupRoot = Path.Combine(Home, "setup-" + id);
            Directory.CreateDirectory(SetupRoot);
            Installer.RegRoot = @"Software\IDMCloneTests\" + id;
            Installer.StartMenuDirOverride = Path.Combine(SetupRoot, @"menu\IDMClone");
            Installer.DesktopDirOverride = Path.Combine(SetupRoot, "escritorio");
            Directory.CreateDirectory(Installer.DesktopDirOverride);
            Installer.PayloadOverride = MakePayload(Path.Combine(SetupRoot, "payload.zip"));
            Installer.SelfPathOverride = Path.Combine(SetupRoot, "IDMClone-Setup.exe");
            File.Copy(Installer.PayloadOverride, Installer.SelfPathOverride);
            Installer.NoSelfDelete = true;
            // ID de tienda de prueba: como si la extensión ya estuviera publicada en las dos tiendas.
            StoreIds.ChromeWebStore = "aaaabbbbccccddddeeeeffffgggghhhh";
            StoreIds.EdgeAddons = "hhhhggggffffeeeeddddccccbbbbaaaa";
            try
            {
                Run("instalador: copia el programa, registra el host en los navegadores, accesos directos, inicio y desinstalador", TS_Install);
                Run("instalador: actualizar con IDMClone abierto (el archivo en uso se aparta y se sustituye)", TS_UpgradeLocked);
                Run("instalador: el asistente (licencia, carpeta, instalar y finalizar)", TS_Wizard);
                Run("desinstalador: quita todo lo suyo y respeta otra instalación y los datos si se pide", TS_Uninstall);
                Run("desinstalador: también puede borrar la lista de descargas y la configuración", TS_UninstallData);
                Run("ipc: la orden /exit cierra IDMClone (la usa el instalador)", TS_ExitCommand);
                Run("host: anota en qué navegador se conecta la extensión (para el ✓ del instalador)", TS_Seen);
            }
            finally
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(Installer.RegRoot, false); } catch { }
                try { if (Registry.CurrentUser.OpenSubKey(@"Software\IDMCloneTests") != null && Registry.CurrentUser.OpenSubKey(@"Software\IDMCloneTests").SubKeyCount == 0) Registry.CurrentUser.DeleteSubKey(@"Software\IDMCloneTests", false); } catch { }
                Installer.RegRoot = ""; Installer.StartMenuDirOverride = null; Installer.DesktopDirOverride = null;
                Installer.PayloadOverride = null; Installer.SelfPathOverride = null;
                StoreIds.ChromeWebStore = ""; StoreIds.EdgeAddons = "";
            }
        }

        // Paquete de prueba: un "IDMClone.exe" de verdad (copia de ping.exe, para poder dejarlo abierto),
        // la extensión, un idioma y la licencia.
        static string MakePayload(string zipPath)
        {
            string ping = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE");
            using (ZipArchive z = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                z.CreateEntryFromFile(ping, "IDMClone.exe");
                Entry(z, "browser/extension/manifest.json", "{\"name\":\"IDMClone Integration\"}");
                Entry(z, "browser/extension/background.js", "// fondo");
                Entry(z, "Languages/idmclone_en.lng", "[IDMClone]\nLANGUAGE=English\n[Textos]\n");
                Entry(z, "LICENCIA.txt", "Licencia de prueba");
                Entry(z, "browser/guia.html", "LANG={{IDIOMA}};RUTA={{RUTA}};NAV={{NAVEGADOR}};CWS={{TIENDA_CHROME}};EDGE={{TIENDA_EDGE}}");
                Entry(z, "../fuera.txt", "no debe salir de la carpeta");
            }
            return zipPath;
        }

        static void Entry(ZipArchive z, string name, string text)
        {
            using (StreamWriter w = new StreamWriter(z.CreateEntry(name).Open())) w.Write(text);
        }

        static string RegDefault(string key)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Installer.RegRoot + "\\" + key))
                return k != null ? k.GetValue("") as string : null;
        }

        static object RegValue(string key, string name)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Installer.RegRoot + "\\" + key))
                return k != null ? k.GetValue(name) : null;
        }

        static void TS_Install()
        {
            string dir = Path.Combine(SetupRoot, @"Programas\IDMClone");
            InstallOptions o = new InstallOptions(); o.Dir = dir;
            List<int> steps = new List<int>();
            Installer.Install(o, delegate(int pct, string msg) { steps.Add(pct); });
            string exe = Path.Combine(dir, "IDMClone.exe");
            Check(File.Exists(exe) && File.Exists(Path.Combine(dir, @"browser\extension\manifest.json")) && File.Exists(Path.Combine(dir, @"Languages\idmclone_en.lng")), "Faltan archivos");
            Check(!File.Exists(Path.Combine(SetupRoot, @"Programas\fuera.txt")), "Una entrada del paquete salió de la carpeta (../)");
            Check(steps.Count > 3 && steps[steps.Count - 1] == 100, "Progreso: " + string.Join(",", steps.ConvertAll(x => x.ToString()).ToArray()));
            // Manifiesto del host con la ruta instalada.
            string manifest = Path.Combine(dir, @"browser\com.idmclone.host.json");
            Dictionary<string, object> m = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(manifest));
            Check((string)m["path"] == exe && (string)m["name"] == "com.idmclone.host", "Manifiesto: " + m["path"]);
            System.Collections.ArrayList origins = (System.Collections.ArrayList)m["allowed_origins"];
            Check(origins.Count == 3 && origins[0].ToString() == "chrome-extension://jolfcgooaockjhnalagfilpljkloallc/"
                  && origins.Contains("chrome-extension://" + StoreIds.ChromeWebStore + "/") && origins.Contains("chrome-extension://" + StoreIds.EdgeAddons + "/"),
                  "Orígenes permitidos: " + string.Join(" ", (string[])origins.ToArray(typeof(string))));
            // Extensión externa: Chrome y Brave leen la clave de Chrome; Edge, la suya.
            Check((string)RegValue(StoreIds.ChromeExternalKey + "\\" + StoreIds.ChromeWebStore, "update_url") == StoreIds.ChromeUpdateUrl, "Clave externa de Chrome/Brave");
            Check((string)RegValue(StoreIds.EdgeExternalKey + "\\" + StoreIds.EdgeAddons, "update_url") == StoreIds.EdgeUpdateUrl, "Clave externa de Edge");
            // Guía con la ruta de la extensión (escapada para JavaScript) y las tiendas publicadas.
            string guide = File.ReadAllText(Installer.GuidePath(dir));
            string lang = IDMClone.Setup.SetupProgram.SetupLanguage(System.Globalization.CultureInfo.CurrentUICulture) ?? "es"; // la del instalador
            Check(guide.StartsWith("LANG=" + lang + ";RUTA=" + Installer.ExtensionDir(dir).Replace("\\", "\\\\") + ";NAV=") && guide.EndsWith("CWS=1;EDGE=1"), "Guía: " + guide);
            // En otro idioma (el instalador se muestra en el de Windows): la guía también.
            try
            {
                Installer.GuideLanguage = "zh";
                Installer.Install(o, null);
                Check(File.ReadAllText(Installer.GuidePath(dir)).StartsWith("LANG=zh;"), "Guía en chino: " + File.ReadAllText(Installer.GuidePath(dir)));
            }
            finally { Installer.GuideLanguage = null; }
            Check(File.ReadAllBytes(manifest)[0] == (byte)'{', "El manifiesto no debe llevar BOM");
            foreach (string b in Installer.ChromiumHostKeys)
                Check(RegDefault(b + @"\com.idmclone.host") == manifest, "Sin registrar en " + b);
            Check(RegDefault(@"Software\Mozilla\NativeMessagingHosts\com.idmclone.host") == Path.Combine(dir, @"browser\com.idmclone.host.firefox.json"), "Firefox");
            // Inicio con Windows, desinstalador y "Aplicaciones instaladas".
            Check((string)RegValue(Installer.RunKey, "IDMClone") == "\"" + exe + "\" /onboot", "Inicio con Windows: " + RegValue(Installer.RunKey, "IDMClone"));
            Check(File.Exists(Path.Combine(dir, "Uninstall.exe")), "No se copió Uninstall.exe");
            Check((string)RegValue(Installer.UninstallKey, "DisplayName") == "IDMClone" && (string)RegValue(Installer.UninstallKey, "InstallLocation") == dir, "Clave de desinstalación");
            Check(((string)RegValue(Installer.UninstallKey, "UninstallString")).Contains("Uninstall.exe"), "UninstallString");
            Check(Installer.InstalledDir() == dir, "InstalledDir: " + Installer.InstalledDir());
            // Accesos directos.
            Check(ShellShortcut.Target(Path.Combine(Installer.StartMenuDirOverride, "IDMClone.lnk")) == exe, "Menú Inicio");
            Check(ShellShortcut.Target(Path.Combine(Installer.StartMenuDirOverride, "Desinstalar IDMClone.lnk")) == Path.Combine(dir, "Uninstall.exe"), "Menú Inicio: desinstalar");
            Check(ShellShortcut.Target(Path.Combine(Installer.DesktopDirOverride, "IDMClone.lnk")) == exe, "Escritorio");
        }

        static void TS_UpgradeLocked()
        {
            string dir = Installer.InstalledDir();
            Check(dir != null, "Hace falta la instalación anterior");
            string exe = Path.Combine(dir, "IDMClone.exe");
            // El "IDMClone.exe" instalado es ping.exe: se deja abierto como lo dejaría un navegador con el host.
            ProcessStartInfo psi = new ProcessStartInfo(exe, "-n 60 127.0.0.1"); psi.CreateNoWindow = true; psi.UseShellExecute = false;
            Process p = Process.Start(psi);
            try
            {
                InstallOptions o = new InstallOptions(); o.Dir = dir; o.DesktopShortcut = false; o.AutoStart = false;
                Installer.Install(o, null);
                Check(File.Exists(exe), "Falta el programa tras actualizar");
                Check(Directory.GetFiles(dir, "IDMClone.exe.old-*").Length == 1, "El archivo en uso no se apartó");
                Check(!File.Exists(Path.Combine(Installer.DesktopDirOverride, "IDMClone.lnk")), "Sin acceso directo en el escritorio: debía quitarse");
                Check(RegValue(Installer.RunKey, "IDMClone") == null, "Sin inicio con Windows: debía quitarse");
            }
            finally { try { p.Kill(); p.WaitForExit(3000); } catch { } }
        }

        static void TS_Wizard()
        {
            string dir = Path.Combine(SetupRoot, @"Asistente\IDMClone");
            using (SetupForm f = new SetupForm())
            {
                f.Quiet = true;
                IntPtr h = f.Handle;
                Check(f.Page == 0, "Empieza en la bienvenida");
                f.GoNext();
                Check(f.Page == 1 && !f.NextButton.Enabled, "Licencia: Siguiente desactivado hasta aceptar");
                Check(f.LicenseText == "Licencia de prueba", "Texto de la licencia: " + f.LicenseText);
                f.AcceptLicense();
                Check(f.NextButton.Enabled, "Al aceptar se activa Siguiente");
                f.GoNext();
                Check(f.Page == 2 && f.NextButton.Text == "Instalar", "Carpeta: botón " + f.NextButton.Text);
                f.SetDir(dir); f.SetShortcuts(false, false);
                f.GoNext();
                Check(PumpUntil(delegate { return f.Installed; }, 30), "No terminó de instalar: " + (f.Failure != null ? f.Failure.Message : "?"));
                Check(f.Page == 4 && f.NextButton.Text == "Finalizar", "Página final: " + f.Page);
                Check(File.Exists(Path.Combine(dir, "IDMClone.exe")), "No instaló en la carpeta elegida");
                f.SetRunNow(false);
                f.GoNext();
            }
        }

        static void TS_Uninstall()
        {
            UseFreshHome("uninstall-keep");
            File.WriteAllText(Path.Combine(AppConfig.Dir, "downloads.json"), "[]");
            string dir = Installer.InstalledDir();
            Check(dir != null, "No hay instalación que quitar");
            // Otra copia (p. ej. la de desarrollo) registrada en Opera: el desinstalador no la toca.
            string other = @"C:\Otra\IDMCLONE\browser\com.idmclone.host.json";
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Installer.RegRoot + @"\Software\Opera Software\NativeMessagingHosts\com.idmclone.host")) k.SetValue("", other);
            InstallOptions o = new InstallOptions(); o.Dir = dir; o.KeepUserData = true;
            Installer.Uninstall(o, null);
            Check(!Directory.Exists(dir), "Quedó la carpeta del programa");
            Check(RegDefault(@"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.idmclone.host") == null, "Quedó el registro de Brave");
            Check(RegDefault(@"Software\Opera Software\NativeMessagingHosts\com.idmclone.host") == other, "Borró el registro de otra instalación");
            Check(RegValue(StoreIds.ChromeExternalKey + "\\" + StoreIds.ChromeWebStore, "update_url") == null && RegValue(StoreIds.EdgeExternalKey + "\\" + StoreIds.EdgeAddons, "update_url") == null, "Quedaron las claves de extensión externa");
            Check(RegValue(Installer.UninstallKey, "DisplayName") == null, "Quedó en Aplicaciones instaladas");
            Check(!Directory.Exists(Installer.StartMenuDirOverride), "Quedó la carpeta del menú Inicio");
            Check(File.Exists(Path.Combine(AppConfig.Dir, "downloads.json")), "Borró la lista de descargas aunque se pidió conservarla");
        }

        static void TS_UninstallData()
        {
            UseFreshHome("uninstall-data");
            string data = AppConfig.Dir;
            File.WriteAllText(Path.Combine(data, "downloads.json"), "[]");
            string dir = Path.Combine(SetupRoot, @"Datos\IDMClone");
            InstallOptions o = new InstallOptions(); o.Dir = dir;
            Installer.Install(o, null);
            o.KeepUserData = false;
            Installer.Uninstall(o, null);
            Check(!Directory.Exists(data), "No borró la configuración y la lista");
            Check(!Directory.Exists(dir), "Quedó la carpeta del programa");
            Check(RegValue(Installer.RunKey, "IDMClone") == null, "Quedó el inicio con Windows");
            Check(!File.Exists(Path.Combine(Installer.DesktopDirOverride, "IDMClone.lnk")), "Quedó el acceso directo");
        }

        static void TS_Seen()
        {
            UseFreshHome("seen");
            DateTime t0 = DateTime.UtcNow.AddSeconds(-1);
            Check(ExtensionSeen.Since(t0).Count == 0, "Sin conexiones todavía");
            ExtensionSeen.Record("chrome-extension://jolfcgooaockjhnalagfilpljkloallc/");
            List<string> b = ExtensionSeen.Since(t0);
            // Aquí el "navegador" es quien lanzó las pruebas; lo que importa es que quede anotado y se lea.
            Check(b.Count == 1 && b[0].Length > 0, "Conexiones: " + string.Join(",", b.ToArray()));
            ExtensionSeen.Record("chrome-extension://otra/");
            Check(ExtensionSeen.Read().Count == 1 && ExtensionSeen.Read()[0].Origin == "chrome-extension://otra/", "Una línea por navegador, con la última conexión");
            Check(ExtensionSeen.Since(DateTime.UtcNow.AddMinutes(1)).Count == 0, "Las anteriores a la instalación no cuentan");
            Check(ExtensionSeen.DisplayName("brave.exe") == "Brave" && ExtensionSeen.DisplayName("msedge.exe") == "Microsoft Edge", "Nombres de navegador");
        }

        static void TS_ExitCommand()
        {
            MainForm f = NewMainForm();
            try
            {
                string r = f.HandleCommand("/exit");
                Check(r.Contains("\"ok\":true"), "Respuesta: " + r);
                Check(PumpUntil(delegate { return f.IsDisposed; }, 10), "IDMClone no se cerró");
            }
            finally { if (!f.IsDisposed) DisposeMainForm(f); }
        }
    }
}
