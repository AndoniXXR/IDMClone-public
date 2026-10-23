using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using IDMClone.Setup;

namespace IDMClone.Tests
{
    // Detalles de interfaz: menú contextual del Explorador (/urlfile, /linksfile, registro por usuario en una clave de
    // pruebas), velocidad en MB/sec desde 1000 KB/sec, la lista de Columnas sin filas cortadas y la flecha animada
    // de la ventana de progreso. El registro de verdad no se toca: todo va bajo HKCU\Software\IDMCloneTests\<id>.
    public static partial class TestMain
    {
        static void RunUiDetails()
        {
            Run("explorador: /urlfile y /linksfile en la línea de comandos y al reenviarlos a la instancia abierta", TUD_CliSwitches);
            Run("explorador: dirección de un acceso directo de Internet (.url)", TUD_ShortcutUrl);
            Run("explorador: enlaces de un .txt y de una página guardada (.htm/.html)", TUD_LinksInFiles);
            Run("explorador: registrar y quitar el menú contextual (solo lo que apunta a este IDMClone.exe)", TUD_ShellMenuRegistry);
            Run("explorador: el instalador lo registra y el desinstalador quita solo lo suyo", TUD_InstallerShellMenu);
            Run("explorador: la casilla de Opciones > General lo activa y lo desactiva", TUD_OptionsToggle);
            Run("explorador: la instancia abierta recibe /linksfile (lista de enlaces) y /urlfile (descarga)", TUD_IpcFiles);
            Run("velocidad: pasa a MB/sec desde 1000 KB/sec, igual en la lista, el progreso, la bandeja y la consola", TUD_SpeedFormat);
            Run("columnas: la lista muestra filas enteras (sin la última cortada) a 100/125/150 %", TUD_ColumnsRows);
            Run("progreso: la flecha verde se anima al descargar y se para en pausa", TUD_ProgressArrow);
        }

        // Clave de clases de prueba; se borra al terminar.
        static void TUD_WithClasses(Action<string> body)
        {
            string root = @"Software\IDMCloneTests\ui-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string saved = ShellMenu.ClassesKey, savedUninstall = ShellMenu.UninstallKey;
            ShellMenu.ClassesKey = root + @"\Classes";
            ShellMenu.UninstallKey = root + @"\Uninstall\IDMClone"; // la marca "quitado por el usuario", tampoco en la de verdad
            try { body(ShellMenu.ClassesKey); }
            finally
            {
                ShellMenu.ClassesKey = saved; ShellMenu.UninstallKey = savedUninstall;
                try { Registry.CurrentUser.DeleteSubKeyTree(root, false); } catch { }
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\IDMCloneTests"))
                        if (k != null && k.SubKeyCount == 0 && k.ValueCount == 0) { k.Close(); Registry.CurrentUser.DeleteSubKey(@"Software\IDMCloneTests", false); }
                }
                catch { }
            }
        }

