using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using IDMClone.Setup;

namespace IDMClone.Tests
{
    // Arreglos de la auditoría (A): desinstalador, antivirus con "[file]" en la ruta, "Sobrescribir" un
    // duplicado y cancelar, y volcado a disco antes de guardar el estado.
    public static partial class TestMain
    {
        static void RunFixesA()
        {
            Run("arreglo A: desinstalar en una carpeta sin IDMClone -> error y no borra nada", TFA_UninstallNotInstalled);
            Run("arreglo A: desinstalar respeta lo del usuario en la carpeta y borra los .old-* / .new", TFA_UninstallKeepsUserFiles);
            Run("arreglo A: Installer.OwnFolder (vacía / de IDMClone / con otras cosas)", TFA_OwnFolder);
            Run("arreglo A: el asistente instala en \\IDMClone si la carpeta elegida tiene otras cosas", TFA_WizardSubfolder);
            Run("arreglo A: antivirus: ruta con [file] -> parámetros exactos y sin bucle", TFA_AvArgs);
            Run("arreglo A: sobrescribir + Cancelar sin carpeta temporal -> el original intacto", delegate { TFA_OverwriteCancel(false); });
            Run("arreglo A: sobrescribir + Cancelar con carpeta temporal -> el original intacto", delegate { TFA_OverwriteCancel(true); });
            Run("arreglo A: sobrescribir + Cancelar con el duplicado descargando -> sigue y termina bien", TFA_OverwriteCancelRunningDup);
            Run("arreglo A: sobrescribir + Iniciar sin carpeta temporal -> contenido nuevo y el duplicado fuera", delegate { TFA_OverwriteStart(false); });
            Run("arreglo A: sobrescribir + Iniciar con carpeta temporal -> contenido nuevo y el duplicado fuera", delegate { TFA_OverwriteStart(true); });
            Run("arreglo A: sobrescribir sin diálogo (sin temporal) -> sin nombre_1", delegate { TFA_OverwriteNonInteractive(false); });
            Run("arreglo A: sobrescribir sin diálogo (con temporal) -> sin nombre_1", delegate { TFA_OverwriteNonInteractive(true); });
            Run("arreglo A: sobrescribir (diálogo) con el duplicado en una cola en marcha -> ni se relanza ni queda bajando", delegate { TFA_OverwriteDupInRunningQueue(true); });
            Run("arreglo A: sobrescribir (sin diálogo) con el duplicado en una cola en marcha -> no queda bajando", delegate { TFA_OverwriteDupInRunningQueue(false); });
            Run("arreglo A: lo que dicen las posiciones (y el estado guardado) ya está en el archivo", TFA_PositionsBackedByFile);
            Run("arreglo D: desinstalar en una carpeta con un Uninstall.exe ajeno -> error y no borra nada", TFD_UninstallForeignUninstaller);
            Run("arreglo D: desinstalar conserva un archivo del paquete cambiado por el usuario (otro tamaño)", TFD_UninstallKeepsModifiedPayload);
            Run("arreglo D: instalar y desinstalar en su carpeta no deja nada (ni la carpeta)", TFD_InstallUninstallClean);
            Run("arreglo D: su Uninstall.exe limpia la carpeta aunque falte IDMClone.exe", TFD_UninstallFromOwnUninstallerNoExe);
            Run("arreglo D: Hold de una descarga que ya se estaba parando -> false y Release no la relanza", TFD_HoldWhileStopping);
            Run("arreglo E: Installer.RunningAsUninstaller según el nombre del ejecutable", TFE_RunningAsUninstaller);
            Run("arreglo E: GUI: el Uninstall.exe de una instalación a medias no dice \"no está instalado\" y limpia", TFE_GuiUninstallHalfInstall);
            Run("arreglo E: GUI: IDMClone-Setup.exe sin instalación -> \"no está instalado\"", TFE_GuiSetupNotInstalled);
            Run("arreglo E: GUI: instalador renombrado a Uninstall.exe en Descargas -> no borra lo del usuario", TFE_GuiRenamedSetupInDownloads);
            Run("arreglo E: sobrescribir y el diálogo falla -> la pendiente fuera, el original intacto y el duplicado libre", TFE_OverwriteInfoThrows);
            Run("arreglo E: sobrescribir y el diálogo falla con el duplicado bajando -> se reanuda y termina bien", TFE_OverwriteInfoThrowsRunningDup);
            Run("arreglo E: el diálogo falla sin duplicado (sin temporal) -> ni en la lista ni archivo vacío", delegate { TFE_InfoThrowsNoDup(false); });
            Run("arreglo E: el diálogo falla sin duplicado (con temporal, bajando) -> ni en la lista ni temporal", delegate { TFE_InfoThrowsNoDup(true); });
        }

        // ------------------------------------------------------------ instalador (solo carpetas de prueba)

        // Toda ruta que se pase al instalador tiene que estar dentro de la carpeta de pruebas.
        static string FA_SafeDir(string d)
        {
            string full = Path.GetFullPath(d);
            string tmp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
            string home = Path.GetFullPath(Home).TrimEnd('\\') + "\\";
            if (!home.StartsWith(tmp + "idmclone-tests-", StringComparison.OrdinalIgnoreCase) || !full.StartsWith(home, StringComparison.OrdinalIgnoreCase))
                throw new Exception("SEGURIDAD: carpeta fuera de la de pruebas: " + full);
            return full;
        }

