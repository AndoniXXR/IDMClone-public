using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace IDMClone
{
    // Piezas comunes de las ventanas de Opciones, Programador, categorías y lotes, con las medidas de IDM 6.43
    // (Tahoma 8, botones de 75x23, casillas de 17 px de alto, cajas de 20 px).
    public static class OptUi
    {
        // Títulos de las páginas: Tahoma 10 negrita (o la letra de la interfaz del idioma, ver Theme.UiFont).
        static Font _title;
        public static Font TitleFont
        {
            get
            {
                if (_title == null || _title.Name != Theme.UiFamily) _title = new Font(Theme.UiFamily, 9.75f, FontStyle.Bold, GraphicsUnit.Point);
                return _title;
            }
        }
        public static readonly Color NoteColor = SystemColors.GrayText;
        public static readonly Color BlueLabel = Color.FromArgb(0x00, 0x00, 0xFF);   // etiquetas azules de IDM

        public static T Add<T>(Control parent, T c, int x, int y, int w, int h) where T : Control
        {
            c.SetBounds(x, y, w, h); parent.Controls.Add(c); return c;
        }

        public static Label Lbl(Control parent, string text, int x, int y, int w) { return Lbl(parent, text, x, y, w, 16); }

        public static Label Lbl(Control parent, string text, int x, int y, int w, int h)
        {
            Label l = new Label(); l.Text = text; l.AutoSize = false;
            l.TextAlign = h > 20 ? ContentAlignment.TopLeft : ContentAlignment.MiddleLeft;
            return Add(parent, l, x, y, w, h);
        }

        // Etiqueta alineada a la derecha (las filas "texto ... [Editar...]" de IDM).
        public static Label LblRight(Control parent, string text, int x, int y, int w)
        {
            Label l = Lbl(parent, text, x, y, w, 16); l.TextAlign = ContentAlignment.MiddleRight; return l;
        }

        public static Label Note(Control parent, string text, int x, int y, int w, int h)
        {
            Label l = Lbl(parent, text, x, y, w, h); l.ForeColor = NoteColor; return l;
        }

        public static CheckBox Chk(Control parent, string text, int x, int y, int w) { return Chk(parent, text, x, y, w, 17); }

        public static CheckBox Chk(Control parent, string text, int x, int y, int w, int h)
        {
            CheckBox c = new CheckBox(); c.Text = text;
            if (h > 20) { c.CheckAlign = ContentAlignment.TopLeft; c.TextAlign = ContentAlignment.TopLeft; }
            return Add(parent, c, x, y, w, h);
        }

        public static RadioButton Radio(Control parent, string text, int x, int y, int w)
        {
            RadioButton r = new RadioButton(); r.Text = text; return Add(parent, r, x, y, w, 17);
        }

        public static Button Btn(Control parent, string text, int x, int y) { return Btn(parent, text, x, y, 75); }

        public static Button Btn(Control parent, string text, int x, int y, int w)
        {
            Button b = new Button(); b.Text = text; b.UseVisualStyleBackColor = true; return Add(parent, b, x, y, w, 23);
        }

        public static TextBox Box(Control parent, string text, int x, int y, int w)
        {
            TextBox t = new TextBox(); t.Text = text ?? ""; return Add(parent, t, x, y, w, 20);
        }

        public static NumericUpDown Num(Control parent, int min, int max, int value, int x, int y, int w)
        {
            NumericUpDown n = new NumericUpDown(); n.Minimum = min; n.Maximum = max;
            n.Value = value < min ? min : (value > max ? max : value);
            return Add(parent, n, x, y, w, 20);
        }

        public static ComboBox Combo(Control parent, int x, int y, int w, bool translate, params string[] items)
        {
            ComboBox c = new ComboBox(); c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Items.AddRange(items);
            if (translate) c.Tag = Lang.Mark;
            return Add(parent, c, x, y, w, 21);
        }

        // Línea grabada horizontal (separadores de IDM).
        public static Label Line(Control parent, int x, int y, int w)
        {
            Label l = new Label(); l.AutoSize = false; l.BorderStyle = BorderStyle.Fixed3D; return Add(parent, l, x, y, w, 2);
        }

        public static Label VLine(Control parent, int x, int y, int h)
        {
            Label l = new Label(); l.AutoSize = false; l.BorderStyle = BorderStyle.Fixed3D; return Add(parent, l, x, y, 2, h);
        }

        public static GroupBox Group(Control parent, string text, int x, int y, int w, int h)
        {
            GroupBox g = new GroupBox(); g.Text = text; return Add(parent, g, x, y, w, h);
        }

        // Recuadro cuyo título es una casilla (IDM: "Límites de descarga", "usar autorización").
        // El fondo de la casilla tapa la línea del recuadro: blanco en las pestañas, gris en los diálogos.
        public static GroupBox CheckGroup(Control parent, out CheckBox check, string text, int x, int y, int w, int h)
        {
            GroupBox g = Group(parent, "", x, y, w, h);
            check = new CheckBox(); check.Text = text;
            check.BackColor = parent is TabPage ? SystemColors.Window : SystemColors.Control;
            // Ancho del texto más largo entre el español y el traducido (el que pondrá Lang.Apply al abrir la ventana):
            // medido solo con el español, en japonés se cortaba "…を使".
            int tw = Math.Max(TextRenderer.MeasureText(text, Theme.UiFont).Width, TextRenderer.MeasureText(Lang.T(text), Theme.UiFont).Width);
            check.SetBounds(8, -1, Math.Min(w - 16, tw + 36), 17);
            g.Controls.Add(check);
            return g;
        }

        // Cabecera de cada página de Opciones: icono de 32 px a la izquierda y título sobre una línea.
        public static void Header(Control page, string icon, string title, bool bold, int width) { Header(page, icon, title, bold, width, width); }

        public static void Header(Control page, string icon, string title, bool bold, int width, int titleRight)
        {
            PictureBox pic = new PictureBox(); pic.SizeMode = PictureBoxSizeMode.Zoom;
            pic.Image = OptIcons.Page(icon, Dpi.Px(32));
            Add(page, pic, 6, 4, 32, 32);
            Label t = new Label(); t.AutoSize = false; t.Text = title; t.TextAlign = ContentAlignment.BottomRight;
            if (bold) t.Font = TitleFont;
            Add(page, t, 44, 4, titleRight - 44, 22);
            Line(page, 44, 28, width - 44);
        }

        // Activa/desactiva varios controles a la vez.
        public static void Enable(bool on, params Control[] cs) { foreach (Control c in cs) if (c != null) c.Enabled = on; }
    }

    // Ayuda de IDMClone (botones "Ayuda", F1 y menú Ayuda): páginas HTML locales de la carpeta help (help\en
    // con la interfaz en inglés), una por tema, abiertas con el navegador predeterminado como archivo local.
    // Nunca abre páginas web. Sin la carpeta help, el README de siempre en el Bloc de notas.
    public static class OptionsHelp
    {
        // Temas que tienen página (help\<tema>.html).
        public static readonly string[] Topics = {
            "index", "opciones-general", "opciones-tipos", "opciones-guardar", "opciones-descargas", "opciones-conexion",
            "opciones-proxy", "opciones-sesiones", "opciones-discado", "opciones-sonidos", "programador", "grabber", "progreso" };

        // Para las pruebas: recibe la ruta que se abriría (no se lanza nada) y la carpeta de ayuda a usar.
        public static Action<string> OpenHook;
        public static string DirOverride;

        // Página de Opciones (TabPage.Name de OptionsForm) -> tema.
        public static string TopicForOptionsPage(string pageId)
        {
            switch (pageId ?? "")
            {
                case "general": return "opciones-general";
                case "types": return "opciones-tipos";
                case "save": return "opciones-guardar";
                case "downloads": return "opciones-descargas";
                case "connection": return "opciones-conexion";
                case "proxy": return "opciones-proxy";
                case "logins": return "opciones-sesiones";
                case "dialup": return "opciones-discado";
                case "sounds": return "opciones-sonidos";
            }
            return "index";
        }

        // Tema según la ventana que pide la ayuda (Opciones: la página que se ve).
        public static string TopicFor(IWin32Window owner)
        {
            if (owner is SchedulerForm) return "programador";
            if (owner is ProgressForm) return "progreso";
            if (owner is GrabberForm) return "grabber";
            OptionsForm opt = owner as OptionsForm;
            if (opt != null)
            {
                TabControl tabs = FindTabs(opt);
                return TopicForOptionsPage(tabs != null && tabs.SelectedTab != null ? tabs.SelectedTab.Name : null);
            }
            return "index";
        }

        static TabControl FindTabs(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                TabControl t = c as TabControl ?? FindTabs(c);
                if (t != null) return t;
            }
            return null;
        }

        // Carpeta help: junto al programa (instalado) o, al ejecutar desde el código fuente, en la carpeta del
        // proyecto (IDMClone.exe en la raíz o testsin). null si no hay.
        public static string HelpDir()
        {
            if (!string.IsNullOrEmpty(DirOverride)) return Directory.Exists(DirOverride) ? DirOverride : null;
            try
            {
                string d = Path.GetDirectoryName(Application.ExecutablePath);
                for (int i = 0; i < 3 && !string.IsNullOrEmpty(d); i++)
                {
                    string h = Path.Combine(d, "help");
                    if (File.Exists(Path.Combine(h, "index.html"))) return h;
                    d = Path.GetDirectoryName(d);
                }
            }
            catch { }
            return null;
        }

        // Código del idioma de la interfaz ("en"), o null con el español del programa.
        static string LangCode()
        {
            if (Lang.Current == Lang.BuiltIn) return null;
            if (Lang.CurrentCode != null) return Lang.CurrentCode;
            try
            {
                foreach (Lang.LangFile l in Lang.Available())
                    if (l.Name == Lang.Current && !string.IsNullOrEmpty(l.Code)) return l.Code.Trim().ToLowerInvariant();
            }
            catch { }
            return string.Equals(Lang.Current, "English", StringComparison.OrdinalIgnoreCase) ? "en" : null;
        }

        // Página del tema (la del idioma si existe; si no, la inglesa para otros idiomas y la española; si el
        // tema no existe, el índice).
        public static string Resolve(string topic)
        {
            string dir = HelpDir();
            if (dir == null) return null;
            if (Array.IndexOf(Topics, topic) < 0) topic = "index";
            string code = LangCode();
            foreach (string t in new string[] { topic, "index" })
            {
                if (code != null && code.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                {
                    string loc = Path.Combine(Path.Combine(dir, code), t + ".html");
                    if (File.Exists(loc)) return loc;
                    // Otro idioma sin ayuda propia (chino, japonés...): mejor la inglesa que la española.
                    string en = Path.Combine(Path.Combine(dir, "en"), t + ".html");
                    if (code != "es" && File.Exists(en)) return en;
                }
                string es = Path.Combine(dir, t + ".html");
                if (File.Exists(es)) return es;
            }
            return null;
        }

        public static void Open(IWin32Window owner) { Open(owner, TopicFor(owner)); }

        public static void Open(IWin32Window owner, string topic)
        {
            string page = Resolve(topic);
            if (page != null)
            {
                if (OpenHook != null) { OpenHook(page); return; }
                try
                {
                    // Archivo local: lo abre el navegador predeterminado (o lo que el sistema tenga para .html).
                    ProcessStartInfo psi = new ProcessStartInfo(page);
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                    return;
                }
                catch { }
            }
            try
            {
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                foreach (string p in new string[] { Path.Combine(dir, "README.md"), Path.Combine(Path.GetDirectoryName(dir) ?? dir, "README.md") })
                {
                    if (!File.Exists(p)) continue;
                    if (OpenHook != null) { OpenHook(p); return; }
                    ProcessStartInfo psi = new ProcessStartInfo("notepad.exe", "\"" + p + "\"");
                    psi.UseShellExecute = false;
                    Process.Start(psi);
                    return;
                }
            }
            catch { }
            if (OpenHook != null) { OpenHook(null); return; }
            Msg.Show(owner, "No se encuentra la ayuda de IDMClone (carpeta help junto al programa).", "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    // Iconos propios de las páginas de Opciones y del Programador, dibujados en código con el estilo de los
    // de IDM (dibujos de colores con contorno, 32 px las cabeceras y 16 px las listas). No se copian los de IDM.
    public static class OptIcons
    {
        static readonly System.Collections.Generic.Dictionary<string, Bitmap> _cache = new System.Collections.Generic.Dictionary<string, Bitmap>();

        public static Bitmap Page(string name, int size)
        {
            string key = name + "@" + size;
            lock (_cache)
            {
                Bitmap b;
                if (_cache.TryGetValue(key, out b)) return b;
                b = new Bitmap(Math.Max(1, size), Math.Max(1, size));
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.ScaleTransform(size / 32f, size / 32f);
                    Draw(g, name);
                }
                _cache[key] = b;
                return b;
            }
        }

        static readonly Color Outline = Color.FromArgb(70, 70, 90);

        static void Draw(Graphics g, string name)
        {
            switch (name)
            {
                case "general": Monitor(g); Globe(g, 18, 16, 13); break;
                case "types": Doc(g, 4, 2, Color.White); Doc(g, 10, 8, Color.FromArgb(255, 250, 225)); Monitor(g, 14, 18, 16, 12); break;
                case "save": Folder(g, 2, 8, 28, 20, Color.FromArgb(250, 210, 90)); Doc(g, 10, 2, Color.White, 14, 18); break;
                case "downloads": Globe(g, 15, 15, 13); Arrow(g, 18, 6, Color.FromArgb(40, 170, 60)); break;
                case "connection": Globe(g, 11, 11, 9); Globe(g, 22, 21, 9); break;
                case "proxy": Server(g); break;
                case "logins": Key(g, 3, 4, Color.FromArgb(230, 180, 40)); Key(g, 11, 12, Color.FromArgb(200, 140, 30)); break;
                case "dialup": Monitor(g, 2, 4, 22, 17); Phone(g); break;
                case "sounds": Speaker(g); break;
                case "clock": Clock(g); break;
                case "q-main": Folder(g, 1, 6, 30, 22, Color.FromArgb(250, 215, 100)); break;
                case "q-sync": Folder(g, 1, 6, 30, 22, Color.FromArgb(120, 200, 110)); break;
                case "q-limits": ClockSmall(g); Cross(g, 14, 14, 16); break;
                case "down": BigArrow(g, true); break;
                case "up": BigArrow(g, false); break;
                case "remove": Cross(g, 5, 5, 22); break;
            }
        }

        static void Monitor(Graphics g) { Monitor(g, 2, 4, 22, 17); }

        static void Monitor(Graphics g, float x, float y, float w, float h)
        {
            using (SolidBrush frame = new SolidBrush(Color.FromArgb(200, 205, 215)))
            using (LinearGradientBrush screen = new LinearGradientBrush(new RectangleF(x, y, w, h), Color.FromArgb(120, 180, 240), Color.FromArgb(40, 100, 200), 90f))
            using (Pen p = new Pen(Outline, 1f))
            {
                g.FillRectangle(frame, x, y, w, h); g.DrawRectangle(p, x, y, w, h);
                g.FillRectangle(screen, x + 2, y + 2, w - 4, h - 4);
                g.FillRectangle(frame, x + w / 2 - 3, y + h, 6, 3); g.FillRectangle(frame, x + w / 2 - 7, y + h + 3, 14, 2);
            }
        }

        static void Globe(Graphics g, float cx, float cy, float r)
        {
            RectangleF rc = new RectangleF(cx - r, cy - r, 2 * r, 2 * r);
            using (LinearGradientBrush b = new LinearGradientBrush(rc, Color.FromArgb(110, 190, 255), Color.FromArgb(20, 90, 190), 60f))
            using (SolidBrush land = new SolidBrush(Color.FromArgb(60, 170, 70)))
            using (Pen p = new Pen(Color.FromArgb(20, 60, 140), 1f))
            {
                g.FillEllipse(b, rc);
                g.FillEllipse(land, cx - r * 0.6f, cy - r * 0.55f, r * 0.7f, r * 0.6f);
                g.FillEllipse(land, cx + r * 0.05f, cy + r * 0.05f, r * 0.55f, r * 0.5f);
                g.DrawEllipse(p, rc);
            }
        }

        static void Doc(Graphics g, float x, float y, Color fill) { Doc(g, x, y, fill, 16, 20); }

        static void Doc(Graphics g, float x, float y, Color fill, float w, float h)
        {
            using (SolidBrush b = new SolidBrush(fill))
            using (Pen p = new Pen(Outline, 1f))
            using (Pen t = new Pen(Color.FromArgb(150, 160, 180), 1f))
            {
                g.FillRectangle(b, x, y, w, h); g.DrawRectangle(p, x, y, w, h);
                for (float ly = y + 4; ly < y + h - 2; ly += 3) g.DrawLine(t, x + 3, ly, x + w - 3, ly);
            }
        }

        static void Folder(Graphics g, float x, float y, float w, float h, Color c)
        {
            using (SolidBrush back = new SolidBrush(ControlPaint.Dark(c, 0.05f)))
            using (SolidBrush front = new SolidBrush(c))
            using (Pen p = new Pen(ControlPaint.Dark(c, 0.4f), 1f))
            {
                g.FillRectangle(back, x, y, w * 0.45f, h * 0.2f); g.DrawRectangle(p, x, y, w * 0.45f, h * 0.2f);
                g.FillRectangle(back, x, y + h * 0.15f, w, h * 0.85f); g.DrawRectangle(p, x, y + h * 0.15f, w, h * 0.85f);
                PointF[] lid = { new PointF(x + 2, y + h * 0.35f), new PointF(x + w + 1, y + h * 0.35f), new PointF(x + w - 2, y + h), new PointF(x, y + h) };
                g.FillPolygon(front, lid); g.DrawPolygon(p, lid);
            }
        }

        static void Arrow(Graphics g, float x, float y, Color c)
        {
            PointF[] pts = { new PointF(x + 3, y), new PointF(x + 9, y), new PointF(x + 9, y + 9), new PointF(x + 13, y + 9), new PointF(x + 6, y + 17), new PointF(x - 1, y + 9), new PointF(x + 3, y + 9) };
            using (SolidBrush b = new SolidBrush(c)) using (Pen p = new Pen(ControlPaint.Dark(c, 0.4f), 1f)) { g.FillPolygon(b, pts); g.DrawPolygon(p, pts); }
        }

        static void Server(Graphics g)
        {
            using (LinearGradientBrush b = new LinearGradientBrush(new RectangleF(4, 2, 16, 28), Color.FromArgb(235, 238, 245), Color.FromArgb(160, 170, 190), 0f))
            using (Pen p = new Pen(Outline, 1f))
            using (SolidBrush led = new SolidBrush(Color.FromArgb(40, 190, 70)))
            {
                g.FillRectangle(b, 4, 2, 16, 28); g.DrawRectangle(p, 4, 2, 16, 28);
                for (int i = 0; i < 4; i++) g.DrawLine(p, 7, 7 + i * 5, 17, 7 + i * 5);
                g.FillEllipse(led, 14, 25, 3, 3);
            }
            Monitor(g, 16, 10, 14, 11);
        }

        static void Key(Graphics g, float x, float y, Color c)
        {
            using (SolidBrush b = new SolidBrush(c)) using (Pen p = new Pen(ControlPaint.Dark(c, 0.5f), 1f))
            {
                g.FillEllipse(b, x, y, 10, 10); g.DrawEllipse(p, x, y, 10, 10);
                g.FillEllipse(Brushes.White, x + 3, y + 3, 4, 4);
                PointF[] shaft = { new PointF(x + 8, y + 8), new PointF(x + 18, y + 18), new PointF(x + 16, y + 20), new PointF(x + 14, y + 18), new PointF(x + 12, y + 20), new PointF(x + 6, y + 10) };
                g.FillPolygon(b, shaft); g.DrawPolygon(p, shaft);
            }
        }

        static void Phone(Graphics g)
        {
            using (SolidBrush b = new SolidBrush(Color.FromArgb(230, 230, 220))) using (Pen p = new Pen(Outline, 1f))
            {
                g.FillRectangle(b, 16, 20, 14, 9); g.DrawRectangle(p, 16, 20, 14, 9);
                g.FillRectangle(b, 15, 16, 16, 4); g.DrawRectangle(p, 15, 16, 16, 4);
            }
        }

        static void Speaker(Graphics g)
        {
            PointF[] cone = { new PointF(4, 12), new PointF(10, 12), new PointF(18, 5), new PointF(18, 27), new PointF(10, 20), new PointF(4, 20) };
            using (LinearGradientBrush b = new LinearGradientBrush(new RectangleF(4, 5, 14, 22), Color.FromArgb(150, 160, 220), Color.FromArgb(60, 70, 150), 0f))
            using (Pen p = new Pen(Color.FromArgb(40, 40, 100), 1f))
            using (Pen w = new Pen(Color.FromArgb(70, 110, 200), 1.6f))
            {
                g.FillPolygon(b, cone); g.DrawPolygon(p, cone);
                g.DrawArc(w, 14, 10, 8, 12, -50, 100); g.DrawArc(w, 15, 6, 12, 20, -50, 100);
            }
        }

        static void Clock(Graphics g)
        {
            using (SolidBrush bell = new SolidBrush(Color.FromArgb(240, 200, 40)))
            using (SolidBrush face = new SolidBrush(Color.FromArgb(255, 252, 230)))
            using (Pen p = new Pen(Color.FromArgb(150, 110, 20), 1.2f))
            using (Pen hand = new Pen(Color.FromArgb(60, 60, 60), 1.5f))
            {
                g.FillEllipse(bell, 2, 2, 10, 10); g.DrawEllipse(p, 2, 2, 10, 10);
                g.FillEllipse(bell, 20, 2, 10, 10); g.DrawEllipse(p, 20, 2, 10, 10);
                g.DrawLine(p, 8, 26, 5, 30); g.DrawLine(p, 24, 26, 27, 30);
                g.FillEllipse(bell, 4, 5, 24, 24); g.DrawEllipse(p, 4, 5, 24, 24);
                g.FillEllipse(face, 8, 9, 16, 16);
                g.DrawLine(hand, 16, 17, 16, 11); g.DrawLine(hand, 16, 17, 20, 19);
            }
        }

        static void ClockSmall(Graphics g)
        {
            using (SolidBrush bell = new SolidBrush(Color.FromArgb(240, 200, 40)))
            using (SolidBrush face = new SolidBrush(Color.White))
            using (Pen p = new Pen(Color.FromArgb(150, 110, 20), 1.5f))
            {
                g.FillEllipse(bell, 2, 4, 24, 24); g.DrawEllipse(p, 2, 4, 24, 24);
                g.FillEllipse(face, 6, 8, 16, 16);
                g.DrawLine(p, 14, 16, 14, 10); g.DrawLine(p, 14, 16, 18, 18);
            }
        }

        static void Cross(Graphics g, float x, float y, float s)
        {
            using (Pen p = new Pen(Color.FromArgb(220, 30, 30), s / 4f)) { p.StartCap = p.EndCap = LineCap.Round; g.DrawLine(p, x, y, x + s, y + s); g.DrawLine(p, x + s, y, x, y + s); }
        }

        // Flechas amarillas de "Archivos en la cola" (bajar / subir).
        static void BigArrow(Graphics g, bool down)
        {
            PointF[] pts = down
                ? new PointF[] { new PointF(11, 3), new PointF(21, 3), new PointF(21, 15), new PointF(28, 15), new PointF(16, 29), new PointF(4, 15), new PointF(11, 15) }
                : new PointF[] { new PointF(11, 29), new PointF(21, 29), new PointF(21, 17), new PointF(28, 17), new PointF(16, 3), new PointF(4, 17), new PointF(11, 17) };
            using (SolidBrush b = new SolidBrush(Color.FromArgb(250, 220, 40))) using (Pen p = new Pen(Color.FromArgb(40, 40, 120), 1.6f)) { g.FillPolygon(b, pts); g.DrawPolygon(p, pts); }
        }
    }
}