        static bool TUD_KeyExists(string key)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(key)) return k != null;
        }

        static string TUD_Default(string key)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(key)) return k != null ? k.GetValue("") as string : null;
        }

        static void TUD_CliSwitches()
        {
            CliArgs a = CliArgs.Parse(new string[] { "/urlfile", @"C:\Mi escritorio\Página de descargas.url" });
            CheckEq(@"C:\Mi escritorio\Página de descargas.url", a.UrlFile, "/urlfile");
            Check(a.HasFiles && string.IsNullOrEmpty(a.Url), "/urlfile no cuenta como orden");
            CliArgs back = CliArgs.Parse(a.ToCommandLine());
            CheckEq(a.UrlFile, back.UrlFile, "/urlfile reenviado a la instancia abierta (" + a.ToCommandLine() + ")");

            CliArgs l = CliArgs.Parse("/linksfile \"C:\\Mis documentos\\lista de enlaces.txt\"");
            CheckEq(@"C:\Mis documentos\lista de enlaces.txt", l.LinksFile, "/linksfile");
            CheckEq(l.LinksFile, CliArgs.Parse(l.ToCommandLine()).LinksFile, "/linksfile reenviado");
            Check(!CliArgs.Parse(new string[] { "/tray", "/linksfile", @"C:\x\a.txt" }).OnlyStartsHidden, "Con /linksfile no es 'solo arrancar oculto'");

            // Relativo: relativo a donde se ejecutó el comando (la instancia abierta puede estar en otra carpeta).
            CliArgs r = CliArgs.Parse(new string[] { "/linksfile", "enlaces.txt", "/urlfile", "sitio.url" });
            r.ResolvePaths();
            CheckEq(Path.GetFullPath("enlaces.txt"), r.LinksFile, "Ruta relativa de /linksfile");
            CheckEq(Path.GetFullPath("sitio.url"), r.UrlFile, "Ruta relativa de /urlfile");

            // La orden que escribe el menú del Explorador se lee igual.
            string cmd = ShellMenu.Command(@"C:\Programas\IDMClone\IDMClone.exe", true).Replace("%1", @"C:\Users\Ana\Descargas\mis enlaces.html");
            CheckEq(@"C:\Programas\IDMClone\IDMClone.exe", ShellMenu.CommandExe(cmd), "Programa de la orden");
            CheckEq(@"C:\Users\Ana\Descargas\mis enlaces.html", CliArgs.Parse(cmd.Substring(cmd.IndexOf(ShellMenu.LinksSwitch))).LinksFile, "Archivo de la orden");
        }

        static void TUD_ShortcutUrl()
        {
            CheckEq("https://ejemplo.com/descargas/programa.zip",
                ShellMenu.ShortcutUrl("[{000214A0-0000-0000-C000-000000000046}]\r\nProp3=19,11\r\n[InternetShortcut]\r\nIDList=\r\nURL=https://ejemplo.com/descargas/programa.zip\r\nIconIndex=13\r\n"),
                "Acceso directo de Windows");
            CheckEq("http://ejemplo.com/a b.pdf", ShellMenu.ShortcutUrl("[InternetShortcut]\nURL= http://ejemplo.com/a b.pdf \n"), "Con LF y espacios");
            CheckEq("ftp://ftp.ejemplo.com/f.iso", ShellMenu.ShortcutUrl("URL=ftp://ftp.ejemplo.com/f.iso"), "Sin sección");
            // La de [InternetShortcut] manda sobre una URL= de otra sección.
            CheckEq("https://bueno.com/x.zip", ShellMenu.ShortcutUrl("[Otra]\nURL=https://malo.com/\n[InternetShortcut]\nURL=https://bueno.com/x.zip\n"), "Sección InternetShortcut");
            Check(ShellMenu.ShortcutUrl("[InternetShortcut]\nURL=file:///C:/Windows/notepad.exe\n") == null, "file:// no es una dirección web");
            Check(ShellMenu.ShortcutUrl("[InternetShortcut]\nURL=javascript:alert(1)\n") == null, "javascript: no es una dirección web");
            Check(ShellMenu.ShortcutUrl("") == null && ShellMenu.ShortcutUrl(null) == null, "Vacío");

            string f = Path.Combine(Home, "acceso-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".url");
            File.WriteAllText(f, "[InternetShortcut]\r\nURL=https://ejemplo.com/ñandú.mp4\r\n", new UTF8Encoding(true));
            CheckEq("https://ejemplo.com/ñandú.mp4", ShellMenu.ReadShortcutUrl(f), "Leído del archivo (UTF-8)");
            // Como lo escribe Windows: ANSI, y la dirección con caracteres raros repetida en UTF-7 en [InternetShortcut.W].
            string utf7 = Encoding.ASCII.GetString(Encoding.UTF7.GetBytes("https://ejemplo.com/日本.zip"));
            File.WriteAllBytes(f, Encoding.Default.GetBytes("[InternetShortcut]\r\nURL=https://ejemplo.com/??.zip\r\n[InternetShortcut.W]\r\nURL=" + utf7 + "\r\n"));
            CheckEq("https://ejemplo.com/日本.zip", ShellMenu.ReadShortcutUrl(f), "Sección [InternetShortcut.W] en UTF-7");
            if (Encoding.Default.GetString(Encoding.Default.GetBytes("ó")) == "ó") // la página ANSI de este equipo tiene la ó
            {
                File.WriteAllBytes(f, Encoding.Default.GetBytes("[InternetShortcut]\r\nURL=https://ejemplo.com/canción.mp3\r\n"));
                CheckEq("https://ejemplo.com/canción.mp3", ShellMenu.ReadShortcutUrl(f), "Archivo en ANSI");
            }
        }

        static void TUD_LinksInFiles()
        {
            List<string> t = ShellMenu.LinksInText("Mira esto: https://a.com/uno.zip, y (http://b.com/dos.pdf).\nOtra vez https://a.com/uno.zip\nftp://c.com/tres.iso", false);
            CheckEq("https://a.com/uno.zip|http://b.com/dos.pdf|ftp://c.com/tres.iso", string.Join("|", t.ToArray()), "Texto");

            string html = "<html><head><base href=\"https://sitio.com/descargas/\"></head><body>" +
                "<a href=\"programa.exe\">Programa</a> <a href='/docs/manual.pdf'>Manual</a> <a href=https://otro.com/v.mp4>Vídeo</a>" +
                "<a href=\"x.zip?a=1&amp;b=2\">Zip</a> <a href=\"#arriba\">Arriba</a> <a href=\"mailto:yo@sitio.com\">Correo</a>" +
                "<a href=\"javascript:void(0)\">Nada</a> <img src=\"img/foto.jpg\"> <a href=\"programa.exe#v2\">Otra vez</a>" +
                "<p>Texto: https://texto.com/archivo.rar</p></body></html>";
            List<string> h = ShellMenu.LinksInText(html, true);
            CheckEq("https://sitio.com/descargas/programa.exe|https://sitio.com/docs/manual.pdf|https://otro.com/v.mp4|" +
                    "https://sitio.com/descargas/x.zip?a=1&b=2|https://sitio.com/descargas/img/foto.jpg|https://texto.com/archivo.rar",
                    string.Join("|", h.ToArray()), "Página con <base href>");

            // Guardada con el navegador: "saved from url" da la dirección para los enlaces relativos.
            string saved = "<!DOCTYPE html>\r\n<!-- saved from url=(0033)https://web.com/carpeta/pagina.html -->\r\n<a href=\"a.zip\">a</a><a href=\"../b.zip\">b</a>";
            CheckEq("https://web.com/carpeta/a.zip|https://web.com/b.zip", string.Join("|", ShellMenu.LinksInText(saved, true).ToArray()), "saved from url");
            // Sin dirección base, los relativos no se pueden descargar: solo los absolutos.
            CheckEq("http://abs.com/z.7z", string.Join("|", ShellMenu.LinksInText("<a href=\"rel.zip\">r</a><a href=\"http://abs.com/z.7z\">z</a>", true).ToArray()), "Sin base");
            Check(ShellMenu.LinksInText("sin enlaces", false).Count == 0 && ShellMenu.LinksInText("", true).Count == 0, "Sin enlaces");
            // Un href con espacios o con #ancla da un solo enlace (no otro cortado o repetido por el texto de la página).
            CheckEq("https://a.com/my file.zip|https://a.com/x.zip",
                string.Join("|", ShellMenu.LinksInText("<a href=\"https://a.com/my file.zip\">f</a><a href=\"https://a.com/x.zip#dl\">x</a> https://a.com/x.zip#otra", true).ToArray()),
                "href con espacios y con #ancla");

            string dir = Path.Combine(Home, "enlaces-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "lista.txt"), "https://a.com/1.zip\r\nhttps://a.com/2.zip\r\n");
            File.WriteAllText(Path.Combine(dir, "pagina.HTML"), "<a href=\"https://a.com/x.zip?a=1&amp;b=2\">x</a>");
            CheckEq(2, ShellMenu.LinksInFile(Path.Combine(dir, "lista.txt")).Count, "Enlaces de lista.txt");
            CheckEq("https://a.com/x.zip?a=1&b=2", ShellMenu.LinksInFile(Path.Combine(dir, "pagina.HTML"))[0], ".HTML se lee como página (&amp;)");
        }

        static void TUD_ShellMenuRegistry()
        {
            TUD_WithClasses(delegate(string classes)
            {
                string exe = @"C:\Programas\IDMClone\IDMClone.exe", other = @"D:\Otra copia\IDMClone.exe";
                // Un verbo de otro programa en .txt: no se toca.
                string foreign = classes + @"\SystemFileAssociations\.txt\shell\OtroPrograma";
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(foreign + @"\command")) k.SetValue("", "\"C:\\Otro\\otro.exe\" \"%1\"");

                Check(!ShellMenu.IsRegistered(exe), "Registrado antes de empezar");
                ShellMenu.Set(true, exe);
                Check(ShellMenu.IsRegistered(exe), "No quedó registrado");
                Check(!ShellMenu.IsRegistered(other), "Registrado también para otra copia");

                string url = classes + @"\InternetShortcut\shell\" + ShellMenu.UrlVerb;
                CheckEq(Lang.T(ShellMenu.UrlText), TUD_Default(url), "Texto en los .url");
                CheckEq("\"" + exe + "\" /urlfile \"%1\"", TUD_Default(url + @"\command"), "Orden en los .url");
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(url)) CheckEq(exe + ",0", k.GetValue("Icon") as string, "Icono");
                // Windows 10 saca el menú de los .url de IE.AssocFile.URL (su UserChoice): también ahí.
                string urlIe = classes + @"\IE.AssocFile.URL\shell\" + ShellMenu.UrlVerb;
                CheckEq("\"" + exe + "\" /urlfile \"%1\"", TUD_Default(urlIe + @"\command"), "Orden en IE.AssocFile.URL");
                foreach (string ext in ShellMenu.LinkExtensions)
                {
                    string key = classes + @"\SystemFileAssociations\" + ext + @"\shell\" + ShellMenu.LinksVerb;
                    CheckEq(Lang.T(ShellMenu.LinksText), TUD_Default(key), "Texto en " + ext);
                    CheckEq("\"" + exe + "\" /linksfile \"%1\"", TUD_Default(key + @"\command"), "Orden en " + ext);
                }
                Check(!ShellMenu.IsRegistered(classes + @"\nada", exe), "IsRegistered con otra raíz");

                // Quitarlo desde otra copia no borra lo de esta.
                ShellMenu.Set(false, other);
                Check(ShellMenu.IsRegistered(exe), "Otra copia quitó lo de esta");
                ShellMenu.Set(false, exe);
                Check(!ShellMenu.IsRegistered(exe), "No se quitó");
                Check(!TUD_KeyExists(url), "Queda el verbo de los .url");
                Check(!TUD_KeyExists(classes + @"\InternetShortcut"), "Queda la clave vacía InternetShortcut");
                Check(!TUD_KeyExists(classes + @"\IE.AssocFile.URL"), "Queda la clave vacía IE.AssocFile.URL");
                Check(!TUD_KeyExists(classes + @"\SystemFileAssociations\.htm") && !TUD_KeyExists(classes + @"\SystemFileAssociations\.html"), "Quedan claves vacías de .htm/.html");
                Check(!TUD_KeyExists(classes + @"\SystemFileAssociations\.txt\shell\" + ShellMenu.LinksVerb), "Queda el verbo de .txt");
                CheckEq("\"C:\\Otro\\otro.exe\" \"%1\"", TUD_Default(foreign + @"\command"), "Se tocó el verbo de otro programa");
                ShellMenu.Set(false, exe); // dos veces no falla
            });
        }

        static void TUD_InstallerShellMenu()
        {
            FA_SetupSandbox(delegate(string root)
            {
                string classes = Installer.RegRoot + @"\" + Installer.ClassesKey;
                string dir = FA_SafeDir(Path.Combine(root, @"Programas\IDMClone"));
                string exe = Path.Combine(dir, Installer.ExeName);
                InstallOptions o = new InstallOptions(); o.Dir = dir;
                Installer.Install(o, null);
                Check(ShellMenu.IsRegistered(classes, exe), "El instalador no añadió el menú contextual del Explorador");
                CheckEq("\"" + exe + "\" /urlfile \"%1\"", TUD_Default(classes + @"\InternetShortcut\shell\" + ShellMenu.UrlVerb + @"\command"), "Orden del instalador");

                InstallOptions u = new InstallOptions(); u.Dir = dir; u.KeepUserData = true;
                Installer.Uninstall(u, null);
                Check(!ShellMenu.IsRegistered(classes, exe) && !TUD_KeyExists(classes + @"\InternetShortcut\shell\" + ShellMenu.UrlVerb), "El desinstalador no lo quitó");
                Check(!TUD_KeyExists(classes + @"\IE.AssocFile.URL"), "El desinstalador dejó IE.AssocFile.URL");

                // Un verbo de otro programa en IE.AssocFile.URL sobrevive; y una versión anterior (solo InternetShortcut) gana el nuevo al arrancar.
                string ieForeign = classes + @"\IE.AssocFile.URL\shell\OtroPrograma";
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(ieForeign + @"\command")) k.SetValue("", "otro.exe \"%1\"");
                Installer.Install(o, null);
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(classes + @"\IE.AssocFile.URL\shell")) k.DeleteSubKeyTree(ShellMenu.UrlVerb, false);
                Check(ShellMenu.IsRegistered(classes, exe), "Una versión anterior (sin IE.AssocFile.URL) debe contar como puesto");
                ShellMenu.Register(classes, exe); // lo que hace Relabel al arrancar
                CheckEq("\"" + exe + "\" /urlfile \"%1\"", TUD_Default(classes + @"\IE.AssocFile.URL\shell\" + ShellMenu.UrlVerb + @"\command"), "Al arrancar no se añadió IE.AssocFile.URL");
                Installer.Uninstall(u, null);
                CheckEq("otro.exe \"%1\"", TUD_Default(ieForeign + @"\command"), "El desinstalador tocó el verbo de otro programa");
                Registry.CurrentUser.DeleteSubKeyTree(classes + @"\IE.AssocFile.URL", false);

                // Si después lo activó otra copia (p. ej. una de desarrollo), el desinstalador la respeta.
                Installer.Install(o, null);
                string otherExe = Path.Combine(root, @"desarrollo\IDMClone.exe");
                ShellMenu.Register(classes, otherExe);
                Installer.Uninstall(u, null);
                Check(ShellMenu.IsRegistered(classes, otherExe), "El desinstalador quitó el menú de otra copia");
                ShellMenu.Unregister(classes, otherExe);

                // "/noexplorermenu" o la casilla desmarcada en el asistente: no se añade, y al actualizar se quita.
                Installer.Install(o, null);
                InstallOptions no = new InstallOptions(); no.Dir = dir; no.ExplorerMenu = false;
                Installer.Install(no, null);
                Check(!ShellMenu.IsRegistered(classes, exe) && !TUD_KeyExists(classes + @"\InternetShortcut\shell\" + ShellMenu.UrlVerb), "Sigue con ExplorerMenu = false");
                Installer.Uninstall(u, null);
            });
        }

        static void TUD_OptionsToggle()
        {
            TUD_WithClasses(delegate(string classes)
            {
                UseFreshHome("ui-explorer-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                AppConfig c = Cfg();
                string exe = Application.ExecutablePath;
                MethodInfo apply = typeof(OptionsForm).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance);
                string root = classes.Substring(0, classes.LastIndexOf('\\'));
                // Aceptar también guarda el inicio con Windows: a una clave de pruebas, no a la de verdad.
                string savedRun = Startup.RunKeyPath;
                Startup.RunKeyPath = root + @"\Run";
                try
                {
                    using (OptionsForm o = new OptionsForm(c))
                    {
                        IntPtr h = o.Handle;
                        CheckBox box = Field<CheckBox>(o, "_explorerMenu");
                        Check(box != null && !box.Checked, "La casilla debería salir desmarcada");
                        apply.Invoke(o, null);
                        Check(!TUD_KeyExists(classes), "Aceptar sin cambiar la casilla tocó el registro");
                        box.Checked = true;
                        apply.Invoke(o, null);
                    }
                    Check(ShellMenu.IsRegistered(exe), "Al marcar la casilla no se registró");
                    using (OptionsForm o = new OptionsForm(c))
                    {
                        IntPtr h = o.Handle;
                        CheckBox box = Field<CheckBox>(o, "_explorerMenu");
                        Check(box.Checked, "La casilla no sale marcada cuando está registrado");
                        box.Checked = false;
                        apply.Invoke(o, null);
                    }
                    Check(!ShellMenu.IsRegistered(exe) && !TUD_KeyExists(classes + @"\InternetShortcut\shell\" + ShellMenu.UrlVerb), "Al desmarcar no se quitó");
                }
                finally { Startup.RunKeyPath = savedRun; }
            });
        }

        static List<string> TUD_LinkUrls(LinkListForm f)
        {
            List<string> r = new List<string>();
            foreach (ListViewItem li in Field<ListView>(f, "_list").Items) r.Add(((LinkEntry)li.Tag).Url);
            return r;
        }

        static void TUD_IpcFiles()
        {
            MainForm form = NewMainForm();
            DownloadManager m = Field<DownloadManager>(form, "_mgr");
            try
            {
                string dir = Path.Combine(Home, "explorador-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                Directory.CreateDirectory(dir);
                string txt = Path.Combine(dir, "mis enlaces.txt");
                File.WriteAllText(txt, "https://ejemplo.com/a.zip\r\nhttps://ejemplo.com/b.pdf texto https://ejemplo.com/a.zip\r\nftp://ejemplo.com/c.iso\r\n");
                string reply = form.HandleCommand(CliArgs.Parse(new string[] { ShellMenu.LinksSwitch, txt }).ToCommandLine());
                Check(reply.Contains("\"ok\":true"), "Respuesta a /linksfile: " + reply);
                LinkListForm lf = null;
                PumpUntil(delegate { foreach (Form x in Application.OpenForms) if (x is LinkListForm && x.Visible) { lf = (LinkListForm)x; return true; } return false; }, 10);
                Check(lf != null, "No se abrió la lista de enlaces");
                CheckEq("https://ejemplo.com/a.zip|https://ejemplo.com/b.pdf|ftp://ejemplo.com/c.iso", string.Join("|", TUD_LinkUrls(lf).ToArray()), "Enlaces de la lista");
                CheckEq(Lang.T(ShellMenu.LinksText), lf.Text, "Título de la lista");
                Check(!form.Visible, "Sacó la ventana principal de la bandeja");
                CloseOpenForms(typeof(LinkListForm));

                // /urlfile: el archivo lo lee la instancia abierta y añade la dirección (con /n, sin preguntar).
                string target = S.Url("range", "desde-acceso-directo.zip", 64 * 1024, NewId("urlfile"));
                string url = Path.Combine(dir, "Descarga.url");
                File.WriteAllText(url, "[InternetShortcut]\r\nURL=" + target + "\r\n");
                reply = form.HandleCommand("/urlfile \"" + url + "\" /n");
                Check(reply.Contains("\"id\""), "Respuesta a /urlfile: " + reply);
                DownloadItem got = null;
                foreach (DownloadItem it in m.Snapshot()) if (it.Url == target) got = it;
                Check(got != null, "No se añadió la descarga del acceso directo");
                WaitEnd(got, 30);
                CheckEq(DownloadStatus.Complete, got.Status, "Estado de la descarga del acceso directo");
            }
            finally { CloseOpenForms(typeof(LinkListForm)); try { m.StopAllAndWait(10000); } catch { } DisposeMainForm(form); }
        }

        static void TUD_SpeedFormat()
        {
            // 1016.746 KB/sec (lo que salía en la captura 04): IDM ya lo da en MB/sec.
            long k1016 = 1041148;
            CheckEq("0.99 MB/sec", ListColumns.Speed(k1016), "Lista a 1016 KB/sec");
            CheckEq("0.993 MB/sec", ProgressForm.IdmSpeed(k1016), "Progreso a 1016 KB/sec");
            CheckEq("999.00 KB/sec", ListColumns.Speed(999 * 1024), "Lista a 999 KB/sec");
            CheckEq("999.000 KB/sec", ProgressForm.IdmSpeed(999 * 1024), "Progreso a 999 KB/sec");
            CheckEq("0.98 MB/sec", ListColumns.Speed(1023999), "Lista a 999.999 KB/sec (redondeado a 1000.00)");
            CheckEq("999.999 KB/sec", ProgressForm.IdmSpeed(1023999), "Progreso a 999.999 KB/sec");
            CheckEq("1.106 MB/sec", ProgressForm.IdmSpeed(1159725), "Progreso a 1.106 MB/sec (captura de IDM)");
            CheckEq("1.11 MB/sec", ListColumns.Speed(1159725), "Lista a 1.106 MB/sec");
            CheckEq("523.40 KB/sec", ListColumns.Speed(535962), "Lista a 523.4 KB/sec");
            CheckEq("0.49 KB/sec", ListColumns.Speed(500), "Menos de 1 KB/sec");
            CheckEq("1.50 GB/sec", ListColumns.Speed(1610612736L), "GB/sec");
            CheckEq("", ListColumns.Speed(0), "Parada");
            CheckEq(ListColumns.Speed(k1016), Fmt.Speed(k1016), "Consola igual que la lista");

            // Ventana de progreso y bandeja, con una descarga "en marcha" a 1016 KB/sec.
            MainForm form = NewMainForm();
            DownloadManager m = Field<DownloadManager>(form, "_mgr");
            DownloadItem it = null;
            try
            {
                it = m.Add(S.Url("range", "velocidad.bin", 4096, NewId("vel")), null, null, false);
                it.Status = DownloadStatus.Downloading; it.SpeedBps = k1016;
                typeof(MainForm).GetMethod("UpdateStatus", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, null);
                string tip = Field<NotifyIcon>(form, "_tray").Text;
                Check(tip.Contains("0.99 MB/sec"), "Texto de la bandeja: " + tip);
                using (ProgressForm pf = new ProgressForm(it, m))
                {
                    CheckEq("0.993 MB/sec", Field<Label>(pf, "_speed").Text, "Velocidad en la ventana de progreso");
                    pf.Close();
                }
            }
            finally { if (it != null) { it.Status = DownloadStatus.Paused; it.SpeedBps = 0; } DisposeMainForm(form); }
        }

        static void TUD_ColumnsRows()
        {
            try
            {
                foreach (float k in Scales)
                {
                    using (Form f = Scaled(delegate { return new ColumnsForm(AppConfig.DefaultColumns); }, k))
                    {
                        f.Show(); Application.DoEvents();
                        CheckedListBox l = ((ColumnsForm)f).List;
                        string n = " al " + (int)(k * 100) + " %";
                        int h = l.ClientSize.Height, ih = l.GetItemHeight(0);
                        Check(h % ih == 0, "Última fila cortada" + n + ": alto " + h + ", fila " + ih);
                        Check(h / ih >= 7, "Muy pocas filas" + n + ": " + (h / ih));
                        Check(l.Items.Count > h / ih, "La lista debería tener barra de desplazamiento" + n);
                        foreach (Control c in f.Controls)
                            if (c != l && c.Visible && c.Left < l.Right && c.Right > l.Left && c.Top >= l.Top)
                                Check(c.Top >= l.Bottom, "La lista tapa " + c.GetType().Name + " '" + c.Text + "'" + n);
                        f.Close();
                    }
                }
            }
            finally { Dpi.Override = 0; }
        }

        static void TUD_ProgressArrow()
        {
            // Los fotogramas: del tamaño pequeño, con verde, y distintos entre sí (se mueve).
            int s = SystemInformation.SmallIconSize.Width;
            string prev = null;
            for (int i = 0; i < ActivityArrow.Frames; i++)
            {
                using (Bitmap b = ActivityArrow.Draw(s, i))
                {
                    int green = 0; StringBuilder sig = new StringBuilder();
                    for (int y = 0; y < b.Height; y++)
                        for (int x = 0; x < b.Width; x++)
                        {
                            Color c = b.GetPixel(x, y);
                            if (c.A > 128 && c.G > c.R + 60 && c.G > c.B + 60) green++;
                            sig.Append(c.A > 128 ? '#' : '.');
                        }
                    Check(green > s, "Fotograma " + i + " casi sin verde: " + green);
                    Check(sig.ToString() != prev, "El fotograma " + i + " es igual al anterior");
                    prev = sig.ToString();
                }
                Check(ActivityArrow.Frame(i) != null, "Sin icono para el fotograma " + i);
            }

            DownloadManager m = NewMgr();
            DownloadItem it = new DownloadItem();
            it.Url = "http://127.0.0.1:1/flecha.bin"; it.FilePath = Path.Combine(Dl, "flecha.bin"); it.Size = 1000000;
            it.Status = DownloadStatus.Downloading; it.SpeedBps = 200 * 1024;
            using (ProgressForm pf = new ProgressForm(it, m))
            {
                pf.Show();
                Check(pf.ArrowAnimating, "La flecha no se mueve mientras descarga");
                // Solo cambia el icono pequeño de la barra de título; el de la ventana (barra de tareas) sigue igual.
                List<IntPtr> frames = new List<IntPtr>();
                for (int i = 0; i < ActivityArrow.Frames; i++) frames.Add(ActivityArrow.Frame(i).Handle);
                Check(PumpUntil(delegate { return frames.Contains(ActivityArrow.CaptionIcon(pf)); }, 3), "La barra de título no lleva la flecha");
                IntPtr first = ActivityArrow.CaptionIcon(pf);
                Check(PumpUntil(delegate { IntPtr now = ActivityArrow.CaptionIcon(pf); return now != first && frames.Contains(now); }, 3), "La flecha no se anima");
                Check(pf.Icon == AppIcon.Small, "Cambió el icono grande de la ventana");
                MethodInfo refresh = typeof(ProgressForm).GetMethod("Refresh2", BindingFlags.NonPublic | BindingFlags.Instance);
                it.Status = DownloadStatus.Paused; it.SpeedBps = 0;
                refresh.Invoke(pf, null);
                Check(!pf.ArrowAnimating, "La flecha sigue en pausa");
                Check(ActivityArrow.CaptionIcon(pf) == ActivityArrow.AppSmall.Handle, "En pausa no vuelve el icono de IDMClone");
                it.Status = DownloadStatus.Downloading;
                refresh.Invoke(pf, null);
                Check(pf.ArrowAnimating && frames.Contains(ActivityArrow.CaptionIcon(pf)), "No vuelve a moverse al reanudar");
                it.Status = DownloadStatus.Complete;
                refresh.Invoke(pf, null);
                Check(!pf.ArrowAnimating && ActivityArrow.CaptionIcon(pf) == ActivityArrow.AppSmall.Handle, "La flecha sigue al terminar");
                pf.Close();
            }
        }
    }
}