        // Registro, menú Inicio, escritorio, paquete e "instalador" de prueba; todo se restaura al acabar.
        static void FA_SetupSandbox(Action<string> body)
        {
            string id = Guid.NewGuid().ToString("N").Substring(0, 8);
            string root = FA_SafeDir(Path.Combine(Home, "fixA-setup-" + id));
            Directory.CreateDirectory(root);
            string sRoot = Installer.RegRoot, sMenu = Installer.StartMenuDirOverride, sDesk = Installer.DesktopDirOverride,
                   sPay = Installer.PayloadOverride, sSelf = Installer.SelfPathOverride;
            bool sNoDel = Installer.NoSelfDelete;
            try
            {
                UseFreshHome("fixA-setup-" + id);
                Installer.RegRoot = @"Software\IDMClone" + "Tests\\" + id;
                Installer.StartMenuDirOverride = Path.Combine(root, @"menu\IDMClone");
                Installer.DesktopDirOverride = Path.Combine(root, "escritorio");
                Directory.CreateDirectory(Installer.DesktopDirOverride);
                Installer.PayloadOverride = MakePayload(Path.Combine(root, "payload.zip"));
                Installer.SelfPathOverride = Path.Combine(root, "IDMClone-Setup.exe");
                File.Copy(Installer.PayloadOverride, Installer.SelfPathOverride);
                Installer.NoSelfDelete = true;
                body(root);
            }
            finally
            {
                string reg = Installer.RegRoot;
                Installer.RegRoot = sRoot; Installer.StartMenuDirOverride = sMenu; Installer.DesktopDirOverride = sDesk;
                Installer.PayloadOverride = sPay; Installer.SelfPathOverride = sSelf; Installer.NoSelfDelete = sNoDel;
                if (reg.StartsWith(@"Software\IDMCloneTests\")) { try { Registry.CurrentUser.DeleteSubKeyTree(reg, false); } catch { } }
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\IDMCloneTests"))
                        if (k != null && k.SubKeyCount == 0 && k.ValueCount == 0) { k.Close(); Registry.CurrentUser.DeleteSubKey(@"Software\IDMCloneTests", false); }
                }
                catch { }
            }
        }

        static void FA_Install(string dir)
        {
            InstallOptions o = new InstallOptions(); o.Dir = FA_SafeDir(dir);
            Installer.Install(o, null);
        }

        static void FA_Uninstall(string dir)
        {
            InstallOptions o = new InstallOptions(); o.Dir = FA_SafeDir(dir); o.KeepUserData = true;
            Installer.Uninstall(o, null);
        }

        static void TFA_UninstallNotInstalled()
        {
            FA_SetupSandbox(delegate(string root)
            {
                string dir = Path.Combine(root, "Descargas");
                Directory.CreateDirectory(dir);
                string foto = Path.Combine(dir, "foto.jpg");
                File.WriteAllText(foto, "foto del usuario");
                bool threw = false;
                try { FA_Uninstall(dir); }
                catch (Exception ex) { if (ex.Message.StartsWith("SEGURIDAD")) throw; threw = true; }
                Check(File.Exists(foto) && File.ReadAllText(foto) == "foto del usuario", "Borró foto.jpg de una carpeta sin IDMClone");
                Check(Directory.Exists(dir), "Borró la carpeta");
                Check(threw, "No avisó de que IDMClone no está instalado ahí");
            });
        }

        static void TFA_UninstallKeepsUserFiles()
        {
            FA_SetupSandbox(delegate(string root)
            {
                string dir = Path.Combine(root, "Carpeta");
                Directory.CreateDirectory(Path.Combine(dir, "sub"));
                string foto = Path.Combine(dir, "foto.jpg"), doc = Path.Combine(dir, @"sub\doc.txt");
                File.WriteAllText(foto, "foto"); File.WriteAllText(doc, "doc");
                FA_Install(dir); // como lo dejaba el instalador anterior: directamente en la carpeta elegida
                Check(File.Exists(Path.Combine(dir, "IDMClone.exe")), "No se instaló");
                // Restos de una actualización (archivo en uso apartado, copia a medias) y algo del usuario dentro de browser.
                File.WriteAllText(Path.Combine(dir, "IDMClone.exe.old-1"), "viejo");
                File.WriteAllText(Path.Combine(dir, @"browser\extension\background.js.old-7"), "viejo");
                File.WriteAllText(Path.Combine(dir, "LICENCIA.txt.new"), "a medias");
                string mio = Path.Combine(dir, @"browser\mio.txt");
                File.WriteAllText(mio, "mío");

                FA_Uninstall(dir);
                Check(File.Exists(foto) && File.ReadAllText(foto) == "foto", "Borró foto.jpg");
                Check(File.Exists(doc) && File.ReadAllText(doc) == "doc", "Borró sub\\doc.txt");
                Check(File.Exists(mio), "Borró browser\\mio.txt (del usuario)");
                Check(Directory.Exists(dir), "Borró la carpeta del usuario");
                foreach (string f in new string[] { "IDMClone.exe", "Uninstall.exe", "LICENCIA.txt", @"Languages\idmclone_en.lng",
                                                    @"browser\extension\manifest.json", @"browser\extension\background.js", @"browser\guia.html",
                                                    @"browser\com.idmclone.host.json", @"browser\com.idmclone.host.firefox.json",
                                                    "IDMClone.exe.old-1", @"browser\extension\background.js.old-7", "LICENCIA.txt.new" })
                    Check(!File.Exists(Path.Combine(dir, f)), "Quedó " + f);
                Check(!Directory.Exists(Path.Combine(dir, "Languages")) && !Directory.Exists(Path.Combine(dir, @"browser\extension")), "Quedaron carpetas vacías de IDMClone");
                Check(Directory.Exists(Path.Combine(dir, "sub")), "Borró la carpeta sub del usuario");
                Check(Installer.InstalledDir() == null, "Sigue registrado como instalado");
            });
        }

        static string FA_OwnFolder(string dir)
        {
            MethodInfo mi = typeof(Installer).GetMethod("OwnFolder", BindingFlags.Public | BindingFlags.Static);
            if (mi == null) throw new Exception("Installer.OwnFolder no existe en este código");
            return (string)mi.Invoke(null, new object[] { dir });
        }

        static void TFA_OwnFolder()
        {
            string root = FA_SafeDir(Path.Combine(Home, "fixA-own-" + Guid.NewGuid().ToString("N").Substring(0, 6)));
            string missing = Path.Combine(root, "noexiste");
            Check(FA_OwnFolder(missing) == missing, "Carpeta que no existe: " + FA_OwnFolder(missing));
            string empty = Path.Combine(root, "vacia"); Directory.CreateDirectory(empty);
            Check(FA_OwnFolder(empty) == empty, "Carpeta vacía: " + FA_OwnFolder(empty));
            string mine = Path.Combine(root, "mia"); Directory.CreateDirectory(mine);
            File.WriteAllText(Path.Combine(mine, "IDMClone.exe"), "x"); File.WriteAllText(Path.Combine(mine, "otra.txt"), "x");
            Check(FA_OwnFolder(mine) == mine, "Carpeta con IDMClone.exe: " + FA_OwnFolder(mine));
            string unin = Path.Combine(root, "solo-uninstall"); Directory.CreateDirectory(unin);
            File.WriteAllText(Path.Combine(unin, "Uninstall.exe"), "x");
            // "Uninstall.exe" a secas puede ser de otro programa: no se instala encima.
            Check(FA_OwnFolder(unin) == Path.Combine(unin, "IDMClone"), "Carpeta con solo Uninstall.exe: " + FA_OwnFolder(unin));
            string other = Path.Combine(root, "descargas"); Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "foto.jpg"), "x");
            Check(FA_OwnFolder(other) == Path.Combine(other, "IDMClone"), "Carpeta con otras cosas: " + FA_OwnFolder(other));
            string sub = Path.Combine(root, "con-subcarpeta"); Directory.CreateDirectory(Path.Combine(sub, "algo"));
            Check(FA_OwnFolder(sub) == Path.Combine(sub, "IDMClone"), "Carpeta con una subcarpeta: " + FA_OwnFolder(sub));
        }

