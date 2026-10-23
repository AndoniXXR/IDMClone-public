using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace IDMClone
{
    // Abrir, "Abrir con..." y "Abrir carpeta" (menú contextual y diálogo de descarga completada).
    public static class Shell
    {
        public static void Open(IWin32Window owner, string path)
        {
            try
            {
                if (!File.Exists(path)) { Msg.Show(owner, "El archivo no existe:\n" + path, "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                Process.Start(path);
            }
            catch (Exception ex) { Msg.Show(owner, ex.Message, "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        public static void OpenWith(IWin32Window owner, string path)
        {
            try
            {
                if (!File.Exists(path)) { Msg.Show(owner, "El archivo no existe:\n" + path, "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                Process.Start("rundll32.exe", "shell32.dll,OpenAs_RunDLL " + path);
            }
            catch (Exception ex) { Msg.Show(owner, ex.Message, "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        public static void OpenFolder(string path)
        {
            try
            {
                if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else if (Directory.Exists(Path.GetDirectoryName(path))) Process.Start(Path.GetDirectoryName(path));
            }
            catch { }
        }
    }

    // Piezas comunes de las ventanas de descarga, con las medidas de IDM (Tahoma 8): etiquetas de 15 px,
    // cajas de 20 px y botones de 23 px de alto.
    static class DownloadDlgUi
    {
        public static Label Lbl(Control parent, string text, int x, int y, int w)
        {
            Label l = new Label(); l.Text = text; l.AutoSize = false; l.TextAlign = ContentAlignment.MiddleLeft;
            l.SetBounds(x, y, w, 15); parent.Controls.Add(l); return l;
        }

        // Etiqueta alineada a la derecha (columna de etiquetas de "Información del archivo", "Usuario"...).
        public static Label LblRight(Control parent, string text, int x, int y, int w)
        {
            Label l = Lbl(parent, text, x, y, w); l.TextAlign = ContentAlignment.MiddleRight; return l;
        }

        // Dato (ruta, dirección, tamaño...): no se traduce y puede cortarse con "...".
        public static Label Data(Control parent, string text, int x, int y, int w)
        {
            Label l = Lbl(parent, text, x, y, w); l.Tag = Lang.Keep; l.AutoEllipsis = true; l.UseMnemonic = false; return l;
        }

        public static Button Btn(Control parent, string text, int x, int y, int w, DialogResult r)
        {
            Button b = new Button(); b.Text = text; b.SetBounds(x, y, w, 23); b.DialogResult = r; b.UseVisualStyleBackColor = true;
            parent.Controls.Add(b); return b;
        }

        public static TextBox Box(Control parent, int x, int y, int w)
        {
            TextBox t = new TextBox(); t.SetBounds(x, y, w, 20); parent.Controls.Add(t); return t;
        }

        // Caja de solo lectura (gris, como las de IDM).
        public static TextBox ReadOnlyBox(Control parent, string text, int x, int y, int w)
        {
            TextBox t = Box(parent, x, y, w); t.ReadOnly = true; t.Text = text ?? ""; return t;
        }

        public static CheckBox Chk(Control parent, string text, int x, int y, int w, int h)
        {
            CheckBox c = new CheckBox(); c.Text = text; c.SetBounds(x, y, w, h); c.UseVisualStyleBackColor = true; parent.Controls.Add(c); return c;
        }

        public static RadioButton Radio(Control parent, string text, int x, int y, int w)
        {
            RadioButton r = new RadioButton(); r.Text = text; r.SetBounds(x, y, w, 17); r.UseVisualStyleBackColor = true; parent.Controls.Add(r); return r;
        }

        // Línea horizontal grabada (separadores de "Propiedades", "Columnas"...).
        public static Label Line(Control parent, int x, int y, int w)
        {
            Label l = new Label(); l.BorderStyle = BorderStyle.Fixed3D; l.AutoSize = false; l.SetBounds(x, y, w, 2); parent.Controls.Add(l); return l;
        }

        // Recuadro cuyo título es una casilla (IDM: "☑ Recordar esta ruta...", "☐ usar autorización").
        public static GroupBox CheckGroup(Control parent, CheckBox title, int x, int y, int w, int h)
        {
            GroupBox g = new GroupBox(); g.Text = ""; g.SetBounds(x, y, w, h); parent.Controls.Add(g);
            title.Location = new Point(7, 0); g.Controls.Add(title);
            FitCheck(title, w - 14);
            return g;
        }

        // Ancho justo para el texto de la casilla (para que el marco del recuadro siga a su derecha).
        public static void FitCheck(CheckBox c, int max)
        {
            int need = TextRenderer.MeasureText(c.Text, c.Font).Width + (int)Math.Ceiling(30 * c.Font.Size / 8.25f);
            c.Size = new Size(Math.Min(max, need), c.Height > 0 ? c.Height : 17);
        }

        // Solo números en una caja de texto (velocidades, anchos).
        public static void DigitsOnly(TextBox t)
        {
            t.KeyPress += delegate(object s, KeyPressEventArgs e) { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
        }

        // Tamaño como en los diálogos de IDM: "47.68 MB" (dos decimales).
        public static string Size2(long bytes, string sep)
        {
            if (bytes < 0) return "";
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + sep + "Bytes";
            double b = bytes;
            string[] u = { "KB", "MB", "GB", "TB" };
            int i = -1;
            while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
            return string.Format(CultureInfo.InvariantCulture, "{0:0.00}{1}{2}", b, sep, u[i]);
        }

        public static MainForm FindMain()
        {
            foreach (Form f in Application.OpenForms) { MainForm m = f as MainForm; if (m != null && !m.IsDisposed) return m; }
            return null;
        }
    }

    // "Ingresar nueva dirección a descargar" (botón Agregar URL), ~565x110 como IDM: combo de direcciones con las
    // últimas usadas, Aceptar/Cancelar a la derecha y el recuadro "Usar autorización" con Usuario y Contraseña.
    // Si el portapapeles tiene una URL, la propone.
    public class AddUrlForm : DialogBase
    {
        readonly AppConfig _cfg;
        ComboBox _url;
        TextBox _user, _pass;
        Label _userLbl, _passLbl;
        CheckBox _auth;
        public string ResultUrl, ResultUser, ResultPassword;

        public AddUrlForm(string presetUrl) : this(presetUrl, null) { }

        public AddUrlForm(string presetUrl, AppConfig cfg)
        {
            _cfg = cfg;
            Text = "Ingresar nueva dirección a descargar";
            ClientSize = new Size(566, 86);
            DownloadDlgUi.Lbl(this, "Dirección", 16, 12, 57);
            _url = new ComboBox(); _url.DropDownStyle = ComboBoxStyle.DropDown; _url.SetBounds(75, 9, 393, 21); Controls.Add(_url);
            if (cfg != null && cfg.AddUrlHistory != null) foreach (string u in cfg.AddUrlHistory) if (!string.IsNullOrEmpty(u)) _url.Items.Add(u);
            Button ok = DownloadDlgUi.Btn(this, "Aceptar", 482, 9, 74, DialogResult.OK);
            ok.Click += delegate { OnOk(); };
            Button cancel = DownloadDlgUi.Btn(this, "Cancelar", 482, 37, 74, DialogResult.Cancel);
            AcceptButton = ok; CancelButton = cancel;

            _auth = new CheckBox(); _auth.Text = "Usar autorización"; _auth.UseVisualStyleBackColor = true; _auth.Height = 17;
            GroupBox box = DownloadDlgUi.CheckGroup(this, _auth, 20, 34, 450, 47);
            _userLbl = DownloadDlgUi.LblRight(box, "Usuario", 30, 23, 66);
            _user = DownloadDlgUi.Box(box, 102, 20, 116);
            _passLbl = DownloadDlgUi.LblRight(box, "Contraseña", 230, 23, 95);
            _pass = DownloadDlgUi.Box(box, 329, 20, 112); _pass.UseSystemPasswordChar = true;
            _auth.CheckedChanged += delegate { EnableAuth(); };
            EnableAuth();

            string preset = presetUrl;
            if (string.IsNullOrEmpty(preset))
            {
                try { string clip = Clipboard.GetText().Trim(); if (Util.LooksLikeUrl(clip) && clip.IndexOf('\n') < 0) preset = clip; }
                catch { }
            }
            _url.Text = preset ?? "";
            EndLayout();
        }

        void EnableAuth() { _user.Enabled = _pass.Enabled = _userLbl.Enabled = _passLbl.Enabled = _auth.Checked; }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e); // traduce
            DownloadDlgUi.FitCheck(_auth, _auth.Parent.Width - 14);
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); _url.SelectAll(); _url.Focus(); }

        void OnOk()
        {
            string u = _url.Text.Trim();
            if (!Util.LooksLikeUrl(u))
            {
                Msg.Show(this, "La URL ingresada es inválida. Por favor, corrígela.", "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }
            ResultUrl = u;
            if (_auth.Checked && _user.Text.Trim().Length > 0) { ResultUser = _user.Text.Trim(); ResultPassword = _pass.Text; }
            if (_cfg != null) { _cfg.RememberUrl(u); try { _cfg.Save(); } catch { } }
        }
    }

    // "Información del archivo a descargar": aparece al añadir una descarga. Como en IDM, el archivo
    // puede ir bajando mientras tanto; "Iniciar descarga" sigue, "Descargar más tarde" la deja en la
    // cola y "Cancelar" la descarta sin dejar nada. Medidas de capturas\idm\08-informacion-archivo.jpg.
    public class FileInfoForm : DialogBase
    {
        readonly AppConfig _cfg;
        readonly DownloadManager _mgr;
        readonly DownloadItem _it;
        TextBox _url, _desc, _folderBox;
        // La ruta elegida. "Guardar como" es un combo (con las últimas carpetas), pero la ruta vive en _path,
        // que no se ve: así el resto del código (y las pruebas) la leen y la cambian como siempre.
        readonly TextBox _path = new TextBox();
        ComboBox _cat, _saveAs;
        CheckBox _remember;
        Label _size;
        PictureBox _icon;
        Timer _timer;
        bool _programmatic, _pathEdited, _syncing;
        string _shownPath;

        public string Choice;          // "start", "later" o "cancel"
        public event Action<FileInfoForm> Decided;
        public DownloadItem Item { get { return _it; } }

        public FileInfoForm(AppConfig cfg, DownloadManager mgr, DownloadItem item)
        {
            _cfg = cfg; _mgr = mgr; _it = item;
            Text = "Información del archivo a descargar";
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = true;
            ClientSize = new Size(568, 208);

            DownloadDlgUi.LblRight(this, "URL", 4, 6, 84);
            _url = DownloadDlgUi.ReadOnlyBox(this, "", 97, 3, 366);
            DownloadDlgUi.LblRight(this, "Categoría", 4, 34, 84);
            _cat = new ComboBox(); _cat.DropDownStyle = ComboBoxStyle.DropDownList; _cat.SetBounds(99, 31, 171, 21); Controls.Add(_cat);
            foreach (Category c in cfg.Categories) _cat.Items.Add(c.Name);
            Button addCat = DownloadDlgUi.Btn(this, "+", 278, 30, 22, DialogResult.None);
            addCat.Click += delegate { AddCategory(); };
            DownloadDlgUi.LblRight(this, "Guardar como", 4, 62, 84);
            _saveAs = new ComboBox(); _saveAs.DropDownStyle = ComboBoxStyle.DropDown; _saveAs.SetBounds(97, 59, 333, 21); Controls.Add(_saveAs);
            Button browse = DownloadDlgUi.Btn(this, "...", 435, 58, 28, DialogResult.None);
            browse.Click += delegate { Browse(); };
            _remember = new CheckBox(); _remember.UseVisualStyleBackColor = true; _remember.Height = 17;
            GroupBox box = DownloadDlgUi.CheckGroup(this, _remember, 99, 86, 357, 50);
            _folderBox = DownloadDlgUi.ReadOnlyBox(box, "", 6, 23, 345);
            DownloadDlgUi.LblRight(this, "Descripción", 4, 148, 84);
            _desc = DownloadDlgUi.Box(this, 97, 145, 366);

            _icon = new PictureBox(); _icon.SetBounds(497, 40, 32, 32); _icon.SizeMode = PictureBoxSizeMode.Zoom; Controls.Add(_icon);
            _size = new Label(); _size.SetBounds(470, 80, 94, 56); _size.TextAlign = ContentAlignment.TopCenter; _size.AutoEllipsis = true;
            _size.Tag = Lang.Keep; Controls.Add(_size);

            Button later = DownloadDlgUi.Btn(this, "Descargar más tarde", 97, 177, 120, DialogResult.None);
            later.Click += delegate { Later(); };
            Button start = DownloadDlgUi.Btn(this, "Iniciar descarga", 226, 177, 107, DialogResult.None);
            start.Click += delegate { Start(); };
            // Ventana no modal (Show): un botón con DialogResult no la cerraría; Cancelar (y Esc) llaman a Cancel().
            Button cancel = DownloadDlgUi.Btn(this, "Cancelar", 353, 177, 110, DialogResult.None);
            cancel.Click += delegate { Cancel(); };
            AcceptButton = start; CancelButton = cancel;
            ActiveControl = start;

            _programmatic = true;
            _url.Text = item.Url;
            _desc.Text = item.Description ?? "";
            ShowPath(item.FilePath);
            _programmatic = false;
            _path.TextChanged += delegate
            {
                SyncSaveAs();
                if (!_programmatic) _pathEdited = true;
                UpdateRemember();
            };
            _saveAs.TextChanged += delegate { if (_syncing) return; _syncing = true; _path.Text = _saveAs.Text; _syncing = false; };
            _saveAs.DropDown += delegate { FillHistory(); };
            _cat.SelectedIndexChanged += delegate { if (!_programmatic) CategoryChanged(); };

            _timer = new Timer(); _timer.Interval = 300; _timer.Tick += delegate { Poll(); }; _timer.Start();
            Poll();
            EndLayout();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e); // traduce
            FitRemember();
        }

        void FitRemember() { if (_remember.Parent != null) DownloadDlgUi.FitCheck(_remember, _remember.Parent.Width - 14); }

        void SyncSaveAs()
        {
            if (_syncing) return;
            _syncing = true;
            if (_saveAs.Text != _path.Text) _saveAs.Text = _path.Text;
            _syncing = false;
        }

        // El combo de "Guardar como" ofrece el archivo en las últimas carpetas usadas.
        void FillHistory()
        {
            string name = SafeName(_path.Text);
            string current = _saveAs.Text;
            _syncing = true;
            _saveAs.Items.Clear();
            if (_cfg.SaveAsHistory != null)
                foreach (string dir in _cfg.SaveAsHistory)
                {
                    try { if (!string.IsNullOrEmpty(dir)) _saveAs.Items.Add(Path.Combine(dir, name)); }
                    catch { }
                }
            _saveAs.Text = current;
            _syncing = false;
        }

        void ShowPath(string path)
        {
            bool was = _programmatic; _programmatic = true;
            _path.Text = path;
            _shownPath = path;
            SyncSaveAs();
            int idx = _cat.Items.IndexOf(_it.Category ?? _cfg.CategoryNameFor(Path.GetFileName(path), _it.Url, _it.Referrer));
            _cat.SelectedIndex = idx >= 0 ? idx : (_cat.Items.Count > 0 ? 0 : -1);
            _icon.Image = ShellIcons.For(path, true);
            _programmatic = was;
            UpdateRemember();
        }

        void UpdateRemember()
        {
            string c = _cat.SelectedItem != null ? _cat.SelectedItem.ToString() : "General";
            string text = Lang.T("Recordar esta ruta para la categoría \"" + c + "\"");
            if (_remember.Text != text) { _remember.Text = text; FitRemember(); }
            // "Recordar la última ruta de guardado" de la categoría deja la casilla marcada de entrada.
            Category cat = _cfg.FindCategory(c);
            if (cat != null && c != _rememberFor) { _rememberFor = c; _remember.Checked = cat.RememberLastPath; }
            // Debajo, la carpeta que se recordaría (IDM: "Descargas\").
            string dir = SafeDir(_path.Text);
            string shown = string.IsNullOrEmpty(dir) ? "" : dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (_folderBox.Text != shown) _folderBox.Text = shown;
        }

        // Otra categoría: el archivo va a su carpeta (como IDM).
        string _rememberFor;

        void CategoryChanged()
        {
            Category cat = _cfg.Categories.Find(delegate(Category c) { return c.Name == (string)_cat.SelectedItem; });
            if (cat == null) return;
            string name = SafeName(_path.Text);
            bool was = _programmatic; _programmatic = true;
            _path.Text = Path.Combine(cat.Folder, name);
            _programmatic = was;
            _pathEdited = true;
            UpdateRemember();
        }

        // Botón "+": "Agregando categoría" (la misma ventana que el panel Categorías) y se elige la nueva.
        void AddCategory()
        {
            using (CategoryForm f = new CategoryForm(_cfg, null))
            {
                if (f.ShowDialog(this) != DialogResult.OK || f.Result == null) return;
                Category c;
                MainForm main = DownloadDlgUi.FindMain();
                if (main != null) c = main.ApplyCategoryEdit(null, f.Result); // también actualiza el panel Categorías
                else { c = _cfg.ApplyCategory(null, f.Result); _cfg.Save(); }
                _programmatic = true;
                object sel = _cat.SelectedItem;
                _cat.Items.Clear();
                foreach (Category x in _cfg.Categories) _cat.Items.Add(x.Name);
                _cat.SelectedItem = sel;
                _programmatic = false;
                if (c != null) _cat.SelectedItem = c.Name; // la ruta pasa a su carpeta
            }
        }

        static string SafeName(string path) { try { return Path.GetFileName(path); } catch { return path; } }
        static string SafeDir(string path) { try { return Path.GetDirectoryName(path); } catch { return ""; } }

        void Browse()
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Title = "Guardar como...";
                try { d.InitialDirectory = Path.GetDirectoryName(_path.Text); d.FileName = Path.GetFileName(_path.Text); } catch { }
                d.OverwritePrompt = false; // lo pregunta IDMClone al aceptar
                if (d.ShowDialog(this) == DialogResult.OK) _path.Text = d.FileName;
            }
        }

        // Tamaño y nombre llegan con la respuesta del servidor (Content-Disposition, redirecciones...).
        void Poll()
        {
            if (_it.Status == DownloadStatus.Error && !string.IsNullOrEmpty(_it.Error))
            {
                _size.ForeColor = Color.Firebrick;
                _size.Text = _it.Error;
            }
            else
            {
                _size.ForeColor = SystemColors.ControlText;
                _size.Text = _it.Size > 0 ? DownloadDlgUi.Size2(_it.Size, " ") : (_it.Status == DownloadStatus.Complete ? DownloadDlgUi.Size2(_it.Downloaded, " ") : Lang.T("(desconocido)"));
            }
            if (!_pathEdited && !string.Equals(_it.FilePath, _shownPath, StringComparison.OrdinalIgnoreCase)) ShowPath(_it.FilePath);
        }

        // Aplica nombre, carpeta, categoría y descripción a la descarga. false = el usuario canceló.
        bool Apply()
        {
            string wanted = _path.Text.Trim();
            try { wanted = Path.GetFullPath(wanted); }
            catch { Msg.Show(this, "La ruta no es válida.", "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            if (string.IsNullOrEmpty(Path.GetFileName(wanted))) { Msg.Show(this, "Escribe un nombre de archivo.", "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }

            if (!string.Equals(wanted, _it.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                bool overwrite = false;
                if (File.Exists(wanted))
                {
                    DialogResult r = Msg.Show(this,
                        Path.GetFileName(wanted) + " ya existe en tu ordenador. ¿Quieres reemplazar el archivo?\n\n" +
                        "Pulsa \"Sí\" para reemplazar, \"No\" para guardar el archivo con un nombre diferente, \"Cancelar\" para volver.",
                        "IDMClone", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (r == DialogResult.Cancel) return false;
                    overwrite = r == DialogResult.Yes;
                }
                string old = _it.FilePath;
                string final = overwrite ? wanted : _mgr.ReservePath(wanted, _it);
                if (_it.Status == DownloadStatus.Complete)
                {
                    // Ya terminó mientras se mostraba el diálogo: el archivo está en su destino y hay que moverlo.
                    try
                    {
                        if (overwrite && File.Exists(final)) File.Delete(final);
                        _mgr.MoveRename(_it, final);
                    }
                    catch (Exception ex) { Msg.Show(this, ex.Message, "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
                }
                else
                {
                    // Sin carpeta temporal se apartó un archivo vacío con el nombre provisional: liberarlo.
                    // (Con "Sobrescribir", la ruta anterior es la del duplicado: no es nuestra.)
                    try { if (!_it.Overwrite && string.IsNullOrEmpty(_it.TempPath) && File.Exists(old) && new FileInfo(old).Length == 0 && _it.Downloaded == 0) File.Delete(old); } catch { }
                    _it.FilePath = final;
                    _it.Overwrite = overwrite;
                    _it.NameFromUser = true;
                    _it.FolderFromUser = true;
                }
            }
            if (_cat.SelectedItem != null) _it.Category = _cat.SelectedItem.ToString();
            _it.Description = _desc.Text.Trim().Length > 0 ? _desc.Text.Trim() : null;
            if (_remember.Checked)
            {
                Category cat = _cfg.Categories.Find(delegate(Category c) { return c.Name == _it.Category; });
                if (cat != null)
                {
                    cat.Folder = Path.GetDirectoryName(_it.FilePath);
                    if (cat.IsGeneral) _cfg.DefaultFolder = cat.Folder;
                }
            }
            // Carpeta para el combo "Guardar como" de la próxima vez.
            _cfg.RememberSaveFolder(SafeDir(_it.FilePath));
            try { _cfg.Save(); } catch { }
            return true;
        }

        public void Start()
        {
            if (!Apply()) return;
            _mgr.Confirm(_it);
            if (_it.Engine == null || !_it.Engine.IsRunning) _mgr.StartItem(_it);
            Finish("start");
        }

        public void Later()
        {
            if (!Apply()) return;
            int queue = DownloadItem.MainQueue;
            bool startQueue = false;
            if (Visible && _it.Downloaded > 0 && _it.Status != DownloadStatus.Complete)
            {
                // Como IDM: si ya bajó una parte, avisa (y no sale el panel de colas: va a la cola principal).
                Msg.Show(this, "Pulsaste el botón \"Descargar Más Tarde\", pero actualmente IDMClone ya descargó una parte del archivo. " +
                    "IDMClone inicia una descarga mientras muestra el diálogo \"Información del archivo a descargar\". " +
                    "Puedes desactivar esta opción en \"Opciones -> Descargas\"", "Descargar más tarde", MessageBoxButtons.OK, MessageBoxIcon.None);
            }
            else if (_cfg.ShowQueuePicker && Visible && _it.Status != DownloadStatus.Complete) // ya terminó: nada que encolar
            {
                using (QueuePickForm q = new QueuePickForm(_mgr.Queues))
                {
                    if (q.ShowDialog(this) != DialogResult.OK) return;
                    queue = q.Queue; startQueue = q.StartQueue;
                    if (q.DontAsk) { _cfg.ShowQueuePicker = false; _cfg.Save(); }
                }
            }
            // Lo que ya se bajó mientras se mostraba el diálogo se conserva: la cola lo reanudará.
            DownloadEngine eng = _it.Engine;
            if (eng != null && eng.IsRunning) { eng.Stop(); eng.WaitForExit(10000); }
            if (_it.Status != DownloadStatus.Complete && _it.Status != DownloadStatus.Error) _it.Status = _it.Downloaded > 0 ? DownloadStatus.Paused : DownloadStatus.Queued;
            if (_it.Status != DownloadStatus.Complete) _it.Queue = queue; // una terminada no vuelve a la cola
            _mgr.Confirm(_it);
            if (startQueue && queue != DownloadItem.NoQueue) _mgr.StartQueue(queue);
            Finish("later");
        }

        public void Cancel()
        {
            if (Choice != null) return;
            _mgr.Discard(_it);
            Finish("cancel");
        }

        void Finish(string choice)
        {
            Choice = choice;
            if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; }
            Action<FileInfoForm> h = Decided; if (h != null) h(this);
            if (!_closing && !IsDisposed) Close();
        }

        bool _closing;

        // Cerrar con la X equivale a Cancelar.
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _closing = true;
            if (Choice == null) Cancel();
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _path.Dispose();
            base.Dispose(disposing);
        }
    }

    // Panel de selección de colas al pulsar "Descargar más tarde" (y al añadir varios enlaces).
    public class QueuePickForm : DialogBase
    {
        ComboBox _queue;
        CheckBox _start, _dontAsk;
        readonly List<QueueDef> _list = new List<QueueDef>();
        public int Queue { get { return _queue.SelectedIndex >= 0 && _queue.SelectedIndex < _list.Count ? _list[_queue.SelectedIndex].Id : DownloadItem.NoQueue; } }
        public bool StartQueue { get { return _start.Checked && _start.Enabled; } }
        public bool DontAsk { get { return _dontAsk.Checked; } }

        public QueuePickForm() : this(null) { }

        public QueuePickForm(QueueSet queues)
        {
            Text = "Descargar más tarde";
            ClientSize = new Size(384, 138);
            DownloadDlgUi.Lbl(this, "Añadir archivos a la cola:", 14, 12, 356);
            _queue = new ComboBox(); _queue.DropDownStyle = ComboBoxStyle.DropDownList; _queue.SetBounds(14, 31, 356, 21);
            foreach (QueueDef q in (queues ?? QueueSet.CreateDefault(null)).Queues) { _list.Add(q); _queue.Items.Add(MainForm.QueueLabel(q)); }
            _queue.Items.Add("No añadir a ninguna cola");
            _queue.SelectedIndex = 0; Controls.Add(_queue);
            _start = DownloadDlgUi.Chk(this, "Iniciar procesamiento de la cola", 14, 60, 356, 17);
            _dontAsk = DownloadDlgUi.Chk(this, "No preguntar otra vez", 14, 81, 356, 17);
            Button ok = DownloadDlgUi.Btn(this, "Aceptar", 214, 107, 75, DialogResult.OK);
            Button cancel = DownloadDlgUi.Btn(this, "Cancelar", 295, 107, 75, DialogResult.Cancel);
            AcceptButton = ok; CancelButton = cancel;
            _queue.SelectedIndexChanged += delegate { _start.Enabled = _queue.SelectedIndex < _list.Count; };
            EndLayout();
        }
    }

    // Icono de IDMClone que se arrastra para soltar el archivo en otro programa (abajo a la derecha de
    // "Descarga completada", como IDM).
    class FileDragIcon : PictureBox
    {
        readonly string _path;

        public FileDragIcon(string path)
        {
            _path = path;
            SizeMode = PictureBoxSizeMode.CenterImage;
            using (Icon ic = AppIcon.Sized(Dpi.Px(20))) Image = ic.ToBitmap();
            Cursor = Cursors.Hand;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Image != null) { Image.Dispose(); Image = null; } // el PictureBox no libera su imagen
            base.Dispose(disposing);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !File.Exists(_path)) return;
            DataObject d = new DataObject(DataFormats.FileDrop, new string[] { _path });
            DoDragDrop(d, DragDropEffects.Copy);
        }
    }

    // "Descarga completada" (capturas\idm\10-descarga-completada.jpg, ~462x238).
    public class CompleteForm : DialogBase
    {
        readonly DownloadItem _it;
        CheckBox _dontShow;
        public bool DontShowAgain { get { return _dontShow.Checked; } }

        public CompleteForm(DownloadItem it)
        {
            _it = it;
            Text = "Descarga completada";
            ShowInTaskbar = true;
            ShowIcon = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(462, 210);

            PictureBox icon = new PictureBox(); icon.SizeMode = PictureBoxSizeMode.Zoom; icon.Image = Glyphs.Get("folder-open", Dpi.Px(32));
            icon.SetBounds(14, 8, 34, 34); Controls.Add(icon);
            DownloadDlgUi.Lbl(this, "Descarga completada", 54, 8, 394);
            long size = it.Size > 0 ? it.Size : it.Downloaded;
            Label got = DownloadDlgUi.Lbl(this, "Descargado " + DownloadDlgUi.Size2(size, "  ") + " (" + size.ToString(CultureInfo.InvariantCulture) + " Bytes)" +
                (it.Size > 0 ? "" : ". El archivo puede no haber sido descargado completamente porque su tamaño es desconocido."), 54, 23, 394);
            if (it.Size <= 0) got.Height = 30;
            DownloadDlgUi.Lbl(this, "Dirección", 15, 59, 300);
            DownloadDlgUi.ReadOnlyBox(this, it.Url, 15, 74, 434);
            DownloadDlgUi.Lbl(this, "El archivo se guardó como", 15, 106, 300);
            DownloadDlgUi.ReadOnlyBox(this, it.FilePath, 15, 121, 434);

            Button open = DownloadDlgUi.Btn(this, "Abrir", 16, 155, 89, DialogResult.None);
            open.Click += delegate { Shell.Open(this, _it.FilePath); Close(); };
            Button openWith = DownloadDlgUi.Btn(this, "Abrir con...", 119, 155, 98, DialogResult.None);
            openWith.Click += delegate { Shell.OpenWith(this, _it.FilePath); Close(); };
            Button folder = DownloadDlgUi.Btn(this, "Abrir carpeta", 223, 155, 89, DialogResult.None);
            folder.Click += delegate { Shell.OpenFolder(_it.FilePath); Close(); };
            Button close = DownloadDlgUi.Btn(this, "Cerrar", 358, 155, 90, DialogResult.Cancel);
            close.Click += delegate { Close(); };
            _dontShow = DownloadDlgUi.Chk(this, "No mostrar este diálogo la próxima vez", 17, 187, 300, 17);
            FileDragIcon drag = new FileDragIcon(it.FilePath); drag.SetBounds(419, 182, 28, 25); Controls.Add(drag);
            AcceptButton = open; CancelButton = close;
            ActiveControl = open;
            EndLayout();
        }
    }

    // "Propiedades de archivo" (capturas\idm\11-propiedades.jpg, ~485x420): nombre, tipo, estado y tamaño;
    // "Guardar en" con "Mover" (el cuadro estándar Guardar como), dirección, descripción, página de origen,
    // referencia y usuario/contraseña de la descarga. Solo "Abrir" y "Aceptar", como IDM (Esc cierra sin cambios).
    public class PropertiesForm : DialogBase
    {
        readonly AppConfig _cfg;
        readonly DownloadManager _mgr;
        readonly DownloadItem _it;
        TextBox _name, _saveTo, _url, _desc, _user, _pass;
        bool _sync;

        public PropertiesForm(AppConfig cfg, DownloadManager mgr, DownloadItem it)
        {
            _cfg = cfg; _mgr = mgr; _it = it;
            Text = "Propiedades de archivo";
            ClientSize = new Size(486, 396);

            PictureBox icon = new PictureBox(); icon.SizeMode = PictureBoxSizeMode.Zoom; icon.Image = ShellIcons.For(it.FilePath, true); icon.SetBounds(17, 9, 32, 32); Controls.Add(icon);
            // El nombre se puede cambiar aquí mismo (caja sin borde, parece un texto, como IDM).
            _name = new TextBox(); _name.BorderStyle = BorderStyle.None; _name.BackColor = SystemColors.Control; _name.Text = it.FileName;
            _name.SetBounds(88, 19, 380, 14); Controls.Add(_name);
            DownloadDlgUi.Line(this, 11, 47, 459);
            DownloadDlgUi.Lbl(this, "Tipo:", 11, 56, 74); DownloadDlgUi.Data(this, ShellIcons.TypeName(it.FilePath), 88, 56, 380); // nombre que da Windows
            DownloadDlgUi.Lbl(this, "Estado:", 11, 76, 74);
            Label state = DownloadDlgUi.Data(this, StateText(it), 88, 76, 380);
            if (it.Status == DownloadStatus.Error) state.ForeColor = Color.Firebrick;
            DownloadDlgUi.Lbl(this, "Tamaño:", 11, 96, 74);
            DownloadDlgUi.Data(this, it.Size >= 0 ? DownloadDlgUi.Size2(it.Size, " ") + " (" + it.Size.ToString(CultureInfo.InvariantCulture) + " Bytes)" : Lang.T("(desconocido)"), 88, 96, 380);

            DownloadDlgUi.Lbl(this, "Guardar en:", 11, 121, 76);
            _saveTo = DownloadDlgUi.Box(this, 89, 118, 295); _saveTo.Text = it.FilePath ?? "";
            Button move = DownloadDlgUi.Btn(this, "Mover", 396, 117, 72, DialogResult.None);
            move.Click += delegate { MoveTo(); };
            DownloadDlgUi.Lbl(this, "Dirección:", 11, 152, 76);
            _url = DownloadDlgUi.Box(this, 89, 149, 381); _url.Text = it.Url;
            _url.ReadOnly = it.Status == DownloadStatus.Complete; // terminada: la dirección ya no sirve de nada
            DownloadDlgUi.Lbl(this, "Descripción", 11, 183, 76);
            _desc = DownloadDlgUi.Box(this, 89, 180, 381); _desc.Text = it.Description ?? "";
            DownloadDlgUi.Line(this, 11, 210, 459);
            DownloadDlgUi.Lbl(this, "La página web con este archivo fue obtenida de:", 11, 218, 459);
            string page = ParentPage(it);
            Label pageLbl = DownloadDlgUi.Data(this, page.Length > 0 ? page : Lang.T("Desconocido"), 11, 237, 459);
            if (page.Length == 0) pageLbl.Tag = null;
            DownloadDlgUi.Line(this, 11, 259, 459);
            DownloadDlgUi.Lbl(this, "Referencia:", 11, 272, 76);
            DownloadDlgUi.ReadOnlyBox(this, it.Referrer ?? "", 89, 269, 381);
            DownloadDlgUi.LblRight(this, "Usuario", 11, 300, 104);
            _user = DownloadDlgUi.Box(this, 122, 297, 189); _user.Text = it.LoginUser ?? "";
            DownloadDlgUi.LblRight(this, "Contraseña", 11, 328, 104);
            _pass = DownloadDlgUi.Box(this, 122, 325, 189); _pass.UseSystemPasswordChar = true; _pass.Text = it.LoginUser != null ? it.LoginPassword : "";
            DownloadDlgUi.Line(this, 11, 355, 459);

            Button open = DownloadDlgUi.Btn(this, "Abrir", 261, 366, 72, DialogResult.None);
            open.Enabled = it.Status == DownloadStatus.Complete;
            open.Click += delegate { Shell.Open(this, _it.FilePath); };
            Button ok = DownloadDlgUi.Btn(this, "Aceptar", 396, 366, 72, DialogResult.OK);
            ok.Click += delegate { if (!Apply()) DialogResult = DialogResult.None; };
            AcceptButton = ok;

            // Nombre y "Guardar en" son la misma ruta: cambiar uno cambia el otro.
            _name.TextChanged += delegate
            {
                if (_sync) return;
                _sync = true;
                try { _saveTo.Text = Path.Combine(SafeDir(_saveTo.Text), _name.Text); } catch { }
                _sync = false;
            };
            _saveTo.TextChanged += delegate
            {
                if (_sync) return;
                _sync = true;
                try { _name.Text = Path.GetFileName(_saveTo.Text); } catch { }
                _sync = false;
            };
            ActiveControl = _saveTo;
            EndLayout();
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); _saveTo.SelectAll(); }

        // Sin botón Cancelar (como IDM): Esc cierra sin aplicar nada.
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            return base.ProcessDialogKey(keyData);
        }

        // "Mover": el cuadro estándar de Windows "Guardar como" (como Mover/Renombrar en IDM).
        void MoveTo()
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                try { d.InitialDirectory = SafeDir(_saveTo.Text); d.FileName = Path.GetFileName(_saveTo.Text); } catch { }
                d.Filter = Lang.T("Todos los archivos") + " (*.*)|*.*";
                d.OverwritePrompt = true;
                if (d.ShowDialog(this) == DialogResult.OK) _saveTo.Text = d.FileName;
            }
        }

        static string SafeDir(string p) { try { return Path.GetDirectoryName(p) ?? ""; } catch { return ""; } }

        static string ParentPage(DownloadItem it)
        {
            string r = it.Referrer ?? "";
            return Util.LooksLikeUrl(r) ? r : "";
        }

        static string StateText(DownloadItem it)
        {
            if (it.Status == DownloadStatus.Complete) return Lang.T("Completo");
            if (it.Status == DownloadStatus.Error) return Lang.T("Error: " + (it.Error ?? ""));
            string s = ListColumns.StatusText(it);
            return s.Length > 0 ? s : ProgressForm.StatusText(it.Status);
        }

        public bool Apply()
        {
            try
            {
                string url = _url.Text.Trim();
                if (!Util.LooksLikeUrl(url)) { Msg.Show(this, "La URL ingresada es inválida. Por favor, corrígela.", "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
                string path = _saveTo.Text.Trim();
                string name = Util.SanitizeFileName(SafeName(path));
                if (string.IsNullOrEmpty(name)) { Msg.Show(this, "Escribe un nombre de archivo.", "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
                string dir = SafeDir(path);
                if (string.IsNullOrEmpty(dir)) dir = SafeDir(_it.FilePath);
                string newPath = Path.Combine(dir, name);
                _it.Description = _desc.Text.Trim().Length > 0 ? _desc.Text.Trim() : null;
                if (_user.Text.Trim().Length > 0) { _it.LoginUser = _user.Text.Trim(); _it.LoginPassword = _pass.Text; }
                else { _it.LoginUser = null; _it.LoginPasswordEnc = null; }
                if (!string.Equals(Path.GetFullPath(newPath), _it.FilePath, StringComparison.OrdinalIgnoreCase)) _mgr.MoveRename(_it, newPath);
                if (url != _it.Url)
                {
                    bool running = _it.Engine != null && _it.Engine.IsRunning && !_it.Engine.StopRequested;
                    if (running) _mgr.ApplyNewAddress(_it, url, null, null, null);
                    else { _it.Url = url; _it.SaveState(); }
                }
                _mgr.SaveList();
                return true;
            }
            catch (Exception ex)
            {
                Msg.Show(this, ex.Message, "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        static string SafeName(string p) { try { return Path.GetFileName(p); } catch { return p; } }
    }

    // "Columnas" (capturas\idm\12-columnas.jpg, ~363x307): qué columnas se ven, en qué orden y con qué ancho.
    public class ColumnsForm : DialogBase
    {
        CheckedListBox _list;
        TextBox _width;
        Button _up, _down, _show, _hide;
        bool _settingWidth;
        readonly Dictionary<string, int> _widths = new Dictionary<string, int>();
        public string ResultLayout;
        public CheckedListBox List { get { return _list; } } // para las pruebas

        public ColumnsForm(string layout)
        {
            Text = "Columnas";
            ShowIcon = false;
            ClientSize = new Size(360, 282);
            Label help = DownloadDlgUi.Lbl(this, "Marca las columnas que quieres ver en esta lista. Usa los botones Subir y Bajar para reordenar las columnas como quieras.", 14, 8, 332);
            help.Height = 30; help.TextAlign = ContentAlignment.TopLeft;
            // Alto entero de filas (como IDM): sin IntegralHeight la última fila se veía cortada por abajo.
            _list = new CheckedListBox(); _list.SetBounds(16, 57, 207, 140); _list.CheckOnClick = true; _list.IntegralHeight = true; Controls.Add(_list);
            Fill(layout);
            _up = DownloadDlgUi.Btn(this, "Subir", 242, 59, 104, DialogResult.None); _up.Click += delegate { MoveItem(-1); };
            _down = DownloadDlgUi.Btn(this, "Bajar", 242, 87, 104, DialogResult.None); _down.Click += delegate { MoveItem(1); };
            _show = DownloadDlgUi.Btn(this, "Mostrar", 242, 116, 104, DialogResult.None); _show.Click += delegate { if (_list.SelectedIndex >= 0) _list.SetItemChecked(_list.SelectedIndex, true); };
            _hide = DownloadDlgUi.Btn(this, "Ocultar", 242, 145, 104, DialogResult.None); _hide.Click += delegate { if (_list.SelectedIndex >= 0) _list.SetItemChecked(_list.SelectedIndex, false); };
            Button reset = DownloadDlgUi.Btn(this, "Restablecer", 242, 174, 104, DialogResult.None); reset.Click += delegate { _widths.Clear(); Fill(AppConfig.DefaultColumns); UpdateButtons(); };
            DownloadDlgUi.LblRight(this, "La columna seleccionada debe ser", 10, 211, 194);
            _width = DownloadDlgUi.Box(this, 208, 208, 30); _width.MaxLength = 4; DownloadDlgUi.DigitsOnly(_width);
            DownloadDlgUi.Lbl(this, "píxeles de ancho", 242, 211, 110);
            DownloadDlgUi.Line(this, 16, 241, 330);
            Button ok = DownloadDlgUi.Btn(this, "Aceptar", 166, 252, 72, DialogResult.OK); ok.Click += delegate { Save(); };
            Button cancel = DownloadDlgUi.Btn(this, "Cancelar", 273, 252, 73, DialogResult.Cancel);
            AcceptButton = ok; CancelButton = cancel;
            _list.SelectedIndexChanged += delegate { UpdateButtons(); };
            _width.TextChanged += delegate
            {
                if (_settingWidth) return;
                ColumnDef c = Selected(); int w;
                if (c != null && int.TryParse(_width.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out w) && w >= 16 && w <= 2000) _widths[c.Id] = w;
            };
            _list.ItemCheck += delegate(object s, ItemCheckEventArgs e)
            {
                // "Nombre de archivo" no se puede ocultar.
                if (((ColumnItem)_list.Items[e.Index]).Def.Id == "name") e.NewValue = CheckState.Checked;
            };
            UpdateButtons();
            EndLayout();
        }

        // Como IDM: sin columna elegida los botones quedan en gris y la caja del ancho vacía.
        void UpdateButtons()
        {
            ColumnDef c = Selected();
            int i = _list.SelectedIndex;
            _up.Enabled = c != null && i > 0;
            _down.Enabled = c != null && i < _list.Items.Count - 1;
            _show.Enabled = _hide.Enabled = c != null;
            _width.Enabled = c != null;
            _settingWidth = true;
            _width.Text = c != null ? WidthOf(c).ToString(CultureInfo.InvariantCulture) : "";
            _settingWidth = false;
        }

        class ColumnItem
        {
            public ColumnDef Def;
            public override string ToString() { return Lang.T(Def.Title); }
        }

        ColumnDef Selected() { return _list.SelectedItem != null ? ((ColumnItem)_list.SelectedItem).Def : null; }
        int WidthOf(ColumnDef c) { int w; return _widths.TryGetValue(c.Id, out w) ? w : c.Width; }

        void Fill(string layout)
        {
            _list.Items.Clear();
            HashSet<string> visible = new HashSet<string>();
            foreach (KeyValuePair<ColumnDef, int> kv in ListColumns.Parse(layout))
            {
                if (!_widths.ContainsKey(kv.Key.Id)) _widths[kv.Key.Id] = kv.Value;
                _list.Items.Add(new ColumnItem { Def = kv.Key }, true);
                visible.Add(kv.Key.Id);
            }
            foreach (ColumnDef c in ListColumns.All)
                if (!visible.Contains(c.Id)) _list.Items.Add(new ColumnItem { Def = c }, false);
        }

        void MoveItem(int dir)
        {
            int i = _list.SelectedIndex, j = i + dir;
            if (i < 0 || j < 0 || j >= _list.Items.Count) return;
            object item = _list.Items[i];
            bool chk = _list.GetItemChecked(i);
            _list.Items.RemoveAt(i);
            _list.Items.Insert(j, item);
            _list.SetItemChecked(j, chk);
            _list.SelectedIndex = j;
        }

        void Save()
        {
            List<KeyValuePair<ColumnDef, int>> cols = new List<KeyValuePair<ColumnDef, int>>();
            for (int i = 0; i < _list.Items.Count; i++)
                if (_list.GetItemChecked(i)) { ColumnDef c = ((ColumnItem)_list.Items[i]).Def; cols.Add(new KeyValuePair<ColumnDef, int>(c, WidthOf(c))); }
            ResultLayout = ListColumns.Format(cols);
        }
    }

    // "Enlace de descarga duplicado" (capturas\idm\13-duplicado.jpg, ~428x277).
    public class DuplicateForm : DialogBase
    {
        RadioButton _number, _overwrite, _resume;
        CheckBox _remember;
        TextBox _link;
        public string Action { get { return _overwrite.Checked ? "overwrite" : _resume.Checked ? "resume" : "number"; } }
        public bool Remember { get { return _remember.Checked; } }

        public DuplicateForm(DownloadItem existing)
        {
            Text = "Enlace de descarga duplicado";
            ShowIcon = false;
            ClientSize = new Size(430, 250);
            _link = DownloadDlgUi.ReadOnlyBox(this, existing.Url, 14, 10, 406); _link.BackColor = SystemColors.Window;
            Label l = DownloadDlgUi.Lbl(this, "Este archivo ya existe en la lista de descarga. Puedes elegir una de las opciones siguientes, o pulsar el botón Cancelar para saltar la descarga de este archivo", 14, 40, 406);
            l.Height = 30; l.TextAlign = ContentAlignment.TopLeft;
            _number = DownloadDlgUi.Radio(this, "Añadir el duplicado con un nombre de archivo numerado.", 16, 89, 404);
            _overwrite = DownloadDlgUi.Radio(this, "Añadir el duplicado y sobrescribir el archivo existente.", 16, 117, 404);
            _resume = DownloadDlgUi.Radio(this, "Con archivo completo, mostrar descarga completada; si no, continuar.", 16, 145, 404);
            _resume.Checked = true; // la opción marcada de IDM
            Button ok = DownloadDlgUi.Btn(this, "Aceptar", 131, 182, 72, DialogResult.OK);
            Button cancel = DownloadDlgUi.Btn(this, "Cancelar", 233, 182, 72, DialogResult.Cancel);
            _remember = DownloadDlgUi.Chk(this, "Recordar mi selección y no mostrar este diálogo la próxima vez.\nPuedes cambiar más tarde esto en Opciones -> Descargas.", 21, 214, 400, 30);
            AcceptButton = ok; CancelButton = cancel;
            ActiveControl = _link;
            EndLayout();
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); _link.SelectAll(); }
    }

    // "Cancelando una descarga iniciada automáticamente" (capturas\extension\idm-04-cancelar-descarga-automatica.jpg):
    // al cancelar otra vez una descarga que el navegador pasó solo desde el mismo sitio, ofrece añadir el sitio o un
    // patrón de direcciones a las excepciones (Opciones > Tipos de archivo).
    public class AutoCancelForm : DialogBase
    {
        CheckBox _site, _pattern, _dontAsk;
        public readonly string SiteHost, Pattern;
        public bool ExcludeSite { get { return _site.Checked; } }
        public bool ExcludePattern { get { return _pattern.Checked; } }
        public bool DontAskAgain { get { return _dontAsk.Checked; } }

        public AutoCancelForm(string url)
        {
            SiteHost = HostOf(url);
            Pattern = PatternOf(url);
            Text = "Cancelando una descarga iniciada automáticamente";
            ShowIcon = false;
            ShowInTaskbar = true; StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(462, 250);
            DownloadDlgUi.Lbl(this, "Has cancelado descargas similares.", 12, 10, 438);
            Label l = DownloadDlgUi.Lbl(this, "Si IDMClone siempre toma el control de estas descargas por error, puedes añadir este sitio o un patrón para direcciones similares a la lista de excepciones de IDMClone:", 12, 32, 438);
            l.Height = 30; l.TextAlign = ContentAlignment.TopLeft;
            _site = DownloadDlgUi.Chk(this, "No iniciar la descarga automáticamente desde este sitio:", 14, 68, 436, 20);
            Link(SiteHost, 88);
            _pattern = DownloadDlgUi.Chk(this, "No capturar descargas de similares direcciones usando este patrón:", 14, 112, 436, 20);
            _pattern.Checked = true; // como IDM
            Link(Pattern, 132);
            Label n = DownloadDlgUi.Lbl(this, "Puedes editar las listas de excepción en la pestaña \"Opciones->Tipos de archivo\" del cuadro de diálogo opciones", 12, 156, 438);
            n.Height = 28; n.TextAlign = ContentAlignment.TopLeft;
            Button ok = DownloadDlgUi.Btn(this, "Aceptar", 146, 190, 80, DialogResult.OK);
            Button cancel = DownloadDlgUi.Btn(this, "Cancelar", 240, 190, 80, DialogResult.Cancel);
            _dontAsk = DownloadDlgUi.Chk(this, "No mostrar este diálogo otra vez", 14, 222, 300, 20);
            AcceptButton = ok; CancelButton = cancel;
            EndLayout();
        }

        void Link(string text, int y)
        {
            Label v = DownloadDlgUi.Lbl(this, text, 34, y, 416);
            v.ForeColor = Color.FromArgb(0, 0, 204); v.AutoEllipsis = true;
        }

        // "sitio.com" (sin puerto: vale para cualquiera).
        // En punycode (IdnHost), como lo ve la extensión: "münchen.de" -> "xn--mnchen-3ya.de".
        public static string HostOf(string url)
        {
            Uri u;
            return Uri.TryCreate(url, UriKind.Absolute, out u) ? HostPart(u) : "";
        }

        // IPv6 con corchetes ("[::1]"), como la extensión; el resto en punycode.
        static string HostPart(Uri u) { return (u.HostNameType == UriHostNameType.IPv6 ? u.Host : u.IdnHost).ToLowerInvariant(); }

        // "http://sitio.com:8080/carpeta/*": la misma carpeta del servidor, como el patrón de IDM.
        public static string PatternOf(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return "";
            string path = u.AbsolutePath;
            int slash = path.LastIndexOf('/');
            return u.Scheme + "://" + HostPart(u) + (u.IsDefaultPort ? "" : ":" + u.Port) +
                   (slash >= 0 ? path.Substring(0, slash + 1) : "/") + "*";
        }

        // Con la ventana principal oculta (en la bandeja) no tiene dueño: en la barra de tareas y delante del navegador.
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (Owner == null) { TopMost = true; Activate(); TopMost = false; }
        }
    }

    // "Actualizar dirección de descarga": espera a que el navegador vuelva a dar el enlace.
    public class RefreshWaitForm : DialogBase
    {
        public RefreshWaitForm(DownloadItem it)
        {
            Text = "Actualizar dirección de descarga";
            ShowInTaskbar = true;
            ClientSize = new Size(480, 172);
            Label l = DownloadDlgUi.Lbl(this, "IDMClone abrirá la página web en el navegador donde se capturó esta descarga. Por favor, inicia otra vez la descarga del mismo archivo en el navegador; IDMClone capturará la nueva dirección para reanudarla.", 14, 10, 452);
            l.Height = 42; l.TextAlign = ContentAlignment.TopLeft;
            DownloadDlgUi.Lbl(this, "Archivo:", 14, 63, 125);
            DownloadDlgUi.ReadOnlyBox(this, it.FileName, 142, 60, 324);
            DownloadDlgUi.Lbl(this, "Página de la descarga:", 14, 90, 125);
            DownloadDlgUi.ReadOnlyBox(this, string.IsNullOrEmpty(it.Referrer) ? Lang.T("(desconocida: abre la página tú mismo)") : it.Referrer, 142, 87, 324);
            Label w = DownloadDlgUi.Lbl(this, "Esperando nuevos datos...", 14, 116, 300);
            w.Font = Theme.UiFontBold;
            Button stop = DownloadDlgUi.Btn(this, "Detener espera", 361, 140, 105, DialogResult.Cancel);
            CancelButton = stop;
            EndLayout();
        }
    }

    // Cuenta atrás antes de apagar, suspender o hibernar el equipo ("Apagar equipo al finalizar").
    public class ShutdownForm : DialogBase
    {
        int _left;
        readonly string _mode;
        Label _msg;
        Timer _t;

        public ShutdownForm(string reason, int seconds) : this(reason, seconds, FinishOptions.Shutdown) { }

        public ShutdownForm(string reason, int seconds, string mode)
        {
            _mode = mode ?? FinishOptions.Shutdown;
            int i = Math.Max(0, Array.IndexOf(FinishOptions.Modes, _mode));
            Text = FinishOptions.ModeNames[i];
            ShowInTaskbar = true;
            TopMost = true;
            ClientSize = new Size(420, 122);
            _left = seconds;
            Label r = DownloadDlgUi.Lbl(this, reason, 14, 10, 392); r.Height = 42; r.TextAlign = ContentAlignment.TopLeft; r.UseMnemonic = false;
            _msg = DownloadDlgUi.Lbl(this, "", 14, 58, 392);
            Button now = DownloadDlgUi.Btn(this, FinishOptions.ModeNames[i] + " ahora", 226, 89, 99, DialogResult.OK);
            Button cancel = DownloadDlgUi.Btn(this, "Cancelar", 331, 89, 75, DialogResult.Cancel);
            AcceptButton = now; CancelButton = cancel;
            _t = new Timer(); _t.Interval = 1000;
            _t.Tick += delegate { _left--; Show2(); if (_left <= 0) { _t.Stop(); DialogResult = DialogResult.OK; } };
            Show2();
            EndLayout();
        }

        public string Mode { get { return _mode; } }

        void Show2() { _msg.Text = Lang.T(FinishOptions.CountdownText(_mode, _left)); }
        protected override void OnShown(EventArgs e) { base.OnShown(e); _t.Start(); }
        protected override void OnFormClosed(FormClosedEventArgs e) { _t.Stop(); _t.Dispose(); base.OnFormClosed(e); }
    }

    // Descargas > Limitador de velocidad > Configurar... (capturas\idm\16-limitador.jpg, ~464x160). Activar y
    // desactivar el limitador se hace desde el menú (Activar/Desactivar), como en IDM.
    public class SpeedLimiterForm : DialogBase
    {
        CheckBox _atStart;
        TextBox _kb;

        public SpeedLimiterForm(AppConfig cfg)
        {
            Text = "Configurar Limitador de Velocidad";
            ShowIcon = false;
            ClientSize = new Size(462, 137);
            DownloadDlgUi.LblRight(this, "Máxima velocidad de descarga", 14, 9, 259);
            _kb = DownloadDlgUi.Box(this, 278, 6, 52); _kb.TextAlign = HorizontalAlignment.Right; _kb.MaxLength = 7; DownloadDlgUi.DigitsOnly(_kb);
            _kb.Text = Clamp(cfg.SpeedLimitKBps > 0 ? cfg.SpeedLimitKBps : 500, 1, 9999999).ToString(CultureInfo.InvariantCulture);
            DownloadDlgUi.Lbl(this, "KBytes/seg", 336, 9, 112);
            Label n = DownloadDlgUi.Lbl(this, "Nota: ¡Los servidores de Internet pueden cortar la conexión cuando la velocidad esté demasiado limitada! No se recomienda usar el Limitador de Velocidad para descargar de servidores que no soporten la opción de \"reanudar\".", 14, 34, 434);
            n.Height = 42; n.TextAlign = ContentAlignment.TopLeft;
            _atStart = DownloadDlgUi.Chk(this, "Activar siempre al iniciar el Limitador de Velocidad en IDMClone", 18, 81, 420, 17);
            _atStart.Checked = cfg.SpeedLimiterOnAtStart;
            Button ok = DownloadDlgUi.Btn(this, "Aceptar", 291, 109, 72, DialogResult.OK);
            ok.Click += delegate
            {
                int kb;
                if (!int.TryParse(_kb.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out kb) || kb < 1)
                {
                    Msg.Show(this, "Escribe una velocidad mayor que 0.", "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    DialogResult = DialogResult.None;
                    return;
                }
                // Cambiar el valor no enciende ni apaga el limitador (configuraciones antiguas: se fija como estaba).
                if (cfg.SpeedLimiterOn == null) cfg.SpeedLimiterOn = cfg.EffectiveSpeedLimitKBps > 0;
                cfg.SpeedLimitKBps = kb;
                cfg.SpeedLimiterOnAtStart = _atStart.Checked;
                cfg.Save();
            };
            Button cancel = DownloadDlgUi.Btn(this, "Cancelar", 373, 109, 75, DialogResult.Cancel);
            AcceptButton = ok; CancelButton = cancel;
            EndLayout();
        }
    }
}
