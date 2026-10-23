using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace IDMClone.Setup
{
    // IDMClone-Setup.exe: instala IDMClone. Copiado en la carpeta del programa como Uninstall.exe, lo desinstala.
    //   IDMClone-Setup.exe /update [/D=carpeta] [/S]   actualización lanzada por IDMClone ("Actualizar ahora")
    //   IDMClone-Setup.exe [/S] [/D=carpeta] [/nodesktop] [/noautostart] [/noexplorermenu]
    //   Uninstall.exe [/S] [/removedata]          (o IDMClone-Setup.exe /uninstall)
    static class SetupProgram
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool silent = false, uninstall = false, update = false;
            InstallOptions o = new InstallOptions();
            foreach (string a in args)
            {
                string l = a.ToLowerInvariant();
                if (l == "/s" || l == "/silent") silent = true;
                else if (l == "/update") update = true;
                else if (l == "/uninstall") uninstall = true;
                else if (l.StartsWith("/d=")) o.Dir = a.Substring(3).Trim('"');
                else if (l == "/nodesktop") o.DesktopShortcut = false;
                else if (l == "/noautostart") o.AutoStart = false;
                else if (l == "/noexplorermenu") o.ExplorerMenu = false;
                else if (l == "/removedata") o.KeepUserData = false;
            }
            string self = Path.GetFileNameWithoutExtension(Installer.SelfPath);
            if (self.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)) uninstall = true;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            UseSystemLanguage();

            if (update && !uninstall) return RunUpdate(o, silent);
            if (silent)
            {
                try
                {
                    if (uninstall) Installer.Uninstall(o, null);
                    else { o.Dir = Installer.OwnFolder(o.Dir ?? Installer.InstalledDir() ?? Installer.DefaultDir); Installer.Install(o, null); }
                    return 0;
                }
                catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
            }
            if (uninstall)
            {
                // Como en modo silencioso: el Uninstall.exe de una instalación a medias (sin IDMClone.exe) también limpia.
                if (Installer.InstalledDir() == null && !File.Exists(Path.Combine(Path.GetDirectoryName(Installer.SelfPath), Installer.ExeName))
                    && !Installer.RunningAsUninstaller)
                {
                    Msg.Show(null, Lang.T("IDMClone no está instalado."), "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 1;
                }
                Application.Run(new UninstallForm());
                return 0;
            }
            Application.Run(new SetupForm());
            return 0;
        }

        // Para las pruebas: qué se abriría al terminar la actualización (null = IDMClone.exe de verdad).
        internal static Action<string> RestartHook = null;

        // Modo actualización: reinstala sin asistente en la carpeta de la instalación (la de /D= o la registrada),
        // conserva lo que el usuario eligió la otra vez (acceso directo del escritorio, inicio con Windows) y vuelve a
        // abrir IDMClone. Con /S no muestra nada; si no, una ventanita de progreso (y el error, si lo hay).
        internal static int RunUpdate(InstallOptions o, bool silent)
        {
            o.Dir = Installer.OwnFolder(o.Dir ?? Installer.InstalledDir() ?? Installer.DefaultDir);
            Installer.KeepChoices(o);
            string error = null;
            if (silent)
            {
                try { Installer.Install(o, null); }
                catch (Exception ex) { error = ex.Message; Console.Error.WriteLine(ex.Message); }
            }
            else
            {
                using (UpdateProgressForm f = new UpdateProgressForm(o))
                {
                    Application.Run(f);
                    error = f.Error;
                }
            }
            // También si falla: IDMClone ya se cerró para dejar actualizar; se vuelve a abrir el que haya (el anterior,
            // si no se llegó a sustituir) para que el usuario no se quede sin él.
            string exe = Path.Combine(o.Dir, Installer.ExeName);
            if (error != null && !File.Exists(exe)) return 1;
            if (RestartHook != null) RestartHook(exe);
            else
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { WorkingDirectory = o.Dir, UseShellExecute = false }); }
                catch { }
            }
            return error == null ? 0 : 1;
        }

        // Como el instalador de IDM: en el idioma de Windows (español, chino simplificado, japonés, o inglés
        // si no es ninguno de ellos).
        static void UseSystemLanguage()
        {
            string code = SetupLanguage(Thread.CurrentThread.CurrentUICulture);
            if (code == null) return;
            string t = Installer.PayloadText("Languages/idmclone_" + code + ".lng");
            if (t == null && code != "en") { code = "en"; t = Installer.PayloadText("Languages/idmclone_en.lng"); }
            if (t == null) return;
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "idmclone-setup-" + code + ".lng");
                File.WriteAllText(tmp, t, new System.Text.UTF8Encoding(false));
                Lang.LoadFile(tmp);
                File.Delete(tmp);
            }
            catch { }
        }

        // Idioma del instalador según el de Windows: null = español (el del programa).
        public static string SetupLanguage(System.Globalization.CultureInfo ui)
        {
            string two = ui.TwoLetterISOLanguageName;
            if (two == "es") return null;
            if (two == "ja") return "ja";
            // Chino simplificado (China, Singapur); el tradicional (Taiwán, Hong Kong, Macao) va en inglés.
            string n = ui.Name.ToLowerInvariant();
            if (n == "zh-cn" || n == "zh-sg" || n.StartsWith("zh-hans")) return "zh";
            return "en";
        }
    }

    // "Actualizando IDMClone": solo progreso (la actualización la pidió el usuario desde IDMClone). Si falla, lo dice.
    public class UpdateProgressForm : DialogBase
    {
        readonly InstallOptions _o;
        ProgressBar _bar;
        Label _status;
        bool _working;
        public string Error { get; private set; }

        public UpdateProgressForm(InstallOptions o)
        {
            _o = o;
            Text = "Actualizando IDMClone";
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            ControlBox = false;
            ClientSize = new Size(400, 92);
            PictureBox icon = new PictureBox(); icon.SetBounds(14, 14, 32, 32); icon.Image = AppIcon.Sized(32).ToBitmap(); Controls.Add(icon);
            Label l = AddLabel("Instalando la nueva versión de IDMClone...", 56, 12, 330); l.Height = 20;
            _status = AddLabel("", 56, 34, 330); _status.Height = 18; _status.AutoEllipsis = true;
            _bar = new ProgressBar(); _bar.SetBounds(14, 62, 372, 18); Controls.Add(_bar);
            EndLayout();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _working = true;
            Thread th = new Thread(delegate()
            {
                string err = null;
                try { Installer.Install(_o, delegate(int pct, string m) { BeginInvoke((Action)delegate { _bar.Value = Math.Min(100, pct); _status.Text = Lang.T(m); }); }); }
                catch (Exception ex) { err = ex.Message; }
                BeginInvoke((Action)delegate
                {
                    _working = false;
                    Error = err;
                    if (err != null) Msg.Show(this, Lang.T("No se pudo actualizar IDMClone: " + err), "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Close();
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_working) e.Cancel = true;
            base.OnFormClosing(e);
        }
    }

    // "Desinstalar IDMClone": confirmación, conservar o no los datos, progreso y aviso final.
    public class UninstallForm : DialogBase
    {
        CheckBox _keep;
        ProgressBar _bar;
        Label _msg, _status;
        Button _go, _cancel;
        bool _working, _done;

        public UninstallForm()
        {
            Text = "Desinstalar IDMClone";
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            ClientSize = new Size(480, 220);
            PictureBox icon = new PictureBox(); icon.SetBounds(16, 16, 32, 32); icon.Image = AppIcon.Sized(32).ToBitmap(); Controls.Add(icon);
            _msg = AddLabel("¿Seguro que quieres desinstalar IDMClone por completo?", 60, 16, 400);
            _keep = new CheckBox(); _keep.Text = "Conservar la lista de descargas y la configuración"; _keep.Checked = true; _keep.SetBounds(60, 50, 400, 22); Controls.Add(_keep);
            Label n = AddLabel("La extensión del navegador se quita desde su página de extensiones (chrome://extensions...).", 60, 76, 404);
            n.Height = 36; n.ForeColor = SystemColors.GrayText;
            _status = AddLabel("", 16, 118, 448);
            _bar = new ProgressBar(); _bar.SetBounds(16, 144, 448, 20); Controls.Add(_bar);
            _go = AddButton("Desinstalar", 278, 180, 90, DialogResult.None);
            _cancel = AddButton("Cancelar", 374, 180, 90, DialogResult.Cancel);
            AcceptButton = _go; CancelButton = _cancel;
            _go.Click += delegate { if (_done) Close(); else Start(); };
            EndLayout();
        }

        void Start()
        {
            InstallOptions o = new InstallOptions();
            o.KeepUserData = _keep.Checked;
            _working = true; _go.Enabled = false; _cancel.Enabled = false; _keep.Enabled = false;
            Thread th = new Thread(delegate()
            {
                Exception err = null;
                try { Installer.Uninstall(o, delegate(int pct, string m) { BeginInvoke((Action)delegate { _bar.Value = Math.Min(100, pct); _status.Text = Lang.T(m); }); }); }
                catch (Exception ex) { err = ex; }
                BeginInvoke((Action)delegate
                {
                    _working = false; _done = true;
                    _msg.Text = err == null ? Lang.T("IDMClone se ha desinstalado.") : Lang.T("No se pudo desinstalar del todo: " + err.Message);
                    _go.Text = Lang.T("Cerrar"); _go.Enabled = true;
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_working) e.Cancel = true;
            base.OnFormClosing(e);
        }
    }
}
