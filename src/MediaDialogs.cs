using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace IDMClone
{
    // Opciones > General > "Panel de descarga de IDM para reproductores-web" > Editar...: "Personalizar panel de descarga
    // de IDM en los navegadores" (capturas 29b y 29c), con sus dos pestañas.
    public class MediaPanelForm : DialogBase
    {
        readonly AppConfig _cfg;
        TabControl _tabs;
        RadioButton _full, _mini, _selFull, _selMini, _selShow, _selHide, _selSites;
        ListView _types;
        CheckBox _noAuto, _protected, _merge, _srt;
        ListBox _sites;
        Button _minSize, _siteAdd, _siteDel;
        bool _show;          // "Mostrar el panel de descarga en los navegadores" (la casilla está en Opciones > General)
        string _exclude;     // Excepciones: sitios donde no se muestra el panel

        class TypeRow { public string Ext; public int MinKB; }

        public MediaPanelForm(AppConfig cfg)
        {
            _cfg = cfg;
            Text = "Personalizar panel de descarga de IDMClone en los navegadores";
            ClientSize = new Size(500, 528);
            _tabs = new TabControl(); _tabs.SetBounds(8, 8, 484, 482); Controls.Add(_tabs);
            TabPage web = Page("Para reproductores Web"), sel = Page("Para los archivos seleccionados");
            BuildWeb(web); BuildSelection(sel);

            Button ok = AddButton("Aceptar", 336, 497, 75, DialogResult.OK); ok.Height = 23;
            ok.Click += delegate { Apply(); };
            Button cancel = AddButton("Cancelar", 417, 497, 75, DialogResult.Cancel); cancel.Height = 23;
            AcceptButton = ok; CancelButton = cancel;

            _show = cfg.MediaPanel;
            _full.Checked = !cfg.MediaPanelMini; _mini.Checked = cfg.MediaPanelMini;
            FillTypes(cfg);
            _exclude = cfg.MediaPanelExcludeSites ?? "";
            _noAuto.Checked = cfg.MediaNoAutoCapture; _protected.Checked = cfg.MediaPanelProtected; _merge.Checked = cfg.MergeAudioVideo;
            _srt.Checked = cfg.SubtitlesToSrt;
            _selFull.Checked = !cfg.SelectionPanelMini; _selMini.Checked = cfg.SelectionPanelMini;
            _selShow.Checked = cfg.SelectionPanelMode == 0; _selHide.Checked = cfg.SelectionPanelMode == 1; _selSites.Checked = cfg.SelectionPanelMode == 2;
            foreach (string s in Words(cfg.SelectionPanelSites)) _sites.Items.Add(s);
            SitesUi();
            EndLayout();
        }

        TabPage Page(string text)
        {
            TabPage p = new TabPage(text); p.UseVisualStyleBackColor = true; _tabs.TabPages.Add(p); return p;
        }

        static T C<T>(Control p, T c, int x, int y, int w, int h) where T : Control { c.SetBounds(x, y, w, h); p.Controls.Add(c); return c; }
        static Label L(Control p, string text, int x, int y, int w, int h) { Label l = C(p, new Label(), x, y, w, h); l.Text = text; return l; }
        static Label Line(Control p, int x, int y, int w) { Label l = C(p, new Label(), x, y, w, 2); l.BorderStyle = BorderStyle.Fixed3D; return l; }
        static Button Btn(Control p, string text, int x, int y, int w) { Button b = C(p, new Button(), x, y, w, 23); b.Text = text; return b; }

        static PictureBox Preview(Control p, string text, bool mini, int x, int y, int w, int h)
        {
            PictureBox pb = C(p, new PictureBox(), x, y, w, h);
            pb.SizeMode = PictureBoxSizeMode.Zoom;
            pb.Image = GrabberArt.PanelPreview(Lang.T(text), mini, Dpi.Px(20));
            pb.Disposed += delegate { if (pb.Image != null) pb.Image.Dispose(); }; // el PictureBox no libera su imagen
            return pb;
        }

        void BuildWeb(TabPage p)
        {
            L(p, "IDMClone puede mostrar su panel de descarga en un reproductor-web de un navegador cuando detecta una solicitud multimedia del reproductor-web.", 8, 8, 456, 30);
            Line(p, 8, 42, 456);
            L(p, "Ver panel:", 8, 50, 120, 16);
            _full = C(p, new RadioButton(), 24, 70, 150, 20); _full.Text = "Modo completo";
            Preview(p, "Descargar este video", false, 196, 64, 256, 28);
            _mini = C(p, new RadioButton(), 24, 96, 150, 20); _mini.Text = "Modo mini";
            Preview(p, "", true, 404, 94, 48, 24);
            Line(p, 8, 124, 456);
            L(p, "Mostrar panel de descarga de IDMClone para los siguientes tipos de archivo:", 8, 130, 456, 16);
            _types = C(p, new ListView(), 36, 150, 232, 128);
            _types.View = View.Details; _types.CheckBoxes = true; _types.FullRowSelect = true; _types.HideSelection = false; _types.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _types.Columns.Add("Tipo de archivo", Dpi.Px(100)); _types.Columns.Add("Tamaño mínimo", Dpi.Px(104));
            Button add = Btn(p, "Agregar", 290, 150, 150);
            _minSize = Btn(p, "Tamaño mínimo", 290, 178, 150);
            Button all = Btn(p, "Seleccionar Todos", 290, 206, 150);
            Button none = Btn(p, "Borrar todo", 290, 234, 150);
            Button exc = Btn(p, "Excepciones", 290, 262, 150);
            _minSize.Enabled = false;
            _types.SelectedIndexChanged += delegate { _minSize.Enabled = _types.SelectedItems.Count > 0; };
            _types.DoubleClick += delegate { EditMinSize(); };
            add.Click += delegate { AddType(); };
            _minSize.Click += delegate { EditMinSize(); };
            all.Click += delegate { foreach (ListViewItem i in _types.Items) i.Checked = true; };
            none.Click += delegate { foreach (ListViewItem i in _types.Items) i.Checked = false; };
            exc.Click += delegate
            {
                using (MediaSitesForm f = new MediaSitesForm("Excepciones", "No mostrar el panel de descarga de IDMClone en los siguientes sitios:", _exclude))
                    if (f.ShowDialog(this) == DialogResult.OK) _exclude = f.Sites;
            };
            L(p, "Nota: si desmarcas un tipo de la lista superior y está en \"Opciones -> Tipos de archivos\", IDMClone descarga automáticamente esos archivos cuando un reproductor-web los pide (el navegador no deja cortar la reproducción, así que el reproductor sigue funcionando). Si deseas evitarlo, marca la casilla de abajo:", 8, 286, 456, 56);
            _noAuto = C(p, new CheckBox(), 64, 344, 400, 20); _noAuto.Text = "No capturar las descargas automáticamente de los reproductores-web";
            Line(p, 8, 370, 456);
            _protected = C(p, new CheckBox(), 24, 376, 440, 20); _protected.Text = "Mostrar el panel para los archivos protegidos que IDMClone no puede descargar";
            _merge = C(p, new CheckBox(), 24, 398, 440, 20); _merge.Text = "Mezclar secuencias de audio y vídeo en un solo archivo";
            _srt = C(p, new CheckBox(), 24, 420, 440, 20); _srt.Text = "Convertir los subtítulos WebVTT a SRT";
        }

        void BuildSelection(TabPage p)
        {
            L(p, "IDMClone puede mostrar el panel de descarga sobre una página web cuando se selecciona un texto que contiene enlaces de descarga.", 8, 8, 456, 30);
            Line(p, 8, 50, 456);
            L(p, "Ver panel:", 8, 58, 120, 16);
            _selFull = C(p, new RadioButton(), 24, 78, 150, 20); _selFull.Text = "Modo completo";
            Preview(p, "Descargar con IDMClone", false, 196, 72, 256, 28);
            _selMini = C(p, new RadioButton(), 24, 104, 150, 20); _selMini.Text = "Modo mini";
            Preview(p, "", true, 404, 102, 48, 24);
            Line(p, 8, 134, 456);
            _selShow = C(p, new RadioButton(), 24, 144, 440, 20); _selShow.Text = "Mostrar el panel de descarga para los enlaces seleccionados";
            _selHide = C(p, new RadioButton(), 24, 166, 440, 20); _selHide.Text = "No mostrar el panel de descarga para los enlaces seleccionados";
            _selSites = C(p, new RadioButton(), 24, 188, 440, 20); _selSites.Text = "Mostrar el panel solo para los enlaces seleccionados en estos sitios:";
            _sites = C(p, new ListBox(), 48, 214, 264, 110); _sites.IntegralHeight = false; _sites.Tag = Lang.Keep;
            _siteAdd = Btn(p, "Agregar", 330, 214, 82);
            _siteDel = Btn(p, "Eliminar", 330, 242, 82);
            EventHandler mode = delegate { SitesUi(); };
            _selShow.CheckedChanged += mode; _selHide.CheckedChanged += mode; _selSites.CheckedChanged += mode;
            _sites.SelectedIndexChanged += mode;
            _siteAdd.Click += delegate
            {
                string s = AskSite(this);
                if (s != null && !_sites.Items.Contains(s)) _sites.Items.Add(s);
            };
            _siteDel.Click += delegate { if (_sites.SelectedIndex >= 0) _sites.Items.RemoveAt(_sites.SelectedIndex); SitesUi(); };
            Label n = L(p, "Al pulsar el panel se abre la lista de los enlaces seleccionados para elegir cuáles descargar. Necesita la extensión de IDMClone 1.4 o posterior.", 8, 336, 456, 30);
            n.ForeColor = SystemColors.GrayText;
        }

        void SitesUi()
        {
            _sites.Enabled = _siteAdd.Enabled = _selSites.Checked;
            _siteDel.Enabled = _selSites.Checked && _sites.SelectedIndex >= 0;
        }

        internal static string AskSite(IWin32Window owner)
        {
            using (GrabberNameForm f = new GrabberNameForm("Agregar sitio", "Sitio (puedes usar el asterisco (*) como comodín):", ""))
            {
                if (f.ShowDialog(owner) != DialogResult.OK) return null;
                string s = f.Value.Trim().Replace(" ", "");
                return s.Length == 0 ? null : s;
            }
        }

        static List<string> Words(string s)
        {
            return new List<string>((s ?? "").Split(new char[] { ' ', '\r', '\n', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries));
        }

        // ------------------------------------------------------------------ tipos de archivo

        void FillTypes(AppConfig cfg)
        {
            _types.Items.Clear();
            // El mismo intérprete que usa el host nativo para mandar la lista a la extensión.
            foreach (AppConfig.MediaTypeRule r in AppConfig.ParseMediaTypes(cfg))
                if (Find(r.Ext) == null) AddRow(r.Ext, r.MinKB, r.On);
        }

        ListViewItem Find(string ext)
        {
            foreach (ListViewItem i in _types.Items) if (string.Equals(((TypeRow)i.Tag).Ext, ext, StringComparison.OrdinalIgnoreCase)) return i;
            return null;
        }

        void AddRow(string ext, int minKB, bool on)
        {
            TypeRow r = new TypeRow(); r.Ext = ext.ToLowerInvariant().TrimStart('.'); r.MinKB = minKB;
            ListViewItem i = new ListViewItem(new string[] { r.Ext.ToUpperInvariant(), SizeText(minKB) }); i.Tag = r; i.Checked = on;
            _types.Items.Add(i);
        }

        // Como IDM: "50.00 KB" (vacío si no hay mínimo).
        static string SizeText(int kb)
        {
            if (kb <= 0) return "";
            if (kb >= 1024) return (kb / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " MB";
            return kb.ToString(CultureInfo.InvariantCulture) + ".00 KB";
        }

        void SetMin(ListViewItem i, int kb)
        {
            TypeRow r = (TypeRow)i.Tag; r.MinKB = Math.Max(0, kb); i.SubItems[1].Text = SizeText(r.MinKB);
        }

        void AddType()
        {
            using (GrabberNameForm f = new GrabberNameForm("Agregar tipo de archivo", "Extensión del tipo de archivo (por ejemplo, mkv):", ""))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                string e = f.Value.Trim().TrimStart('*', '.').Replace(" ", "").ToLowerInvariant();
                if (e.Length == 0 || e.IndexOfAny(new char[] { ':', '-', '/', '\\' }) >= 0) return;
                ListViewItem old = Find(e);
                if (old != null) { old.Checked = true; old.Selected = true; old.EnsureVisible(); return; }
                AddRow(e, AppConfig.IsMediaAudioType(e) ? _cfg.MediaPanelMinAudioKB : _cfg.MediaPanelMinVideoKB, true);
                ListViewItem n = _types.Items[_types.Items.Count - 1]; n.Selected = true; n.EnsureVisible();
            }
        }

        void EditMinSize()
        {
            if (_types.SelectedItems.Count == 0) return;
            using (MediaMinSizeForm f = new MediaMinSizeForm(((TypeRow)_types.SelectedItems[0].Tag).MinKB))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                foreach (ListViewItem i in _types.SelectedItems) SetMin(i, f.KB);
            }
        }

        // ------------------------------------------------------------------ guardar

        // Para las pruebas: mostrar, modo mini, mínimos de vídeo y audio (en todos los tipos), sitios excluidos y mezcla.
        public void Set(bool show, bool mini, int minVideoKB, int minAudioKB, string exclude, bool merge)
        {
            _show = show; _mini.Checked = mini; _full.Checked = !mini;
            foreach (ListViewItem i in _types.Items) SetMin(i, AppConfig.IsMediaAudioType(((TypeRow)i.Tag).Ext) ? minAudioKB : minVideoKB);
            _exclude = exclude; _merge.Checked = merge;
        }

        public void Apply()
        {
            _cfg.MediaPanel = _show;
            _cfg.MediaPanelMini = _mini.Checked;
            // La extensión usa un mínimo para vídeo y otro para audio: el menor de los tipos marcados de cada clase
            // (así ningún tipo marcado se queda sin panel). La lista completa se guarda en MediaPanelTypes.
            int video = -1, audio = -1;
            List<string> list = new List<string>();
            foreach (ListViewItem i in _types.Items)
            {
                TypeRow r = (TypeRow)i.Tag;
                list.Add((i.Checked ? "" : "-") + r.Ext + (r.MinKB > 0 ? ":" + r.MinKB.ToString(CultureInfo.InvariantCulture) : ""));
                if (!i.Checked) continue;
                if (AppConfig.IsMediaAudioType(r.Ext)) audio = audio < 0 ? r.MinKB : Math.Min(audio, r.MinKB);
                else if (AppConfig.IsMediaVideoType(r.Ext)) video = video < 0 ? r.MinKB : Math.Min(video, r.MinKB);
            }
            if (video >= 0) _cfg.MediaPanelMinVideoKB = video;
            if (audio >= 0) _cfg.MediaPanelMinAudioKB = audio;
            _cfg.MediaPanelTypes = string.Join(" ", list.ToArray());
            _cfg.MediaPanelExcludeSites = string.Join(" ", Words(_exclude).ToArray());
            _cfg.MediaNoAutoCapture = _noAuto.Checked;
            _cfg.MediaPanelProtected = _protected.Checked;
            _cfg.MergeAudioVideo = _merge.Checked;
            _cfg.SubtitlesToSrt = _srt.Checked;
            _cfg.SelectionPanelMini = _selMini.Checked;
            _cfg.SelectionPanelMode = _selHide.Checked ? 1 : _selSites.Checked ? 2 : 0;
            List<string> sites = new List<string>();
            foreach (object o in _sites.Items) sites.Add((string)o);
            _cfg.SelectionPanelSites = string.Join(" ", sites.ToArray());
        }
    }

    // "Tamaño mínimo" de un tipo de archivo del panel.
    public class MediaMinSizeForm : DialogBase
    {
        readonly NumericUpDown _kb;
        public int KB { get { return (int)_kb.Value; } }

        public MediaMinSizeForm(int kb)
        {
            Text = "Tamaño mínimo";
            ClientSize = new Size(360, 104);
            Label l = AddLabel("No mostrar el panel si el archivo es más pequeño que (0 = sin mínimo):", 12, 8, 336);
            _kb = new NumericUpDown(); _kb.Maximum = 10000000; _kb.SetBounds(12, 36, 90, 21); _kb.Value = Math.Max(0, Math.Min(10000000, kb)); Controls.Add(_kb);
            AddLabel("KB", 108, 35, 40);
            Button ok = AddButton("Aceptar", 192, 70, 75, DialogResult.OK); ok.Height = 23;
            Button cancel = AddButton("Cancelar", 273, 70, 75, DialogResult.Cancel); cancel.Height = 23;
            AcceptButton = ok; CancelButton = cancel;
            EndLayout();
        }
    }

    // Lista de sitios (Excepciones del panel): Agregar, Eliminar, Aceptar (como la lista de excepciones de IDM).
    public class MediaSitesForm : DialogBase
    {
        readonly ListBox _l;
        public string Sites
        {
            get { List<string> r = new List<string>(); foreach (object o in _l.Items) r.Add((string)o); return string.Join(" ", r.ToArray()); }
        }

        public MediaSitesForm(string title, string prompt, string sites)
        {
            Text = title;
            ClientSize = new Size(380, 262);
            Label p = AddLabel(prompt, 12, 8, 356); p.Height = 30; p.TextAlign = ContentAlignment.TopLeft;
            _l = new ListBox(); _l.SetBounds(12, 40, 270, 210); _l.IntegralHeight = false; _l.Tag = Lang.Keep; Controls.Add(_l);
            foreach (string s in (sites ?? "").Split(new char[] { ' ', '\r', '\n', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)) if (!_l.Items.Contains(s)) _l.Items.Add(s);
            Button add = AddButton("Agregar", 292, 40, 76, DialogResult.None); add.Height = 23;
            Button del = AddButton("Eliminar", 292, 68, 76, DialogResult.None); del.Height = 23;
            Button ok = AddButton("Aceptar", 292, 199, 76, DialogResult.OK); ok.Height = 23;
            Button cancel = AddButton("Cancelar", 292, 227, 76, DialogResult.Cancel); cancel.Height = 23;
            AcceptButton = ok; CancelButton = cancel;
            add.Click += delegate { string s = MediaPanelForm.AskSite(this); if (s != null && !_l.Items.Contains(s)) _l.Items.Add(s); };
            del.Click += delegate { if (_l.SelectedIndex >= 0) _l.Items.RemoveAt(_l.SelectedIndex); };
            EndLayout();
        }
    }
}
