using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace IDMClone
{
    // "Configuración de IDMClone" (IDM: "Configuración de Internet Download Manager"): 9 pestañas en dos filas,
    // cada página con su cabecera (icono + título sobre una línea) y, al pie, solo "Aceptar" y "Ayuda".
    // Cerrar con la X (o Esc) no aplica nada: los cambios se guardan solo con Aceptar.
    public class OptionsForm : DialogBase
    {
        const int W = 397;   // borde derecho del contenido de cada página
        readonly AppConfig _cfg;
        TabControl _tabs;

        // General
        CheckBox _startup, _clipboard, _explorerMenu;
        bool _explorerMenuWas;
        CheckedListBox _browsers;
        bool _usePrevent, _useForce, _menuLink, _menuAll, _ffLink, _ffAll;
        string _preventKeys, _forceKeys;
        AppConfig _media; // ajustes del panel de vídeo mientras se editan (se guardan al Aceptar)
        CheckBox _mediaPanel;
        // Tipos de archivos
        TextBox _exts, _sites;
        List<string> _addresses = new List<string>();
        string _excludeAtOpen = "";      // excepciones al abrir Opciones (para no perder las que se añadan mientras)
        bool _askOnAutoCancel = true, _askAtOpen = true; // "Editar lista..." > mostrar el diálogo de una descarga cancelada dos veces
        // Guardar
        ComboBox _saveCat;
        TextBox _catTypes, _folder, _temp;
        Button _catEdit;
        CheckBox _catRemember, _fileDate, _useTemp;
        Label _folderLabel, _catTypesLabel;
        // Carpeta y "recordar la última" de cada categoría mientras se edita (se guardan al Aceptar).
        readonly Dictionary<string, string> _catFolders = new Dictionary<string, string>();
        readonly Dictionary<string, bool> _catRemembers = new Dictionary<string, bool>();
        string _shownCat;
        // Descargas
        CheckBox _infoDlg, _queueOnly, _completeDlg, _startWhileInfo, _queuePicker, _queuePickerBatch, _ignoreDate, _confirmDelete;
        ComboBox _duplicate;
        TextBox _ua;
        string _progressMode; bool _progLimit, _progFinish, _progHide;
        string _avPath, _avArgs; // comprobación de virus mientras se edita
        // Conexión
        ComboBox _conn;
        NumericUpDown _retries, _quotaMB, _quotaHours;
        ListView _limits;
        CheckBox _quota, _quotaWarn;
        // Proxy/Socks
        RadioButton _pxNone, _pxSystem, _pxManual, _pxPac;
        TextBox _pxHost, _pxPort, _pxUser, _pxPass, _pacUrl;
        CheckBox _pxBrowser, _pxHttp, _pxHttps, _pxFtp, _pasv;
        string _pxBypass;
        // Proxys propios de https y ftp (Avanzado... / Socks...) mientras se editan: servidor vacío = el de la página.
        ProxyServerFields _pxS, _pxF;
        // Servidor SOCKS mientras se edita (Avanzado... / Socks...); se guarda al Aceptar.
        bool _sxOn, _sxDns; string _sxType, _sxHost, _sxUser, _sxPass; int _sxPort;
        // Sesiones de sitios
        ListView _logins;
        // Sonidos
        ListView _sounds;

        static readonly string[] DupIds = { "ask", "number", "overwrite", "resume" };
        static readonly string[] KnownBrowsers = { "Brave", "Google Chrome", "Microsoft Edge", "Mozilla Firefox", "Opera", "Vivaldi", "Yandex" };

        public OptionsForm(AppConfig cfg)
        {
            _cfg = cfg;
            Text = "Configuración de IDMClone";
            ClientSize = new Size(427, 505);
            _tabs = new TabControl(); _tabs.Multiline = true; _tabs.SizeMode = TabSizeMode.FillToRight;
            _tabs.SetBounds(7, 7, 413, 462); Controls.Add(_tabs);
            // Mismas pestañas y orden que IDM: la primera fila de General a Conexión y la segunda de Proxy/Socks a Sonidos.
            BuildGeneral(); BuildFileTypes(); BuildSaveTo(); BuildDownloads(); BuildConnection();
            BuildProxy(); BuildLogins(); BuildDialUp(); BuildSounds();
            Button ok = OptUi.Btn(this, "Aceptar", 266, 475);
            ok.DialogResult = DialogResult.OK;
            ok.Click += delegate { Apply(); };
            Button help = OptUi.Btn(this, "Ayuda", 345, 475);
            help.Click += delegate { OptionsHelp.Open(this); };
            AcceptButton = ok;
            _tabs.FontChanged += delegate { FitTabRows(); };
            _tabs.SizeChanged += delegate { FitTabRows(); };
            FitTabRows();
            EndLayout();
        }

        // Esc cierra sin aplicar, como la X (IDM no tiene botón Cancelar).
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            return base.ProcessDialogKey(keyData);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e); // traduce
            FitTabRows();
            // Comprobación final con el control ya creado: 5 pestañas en la primera fila y 4 en la segunda.
            for (int i = 0; i < 12 && _tabs.IsHandleCreated && _tabs.TabCount == 9; i++)
            {
                bool sixInRow = _tabs.GetTabRect(5).Y == _tabs.GetTabRect(4).Y;
                bool split = _tabs.GetTabRect(4).Y != _tabs.GetTabRect(0).Y || _tabs.GetTabRect(8).Y != _tabs.GetTabRect(5).Y;
                if (sixInRow) _tabs.Padding = new Point(_tabs.Padding.X + 1, _tabs.Padding.Y);
                else if (split && _tabs.Padding.X > 2) _tabs.Padding = new Point(_tabs.Padding.X - 1, _tabs.Padding.Y);
                else break;
            }
        }

        // Margen de las pestañas para que queden 5 en la primera fila y 4 en la segunda, como en IDM
        // (el control reparte las pestañas por anchura; con este margen la sexta ya no cabe en la primera fila).
        bool _fitting;
        void FitTabRows()
        {
            if (_fitting || _tabs.TabCount != 9) return;
            _fitting = true;
            try
            {
                int avail = _tabs.Width - 6;
                int[] w = new int[9];
                for (int i = 0; i < 9; i++) w[i] = TextRenderer.MeasureText(_tabs.TabPages[i].Text, _tabs.Font).Width;
                int row1 = w[0] + w[1] + w[2] + w[3] + w[4], row2 = w[5] + w[6] + w[7] + w[8];
                // Ancho de una pestaña ~ texto + 2 * margen + 2. Cabe la fila 1: row1 + 10m + 10 <= avail;
                // la sexta no: row1 + w5 + 12m + 12 > avail; la fila 2 cabe: row2 + 8m + 8 <= avail.
                double hi = Math.Min((avail - row1 - 10) / 10.0, (avail - row2 - 8) / 8.0);
                double lo = (avail - row1 - w[5] - 12) / 12.0;
                int m = (int)Math.Round(lo < hi ? (lo + hi) / 2 : hi);
                if (m < 3) m = 3;
                if (_tabs.Padding.X != m) _tabs.Padding = new Point(m, 3);
            }
            finally { _fitting = false; }
        }

        TabPage Page(string id, string title)
        {
            TabPage p = new TabPage(title); p.Name = id; p.UseVisualStyleBackColor = true; _tabs.TabPages.Add(p); return p;
        }

        // Abre el diálogo en una pestaña concreta (p. ej. "Tipos de archivo" desde la extensión).
        public void SelectTab(string title)
        {
            if (string.IsNullOrEmpty(title)) return;
            string t = title.Trim().ToLowerInvariant();
            string id = t == "tipos de archivo" || t == "tipos de archivos" || t == "file types" ? "types"
                : t == "guardar en" || t == "guardar" || t == "save to" ? "save"
                : t == "proxy" || t == "proxy/socks" ? "proxy"
                : t == "inicios de sesión" || t == "sesiones de sitios" || t == "site logins" ? "logins"
                : t == "discado / vpn" || t == "marcado/vpn" ? "dialup" : null;
            foreach (TabPage p in _tabs.TabPages)
                if (p.Name == id || p.Name == t || string.Equals(p.Text, title, StringComparison.OrdinalIgnoreCase)) { _tabs.SelectedTab = p; return; }
        }

        // ------------------------------------------------------------------ General
        void BuildGeneral()
        {
            TabPage p = Page("general", "General");
            OptUi.Header(p, "general", "Navegador/Integración al sistema", false, W);
            List<ExtensionSeen.Entry> seen = ExtensionSeen.Read();
            List<string> names = new List<string>();
            foreach (ExtensionSeen.Entry e in seen) if (e.Browser != "?" && !names.Contains(e.Browser)) names.Add(e.Browser);
            OptUi.LblRight(p, names.Count > 0 ? "La extensión de IDMClone está conectada" : "La extensión de IDMClone aún no se ha conectado", 8, 40, W - 8);
            _startup = OptUi.Chk(p, "Lanzar IDMClone al arrancar Windows", 8, 62, W - 8);
            _startup.Checked = Startup.IsEnabled(Application.ExecutablePath);
            _clipboard = OptUi.Chk(p, "Descargar automáticamente las URLs copiadas al portapapeles", 8, 81, W - 8);
            _clipboard.Checked = _cfg.MonitorClipboard;
            // Como la extensión del shell de IDM: "Descargar con IDMClone" en los .url y "Descargar los enlaces
            // con IDMClone" en .txt/.htm/.html (HKCU\Software\Classes; lo pone también el instalador).
            _explorerMenu = OptUi.Chk(p, "Integrar en el menú contextual del Explorador", 8, 100, W - 8);
            _explorerMenuWas = _explorerMenu.Checked = ShellMenu.IsRegistered(Application.ExecutablePath);

            GroupBox g = OptUi.Group(p, "Capturar descargas para los siguientes navegadores:", 8, 122, W - 8, 170);
            _browsers = OptUi.Add(g, new CheckedListBox(), 10, 18, g.Width - 20, 116); // 7 filas enteras, como IDM
            _browsers.CheckOnClick = true; _browsers.IntegralHeight = true;
            List<string> off = SplitList(_cfg.CaptureBrowsersOff);
            List<string> all = new List<string>(KnownBrowsers);
            foreach (string b in SplitList(_cfg.CaptureBrowsersExtra)) if (!all.Contains(b)) all.Add(b);
            foreach (string b in names) if (!all.Contains(b)) all.Add(b);
            foreach (string b in all) _browsers.Items.Add(b, !off.Contains(b));
            Button add = OptUi.Btn(g, "Añadir navegador...", g.Width - 130, 140, 120);
            add.Click += delegate { AddBrowser(); };

            OptUi.LblRight(p, "Personalizar las teclas para prevenir o forzar descargas", 8, 304, 290);
            Button keys = OptUi.Btn(p, "Teclas...", 306, 300, 91);
            _usePrevent = _cfg.UseKeyToPrevent; _preventKeys = _cfg.PreventKeys; _useForce = _cfg.UseKeyToForce; _forceKeys = _cfg.ForceKeys;
            keys.Click += delegate
            {
                using (SpecialKeysForm f = new SpecialKeysForm(_usePrevent, _preventKeys, _useForce, _forceKeys))
                    if (f.ShowDialog(this) == DialogResult.OK) { _usePrevent = f.UsePrevent; _preventKeys = f.PreventKeys; _useForce = f.UseForce; _forceKeys = f.ForceKeys; }
            };
            OptUi.Line(p, 8, 330, W - 8);
            OptUi.LblRight(p, "Personalizar el menú contextual del navegador", 8, 342, 290);
            Button menu = OptUi.Btn(p, "Editar...", 306, 338, 91);
            _menuLink = _cfg.BrowserMenuDownload; _menuAll = _cfg.BrowserMenuDownloadAll; _ffLink = _cfg.FirefoxMenuDownload; _ffAll = _cfg.FirefoxMenuDownloadAll;
            menu.Click += delegate
            {
                using (BrowserMenuForm f = new BrowserMenuForm(_menuLink, _menuAll, _ffLink, _ffAll))
                    if (f.ShowDialog(this) == DialogResult.OK) { _menuLink = f.ChromeLink; _menuAll = f.ChromeAll; _ffLink = f.FirefoxLink; _ffAll = f.FirefoxAll; }
            };
            OptUi.Line(p, 8, 368, W - 8);
            // La casilla para activar o desactivar el panel "Descargar este video" (con sus ajustes en Editar...).
            _media = new AppConfig();
            CopyMedia(_cfg, _media);
            _mediaPanel = OptUi.Chk(p, "Mostrar el panel de descarga en los navegadores", 8, 380, 290);
            _mediaPanel.Checked = _media.MediaPanel;
            Button panel = OptUi.Btn(p, "Editar...", 306, 376, 91);
            panel.Click += delegate
            {
                _media.MediaPanel = _mediaPanel.Checked;
                using (MediaPanelForm f = new MediaPanelForm(_media)) f.ShowDialog(this);
                _mediaPanel.Checked = _media.MediaPanel;
            };
        }

        void AddBrowser()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "Programas (*.exe)|*.exe";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string name = ExtensionSeen.DisplayName(Path.GetFileName(d.FileName));
                if (_browsers.Items.Contains(name)) return;
                _browsers.Items.Add(name, true);
            }
        }

        static List<string> SplitList(string s)
        {
            List<string> r = new List<string>();
            foreach (string x in (s ?? "").Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (x.Trim().Length > 0 && !r.Contains(x.Trim())) r.Add(x.Trim());
            return r;
        }

        static void CopyMedia(AppConfig from, AppConfig to)
        {
            to.MediaPanel = from.MediaPanel; to.MediaPanelMini = from.MediaPanelMini;
            to.MediaPanelMinVideoKB = from.MediaPanelMinVideoKB; to.MediaPanelMinAudioKB = from.MediaPanelMinAudioKB;
            to.MediaPanelExcludeSites = from.MediaPanelExcludeSites; to.MergeAudioVideo = from.MergeAudioVideo;
            AppConfig.CopyMediaExtras(from, to);   // opciones del panel añadidas en ConfigMedia.cs
        }

        // ------------------------------------------------------------------ Tipos de archivos
        void BuildFileTypes()
        {
            TabPage p = Page("types", "Tipos de archivos");
            OptUi.Header(p, "types", "Tipos de archivos descargados", true, W);
            OptUi.Lbl(p, "Comenzar a descargar los siguientes tipos de archivo automáticamente:", 8, 40, W - 8);
            _exts = OptUi.Add(p, new TextBox(), 8, 58, W - 8, 62); _exts.Multiline = true; _exts.ScrollBars = ScrollBars.Vertical;
            _exts.Text = _cfg.CaptureExts;
            Button def = OptUi.Btn(p, "Predeterminado", W - 97, 124, 97);
            def.Click += delegate { _exts.Text = AppConfig.DefaultCaptureExts; };

            // Las excepciones con ruta ("sitio/carpeta/*") van en "Editar lista..."; aquí solo los sitios.
            List<string> sites = new List<string>();
            _excludeAtOpen = _cfg.CaptureExcludeSites ?? "";
            _askOnAutoCancel = _askAtOpen = _cfg.AskOnAutoCaptureCancel;
            foreach (string s in _excludeAtOpen.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                (IsAddress(s) ? _addresses : sites).Add(s);
            OptUi.Lbl(p, "No iniciar automáticamente la descarga desde los siguientes sitios:", 8, 158, W - 8);
            _sites = OptUi.Add(p, new TextBox(), 8, 176, W - 8, 48); _sites.Multiline = true; _sites.ScrollBars = ScrollBars.Vertical;
            _sites.Text = string.Join(" ", sites.ToArray());
            OptUi.Lbl(p, "(separar nombres con espacios)", 8, 232, 250);
            Button defSites = OptUi.Btn(p, "Predeterminado", W - 97, 228, 97);
            defSites.Click += delegate
            {
                List<string> d = new List<string>();
                foreach (string s in AppConfig.DefaultCaptureExclude.Split(' ')) if (!IsAddress(s)) d.Add(s);
                _sites.Text = string.Join(" ", d.ToArray());
            };
            OptUi.Lbl(p, "No iniciar la descarga automáticamente desde las siguientes direcciones:", 8, 266, W - 8);
            Button list = OptUi.Btn(p, "Editar lista ...", 20, 286, 110);
            list.Click += delegate
            {
                using (AddressExceptionsForm f = new AddressExceptionsForm(_addresses, _askOnAutoCancel))
                    if (f.ShowDialog(this) == DialogResult.OK) { _addresses = f.Result; _askOnAutoCancel = f.AskOnCancel; }
            };
            OptUi.Note(p, "Se admite el comodín * (p. ej. r0*, *.ejemplo.com). La extensión del navegador de IDMClone usa estas listas.", 8, 324, W - 8, 30);
        }

        static bool IsAddress(string s) { return s.IndexOf('/') >= 0; }

        // ------------------------------------------------------------------ Guardar
        void BuildSaveTo()
        {
            TabPage p = Page("save", "Guardar");
            OptUi.Header(p, "save", "Categorías, tipos de archivos, carpetas", true, W);
            GroupBox g = OptUi.Group(p, "Guardar en...", 8, 40, W - 8, 196);
            int gw = g.Width;
            OptUi.Lbl(g, "Categoría", 10, 24, 120);
            Button nw = OptUi.Btn(g, "Nuevo", gw - 95, 16, 85);
            nw.Click += delegate { EditCategory(null); };
            _saveCat = OptUi.Add(g, new ComboBox(), 10, 42, gw - 115, 21); _saveCat.DropDownStyle = ComboBoxStyle.DropDownList;
            _catEdit = OptUi.Btn(g, "Editar", gw - 95, 42, 85);
            _catEdit.Click += delegate { string n = _saveCat.SelectedItem as string; if (n != null) EditCategory(_cfg.FindCategory(n)); };
            _catTypesLabel = OptUi.Lbl(g, "", 10, 70, gw - 20);
            _catTypes = OptUi.Box(g, "", 10, 88, gw - 20); _catTypes.ReadOnly = true;
            _folderLabel = OptUi.Lbl(g, "", 10, 114, gw - 20);
            _folder = OptUi.Box(g, "", 10, 132, gw - 115);
            Browse(g, _folder, gw - 95, 131);
            _catRemember = OptUi.Chk(g, "", 10, 158, gw - 20, 30);
            FillSaveCategories(null);
            _saveCat.SelectedIndexChanged += delegate { ShowSaveCategory(); };
            if (_saveCat.Items.Count > 0) _saveCat.SelectedIndex = 0;

            _fileDate = OptUi.Chk(p, "Establecer la fecha del archivo como la proporcionada por el servidor", 8, 242, W - 8);
            _fileDate.Checked = _cfg.SetFileDateFromServer;

            GroupBox t = OptUi.CheckGroup(p, out _useTemp, "Directorio temporal", 8, 264, W - 8, 138);
            _useTemp.Checked = _cfg.UseTempFolder;
            _temp = OptUi.Box(t, _cfg.TempFolderOrDefault, 10, 24, t.Width - 115);
            Button tb = Browse(t, _temp, t.Width - 95, 23);
            Label tn = OptUi.Note(t, "Las partes de cada archivo se guardan aquí durante la descarga y el archivo se mueve a su carpeta al terminar: así no aparece en su destino hasta que está completo. Si tienes varios discos, es más rápido que el directorio temporal y las carpetas de \"Guardar\" estén en discos distintos.", 10, 52, t.Width - 20, 80);
            tn.ForeColor = SystemColors.ControlText;
            EventHandler upd = delegate { OptUi.Enable(_useTemp.Checked, _temp, tb); };
            _useTemp.CheckedChanged += upd; upd(null, null);
        }

        void FillSaveCategories(string select)
        {
            KeepSaveCategory();
            _shownCat = null;
            if (!_saveCatFormatted)
            {
                // Los nombres guardados no cambian; se muestran con las mismas etiquetas que el árbol ("Comprimido", "Video").
                _saveCatFormatted = true;
                _saveCat.FormattingEnabled = true;
                _saveCat.Format += delegate(object s, ListControlConvertEventArgs e) { e.Value = SaveCatLabel(e.ListItem as string); };
            }
            _saveCat.Items.Clear();
            foreach (Category c in _cfg.Categories)
            {
                _saveCat.Items.Add(c.Name);
                if (!_catFolders.ContainsKey(c.Name)) _catFolders[c.Name] = c.IsGeneral ? _cfg.DefaultFolder : c.Folder;
                if (!_catRemembers.ContainsKey(c.Name)) _catRemembers[c.Name] = c.RememberLastPath;
            }
            if (select != null) _saveCat.SelectedItem = select;
        }

        // "Nuevo" / "Editar" de Guardar: la misma ventana que el menú del panel Categorías. Lo que se acepta en
        // ella se guarda en ese momento (como al usarla desde el panel); el resto de Opciones, al pulsar Aceptar.
        void EditCategory(Category original)
        {
            KeepSaveCategory();
            using (CategoryForm f = new CategoryForm(_cfg, original))
            {
                if (f.ShowDialog(this) != DialogResult.OK || f.Result == null) return;
                string oldName = original != null ? original.Name : null;
                MainForm main = Owner as MainForm;
                Category c;
                if (main != null) c = main.ApplyCategoryEdit(original, f.Result);
                else { c = _cfg.ApplyCategory(original, f.Result); _cfg.Save(); }
                if (oldName != null) { _catFolders.Remove(oldName); _catRemembers.Remove(oldName); }
                _catFolders[c.Name] = c.IsGeneral ? _cfg.DefaultFolder : c.Folder;
                _catRemembers[c.Name] = c.RememberLastPath;
                FillSaveCategories(c.Name);
            }
        }

        bool _saveCatFormatted;

        string SaveCatLabel(string name)
        {
            Category c = name != null ? _cfg.FindCategory(name) : null;
            return c != null ? Lang.T(MainForm.CategoryLabel(c)) : name;
        }

        void KeepSaveCategory()
        {
            if (_shownCat == null) return;
            _catFolders[_shownCat] = _folder.Text.Trim();
            _catRemembers[_shownCat] = _catRemember.Checked;
        }

        void ShowSaveCategory()
        {
            KeepSaveCategory();
            _shownCat = _saveCat.SelectedItem as string;
            if (_shownCat == null) return;
            Category c = _cfg.FindCategory(_shownCat);
            _catEdit.Enabled = c != null;
            // Con la misma etiqueta que el árbol; Lang.Apply traduce estos textos (patrones con %s) al volver a aplicarse.
            string label = SaveCatLabel(_shownCat);
            _catTypesLabel.Text = "Colocar en la categoría \"" + label + "\" los siguientes tipos de archivos:";
            _catTypes.Text = c == null ? "" : c.IsGeneral ? Lang.T("Los tipos de archivos que no están listados en cualquier otra categoría") : (c.Extensions ?? "");
            _folderLabel.Text = "Directorio predeterminado de descarga para la categoría \"" + label + "\"";
            _folder.Text = _catFolders[_shownCat];
            _catRemember.Text = "Cambiar la carpeta para la categoría \"" + label + "\" a la última seleccionada";
            _catRemember.Checked = _catRemembers[_shownCat];
        }

        // Para las pruebas.
        public void SetCategoryFolder(string category, string folder, bool remember)
        {
            _saveCat.SelectedItem = category;
            _folder.Text = folder; _catRemember.Checked = remember;
        }

        Button Browse(Control parent, TextBox target, int x, int y)
        {
            Button b = OptUi.Btn(parent, "Examinar", x, y, 85);
            b.Click += delegate
            {
                using (FolderBrowserDialog f = new FolderBrowserDialog())
                {
                    if (Directory.Exists(target.Text)) f.SelectedPath = target.Text;
                    if (f.ShowDialog(this) == DialogResult.OK) target.Text = f.SelectedPath;
                }
            };
            return b;
        }

        // ------------------------------------------------------------------ Descargas
        void BuildDownloads()
        {
            TabPage p = Page("downloads", "Descargas");
            OptUi.Header(p, "downloads", "Configuración predeterminada de descarga", true, W);
            OptUi.LblRight(p, "Personalizar ventana del \"Progreso de descarga\"", 8, 42, 290);
            Button prog = OptUi.Btn(p, "Editar...", 306, 38, 91);
            _progressMode = _cfg.ProgressDialogMode;
            _progLimit = _cfg.ProgressShowLimitTab; _progFinish = _cfg.ProgressShowFinishTab; _progHide = _cfg.ProgressShowHideTabsButton;
            prog.Click += delegate
            {
                using (ProgressWindowForm f = new ProgressWindowForm(_progressMode, _progLimit, _progFinish, _progHide))
                    if (f.ShowDialog(this) == DialogResult.OK) { _progressMode = f.Mode; _progLimit = f.LimitTab; _progFinish = f.FinishTab; _progHide = f.HideButton; }
            };
            OptUi.Line(p, 8, 64, W - 8);
            _infoDlg = OptUi.Chk(p, "Mostrar diálogo de inicio de descarga", 8, 70, W - 8);
            _infoDlg.Checked = _cfg.ShowFileInfoDialog;
            _queueOnly = OptUi.Chk(p, "No iniciar la descarga, sólo añadir archivos a la cola", 28, 88, W - 28);
            _queueOnly.Checked = _cfg.AddToQueueWithoutDialog;
            _completeDlg = OptUi.Chk(p, "Mostrar diálogo de descarga completa", 8, 106, W - 8);
            _completeDlg.Checked = _cfg.ShowCompleteDialog;
            OptUi.Lbl(p, "Nota: Esta configuración no está relacionada con el proceso de cola", 8, 124, W - 8);
            OptUi.Line(p, 8, 144, W - 8);
            _startWhileInfo = OptUi.Chk(p, "Iniciar descarga mientras se muestra la ventana \"Información del archivo a descargar\"", 8, 150, W - 8, 30);
            _startWhileInfo.Checked = _cfg.StartWhileInfoDialog;
            _queuePicker = OptUi.Chk(p, "Mostrar panel de selección de colas al pulsar \"Descargar más tarde\"", 8, 182, W - 8);
            _queuePicker.Checked = _cfg.ShowQueuePicker;
            _queuePickerBatch = OptUi.Chk(p, "Mostrar panel de selección de colas al añadir descargas por lotes", 8, 200, W - 8);
            _queuePickerBatch.Checked = _cfg.ShowQueuePickerBatch;
            _ignoreDate = OptUi.Chk(p, "Ignorar los cambios de fecha del archivo al continuar una descarga", 8, 218, W - 8);
            _ignoreDate.Checked = _cfg.IgnoreDateChangesOnResume;
            _confirmDelete = OptUi.Chk(p, "Pedir confirmación antes de eliminar descargas de la lista", 8, 236, W - 8);
            _confirmDelete.Checked = !_cfg.SkipDeleteConfirm;
            EventHandler info = delegate { _queueOnly.Enabled = !_infoDlg.Checked; _startWhileInfo.Enabled = _infoDlg.Checked; };
            _infoDlg.CheckedChanged += info; info(null, null);
            OptUi.Line(p, 8, 256, W - 8);
            OptUi.Lbl(p, "Si se añade un enlace de descarga duplicado:", 8, 262, W - 8);
            _duplicate = OptUi.Combo(p, 8, 280, W - 8, true,
                "Mostrar un cuadro de diálogo y preguntar qué hacer",
                "Añadir el duplicado con un nombre de archivo numerado",
                "Añadir el duplicado y sobrescribir el archivo existente",
                "Con archivo completo, mostrar descarga completada; si no, continuar");
            _duplicate.SelectedIndex = Math.Max(0, Array.IndexOf(DupIds, _cfg.DuplicateAction));
            OptUi.Line(p, 8, 306, W - 8);
            OptUi.Lbl(p, "Agente para descargas agregadas manualmente:", 8, 312, W - 8);
            _ua = OptUi.Box(p, _cfg.UserAgent, 8, 330, W - 8);
            OptUi.Line(p, 8, 356, W - 8);
            OptUi.LblRight(p, "Configuración de análisis de virus", 8, 368, 290);
            Button av = OptUi.Btn(p, "Editar...", 306, 364, 91);
            _avPath = _cfg.AntivirusPath; _avArgs = _cfg.AntivirusArgs;
            av.Click += delegate
            {
                using (VirusCheckForm f = new VirusCheckForm(_avPath, _avArgs))
                    if (f.ShowDialog(this) == DialogResult.OK) { _avPath = f.ExePath; _avArgs = f.Args; }
            };
        }

        // ------------------------------------------------------------------ Conexión
        void BuildConnection()
        {
            TabPage p = Page("connection", "Conexión");
            OptUi.Header(p, "connection", "Tipo de conexión", true, W);
            GroupBox g = OptUi.Group(p, "Número máximo de conexiones", 8, 40, W - 8, 262);
            int gw = g.Width;
            OptUi.LblRight(g, "Número máximo de conexiones", 10, 22, 220);
            _conn = OptUi.Combo(g, 238, 20, 80, false, "1", "2", "4", "8", "16", "24", "32");
            string cur = _cfg.MaxConnections.ToString();
            if (!_conn.Items.Contains(cur)) _conn.Items.Add(cur);
            _conn.SelectedItem = cur;
            OptUi.LblRight(g, "Reintentos si falla una conexión", 10, 47, 220);
            _retries = OptUi.Num(g, 0, 1000, _cfg.RetryCount, 238, 45, 80);
            OptUi.Line(g, 10, 72, gw - 20);
            OptUi.Lbl(g, "Excepciones:", 10, 78, 200);
            _limits = OptUi.Add(g, new ListView(), 10, 96, gw - 110, 156);
            _limits.View = View.Details; _limits.FullRowSelect = true; _limits.GridLines = true; _limits.HideSelection = false; _limits.MultiSelect = false;
            _limits.Columns.Add("Servidor", Dpi.Px(200)); _limits.Columns.Add("Número", Dpi.Px(64));
            foreach (ServerLimit l in _cfg.ServerLimits) AddLimitRow(l.Host, l.MaxConnections);
            Button nw = OptUi.Btn(g, "Nuevo", gw - 92, 96, 82);
            Button del = OptUi.Btn(g, "Eliminar", gw - 92, 126, 82);
            Button ed = OptUi.Btn(g, "Editar", gw - 92, 156, 82);
            nw.Click += delegate { EditLimit(null); };
            ed.Click += delegate { if (_limits.SelectedItems.Count > 0) EditLimit(_limits.SelectedItems[0]); };
            _limits.DoubleClick += delegate { if (_limits.SelectedItems.Count > 0) EditLimit(_limits.SelectedItems[0]); };
            del.Click += delegate { if (_limits.SelectedItems.Count > 0) _limits.Items.Remove(_limits.SelectedItems[0]); };
            EventHandler sel = delegate { OptUi.Enable(_limits.SelectedItems.Count > 0, del, ed); };
            _limits.SelectedIndexChanged += sel; sel(null, null);

            GroupBox q = OptUi.CheckGroup(p, out _quota, "Límites de descarga", 8, 310, W - 8, 92);
            _quota.Checked = _cfg.QuotaEnabled;
            Label l1 = OptUi.LblRight(q, "No descargar más de", 10, 22, 130);
            _quotaMB = OptUi.Num(q, 1, 1000000, _cfg.QuotaMB, 146, 20, 70);
            Label l2 = OptUi.Lbl(q, "MBytes", 222, 22, 80);
            Label l3 = OptUi.LblRight(q, "cada", 10, 46, 130);
            _quotaHours = OptUi.Num(q, 1, 168, _cfg.QuotaHours, 146, 44, 70);
            Label l4 = OptUi.Lbl(q, "Horas", 222, 46, 80);
            _quotaWarn = OptUi.Chk(q, "Mostrar advertencia antes de detener descargas", 10, 68, q.Width - 20);
            _quotaWarn.Checked = _cfg.QuotaWarning;
            EventHandler qe = delegate { OptUi.Enable(_quota.Checked, l1, _quotaMB, l2, l3, _quotaHours, l4, _quotaWarn); };
            _quota.CheckedChanged += qe; qe(null, null);
        }

        void AddLimitRow(string host, int n)
        {
            ListViewItem li = new ListViewItem(host); li.SubItems.Add(n.ToString()); _limits.Items.Add(li);
        }

        void EditLimit(ListViewItem li)
        {
            int n; int.TryParse(li != null ? li.SubItems[1].Text : "", out n);
            using (ServerLimitForm f = new ServerLimitForm(li != null ? li.Text : "", li != null ? n : 2))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                if (li == null) AddLimitRow(f.Host, f.Connections);
                else { li.Text = f.Host; li.SubItems[1].Text = f.Connections.ToString(); }
            }
        }

        // ------------------------------------------------------------------ Proxy/Socks
        void BuildProxy()
        {
            TabPage p = Page("proxy", "Proxy/Socks");
            OptUi.Header(p, "proxy", "Configuración de Proxy/Socks", true, W, 276);
            Button fromSys = OptUi.Btn(p, "Obtener del sistema", 282, 4, W - 282);
            _pxBrowser = OptUi.Chk(p, "Usar proxy/socks desde la petición de un navegador si hay errores de descarga para las descargas interceptadas desde el navegador.", 8, 37, W - 8, 30);
            _pxBrowser.Checked = _cfg.UseBrowserProxyOnError;
            OptUi.Line(p, 8, 68, W - 8);
            _pxNone = OptUi.Radio(p, "No hay proxy/socks", 8, 74, W - 8);
            OptUi.Line(p, 8, 96, W - 8);
            _pxSystem = OptUi.Radio(p, "Usar configuración del sistema", 8, 102, W - 8);
            OptUi.Line(p, 8, 124, W - 8);
            _pxPac = OptUi.Radio(p, "Usar scripts de configuración automáticamente", 8, 130, W - 8);
            Label pacL = OptUi.Lbl(p, "Dirección", 26, 154, 62);
            _pacUrl = OptUi.Box(p, _cfg.PacUrl, 92, 152, W - 92);
            OptUi.Line(p, 8, 180, W - 8);
            _pxManual = OptUi.Radio(p, "Configuración manual de proxy/socks", 8, 186, W - 8);
            Label hl = OptUi.Lbl(p, "Proxy", 26, 206, 120);
            Label pl = OptUi.Lbl(p, "Puerto", 172, 206, 52);
            Label ul = OptUi.Lbl(p, "Usuario", 230, 206, 80);
            Label wl = OptUi.Lbl(p, "Contraseña", 316, 206, 81);
            _pxHost = OptUi.Box(p, _cfg.ProxyHost, 26, 224, 140);
            _pxPort = OptUi.Box(p, _cfg.ProxyPort.ToString(), 172, 224, 52);
            _pxUser = OptUi.Box(p, _cfg.ProxyUser, 230, 224, 80);
            _pxPass = OptUi.Box(p, Util.Unprotect(_cfg.ProxyPasswordEnc) ?? "", 316, 224, W - 316); _pxPass.UseSystemPasswordChar = true;
            Label protoL = OptUi.Lbl(p, "Usar este proxy para los siguientes protocolos:", 26, 252, W - 26);
            _pxHttp = OptUi.Chk(p, "http", 26, 272, 56); _pxHttp.Checked = _cfg.ProxyHttp; _pxHttp.Tag = Lang.Keep;
            _pxHttps = OptUi.Chk(p, "https", 90, 272, 62); _pxHttps.Checked = _cfg.ProxyHttps; _pxHttps.Tag = Lang.Keep;
            _pxFtp = OptUi.Chk(p, "ftp", 160, 272, 50); _pxFtp.Checked = _cfg.ProxyFtp; _pxFtp.Tag = Lang.Keep;
            Button adv = OptUi.Btn(p, "Avanzado... / Socks...", 236, 270, W - 236);
            _pxBypass = _cfg.ProxyBypass ?? "";
            _pxS = new ProxyServerFields(true, _cfg.ProxyHttpsHost, _cfg.ProxyHttpsPort, _cfg.ProxyHttpsUser, Util.Unprotect(_cfg.ProxyHttpsPasswordEnc));
            _pxF = new ProxyServerFields(true, _cfg.ProxyFtpHost, _cfg.ProxyFtpPort, _cfg.ProxyFtpUser, Util.Unprotect(_cfg.ProxyFtpPasswordEnc));
            _sxOn = _cfg.SocksEnabled; _sxType = _cfg.SocksType; _sxHost = _cfg.SocksHost; _sxPort = _cfg.SocksPort; _sxDns = _cfg.SocksProxyDns;
            _sxUser = _cfg.SocksUser ?? ""; _sxPass = Util.Unprotect(_cfg.SocksPasswordEnc) ?? "";
            adv.Click += delegate { Advanced(); };
            OptUi.Line(p, 8, 300, W - 8);
            OptUi.Note(p, "Cada protocolo marcado usa su proxy (el de arriba o el suyo de \"Avanzado...\"); los demás, el SOCKS si está activo o van directos. " +
                          "El FTP no pasa por SOCKS y ftps:// va siempre directo; si el modo FTP (PASV o activo) falla, se prueba el otro. " +
                          "La casilla de arriba solo sirve con un proxy HTTP sin contraseña indicado por el navegador (Firefox; Chromium no lo indica).", 8, 306, W - 8, 72);
            _pasv = OptUi.Chk(p, "usar FTP en modo PASV", 8, 384, W - 8); _pasv.Checked = _cfg.FtpPassive;
            switch (_cfg.ProxyMode)
            {
                case "none": _pxNone.Checked = true; break;
                case "manual": _pxManual.Checked = true; break;
                case "pac": _pxPac.Checked = true; break;
                default: _pxSystem.Checked = true; break;
            }
            EventHandler upd = delegate
            {
                OptUi.Enable(_pxPac.Checked, pacL, _pacUrl);
                OptUi.Enable(_pxManual.Checked, hl, pl, ul, wl, _pxHost, _pxPort, _pxUser, _pxPass, protoL, _pxHttp, _pxHttps, _pxFtp, adv);
            };
            foreach (RadioButton r in new RadioButton[] { _pxNone, _pxSystem, _pxPac, _pxManual }) r.CheckedChanged += upd;
            upd(null, null);
            fromSys.Click += delegate { FromSystem(); };
        }

        // "Obtener del sistema": copia el proxy configurado en Windows a la configuración manual.
        void FromSystem()
        {
            string server, bypass;
            string pac = ReadWindowsProxy(out server, out bypass);
            string http, https, ftp, socks;
            ParseWindowsProxy(server, out http, out https, out ftp, out socks);
            if (http != null || https != null || ftp != null || socks != null)
            {
                string h; int port;
                // La página enseña el de http (o el primero que haya); https y ftp guardan el suyo si es otro.
                string main = http ?? https ?? ftp;
                if (main != null && SplitHostPort(main, out h, out port)) { _pxHost.Text = h; if (port > 0) _pxPort.Text = port.ToString(); }
                _pxHttp.Checked = http != null; _pxHttps.Checked = https != null; _pxFtp.Checked = ftp != null;
                _pxS = SystemFields(https, main); _pxF = SystemFields(ftp, main);
                // "socks=" de Windows: WinINet lo usa como SOCKS4 (los servidores SOCKS suelen admitir 4, 4a y 5).
                if (socks != null && SplitHostPort(socks, out h, out port)) { _sxOn = true; _sxType = "4"; _sxHost = h; _sxPort = port > 0 ? port : 1080; }
                else _sxOn = false;
                if (!string.IsNullOrEmpty(bypass)) _pxBypass = bypass.Trim();
                _pxManual.Checked = true;
            }
            else if (!string.IsNullOrEmpty(pac)) { _pacUrl.Text = pac; _pxPac.Checked = true; }
            else Msg.Show(this, "Windows no tiene configurado ningún proxy.", "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // Proxy de https o ftp de "Obtener del sistema": vacío (= el de la página) si es el mismo o no hay.
        static ProxyServerFields SystemFields(string value, string main)
        {
            string h; int port;
            if (value == null || value == main || !SplitHostPort(value, out h, out port)) return new ProxyServerFields(true, "", 8080, "", "");
            return new ProxyServerFields(true, h, port > 0 ? port : 8080, "", "");
        }

        // "Avanzado... / Socks...": los tres proxys (la página es el de http), el SOCKS y las excepciones.
        void Advanced()
        {
            int mainPort;
            if (!int.TryParse(_pxPort.Text.Trim(), out mainPort) || mainPort < 1 || mainPort > 65535) mainPort = 8080;
            ProxyServerFields main = new ProxyServerFields(_pxHttp.Checked, _pxHost.Text, mainPort, _pxUser.Text.Trim(), _pxPass.Text);
            ProxyServerFields https = _pxS.Host.Length > 0 ? _pxS : main, ftp = _pxF.Host.Length > 0 ? _pxF : main;
            using (ProxyAdvancedForm f = new ProxyAdvancedForm(_pxBypass, main,
                       new ProxyServerFields(_pxHttps.Checked, https.Host, https.Port, https.User, https.Password),
                       new ProxyServerFields(_pxFtp.Checked, ftp.Host, ftp.Port, ftp.User, ftp.Password),
                       _sxOn, _sxType, _sxDns, _sxHost, _sxPort, _sxUser, _sxPass))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                ApplyAdvanced(f.Bypass, f.Http, f.Https, f.Ftp);
                _sxOn = f.SocksEnabled; _sxType = f.SocksType; _sxDns = f.SocksProxyDns; _sxHost = f.SocksHost; _sxPort = f.SocksPort; _sxUser = f.SocksUser; _sxPass = f.SocksPassword;
            }
        }

        // Lo de "Avanzado...": el de http pasa a la página; https y ftp guardan su servidor solo si es otro.
        internal void ApplyAdvanced(string bypass, ProxyServerFields http, ProxyServerFields https, ProxyServerFields ftp)
        {
            _pxBypass = bypass ?? "";
            _pxHttp.Checked = http.On; _pxHttps.Checked = https.On; _pxFtp.Checked = ftp.On;
            ProxyServerFields main = http;
            if (main.Host.Length == 0) main = https.Host.Length > 0 ? https : ftp;   // solo hay servidor en https o en ftp
            _pxHost.Text = main.Host; _pxPort.Text = main.Port.ToString(); _pxUser.Text = main.User; _pxPass.Text = main.Password;
            _pxS = https.Host.Length == 0 || https.SameServer(main) ? new ProxyServerFields(true, "", 8080, "", "") : https;
            _pxF = ftp.Host.Length == 0 || ftp.SameServer(main) ? new ProxyServerFields(true, "", 8080, "", "") : ftp;
        }

        // Proxy de Windows (Internet Settings): ProxyServer y ProxyOverride si está activo; devuelve el script PAC.
        internal static string ReadWindowsProxy(out string server, out string bypass)
        {
            server = null; bypass = null;
            string pac = null;
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (k != null)
                    {
                        object en = k.GetValue("ProxyEnable");
                        if (en is int && (int)en != 0) { server = k.GetValue("ProxyServer") as string; bypass = k.GetValue("ProxyOverride") as string; }
                        pac = k.GetValue("AutoConfigURL") as string;
                    }
                }
            }
            catch { }
            return pac;
        }

        // ProxyServer de Windows: "host:puerto" (todos los protocolos) o "http=h:p;https=h:p;ftp=h:p;socks=h:p".
        // http: el proxy HTTP (o el de https si no hay de http); socks: el servidor SOCKS. null si no hay.
        internal static void ParseWindowsProxy(string server, out string http, out string socks)
        {
            string https, ftp;
            ParseWindowsProxy(server, out http, out https, out ftp, out socks);
            if (http == null) http = https;
        }

        // Lo mismo por protocolo: "host:puerto" vale para http, https y ftp (como en Windows); null si no hay.
        internal static void ParseWindowsProxy(string server, out string http, out string https, out string ftp, out string socks)
        {
            http = null; https = null; ftp = null; socks = null;
            if (string.IsNullOrEmpty(server)) return;
            string all = null;
            foreach (string raw in server.Split(';'))
            {
                string part = raw.Trim();
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                if (eq < 0) { if (all == null) all = part; continue; }
                string key = part.Substring(0, eq).Trim().ToLowerInvariant(), val = part.Substring(eq + 1).Trim();
                if (val.Length == 0) continue;
                if (key == "http") http = val;
                else if (key == "https") https = val;
                else if (key == "ftp") ftp = val;
                else if (key == "socks") socks = val;
            }
            if (all != null) { if (http == null) http = all; if (https == null) https = all; if (ftp == null) ftp = all; }
        }

        internal static bool SplitHostPort(string s, out string host, out int port)
        {
            port = 0;
            s = s.Trim();
            int sch = s.IndexOf("://", StringComparison.Ordinal);
            if (sch >= 0) s = s.Substring(sch + 3);
            s = s.TrimEnd('/');
            int c = s.LastIndexOf(':');
            if (c > 0 && s.IndexOf(']') < c && int.TryParse(s.Substring(c + 1), out port)) host = s.Substring(0, c);
            else { host = s; port = 0; }
            return host.Length > 0;
        }

        // ------------------------------------------------------------------ Sesiones de sitios
        class LoginRow { public string Site, User, Pass; }

        void BuildLogins()
        {
            TabPage p = Page("logins", "Sesiones de sitios");
            OptUi.Header(p, "logins", "usuarios y contraseñas para servidores/sitios", true, W);
            _logins = OptUi.Add(p, new ListView(), 8, 40, W - 8, 296);
            _logins.View = View.Details; _logins.FullRowSelect = true; _logins.HideSelection = false; _logins.MultiSelect = false;
            _logins.Columns.Add("Sitio/Ruta", Dpi.Px(220)); _logins.Columns.Add("Usuario", Dpi.Px(80)); _logins.Columns.Add("Contraseña", Dpi.Px(70));
            foreach (SiteLogin s in _cfg.SiteLogins) { LoginRow r = new LoginRow(); r.Site = s.Site; r.User = s.User; r.Pass = s.Password; AddLogin(r); }
            Button nw = OptUi.Btn(p, "Nuevo", 20, 344);
            Button ed = OptUi.Btn(p, "Editar", 110, 344);
            Button rm = OptUi.Btn(p, "Quitar", 200, 344);
            OptUi.Note(p, "Las contraseñas se guardan cifradas para tu usuario de Windows.", 8, 376, W - 8, 16);
            nw.Click += delegate { EditLogin(null); };
            ed.Click += delegate { if (_logins.SelectedItems.Count > 0) EditLogin(_logins.SelectedItems[0]); };
            _logins.DoubleClick += delegate { if (_logins.SelectedItems.Count > 0) EditLogin(_logins.SelectedItems[0]); };
            rm.Click += delegate { if (_logins.SelectedItems.Count > 0) _logins.Items.Remove(_logins.SelectedItems[0]); };
            EventHandler sel = delegate { OptUi.Enable(_logins.SelectedItems.Count > 0, ed, rm); };
            _logins.SelectedIndexChanged += sel; sel(null, null);
        }

        void AddLogin(LoginRow r)
        {
            ListViewItem li = new ListViewItem(r.Site); li.SubItems.Add(r.User ?? ""); li.SubItems.Add(new string('●', (r.Pass ?? "").Length)); li.Tag = r;
            _logins.Items.Add(li);
        }

        void EditLogin(ListViewItem li)
        {
            LoginRow r = li != null ? (LoginRow)li.Tag : new LoginRow();
            using (SiteLoginForm f = new SiteLoginForm(r.Site, r.User, r.Pass))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                r.Site = f.SitePath; r.User = f.User; r.Pass = f.Password;
                if (li == null) AddLogin(r);
                else { li.Text = r.Site; li.SubItems[1].Text = r.User; li.SubItems[2].Text = new string('●', r.Pass.Length); }
            }
        }

        // ------------------------------------------------------------------ Discado / VPN
        // IDMClone usa la conexión que ya tenga Windows: la página se muestra como en IDM, pero desactivada.
        void BuildDialUp()
        {
            TabPage p = Page("dialup", "Discado / VPN");
            OptUi.Header(p, "dialup", "Configuración de discado / Configuración VPN", true, W);
            CheckBox use = OptUi.Chk(p, "Usar la red de discado de Windows", 8, 40, W - 8); use.Enabled = false;
            GroupBox g = OptUi.Group(p, "Opciones de conexión", 8, 62, W - 8, 148);
            OptUi.LblRight(g, "Conexión:", 10, 24, 100);
            OptUi.Combo(g, 116, 22, g.Width - 136, false);
            OptUi.LblRight(g, "Usuario", 10, 54, 100);
            OptUi.Box(g, "", 116, 52, 170);
            OptUi.LblRight(g, "Contraseña", 10, 82, 100);
            OptUi.Box(g, "", 116, 80, 170).UseSystemPasswordChar = true;
            OptUi.Chk(g, "Guardar contraseña", 136, 108, 150);
            OptUi.Btn(g, "Aplicar", g.Width - 95, 112, 85);
            g.Enabled = false;
            GroupBox r = OptUi.Group(p, "Opciones de remarcado", 8, 218, W - 8, 78);
            OptUi.LblRight(r, "Intentos de remarcado (cero para infinito):", 10, 22, 236);
            OptUi.Num(r, 0, 100, 0, 252, 20, 46);
            OptUi.Lbl(r, "veces", 304, 22, 70);
            OptUi.LblRight(r, "Intervalo entre intentos de remarcado:", 10, 48, 236);
            OptUi.Num(r, 1, 600, 30, 252, 46, 46);
            OptUi.Lbl(r, "Segundos", 304, 48, 70);
            r.Enabled = false;
            OptUi.Note(p, "IDMClone usa la conexión a Internet que ya tenga Windows: no marca ni cuelga conexiones de acceso telefónico ni VPN, así que esta página no tiene opciones que cambiar. " +
                          "Si usas una VPN, conéctala antes de descargar (tanto las VPN de Windows como las de los programas de VPN valen para todo el equipo) o, si te da un proxy o un servidor SOCKS, configúralo en la página Proxy/Socks.", 8, 306, W - 8, 86);
        }

        // ------------------------------------------------------------------ Sonidos
        void BuildSounds()
        {
            TabPage p = Page("sounds", "Sonidos");
            OptUi.Header(p, "sounds", "Configuración de sonido", true, W);
            GroupBox g = OptUi.Group(p, "Seleccionar sonidos para los eventos de IDMClone", 8, 40, W - 8, 300);
            _sounds = OptUi.Add(g, new ListView(), 10, 20, g.Width - 20, 236);
            _sounds.Tag = Lang.Mark;
            _sounds.View = View.Details; _sounds.CheckBoxes = true; _sounds.FullRowSelect = true; _sounds.HideSelection = false; _sounds.MultiSelect = false;
            _sounds.Columns.Add("Evento", Dpi.Px(180)); _sounds.Columns.Add("archivo de sonido", Dpi.Px(300));
            foreach (SoundSetting ss in _cfg.Sounds)
            {
                ListViewItem li = new ListViewItem(SoundSetting.Title(ss.Event)); li.SubItems.Add(ss.File ?? ""); li.Checked = ss.Enabled; li.Tag = ss.Event;
                _sounds.Items.Add(li);
            }
            Button browse = OptUi.Btn(g, "Examinar...", 84, 264, 85);
            Button play = OptUi.Btn(g, "Reproducir", 210, 264, 85);
            EventHandler sel = delegate { OptUi.Enable(_sounds.SelectedItems.Count > 0, browse, play); };
            _sounds.SelectedIndexChanged += sel; sel(null, null);
            browse.Click += delegate
            {
                if (_sounds.SelectedItems.Count == 0) return;
                ListViewItem li = _sounds.SelectedItems[0];
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Filter = "Archivos de sonido (*.wav)|*.wav";
                    try { d.InitialDirectory = Path.GetDirectoryName(li.SubItems[1].Text); } catch { }
                    if (d.ShowDialog(this) == DialogResult.OK) { li.SubItems[1].Text = d.FileName; li.Checked = true; }
                }
            };
            play.Click += delegate
            {
                if (_sounds.SelectedItems.Count == 0) return;
                string f = _sounds.SelectedItems[0].SubItems[1].Text;
                try { if (File.Exists(f)) new System.Media.SoundPlayer(f).Play(); } catch { }
            };
            OptUi.Note(p, "Marca los eventos que deben sonar. Se usan archivos .wav (por defecto, los sonidos de Windows).", 8, 348, W - 8, 30);
        }

        // ------------------------------------------------------------------ Aceptar

        // Lista separada por espacios, sin duplicados (las extensiones, en mayúsculas como en IDM).
        static string NormalizeList(string text, bool upper)
        {
            List<string> r = new List<string>();
            foreach (string t in text.Split(new char[] { ' ', '\r', '\n', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string v = upper ? t.TrimStart('.').ToUpperInvariant() : t;
                if (v.Length > 0 && !r.Contains(v)) r.Add(v);
            }
            return string.Join(" ", r.ToArray());
        }

        void Apply()
        {
            // General
            _cfg.LaunchOnStart = _startup.Checked;
            _cfg.MonitorClipboard = _clipboard.Checked;
            List<string> off = new List<string>(), extra = new List<string>();
            for (int i = 0; i < _browsers.Items.Count; i++)
            {
                string b = (string)_browsers.Items[i];
                if (!_browsers.GetItemChecked(i)) off.Add(b);
                if (Array.IndexOf(KnownBrowsers, b) < 0) extra.Add(b);
            }
            _cfg.CaptureBrowsersOff = string.Join(";", off.ToArray());
            _cfg.CaptureBrowsersExtra = string.Join(";", extra.ToArray());
            _cfg.UseKeyToPrevent = _usePrevent; _cfg.PreventKeys = _preventKeys;
            _cfg.UseKeyToForce = _useForce; _cfg.ForceKeys = _forceKeys;
            _cfg.BrowserMenuDownload = _menuLink; _cfg.BrowserMenuDownloadAll = _menuAll;
            _cfg.FirefoxMenuDownload = _ffLink; _cfg.FirefoxMenuDownloadAll = _ffAll;
            _media.MediaPanel = _mediaPanel.Checked;
            CopyMedia(_media, _cfg);
            // Tipos de archivos
            _cfg.CaptureExts = NormalizeList(_exts.Text, true);
            string sites = NormalizeList(_sites.Text, false), addrs = NormalizeList(string.Join(" ", _addresses.ToArray()), false);
            // Lo que añadió "Cancelando una descarga iniciada automáticamente" con esta ventana abierta no se pierde.
            List<string> added = new List<string>();
            foreach (string s in (_cfg.CaptureExcludeSites ?? "").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if ((" " + _excludeAtOpen + " ").IndexOf(" " + s + " ", StringComparison.OrdinalIgnoreCase) < 0) added.Add(s);
            _cfg.CaptureExcludeSites = (sites + " " + addrs).Trim();
            foreach (string s in added) _cfg.AddCaptureExclusion(s);
            // Solo si se cambió aquí: "No mostrar este diálogo otra vez" pudo marcarse con Opciones abierto.
            if (_askOnAutoCancel != _askAtOpen) _cfg.AskOnAutoCaptureCancel = _askOnAutoCancel;
            // Guardar
            KeepSaveCategory();
            foreach (Category c in _cfg.Categories)
            {
                string f;
                if (_catFolders.TryGetValue(c.Name, out f) && !string.IsNullOrEmpty(f)) c.Folder = f;
                bool r;
                if (_catRemembers.TryGetValue(c.Name, out r)) c.RememberLastPath = r;
                if (c.IsGeneral) _cfg.DefaultFolder = c.Folder;
            }
            _cfg.SetFileDateFromServer = _fileDate.Checked;
            _cfg.UseTempFolder = _useTemp.Checked;
            string t = _temp.Text.Trim();
            _cfg.TempFolder = string.IsNullOrEmpty(t) || string.Equals(t, Path.Combine(AppConfig.Dir, "DwnlData"), StringComparison.OrdinalIgnoreCase) ? null : t;
            // Descargas
            _cfg.ProgressDialogMode = _progressMode == "minimized" || _progressMode == "none" ? _progressMode : "normal";
            _cfg.ProgressShowLimitTab = _progLimit; _cfg.ProgressShowFinishTab = _progFinish; _cfg.ProgressShowHideTabsButton = _progHide;
            _cfg.ShowFileInfoDialog = _infoDlg.Checked;
            _cfg.AddToQueueWithoutDialog = _queueOnly.Checked;
            _cfg.ShowCompleteDialog = _completeDlg.Checked;
            _cfg.StartWhileInfoDialog = _startWhileInfo.Checked;
            _cfg.ShowQueuePicker = _queuePicker.Checked;
            _cfg.ShowQueuePickerBatch = _queuePickerBatch.Checked;
            _cfg.IgnoreDateChangesOnResume = _ignoreDate.Checked;
            _cfg.SkipDeleteConfirm = !_confirmDelete.Checked;
            _cfg.DuplicateAction = DupIds[Math.Max(0, _duplicate.SelectedIndex)];
            if (_ua.Text.Trim().Length > 0) _cfg.UserAgent = _ua.Text.Trim();
            _cfg.AntivirusPath = string.IsNullOrEmpty(_avPath) ? null : _avPath;
            _cfg.AntivirusArgs = string.IsNullOrEmpty(_avPath) ? null : _avArgs;
            // Conexión
            int conn;
            if (int.TryParse(_conn.SelectedItem as string, out conn)) _cfg.MaxConnections = Math.Max(1, Math.Min(32, conn));
            _cfg.RetryCount = (int)_retries.Value;
            _cfg.ServerLimits.Clear();
            foreach (ListViewItem li in _limits.Items)
            {
                int n;
                if (li.Text.Trim().Length > 0 && int.TryParse(li.SubItems[1].Text, out n) && n > 0)
                    _cfg.ServerLimits.Add(new ServerLimit { Host = li.Text.Trim(), MaxConnections = Math.Min(32, n) });
            }
            _cfg.QuotaEnabled = _quota.Checked; _cfg.QuotaMB = (int)_quotaMB.Value; _cfg.QuotaHours = (int)_quotaHours.Value;
            _cfg.QuotaWarning = _quotaWarn.Checked;
            // Proxy/Socks
            _cfg.ProxyMode = _pxNone.Checked ? "none" : _pxManual.Checked ? "manual" : _pxPac.Checked ? "pac" : "system";
            _cfg.ProxyHost = _pxHost.Text.Trim();
            int port;
            if (int.TryParse(_pxPort.Text.Trim(), out port) && port > 0 && port <= 65535) _cfg.ProxyPort = port;
            _cfg.ProxyUser = _pxUser.Text.Trim(); _cfg.ProxyPasswordEnc = Util.Protect(_pxPass.Text);
            _cfg.ProxyBypass = (_pxBypass ?? "").Trim(); _cfg.PacUrl = _pacUrl.Text.Trim();
            _cfg.ProxyHttp = _pxHttp.Checked; _cfg.ProxyHttps = _pxHttps.Checked; _cfg.ProxyFtp = _pxFtp.Checked;
            _cfg.ProxyHttpsHost = _pxS.Host; _cfg.ProxyHttpsPort = _pxS.Port; _cfg.ProxyHttpsUser = _pxS.User.Length > 0 ? _pxS.User : null;
            _cfg.ProxyHttpsPasswordEnc = _pxS.Password.Length > 0 ? Util.Protect(_pxS.Password) : null;
            _cfg.ProxyFtpHost = _pxF.Host; _cfg.ProxyFtpPort = _pxF.Port; _cfg.ProxyFtpUser = _pxF.User.Length > 0 ? _pxF.User : null;
            _cfg.ProxyFtpPasswordEnc = _pxF.Password.Length > 0 ? Util.Protect(_pxF.Password) : null;
            _cfg.SocksEnabled = _sxOn; _cfg.SocksType = _sxType; _cfg.SocksHost = _sxHost; _cfg.SocksPort = _sxPort; _cfg.SocksProxyDns = _sxDns;
            _cfg.SocksUser = string.IsNullOrEmpty(_sxUser) ? null : _sxUser;
            _cfg.SocksPasswordEnc = string.IsNullOrEmpty(_sxPass) ? null : Util.Protect(_sxPass);
            _cfg.UseBrowserProxyOnError = _pxBrowser.Checked;
            _cfg.FtpPassive = _pasv.Checked;
            // Sesiones de sitios
            _cfg.SiteLogins.Clear();
            foreach (ListViewItem li in _logins.Items)
            {
                LoginRow r = (LoginRow)li.Tag;
                if (string.IsNullOrEmpty(r.Site)) continue;
                SiteLogin s = new SiteLogin(); s.Site = r.Site; s.User = r.User ?? ""; s.Password = r.Pass ?? "";
                _cfg.SiteLogins.Add(s);
            }
            // Sonidos
            foreach (ListViewItem li in _sounds.Items)
            {
                SoundSetting ss = _cfg.SoundFor((string)li.Tag);
                if (ss != null) { ss.Enabled = li.Checked; ss.File = li.SubItems[1].Text.Trim(); }
            }
            _cfg.Save();
            try { Startup.Set(_startup.Checked, Application.ExecutablePath); }
            catch (Exception ex) { Msg.Show(this, "No se pudo cambiar el inicio con Windows: " + ex.Message, "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            // Solo si se ha cambiado la casilla: así no se toca el registro al aceptar sin más.
            if (_explorerMenu.Checked != _explorerMenuWas)
            {
                try { ShellMenu.Set(_explorerMenu.Checked, Application.ExecutablePath); _explorerMenuWas = _explorerMenu.Checked; }
                catch (Exception ex) { Msg.Show(this, "No se pudo cambiar el menú contextual del Explorador: " + ex.Message, "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }
    }
}
