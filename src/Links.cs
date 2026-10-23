using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace IDMClone
{
    // Un enlace para añadir (de la página del navegador, del portapapeles, de un lote o de un archivo).
    public class LinkEntry
    {
        public string Url, Text, Referrer, Cookies, UserAgent, PostData, LoginUser, LoginPassword;
        public LinkEntry() { }
        public LinkEntry(string url, string text) { Url = url; Text = text; }
    }

    // Formato de exportación de IDM (.ef2): un bloque por descarga entre "<" y ">", con la dirección
    // y, si hay, "referer:", "User-Agent:", "cookie:" y "postdata:".
    public static class Ef2
    {
        public static string Export(IEnumerable<DownloadItem> items)
        {
            StringBuilder sb = new StringBuilder();
            foreach (DownloadItem it in items)
            {
                if (it == null || it.Pending || string.IsNullOrEmpty(it.Url)) continue;
                sb.Append("<\r\n").Append(it.Url).Append("\r\n");
                if (!string.IsNullOrEmpty(it.Referrer)) sb.Append("referer: ").Append(it.Referrer).Append("\r\n");
                if (!string.IsNullOrEmpty(it.UserAgentOverride)) sb.Append("User-Agent: ").Append(it.UserAgentOverride).Append("\r\n");
                if (!string.IsNullOrEmpty(it.Cookies)) sb.Append("cookie: ").Append(it.Cookies).Append("\r\n");
                if (!string.IsNullOrEmpty(it.PostData)) sb.Append("postdata: ").Append(it.PostData).Append("\r\n");
                sb.Append(">\r\n");
            }
            return sb.ToString();
        }

        public static List<LinkEntry> Parse(string text)
        {
            List<LinkEntry> r = new List<LinkEntry>();
            LinkEntry cur = null;
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line == "<") { cur = new LinkEntry(); continue; }
                if (line == ">") { if (cur != null && Util.LooksLikeUrl(cur.Url)) r.Add(cur); cur = null; continue; }
                if (cur == null || line.Length == 0) continue;
                if (cur.Url == null) { cur.Url = line; continue; }
                int c = line.IndexOf(':');
                if (c <= 0) continue;
                string k = line.Substring(0, c).Trim().ToLowerInvariant(), v = line.Substring(c + 1).Trim();
                if (k == "referer") cur.Referrer = v;
                else if (k == "user-agent") cur.UserAgent = v;
                else if (k == "cookie") cur.Cookies = v;
                else if (k == "postdata") cur.PostData = v;
                else if (k == "username") cur.LoginUser = v;
                else if (k == "password") cur.LoginPassword = v;
            }
            return r;
        }
    }

    // Lo que se arrastra desde el navegador u otro programa: una o varias direcciones.
    public static class DropData
    {
        public static bool HasLinks(IDataObject d)
        {
            return d != null && (d.GetDataPresent("UniformResourceLocatorW") || d.GetDataPresent(DataFormats.UnicodeText) || d.GetDataPresent(DataFormats.Text));
        }

        public static List<string> Links(IDataObject d)
        {
            List<string> urls = new List<string>();
            try
            {
                if (d.GetDataPresent("UniformResourceLocatorW"))
                {
                    MemoryStream ms = d.GetData("UniformResourceLocatorW") as MemoryStream;
                    if (ms != null) { string u = Encoding.Unicode.GetString(ms.ToArray()).TrimEnd('\0').Trim(); if (Util.LooksLikeUrl(u)) urls.Add(u); }
                }
                if (urls.Count == 0)
                {
                    string t = d.GetData(DataFormats.UnicodeText) as string ?? d.GetData(DataFormats.Text) as string;
                    if (t != null) urls.AddRange(Util.ExtractUrls(t));
                }
            }
            catch { }
            return urls;
        }
    }

    // "Descargar todos los enlaces con IDM": lista de enlaces con casillas, filtros y dónde guardarlos.
    public class LinkListForm : DialogBase
    {
        readonly AppConfig _cfg;
        readonly DownloadManager _mgr;
        readonly List<LinkEntry> _all;
        readonly Dictionary<LinkEntry, bool> _checked = new Dictionary<LinkEntry, bool>();
        ListView _list;
        CheckBox _hideHtml, _hideDup, _hideImages, _useText, _startQueue;
        ComboBox _type, _queue;
        RadioButton _byCategory, _oneFolder;
        TextBox _folder;
        readonly List<QueueDef> _queues = new List<QueueDef>();
        bool _filling;
        public readonly List<DownloadItem> Added = new List<DownloadItem>();

        static readonly string[] HtmlExt = { "", "HTM", "HTML", "SHTML", "PHP", "ASP", "ASPX", "JSP", "CGI", "XHTML" };
        static readonly string[] ImageExt = { "PNG", "JPG", "JPEG", "GIF", "WEBP", "BMP", "SVG", "ICO", "AVIF" };

        public LinkListForm(AppConfig cfg, DownloadManager mgr, List<LinkEntry> links, string title)
        {
            _cfg = cfg; _mgr = mgr; _all = links ?? new List<LinkEntry>();
            Text = title ?? "Descargar todos los enlaces con IDMClone";
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 520);

            Label help = AddLabel("Marca los enlaces que quieres añadir a la lista de descargas de IDMClone y pulsa Aceptar.", 12, 8, 736);
            _list = new ListView(); _list.SetBounds(12, 36, 736, 250); _list.View = View.Details; _list.CheckBoxes = true;
            _list.FullRowSelect = true; _list.HideSelection = false;
            _list.Columns.Add("Dirección", Dpi.Px(400)); _list.Columns.Add("Tipo de archivo", Dpi.Px(90)); _list.Columns.Add("Texto del enlace", Dpi.Px(220));
            Controls.Add(_list);
            _list.ItemChecked += delegate(object s, ItemCheckedEventArgs e) { if (!_filling) _checked[(LinkEntry)e.Item.Tag] = e.Item.Checked; };

            _hideHtml = Chk("Ocultar archivos html", 12, 294, 200, true);
            _hideDup = Chk("Ocultar archivos repetidos", 216, 294, 200, true);
            _hideImages = Chk("Ocultar las imágenes", 420, 294, 180, false);
            Button all = AddButton("Marcar todos", 12, 324, 120, DialogResult.None);
            all.Click += delegate { SetAll(true); };
            Button none = AddButton("Desmarcar todos", 138, 324, 120, DialogResult.None);
            none.Click += delegate { SetAll(false); };
            AddLabel("Marcar los de tipo:", 276, 326, 130);
            _type = new ComboBox(); _type.DropDownStyle = ComboBoxStyle.DropDownList; _type.SetBounds(410, 326, 110, 23); Controls.Add(_type);
            Button markType = AddButton("Marcar", 526, 324, 80, DialogResult.None);
            markType.Click += delegate { MarkType(); };
            _useText = Chk("Usar los textos de los enlaces como descripciones de las descargas", 12, 360, 600, false);

            AddLabel("Guardar en:", 12, 392, 100);
            _byCategory = new RadioButton(); _byCategory.Text = "Cada archivo en la carpeta de su categoría"; _byCategory.SetBounds(116, 392, 400, 23); _byCategory.Checked = true; Controls.Add(_byCategory);
            _oneFolder = new RadioButton(); _oneFolder.Text = "Todos en esta carpeta:"; _oneFolder.SetBounds(116, 418, 170, 23); Controls.Add(_oneFolder);
            _folder = new TextBox(); _folder.SetBounds(290, 418, 412, 23); _folder.Text = cfg.DefaultFolder; Controls.Add(_folder);
            Button browse = AddButton("...", 708, 417, 40, DialogResult.None);
            browse.Click += delegate
            {
                using (FolderBrowserDialog f = new FolderBrowserDialog())
                {
                    if (Directory.Exists(_folder.Text)) f.SelectedPath = _folder.Text;
                    if (f.ShowDialog(this) == DialogResult.OK) { _folder.Text = f.SelectedPath; _oneFolder.Checked = true; }
                }
            };
            Label queueL = AddLabel("Añadir a la cola:", 12, 452, 100);
            _queue = new ComboBox(); _queue.DropDownStyle = ComboBoxStyle.DropDownList; _queue.SetBounds(116, 452, 220, 23); Controls.Add(_queue);
            foreach (QueueDef q in mgr.Queues.Queues) { _queues.Add(q); _queue.Items.Add(Lang.T(MainForm.QueueLabel(q))); }
            _queue.Items.Add("Ninguna: empezar a descargar ya");
            _queue.SelectedIndex = 0;
            _startQueue = Chk("Iniciar procesamiento de la cola", 344, 452, 260, false);
            _queue.SelectedIndexChanged += delegate { _startQueue.Enabled = _queue.SelectedIndex < _queues.Count; };
            // Opciones > Descargas > "Mostrar panel de selección de colas al añadir descargas por lotes": sin él,
            // los enlaces van a la cola principal sin preguntar (y sin empezar).
            if (!cfg.ShowQueuePickerBatch) queueL.Visible = _queue.Visible = _startQueue.Visible = false;

            Button ok = AddButton("Aceptar", 562, 484, 90, DialogResult.OK);
            ok.Click += delegate { if (!AddChecked()) DialogResult = DialogResult.None; };
            Button cancel = AddButton("Cancelar", 658, 484, 90, DialogResult.Cancel);
            AcceptButton = ok; CancelButton = cancel;

            // Marcados al principio: los tipos que IDMClone descarga (Opciones > Tipos de archivo).
            List<string> exts = new List<string>((cfg.CaptureExts ?? "").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            SortedSet<string> types = new SortedSet<string>();
            foreach (LinkEntry l in _all)
            {
                string ext = ExtOf(l.Url);
                bool known = false;
                foreach (string e in exts) if (Util.WildcardMatch(ext, e)) { known = true; break; }
                _checked[l] = known;
                if (ext.Length > 0) types.Add(ext);
            }
            foreach (string t in types) _type.Items.Add(t);
            if (_type.Items.Count > 0) _type.SelectedIndex = 0;
            _hideHtml.CheckedChanged += delegate { Fill(); };
            _hideDup.CheckedChanged += delegate { Fill(); };
            _hideImages.CheckedChanged += delegate { Fill(); };
            Fill();
            EndLayout();
        }

        CheckBox Chk(string text, int x, int y, int w, bool on)
        {
            CheckBox c = new CheckBox(); c.Text = text; c.Checked = on; c.SetBounds(x, y, w, 23); Controls.Add(c); return c;
        }

        public static string ExtOf(string url)
        {
            string name = Util.FileNameFromUrl(url) ?? "";
            int dot = name.LastIndexOf('.');
            return dot > 0 && dot < name.Length - 1 ? name.Substring(dot + 1).ToUpperInvariant() : "";
        }

        public int VisibleCount { get { return _list.Items.Count; } }
        public int CheckedCount { get { return _list.CheckedItems.Count; } }

        void Fill()
        {
            _filling = true;
            _list.BeginUpdate();
            _list.Items.Clear();
            HashSet<string> seen = new HashSet<string>();
            foreach (LinkEntry l in _all)
            {
                string ext = ExtOf(l.Url);
                if (_hideHtml.Checked && Array.IndexOf(HtmlExt, ext) >= 0) continue;
                if (_hideImages.Checked && Array.IndexOf(ImageExt, ext) >= 0) continue;
                if (_hideDup.Checked && !seen.Add(l.Url)) continue;
                ListViewItem li = new ListViewItem(l.Url);
                li.SubItems.Add(ext);
                li.SubItems.Add(l.Text ?? "");
                li.Tag = l;
                li.Checked = _checked[l];
                _list.Items.Add(li);
            }
            _list.EndUpdate();
            _filling = false;
        }

        public void SetAll(bool on) { foreach (ListViewItem li in _list.Items) li.Checked = on; }

        void MarkType()
        {
            string t = _type.SelectedItem as string;
            if (t == null) return;
            foreach (ListViewItem li in _list.Items) if (li.SubItems[1].Text == t) li.Checked = true;
        }

        public void UseOneFolder(string folder) { _oneFolder.Checked = true; _folder.Text = folder; }
        public void SelectQueue(int index) { _queue.SelectedIndex = index; }

        public bool AddChecked()
        {
            List<LinkEntry> pick = new List<LinkEntry>();
            foreach (ListViewItem li in _list.CheckedItems) pick.Add((LinkEntry)li.Tag);
            if (pick.Count == 0)
            {
                Msg.Show(this, "No has marcado ningún archivo para descargar. Si no quieres descargar nada, pulsa Cancelar.", "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            string folder = null;
            if (_oneFolder.Checked)
            {
                folder = _folder.Text.Trim();
                try { Directory.CreateDirectory(folder); }
                catch (Exception ex) { Msg.Show(this, "No se puede usar la carpeta: " + ex.Message, "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            }
            bool toQueue = _queue.SelectedIndex < _queues.Count;
            int queue = toQueue ? _queues[_queue.SelectedIndex].Id : DownloadItem.NoQueue;
            _mgr.BeginBatch();
            try
            {
                foreach (LinkEntry l in pick)
                {
                    AddOptions o = new AddOptions();
                    o.Url = l.Url; o.Folder = folder; o.Referrer = l.Referrer; o.Cookies = l.Cookies; o.UserAgent = l.UserAgent;
                    o.PostData = l.PostData; o.LoginUser = l.LoginUser; o.LoginPassword = l.LoginPassword;
                    if (_useText.Checked && !string.IsNullOrEmpty(l.Text)) o.Description = l.Text;
                    o.Queue = queue;
                    o.StartNow = !toQueue;
                    try { Added.Add(_mgr.Add(o)); } catch { }
                }
            }
            finally { _mgr.EndBatch(); }
            if (toQueue && _startQueue.Checked) _mgr.StartQueue(queue);
            return true;
        }
    }

    // "Descargar archivo por lotes" (IDM, captura 19b): una dirección con * y el rango que lo sustituye
    // (números con su tamaño de comodín, o letras), con vista previa del primero, el segundo y el último.
    public class BatchForm : DialogBase
    {
        ComboBox _url;
        NumericUpDown _from, _to, _width;
        DomainUpDown _fromL, _toL;
        TextBox _user, _pass, _first, _second, _last;
        RadioButton _numbers, _letters;
        CheckBox _auth;
        Label _widthL, _userL, _passL;
        public List<LinkEntry> Result;

        public BatchForm(string preset)
        {
            Text = "Descargar archivo por lotes";
            ClientSize = new Size(464, 378);
            OptUi.Lbl(this, "Se pueden añadir archivos secuenciales como img001.jpg, img002.jpg, img100.jpg, etc a la cola de descarga de IDMClone. Usa el asterisco (*) de comodín como patrón para el nombre del archivo.\r\nPor ejemplo: http://www.ejemplo.com/imagenes/img*.jpg", 10, 8, 444, 58);
            OptUi.Lbl(this, "Dirección", 10, 74, 60);
            _url = OptUi.Add(this, new ComboBox(), 72, 72, 382, 21);
            _url.Text = preset ?? "http://";

            GroupBox g = OptUi.Group(this, "Reemplazar asterisco por", 10, 100, 444, 82);
            _numbers = OptUi.Radio(g, "Números", 12, 20, 110); _numbers.Checked = true;
            _letters = OptUi.Radio(g, "Letras", 130, 20, 110);
            OptUi.Lbl(g, "Desde:", 10, 52, 44);
            _from = OptUi.Num(g, 0, int.MaxValue, 0, 58, 50, 62);
            _fromL = Letters(g, "a", 58, 50);
            OptUi.Lbl(g, "Hasta:", 146, 52, 44);
            _to = OptUi.Num(g, 0, int.MaxValue, 100, 194, 50, 62);
            _toL = Letters(g, "z", 194, 50);
            _widthL = OptUi.LblRight(g, "Tamaño comodín", 272, 52, 110);
            _width = OptUi.Num(g, 1, 9, 2, 388, 50, 46);

            GroupBox a = OptUi.CheckGroup(this, out _auth, "usar autorización", 10, 190, 444, 52);
            _userL = OptUi.LblRight(a, "Usuario", 8, 22, 60);
            _user = OptUi.Box(a, "", 74, 20, 120);
            _passL = OptUi.LblRight(a, "Contraseña", 206, 22, 90);
            _pass = OptUi.Box(a, "", 302, 20, 130); _pass.UseSystemPasswordChar = true;

            OptUi.Lbl(this, "Primer archivo:", 10, 254, 92);
            _first = Preview(254);
            OptUi.Lbl(this, "Segundo", 10, 278, 92);
            _second = Preview(278);
            OptUi.Lbl(this, "...", 40, 296, 30, 14);
            OptUi.Lbl(this, "Último archivo:", 10, 312, 92);
            _last = Preview(312);

            Button ok = OptUi.Btn(this, "Aceptar", 290, 345); ok.DialogResult = DialogResult.OK;
            ok.Click += delegate { if (!Build()) DialogResult = DialogResult.None; };
            Button cancel = OptUi.Btn(this, "Cancelar", 378, 345); cancel.DialogResult = DialogResult.Cancel;
            AcceptButton = ok; CancelButton = cancel;
            EventHandler upd = delegate { UpdateMode(); ShowPreview(); };
            _url.TextChanged += upd; _from.ValueChanged += upd; _to.ValueChanged += upd; _width.ValueChanged += upd;
            _fromL.SelectedItemChanged += upd; _toL.SelectedItemChanged += upd; _numbers.CheckedChanged += upd;
            _auth.CheckedChanged += delegate { OptUi.Enable(_auth.Checked, _userL, _user, _passL, _pass); };
            OptUi.Enable(false, _userL, _user, _passL, _pass);
            UpdateMode(); ShowPreview();
            ActiveControl = _url;
            EndLayout();
        }

        static DomainUpDown Letters(Control parent, string value, int x, int y)
        {
            DomainUpDown d = OptUi.Add(parent, new DomainUpDown(), x, y, 62, 20);
            for (char c = 'z'; c >= 'a'; c--) d.Items.Add(c.ToString());
            d.ReadOnly = true; d.Wrap = false; d.Tag = Lang.Keep;
            d.SelectedItem = value;
            return d;
        }

        TextBox Preview(int y)
        {
            TextBox t = OptUi.Box(this, "", 104, y - 2, 350); t.ReadOnly = true; return t;
        }

        void UpdateMode()
        {
            bool n = _numbers.Checked;
            _from.Visible = _to.Visible = n;
            _fromL.Visible = _toL.Visible = !n;
            _width.Enabled = _widthL.Enabled = n;
        }

        string UrlText { get { return (_url.Text ?? "").Trim(); } }

        public List<string> Expand()
        {
            return _numbers.Checked ? Util.ExpandBatch(UrlText, ((long)_from.Value).ToString(), ((long)_to.Value).ToString(), (int)_width.Value)
                                    : Util.ExpandBatch(UrlText, (string)_fromL.SelectedItem, (string)_toL.SelectedItem, 1);
        }

        void ShowPreview()
        {
            List<string> l = Expand();
            _first.Text = l.Count > 0 ? l[0] : "";
            _second.Text = l.Count > 1 ? l[1] : "";
            _last.Text = l.Count > 0 ? l[l.Count - 1] : "";
        }

        public void Set(string url, string from, string to, int width)
        {
            _url.Text = url; _numbers.Checked = true;
            long f, t;
            if (long.TryParse(from, out f)) _from.Value = Math.Max(0, Math.Min(int.MaxValue, f));
            if (long.TryParse(to, out t)) _to.Value = Math.Max(0, Math.Min(int.MaxValue, t));
            _width.Value = Math.Max(1, Math.Min(9, width));
            ShowPreview();
        }

        public bool Build()
        {
            List<string> urls = Expand();
            if (urls.Count == 0 || !Util.LooksLikeUrl(urls[0]))
            {
                Msg.Show(this, "Escribe una dirección válida que lleve un * y un rango correcto.", "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            Result = new List<LinkEntry>();
            foreach (string u in urls)
            {
                LinkEntry e = new LinkEntry(u, "");
                if (_auth.Checked && _user.Text.Trim().Length > 0) { e.LoginUser = _user.Text.Trim(); e.LoginPassword = _pass.Text; }
                Result.Add(e);
            }
            return true;
        }

        // Tareas > "Añadir descarga por lotes desde portapapeles" sin direcciones en el portapapeles (IDM, captura 73).
        public static void NoClipboardLinks(IWin32Window owner)
        {
            Msg.Show(owner, "¡No se encuentra ninguna dirección web en el portapapeles!", "Descarga por lotes desde portapapeles", MessageBoxButtons.OK, MessageBoxIcon.None);
        }
    }

    // Destino de arrastre flotante (Tareas > Mostrar destino de arrastre): suelta aquí enlaces del navegador.
    public class DropTargetForm : Form
    {
        public event Action<List<string>> Dropped;
        public event Action OpenMain, AddUrl, Closed2;
        Point _down;
        bool _dragging;

        public DropTargetForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            int sz = Dpi.Px(56);
            Size = new Size(sz, sz);
            BackColor = Color.FromArgb(235, 242, 252);
            AllowDrop = true;
            Text = "IDMClone";
            ContextMenuStrip cm = new ContextMenuStrip();
            cm.Items.Add("Agregar URL...", null, delegate { Action a = AddUrl; if (a != null) a(); });
            cm.Items.Add("Abrir IDMClone", null, delegate { Action a = OpenMain; if (a != null) a(); });
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add("Ocultar destino de arrastre", null, delegate { Hide(); Action a = Closed2; if (a != null) a(); });
            ContextMenuStrip = cm;
            DoubleClick += delegate { Action a = OpenMain; if (a != null) a(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (Pen p = new Pen(Color.FromArgb(33, 118, 214), 2f)) g.DrawRectangle(p, 1, 1, Width - 3, Height - 3);
            int s = Dpi.Px(32);
            // El icono de IDMClone (el "resume" de la barra ahora es una hoja con flecha, no sirve aquí).
            if (_icon == null) _icon = AppIcon.Sized(s);
            if (_icon != null) g.DrawIcon(_icon, new Rectangle((Width - s) / 2, (Height - s) / 2 - Dpi.Px(2), s, s));
        }
        static Icon _icon;

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _down = e.Location; _dragging = true; } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _dragging = false; }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) Location = new Point(Location.X + e.X - _down.X, Location.Y + e.Y - _down.Y);
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            e.Effect = DropData.HasLinks(e.Data) ? DragDropEffects.Copy | DragDropEffects.Link : DragDropEffects.None;
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            List<string> l = DropData.Links(e.Data);
            Action<List<string>> h = Dropped;
            if (l.Count > 0 && h != null) h(l);
        }
    }
}
