using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace IDMClone.Tests
{
    // "Cancelando una descarga iniciada automáticamente" (como IDM 6.43): al cancelar otra vez una captura automática
    // del navegador desde el mismo sitio, ofrece añadir el sitio o un patrón a las excepciones.
    public static partial class TestMain
    {
        static void RunAutoCancel()
        {
            Run("cancelar captura automática: sitio y patrón como IDM", TAC_SiteAndPattern);
            Run("cancelar captura automática: a la segunda, con el patrón marcado por defecto", TAC_SecondCancelAddsPattern);
            Run("cancelar captura automática: 'No mostrar este diálogo otra vez'", TAC_DontAskAgain);
            Run("instalador: al actualizar con la extensión ya conectada no se abre la guía", TAC_SetupKnownExtension);
            Run("cancelar captura automática: con el botón Cancelar de 'Información del archivo', dos veces", TAC_FlowThroughFileInfo);
        }

        static Button FindButton(Control parent, string text)
        {
            foreach (Control c in parent.Controls)
            {
                Button b = c as Button;
                if (b != null && b.Text == text) return b;
                Button r = FindButton(c, text);
                if (r != null) return r;
            }
            return null;
        }

        static void TAC_FlowThroughFileInfo()
        {
            MainForm form = NewMainForm();
            DownloadManager m = Field<DownloadManager>(form, "_mgr");
            Func<AutoCancelForm, DialogResult> old = MainForm.AutoCancelHook;
            int shown = 0;
            try
            {
                AppConfig c = m.Config;
                c.ShowFileInfoDialog = true; c.StartWhileInfoDialog = true;
                MainForm.AutoCancelHook = delegate(AutoCancelForm d) { shown++; return DialogResult.Cancel; };
                for (int i = 0; i < 2; i++)
                {
                    AddOptions o = new AddOptions();
                    o.Url = S.Url("range,veryslow", "auto" + i + ".zip", 3L * 1024 * 1024, NewId("tacf" + i));
                    o.StartNow = true; o.AutoCaptured = true;
                    form.NewDownload(o, true);
                    FileInfoForm fi = null;
                    PumpUntil(delegate { foreach (Form x in Application.OpenForms) if (x is FileInfoForm && !x.IsDisposed && x.Visible) { fi = (FileInfoForm)x; return true; } return false; }, 10);
                    Check(fi != null, "No salió 'Información del archivo' (" + (i + 1) + ")");
                    Button cancel = FindButton(fi, "Cancelar");
                    Check(cancel != null, "No está el botón Cancelar");
                    cancel.PerformClick();
                    FileInfoForm closing = fi;
                    PumpUntil(delegate { return closing.IsDisposed || !closing.Visible; }, 5);
                    Check(closing.IsDisposed || !closing.Visible, "La ventana no se cerró con Cancelar (" + (i + 1) + "ª vez)");
                }
                PumpUntil(delegate { return shown > 0; }, 5);
                CheckEq(1, shown, "Veces que salió 'Cancelando una descarga iniciada automáticamente'");
            }
            finally { MainForm.AutoCancelHook = old; CloseOpenForms(typeof(FileInfoForm)); try { m.StopAllAndWait(10000); } catch { } DisposeMainForm(form); }
        }

        static void TAC_SetupKnownExtension()
        {
            UseFreshHome("setup-ext-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            System.Reflection.MethodInfo known2 = typeof(IDMClone.Setup.SetupForm).GetMethod("KnownExtensionBrowsers",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static, null, new Type[] { typeof(string), typeof(string) }, null);
            Check(known2 != null, "Falta SetupForm.KnownExtensionBrowsers(dir, installed)");
            string dir = System.IO.Path.Combine(Home, "instalado-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            System.IO.Directory.CreateDirectory(dir);
            string seen = System.IO.Path.Combine(AppConfig.Dir, ExtensionSeen.FileName);
            System.IO.Directory.CreateDirectory(AppConfig.Dir);
            System.IO.File.WriteAllText(seen, "Microsoft Edge\t" + DateTime.UtcNow.AddDays(-2).ToString("o") + "\tchrome-extension://x/\r\n" +
                                              "Brave\t" + DateTime.UtcNow.AddDays(-400).ToString("o") + "\tchrome-extension://x/\r\n");
            Func<string, List<string>> known = delegate(string installed) { return (List<string>)known2.Invoke(null, new object[] { dir, installed }); };
            CheckEq(0, known(dir).Count, "Instalación nueva (sin IDMClone.exe): siempre la guía");
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, IDMClone.Setup.Installer.ExeName), "x");
            List<string> r = known(dir + "\\");
            CheckEq(1, r.Count, "Actualización: el navegador conectado hace poco (no el de hace más de 90 días)");
            CheckEq("Microsoft Edge", r[0], "navegador");
            CheckEq(0, known(null).Count, "Sin instalación registrada: la guía");
            CheckEq(0, known(System.IO.Path.Combine(Home, "otra")).Count, "Instalada en otra carpeta: la extensión cambia de ruta, guía");
            System.IO.File.Delete(seen);
            CheckEq(0, known(dir).Count, "Actualización sin la extensión conectada nunca: la guía");
        }

        static void CheckEq<T>(T expected, T actual, string what)
        {
            Check(EqualityComparer<T>.Default.Equals(expected, actual), what + ": se esperaba «" + expected + "» y es «" + actual + "»");
        }

        static void TAC_SiteAndPattern()
        {
            CheckEq("127.0.0.1", AutoCancelForm.HostOf("http://127.0.0.1:18765/f/range/documento.pdf?size=1&id=z3"), "sitio");
            CheckEq("http://127.0.0.1:18765/f/range/*", AutoCancelForm.PatternOf("http://127.0.0.1:18765/f/range/documento.pdf?size=1&id=z3"), "patrón");
            CheckEq("https://descargas.ejemplo.com/*", AutoCancelForm.PatternOf("https://Descargas.Ejemplo.com/setup.exe"), "patrón en la raíz");
            CheckEq("", AutoCancelForm.HostOf("no es una url"), "sin url");
            // Como lo ve la extensión (new URL): punycode y sin el puerto por defecto.
            CheckEq("xn--mnchen-3ya.de", AutoCancelForm.HostOf("http://münchen.de:8080/a/b.zip"), "sitio IDN");
            CheckEq("http://xn--mnchen-3ya.de:8080/a/*", AutoCancelForm.PatternOf("http://münchen.de:8080/a/b.zip"), "patrón IDN con puerto");
            CheckEq("https://x.com/a/*", AutoCancelForm.PatternOf("https://x.com:443/a/b.zip"), "puerto por defecto");
            CheckEq("http://[::1]:8080/a/*", AutoCancelForm.PatternOf("http://[::1]:8080/a/b.zip"), "IPv6 con corchetes");
            AppConfig c = AppConfig.CreateDefault();
            Check(c.AskOnAutoCaptureCancel, "Por defecto se pregunta (como IDM)");
            Check(c.AddCaptureExclusion("ejemplo.com"), "No se añadió");
            Check(!c.AddCaptureExclusion("EJEMPLO.com"), "Se añadió dos veces");
            Check(!c.AddCaptureExclusion("dos palabras"), "Una entrada con espacios rompería la lista");
            Check((" " + c.CaptureExcludeSites + " ").Contains(" ejemplo.com "), "No está en la lista: " + c.CaptureExcludeSites);
        }

        static void TAC_SecondCancelAddsPattern()
        {
            MainForm f = NewMainForm();
            Func<AutoCancelForm, DialogResult> old = MainForm.AutoCancelHook;
            int shown = 0;
            try
            {
                MainForm.AutoCancelHook = delegate(AutoCancelForm d) { shown++; return DialogResult.OK; };
                AppConfig cfg = Field<AppConfig>(f, "_cfg");
                f.AutoCaptureCancelled("http://127.0.0.1:18765/f/range/a.zip");
                CheckEq(0, shown, "A la primera no se pregunta");
                f.AutoCaptureCancelled("http://otro.com/b.zip");
                CheckEq(0, shown, "Otro sitio no cuenta");
                f.AutoCaptureCancelled("http://127.0.0.1:18765/f/range/b.pdf");
                CheckEq(1, shown, "A la segunda del mismo sitio, sí");
                string ex = " " + cfg.CaptureExcludeSites + " ";
                Check(ex.Contains(" http://127.0.0.1:18765/f/range/* "), "Falta el patrón: " + cfg.CaptureExcludeSites);
                Check(!ex.Contains(" 127.0.0.1 "), "El sitio viene desmarcado y se añadió igual");
                f.AutoCaptureCancelled("http://127.0.0.1:18765/f/range/c.pdf");
                CheckEq(1, shown, "Tras preguntar, vuelven a hacer falta dos");
                // Marcando también el sitio.
                MainForm.AutoCancelHook = delegate(AutoCancelForm d) { shown++; Field<CheckBox>(d, "_site").Checked = true; return DialogResult.OK; };
                f.AutoCaptureCancelled("http://127.0.0.1:18765/f/range/d.pdf");
                CheckEq(2, shown, "La cuarta vuelve a preguntar");
                Check((" " + cfg.CaptureExcludeSites + " ").Contains(" 127.0.0.1 "), "Falta el sitio: " + cfg.CaptureExcludeSites);
                // Se guardó en disco: lo lee la extensión (getconfig).
                AppConfig disk = AppConfig.Load();
                Check((" " + disk.CaptureExcludeSites + " ").Contains(" http://127.0.0.1:18765/f/range/* "), "No se guardó: " + disk.CaptureExcludeSites);
            }
            finally { MainForm.AutoCancelHook = old; DisposeMainForm(f); }
        }

        static void TAC_DontAskAgain()
        {
            MainForm f = NewMainForm();
            Func<AutoCancelForm, DialogResult> old = MainForm.AutoCancelHook;
            int shown = 0;
            try
            {
                AppConfig cfg = Field<AppConfig>(f, "_cfg");
                string before = cfg.CaptureExcludeSites;
                MainForm.AutoCancelHook = delegate(AutoCancelForm d) { shown++; Field<CheckBox>(d, "_dontAsk").Checked = true; return DialogResult.Cancel; };
                f.AutoCaptureCancelled("https://x.com/a.zip");
                f.AutoCaptureCancelled("https://x.com/b.zip");
                CheckEq(1, shown, "Debía preguntar una vez");
                CheckEq(before, cfg.CaptureExcludeSites, "Con Cancelar no se añade nada");
                Check(!cfg.AskOnAutoCaptureCancel, "La casilla no se guardó");
                f.AutoCaptureCancelled("https://x.com/c.zip");
                f.AutoCaptureCancelled("https://x.com/d.zip");
                CheckEq(1, shown, "Volvió a preguntar con 'No mostrar este diálogo otra vez'");
            }
            finally { MainForm.AutoCancelHook = old; DisposeMainForm(f); }
        }
    }
}
