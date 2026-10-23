using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace IDMClone
{
    // "IDM Site Grabber": asistente de 4 pasos y ventana principal del proyecto ("Sitio grabber de IDM"), con la
    // disposición de IDM 6.43 (capturas 40 a 45): ilustración a la izquierda, botón "Avanzado >>" que alarga la
    // ventana, menú "Proyecto / Opciones / Contenidos de ayuda" y, en la ventana principal, barra de iconos pequeños,
    // árbol "Ver Miniaturas / Todos los Archivos / Ver Enlace / Ver Carpeta", lista con casillas y cuadritos de paso.
    public class GrabberForm : DialogBase
    {
        readonly AppConfig _cfg;
        readonly DownloadManager _mgr;
        GrabberProject _p;
        GrabberEngine _engine;
        int _step = 1;
        bool _dirty;
        volatile bool _changed;
        bool _watchStart;                       // avisar si la página de inicio no se pudo abrir (IDM: "El archivo solicitado no existe.")
        DateTime _scheduleAt = DateTime.MinValue; // Opciones > Programar el proyecto
        bool _statsClosed;                      // el usuario cerró las estadísticas: no volver a abrirlas solas

        // Para las pruebas: respuesta a "¿Guardar el proyecto de Grabber?" y avisos.
        public static Func<string, DialogResult> AskHook;
        public static Action<string> MessageHook;
        public event Action ProjectsChanged;

        public const string Caption = "IDMClone Capturador de sitios";
        public const string MainTitle = "Sitio grabber de IDMClone";
        // Medidas lógicas (96 ppp) de IDM: asistente ~613x389 (con Avanzado ~613x544) y ventana principal ~768x552.
        const int WizW = 600, WizH0 = 280, WizH1 = 444, BottomH = 44, MainW = 752, MainH = 462;

        MenuStrip _menu;
        ToolStripMenuItem _recent;
        Panel _wizard, _action, _bottom;
        Button _back, _next, _close;
        readonly Button[] _dots = new Button[5];
        readonly ToolTip _tips = new ToolTip();
        readonly Panel[] _steps = new Panel[4];
        readonly bool[] _adv = new bool[4];
        readonly Control[][] _advCtl = new Control[4][];
        readonly Button[] _advBtn = new Button[4];
        Size _mainSize = Size.Empty;
        readonly string[] _titles = { "Asignar página de inicio", "Guardar archivos", "Asignar explorador de filtros", "Asignar filtros de archivos" };

        // Paso 1
        TextBox _name, _start, _user, _pass, _manualUrl, _logout;
        ComboBox _template;
        CheckBox _useAuth, _manual, _logoutOn;
        // Paso 2
        RadioButton _byCat, _toCat, _toFolder;
        ComboBox _saveCat;
        TextBox _saveCatFolder, _folder;
        Button _browse;
        CheckBox _relative, _convert, _overwrite, _addOnClose;
        // Paso 3
        RadioButton _whole, _levels;
        NumericUpDown _lvSite, _lvOther;
        CheckBox _popups, _noParent, _mainDomain, _js, _exploreOnlyOn, _exploreNotOn;
        TextBox _exploreOnly, _exploreNot;
        Label _jsNote;
        LinkLabel _jsRisk;
        // Paso 4
        ComboBox _include, _exclude, _minUnit, _maxUnit;
        CheckBox _thisSite, _hideDup, _downloadAll, _minOn, _maxOn, _dlOnlyOn, _dlNotOn;
        TextBox _dlOnly, _dlNot;
        NumericUpDown _minVal, _maxVal;
        // Ventana principal
        ToolStrip _tb;
        TreeView _tree;
        ListView _list;
        ImageList _treeIcons, _icons, _thumbs;
        readonly HashSet<string> _thumbKeys = new HashSet<string>(); // ImageList.ContainsKey recorre toda la lista
        Timer _timer;
        readonly Dictionary<GrabberFile, ListViewItem> _rows = new Dictionary<GrabberFile, ListViewItem>();
        string _view = "all";
        int _treeFiles = -1;
        bool _programmatic;
        string _statusText = "";
        GrabberStatsForm _stats;

        public GrabberProject Project { get { return _p; } }
        public GrabberEngine Engine { get { return _engine; } }
        public int Step { get { return _step; } }

        public GrabberForm(AppConfig cfg, DownloadManager mgr, GrabberProject project, string startUrl)
        {
            _cfg = cfg; _mgr = mgr;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(WizW, 24 + WizH0 + BottomH);
            BuildMenu();
            BuildBottom();
            BuildWizard();
            BuildAction();
            Controls.Add(_action);
            Controls.Add(_wizard);
            Controls.Add(_bottom);
            Controls.Add(_menu);
            MainMenuStrip = _menu;
            _timer = new Timer(); _timer.Interval = 400; _timer.Tick += delegate { OnTick(); };
            _timer.Start();
            if (project == null) project = NewDefault(startUrl);
            SetProject(project);
            // Un proyecto guardado se abre en la ventana principal (como IDM).
            ShowStep(project.Pages.Count > 0 ? 5 : 1);
            EndLayout();
            // ShowStep calculó el alto con el del menú, que ya iba en píxeles de la pantalla, y EndLayout lo escaló otra
            // vez: con todo ya escalado, el tamaño del paso se vuelve a calcular.
            ApplySize();
        }

        // Proyecto nuevo con los valores de IDM para "Configuración personalizada" (pasos 3 y 4: 2 niveles en el
        // sitio base, todos los archivos, ocultar duplicados) y el nombre "project_N".
        GrabberProject NewDefault(string startUrl)
        {
            GrabberProject p = GrabberProject.CreateDefault();
            p.WholeSite = false; p.LevelsSite = 2; p.LevelsOther = 0;
            p.IncludeFilter = GrabberFilters.All; p.ThisSiteOnly = false; p.HideDuplicates = true;
            p.StartUrl = startUrl ?? "";
            p.SaveFolder = _cfg.DefaultFolder;
            for (int i = 1; i < 10000; i++)
            {
                string n = "project_" + i;
                bool used = false;
                try { used = File.Exists(GrabberProject.PathFor(n)); } catch { }
                if (!used) { p.Name = n; break; }
            }
            return p;
        }

        void Say(string text) { Say(text, MessageBoxIcon.Information); }

        void Say(string text, MessageBoxIcon icon)
        {
            if (MessageHook != null) { MessageHook(Lang.T(text)); return; }
            Msg.Show(this, text, Caption, MessageBoxButtons.OK, icon);
        }

        DialogResult Ask(string text, MessageBoxButtons b)
        {
            if (AskHook != null) return AskHook(text);
            return Msg.Show(this, text, Caption, b, MessageBoxIcon.Question);
        }

        // ------------------------------------------------------------------ menú y botones

        void BuildMenu()
        {
            _menu = new MenuStrip();
            ToolStripMenuItem proj = new ToolStripMenuItem("&Proyecto");
            proj.DropDownItems.Add("Nuevo", null, delegate { NewProject(); });
            proj.DropDownItems.Add("Cargar...", null, delegate { LoadProject(); });
            proj.DropDownItems.Add("Guardar", null, delegate { SaveProject(false); });
            proj.DropDownItems.Add("Guardar configuración en una plantilla", null, delegate { SaveTemplate(); });
            proj.DropDownItems.Add(new ToolStripSeparator());
            _recent = new ToolStripMenuItem("Proyectos Recientes");
            _recent.DropDownItems.Add(new ToolStripMenuItem("-")); // para que muestre la flecha ">"
            _recent.DropDownOpening += delegate { FillRecent(); };
            proj.DropDownItems.Add(_recent);
            proj.DropDownItems.Add(new ToolStripSeparator());
            proj.DropDownItems.Add("Cerrar", null, delegate { Close(); });
            proj.DropDownOpening += delegate { _recent.Enabled = RecentProjects().Count > 0; };
            ToolStripMenuItem opt = new ToolStripMenuItem("&Opciones");
            opt.DropDownItems.Add("Configuración de Grabber", null, delegate { ShowSettings(); });
            opt.DropDownItems.Add("Programar el proyecto", null, delegate { ScheduleProject(); });
            // IDM: "Contenidos de ayuda" es un elemento directo del menú (sin submenú). No abre ninguna web.
            ToolStripMenuItem help = new ToolStripMenuItem("Contenidos de ayuda");
            help.Click += delegate { Say(HelpText); };
            _menu.Items.Add(proj); _menu.Items.Add(opt); _menu.Items.Add(help);
        }

        // Los proyectos guardados, del más reciente al más antiguo (máximo 8).
        static List<string> RecentProjects()
        {
            List<string> names = GrabberProject.SavedNames();
            names.Sort(delegate(string a, string b)
            {
                DateTime ta = DateTime.MinValue, tb = DateTime.MinValue;
                try { ta = File.GetLastWriteTime(GrabberProject.PathFor(a)); tb = File.GetLastWriteTime(GrabberProject.PathFor(b)); } catch { }
                return tb.CompareTo(ta);
            });
            if (names.Count > 8) names.RemoveRange(8, names.Count - 8);
            return names;
        }

        void FillRecent()
        {
            _recent.DropDownItems.Clear();
            int i = 1;
            foreach (string n in RecentProjects())
            {
                string name = n;
                ToolStripMenuItem it = new ToolStripMenuItem("&" + i++ + " " + n);
                it.Tag = Lang.Keep;
                it.Click += delegate { OpenProject(name); };
                _recent.DropDownItems.Add(it);
            }
        }

        const string HelpText = "El Grabber explora un sitio web desde la página de inicio y encuentra los archivos que pasan los filtros " +
            "(todas las imágenes, el audio, el sitio completo...).\r\n\r\n" +
            "Paso 1: página de inicio y plantilla. Paso 2: dónde guardar. Paso 3: qué páginas explorar. Paso 4: qué archivos descargar.\r\n\r\n" +
            "Después marca los archivos y descárgalos aquí, o añádelos a la lista principal de IDMClone. Puedes volver a cualquier paso " +
            "para cambiar la configuración y continuar.";

        void BuildBottom()
        {
            _bottom = new Panel(); _bottom.SetBounds(0, 0, WizW, BottomH); _bottom.Dock = DockStyle.Bottom;
            Label line = new Label(); line.BorderStyle = BorderStyle.Fixed3D; line.SetBounds(10, 2, WizW - 20, 2); line.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _back = new Button(); _back.Text = "< Atrás"; _back.SetBounds(242, 13, 88, 23); _back.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _next = new Button(); _next.Text = "Siguiente >"; _next.SetBounds(342, 13, 88, 23); _next.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _close = new Button(); _close.Text = "Cerrar"; _close.SetBounds(498, 13, 88, 23); _close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _back.Click += delegate { GoBack(); };
            _next.Click += delegate { GoNext(); };
            _close.Click += delegate { Close(); };
            _bottom.Controls.Add(line); _bottom.Controls.Add(_back); _bottom.Controls.Add(_next); _bottom.Controls.Add(_close);
            // Ventana principal: cinco cuadritos abajo a la izquierda, uno por paso (pulsarlos vuelve a ese paso).
            for (int i = 0; i < 5; i++)
            {
                Button d = new Button(); d.FlatStyle = FlatStyle.Flat; d.SetBounds(14 + i * 22, 17, 15, 15);
                d.FlatAppearance.BorderColor = Color.FromArgb(0xAD, 0xAD, 0xAD); d.BackColor = Color.White; d.TabStop = false;
                int step = i + 1;
                d.Click += delegate { GoToStep(step); };
                d.Visible = false;
                _dots[i] = d; _bottom.Controls.Add(d);
            }
        }

        // ------------------------------------------------------------------ asistente

        void BuildWizard()
        {
            _wizard = new Panel(); _wizard.Dock = DockStyle.Fill;
            Bitmap art = GrabberArt.Wizard(Dpi.Px(130), Dpi.Px(262));
            for (int i = 0; i < 4; i++)
            {
                _steps[i] = new Panel(); _steps[i].Dock = DockStyle.Fill; _steps[i].Visible = false;
                PictureBox pic = C(_steps[i], new PictureBox(), 10, 8, 130, 262); pic.SizeMode = PictureBoxSizeMode.StretchImage; pic.Image = art;
                _wizard.Controls.Add(_steps[i]);
            }
            BuildStep1(_steps[0]); BuildStep2(_steps[1]); BuildStep3(_steps[2]); BuildStep4(_steps[3]);
        }

        static Label L(Control p, string text, int x, int y, int w, int h)
        {
            Label l = new Label(); l.Text = text; l.SetBounds(x, y, w, h); l.TextAlign = h > 24 ? ContentAlignment.TopLeft : ContentAlignment.MiddleLeft; p.Controls.Add(l); return l;
        }
        static T C<T>(Control p, T c, int x, int y, int w, int h) where T : Control { c.SetBounds(x, y, w, h); p.Controls.Add(c); return c; }
        static CheckBox Chk(Control p, string text, int x, int y, int w) { CheckBox c = C(p, new CheckBox(), x, y, w, 20); c.Text = text; return c; }
        static RadioButton Rad(Control p, string text, int x, int y, int w) { RadioButton c = C(p, new RadioButton(), x, y, w, 20); c.Text = text; return c; }
        static Label Line(Control p, int x, int y, int w) { Label l = new Label(); l.BorderStyle = BorderStyle.Fixed3D; l.SetBounds(x, y, w, 2); p.Controls.Add(l); return l; }
        static TextBox Multi(Control p, int x, int y, int w, int h)
        {
            TextBox t = C(p, new TextBox(), x, y, w, h); t.Multiline = true; t.ScrollBars = ScrollBars.Vertical; t.AcceptsReturn = true; return t;
        }

        // "Avanzado >>": muestra la parte de abajo y alarga la ventana (como IDM).
        void Advanced(int index, Control p, Control[] shown)
        {
            Button b = C(p, new Button(), 456, 240, 108, 23); b.Text = "Avanzado >>";
            _advBtn[index] = b;
            Control[] all = new Control[shown.Length + 1];
            all[0] = Line(p, 10, 282, 580); shown.CopyTo(all, 1);
            _advCtl[index] = all;
            b.Click += delegate { SetAdvanced(index, !_adv[index]); };
            SetAdvanced(index, false);
        }

        void SetAdvanced(int index, bool on)
        {
            _adv[index] = on;
            foreach (Control c in _advCtl[index]) c.Visible = on;
            _advBtn[index].Text = on ? "Avanzado <<" : "Avanzado >>";
            Lang.Apply(_advBtn[index]);
            if (_step == index + 1) ApplySize();
        }

        void BuildStep1(Panel p)
        {
            L(p, "Nombre del proyecto grabber", 156, 4, 300, 15);
            _name = C(p, new TextBox(), 156, 20, 250, 21);
            L(p, "Iniciar página/dirección", 156, 48, 300, 15);
            _start = C(p, new TextBox(), 156, 64, 434, 21);
            L(p, "Plantilla de proyecto", 156, 92, 300, 15);
            _template = C(p, new ComboBox(), 156, 108, 434, 21); _template.DropDownStyle = ComboBoxStyle.DropDownList; _template.Tag = Lang.Mark;
            _template.MaxDropDownItems = 12;
            foreach (string t in GrabberProject.Templates) _template.Items.Add(t);
            foreach (string t in GrabberProject.UserTemplates()) _template.Items.Add(t);
            L(p, "Si seleccionas una plantilla de proyecto, el asistente realizará la configuración requerida para la plantilla seleccionada en los próximos pasos. Ten en cuenta que siempre puedes cambiar cualquier configuración manualmente.", 156, 133, 434, 42);
            _useAuth = Chk(p, "Usar autorización", 156, 182, 300);
            Label u = L(p, "Usuario", 156, 206, 76, 21); u.TextAlign = ContentAlignment.MiddleRight;
            _user = C(p, new TextBox(), 236, 206, 110, 21);
            Label pw = L(p, "Contraseña", 350, 206, 96, 21); pw.TextAlign = ContentAlignment.MiddleRight;
            _pass = C(p, new TextBox(), 450, 206, 110, 21); _pass.UseSystemPasswordChar = true;
            _useAuth.CheckedChanged += delegate { _user.Enabled = _pass.Enabled = _useAuth.Checked; };
            L(p, "Pulsar el botón Avanzado para activar conexión manual o desactivar una página de desconexión", 156, 238, 290, 30);
            // Avanzado: inicio de sesión manual (antes de explorar se abre esa página en el navegador del usuario y
            // después se usan las cookies de su sesión) y página de desconexión.
            _manual = Chk(p, "Ingresa el usuario y contraseña manualmente en la siguiente web:", 156, 292, 434);
            _manualUrl = C(p, new TextBox(), 156, 314, 434, 21); _manualUrl.Enabled = false;
            _manual.CheckedChanged += delegate { _manualUrl.Enabled = _manual.Checked; };
            string manualTip = Lang.T("Antes de explorar, IDMClone abre esta página en tu navegador para que inicies sesión y después usa las cookies de ese sitio (con la extensión de IDMClone, o pegándolas). Las cookies no se guardan en el proyecto.");
            _tips.SetToolTip(_manual, manualTip); _tips.SetToolTip(_manualUrl, manualTip);
            _logoutOn = Chk(p, "No abrir la página de desconexión:", 156, 346, 434);
            _logout = C(p, new TextBox(), 156, 368, 434, 21);
            Label n = L(p, "Algunos sitios tienen página(s) de desconexión que Grabber no abrirá. Utiliza un asterisco (*) como comodín para especificar un patrón con diferentes páginas de desconexión.", 156, 394, 434, 44);
            _logoutOn.CheckedChanged += delegate { _logout.Enabled = _logoutOn.Checked; };
            Advanced(0, p, new Control[] { _manual, _manualUrl, _logoutOn, _logout, n });
            _template.SelectedIndexChanged += delegate { if (!_programmatic) TemplateChanged(); };
        }

        void BuildStep2(Panel p)
        {
            GroupBox g = C(p, new GroupBox(), 150, 0, 440, 194); g.Text = "Guardar en";
            _byCat = Rad(g, "Cada archivo al directorio según su categoría", 12, 18, 410);
            _toCat = Rad(g, "Todos los archivos a una categoría", 12, 44, 410);
            _saveCat = C(g, new ComboBox(), 40, 66, 194, 21); _saveCat.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (Category c in _cfg.Categories) _saveCat.Items.Add(c.Name);
            _saveCatFolder = C(g, new TextBox(), 40, 92, 384, 21); _saveCatFolder.ReadOnly = true;
            _toFolder = Rad(g, "Todos los archivos a un directorio", 12, 120, 410);
            _folder = C(g, new TextBox(), 40, 142, 384, 21);
            _relative = Chk(g, "Usar las carpetas relacionadas originales", 40, 168, 290);
            _browse = C(g, new Button(), 342, 167, 82, 23); _browse.Text = "Examinar...";
            _browse.Click += delegate
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    if (Directory.Exists(_folder.Text)) d.SelectedPath = _folder.Text;
                    if (d.ShowDialog(this) == DialogResult.OK) _folder.Text = d.SelectedPath;
                }
            };
            _saveCat.SelectedIndexChanged += delegate { Category c = _cfg.FindCategory(_saveCat.SelectedItem as string); _saveCatFolder.Text = c != null ? c.Folder : ""; };
            EventHandler modes = delegate { SaveModeUi(); };
            _byCat.CheckedChanged += modes; _toCat.CheckedChanged += modes; _toFolder.CheckedChanged += modes; _relative.CheckedChanged += modes;
            _convert = Chk(p, "Convertir los enlaces de los archivos html descargados en archivos locales", 156, 204, 434);
            _overwrite = Chk(p, "Sobrescribir archivos existentes", 156, 230, 434);
            _addOnClose = Chk(p, "Añadir los marcados a la lista principal de IDMClone y descargar cola al cerrar", 156, 256, 440);
        }

        void SaveModeUi()
        {
            _saveCat.Enabled = _saveCatFolder.Enabled = _toCat.Checked;
            _folder.Enabled = _browse.Enabled = _relative.Enabled = _toFolder.Checked;
            if (_hideDup != null) _hideDup.Enabled = !(_toFolder.Checked && _relative.Checked);
        }

        void BuildStep3(Panel p)
        {
            L(p, "En este paso debes especificar qué páginas web se explorarán para encontrar los archivos requeridos. En el siguiente paso podrás elegir los tipos de archivos, la ubicación y otros filtros.", 156, 4, 434, 42);
            _whole = Rad(p, "Explorar el sitio completo", 156, 48, 400);
            _levels = Rad(p, "Explorar el número especificado de niveles de enlace:", 156, 68, 420);
            _lvSite = C(p, new NumericUpDown(), 176, 92, 46, 21); _lvSite.Maximum = 50;
            L(p, "niveles en el sitio base", 226, 92, 160, 21);
            _lvOther = C(p, new NumericUpDown(), 396, 92, 46, 21); _lvOther.Maximum = 20;
            L(p, "niveles en otros sitios", 446, 92, 144, 21);
            LinkLabel what = C(p, new LinkLabel(), 176, 116, 300, 16); what.Text = "¿Cuál es el número de niveles de enlace?";
            what.LinkColor = Theme.HelpBlue;
            what.LinkClicked += delegate { Say(LevelsHelp); };
            EventHandler lv = delegate { _lvSite.Enabled = _lvOther.Enabled = _levels.Checked; };
            _whole.CheckedChanged += lv; _levels.CheckedChanged += lv;
            _popups = Chk(p, "Ignorar ventanas emergentes", 162, 138, 400);
            _noParent = Chk(p, "No explorar directorios padre", 162, 158, 400);
            _mainDomain = Chk(p, "Explorar todos los sitios del dominio principal", 162, 178, 400);
            _js = Chk(p, "Procesar JavaScript", 162, 198, 400);
            _jsNote = L(p, "Usa esta opción con cuidado.", 180, 218, 150, 16); _jsNote.ForeColor = SystemColors.GrayText; _jsNote.AutoSize = true;
            _jsRisk = C(p, new LinkLabel(), 332, 218, 200, 16); _jsRisk.Text = "¿Cuál es el riesgo?"; _jsRisk.AutoSize = true; _jsRisk.LinkColor = Theme.HelpBlue;
            _jsRisk.LinkClicked += delegate { Say(JsHelp); };
            _exploreOnlyOn = Chk(p, "Explorar solamente las páginas web con las siguientes rutas/dominios:", 156, 290, 434);
            _exploreOnly = Multi(p, 156, 312, 434, 40);
            _exploreNotOn = Chk(p, "No explorar las páginas web con las siguientes rutas/dominios:", 156, 358, 434);
            _exploreNot = Multi(p, 156, 380, 434, 40);
            Label n = L(p, "Usa punto y coma ( ; ) para separar las entradas y el asterisco (*) como comodín.", 156, 424, 434, 16);
            _exploreOnlyOn.CheckedChanged += delegate { _exploreOnly.Enabled = _exploreOnlyOn.Checked; };
            _exploreNotOn.CheckedChanged += delegate { _exploreNot.Enabled = _exploreNotOn.Checked; };
            Advanced(2, p, new Control[] { _exploreOnlyOn, _exploreOnly, _exploreNotOn, _exploreNot, n });
        }

        const string LevelsHelp = "Nivel 0 en este sitio y 0 en otros: solo se explora la página de inicio; se muestran todos los archivos que enlaza.\r\n\r\n" +
            "Nivel 1 en este sitio: se exploran también las páginas enlazadas desde la de inicio.\r\n\r\n" +
            "Los niveles en otros sitios cuentan las páginas de otros sitios enlazadas seguidas. Cuidado con números altos en otros sitios: " +
            "pueden aparecer miles de páginas inútiles.\r\n\r\nLos niveles solo cuentan para las páginas web; para los archivos están los filtros del paso 4.";

        const string JsHelp = "Con \"Procesar JavaScript\" el Grabber busca también direcciones dentro del código JavaScript de las páginas (no lo ejecuta).\r\n\r\n" +
            "Así encuentra enlaces que de otro modo no vería, pero también direcciones que no son páginas reales: puede explorar muchas páginas inútiles o abrir páginas que no deberían abrirse (por ejemplo, la de desconexión).";

        void BuildStep4(Panel p)
        {
            L(p, "Descargar los siguientes archivos (Tipos de archivo)", 156, 4, 434, 15);
            _include = C(p, new ComboBox(), 156, 20, 294, 21); _include.DropDownStyle = ComboBoxStyle.DropDownList;
            Button addInc = C(p, new Button(), 456, 19, 108, 23); addInc.Text = "Agregar filtro";
            L(p, "No descargar los siguientes archivos (Tipos de archivo)", 156, 50, 434, 15);
            _exclude = C(p, new ComboBox(), 156, 66, 294, 21); _exclude.DropDownStyle = ComboBoxStyle.DropDownList; _exclude.Tag = Lang.Mark;
            Button addExc = C(p, new Button(), 456, 65, 108, 23); addExc.Text = "Agregar filtro";
            addInc.Click += delegate { EditFilters(_include); };
            addExc.Click += delegate { EditFilters(_exclude); };
            FillFilterCombos();
            _thisSite = Chk(p, "Buscar archivos en este sitio solamente", 162, 98, 420);
            _hideDup = Chk(p, "Ocultar archivos duplicados encontrados en diferentes localizaciones", 162, 124, 420);
            _downloadAll = Chk(p, "Iniciar la descarga de todos los archivos coincidentes de una vez", 162, 152, 420);
            L(p, "Puedes explorar el sitio primero, marcar los archivos que necesitas y descargarlos en la ventana principal del Grabber, o añadirlos a la lista principal de IDMClone. Esta opción descarga todos los archivos explorados inmediatamente.", 156, 172, 434, 44);
            // Avanzado: tamaño (izquierda) y rutas (derecha).
            GroupBox size = C(p, new GroupBox(), 8, 288, 188, 128); size.Text = "Descargar si el tamaño de archivo";
            _minOn = Chk(size, "No menos de", 10, 20, 140);
            _minVal = C(size, new NumericUpDown(), 10, 42, 70, 21); _minVal.Maximum = 100000000; _minVal.Minimum = 0;
            _minUnit = Unit(size, 84, 42);
            _maxOn = Chk(size, "No más de", 10, 72, 140);
            _maxVal = C(size, new NumericUpDown(), 10, 94, 70, 21); _maxVal.Maximum = 100000000; _maxVal.Minimum = 0;
            _maxUnit = Unit(size, 84, 94);
            _minOn.CheckedChanged += delegate { _minVal.Enabled = _minUnit.Enabled = _minOn.Checked; };
            _maxOn.CheckedChanged += delegate { _maxVal.Enabled = _maxUnit.Enabled = _maxOn.Checked; };
            _dlOnlyOn = Chk(p, "Descargar sólo los archivos ubicados en las siguientes rutas/dominios:", 202, 290, 388);
            _dlOnly = Multi(p, 202, 312, 388, 40);
            _dlNotOn = Chk(p, "No descargar los archivos ubicados en las siguientes rutas/dominios:", 202, 358, 388);
            _dlNot = Multi(p, 202, 380, 388, 40);
            Label n = L(p, "Usa punto y coma ( ; ) para separar las entradas.", 202, 424, 388, 16);
            _dlOnlyOn.CheckedChanged += delegate { _dlOnly.Enabled = _dlOnlyOn.Checked; };
            _dlNotOn.CheckedChanged += delegate { _dlNot.Enabled = _dlNotOn.Checked; };
            Advanced(3, p, new Control[] { size, _dlOnlyOn, _dlOnly, _dlNotOn, _dlNot, n });
        }

        static ComboBox Unit(Control p, int x, int y)
        {
            ComboBox c = C(p, new ComboBox(), x, y, 52, 21); c.DropDownStyle = ComboBoxStyle.DropDownList; c.Tag = Lang.Keep;
            c.Items.Add("KB"); c.Items.Add("MB"); c.SelectedIndex = 0; return c;
        }

        void FillFilterCombos()
        {
            string inc = _include.SelectedItem != null ? ((GrabberFilter)_include.SelectedItem).Name : null;
            object exc = _exclude.SelectedItem;
            _include.Items.Clear(); _exclude.Items.Clear();
            _exclude.Items.Add("No usar un filtro excluido");
            foreach (GrabberFilter f in GrabberFilters.List) { _include.Items.Add(f); _exclude.Items.Add(f); }
            SelectFilter(_include, inc);
            SelectFilter(_exclude, exc is GrabberFilter ? ((GrabberFilter)exc).Name : null);
        }

        static void SelectFilter(ComboBox c, string name)
        {
            for (int i = 0; i < c.Items.Count; i++)
            {
                GrabberFilter f = c.Items[i] as GrabberFilter;
                if (f != null && f.Name == name) { c.SelectedIndex = i; return; }
            }
            c.SelectedIndex = c.Items.Count > 0 ? 0 : -1;
        }

        void EditFilters(ComboBox target)
        {
            using (GrabberFiltersForm f = new GrabberFiltersForm(target != null))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                FillFilterCombos();
                if (target != null && f.Selected != null) SelectFilter(target, f.Selected);
            }
        }

        // ------------------------------------------------------------------ proyecto <-> controles

        void SetProject(GrabberProject p)
        {
            if (_engine != null) { _engine.Close(); _engine = null; }
            _p = p;
            _rows.Clear();
            if (_list != null) _list.Items.Clear();
            if (_thumbs != null) { _thumbs.Images.Clear(); _thumbKeys.Clear(); }
            _treeFiles = -1;
            _dirty = false;
            ToUi();
        }

        static string Join(string list) { return string.Join("; ", Split(list).ToArray()); }

        static List<string> Split(string s)
        {
            List<string> r = new List<string>();
            foreach (string x in (s ?? "").Split(new char[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) if (x.Trim().Length > 0) r.Add(x.Trim());
            return r;
        }

        static string Blank(string s) { s = Join(s); return s.Length == 0 ? null : s; }

        // Tamaño en KB -> valor y unidad (MB si es múltiplo de 1024).
        static void ShowSize(long kb, long dflt, bool dfltMB, CheckBox on, NumericUpDown val, ComboBox unit)
        {
            on.Checked = kb > 0;
            if (kb <= 0) { val.Value = dflt; unit.SelectedIndex = dfltMB ? 1 : 0; }
            else if (kb % 1024 == 0) { val.Value = Math.Min(val.Maximum, kb / 1024); unit.SelectedIndex = 1; }
            else { val.Value = Math.Min(val.Maximum, kb); unit.SelectedIndex = 0; }
            val.Enabled = unit.Enabled = on.Checked;
        }

        static long SizeKB(CheckBox on, NumericUpDown val, ComboBox unit)
        {
            if (!on.Checked) return 0;
            return (long)val.Value * (unit.SelectedIndex == 1 ? 1024 : 1);
        }

        void ToUi()
        {
            _programmatic = true;
            _name.Text = _p.Name ?? "";
            _start.Text = _p.StartUrl ?? "";
            _template.SelectedIndex = Math.Max(0, Math.Min(_template.Items.Count - 1, _p.Template));
            _useAuth.Checked = _p.UseAuth; _user.Text = _p.User ?? ""; _pass.Text = _p.Password;
            _user.Enabled = _pass.Enabled = _p.UseAuth;
            _logoutOn.Checked = !string.IsNullOrEmpty(_p.LogoutPages); _logout.Text = Join(_p.LogoutPages); _logout.Enabled = _logoutOn.Checked;
            _manual.Checked = _p.ManualLogin; _manualUrl.Text = _p.ManualLoginUrl ?? ""; _manualUrl.Enabled = _manual.Checked;
            _byCat.Checked = _p.SaveMode == 0; _toCat.Checked = _p.SaveMode == 1; _toFolder.Checked = _p.SaveMode == 2;
            _saveCat.SelectedItem = _p.SaveCategory ?? Category.GeneralName;
            if (_saveCat.SelectedIndex < 0 && _saveCat.Items.Count > 0) _saveCat.SelectedIndex = 0;
            _folder.Text = _p.SaveFolder ?? _cfg.DefaultFolder;
            _relative.Checked = _p.RelativeSubfolders; _convert.Checked = _p.ConvertLinks; _overwrite.Checked = _p.Overwrite; _addOnClose.Checked = _p.AddOnClose;
            _convert.Enabled = _p.PagesAreDownloaded || _p.ConvertLinks;
            _whole.Checked = _p.WholeSite; _levels.Checked = !_p.WholeSite;
            _lvSite.Value = Math.Min(_lvSite.Maximum, _p.LevelsSite); _lvOther.Value = Math.Min(_lvOther.Maximum, _p.LevelsOther);
            _lvSite.Enabled = _lvOther.Enabled = !_p.WholeSite;
            _popups.Checked = _p.IgnorePopups; _noParent.Checked = _p.NoParent; _mainDomain.Checked = _p.MainDomain; _js.Checked = _p.ProcessJs;
            _exploreOnlyOn.Checked = _p.ExploreOnly != null; _exploreOnly.Text = Join(_p.ExploreOnly); _exploreOnly.Enabled = _exploreOnlyOn.Checked;
            _exploreNotOn.Checked = _p.ExploreNot != null; _exploreNot.Text = Join(_p.ExploreNot); _exploreNot.Enabled = _exploreNotOn.Checked;
            SelectFilter(_include, _p.IncludeFilter);
            if (_p.ExcludeFilter == null) _exclude.SelectedIndex = 0; else SelectFilter(_exclude, _p.ExcludeFilter);
            _thisSite.Checked = _p.ThisSiteOnly; _hideDup.Checked = _p.HideDuplicates; _downloadAll.Checked = _p.DownloadAll;
            _dlOnlyOn.Checked = _p.DownloadOnly != null; _dlOnly.Text = Join(_p.DownloadOnly); _dlOnly.Enabled = _dlOnlyOn.Checked;
            _dlNotOn.Checked = _p.DownloadNot != null; _dlNot.Text = Join(_p.DownloadNot); _dlNot.Enabled = _dlNotOn.Checked;
            ShowSize(_p.MinKB, 1, false, _minOn, _minVal, _minUnit);   // IDM: "1 KB" y "10 MB" (desactivados)
            ShowSize(_p.MaxKB, 10, true, _maxOn, _maxVal, _maxUnit);
            SaveModeUi();
            UpdateNoParent();
            // Si hay opciones avanzadas puestas, su parte se ve abierta (para que no queden ocultas).
            SetAdvanced(0, _logoutOn.Checked || _manual.Checked);
            SetAdvanced(2, _exploreOnlyOn.Checked || _exploreNotOn.Checked);
            SetAdvanced(3, _minOn.Checked || _maxOn.Checked || _dlOnlyOn.Checked || _dlNotOn.Checked);
            _programmatic = false;
        }

        void FromUi()
        {
            _p.Name = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : _p.Name;
            _p.StartUrl = NormalizeUrl(_start.Text);
            _p.UseAuth = _useAuth.Checked; _p.User = _user.Text.Trim(); _p.Password = _pass.Text;
            _p.LogoutPages = _logoutOn.Checked ? Blank(_logout.Text) : null;
            _p.ManualLogin = _manual.Checked;
            _p.ManualLoginUrl = _manualUrl.Text.Trim().Length > 0 ? NormalizeUrl(_manualUrl.Text) : null;
            _p.SaveMode = _byCat.Checked ? 0 : _toCat.Checked ? 1 : 2;
            _p.SaveCategory = _saveCat.SelectedItem as string;
            _p.SaveFolder = _folder.Text.Trim();
            _p.RelativeSubfolders = _relative.Checked; _p.ConvertLinks = _convert.Checked && _convert.Enabled; _p.Overwrite = _overwrite.Checked; _p.AddOnClose = _addOnClose.Checked;
            _p.WholeSite = _whole.Checked; _p.LevelsSite = (int)_lvSite.Value; _p.LevelsOther = (int)_lvOther.Value;
            _p.IgnorePopups = _popups.Checked; _p.NoParent = _noParent.Checked && _noParent.Enabled; _p.MainDomain = _mainDomain.Checked; _p.ProcessJs = _js.Checked;
            _p.ExploreOnly = _exploreOnlyOn.Checked ? Blank(_exploreOnly.Text) : null;
            _p.ExploreNot = _exploreNotOn.Checked ? Blank(_exploreNot.Text) : null;
            _p.IncludeFilter = _include.SelectedItem is GrabberFilter ? ((GrabberFilter)_include.SelectedItem).Name : GrabberFilters.All;
            _p.ExcludeFilter = _exclude.SelectedItem is GrabberFilter ? ((GrabberFilter)_exclude.SelectedItem).Name : null;
            _p.ThisSiteOnly = _thisSite.Checked; _p.HideDuplicates = _hideDup.Checked && _hideDup.Enabled; _p.DownloadAll = _downloadAll.Checked;
            _p.DownloadOnly = _dlOnlyOn.Checked ? Blank(_dlOnly.Text) : null;
            _p.DownloadNot = _dlNotOn.Checked ? Blank(_dlNot.Text) : null;
            _p.MinKB = SizeKB(_minOn, _minVal, _minUnit); _p.MaxKB = SizeKB(_maxOn, _maxVal, _maxUnit);
            _dirty = true;
        }

        static string NormalizeUrl(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return s;
            if (!s.Contains("://")) s = "http://" + s; // como IDM: http por defecto
            return s;
        }

        void UpdateNoParent()
        {
            Uri u;
            bool deep = Uri.TryCreate(NormalizeUrl(_start.Text), UriKind.Absolute, out u) && u.AbsolutePath.LastIndexOf('/') > 0;
            _noParent.Enabled = deep;
        }

        void TemplateChanged()
        {
            int i = _template.SelectedIndex;
            _p.StartUrl = NormalizeUrl(_start.Text);
            if (i < GrabberProject.Templates.Length) _p.ApplyTemplate(i, _cfg);
            else
            {
                try { _p.ApplyUserTemplate((string)_template.SelectedItem); }
                catch (Exception ex) { Say(ex.Message, MessageBoxIcon.Warning); }
            }
            string name = _name.Text, start = _start.Text;
            bool auth = _useAuth.Checked; string user = _user.Text, pass = _pass.Text;
            bool adv0 = _adv[0];
            // Lo avanzado del paso 1 (inicio de sesión manual y desconexión) tampoco depende de la plantilla.
            bool manual = _manual.Checked, logoutOn = _logoutOn.Checked; string manualUrl = _manualUrl.Text, logout = _logout.Text;
            ToUi();
            _programmatic = true;
            _name.Text = name; _start.Text = start; _useAuth.Checked = auth; _user.Text = user; _pass.Text = pass;
            _manual.Checked = manual; _manualUrl.Text = manualUrl; _logoutOn.Checked = logoutOn; _logout.Text = logout;
            _template.SelectedIndex = i;
            _programmatic = false;
            if (adv0 && !_adv[0]) SetAdvanced(0, true);
            _convert.Enabled = _p.PagesAreDownloaded || i == 0 || i >= GrabberProject.Templates.Length;
        }

        // ------------------------------------------------------------------ navegación y tamaño

        public void ShowStep(int step)
        {
            if (_step == 5 && step != 5 && WindowState == FormWindowState.Normal && FormBorderStyle == FormBorderStyle.Sizable) _mainSize = Size;
            _step = step;
            bool action = step == 5;
            SuspendLayout();
            _wizard.Visible = !action;
            _action.Visible = action;
            for (int i = 0; i < 4; i++) _steps[i].Visible = i == step - 1;
            Text = Lang.T(action ? MainTitle : "IDMClone Site Grabber. Paso " + step + " --- " + _titles[step - 1]); // el título cambia en cada paso: se traduce aquí
            _back.Enabled = step > 1;
            _next.Enabled = step < 5;
            for (int i = 0; i < 5; i++)
            {
                _dots[i].Visible = action;
                _dots[i].BackColor = i == step - 1 ? Color.FromArgb(0xCC, 0xE8, 0xFF) : Color.White;
                _dots[i].FlatAppearance.BorderColor = i == step - 1 ? Color.FromArgb(0x99, 0xD1, 0xFF) : Color.FromArgb(0xAD, 0xAD, 0xAD);
                _tips.SetToolTip(_dots[i], Lang.T(i < 4 ? _titles[i] : MainTitle));
            }
            if (step == 3) UpdateNoParent();
            if (step == 2) _convert.Enabled = _p.PagesAreDownloaded || _p.Template == 0 || _convert.Checked;
            ApplySize();
            ResumeLayout(true);
            if (action) { EnsureEngine(); RefreshAction(); }
        }

        // Escala real de la ventana (WinForms la agranda a otros ppp): se mide con el panel de abajo.
        int Px(int logical) { return (int)Math.Round(logical * (_bottom.Height / (float)BottomH)); }

        // Asistente: ventana fija que crece con "Avanzado"; ventana principal: redimensionable (como IDM).
        void ApplySize()
        {
            if (_step == 5)
            {
                if (FormBorderStyle != FormBorderStyle.Sizable)
                {
                    FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = true; MinimizeBox = true; ShowIcon = true;
                    if (_mainSize.IsEmpty) ClientSize = new Size(Px(MainW), _menu.Height + Px(MainH) + _bottom.Height);
                    else Size = _mainSize;
                }
                MinimumSize = new Size(Px(560), Px(400));
                return;
            }
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            MinimumSize = Size.Empty;
            if (FormBorderStyle != FormBorderStyle.FixedSingle) { FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; MinimizeBox = false; ShowIcon = false; }
            ClientSize = new Size(Px(WizW), _menu.Height + Px(_adv[_step - 1] ? WizH1 : WizH0) + _bottom.Height);
        }

        bool ValidStart(out string url)
        {
            url = NormalizeUrl(_start.Text);
            Uri x;
            return url.Length > 0 && Uri.TryCreate(url, UriKind.Absolute, out x) && (x.Scheme == "http" || x.Scheme == "https" || x.Scheme == "ftp");
        }

        public bool GoNext()
        {
            if (_step == 1)
            {
                string u;
                if (!ValidStart(out u)) { Say("Por favor, introduce la página/dirección de inicio", MessageBoxIcon.Warning); _start.Focus(); return false; }
                if (_useAuth.Checked && _user.Text.Trim().Length == 0)
                { Say("Por favor, introduce el usuario y contraseña o desactiva la casilla \"Usar autorización\"", MessageBoxIcon.Warning); return false; }
                if (_manual.Checked)
                {
                    string login = LoginUrlOf(_manualUrl.Text);
                    if (login == null)
                    {
                        Say("Por favor, introduce la página web (http o https) donde iniciar sesión o desactiva la casilla \"Ingresa el usuario y contraseña manualmente en la siguiente web\"", MessageBoxIcon.Warning);
                        SetAdvanced(0, true); _manualUrl.Focus(); return false;
                    }
                    _manualUrl.Text = login;
                }
                // Otra página de inicio: lo explorado antes ya no vale.
                if (_p.Pages.Count > 0 && !string.Equals(_p.StartUrl, u, StringComparison.OrdinalIgnoreCase))
                {
                    if (_engine != null) { _engine.Close(); _engine = null; }
                    _p.Pages.Clear(); _p.Files.Clear(); _rows.Clear(); _list.Items.Clear(); _treeFiles = -1;
                }
                _start.Text = u;
            }
            if (_step == 2 && _toFolder.Checked && _folder.Text.Trim().Length == 0) { Say("Elige la carpeta donde guardar los archivos.", MessageBoxIcon.Warning); return false; }
            if (_step == 4)
            {
                long min = SizeKB(_minOn, _minVal, _minUnit), max = SizeKB(_maxOn, _maxVal, _maxUnit);
                if (max > 0 && min > max) { Say("El tamaño mínimo es mayor que el máximo.", MessageBoxIcon.Warning); return false; }
            }
            FromUi();
            ShowStep(_step + 1);
            // Como IDM: al llegar a la ventana principal empieza (o sigue) a explorar y muestra las estadísticas.
            if (_step == 5 && !_engine.Exploring && (_p.Pages.Count == 0 || _p.Pages.Exists(delegate(GrabberPage g) { return g.Status == null; })))
                StartExploring();
            if (_step == 5 && !_statsClosed) ShowStats();
            return true;
        }

        void GoBack()
        {
            if (_step == 5 && _engine != null) _engine.StopExploring();
            ShowStep(_step - 1);
        }

        void GoToStep(int step)
        {
            if (step == _step || step > 5) return;
            if (_step == 5 && _engine != null) _engine.StopExploring();
            ShowStep(step);
        }

        void EnsureEngine()
        {
            if (_engine != null) return;
            _engine = new GrabberEngine(_p, _cfg);
            _engine.Changed += delegate { _changed = true; };
            _engine.ExploreFinished += delegate { _changed = true; };
            _engine.DownloadFinished += delegate { _changed = true; };
        }

        void StartExploring() { StartExploring(false); }

        void StartExploring(bool scheduled)
        {
            EnsureEngine();
            if (_engine.Exploring) return;
            if (!ManualLogin(scheduled)) return;
            _engine.StartExploring();
            _watchStart = true;
        }

        // ------------------------------------------------------------------ inicio de sesión manual (paso 1 > Avanzado)

        // Para las pruebas: abrir la página en el navegador (en vez de Process.Start) y el diálogo de inicio de
        // sesión: recibe la página y si se ofrece pegar cookies; devuelve null = Cancelar, o el texto de la caja
        // (sin caja: cualquier texto = Continuar).
        public static Action<string> BrowserHook;
        public static Func<string, bool, string> LoginHook;
        const int CookieWaitMs = 5000;

        static string LoginUrlOf(string s)
        {
            string url = NormalizeUrl(s);
            Uri u;
            if (url.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) return null;
            return u.AbsoluteUri;
        }

        void OpenBrowser(string url)
        {
            if (BrowserHook != null) { BrowserHook(url); return; }
            try { Process.Start(url); } // solo http/https (LoginUrlOf): nunca un archivo o programa del proyecto
            catch (Exception ex) { Say("No se pudo abrir el navegador: " + ex.Message, MessageBoxIcon.Warning); }
        }

        string AskLogin(string url, bool paste, string note)
        {
            if (LoginHook != null) return LoginHook(url, paste);
            using (GrabberLoginForm f = new GrabberLoginForm(url, paste, note, delegate { OpenBrowser(url); }))
                return f.ShowDialog(this) == DialogResult.OK ? f.Pasted : null;
        }

        // Como IDM: antes de explorar abre la página de inicio de sesión en el navegador y espera a que el usuario
        // pulse Continuar. Después pide a la extensión las cookies del sitio (CookieBridge) o, si no las da, ofrece
        // pegarlas. Solo quedan en el motor (nunca en el proyecto). Un proyecto programado no muestra nada: pide las
        // cookies en silencio y, si no hay, explora sin ellas. Devuelve false si el usuario canceló.
        bool ManualLogin(bool scheduled)
        {
            string url = _p.ManualLogin ? LoginUrlOf(_p.ManualLoginUrl) : null;
            if (url == null || _engine.LoginDoneFor == url) return true;
            if (scheduled)
            {
                int got = _engine.ImportBrowserCookies(url, CookieWaitMs);
                _engine.LoginNote = got > 0 ? "Sesión del navegador: " + got + " cookies" : "Sin la sesión del navegador: se explora sin iniciar sesión";
                _engine.LoginDoneFor = url;
                return true;
            }
            OpenBrowser(url);
            bool paste = !GrabberEngine.BrowserCookiesAvailable;
            string note = paste ? "No se pudo leer la sesión del navegador: ¿está instalada y conectada la extensión de IDMClone?" : null;
            while (true)
            {
                string answer = AskLogin(url, paste, note);
                if (answer == null) return false;
                if (!paste)
                {
                    Cursor old = Cursor.Current;
                    Cursor.Current = Cursors.WaitCursor;
                    int got;
                    try { got = _engine.ImportBrowserCookies(url, CookieWaitMs); }
                    finally { Cursor.Current = old; }
                    if (got > 0)
                    {
                        _engine.LoginDoneFor = url; _engine.LoginNote = "Sesión del navegador: " + got + " cookies";
                        return true;
                    }
                    paste = true;
                    note = got == 0 ? "El navegador no tiene cookies de ese sitio: ¿terminaste de iniciar sesión? Pulsa Cancelar y vuelve a explorar, o pega las cookies."
                                    : "No se pudo leer la sesión del navegador: ¿está instalada y conectada la extensión de IDMClone?";
                    continue;
                }
                string text = answer.Trim();
                if (text.Length == 0)
                {
                    _engine.LoginDoneFor = url; _engine.LoginNote = "Sin sesión: se explora sin iniciar sesión";
                    return true;
                }
                int n = _engine.ImportCookieHeader(_p.StartUrl, text);
                if (n == 0) { note = "No se reconoció ninguna cookie. Pega el texto de la cabecera Cookie (nombre=valor; nombre2=valor2)."; continue; }
                _engine.LoginDoneFor = url; _engine.LoginNote = "Cookies pegadas: " + n;
                return true;
            }
        }

        void OnTick()
        {
            if (_changed) { _changed = false; RefreshAction(); }
            if (_watchStart && _engine != null && !_engine.Exploring) { _watchStart = false; ReportStartPage(); }
            if (_scheduleAt != DateTime.MinValue && DateTime.Now >= _scheduleAt) RunScheduled();
        }

        // IDM avisa si la página de inicio no existe ("IDM Capturador de sitios", captura 44b).
        void ReportStartPage()
        {
            GrabberPage s = null;
            try { if (_p.Pages.Count > 0) s = _p.Pages[0]; } catch { }
            if (s == null) return;
            if (s.Status == "No Encontrado") Say("El archivo solicitado no existe.\r\n\r\nDetalles:\r\nHTTP/1.1 404 Not Found", MessageBoxIcon.Warning);
            else if (s.Status == "Error") Say("No se pudo abrir la página de inicio.", MessageBoxIcon.Warning);
        }

        // ------------------------------------------------------------------ ventana principal del proyecto

        void BuildAction()
        {
            _action = new Panel(); _action.Dock = DockStyle.Fill; _action.Visible = false; _action.Padding = new Padding(10, 6, 10, 4);
            _tb = new ToolStrip(); _tb.GripStyle = ToolStripGripStyle.Hidden; _tb.RenderMode = ToolStripRenderMode.System;
            _tb.ImageScalingSize = new Size(Dpi.Px(20), Dpi.Px(20)); _tb.Dock = DockStyle.Top;
            // IDM: 10 iconos pequeños sin texto (captura 87).
            Tool("explore", "Explorar", "Iniciar exploración del sitio", delegate { StartExploring(); });
            Tool("explore-stop", "Detener exploración", "Detener exploración del sitio", delegate { if (_engine != null) _engine.StopExploring(); });
            _tb.Items.Add(new ToolStripSeparator());
            Tool("checkall", "Marcar todos", "Marcar todos los archivos", delegate { SetChecked(true); });
            Tool("uncheckall", "Desmarcar todos", "Desmarcar todos los archivos", delegate { SetChecked(false); });
            Tool("download", "Descargar", "Iniciar descarga de los archivos marcados", delegate { DownloadChecked(); });
            Tool("download-stop", "Detener descarga", "Detener la descarga de los archivos marcados", delegate { if (_engine != null) _engine.StopDownloading(); });
            Tool("addlist", "Añadir a IDMClone", "Añadir los archivos marcados a la lista principal de IDMClone y a la cola de descarga", delegate { AddToMain(); });
            Tool("refresh", "Actualizar", "Actualizar la lista de archivos", delegate { _treeFiles = -1; RefreshAction(); });
            Tool("schedule", "Programar", "Programar el proyecto", delegate { ScheduleProject(); });
            Tool("stats", "Estadísticas", "Mostrar estadísticas de Grabber", delegate { _statsClosed = false; ShowStats(); });

            SplitContainer split = new SplitContainer(); split.Dock = DockStyle.Fill; split.SplitterDistance = 150; split.SplitterWidth = 8; split.FixedPanel = FixedPanel.Panel1;
            // El ancho del árbol (150 px lógicos, escalado) se fija la primera vez que el panel tiene su tamaño de verdad:
            // aquí el SplitContainer aún mide 150 y lo recortaba a ~115 a cualquier escala.
            bool treeSized = false;
            split.SizeChanged += delegate
            {
                if (treeSized || split.Width <= Dpi.Px(150) + 60) return;
                treeSized = true;
                try { split.SplitterDistance = Dpi.Px(150); } catch { }
            };
            _treeIcons = NewImageList(16);
            foreach (string k in new string[] { "thumbs", "folder", "folder-open", "links", "page" }) _treeIcons.Images.Add(k, GrabberArt.Icon("tree-" + k, Dpi.Px(16)));
            _tree = new TreeView(); _tree.Dock = DockStyle.Fill; _tree.HideSelection = false; _tree.ImageList = _treeIcons;
            _tree.ShowRootLines = false; _tree.ShowLines = true; _tree.ShowNodeToolTips = true;
            _tree.AfterSelect += delegate(object s, TreeViewEventArgs e) { if (e.Node != null && e.Node.Tag != null) { _view = (string)e.Node.Tag; FillList(); } };
            _icons = NewImageList(16);
            _thumbs = NewImageList(64);
            IntPtr th = _thumbs.Handle; // con handle, la ImageList copia cada imagen y la original se puede liberar
            _list = new ListView(); _list.Dock = DockStyle.Fill; _list.View = View.Details; _list.CheckBoxes = true; _list.FullRowSelect = true; _list.GridLines = true;
            _list.SmallImageList = _icons; _list.LargeImageList = _thumbs;
            foreach (string[] c in new string[][] { new string[] { "Nombre de Archivo", "150" }, new string[] { "Tipo de archivo", "75" }, new string[] { "Tamaño", "75" },
                                                  new string[] { "Estado", "80" }, new string[] { "Texto del enlace", "100" }, new string[] { "Descargar desde", "220" }, new string[] { "Guardar en", "220" } })
                _list.Columns.Add(c[0], Dpi.Px(int.Parse(c[1])));
            _list.ItemChecked += delegate(object s, ItemCheckedEventArgs e)
            {
                if (_programmatic) return;
                GrabberFile f = e.Item.Tag as GrabberFile;
                if (f != null && f.Checked != e.Item.Checked) { f.Checked = e.Item.Checked; _dirty = true; UpdateStatus(); }
            };
            _list.DoubleClick += delegate { OpenSelected(false); };
            // Menú contextual de IDM (captura 44c).
            ContextMenuStrip cm = new ContextMenuStrip();
            ToolStripItem open = cm.Items.Add("Abrir", null, delegate { OpenSelected(false); });
            ToolStripItem folder = cm.Items.Add("Abrir carpeta", null, delegate { OpenSelected(true); });
            cm.Items.Add("Marcar todos los archivos seleccionados", null, delegate { CheckSelected(true); });
            cm.Items.Add("Desmarcar todos los archivos seleccionados", null, delegate { CheckSelected(false); });
            cm.Items.Add(new ToolStripSeparator());
            ToolStripItem props = cm.Items.Add("propiedades", null, delegate { ShowProperties(); });
            cm.Opening += delegate
            {
                List<GrabberFile> sel = Selected();
                bool saved = sel.Count > 0 && !string.IsNullOrEmpty(sel[0].SaveTo) && File.Exists(sel[0].SaveTo);
                open.Enabled = saved;
                folder.Enabled = saved || (sel.Count > 0 && !string.IsNullOrEmpty(sel[0].SaveTo) && Directory.Exists(Path.GetDirectoryName(sel[0].SaveTo)));
                props.Enabled = sel.Count > 0;
            };
            _list.ContextMenuStrip = cm;
            split.Panel1.Controls.Add(_tree);
            split.Panel2.Controls.Add(_list);
            Panel gap = new Panel(); gap.Dock = DockStyle.Top; gap.Height = 6;
            _action.Controls.Add(split); _action.Controls.Add(gap); _action.Controls.Add(_tb);
        }

        static ImageList NewImageList(int size)
        {
            ImageList l = new ImageList(); l.ColorDepth = ColorDepth.Depth32Bit; l.ImageSize = new Size(Dpi.Px(size), Dpi.Px(size)); return l;
        }

        void Tool(string glyph, string text, string tip, EventHandler h)
        {
            ToolStripButton b = new ToolStripButton(text, GrabberArt.Icon(glyph, _tb.ImageScalingSize.Width));
            b.Name = "gt_" + glyph; b.ToolTipText = tip; b.DisplayStyle = ToolStripItemDisplayStyle.Image;
            b.ImageScaling = ToolStripItemImageScaling.None;
            b.Click += h;
            _tb.Items.Add(b);
        }

        public void ClickTool(string glyph) { ((ToolStripButton)_tb.Items["gt_" + glyph]).PerformClick(); }

        void RefreshAction()
        {
            if (_step != 5 || _engine == null) return;
            List<GrabberFile> files = _engine.FilesSnapshot();
            if (files.Count != _treeFiles) { _treeFiles = files.Count; BuildTree(files); }
            FillList();
            UpdateStatus();
            if (_stats != null && !_stats.IsDisposed) _stats.UpdateFrom(_engine);
            foreach (ToolStripItem i in _tb.Items)
            {
                if (i.Name == "gt_explore") i.Enabled = !_engine.Exploring;
                if (i.Name == "gt_explore-stop") i.Enabled = _engine.Exploring;
                if (i.Name == "gt_download-stop") i.Enabled = _engine.Downloading;
            }
        }

        static TreeNode Node(string text, string tag, string icon)
        {
            TreeNode n = new TreeNode(text); n.Tag = tag; n.ImageKey = n.SelectedImageKey = icon; return n;
        }

        void BuildTree(List<GrabberFile> files)
        {
            string keep = _view;
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            _tree.Nodes.Add(Node(Lang.T("Ver Miniaturas"), "thumbs", "thumbs"));
            TreeNode all = Node(Lang.T("Todos los Archivos"), "all", "folder"); _tree.Nodes.Add(all);
            TreeNode links = Node(Lang.T("Ver Enlace"), "links", "links"); _tree.Nodes.Add(links);
            TreeNode folders = Node(Lang.T("Ver Carpeta"), "folders", "folder-open"); _tree.Nodes.Add(folders);
            SortedDictionary<string, bool> pages = new SortedDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            SortedDictionary<string, bool> dirs = new SortedDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (GrabberFile f in files)
            {
                if (!string.IsNullOrEmpty(f.Referer)) pages[f.Referer] = true;
                dirs[DirOf(f.Url)] = true;
            }
            foreach (string pg in pages.Keys) { TreeNode n = Node(PageLabel(pg), "page:" + pg, "page"); n.ToolTipText = pg; links.Nodes.Add(n); }
            // Ver Carpeta: "*.dominio sitios" (los del dominio de la página de inicio) y "Otros sitios", como IDM.
            string main = MainDomain();
            TreeNode mainNode = Node(Lang.T("*." + main + " sitios"), "group:main", "folder"), other = Node(Lang.T("Otros sitios"), "group:other", "folder");
            folders.Nodes.Add(mainNode); folders.Nodes.Add(other);
            Dictionary<string, TreeNode> hostNodes = new Dictionary<string, TreeNode>();
            foreach (string d in dirs.Keys)
            {
                string host = d.Substring(0, d.IndexOf('/') < 0 ? d.Length : d.IndexOf('/'));
                TreeNode h;
                if (!hostNodes.TryGetValue(host, out h))
                {
                    h = Node(host, "dir:" + host + "/", "folder"); hostNodes[host] = h;
                    (IsMainDomain(host, main) ? mainNode : other).Nodes.Add(h);
                }
                if (d != host + "/") h.Nodes.Add(Node(d.Substring(host.Length), "dir:" + d, "folder"));
            }
            links.Expand(); folders.Expand();
            TreeNode sel = Find(_tree.Nodes, keep) ?? all;
            _tree.SelectedNode = sel;
            _tree.EndUpdate();
        }

        string MainDomain()
        {
            try { return GrabberEngine.MainDomainOf(new Uri(_p.StartUrl).Host); } catch { return ""; }
        }

        static bool IsMainDomain(string hostPort, string main)
        {
            string host = hostPort; int c = host.LastIndexOf(':'); if (c > 0 && host.IndexOf(']') < c) host = host.Substring(0, c);
            return main.Length > 0 && string.Equals(GrabberEngine.MainDomainOf(host), main, StringComparison.OrdinalIgnoreCase);
        }

        static TreeNode Find(TreeNodeCollection nodes, string tag)
        {
            foreach (TreeNode n in nodes)
            {
                if ((string)n.Tag == tag) return n;
                TreeNode r = Find(n.Nodes, tag);
                if (r != null) return r;
            }
            return null;
        }

        // Páginas donde se encontraron archivos: servidor y ruta (127.0.0.1:18765/), como IDM.
        static string PageLabel(string url)
        {
            string s;
            try { Uri u = new Uri(url); s = u.Authority + Uri.UnescapeDataString(u.PathAndQuery); }
            catch { s = GrabberFilters.StripScheme(url); }
            return s.Length > 60 ? "..." + s.Substring(s.Length - 57) : s;
        }

        static string DirOf(string url)
        {
            string s = GrabberFilters.StripScheme(url);
            int q = s.IndexOfAny(new char[] { '?', '#' }); if (q >= 0) s = s.Substring(0, q);
            int slash = s.LastIndexOf('/');
            return slash >= 0 ? s.Substring(0, slash + 1) : s + "/";
        }

        bool InView(GrabberFile f)
        {
            if (_view == "all" || _view == "links" || _view == "folders" || _view == "thumbs") return true;
            if (_view.StartsWith("page:")) return f.Referer == _view.Substring(5);
            if (_view.StartsWith("group:"))
            {
                string d = DirOf(f.Url); string host = d.Substring(0, d.IndexOf('/') < 0 ? d.Length : d.IndexOf('/'));
                return IsMainDomain(host, MainDomain()) == (_view == "group:main");
            }
            if (_view.StartsWith("dir:"))
            {
                string d = _view.Substring(4), fd = DirOf(f.Url);
                return d.EndsWith("/") && d.IndexOf('/') == d.Length - 1 ? fd.StartsWith(d, StringComparison.OrdinalIgnoreCase) : fd == d;
            }
            return true;
        }

        void FillList()
        {
            if (_engine == null) return;
            bool thumbs = _view == "thumbs";
            View want = thumbs ? View.LargeIcon : View.Details;
            _programmatic = true;
            _list.BeginUpdate();
            if (_list.View != want) _list.View = want;
            HashSet<GrabberFile> keep = new HashSet<GrabberFile>();
            foreach (GrabberFile f in _engine.FilesSnapshot())
            {
                if (!InView(f)) continue;
                keep.Add(f);
                ListViewItem it;
                if (!_rows.TryGetValue(f, out it)) { it = new ListViewItem(new string[7]); it.Tag = f; _rows[f] = it; }
                string[] v = { f.Name, Lang.T(f.Type), f.Size >= 0 ? Fmt.Size(f.Size) : "", Lang.T(f.Status ?? ""), Lang.T(f.LinkText ?? ""), f.Url ?? "", f.SaveTo ?? "" };
                for (int i = 0; i < v.Length; i++) if (it.SubItems[i].Text != v[i]) it.SubItems[i].Text = v[i];
                string key = IconKey(f, thumbs);
                if (it.ImageKey != key) it.ImageKey = key;
                if (it.Checked != f.Checked) it.Checked = f.Checked;
                if (it.ListView == null) _list.Items.Add(it);
            }
            for (int i = _list.Items.Count - 1; i >= 0; i--) if (!keep.Contains((GrabberFile)_list.Items[i].Tag)) _list.Items.RemoveAt(i);
            _list.EndUpdate();
            _programmatic = false;
        }

        // Icono del tipo de archivo (lista) o miniatura (Ver Miniaturas: la imagen ya descargada).
        string IconKey(GrabberFile f, bool thumbs)
        {
            if (thumbs)
            {
                bool done = f.Status == "Completo" && !string.IsNullOrEmpty(f.SaveTo);
                string k = "t" + (done ? "1:" : "0:") + f.Url;
                if (_thumbKeys.Add(k))
                    using (Bitmap b = Thumb(f, done)) _thumbs.Images.Add(k, b); // la ImageList guarda su propia copia
                return k;
            }
            string e = "e:" + ShellIcons.ExtensionOf(f.Name);
            if (!_icons.Images.ContainsKey(e))
            {
                Bitmap b = null;
                try { b = ShellIcons.For(f.Name, false); } catch { }
                if (b == null) return "";
                _icons.Images.Add(e, b);
            }
            return e;
        }

        Bitmap Thumb(GrabberFile f, bool done)
        {
            int s = _thumbs.ImageSize.Width;
            Bitmap b = new Bitmap(s, s);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                bool drawn = false;
                string ext = ShellIcons.ExtensionOf(f.Name);
                if (done && (ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".gif" || ext == ".bmp" || ext == ".ico" || ext == ".tif" || ext == ".tiff"))
                {
                    try
                    {
                        FileInfo fi = new FileInfo(f.SaveTo);
                        // Se decodifica en el hilo de la interfaz: solo imágenes de tamaño razonable.
                        if (fi.Exists && fi.Length < 4L * 1024 * 1024)
                            using (MemoryStream ms = new MemoryStream(File.ReadAllBytes(f.SaveTo)))
                            using (Image img = Image.FromStream(ms))
                            {
                                float k = Math.Min((s - 4f) / img.Width, (s - 4f) / img.Height);
                                k = Math.Min(k, 1f);
                                int w = Math.Max(1, (int)(img.Width * k)), h = Math.Max(1, (int)(img.Height * k));
                                g.Clear(Color.White);
                                g.DrawImage(img, (s - w) / 2, (s - h) / 2, w, h);
                                using (Pen p = new Pen(Color.FromArgb(0xC8, 0xC8, 0xC8))) g.DrawRectangle(p, 0, 0, s - 1, s - 1);
                                drawn = true;
                            }
                    }
                    catch { drawn = false; }
                }
                if (!drawn)
                {
                    Bitmap ic = null;
                    try { ic = ShellIcons.For(f.Name, true); } catch { }
                    if (ic != null) g.DrawImage(ic, (s - ic.Width) / 2, (s - ic.Height) / 2, ic.Width, ic.Height);
                }
            }
            return b;
        }

        // IDM no tiene barra de estado: el resumen está en "Estadísticas" (y aquí, para las pruebas).
        void UpdateStatus()
        {
            if (_engine == null) return;
            int pd, pt, files, matched, done;
            _engine.Counts(out pd, out pt, out files, out matched, out done);
            string st = _engine.Exploring ? "Explorando sitio" : _engine.Downloading ? "Descargando" : _engine.StatusText;
            _statusText = Lang.T("Estado: " + st) + "     " + Lang.T("Páginas web procesadas: " + pd + " de " + pt) + "     " + Lang.T("Archivos encontrados: " + files) +
                          "     " + Lang.T("Marcados: " + matched) + "     " + Lang.T("Archivos descargados: " + done);
            string login = _engine.LoginNote;
            if (!string.IsNullOrEmpty(login)) _statusText += "     " + Lang.T(login);
        }

        void SetChecked(bool on)
        {
            if (_engine == null) return;
            foreach (GrabberFile f in _engine.FilesSnapshot()) f.Checked = on;
            _dirty = true;
            FillList(); UpdateStatus();
        }

        List<GrabberFile> Selected()
        {
            List<GrabberFile> r = new List<GrabberFile>();
            foreach (ListViewItem i in _list.SelectedItems) r.Add((GrabberFile)i.Tag);
            return r;
        }

        void CheckSelected(bool on) { foreach (GrabberFile f in Selected()) f.Checked = on; _dirty = true; FillList(); UpdateStatus(); }

        void OpenSelected(bool folder)
        {
            List<GrabberFile> l = Selected();
            if (l.Count == 0 || string.IsNullOrEmpty(l[0].SaveTo)) return;
            try
            {
                if (File.Exists(l[0].SaveTo)) { if (folder) Process.Start("explorer.exe", "/select,\"" + l[0].SaveTo + "\""); else Process.Start(l[0].SaveTo); }
                else if (folder && Directory.Exists(Path.GetDirectoryName(l[0].SaveTo))) Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(l[0].SaveTo) + "\"");
            }
            catch (Exception ex) { Say(ex.Message, MessageBoxIcon.Warning); }
        }

        void ShowProperties()
        {
            List<GrabberFile> l = Selected();
            if (l.Count == 0) return;
            using (GrabberFilePropsForm f = new GrabberFilePropsForm(l[0])) f.ShowDialog(this);
        }

        public void DownloadChecked()
        {
            EnsureEngine();
            if (!_engine.FilesSnapshot().Exists(delegate(GrabberFile f) { return f.Checked; })) { Say("¡Archivos no seleccionados!"); return; }
            _engine.StartDownloading(null);
            _dirty = true;
        }

        public int AddToMain()
        {
            EnsureEngine();
            if (!_engine.FilesSnapshot().Exists(delegate(GrabberFile f) { return f.Checked; })) { Say("¡Archivos no seleccionados!"); return 0; }
            int n = _engine.AddCheckedTo(_mgr);
            Say(n + " archivos fueron añadidos a la lista principal de IDMClone.");
            return n;
        }

        void ShowSettings()
        {
            using (GrabberSettingsForm f = new GrabberSettingsForm(_p)) if (f.ShowDialog(this) == DialogResult.OK) _dirty = true;
        }

        void ShowStats()
        {
            EnsureEngine();
            bool fresh = _stats == null || _stats.IsDisposed;
            if (fresh)
            {
                _stats = new GrabberStatsForm(); _stats.Owner = this;
                _stats.FormClosing += delegate(object s, FormClosingEventArgs e) { if (e.CloseReason == CloseReason.UserClosing) _statsClosed = true; };
            }
            _stats.UpdateFrom(_engine);
            if (fresh && _list.IsHandleCreated && _step == 5)
            {
                // Como IDM: flotando abajo a la derecha de la lista.
                Point p = _list.PointToScreen(new Point(Math.Max(0, _list.ClientSize.Width - _stats.Width - Dpi.Px(16)), Math.Max(0, _list.ClientSize.Height - _stats.Height - Dpi.Px(16))));
                _stats.Location = p;
            }
            _stats.Show();
        }

        // Opciones > Programar el proyecto: empezar a explorar a una hora (con el Grabber abierto).
        void ScheduleProject()
        {
            using (GrabberScheduleForm f = new GrabberScheduleForm(_scheduleAt))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                _scheduleAt = f.At;
            }
        }

        void RunScheduled()
        {
            _scheduleAt = DateTime.MinValue;
            if (_step < 5)
            {
                string u;
                if (!ValidStart(out u)) { Say("Por favor, introduce la página/dirección de inicio", MessageBoxIcon.Warning); return; }
                _start.Text = u;
                FromUi();
                ShowStep(5);
            }
            StartExploring(true);
            if (!_statsClosed) ShowStats();
        }

        // ------------------------------------------------------------------ proyectos

        void NewProject()
        {
            if (!ConfirmSave()) return;
            SetProject(NewDefault(null));
            ShowStep(1);
        }

        void LoadProject()
        {
            List<string> names = GrabberProject.SavedNames();
            if (names.Count == 0) { Say("No hay proyectos guardados de IDMClone Grabber"); return; }
            using (GrabberPickForm f = new GrabberPickForm(names))
            {
                if (f.ShowDialog(this) != DialogResult.OK || f.Picked == null) return;
                OpenProject(f.Picked);
            }
        }

        void OpenProject(string name)
        {
            if (!ConfirmSave()) return;
            try { SetProject(GrabberProject.Load(name)); ShowStep(_p.Pages.Count > 0 ? 5 : 1); }
            catch (Exception ex) { Say(ex.Message, MessageBoxIcon.Warning); }
        }

        // Guarda el proyecto; pide nombre si no lo tiene (o si se pide "con otro nombre").
        public bool SaveProject(bool ask)
        {
            if (_step < 5) FromUi();
            string name = _p.Name;
            if (string.IsNullOrEmpty(name) || ask)
            {
                using (GrabberNameForm f = new GrabberNameForm("Guardando el proyecto de IDMClone Grabber", "Guardar este proyecto con el siguiente nombre:", SuggestName()))
                {
                    if (f.ShowDialog(this) != DialogResult.OK) return false;
                    name = f.Value;
                }
                if (GrabberProject.SavedNames().Exists(delegate(string x) { return string.Equals(x, Util.SanitizeFileName(name), StringComparison.CurrentCultureIgnoreCase); })
                    && !string.Equals(name, _p.Name, StringComparison.CurrentCultureIgnoreCase))
                { Say("El proyecto con el nombre \"" + name + "\" ya existe. Por favor, selecciona otro nombre.", MessageBoxIcon.Warning); return false; }
            }
            _p.Name = name;
            _name.Text = name;
            try { _p.Save(); }
            catch (Exception ex) { Say("No se pudo guardar el proyecto: " + ex.Message, MessageBoxIcon.Warning); return false; }
            _dirty = false;
            Action h = ProjectsChanged; if (h != null) h();
            return true;
        }

        string SuggestName()
        {
            try { return new Uri(NormalizeUrl(_start.Text)).Host; } catch { return "Proyecto"; }
        }

        void SaveTemplate()
        {
            FromUi();
            using (GrabberNameForm f = new GrabberNameForm("Nombre de la Plantilla", "Por favor, introduce un nombre para la plantilla de Grabber", ""))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                if (GrabberProject.UserTemplates().Exists(delegate(string x) { return string.Equals(x, Util.SanitizeFileName(f.Value), StringComparison.CurrentCultureIgnoreCase); })
                    && Ask("La plantilla con este nombre ya existe. ¿Deseas reemplazar la plantilla?", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                try
                {
                    _p.SaveAsTemplate(f.Value);
                    if (!_template.Items.Contains(f.Value)) _template.Items.Add(f.Value);
                }
                catch (Exception ex) { Say(ex.Message, MessageBoxIcon.Warning); }
            }
        }

        // "¿Guardar el proyecto de Grabber?" [Sí] [No] (captura 44e). Devuelve false si no se pudo guardar.
        bool ConfirmSave()
        {
            bool worth = _p.Pages.Count > 0 && (_dirty || string.IsNullOrEmpty(_p.Name) || _engine != null);
            if (!worth) return true;
            DialogResult r = Ask("¿Guardar el proyecto de Grabber?", MessageBoxButtons.YesNo);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) return SaveProject(false);
            return true;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _jsRisk.Left = _jsNote.Right + 2; // el texto puede cambiar de largo al traducirlo
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!ConfirmSave()) { e.Cancel = true; return; }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            if (_engine != null)
            {
                if (_p.AddOnClose) { try { _engine.AddCheckedTo(_mgr); } catch { } }
                _engine.Close();
            }
            if (_stats != null && !_stats.IsDisposed) _stats.Close();
            _tips.Dispose();
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_thumbs != null) _thumbs.Dispose();
                if (_icons != null) _icons.Dispose();
                if (_treeIcons != null) _treeIcons.Dispose();
            }
            base.Dispose(disposing);
        }

        // Para las pruebas.
        public void SetStart(string url, int template)
        {
            _start.Text = url;
            _template.SelectedIndex = template;
        }
        public void SetSaveFolder(string folder, bool relative) { _toFolder.Checked = true; _folder.Text = folder; _relative.Checked = relative; }
        public void SetLevels(bool whole, int site, int other) { _whole.Checked = whole; _levels.Checked = !whole; _lvSite.Value = site; _lvOther.Value = other; }
        public void SetName(string name) { _name.Text = name; }
        public void SetManualLogin(bool on, string url) { _manual.Checked = on; _manualUrl.Text = url ?? ""; }
        public void RunScheduledNow() { RunScheduled(); }
        public void SetAdvancedOpen(int step, bool open) { SetAdvanced(step - 1, open); }
        public int ListCount { get { return _list.Items.Count; } }
        public string StatusText { get { UpdateStatus(); return _statusText; } }
        public void SelectView(string tag) { TreeNode n = Find(_tree.Nodes, tag); if (n != null) _tree.SelectedNode = n; }
        public bool IsDirty { get { return _dirty; } }
    }

    // "Estadísticas de IDM Grabber": ventanita flotante (captura 45).
    public class GrabberStatsForm : DialogBase
    {
        readonly Label _state, _simple, _advanced, _total, _examined, _matched, _downloaded;

        public GrabberStatsForm()
        {
            Text = "Estadísticas de IDMClone Grabber";
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ClientSize = new Size(180, 166);
            StartPosition = FormStartPosition.Manual;
            _state = AddLabel("", 14, 4, 160); _state.Height = 18;
            Rule(24);
            Head("Páginas web procesadas", 30);
            _simple = Row("Simple", 46); _advanced = Row("Avanzado", 61);
            Rule(80);
            Head("Archivos", 86);
            _total = Row("Total", 102); _examined = Row("Examinados", 117); _matched = Row("Coincidentes", 132); _downloaded = Row("Descargados", 147);
            EndLayout();
        }

        void Rule(int y) { Label l = new Label(); l.BorderStyle = BorderStyle.Fixed3D; l.SetBounds(6, y, 168, 2); Controls.Add(l); }
        void Head(string text, int y) { Label l = new Label(); l.Text = text; l.SetBounds(8, y, 168, 15); Controls.Add(l); }

        Label Row(string text, int y)
        {
            Label a = new Label(); a.Text = text; a.SetBounds(8, y, 100, 15); a.TextAlign = ContentAlignment.MiddleRight; Controls.Add(a);
            Label v = new Label(); v.SetBounds(112, y, 64, 15); v.TextAlign = ContentAlignment.MiddleLeft; v.Tag = Lang.Keep; Controls.Add(v);
            return v;
        }

        public void UpdateFrom(GrabberEngine e)
        {
            int pd, pt, files, matched, done;
            e.Counts(out pd, out pt, out files, out matched, out done);
            _state.Text = Lang.T("Estado: " + (e.Exploring ? "Explorando sitio" : e.Downloading ? "Descargando" : e.StatusText));
            // IDMClone solo tiene un modo de procesar páginas (el "Simple" de IDM).
            _simple.Text = pd.ToString(); _advanced.Text = "0";
            _total.Text = files.ToString(); _examined.Text = e.Examined.ToString(); _matched.Text = matched.ToString(); _downloaded.Text = done.ToString();
        }
    }

    // Opciones > Configuración de Grabber (captura 44d).
    public class GrabberSettingsForm : DialogBase
    {
        readonly GrabberProject _p;
        readonly NumericUpDown _explore, _download;
        readonly CheckBox _desc;

        public GrabberSettingsForm(GrabberProject p)
        {
            _p = p;
            Text = "Configuración de IDMClone Grabber";
            ClientSize = new Size(424, 168);
            _explore = new NumericUpDown(); _explore.Minimum = 1; _explore.Maximum = 10; _explore.SetBounds(12, 12, 38, 21); Controls.Add(_explore);
            AddLabel("archivos a examinar al mismo tiempo (1 a 10)", 56, 11, 360);
            _download = new NumericUpDown(); _download.Minimum = 1; _download.Maximum = 10; _download.SetBounds(12, 40, 38, 21); Controls.Add(_download);
            AddLabel("archivos a descargar al mismo tiempo (1 a 10)", 56, 39, 360);
            Label n = AddLabel("Ten en cuenta que un servidor web puede rechazar las peticiones si se exploran (o descargan) muchos archivos al mismo tiempo.", 12, 68, 400);
            n.Height = 30; n.TextAlign = ContentAlignment.TopLeft;
            _desc = new CheckBox(); _desc.Text = "Usar los textos de los enlaces como descripción al añadir archivos a la lista"; _desc.SetBounds(12, 104, 400, 20); Controls.Add(_desc);
            Button ok = AddButton("Aceptar", 254, 136, 75, DialogResult.OK); ok.Height = 23;
            ok.Click += delegate { _p.ExploreAtOnce = (int)_explore.Value; _p.DownloadAtOnce = (int)_download.Value; _p.LinkTextAsDescription = _desc.Checked; };
            Button cancel = AddButton("Cancelar", 337, 136, 75, DialogResult.Cancel); cancel.Height = 23;
            AcceptButton = ok; CancelButton = cancel;
            _explore.Value = Math.Max(1, Math.Min(10, p.ExploreAtOnce)); _download.Value = Math.Max(1, Math.Min(10, p.DownloadAtOnce)); _desc.Checked = p.LinkTextAsDescription;
            EndLayout();
        }
    }

    // "propiedades" de un archivo de la lista del Grabber.
    public class GrabberFilePropsForm : DialogBase
    {
        public GrabberFilePropsForm(GrabberFile f)
        {
            Text = "Propiedades";
            ClientSize = new Size(470, 252);
            int y = 12;
            y = Row("Nombre de archivo:", f.Name, y);
            y = Row("Tipo de archivo:", Lang.T(f.Type ?? ""), y);
            y = Row("Tamaño:", f.Size >= 0 ? Fmt.Size(f.Size) : "", y);
            y = Row("Estado:", Lang.T(f.Status ?? ""), y);
            y = Row("Dirección:", f.Url, y);
            y = Row("Referencia:", f.Referer, y);
            y = Row("Texto del enlace:", f.LinkText, y);
            y = Row("Guardar en:", f.SaveTo, y);
            Button ok = AddButton("Aceptar", 383, 218, 75, DialogResult.OK); ok.Height = 23;
            AcceptButton = ok; CancelButton = ok; ActiveControl = ok;
            EndLayout();
        }

        int Row(string label, string value, int y)
        {
            Label l = AddLabel(label, 12, y, 110); l.Height = 21;
            TextBox t = new TextBox(); t.ReadOnly = true; t.Text = value ?? ""; t.SetBounds(124, y, 334, 21); Controls.Add(t);
            return y + 25;
        }
    }

    // Opciones > Programar el proyecto.
    public class GrabberScheduleForm : DialogBase
    {
        readonly CheckBox _on;
        readonly DateTimePicker _time;
        public DateTime At { get; private set; }

        public GrabberScheduleForm(DateTime current)
        {
            Text = "Programar el proyecto";
            ClientSize = new Size(380, 118);
            _on = new CheckBox(); _on.Text = "Iniciar la exploración del sitio a las"; _on.SetBounds(12, 14, 230, 20); Controls.Add(_on);
            _time = new DateTimePicker(); _time.Format = DateTimePickerFormat.Time; _time.ShowUpDown = true; _time.SetBounds(246, 13, 120, 21); Controls.Add(_time);
            Label n = AddLabel("La ventana del Grabber debe seguir abierta hasta esa hora.", 12, 42, 356); n.ForeColor = SystemColors.GrayText;
            Button ok = AddButton("Aceptar", 210, 84, 75, DialogResult.OK); ok.Height = 23;
            Button cancel = AddButton("Cancelar", 291, 84, 75, DialogResult.Cancel); cancel.Height = 23;
            AcceptButton = ok; CancelButton = cancel;
            _on.Checked = current != DateTime.MinValue;
            _time.Value = current != DateTime.MinValue ? current : DateTime.Now.AddMinutes(5);
            _time.Enabled = _on.Checked;
            _on.CheckedChanged += delegate { _time.Enabled = _on.Checked; };
            ok.Click += delegate
            {
                if (!_on.Checked) { At = DateTime.MinValue; return; }
                DateTime t = DateTime.Today.Add(_time.Value.TimeOfDay);
                if (t <= DateTime.Now) t = t.AddDays(1); // una hora ya pasada: mañana
                At = t;
            };
            EndLayout();
        }
    }

    // Lista de filtros (Agregar / Editar / Eliminar).
    public class GrabberFiltersForm : DialogBase
    {
        readonly ListView _l;
        public string Selected { get; private set; }

        public GrabberFiltersForm(bool picking)
        {
            Text = "Filtros";
            ClientSize = new Size(500, 300);
            _l = new ListView(); _l.View = View.Details; _l.FullRowSelect = true; _l.HideSelection = false; _l.MultiSelect = false; _l.GridLines = true;
            _l.Columns.Add("Nombre", 160); _l.Columns.Add("Máscara", 230);
            _l.SetBounds(12, 12, 390, 276); Controls.Add(_l);
            Button add = AddButton("Agregar", 412, 12, 75, DialogResult.None); add.Height = 23;
            Button edit = AddButton("Editar", 412, 40, 75, DialogResult.None); edit.Height = 23;
            Button del = AddButton("Eliminar", 412, 68, 75, DialogResult.None); del.Height = 23;
            Button ok = AddButton(picking ? "Usar" : "Aceptar", 412, 237, 75, DialogResult.OK); ok.Height = 23;
            Button cancel = AddButton("Cancelar", 412, 265, 75, DialogResult.Cancel); cancel.Height = 23;
            AcceptButton = ok; CancelButton = cancel;
            add.Click += delegate { Edit(null); };
            edit.Click += delegate { if (_l.SelectedItems.Count > 0) Edit((GrabberFilter)_l.SelectedItems[0].Tag); };
            _l.DoubleClick += delegate { if (_l.SelectedItems.Count > 0) Edit((GrabberFilter)_l.SelectedItems[0].Tag); };
            del.Click += delegate
            {
                if (_l.SelectedItems.Count == 0) return;
                GrabberFilters.List.Remove((GrabberFilter)_l.SelectedItems[0].Tag);
                foreach (GrabberFilter d in GrabberFilters.Defaults()) if (GrabberFilters.Find(d.Name) == null) GrabberFilters.List.Add(d); // los de serie no se pierden
                Fill();
            };
            ok.Click += delegate
            {
                GrabberFilters.Save();
                if (_l.SelectedItems.Count > 0) Selected = ((GrabberFilter)_l.SelectedItems[0].Tag).Name;
            };
            cancel.Click += delegate { GrabberFilters.Reload(); };
            Fill();
            EndLayout();
        }

        void Fill()
        {
            _l.Items.Clear();
            foreach (GrabberFilter f in GrabberFilters.List) { ListViewItem i = new ListViewItem(new string[] { Lang.T(f.Name), f.Mask }); i.Tag = f; _l.Items.Add(i); }
        }

        void Edit(GrabberFilter f)
        {
            using (GrabberFilterEditForm e = new GrabberFilterEditForm(f))
            {
                if (e.ShowDialog(this) != DialogResult.OK) return;
                if (f == null) GrabberFilters.List.Add(e.Result);
                else { f.Name = e.Result.Name; f.Mask = e.Result.Mask; }
                Fill();
                foreach (ListViewItem i in _l.Items) if (((GrabberFilter)i.Tag).Name == e.Result.Name) { i.Selected = true; i.EnsureVisible(); }
            }
        }
    }

    public class GrabberFilterEditForm : DialogBase
    {
        readonly TextBox _name, _mask;
        readonly GrabberFilter _orig;
        public GrabberFilter Result { get; private set; }

        public GrabberFilterEditForm(GrabberFilter f)
        {
            _orig = f;
            Text = "Filtro";
            ClientSize = new Size(420, 172);
            AddLabel("Nombre del filtro", 12, 8, 300);
            _name = new TextBox(); _name.SetBounds(12, 30, 396, 21); Controls.Add(_name);
            AddLabel("Máscara", 12, 56, 300);
            _mask = new TextBox(); _mask.SetBounds(12, 78, 396, 21); Controls.Add(_mask);
            Label n = AddLabel("Separa los tipos con comas sin espacios; * es comodín (p. ej. *.jpg,image*.png). \"<start page>\" es la página de inicio.", 12, 104, 396);
            n.Height = 30; n.TextAlign = ContentAlignment.TopLeft; n.ForeColor = SystemColors.GrayText;
            Button ok = AddButton("Aceptar", 252, 140, 75, DialogResult.None); ok.Height = 23;
            Button cancel = AddButton("Cancelar", 333, 140, 75, DialogResult.Cancel); cancel.Height = 23;
            AcceptButton = ok; CancelButton = cancel;
            ok.Click += delegate { if (Accept()) { DialogResult = DialogResult.OK; Close(); } };
            if (f != null) { _name.Text = f.Name; _mask.Text = f.Mask; }
            EndLayout();
        }

        public void Set(string name, string mask) { _name.Text = name; _mask.Text = mask; }

        public bool Accept()
        {
            string name = _name.Text.Trim(), mask = _mask.Text.Trim().Replace(" ", "");
            string err = null;
            if (name.Length == 0) err = "Por favor, complete el nombre del filtro";
            else if (mask.Length == 0) err = "Por favor, complete la máscara";
            else
            {
                GrabberFilter same = GrabberFilters.Find(name);
                if (same != null && same != _orig) err = "El filtro con el nombre \"" + name + "\" ya existe. Por favor, elige otro nombre.";
            }
            if (err != null)
            {
                if (GrabberForm.MessageHook != null) GrabberForm.MessageHook(err);
                else Msg.Show(this, err, "Filtro", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            Result = new GrabberFilter(name, mask);
            return true;
        }
    }

    // "Ingresa el usuario y contraseña manualmente en la siguiente web": se abrió la página en el navegador; el
    // usuario inicia sesión allí y pulsa Continuar. Si no se pudo leer la sesión del navegador, ofrece pegar las
    // cookies (cabecera Cookie). Lo pegado solo se usa en esta exploración: no se guarda.
    public class GrabberLoginForm : DialogBase
    {
        readonly TextBox _cookies;
        public string Pasted { get { return _cookies != null ? _cookies.Text : ""; } }

        public GrabberLoginForm(string url, bool paste, string note, Action reopen)
        {
            Text = "Iniciar sesión en el sitio";
            int w = 440, y = 10;
            AddLabel("Se ha abierto esta página en tu navegador:", 12, y, w - 24); y += 22;
            Label u = AddLabel(url, 24, y, w - 36); u.Tag = Lang.Keep; u.AutoEllipsis = true; u.ForeColor = SystemColors.HotTrack; y += 24;
            Label b = new Label(); b.Text = "Inicia sesión en el navegador y pulsa Continuar. La sesión de ese sitio solo se usa mientras explora el Grabber: no se guarda en el proyecto.";
            b.SetBounds(12, y, w - 24, 44); Controls.Add(b); y += 46;
            LinkLabel again = new LinkLabel(); again.Text = "Volver a abrir la página"; again.SetBounds(12, y, 220, 18); Controls.Add(again); y += 26;
            again.LinkClicked += delegate { if (reopen != null) reopen(); };
            if (paste)
            {
                if (!string.IsNullOrEmpty(note))
                {
                    Label n = new Label(); n.Text = note; n.SetBounds(12, y, w - 24, 32); n.ForeColor = Color.FromArgb(0xB0, 0x40, 0x00); Controls.Add(n); y += 34;
                }
                Label p = new Label(); p.Text = "Pegar cookies (la cabecera Cookie del sitio, p. ej. \"sesion=...; usuario=...\"). Déjalo vacío para explorar sin iniciar sesión:";
                p.SetBounds(12, y, w - 24, 32); Controls.Add(p); y += 34;
                _cookies = new TextBox(); _cookies.Multiline = true; _cookies.ScrollBars = ScrollBars.Vertical; _cookies.SetBounds(12, y, w - 24, 60); Controls.Add(_cookies); y += 68;
            }
            Button ok = AddButton("Continuar", w - 174, y, 78, DialogResult.OK); ok.Height = 23;
            Button cancel = AddButton("Cancelar", w - 90, y, 78, DialogResult.Cancel); cancel.Height = 23;
            if (!paste) AcceptButton = ok;
            CancelButton = cancel;
            ClientSize = new Size(w, y + 34);
            EndLayout();
        }
    }

    // Pide un nombre (guardar proyecto, nombre de la plantilla, sitio de una lista...).
    public class GrabberNameForm : DialogBase
    {
        readonly TextBox _value;
        public string Value { get { return _value.Text.Trim(); } }

        public GrabberNameForm(string title, string prompt, string initial)
        {
            Text = title;
            ClientSize = new Size(400, 104);
            AddLabel(prompt, 12, 8, 376);
            _value = new TextBox(); _value.SetBounds(12, 34, 376, 21); _value.Text = initial ?? ""; Controls.Add(_value);
            Button ok = AddButton("Aceptar", 232, 70, 75, DialogResult.OK); ok.Height = 23;
            Button cancel = AddButton("Cancelar", 313, 70, 75, DialogResult.Cancel); cancel.Height = 23;
            AcceptButton = ok; CancelButton = cancel;
            ok.Click += delegate { if (Value.Length == 0) DialogResult = DialogResult.None; };
            EndLayout();
        }
    }

    // "Cargar proyecto".
    public class GrabberPickForm : DialogBase
    {
        readonly ListBox _l;
        public string Picked { get { return _l.SelectedItem as string; } }

        public GrabberPickForm(List<string> names)
        {
            Text = "Cargar proyecto";
            ClientSize = new Size(360, 280);
            _l = new ListBox(); _l.SetBounds(12, 12, 336, 226); Controls.Add(_l);
            foreach (string n in names) _l.Items.Add(n);
            if (_l.Items.Count > 0) _l.SelectedIndex = 0;
            Button ok = AddButton("Cargar", 192, 247, 75, DialogResult.OK); ok.Height = 23;
            Button cancel = AddButton("Cancelar", 273, 247, 75, DialogResult.Cancel); cancel.Height = 23;
            AcceptButton = ok; CancelButton = cancel;
            _l.DoubleClick += delegate { DialogResult = DialogResult.OK; Close(); };
            EndLayout();
        }
    }
}