        static void TFA_WizardSubfolder()
        {
            FA_SetupSandbox(delegate(string root)
            {
                string chosen = FA_SafeDir(Path.Combine(root, "Escritorio-usuario"));
                Directory.CreateDirectory(chosen);
                string foto = Path.Combine(chosen, "foto.jpg");
                File.WriteAllText(foto, "foto");
                string expected = Path.Combine(chosen, "IDMClone");
                using (SetupForm f = new SetupForm())
                {
                    f.Quiet = true;
                    IntPtr h = f.Handle;
                    f.GoNext(); f.AcceptLicense(); f.GoNext();
                    f.SetDir(chosen); f.SetShortcuts(false, false);
                    f.GoNext();
                    Check(PumpUntil(delegate { return f.Installed || f.Failure != null; }, 30), "No terminó de instalar");
                    Check(f.Failure == null, "Falló: " + (f.Failure != null ? f.Failure.Message : ""));
                    f.SetRunNow(false);
                    f.GoNext();
                }
                try
                {
                    Check(File.Exists(Path.Combine(expected, "IDMClone.exe")), "No instaló en la subcarpeta IDMClone");
                    Check(!File.Exists(Path.Combine(chosen, "IDMClone.exe")), "Instaló mezclado con los archivos del usuario");
                    Check(Installer.InstalledDir() == expected, "InstalledDir: " + Installer.InstalledDir());
                }
                finally
                {
                    // Quitar lo instalado (solo dentro de la carpeta de pruebas).
                    if (File.Exists(Path.Combine(expected, "IDMClone.exe"))) FA_Uninstall(expected);
                    else if (File.Exists(Path.Combine(chosen, "IDMClone.exe"))) FA_Uninstall(chosen);
                }
                Check(File.Exists(foto), "Se borró foto.jpg");
                Check(!Directory.Exists(expected), "Al desinstalar quedó la subcarpeta IDMClone");
            });
        }

        // ------------------------------------------------------------ antivirus

        static void TFA_AvArgs()
        {
            string[] paths = { @"C:\dl\[file] x.exe", @"C:\dl\[FILE]\[File].exe", @"C:\a [File] b\[file][FILE].zip" };
            string[,] cases =
            {
                { "-Scan -ScanType 3 -File \"[File]\"", "-Scan -ScanType 3 -File \"{0}\"" },
                { "\"[File]\"", "\"{0}\"" },
                { "[File]", "\"{0}\"" },
                { "", "\"{0}\"" },
                { "/scan [file] /quiet", "/scan \"{0}\" /quiet" },
                { "\"[FILE]\" [File]", "\"{0}\" \"{0}\"" },
            };
            List<string> errors = new List<string>();
            Exception thrown = null;
            Thread t = new Thread(delegate()
            {
                try
                {
                    foreach (string p in paths)
                        for (int i = 0; i < cases.GetLength(0); i++)
                        {
                            string want = string.Format(cases[i, 1], p);
                            string got = VirusCheck.BuildArgs(cases[i, 0], p);
                            if (got != want) lock (errors) errors.Add("[" + cases[i, 0] + "] + " + p + " -> " + (got.Length > 200 ? got.Substring(0, 200) + "..." : got));
                        }
                }
                catch (ThreadAbortException) { }
                catch (Exception ex) { thrown = ex; }
            });
            t.IsBackground = true;
            t.Start();
            if (!t.Join(5000))
            {
                try { t.Abort(); } catch { }
                t.Join(2000);
                GC.Collect();
                throw new Exception("BuildArgs no terminó en 5 s (bucle infinito con [file] en la ruta)");
            }
            if (thrown != null) throw new Exception("BuildArgs lanzó " + thrown.GetType().Name + ": " + thrown.Message);
            Check(errors.Count == 0, string.Join(" | ", errors.ToArray()));
        }

        // ------------------------------------------------------------ "Sobrescribir" un duplicado

        class FADup { public MainForm Form; public DownloadManager Mgr; public AppConfig Cfg; public string Folder, Url, Path; public long Size; public DownloadItem First; }

