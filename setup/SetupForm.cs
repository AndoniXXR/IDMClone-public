using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace IDMClone.Setup
{
    // Asistente de instalación, como el de IDM: Bienvenida -> Licencia -> Carpeta y opciones ->
    // Instalando -> Finalizar (con cómo añadir la extensión a cada navegador).
    public class SetupForm : Form
    {
        protected Panel _side, _header, _body;
        protected Label _title, _subtitle;
        protected Button _back, _next, _cancel;
        readonly List<Control> _pages = new List<Control>();
        protected int _page;

        // Páginas
        RadioButton _accept, _decline;
        TextBox _license;
        TextBox _dir;
        CheckBox _desktop, _autostart, _explorer, _runNow;
        ProgressBar _bar;
        Label _status;
        bool _installed, _working;
        public InstallOptions Options = new InstallOptions();
        public Exception Failure;

        public SetupForm() : this(true) { }

        protected SetupForm(bool build)
        {
            Text = Lang.T("Instalación de IDMClone");
            Icon = AppIcon.Small;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None; // se escala a mano al terminar (Dpi.ScaleForm, como los diálogos)
            SuspendLayout();
            ClientSize = new Size(600, 400);
            Font = SystemFonts.MessageBoxFont;
            // En chino, la letra con ideogramas chinos (ver Theme.UiFont), al tamaño de la del sistema.
            if (Theme.UiFamily != "Tahoma") Font = new Font(Theme.UiFamily, Font.SizeInPoints, FontStyle.Regular, GraphicsUnit.Point);

            _side = new Panel(); _side.SetBounds(0, 0, 164, 352); _side.Paint += PaintSide; Controls.Add(_side);
            _header = new Panel(); _header.SetBounds(0, 0, 600, 62); _header.BackColor = Color.White; _header.Paint += PaintHeader; Controls.Add(_header);
            _title = new Label(); _title.SetBounds(16, 10, 500, 22); _title.Font = new Font(Font, FontStyle.Bold); _title.BackColor = Color.White; _header.Controls.Add(_title);
            _subtitle = new Label(); _subtitle.SetBounds(30, 32, 480, 22); _subtitle.BackColor = Color.White; _header.Controls.Add(_subtitle);
            _body = new Panel(); _body.SetBounds(0, 62, 600, 290); Controls.Add(_body);
            Label line = new Label(); line.BorderStyle = BorderStyle.Fixed3D; line.SetBounds(0, 352, 600, 2); Controls.Add(line);
            _back = Btn("< Atrás", 316, 364); _next = Btn("Siguiente >", 406, 364); _cancel = Btn("Cancelar", 504, 364);
            _back.Click += delegate { ShowPage(_page - 1); };
            _next.Click += delegate { Next(); };
            _cancel.Click += delegate { Close(); };
            AcceptButton = _next;
            if (build) { BuildPages(); ShowPage(0); }
            ResumeLayout(false);
            Dpi.ScaleForm(this);
            PerformLayout();
        }

        Button Btn(string text, int x, int y)
        {
            Button b = new Button(); b.Text = Lang.T(text); b.SetBounds(x, y, 86, 26); Controls.Add(b); return b;
        }

        protected Panel NewPage(bool wide)
        {
            Panel p = new Panel();
            if (wide) p.SetBounds(164, 0, 436, 352); else p.SetBounds(0, 62, 600, 290);
            p.Visible = false;
            Controls.Add(p); p.BringToFront();
            _pages.Add(p);
            return p;
        }

        protected static Label Lbl(Control parent, string text, int x, int y, int w, int h)
        {
            Label l = new Label(); l.Text = Lang.T(text); l.SetBounds(x, y, w, h); parent.Controls.Add(l); return l;
        }

        void BuildPages()
        {
            // 0 Bienvenida
            Panel p = NewPage(true);
            Label t = Lbl(p, "Bienvenido al asistente de instalación de IDMClone", 20, 20, 400, 50); t.Font = new Font(Font.FontFamily, 12f, FontStyle.Bold);
            Lbl(p, "Este asistente instalará IDMClone " + Installer.Version + " en este equipo.\r\n\r\n" +
                    "IDMClone acelera las descargas con varias conexiones, las reanuda si se cortan, las organiza " +
                    "en categorías y colas, y se integra en Chrome, Edge, Brave, Opera, Vivaldi y Firefox.\r\n\r\n" +
                    "Si IDMClone está abierto, se cerrará para actualizarlo.\r\n\r\nPulsa Siguiente para continuar.", 20, 80, 400, 220);

            // 1 Licencia
            p = NewPage(false);
            TextBox lic = new TextBox(); lic.Multiline = true; lic.ReadOnly = true; lic.ScrollBars = ScrollBars.Vertical; lic.BackColor = SystemColors.Window;
            lic.SetBounds(20, 12, 560, 200); lic.Text = License(); p.Controls.Add(lic); _license = lic;
            _accept = new RadioButton(); _accept.Text = Lang.T("Acepto los términos del acuerdo de licencia"); _accept.SetBounds(20, 222, 500, 22); p.Controls.Add(_accept);
            _decline = new RadioButton(); _decline.Text = Lang.T("No acepto los términos del acuerdo de licencia"); _decline.SetBounds(20, 246, 500, 22); _decline.Checked = true; p.Controls.Add(_decline);
            _accept.CheckedChanged += delegate { UpdateButtons(); };

            // 2 Carpeta y opciones
            p = NewPage(false);
            Lbl(p, "IDMClone se instalará en la siguiente carpeta. Para instalarlo en otra, pulsa Examinar.", 20, 14, 560, 36);
            _dir = new TextBox(); _dir.SetBounds(20, 54, 456, 23); _dir.Text = Installer.InstalledDir() ?? Installer.DefaultDir; p.Controls.Add(_dir);
            Button browse = new Button(); browse.Text = Lang.T("Examinar..."); browse.SetBounds(484, 52, 96, 26); p.Controls.Add(browse);
            browse.Click += delegate
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.SelectedPath = Directory.Exists(_dir.Text) ? _dir.Text : Path.GetDirectoryName(_dir.Text);
                    if (d.ShowDialog(this) == DialogResult.OK)
                        _dir.Text = string.Equals(Path.GetFileName(d.SelectedPath), "IDMClone", StringComparison.OrdinalIgnoreCase) ? d.SelectedPath : Path.Combine(d.SelectedPath, "IDMClone");
                }
            };
            Label note = Lbl(p, "Se instala solo para tu usuario: no hace falta ser administrador.", 20, 82, 560, 22); note.ForeColor = SystemColors.GrayText;
            _desktop = new CheckBox(); _desktop.Text = Lang.T("Crear un acceso directo en el escritorio"); _desktop.Checked = true; _desktop.SetBounds(20, 120, 560, 22); p.Controls.Add(_desktop);
            _autostart = new CheckBox(); _autostart.Text = Lang.T("Iniciar IDMClone al iniciar Windows"); _autostart.Checked = true; _autostart.SetBounds(20, 146, 560, 22); p.Controls.Add(_autostart);
            _explorer = new CheckBox(); _explorer.Text = Lang.T("Integrar en el menú contextual del Explorador"); _explorer.Checked = true; _explorer.SetBounds(20, 172, 560, 22); p.Controls.Add(_explorer);
            Label n2 = Lbl(p, "También se registrará la integración con los navegadores y se añadirá a «Aplicaciones instaladas» para poder desinstalarlo.", 20, 208, 560, 40);
            n2.ForeColor = SystemColors.GrayText;

            // 3 Instalando
            p = NewPage(false);
            _status = Lbl(p, "", 20, 30, 560, 22);
            _bar = new ProgressBar(); _bar.SetBounds(20, 60, 560, 22); p.Controls.Add(_bar);

            // 4 Finalizar
            p = NewPage(true);
            _finishTitle = t = Lbl(p, "IDMClone se ha instalado", 20, 16, 400, 26); t.Font = new Font(Font.FontFamily, 12f, FontStyle.Bold);
            _finishText = Lbl(p, "Falta un paso: añadir la extensión «IDMClone Integration» a tu navegador. Los navegadores solo " +
                    "instalan solas las extensiones de su tienda, así que se hace una vez a mano.\r\n\r\n" +
                    "Se ha abierto una guía en tu navegador con los pasos, y la ruta de la extensión ya está copiada " +
                    "para pegarla.", 20, 48, 404, 112);
            Button guide = new Button(); guide.Text = Lang.T("Ver la guía"); guide.SetBounds(20, 166, 130, 26); p.Controls.Add(guide);
            Button copyExt = new Button(); copyExt.Text = Lang.T("Copiar la ruta"); copyExt.SetBounds(156, 166, 120, 26); p.Controls.Add(copyExt);
            Button openExt = new Button(); openExt.Text = Lang.T("Abrir la carpeta"); openExt.SetBounds(282, 166, 120, 26); p.Controls.Add(openExt);
            guide.Click += delegate { OpenGuide(); };
            openExt.Click += delegate { try { Process.Start("explorer.exe", "\"" + ExtensionDir + "\""); } catch { } };
            copyExt.Click += delegate { CopyPath(); };
            _seen = Lbl(p, "", 20, 206, 404, 44);
            _seenTimer = new System.Windows.Forms.Timer(); _seenTimer.Interval = 1000; _seenTimer.Tick += delegate { CheckSeen(); };
            _runNow = new CheckBox(); _runNow.Text = Lang.T("Ejecutar IDMClone"); _runNow.Checked = true; _runNow.SetBounds(20, 300, 300, 22); p.Controls.Add(_runNow);
        }

        string ExtensionDir { get { return Installer.ExtensionDir(Options.Dir ?? Installer.DefaultDir); } }

        Label _seen, _finishTitle, _finishText;
        System.Windows.Forms.Timer _seenTimer;
        DateTime _installedUtc;

        // Actualización de una instalación cuya extensión ya se había conectado: no hace falta volver a añadirla,
        // así que no se abre la guía (solo se recuerda recargarla si se cargó "sin empaquetar").
        static List<string> KnownExtensionBrowsers(string dir) { return KnownExtensionBrowsers(dir, Installer.InstalledDir()); }

        // installed: la carpeta de la instalación registrada (null = ninguna). Otra carpeta = la extensión estará en otra
        // ruta y hay que volver a añadirla: guía.
        static List<string> KnownExtensionBrowsers(string dir, string installed)
        {
            List<string> r = new List<string>();
            if (string.IsNullOrEmpty(installed) || !string.Equals(Path.GetFullPath(installed).TrimEnd('\\'), Path.GetFullPath(dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return r;
            if (!File.Exists(Path.Combine(dir, Installer.ExeName))) return r; // instalación nueva
            foreach (ExtensionSeen.Entry e in ExtensionSeen.Read())
                if (e.Browser != "?" && DateTime.UtcNow - e.WhenUtc < TimeSpan.FromDays(90) && !r.Contains(e.Browser)) r.Add(e.Browser);
            return r;
        }

        bool _updated;

        void ShowUpdated(List<string> browsers)
        {
            _updated = true;
            CheckSeen();
            _finishTitle.Text = Lang.T("IDMClone se ha actualizado");
            _finishText.Text = Lang.T("La extensión «IDMClone Integration» ya estaba añadida") + " (" + string.Join(", ", browsers.ToArray()) + "). " +
                Lang.T("Si la cargaste sin empaquetar, pulsa «Volver a cargar» en la página de extensiones del navegador (o reinícialo) para usar la versión nueva.");
        }

        void OpenGuide()
        {
            string g = Installer.GuidePath(Options.Dir ?? Installer.DefaultDir);
            try { if (File.Exists(g)) Process.Start(new ProcessStartInfo(g) { UseShellExecute = true }); } catch { }
        }

        void CopyPath() { try { Clipboard.SetText(ExtensionDir); } catch { } }

        // ✓ en cuanto la extensión se conecta con IDMClone en algún navegador (lo anota el host).
        void CheckSeen()
        {
            List<string> b = ExtensionSeen.Since(_installedUtc);
            if (b.Count == 0)
            {
                _seen.ForeColor = SystemColors.GrayText;
                _seen.Text = _updated ? "" : Lang.T("Esperando a que añadas la extensión..."); // al actualizar ya estaba añadida
                return;
            }
            _seen.ForeColor = Color.FromArgb(30, 138, 60);
            _seen.Text = "✓ " + Lang.T("La extensión ya está conectada con IDMClone en " + string.Join(", ", b.ToArray()) + ".");
        }

        static string License()
        {
            string t = Installer.PayloadText("LICENCIA.txt");
            return string.IsNullOrEmpty(t) ? "IDMClone" : t.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        static readonly string[,] Headers =
        {
            { "", "" },
            { "Acuerdo de licencia", "Lee con atención el siguiente acuerdo de licencia." },
            { "Carpeta de destino y opciones", "Elige dónde instalar IDMClone y qué crear." },
            { "Instalando", "Espera mientras se instala IDMClone." },
            { "", "" },
        };

        protected virtual void ShowPage(int i)
        {
            if (i < 0 || i >= _pages.Count) return;
            _page = i;
            for (int k = 0; k < _pages.Count; k++) _pages[k].Visible = k == i;
            bool wide = _pages[i].Left > 0;
            _side.Visible = wide; _header.Visible = !wide;
            if (!wide) { _title.Text = Lang.T(Headers[i, 0]); _subtitle.Text = Lang.T(Headers[i, 1]); }
            UpdateButtons();
        }

        protected virtual void UpdateButtons()
        {
            int last = _pages.Count - 1;
            _back.Enabled = _page > 0 && _page < 3 && !_working;
            _next.Enabled = !_working && (_page != 1 || _accept.Checked);
            _next.Text = Lang.T(_page == last ? "Finalizar" : _page == 2 ? "Instalar" : "Siguiente >");
            _cancel.Enabled = !_working && _page != last;
        }

        protected virtual void Next()
        {
            if (_page == _pages.Count - 1) { Finish(); return; }
            if (_page == 2) { StartInstall(); return; }
            ShowPage(_page + 1);
        }

        void StartInstall()
        {
            string dir = _dir.Text.Trim();
            try { dir = Path.GetFullPath(dir); }
            catch { Msg.Show(this, Lang.T("La carpeta no es válida."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            // Una carpeta con otras cosas (el Escritorio, Descargas...) no se usa tal cual: IDMClone va en su subcarpeta.
            dir = Installer.OwnFolder(dir);
            Options.Dir = dir; Options.DesktopShortcut = _desktop.Checked; Options.AutoStart = _autostart.Checked; Options.ExplorerMenu = _explorer.Checked;
            List<string> known = KnownExtensionBrowsers(dir);
            ShowPage(3);
            _working = true; UpdateButtons();
            Thread th = new Thread(delegate()
            {
                Exception err = null;
                try { Installer.Install(Options, delegate(int pct, string msg) { BeginInvoke((Action)delegate { _bar.Value = Math.Min(100, pct); _status.Text = Lang.T(msg); }); }); }
                catch (Exception ex) { err = ex; }
                BeginInvoke((Action)delegate
                {
                    _working = false;
                    if (err != null)
                    {
                        Failure = err;
                        Msg.Show(this, Lang.T("No se pudo instalar IDMClone: " + err.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        ShowPage(2);
                        return;
                    }
                    _installed = true;
                    _installedUtc = DateTime.UtcNow.AddSeconds(-5);
                    ShowPage(4);
                    CheckSeen();
                    _seenTimer.Start();
                    if (known.Count > 0) ShowUpdated(known);
                    else if (!Quiet) { CopyPath(); OpenGuide(); }
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        void Finish()
        {
            if (_installed && _runNow.Checked)
                try { Process.Start(new ProcessStartInfo(Path.Combine(Options.Dir, Installer.ExeName)) { WorkingDirectory = Options.Dir }); } catch { }
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_working) { e.Cancel = true; return; }
            if (!_installed && e.CloseReason == CloseReason.UserClosing && _page < 3 && !Quiet &&
                Msg.Show(this, Lang.T("¿Seguro que quieres salir del asistente de instalación?"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                e.Cancel = true;
            base.OnFormClosing(e);
        }

        public bool Quiet; // las pruebas cierran sin preguntar

        // Banner de la izquierda (Bienvenida y Finalizar): degradado con el icono de IDMClone.
        void PaintSide(object sender, PaintEventArgs e)
        {
            Rectangle r = _side.ClientRectangle;
            using (LinearGradientBrush b = new LinearGradientBrush(r, Color.FromArgb(24, 92, 178), Color.FromArgb(8, 40, 96), 90f)) e.Graphics.FillRectangle(b, r);
            int s = Dpi.Px(64);
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using (Icon ic = AppIcon.Sized(s)) e.Graphics.DrawIcon(ic, new Rectangle((r.Width - s) / 2, Dpi.Px(40), s, s));
            using (Font f = new Font(Font.FontFamily, 13f, FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, "IDMClone", f, new Rectangle(0, Dpi.Px(116), r.Width, Dpi.Px(30)), Color.White, TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(e.Graphics, Installer.Version, Font, new Rectangle(0, Dpi.Px(146), r.Width, Dpi.Px(20)), Color.FromArgb(200, 220, 255), TextFormatFlags.HorizontalCenter);
        }

        void PaintHeader(object sender, PaintEventArgs e)
        {
            int s = Dpi.Px(40);
            using (Icon ic = AppIcon.Sized(s)) e.Graphics.DrawIcon(ic, new Rectangle(_header.Width - s - Dpi.Px(14), (_header.Height - s) / 2, s, s));
            using (Pen p = new Pen(SystemColors.ControlDark)) e.Graphics.DrawLine(p, 0, _header.Height - 1, _header.Width, _header.Height - 1);
        }

        // Para las pruebas.
        public int Page { get { return _page; } }
        public void GoNext() { Next(); }
        public void AcceptLicense() { _accept.Checked = true; }
        public void SetDir(string d) { _dir.Text = d; }
        public bool Working { get { return _working; } }
        public bool Installed { get { return _installed; } }
        public Button NextButton { get { return _next; } }
        public string LicenseText { get { return _license.Text; } }
        public void SetRunNow(bool run) { _runNow.Checked = run; }
        public void SetShortcuts(bool desktop, bool autostart) { _desktop.Checked = desktop; _autostart.Checked = autostart; }
    }
}
