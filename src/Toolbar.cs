using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace IDMClone
{
    // Botones que puede llevar la barra de herramientas (IDM: clic derecho en la barra > "Personalizar...").
    public static class ToolbarCatalog
    {
        public class Button
        {
            public string Id, Text, Tip;
            public Button(string id, string text, string tip) { Id = id; Text = text; Tip = tip; }
        }

        public static readonly Button[] All =
        {
            new Button("add", "Agregar URL", "Agregar nueva dirección de descarga manualmente"),
            new Button("resume", "Reanudar", "Iniciar o reanudar la descarga de los archivos seleccionados"),
            new Button("stop", "Detener", "Detener la descarga de los archivos seleccionados"),
            new Button("stopall", "Detener todas", "Detener todas las descargas"),
            new Button("delete", "Eliminar", "Eliminar los archivos seleccionados de la lista"),
            new Button("deletecompleted", "Eliminar completadas", "Quitar de la lista todas las descargas completadas"),
            new Button("options", "Opciones", "Integración con el navegador, tipos de archivo, proxy, contraseñas y demás"),
            new Button("scheduler", "Planificador", "Planificador y colas de descarga"),
            new Button("startqueue", "Iniciar cola", "Iniciar la cola principal de descarga, o elegir una cola a iniciar en la lista desplegable"),
            new Button("stopqueue", "Detener cola", "Detener la cola principal de descarga, o elegir una cola a detener en la lista desplegable"),
            new Button("grabber", "Grabber", "Iniciar captura del sitio: descargar todas las imágenes, el audio o un sitio web completo"),
        };

        public const string Separator = "|";
        // Como la barra de IDM por defecto: sin separadores y sin Grabber (se puede añadir con Personalizar...).
        // El último botón de IDM ("Cuéntale a un amigo") es comercial y no se copia.
        public const string Default = "add,resume,stop,stopall,delete,deletecompleted,options,scheduler,startqueue,stopqueue";

        // Tamaño de los iconos: 32 px los grandes y 26 px los pequeños, según la escala. Medido en las capturas de
        // IDM (32-principal-neon-pequenos): el dibujo de los pequeños ocupa ~22-24 px y van cada ~40 px; nuestros
        // dibujos dejan un poco de margen, así que con 20 px se veían de ~16 px y apretados.
        public const int SmallIcon = 26;
        public static int IconSize(bool large) { return Dpi.Px(large ? 32 : SmallIcon); }

        // Relleno de cada botón pequeño: icono de 26 + 2 x 5 + el borde del botón = 40 px de un botón al siguiente, como IDM.
        public static Padding SmallPadding { get { return new Padding(Dpi.Px(5), Dpi.Px(3), Dpi.Px(5), Dpi.Px(3)); } }

        public static Button Find(string id)
        {
            foreach (Button b in All) if (b.Id == id) return b;
            return null;
        }

        // Lista guardada -> botones válidos en orden, sin repetir, sin separadores al principio, al final ni seguidos.
        public static List<string> Parse(string saved)
        {
            List<string> r = new List<string>();
            HashSet<string> seen = new HashSet<string>();
            foreach (string raw in (string.IsNullOrEmpty(saved) ? Default : saved).Split(','))
            {
                string id = raw.Trim();
                if (id == Separator) { if (r.Count > 0 && r[r.Count - 1] != Separator) r.Add(id); continue; }
                if (Find(id) != null && seen.Add(id)) r.Add(id);
            }
            while (r.Count > 0 && r[r.Count - 1] == Separator) r.RemoveAt(r.Count - 1);
            if (r.Count == 0 && !string.IsNullOrEmpty(saved)) return Parse(null); // lista vacía o rota: la de siempre
            return r;
        }
    }

    // "Personalizar la barra de herramientas", como el de Windows que usa IDM: botones disponibles a la
    // izquierda, los de la barra a la derecha (iconos de 32 px con su texto), Agregar/Quitar en medio y
    // Cerrar, Restablecer, Subir y Bajar a la derecha. Los cambios se quedan al cerrar (no hay Cancelar).
    // Al final de la lista de la derecha hay un hueco ("Separador" en gris) para poder añadir al final:
    // "Agregar ->" pone el botón delante del elemento elegido, como en Windows.
    public class ToolbarCustomizeForm : DialogBase
    {
        public const string EndSlot = ""; // hueco del final de la lista de la barra (no es un botón)

        ListBox _avail, _bar;
        System.Windows.Forms.Button _add, _remove, _up, _down;
        string _style;
        public List<string> Result { get; private set; }

        public ToolbarCustomizeForm(List<string> current, string style)
        {
            _style = style;
            Text = "Personalizar la barra de herramientas";
            ClientSize = new Size(543, 204);
            Label left = AddLabel("Botones disponibles:", 10, 4, 170); left.AutoSize = true; left.Top = 8;
            Label right = AddLabel("Botones de la barra de herramientas:", 272, 4, 190); right.AutoSize = true; right.Top = 8;
            _avail = MakeList(10, 25);
            _bar = MakeList(272, 25);
            _add = Btn("Agregar ->", 189, 82, 76);
            _remove = Btn("<- Quitar", 189, 112, 76);
            System.Windows.Forms.Button close = Btn("Cerrar", 464, 25, 72);
            System.Windows.Forms.Button reset = Btn("Restablecer", 464, 53, 72);
            _up = Btn("Subir", 464, 140, 72);
            _down = Btn("Bajar", 464, 168, 72);
            close.DialogResult = DialogResult.OK;
            AcceptButton = close; CancelButton = close;

            LoadButtons(current);
            _add.Click += delegate { AddSelected(); };
            _remove.Click += delegate { RemoveSelected(); };
            _avail.DoubleClick += delegate { AddSelected(); };
            _bar.DoubleClick += delegate { RemoveSelected(); };
            _up.Click += delegate { MoveSelected(-1); };
            _down.Click += delegate { MoveSelected(1); };
            reset.Click += delegate { LoadButtons(ToolbarCatalog.Parse(null)); };
            _avail.SelectedIndexChanged += delegate { UpdateButtons(); };
            _bar.SelectedIndexChanged += delegate { UpdateButtons(); };
            // Como en Windows, cerrar (con Cerrar, Esc o la X) conserva los cambios.
            FormClosing += delegate { Result = Collect(); DialogResult = DialogResult.OK; };
            UpdateButtons();
            EndLayout();
        }

        System.Windows.Forms.Button Btn(string text, int x, int y, int w)
        {
            System.Windows.Forms.Button b = AddButton(text, x, y, w, DialogResult.None);
            b.Height = 23;
            return b;
        }

        ListBox MakeList(int x, int y)
        {
            ListBox l = new ListBox();
            l.SetBounds(x, y, 172, 170);
            l.DrawMode = DrawMode.OwnerDrawFixed;
            l.ItemHeight = Math.Min(255, Math.Max(Dpi.Px(42), Dpi.Px(32) + 8));
            l.IntegralHeight = false;
            l.BackColor = SystemColors.Window;
            l.DrawItem += DrawButton;
            Controls.Add(l);
            return l;
        }

        void DrawButton(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            ListBox l = (ListBox)sender;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            Rectangle r = e.Bounds;
            using (SolidBrush b = new SolidBrush(sel ? Theme.Selection : l.BackColor)) e.Graphics.FillRectangle(b, r);
            string id = (string)l.Items[e.Index];
            Color fore = sel ? Color.White : l.ForeColor;
            if (id == ToolbarCatalog.Separator || id == EndSlot)
            {
                // Como Windows: el separador es un hueco con su nombre en medio; el del final, en gris.
                if (id == EndSlot) fore = sel ? Color.FromArgb(0xB4, 0xC8, 0xE6) : SystemColors.GrayText;
                TextRenderer.DrawText(e.Graphics, Lang.T("Separador"), l.Font, r, fore,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            else
            {
                ToolbarCatalog.Button b = ToolbarCatalog.Find(id);
                int icon = Dpi.Px(32), pad = Dpi.Px(8);
                if (b != null) e.Graphics.DrawImage(Glyphs.Get(id, icon, _style), r.X + pad, r.Y + (r.Height - icon) / 2, icon, icon);
                int tx = r.X + pad + icon + Dpi.Px(14);
                TextRenderer.DrawText(e.Graphics, b != null ? Lang.T(b.Text) : id, l.Font, new Rectangle(tx, r.Y, Math.Max(1, r.Right - tx - 2), r.Height), fore,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
            if ((e.State & DrawItemState.Focus) != 0 && (e.State & DrawItemState.NoFocusRect) == 0) ControlPaint.DrawFocusRectangle(e.Graphics, r, fore, sel ? Theme.Selection : l.BackColor);
        }

        void LoadButtons(List<string> current)
        {
            _bar.BeginUpdate();
            _bar.Items.Clear();
            foreach (string id in current) if (id != EndSlot) _bar.Items.Add(id);
            _bar.Items.Add(EndSlot);
            _bar.SelectedIndex = _bar.Items.Count - 1;
            _bar.EndUpdate();
            RefreshAvailable();
        }

        void RefreshAvailable()
        {
            _avail.BeginUpdate();
            _avail.Items.Clear();
            _avail.Items.Add(ToolbarCatalog.Separator); // siempre se puede añadir otro separador
            foreach (ToolbarCatalog.Button b in ToolbarCatalog.All) if (!_bar.Items.Contains(b.Id)) _avail.Items.Add(b.Id);
            _avail.SelectedIndex = _avail.Items.Count > 1 ? 1 : 0;
            _avail.EndUpdate();
            UpdateButtons();
        }

        int EndIndex { get { return _bar.Items.Count - 1; } }

        void UpdateButtons()
        {
            if (_up == null) return;
            int i = _bar.SelectedIndex;
            bool real = i >= 0 && i < EndIndex;
            _add.Enabled = _avail.SelectedIndex >= 0;
            _remove.Enabled = real;
            _up.Enabled = real && i > 0;
            _down.Enabled = real && i < EndIndex - 1;
        }

        // Pone el botón elegido a la izquierda delante del elegido a la derecha (o al final).
        public void AddSelected()
        {
            if (_avail.SelectedItem == null) return;
            string id = (string)_avail.SelectedItem;
            int at = _bar.SelectedIndex >= 0 ? _bar.SelectedIndex : EndIndex;
            _bar.Items.Insert(at, id);
            _bar.SelectedIndex = at + 1; // la selección sigue en el mismo elemento, así se añaden en orden
            RefreshAvailable();
        }

        public void RemoveSelected()
        {
            int i = _bar.SelectedIndex;
            if (i < 0 || i >= EndIndex) return;
            _bar.Items.RemoveAt(i);
            _bar.SelectedIndex = Math.Min(i, EndIndex);
            RefreshAvailable();
        }

        public void MoveSelected(int delta)
        {
            int i = _bar.SelectedIndex, j = i + delta;
            if (i < 0 || i >= EndIndex || j < 0 || j >= EndIndex) return;
            object o = _bar.Items[i];
            _bar.Items.RemoveAt(i);
            _bar.Items.Insert(j, o);
            _bar.SelectedIndex = j;
        }

        public List<string> Collect()
        {
            List<string> r = new List<string>();
            foreach (object o in _bar.Items) if ((string)o != EndSlot) r.Add((string)o);
            return ToolbarCatalog.Parse(string.Join(",", r.ToArray()));
        }

        // Para las pruebas.
        public ListBox Available { get { return _avail; } }
        public ListBox Current { get { return _bar; } }
    }

    // Aspecto de la barra de IDM: fondo #F0F0F0 liso y sin bordes, botón bajo el ratón en azul muy claro,
    // pulsado #CCE8FF con borde #99D1FF, y los iconos de los botones deshabilitados en gris claro.
    // Uso: toolStrip.Renderer = new ToolbarRenderer();
    public class ToolbarRenderer : ToolStripSystemRenderer
    {
        static readonly Color HotBack = Color.FromArgb(0xE5, 0xF3, 0xFF), HotBorder = Color.FromArgb(0xCC, 0xE8, 0xFF);
        static readonly Color DownBack = Color.FromArgb(0xCC, 0xE8, 0xFF), DownBorder = Color.FromArgb(0x99, 0xD1, 0xFF);

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is ToolStripDropDown) { base.OnRenderToolStripBackground(e); return; }
            using (SolidBrush b = new SolidBrush(Theme.DialogBack)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is ToolStripDropDown) base.OnRenderToolStripBorder(e); // la barra en sí, sin borde
        }

        static void Back(Graphics g, Rectangle r, bool pressed, bool hot)
        {
            if (!pressed && !hot) return;
            using (SolidBrush b = new SolidBrush(pressed ? DownBack : HotBack)) g.FillRectangle(b, r);
            using (Pen p = new Pen(pressed ? DownBorder : HotBorder)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            ToolStripButton b = e.Item as ToolStripButton;
            bool on = b != null && b.Checked;
            Back(e.Graphics, new Rectangle(Point.Empty, e.Item.Size), e.Item.Enabled && (e.Item.Pressed || on), e.Item.Enabled && e.Item.Selected);
        }

        protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e)
        {
            Back(e.Graphics, new Rectangle(Point.Empty, e.Item.Size), e.Item.Enabled && e.Item.Pressed, e.Item.Enabled && e.Item.Selected);
        }

        protected override void OnRenderSplitButtonBackground(ToolStripItemRenderEventArgs e)
        {
            ToolStripSplitButton sb = e.Item as ToolStripSplitButton;
            if (sb == null) { base.OnRenderSplitButtonBackground(e); return; }
            Back(e.Graphics, new Rectangle(Point.Empty, sb.Size), sb.Enabled && (sb.ButtonPressed || sb.DropDownButtonPressed), sb.Enabled && sb.Selected);
            DrawArrow(new ToolStripArrowRenderEventArgs(e.Graphics, sb, sb.DropDownButtonBounds,
                sb.Enabled ? SystemColors.ControlText : SystemColors.GrayText, ArrowDirection.Down));
        }

        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            if (!e.Item.Enabled && e.Image != null && e.ToolStrip != null && !(e.ToolStrip is ToolStripDropDown))
            {
                e.Graphics.DrawImage(Glyphs.Disabled(e.Image), e.ImageRectangle);
                return;
            }
            base.OnRenderItemImage(e);
        }
    }
}