        // Una descarga terminada en su propia carpeta; luego el archivo pasa a tener contenido "del usuario".
        static FADup FA_NewDup(string tag, bool useTemp, string modes, long size, bool userContent)
        {
            FADup d = new FADup();
            d.Form = NewMainForm();
            d.Mgr = Field<DownloadManager>(d.Form, "_mgr");
            d.Cfg = d.Mgr.Config;
            d.Cfg.UseTempFolder = useTemp;
            d.Cfg.TempFolder = Path.Combine(Home, "fixA-tmp-" + tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            d.Folder = Path.Combine(Dl, "fixA-" + tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(d.Folder);
            d.Size = size;
            d.Url = S.Url(modes, "doble-" + tag + ".bin", size, NewId("fa" + tag));
            AddOptions o = new AddOptions(); o.Url = d.Url; o.Folder = d.Folder; o.StartNow = true;
            d.First = d.Form.NewDownload(o, false);
            WaitEnd(d.First, 60);
            AssertComplete(d.First, size);
            d.Path = d.First.FilePath;
            if (userContent) File.WriteAllText(d.Path, "contenido del usuario que no se puede perder");
            return d;
        }

        static FileInfoForm FA_FindInfo(DownloadItem it)
        {
            FileInfoForm info = null;
            PumpUntil(delegate
            {
                foreach (Form f in Application.OpenForms)
                {
                    FileInfoForm fi = f as FileInfoForm;
                    if (fi != null && (it == null || fi.Item == it)) info = fi;
                }
                return info != null;
            }, 10);
            if (info == null) throw new Exception("No apareció «Información del archivo a descargar»");
            return info;
        }

        static string FA_Files(string folder)
        {
            List<string> l = new List<string>();
            foreach (string f in Directory.GetFiles(folder, "*", SearchOption.AllDirectories)) l.Add(Path.GetFileName(f));
            return string.Join(", ", l.ToArray());
        }

        static DownloadItem FA_AddOverwrite(FADup d, bool interactive)
        {
            d.Cfg.DuplicateAction = "overwrite";
            d.Cfg.ShowFileInfoDialog = interactive;
            d.Cfg.StartWhileInfoDialog = true;
            AddOptions o = new AddOptions(); o.Url = d.Url; o.StartNow = true;
            return d.Form.NewDownload(o, interactive);
        }

        static void FA_CloseDup(FADup d)
        {
            CloseOpenForms(typeof(FileInfoForm));
            try { d.Mgr.StopAllAndWait(10000); } catch { }
            DisposeMainForm(d.Form);
        }

        static void TFA_OverwriteCancel(bool useTemp)
        {
            FADup d = FA_NewDup(useTemp ? "cantmp" : "cannotmp", useTemp, "range", 300 * 1024, true);
            try
            {
                string sha0 = Sha(d.Path);
                DownloadItem it = FA_AddOverwrite(d, true);
                FileInfoForm info = FA_FindInfo(it);
                PumpUntil(delegate { return false; }, 2); // tiempo de sobra para que bajara (y sustituyera) si arrancó
                Check(File.Exists(d.Path) && Sha(d.Path) == sha0, "Mientras se muestra el diálogo el original ya cambió o desapareció");
                info.Cancel();
                PumpUntil(delegate { return false; }, 1);
                Check(File.Exists(d.Path), "Cancelar borró el archivo original");
                Check(Sha(d.Path) == sha0, "Cancelar cambió el contenido del archivo original");
                string[] files = Directory.GetFiles(d.Folder);
                Check(files.Length == 1, "Archivos de más en la carpeta: " + FA_Files(d.Folder));
                List<DownloadItem> list = d.Mgr.Snapshot();
                Check(list.Contains(d.First), "El duplicado desapareció de la lista al cancelar");
                Check(!list.Contains(it), "La descarga cancelada sigue en la lista");
                Check(d.First.FilePath == d.Path && d.First.Status == DownloadStatus.Complete, "El duplicado cambió: " + d.First.FilePath + " " + d.First.Status);
                if (useTemp) Check(!Directory.Exists(Path.Combine(d.Cfg.TempFolderOrDefault, it.Id)), "Quedó su carpeta temporal");
            }
            finally { FA_CloseDup(d); }
        }

        static void TFA_OverwriteCancelRunningDup()
        {
            MainForm form = NewMainForm();
            DownloadManager m = Field<DownloadManager>(form, "_mgr");
            try
            {
                AppConfig c = m.Config;
                c.UseTempFolder = false; // los dos escribirían en el mismo archivo
                string folder = Path.Combine(Dl, "fixA-run-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                Directory.CreateDirectory(folder);
                long size = 3L * 1024 * 1024;
                string url = S.Url("range,veryslow", "en-curso.bin", size, NewId("farun"));
                AddOptions o = new AddOptions(); o.Url = url; o.Folder = folder; o.StartNow = true;
                DownloadItem dup = form.NewDownload(o, false);
                WaitProgress(dup, 200 * 1024, 30);

                c.DuplicateAction = "overwrite"; c.ShowFileInfoDialog = true; c.StartWhileInfoDialog = true;
                o = new AddOptions(); o.Url = url; o.StartNow = true;
                DownloadItem it = form.NewDownload(o, true);
                FileInfoForm info = FA_FindInfo(it);
                PumpUntil(delegate { return false; }, 1);
                Check(m.Snapshot().Contains(dup), "El duplicado salió de la lista antes de confirmar");
                Check(dup.Engine == null || !dup.Engine.IsRunning, "El duplicado sigue bajando mientras se muestra el diálogo");
                info.Cancel();
                Check(PumpUntil(delegate { return dup.Engine != null && dup.Engine.IsRunning || dup.Status == DownloadStatus.Complete; }, 10), "Cancelar no reanudó el duplicado");
                Check(!m.Snapshot().Contains(it), "La cancelada sigue en la lista");
                WaitEnd(dup, 90);
                AssertComplete(dup, size);
                Check(Directory.GetFiles(folder).Length == 1, "Archivos de más: " + FA_Files(folder));
            }
            finally { CloseOpenForms(typeof(FileInfoForm)); try { m.StopAllAndWait(10000); } catch { } DisposeMainForm(form); }
        }

        static void TFA_OverwriteStart(bool useTemp)
        {
            FADup d = FA_NewDup(useTemp ? "starttmp" : "startnotmp", useTemp, "range", 300 * 1024, true);
            try
            {
                DownloadItem it = FA_AddOverwrite(d, true);
                FileInfoForm info = FA_FindInfo(it);
                info.Start();
                WaitEnd(it, 60);
                PumpUntil(delegate { return false; }, 1);
                Check(it.FilePath == d.Path, "Se guardó en otra ruta: " + it.FilePath + " en vez de " + d.Path);
                AssertComplete(it, d.Size); // contenido nuevo (el del servidor), no el "del usuario"
                List<DownloadItem> list = d.Mgr.Snapshot();
                Check(!list.Contains(d.First), "El duplicado sigue en la lista tras confirmar");
                Check(list.Contains(it), "La nueva no está en la lista");
                Check(Directory.GetFiles(d.Folder).Length == 1, "Archivos de más en la carpeta: " + FA_Files(d.Folder));
            }
            finally { FA_CloseDup(d); }
        }

        static void TFA_OverwriteNonInteractive(bool useTemp)
        {
            FADup d = FA_NewDup(useTemp ? "nitmp" : "ninotmp", useTemp, "range", 200 * 1024, true);
            try
            {
                DownloadItem it = FA_AddOverwrite(d, false);
                Check(!d.Mgr.Snapshot().Contains(d.First), "El duplicado sigue en la lista");
                WaitEnd(it, 60);
                Check(it.FilePath == d.Path, "Se guardó en otra ruta: " + it.FilePath);
                AssertComplete(it, d.Size);
                Check(Directory.GetFiles(d.Folder).Length == 1, "Archivos de más en la carpeta: " + FA_Files(d.Folder));
            }
            finally { FA_CloseDup(d); }
        }

        // El duplicado está en una cola en marcha (sin carpeta temporal: los dos usan el mismo archivo). Pausarlo
        // no basta si la cola lo relanza; y quitarlo de la lista no debe dejar su motor bajando ("fantasma").
        static void TFA_OverwriteDupInRunningQueue(bool interactive)
        {
            MainForm form = NewMainForm();
            DownloadManager m = Field<DownloadManager>(form, "_mgr");
            try
            {
                AppConfig c = m.Config;
                c.UseTempFolder = false;
                string folder = Path.Combine(Dl, "fixA-queue-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                Directory.CreateDirectory(folder);
                long size = 3L * 1024 * 1024;
                string url = S.Url("range,veryslow", "en-cola.bin", size, NewId("faq"));
                AddOptions o = new AddOptions(); o.Url = url; o.Folder = folder; o.StartNow = false;
                DownloadItem dup = form.NewDownload(o, false);
                m.StartQueue();
                WaitProgress(dup, 200 * 1024, 30);

                c.DuplicateAction = "overwrite"; c.ShowFileInfoDialog = interactive; c.StartWhileInfoDialog = true;
                o = new AddOptions(); o.Url = url; o.StartNow = true;
                DownloadItem it = form.NewDownload(o, interactive);
                List<string> problems = new List<string>();
                if (interactive)
                {
                    FileInfoForm info = FA_FindInfo(it);
                    PumpUntil(delegate { return false; }, 3); // la cola revisa cada 2 s
                    if (dup.Engine != null && dup.Engine.IsRunning) problems.Add("la cola relanzó el duplicado mientras se mostraba el diálogo");
                    info.Start();
                }
                WaitEnd(it, 90);
                PumpUntil(delegate { return false; }, 1);
                if (dup.Engine != null && dup.Engine.IsRunning) problems.Add("el duplicado ya quitado de la lista sigue bajando (motor fantasma)");
                if (m.Snapshot().Contains(dup)) problems.Add("el duplicado sigue en la lista");
                if (it.Status != DownloadStatus.Complete) problems.Add("la nueva terminó en " + it.Status + (it.Error != null ? " (" + it.Error + ")" : ""));
                Check(problems.Count == 0, string.Join("; ", problems.ToArray()));
                AssertComplete(it, size);
                Check(Directory.GetFiles(folder).Length == 1, "Archivos de más: " + FA_Files(folder));
            }
            finally { CloseOpenForms(typeof(FileInfoForm)); try { m.StopAllAndWait(10000); } catch { } DisposeMainForm(form); }
        }

        // ------------------------------------------------------------ posiciones respaldadas por el archivo

        static int FA_ReadByte(string path, long pos)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (pos >= fs.Length) return -1;
                fs.Seek(pos, SeekOrigin.Begin);
                return fs.ReadByte();
            }
        }

        static void TFA_PositionsBackedByFile()
        {
            DownloadManager m = NewMgr();
            long size = 512 * 1024;
            string folder = Path.Combine(Dl, "fixA-flush-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(folder);
            DownloadItem it = m.Add(S.Url("range", "volcado.bin", size, NewId("faflush")), folder, null, false);
            it.SpeedLimitOn = true; it.SpeedLimitKBps = 8; // trozos de 1 KB: con búfer de 4 KB se quedarían en memoria
            m.StartItem(it);
            int samples = 0, stateSamples = 0;
            List<string> bad = new List<string>();
            try
            {
                DateTime until = DateTime.Now.AddSeconds(7);
                while (DateTime.Now < until && bad.Count < 5)
                {
                    if (it.Status == DownloadStatus.Error) throw new Exception("La descarga falló: " + it.Error);
                    // 1) Posiciones en memoria: el byte anterior a Current ya está en el archivo.
                    List<long[]> segs = new List<long[]>();
                    string work;
                    lock (it.Sync)
                    {
                        work = it.WorkingPath;
                        foreach (Segment s in it.Segments) segs.Add(new long[] { s.Start, s.Current });
                    }
                    try
                    {
                        foreach (long[] s in segs)
                        {
                            if (s[1] <= s[0]) continue;
                            int b = FA_ReadByte(work, s[1] - 1);
                            samples++;
                            if (b != TestServer.ByteAt(s[1] - 1)) bad.Add("memoria: byte " + (s[1] - 1) + " = " + b + ", esperado " + TestServer.ByteAt(s[1] - 1));
                        }
                    }
                    catch (IOException) { }
                    // 2) Estado guardado (lo que se usaría al reanudar tras un cierre).
                    try
                    {
                        if (File.Exists(it.StatePath))
                        {
                            Dictionary<string, object> st = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(ReadShared(it.StatePath));
                            string path = st.ContainsKey("TempPath") ? (string)st["TempPath"] : (string)st["FilePath"];
                            foreach (object so in (ArrayList)st["Segments"])
                            {
                                Dictionary<string, object> sd = (Dictionary<string, object>)so;
                                long start = Convert.ToInt64(sd["Start"]), cur = Convert.ToInt64(sd["Current"]);
                                if (cur <= start) continue;
                                int b = FA_ReadByte(path, cur - 1);
                                stateSamples++;
                                if (b != TestServer.ByteAt(cur - 1)) bad.Add("estado: byte " + (cur - 1) + " = " + b + ", esperado " + TestServer.ByteAt(cur - 1));
                            }
                        }
                    }
                    catch (IOException) { }
                    Thread.Sleep(15);
                }
            }
            finally { m.Remove(it, true); }
            Check(bad.Count == 0, "Posiciones por delante de los datos del archivo: " + string.Join(" | ", bad.ToArray()));
            Check(samples >= 50, "Muy pocas muestras (" + samples + "): la prueba no probó nada");
            Check(stateSamples >= 1, "No se llegó a guardar el estado durante la prueba");
        }

        // ------------------------------------------------------------ arreglo D

        // Archivos que deja una instalación de prueba (MakePayload + los que crea Install).
        static readonly string[] FD_Installed = { "IDMClone.exe", "Uninstall.exe", "LICENCIA.txt", @"Languages\idmclone_en.lng",
                                                  @"browser\extension\manifest.json", @"browser\extension\background.js", @"browser\guia.html",
                                                  @"browser\com.idmclone.host.json", @"browser\com.idmclone.host.firefox.json" };

        static void TFD_UninstallForeignUninstaller()
        {
            FA_SetupSandbox(delegate(string root)
            {
                string dir = Path.Combine(root, "OtroPrograma");
                Directory.CreateDirectory(dir);
                string un = Path.Combine(dir, "Uninstall.exe"), foto = Path.Combine(dir, "foto.jpg");
                File.WriteAllText(un, "desinstalador de otro programa");
                File.WriteAllText(foto, "foto del usuario");
                bool threw = false;
                try { FA_Uninstall(dir); }
                catch (Exception ex) { if (ex.Message.StartsWith("SEGURIDAD")) throw; threw = true; }
                Check(threw, "No avisó de que IDMClone no está instalado ahí");
                Check(File.Exists(un) && File.ReadAllText(un) == "desinstalador de otro programa", "Borró o cambió el Uninstall.exe ajeno");
                Check(File.Exists(foto) && File.ReadAllText(foto) == "foto del usuario", "Borró foto.jpg");
                Check(Directory.GetFileSystemEntries(dir).Length == 2, "Cambió el contenido de la carpeta: " + FA_Files(dir));
            });
        }

        static void TFD_UninstallKeepsModifiedPayload()
        {
            FA_SetupSandbox(delegate(string root)
            {
                string dir = Path.Combine(root, "IDMClone");
                FA_Install(dir);
                string lic = Path.Combine(dir, "LICENCIA.txt"), lng = Path.Combine(dir, @"Languages\idmclone_en.lng");
                Check(File.Exists(lic) && File.Exists(lng), "No se instaló");
                long lic0 = new FileInfo(lic).Length, lng0 = new FileInfo(lng).Length;
                // El usuario los sustituye por los suyos (con el mismo nombre y otro tamaño).
                File.WriteAllText(lic, "Mis notas personales, que no son la licencia de IDMClone.");
                File.WriteAllText(lng, "[IDMClone]\nLANGUAGE=Mio\n[Textos]\nHola=Hello\nAdios=Bye\n");
                Check(new FileInfo(lic).Length != lic0 && new FileInfo(lng).Length != lng0, "La prueba necesita otro tamaño");
                // guia.html la reescribe Install (con la ruta): su tamaño ya no es el del paquete y aun así es de IDMClone.
                Check(new FileInfo(Path.Combine(dir, @"browser\guia.html")).Length != Installer.PayloadText("browser/guia.html").Length,
                      "guia.html no cambió de tamaño al instalar (la prueba no comprobaría nada)");

                FA_Uninstall(dir);
                Check(File.Exists(lic) && File.ReadAllText(lic).StartsWith("Mis notas"), "Borró LICENCIA.txt del usuario");
                Check(File.Exists(lng) && File.ReadAllText(lng).Contains("LANGUAGE=Mio"), "Borró el .lng del usuario");
                foreach (string f in FD_Installed)
                    if (f != "LICENCIA.txt" && f != @"Languages\idmclone_en.lng")
                        Check(!File.Exists(Path.Combine(dir, f)), "Quedó " + f);
                Check(!Directory.Exists(Path.Combine(dir, "browser")), "Quedó la carpeta browser");
                Check(Directory.Exists(dir), "Borró la carpeta con archivos del usuario");
                Check(Installer.InstalledDir() == null, "Sigue registrado como instalado");
            });
        }

        static void TFD_InstallUninstallClean()
        {
            FA_SetupSandbox(delegate(string root)
            {
                string dir = Path.Combine(root, @"Programas\IDMClone");
                FA_Install(dir);
                foreach (string f in FD_Installed) Check(File.Exists(Path.Combine(dir, f)), "No se instaló " + f);
                Check(Installer.InstalledDir() != null, "No quedó registrado");
                FA_Uninstall(dir);
                Check(!Directory.Exists(dir), "Quedó la carpeta: " + (Directory.Exists(dir) ? FA_Files(dir) : ""));
                Check(Directory.Exists(Path.Combine(root, "Programas")), "Borró la carpeta de encima");
                Check(!File.Exists(Path.Combine(root, @"Programas\fuera.txt")) && !File.Exists(Path.Combine(root, "fuera.txt")), "Escribió fuera de la carpeta");
                Check(File.Exists(Installer.SelfPathOverride), "Borró el instalador");
                Check(Installer.InstalledDir() == null, "Sigue registrado como instalado");
            });
        }

        // Instalación a medias (sin IDMClone.exe): su propio Uninstall.exe sí puede limpiarla.
        static void TFD_UninstallFromOwnUninstallerNoExe()
        {
            FA_SetupSandbox(delegate(string root)
            {
                string dir = Path.Combine(root, "Medias");
                FA_Install(dir);
                File.Delete(Path.Combine(dir, "IDMClone.exe"));
                string foto = Path.Combine(dir, "foto.jpg");
                File.WriteAllText(foto, "foto");
                Installer.SelfPathOverride = FA_SafeDir(Path.Combine(dir, "Uninstall.exe")); // el sandbox lo restaura
                FA_Uninstall(dir);
                Check(File.Exists(foto), "Borró foto.jpg");
                Check(File.Exists(Path.Combine(dir, "Uninstall.exe")), "Se borró a sí mismo mientras se ejecuta");
                foreach (string f in FD_Installed)
                    if (f != "Uninstall.exe" && f != "IDMClone.exe") Check(!File.Exists(Path.Combine(dir, f)), "Quedó " + f);
                Check(Installer.InstalledDir() == null, "Sigue registrado como instalado");
            });
        }

        static void TFD_HoldWhileStopping()
        {
            DownloadManager m = NewMgr();
            m.Config.MaxConnections = 1;
            try
            {
                DownloadItem it = m.Add(S.Url("range,veryslow", "parandose.bin", 4L * 1024 * 1024, NewId("hstop")), Dl, null, true);
                WaitProgress(it, 32 * 1024, 30);
                bool wasUserStopped = it.UserStopped;
                m.StopItem(it);                       // pausa del usuario: el motor empieza a pararse
                bool userStopped = it.UserStopped;    // lo que guarda NewDownload antes de retener
                bool held = m.Hold(it);
                Check(!wasUserStopped && userStopped, "StopItem no la marcó como detenida por el usuario");
                Check(!held, "Hold de una descarga que ya se estaba parando debe devolver false");
                Check(!TC_Running(it), "Hold volvió sin esperar a que el motor terminara");
                m.Release(it, userStopped, held, false);
                Thread.Sleep(1500);
                Check(!TC_Running(it), "Release la relanzó aunque el usuario la había pausado");
                Check(it.UserStopped, "Release no dejó UserStopped como estaba");
                Check(it.Status != DownloadStatus.Complete, "Terminó: la prueba no comprobó nada");
            }
            finally { m.StopAllAndWait(10000); }
        }

        // ------------------------------------------------------------ arreglo E

        // La comprobación de SetupProgram.Main antes de abrir "Desinstalar IDMClone" (Main no se puede llamar
        // sin que salga un MessageBox o el formulario: se repite aquí la misma condición, pieza a pieza).
        static bool FE_GuiSaysNotInstalled()
        {
            return Installer.InstalledDir() == null
                && !File.Exists(Path.Combine(Path.GetDirectoryName(Installer.SelfPath), Installer.ExeName))
                && !Installer.RunningAsUninstaller;
        }

        // Lo que hace UninstallForm.Start (sin carpeta: la registrada o la del propio desinstalador).
        static void FE_GuiUninstall()
        {
            FA_SafeDir(Installer.InstalledDir() ?? Path.GetDirectoryName(Installer.SelfPath));
            InstallOptions o = new InstallOptions(); o.KeepUserData = true;
            Installer.Uninstall(o, null);
        }

        static void TFE_RunningAsUninstaller()
        {
            FA_SetupSandbox(delegate(string root)
            {
                Check(!Installer.RunningAsUninstaller, "IDMClone-Setup.exe cuenta como desinstalador");
                string[] yes = { @"IDMClone\Uninstall.exe", @"IDMClone\UNINSTALL.EXE", @"Descargas\uninstall.exe" };
                string[] no = { @"IDMClone\Uninstall (1).exe", @"IDMClone\Uninstall.exe.bak", @"IDMClone\Uninstaller.exe", @"IDMClone\IDMClone.exe", @"Uninstall.exe\IDMClone-Setup.exe" };
                foreach (string s in yes)
                {
                    Installer.SelfPathOverride = FA_SafeDir(Path.Combine(root, s)); // el sandbox lo restaura
                    Check(Installer.RunningAsUninstaller, "No cuenta como desinstalador: " + s);
                }
                foreach (string s in no)
                {
                    Installer.SelfPathOverride = FA_SafeDir(Path.Combine(root, s));
                    Check(!Installer.RunningAsUninstaller, "Cuenta como desinstalador: " + s);
                }
            });
        }

        static void TFE_GuiUninstallHalfInstall()
        {
            FA_SetupSandbox(delegate(string root)
            {
                string dir = Path.Combine(root, "Medias");
                FA_Install(dir);
                File.Delete(Path.Combine(dir, "IDMClone.exe"));
                string foto = Path.Combine(dir, "foto.jpg");
                File.WriteAllText(foto, "foto");
                Installer.SelfPathOverride = FA_SafeDir(Path.Combine(dir, "Uninstall.exe"));
                // Sin el arreglo, la GUI paraba aquí: ni registrada (falta IDMClone.exe) ni IDMClone.exe a su lado.
                Check(Installer.InstalledDir() == null && !File.Exists(Path.Combine(dir, "IDMClone.exe")), "La prueba no reproduce la instalación a medias");
                Check(Installer.RunningAsUninstaller, "Su Uninstall.exe no cuenta como desinstalador");
                Check(!FE_GuiSaysNotInstalled(), "La GUI diría \"IDMClone no está instalado\"");
                FE_GuiUninstall();
                Check(File.Exists(foto), "Borró foto.jpg");
                foreach (string f in FD_Installed)
                    if (f != "Uninstall.exe" && f != "IDMClone.exe") Check(!File.Exists(Path.Combine(dir, f)), "Quedó " + f);
                Check(!Directory.Exists(Path.Combine(dir, "browser")), "Quedó la carpeta browser");
                Check(!Directory.Exists(Installer.StartMenuDir), "Quedó el menú Inicio");
                Check(!File.Exists(Path.Combine(Installer.DesktopDir, "IDMClone.lnk")), "Quedó el acceso directo del escritorio");
            });
        }

        static void TFE_GuiSetupNotInstalled()
        {
            FA_SetupSandbox(delegate(string root)
            {
                // root\IDMClone-Setup.exe con "/uninstall" y nada instalado: aviso, como antes.
                Check(!Installer.RunningAsUninstaller, "IDMClone-Setup.exe cuenta como desinstalador");
                Check(FE_GuiSaysNotInstalled(), "La GUI no diría \"IDMClone no está instalado\"");
                bool threw = false;
                try { FE_GuiUninstall(); }
                catch (Exception ex) { if (ex.Message.StartsWith("SEGURIDAD")) throw; threw = true; }
                Check(threw, "Uninstall no se negó (modo silencioso)");
                Check(File.Exists(Installer.SelfPathOverride), "Borró el instalador");
                // Instalado en otra carpeta: la GUI sí sigue (desinstala esa).
                FA_Install(Path.Combine(root, "IDMClone"));
                Check(!FE_GuiSaysNotInstalled(), "Instalado y la GUI diría \"no está instalado\"");
            });
        }

        // El usuario renombra IDMClone-Setup.exe a Uninstall.exe en Descargas (o lo baja con ese nombre) y lo abre.
        static void TFE_GuiRenamedSetupInDownloads()
        {
            FA_SetupSandbox(delegate(string root)
            {
                string dir = Path.Combine(root, "Descargas");
                Directory.CreateDirectory(Path.Combine(dir, "sub"));
                string foto = Path.Combine(dir, "foto.jpg"), doc = Path.Combine(dir, @"sub\doc.txt"), lic = Path.Combine(dir, "LICENCIA.txt");
                File.WriteAllText(foto, "foto"); File.WriteAllText(doc, "doc"); File.WriteAllText(lic, "mi licencia, no la de IDMClone");
                string self = Path.Combine(dir, "Uninstall.exe");
                File.Copy(Installer.SelfPathOverride, self);
                Installer.SelfPathOverride = FA_SafeDir(self);
                Check(Installer.RunningAsUninstaller, "El renombrado no cuenta como desinstalador");
                // Ahora la GUI abre "Desinstalar IDMClone" (como ya hacía /S): solo puede tocar lo de IDMClone.
                Check(!FE_GuiSaysNotInstalled(), "La GUI diría \"no está instalado\" (¿cambió la condición?)");
                FE_GuiUninstall();
                Check(File.Exists(foto) && File.Exists(doc), "Borró archivos del usuario: " + FA_Files(dir));
                Check(File.Exists(lic) && File.ReadAllText(lic).StartsWith("mi licencia"), "Borró LICENCIA.txt del usuario");
                Check(Directory.Exists(dir), "Borró la carpeta Descargas");
                Check(File.Exists(self), "Se borró a sí mismo mientras se ejecuta (NoSelfDelete)");
            });
        }

        // "Información del archivo" no se puede abrir: sin categorías (null) el constructor de FileInfoForm
        // lanza NullReferenceException; DownloadManager.Add las tolera (CategoryFor devuelve General).
        static Exception FE_NewDownloadWithBrokenInfo(MainForm form, AppConfig cfg, AddOptions o)
        {
            List<Category> cats = cfg.Categories;
            cfg.Categories = null;
            try { form.NewDownload(o, true); return null; }
            catch (Exception ex) { return ex; }
            finally { cfg.Categories = cats; }
        }

        static void FE_CheckNoPending(DownloadManager m, int count, string what)
        {
            List<DownloadItem> list = m.Snapshot();
            foreach (DownloadItem x in list) Check(!x.Pending, "Quedó en la lista la descarga pendiente: " + x.FileName);
            Check(list.Count == count, what + ": " + list.Count + " descargas en la lista (esperadas " + count + ")");
            foreach (Form f in Application.OpenForms) Check(!(f is FileInfoForm), "Quedó abierto un «Información del archivo»");
        }

        static void TFE_OverwriteInfoThrows()
        {
            FADup d = FA_NewDup("einfo", false, "range", 300 * 1024, true);
            try
            {
                string sha0 = Sha(d.Path);
                bool us0 = d.First.UserStopped;
                int n0 = d.Mgr.Snapshot().Count;
                d.Cfg.DuplicateAction = "overwrite"; d.Cfg.ShowFileInfoDialog = true; d.Cfg.StartWhileInfoDialog = true;
                AddOptions o = new AddOptions(); o.Url = d.Url; o.StartNow = true;
                Exception ex = FE_NewDownloadWithBrokenInfo(d.Form, d.Cfg, o);
                Check(ex is NullReferenceException, "El diálogo no falló como se esperaba: " + (ex == null ? "sin excepción" : ex.GetType().Name + ": " + ex.Message));
                FE_CheckNoPending(d.Mgr, n0, "La lista cambió");
                Check(d.Mgr.Snapshot().Contains(d.First), "El duplicado salió de la lista");
                Check(File.Exists(d.Path) && Sha(d.Path) == sha0, "El archivo original cambió o desapareció");
                Check(Directory.GetFiles(d.Folder).Length == 1, "Archivos de más en la carpeta: " + FA_Files(d.Folder));
                Check(d.First.UserStopped == us0, "El duplicado quedó retenido (UserStopped=" + d.First.UserStopped + ")");
                Check(d.First.FilePath == d.Path && d.First.Status == DownloadStatus.Complete, "El duplicado cambió: " + d.First.FilePath + " " + d.First.Status);
            }
            finally { FA_CloseDup(d); }
        }

        static void TFE_OverwriteInfoThrowsRunningDup()
        {
            MainForm form = NewMainForm();
            DownloadManager m = Field<DownloadManager>(form, "_mgr");
            try
            {
                AppConfig c = m.Config;
                c.UseTempFolder = false;
                string folder = Path.Combine(Dl, "fixE-run-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                Directory.CreateDirectory(folder);
                long size = 3L * 1024 * 1024;
                string url = S.Url("range,veryslow", "en-curso-e.bin", size, NewId("ferun"));
                AddOptions o = new AddOptions(); o.Url = url; o.Folder = folder; o.StartNow = true;
                DownloadItem dup = form.NewDownload(o, false);
                WaitProgress(dup, 200 * 1024, 30);
                int n0 = m.Snapshot().Count;
                bool us0 = dup.UserStopped;

                c.DuplicateAction = "overwrite"; c.ShowFileInfoDialog = true; c.StartWhileInfoDialog = true;
                o = new AddOptions(); o.Url = url; o.StartNow = true;
                Exception ex = FE_NewDownloadWithBrokenInfo(form, c, o);
                Check(ex is NullReferenceException, "El diálogo no falló como se esperaba: " + (ex == null ? "sin excepción" : ex.GetType().Name));
                FE_CheckNoPending(m, n0, "La lista cambió");
                Check(dup.UserStopped == us0, "El duplicado quedó retenido (UserStopped=" + dup.UserStopped + ")");
                Check(PumpUntil(delegate { return dup.Engine != null && dup.Engine.IsRunning || dup.Status == DownloadStatus.Complete; }, 10), "El duplicado no se reanudó");
                WaitEnd(dup, 90);
                AssertComplete(dup, size);
                Check(Directory.GetFiles(folder).Length == 1, "Archivos de más: " + FA_Files(folder));
            }
            finally { CloseOpenForms(typeof(FileInfoForm)); try { m.StopAllAndWait(10000); } catch { } DisposeMainForm(form); }
        }

        static void TFE_InfoThrowsNoDup(bool useTemp)
        {
            MainForm form = NewMainForm();
            DownloadManager m = Field<DownloadManager>(form, "_mgr");
            try
            {
                AppConfig c = m.Config;
                c.UseTempFolder = useTemp;
                c.TempFolder = Path.Combine(Home, "fixE-tmp-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                c.ShowFileInfoDialog = true; c.StartWhileInfoDialog = true;
                string folder = Path.Combine(Dl, "fixE-nodup-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                Directory.CreateDirectory(folder);
                string url = S.Url("range,veryslow", "sin-dup.bin", 3L * 1024 * 1024, NewId("fenodup"));
                int n0 = m.Snapshot().Count;
                AddOptions o = new AddOptions(); o.Url = url; o.Folder = folder; o.StartNow = true;
                Exception ex = FE_NewDownloadWithBrokenInfo(form, c, o);
                Check(ex is NullReferenceException, "El diálogo no falló como se esperaba: " + (ex == null ? "sin excepción" : ex.GetType().Name));
                FE_CheckNoPending(m, n0, "La lista cambió");
                PumpUntil(delegate { return false; }, 1);
                Check(Directory.GetFiles(folder).Length == 0, "Quedaron archivos en la carpeta: " + FA_Files(folder));
                if (useTemp && Directory.Exists(c.TempFolderOrDefault))
                    Check(Directory.GetFiles(c.TempFolderOrDefault, "*", SearchOption.AllDirectories).Length == 0, "Quedó el temporal: " + FA_Files(c.TempFolderOrDefault));
            }
            finally { CloseOpenForms(typeof(FileInfoForm)); try { m.StopAllAndWait(10000); } catch { } DisposeMainForm(form); }
        }
    }
}
