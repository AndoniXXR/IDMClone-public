using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace IDMClone
{
    // Barra de segmentos de IDM ("Posiciones de inicio y progreso de la descarga por conexiones"): fondo azul muy
    // claro, un bloque azul oscuro (con brillo arriba y abajo) por lo descargado de cada conexión y una marca roja
    // fina donde está escribiendo cada conexión activa. En pausa quedan los bloques con huecos claros.
    public class SegmentPanel : Control
    {
        public DownloadItem Item;
        static readonly Color Border = Color.FromArgb(0x9B, 0x9E, 0xB0);
        static readonly Color BlockTop = Color.FromArgb(0xA4, 0xA8, 0xE6);
        static readonly Color BlockBottom = Color.FromArgb(0x4A, 0xB0, 0xD8);

        public SegmentPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.SegmentBack;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.SegmentBack);
            Rectangle inner = new Rectangle(1, 1, Width - 2, Height - 2);
            if (Item != null && Item.Size > 0 && inner.Width > 0 && inner.Height > 0)
            {
                double total = Item.Size;
                Segment[] segs;
                lock (Item.Sync) segs = Item.Segments.ToArray();
                using (LinearGradientBrush done = new LinearGradientBrush(new Rectangle(0, inner.Top, 1, inner.Height), BlockTop, BlockBottom, LinearGradientMode.Vertical))
                using (Pen mark = new Pen(Theme.SegmentMark))
                {
                    ColorBlend blend = new ColorBlend(4);
                    blend.Colors = new Color[] { BlockTop, Theme.SegmentDone, Theme.SegmentDone, BlockBottom };
                    blend.Positions = new float[] { 0f, 0.4f, 0.6f, 1f };
                    done.InterpolationColors = blend;
                    foreach (Segment s in segs)
                    {
                        int x0 = inner.Left + (int)(s.Start / total * inner.Width);
                        int x1 = inner.Left + (int)(s.Current / total * inner.Width);
                        if (x1 > x0) g.FillRectangle(done, x0, inner.Top, x1 - x0, inner.Height);
                        if (s.Active && s.Current < s.End) g.DrawLine(mark, Math.Max(x0, x1 - 1), inner.Top, Math.Max(x0, x1 - 1), inner.Bottom - 1);
                    }
                }
            }
            using (Pen p = new Pen(Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
    }

    // Barra de progreso total de IDM: plana, verde (#06B025) sobre gris claro, con borde gris (estilo Windows 10).
    // Con tamaño desconocido, mientras descarga, un bloque verde va y viene.
    public class GreenProgressBar : Control
    {
        int _value, _max = 1000, _phase;
        bool _marquee;
        Timer _anim;
        static readonly Color Border = Color.FromArgb(0xBC, 0xBC, 0xBC);
        static readonly Color Back = Color.FromArgb(0xE6, 0xE6, 0xE6);

        public GreenProgressBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Back;
        }

        public int Maximum { get { return _max; } set { _max = Math.Max(1, value); Invalidate(); } }
        public int Value
        {
            get { return _value; }
            set { int v = Math.Max(0, Math.Min(_max, value)); if (v != _value) { _value = v; Invalidate(); } }
        }

        public bool Marquee
        {
            get { return _marquee; }
            set
            {
                if (value == _marquee) return;
                _marquee = value;
                if (value)
                {
                    if (_anim == null) { _anim = new Timer(); _anim.Interval = 40; _anim.Tick += delegate { _phase = (_phase + 1) % 100; Invalidate(); }; }
                    _anim.Start();
                }
                else if (_anim != null) _anim.Stop();
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Back);
            Rectangle inner = new Rectangle(1, 1, Width - 2, Height - 2);
            using (SolidBrush green = new SolidBrush(Theme.ProgressGreen))
            {
                if (_marquee)
                {
                    int w = Math.Max(8, inner.Width / 4);
                    int x = inner.Left - w + (int)((inner.Width + w) * (_phase / 100.0));
                    Rectangle r = Rectangle.Intersect(inner, new Rectangle(x, inner.Top, w, inner.Height));
                    if (r.Width > 0) g.FillRectangle(green, r);
                }
                else
                {
                    int w = (int)((long)inner.Width * _value / _max);
                    if (w > 0) g.FillRectangle(green, inner.Left, inner.Top, w, inner.Height);
                }
            }
            using (Pen p = new Pen(Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _anim != null) { _anim.Stop(); _anim.Dispose(); _anim = null; }
            base.Dispose(disposing);
        }
    }

    // "Opciones de finalización" de cada descarga que Windows hace al terminar: apagar, suspender o hibernar, y
    // "Forzar el fin de procesos". Solo para esta sesión (como ExitWhenDone/ShutdownWhenDone de DownloadItem).
    public static class FinishOptions
    {
        public const string Shutdown = "shutdown", Sleep = "sleep", Hibernate = "hibernate";
        public static readonly string[] Modes = { Shutdown, Sleep, Hibernate };
        // Textos del combo, en el orden de Modes.
        public static readonly string[] ModeNames = { "Apagar", "Suspender", "Hibernar" };

        class Entry { public string Mode = Shutdown; public bool Force; }
        static readonly ConditionalWeakTable<DownloadItem, Entry> _items = new ConditionalWeakTable<DownloadItem, Entry>();

        // Para las pruebas: recibe la acción en vez de hacerla.
        public static Action<string, bool> Hook;

        public static string ModeOf(DownloadItem it)
        {
            Entry e;
            return it != null && _items.TryGetValue(it, out e) ? e.Mode : Shutdown;
        }

        public static bool ForceOf(DownloadItem it)
        {
            Entry e;
            return it != null && _items.TryGetValue(it, out e) && e.Force;
        }

        public static void Set(DownloadItem it, string mode, bool force)
        {
            if (it == null) return;
            Entry e = _items.GetOrCreateValue(it);
            e.Mode = Array.IndexOf(Modes, mode) >= 0 ? mode : Shutdown;
            e.Force = force;
        }

        // "El equipo se apagará en 30 segundos." (y suspenderá / hibernará).
        public static string CountdownText(string mode, int seconds)
        {
            string verb = mode == Sleep ? "se suspenderá" : mode == Hibernate ? "se hibernará" : "se apagará";
            return "El equipo " + verb + " en " + Math.Max(0, seconds) + " segundos.";
        }

        public static void Run(string mode, bool force)
        {
            if (Hook != null) { Hook(mode, force); return; }
            if (mode == Sleep) Application.SetSuspendState(PowerState.Suspend, force, false);
            else if (mode == Hibernate) Application.SetSuspendState(PowerState.Hibernate, force, false);
            else if (!force) MainForm.ShutdownAction();
            else
            {
                ProcessStartInfo psi = new ProcessStartInfo("shutdown.exe", "/s /f /t 0");
                psi.CreateNoWindow = true; psi.UseShellExecute = false;
                Process.Start(psi);
            }
        }
    }

    // Ventana de progreso de IDM: pestañas "Estado de la descarga", "Límite de velocidad" y "Opciones de
    // finalización"; debajo la barra verde y los botones; con "<< Ocultar detalles"/"Mostrar detalles >>" se
    // ve u oculta la barra de segmentos y la tabla de conexiones. Medidas de capturas\idm\04-progreso.jpg.
    // Flecha verde animada de la ventana de progreso: IDM la pone en la barra de título mientras recibe datos y la
    // para en pausa o al terminar. Aquí es el icono de la ventana (dibujado en código): una flecha con la punta verde
    // que avanza hacia la derecha y vuelve a entrar por la izquierda.
    public static class ActivityArrow
    {
        public const int Frames = 8;
        public const int Interval = 110; // ms entre fotogramas
        static Icon[] _icons;
        static int _iconSize;

        public static Bitmap Draw(int size, int frame)
        {
            size = Math.Max(8, size);
            Bitmap b = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float s = size;
                float len = s * 0.82f, head = s * 0.42f, stroke = Math.Max(1.5f, s / 9f), mid = s / 2f;
                float shift = s * ((frame % Frames + Frames) % Frames) / Frames;
                // Dos copias separadas por el ancho del icono: al salir por la derecha entra por la izquierda.
                g.SetClip(new RectangleF(0, 0, s, s));
                for (int k = 0; k < 2; k++)
                {
                    float tip = s * 0.9f + shift - k * s;
                    using (Pen line = new Pen(Color.FromArgb(0x30, 0x30, 0x30), stroke))
                        g.DrawLine(line, tip - len, mid, tip - head * 0.6f, mid);
                    PointF[] tri = { new PointF(tip, mid), new PointF(tip - head, mid - head * 0.62f), new PointF(tip - head, mid + head * 0.62f) };
                    using (SolidBrush green = new SolidBrush(Theme.ProgressGreen)) g.FillPolygon(green, tri);
                    using (Pen edge = new Pen(Color.FromArgb(0x04, 0x7A, 0x1A), Math.Max(1f, s / 16f))) g.DrawPolygon(edge, tri);
                }
            }
            return b;
        }

        // Los fotogramas como iconos del tamaño pequeño del sistema; se crean una vez y se reutilizan
        // (solo los usa el hilo de la interfaz).
        public static Icon Frame(int frame)
        {
            int size = SystemInformation.SmallIconSize.Width;
            if (_icons == null || _iconSize != size)
            {
                Icon[] icons = new Icon[Frames];
                for (int i = 0; i < Frames; i++)
                    using (Bitmap bmp = Draw(size, i)) icons[i] = Icon.FromHandle(bmp.GetHicon());
                _icons = icons; _iconSize = size; // los HICON viven lo que el proceso (solo son 8)
            }
            return _icons[((frame % Frames) + Frames) % Frames];
        }

        // El icono pequeño de IDMClone para volver a ponerlo en la barra de título al parar.
        static Icon _app;
        public static Icon AppSmall
        {
            get { if (_app == null) _app = new Icon(AppIcon.Small, SystemInformation.SmallIconSize); return _app; }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        const int WM_SETICON = 0x80, WM_GETICON = 0x7F;
        static readonly IntPtr ICON_SMALL = IntPtr.Zero;

        // Solo el icono pequeño (el de la barra de título): sin pasar por Form.Icon, que copia el icono, cambia
        // también el grande (barra de tareas, Alt+Tab) y repinta todo el marco en cada fotograma.
        public static void SetCaptionIcon(Form f, Icon icon)
        {
            if (f != null && f.IsHandleCreated && !f.IsDisposed && icon != null) SendMessage(f.Handle, WM_SETICON, ICON_SMALL, icon.Handle);
        }

        public static IntPtr CaptionIcon(Form f)
        {
            return f != null && f.IsHandleCreated ? SendMessage(f.Handle, WM_GETICON, ICON_SMALL, IntPtr.Zero) : IntPtr.Zero;
        }
    }

    public class ProgressForm : Form
    {
        readonly DownloadItem _item;
        readonly DownloadManager _mgr;
        readonly AppConfig _cfg;
        TabControl _tabs;
        TabPage _limitPage, _finishPage;
        Button _hideLimit, _hideFinish;
        Label _url, _status, _size, _done, _speed, _eta, _resume, _limSpeed, _limLabel, _saveTo;
        GreenProgressBar _bar;
        Button _details, _pause, _cancel;
        Panel _detailPanel;
        SegmentPanel _seg;
        ListView _conns;
        CheckBox _limitOn, _remember, _showComplete, _exitDone, _shutdownDone, _force;
        ComboBox _shutdownMode;
        TextBox _limit;
        Timer _timer, _arrowTimer;
        int _arrowFrame;
        bool _expanded = true, _loading;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public DownloadItem Item { get { return _item; } }

        public ProgressForm(DownloadItem item, DownloadManager mgr)
        {
            _item = item; _mgr = mgr; _cfg = mgr != null ? mgr.Config : AppConfig.CreateDefault();
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None; // se escala a mano al terminar (Dpi.ScaleForm, como los diálogos)
            Font = Theme.UiFont; // como IDM: Tahoma 8
            Text = item.FileName;
            Icon = AppIcon.Small;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.Manual; // se centra sobre la ventana principal en OnLoad
            MaximizeBox = false; MinimizeBox = true;
            ClientSize = new Size(541, 410);

            _tabs = new TabControl(); _tabs.SetBounds(17, 9, 511, 163); Controls.Add(_tabs);
            _loading = true;
            BuildStatusTab();
            BuildLimitTab();
            BuildCompletionTab();
            _loading = false;
            ApplyTabSettings();

            _bar = new GreenProgressBar(); _bar.SetBounds(18, 182, 513, 17); Controls.Add(_bar);
            _details = Btn(this, "", 32, 209, 137); _details.Click += delegate { SetExpanded(!_expanded); };
            _pause = Btn(this, "Pausar", 312, 209, 84); _pause.Click += delegate { TogglePause(); };
            _cancel = Btn(this, "Cancelar", 423, 209, 74);
            // Como en IDM: "Cancelar" detiene la descarga (sigue en la lista) y cierra la ventana.
            _cancel.Click += delegate { if (_mgr != null) _mgr.StopItem(_item); Close(); };

            _detailPanel = new Panel(); _detailPanel.SetBounds(0, 232, 541, 178); Controls.Add(_detailPanel);
            Label segTitle = new Label(); segTitle.Text = "Posiciones de inicio y progreso de la descarga por conexiones";
            segTitle.TextAlign = ContentAlignment.MiddleCenter; segTitle.SetBounds(18, 2, 513, 15);
            _detailPanel.Controls.Add(segTitle);
            _seg = new SegmentPanel(); _seg.SetBounds(18, 19, 513, 17); _seg.Item = item; _detailPanel.Controls.Add(_seg);
            _conns = new ListView(); _conns.SetBounds(18, 42, 514, 131); _conns.View = View.Details; _conns.FullRowSelect = true;
            _conns.HeaderStyle = ColumnHeaderStyle.Nonclickable; _conns.GridLines = false; _conns.MultiSelect = false;
            // Los anchos de columna no los escala WinForms: se ajustan a la escala de la pantalla.
            _conns.Columns.Add("N.", Dpi.Px(32));
            _conns.Columns.Add("Descargado", Dpi.Px(100));
            _conns.Columns.Add("Info", Dpi.Px(355));
            _detailPanel.Controls.Add(_conns);

            SetExpanded(true);
            _timer = new Timer(); _timer.Interval = 500; _timer.Tick += delegate { Refresh2(); }; _timer.Start();
            Refresh2();
            ResumeLayout(false);
            Dpi.ScaleForm(this);
            PerformLayout();
        }

        static Button Btn(Control parent, string text, int x, int y, int w)
        {
            Button b = new Button(); b.Text = text; b.SetBounds(x, y, w, 23); b.UseVisualStyleBackColor = true; parent.Controls.Add(b); return b;
        }

        static Label Lbl(Control parent, string text, int x, int y, int w)
        {
            Label l = new Label(); l.Text = text; l.AutoSize = false; l.AutoEllipsis = true; l.TextAlign = ContentAlignment.MiddleLeft;
            l.SetBounds(x, y, w, 15); parent.Controls.Add(l); return l;
        }

        static Label Value(Control parent, int x, int y, int w)
        {
            Label l = Lbl(parent, "", x, y, w); l.Tag = Lang.Keep; l.UseMnemonic = false; return l;
        }

        static CheckBox Check(Control parent, string text, int x, int y, int w, int h)
        {
            CheckBox c = new CheckBox(); c.Text = text; c.SetBounds(x, y, w, h); c.UseVisualStyleBackColor = true;
            if (h > 20) { c.CheckAlign = ContentAlignment.TopLeft; c.TextAlign = ContentAlignment.TopLeft; } // varias líneas
            parent.Controls.Add(c); return c;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Dpi.FitWrappedChecks(this);
        }

        static TabPage Page(TabControl tabs, string title)
        {
            TabPage p = new TabPage(title); p.UseVisualStyleBackColor = true; tabs.TabPages.Add(p); return p;
        }

        void BuildStatusTab()
        {
            TabPage p = Page(_tabs, "Estado de la descarga");
            _url = Value(p, 13, 8, 480);
            Lbl(p, "Estado:", 13, 25, 66);
            _status = Value(p, 81, 25, 410); _status.ForeColor = Theme.StatusBlue;
            // Como IDM: etiquetas a la izquierda y valores muy juntos (la etiqueta larga se corta).
            Lbl(p, "Tamaño de archivo", 13, 47, 102); _size = Value(p, 115, 47, 370); // 102: a 125 % no cabía en 100
            Lbl(p, "Descargado", 13, 63, 100); _done = Value(p, 115, 63, 370);
            Lbl(p, "Velocidad de transferencia", 13, 79, 100); _speed = Value(p, 115, 79, 370);
            Lbl(p, "Tiempo restante", 13, 95, 100); _eta = Value(p, 115, 95, 370);
            Lbl(p, "Capacidad para reanudar:", 13, 112, 178); _resume = Value(p, 194, 112, 200);
        }

        void BuildLimitTab()
        {
            TabPage p = _limitPage = Page(_tabs, "Límite de velocidad");
            Lbl(p, "Velocidad de transferencia", 13, 12, 100); _limSpeed = Value(p, 115, 12, 370);
            _limitOn = Check(p, "Usar Limitador de Velocidad", 13, 33, 300, 18);
            _limLabel = Lbl(p, "Máxima velocidad de descarga:", 13, 53, 300);
            _limit = new TextBox(); _limit.SetBounds(14, 70, 76, 20); _limit.TextAlign = HorizontalAlignment.Right; _limit.MaxLength = 7; p.Controls.Add(_limit);
            Lbl(p, "KBytes/seg", 96, 73, 120);
            _remember = Check(p, "Recordar la configuración del límite de velocidad para este archivo al detener/reanudar descarga", 13, 97, 362, 30);
            _hideLimit = Btn(p, "Ocultar pestaña", 382, 102, 95); _hideLimit.Click += delegate { HideTab(_limitPage); };

            _limitOn.Checked = _item.SpeedLimitOn;
            _limit.Text = Math.Max(1, Math.Min(9999999, _item.SpeedLimitKBps > 0 ? _item.SpeedLimitKBps : 200)).ToString(Inv);
            _remember.Checked = _item.RememberSpeedLimit;
            _limit.Enabled = _limLabel.Enabled = _limitOn.Checked;
            _limit.KeyPress += delegate(object s, KeyPressEventArgs e) { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
            // Se aplica al momento: el motor lee el límite en cada lectura.
            _limitOn.CheckedChanged += delegate { _limit.Enabled = _limLabel.Enabled = _limitOn.Checked; ApplyLimit(); };
            // Mientras se escribe solo se aplica; la lista se guarda al salir de la casilla (no en cada tecla).
            _limit.TextChanged += delegate { ApplyLimit(false); };
            _limit.Validated += delegate { ApplyLimit(true); }; // también al cerrar la ventana (Leave no salta)
            _remember.CheckedChanged += delegate { ApplyLimit(true); };
        }

        void ApplyLimit() { ApplyLimit(true); }

        void ApplyLimit(bool save)
        {
            if (_loading) return;
            int kb;
            if (int.TryParse(_limit.Text.Trim(), NumberStyles.Integer, Inv, out kb) && kb > 0) _item.SpeedLimitKBps = kb;
            _item.SpeedLimitOn = _limitOn.Checked;
            _item.RememberSpeedLimit = _remember.Checked;
            if (save && _mgr != null) _mgr.SaveList();
        }

        void BuildCompletionTab()
        {
            TabPage p = _finishPage = Page(_tabs, "Opciones de finalización");
            Lbl(p, "Guardar en:", 13, 7, 70); _saveTo = Value(p, 86, 7, 400);
            _showComplete = Check(p, "Mostrar diálogo de descarga completa", 13, 24, 400, 18);
            Label line = new Label(); line.BorderStyle = BorderStyle.Fixed3D; line.SetBounds(14, 45, 473, 2); p.Controls.Add(line);
            Label note = Lbl(p, "Esta configuración no está disponible cuando \"Mostrar diálogo de descarga completa\" está activado", 13, 51, 480);
            note.ForeColor = SystemColors.GrayText;
            // "Colgar el módem al finalizar" de IDM no está: no hay módem que colgar.
            _exitDone = Check(p, "Salir de IDMClone al finalizar", 13, 70, 300, 18);
            _shutdownDone = Check(p, "Apagar equipo al finalizar", 13, 98, 230, 18);
            _shutdownMode = new ComboBox(); _shutdownMode.DropDownStyle = ComboBoxStyle.DropDownList; _shutdownMode.SetBounds(249, 97, 118, 21);
            _shutdownMode.Tag = Lang.Mark;
            foreach (string n in FinishOptions.ModeNames) _shutdownMode.Items.Add(n);
            p.Controls.Add(_shutdownMode);
            _hideFinish = Btn(p, "Ocultar pestaña", 382, 96, 95); _hideFinish.Click += delegate { HideTab(_finishPage); };
            _force = Check(p, "Forzar el fin de procesos", 33, 116, 200, 17);

            _showComplete.Checked = _item.ShowCompleteDialog ?? _cfg.ShowCompleteDialog;
            _exitDone.Checked = _item.ExitWhenDone;
            _shutdownDone.Checked = _item.ShutdownWhenDone;
            _shutdownMode.SelectedIndex = Math.Max(0, Array.IndexOf(FinishOptions.Modes, FinishOptions.ModeOf(_item)));
            _force.Checked = FinishOptions.ForceOf(_item);
            UpdateFinishControls();
            _showComplete.CheckedChanged += delegate { _item.ShowCompleteDialog = _showComplete.Checked; ApplyFinish(); };
            _exitDone.CheckedChanged += delegate { ApplyFinish(); };
            _shutdownDone.CheckedChanged += delegate { ApplyFinish(); };
            _shutdownMode.SelectedIndexChanged += delegate { ApplyFinish(); };
            _force.CheckedChanged += delegate { ApplyFinish(); };
        }

        // Opciones > Descargas > "Personalizar ventana del progreso de descarga": qué pestañas se ven y si llevan el
        // botón "Ocultar pestaña". Las pestañas se construyen siempre (Refresh2 usa sus etiquetas) y se quitan después.
        void ApplyTabSettings()
        {
            if (!_cfg.ProgressShowLimitTab) RemoveTab(_limitPage);
            if (!_cfg.ProgressShowFinishTab) RemoveTab(_finishPage);
            _hideLimit.Visible = _hideFinish.Visible = _cfg.ProgressShowHideTabsButton;
        }

        // "Ocultar pestaña": como en IDM (comprobado en IDM 6.43), es permanente: se quita de esta ventana y deja de
        // salir en las siguientes. Se vuelve a mostrar en Opciones > Descargas > Personalizar ventana de progreso.
        void HideTab(TabPage page)
        {
            RemoveTab(page);
            if (_tabs.TabCount > 0) _tabs.SelectedIndex = 0;
            if (page == _limitPage) _cfg.ProgressShowLimitTab = false;
            else if (page == _finishPage) _cfg.ProgressShowFinishTab = false;
            else return;
            try { _cfg.Save(); } catch { }
        }

        void RemoveTab(TabPage page)
        {
            if (page != null && _tabs.TabPages.Contains(page)) _tabs.TabPages.Remove(page);
        }

        // Pestañas visibles (para las pruebas y la ayuda).
        public int VisibleTabCount { get { return _tabs.TabCount; } }

        protected override void Dispose(bool disposing)
        {
            // Las pestañas quitadas ya no son hijas de la ventana: se liberan aquí.
            if (disposing)
            {
                foreach (TabPage p in new TabPage[] { _limitPage, _finishPage })
                    if (p != null && p.Parent == null && !p.IsDisposed) p.Dispose();
                // Si se libera sin cerrarla (OnFormClosed no llega), los temporizadores no pueden quedar en marcha.
                if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; }
                if (_arrowTimer != null) { _arrowTimer.Stop(); _arrowTimer.Dispose(); _arrowTimer = null; }
            }
            base.Dispose(disposing);
        }

        // F1: la ayuda de la ventana de progreso.
        protected override void OnHelpRequested(HelpEventArgs hevent)
        {
            hevent.Handled = true;
            OptionsHelp.Open(this, "progreso");
        }

        // Como IDM: con "Mostrar diálogo de descarga completa" no se puede salir ni apagar al terminar.
        void UpdateFinishControls()
        {
            bool allowed = !_showComplete.Checked;
            _exitDone.Enabled = _shutdownDone.Enabled = allowed;
            _shutdownMode.Enabled = _force.Enabled = allowed && _shutdownDone.Checked;
        }

        void ApplyFinish()
        {
            if (_loading) return;
            UpdateFinishControls();
            bool allowed = !_showComplete.Checked;
            _item.ExitWhenDone = allowed && _exitDone.Checked;
            _item.ShutdownWhenDone = allowed && _shutdownDone.Checked;
            int i = _shutdownMode.SelectedIndex;
            FinishOptions.Set(_item, i >= 0 && i < FinishOptions.Modes.Length ? FinishOptions.Modes[i] : FinishOptions.Shutdown, _force.Checked);
        }

        public void SetExpanded(bool on)
        {
            _expanded = on;
            _detailPanel.Visible = on;
            _details.Text = Lang.T(on ? "<< Ocultar detalles" : "Mostrar detalles >>");
            // Alturas sacadas de los propios controles: valen a cualquier escala de pantalla.
            int margin = (int)Math.Round(9.0 * _details.Height / 23.0);
            ClientSize = new Size(ClientSize.Width, on ? _detailPanel.Bottom : _details.Bottom + margin);
            if (on) { _seg.Invalidate(); RefreshConnections(); }
        }

        void TogglePause()
        {
            if (_mgr == null) return;
            if (IsRunning) _mgr.StopItem(_item); else if (!IsStopping) _mgr.StartItem(_item);
            Refresh2();
        }

        bool IsRunning
        {
            get
            {
                DownloadEngine e = _item.Engine;
                return e != null && e.IsRunning && !e.StopRequested;
            }
        }

        // Pausada pero la conexión aún no se ha cerrado (IDM deja "Pausar" en gris en ese momento).
        bool IsStopping
        {
            get
            {
                DownloadEngine e = _item.Engine;
                return e != null && e.IsRunning && e.StopRequested;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            Lang.Apply(this);
            base.OnLoad(e);
            // Centrado sobre la ventana principal (CenterParent no se aplica a ventanas no modales).
            Form owner = Owner;
            if (owner != null && owner.Visible && owner.WindowState != FormWindowState.Minimized)
            {
                Rectangle o = owner.Bounds;
                Location = new Point(o.Left + (o.Width - Width) / 2, o.Top + (o.Height - Height) / 2);
            }
            else
            {
                Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; }
            if (_arrowTimer != null) { _arrowTimer.Stop(); _arrowTimer.Dispose(); _arrowTimer = null; }
            base.OnFormClosed(e);
        }

        static void Set(Label l, string text) { text = Lang.T(text); if (l.Text != text) l.Text = text; }

        // Tamaños como en la ventana de progreso de IDM: tres decimales ("47.683 MB", "783.937 KB").
        public static string IdmSize(long bytes)
        {
            if (bytes < 0) return "";
            if (bytes < 1024) return bytes.ToString(Inv) + " Bytes";
            double b = bytes;
            string[] u = { "KB", "MB", "GB", "TB" };
            int i = -1;
            while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
            return string.Format(Inv, "{0:0.000} {1}", b, u[i]);
        }

        // "1.106 MB/sec", "523.400 KB/sec"; desde 1000 KB/sec ya en MB/sec ("0.993 MB/sec"), como IDM.
        public static string IdmSpeed(long bps) { return Fmt.IdmSpeed(bps, 3); }

        // "19 segundos", "3 min 20 seg", "2 h 5 min".
        public static string IdmEta(long remaining, long bps)
        {
            if (bps <= 0 || remaining < 0) return "";
            long sec = remaining / bps;
            if (sec >= 3600) return (sec / 3600) + " h " + (sec % 3600 / 60) + " min";
            if (sec >= 60) return (sec / 60) + " min " + (sec % 60) + " seg";
            return sec + " segundos";
        }

        void Refresh2()
        {
            if (IsDisposed) return;
            long done = _item.Downloaded;
            // Título como IDM: "NN% nombre".
            string pct = _item.Size > 0 ? ((int)Math.Min(100, 100.0 * done / _item.Size)).ToString(Inv) + "% " : "";
            string title = (_item.Status == DownloadStatus.Complete ? "100% " : pct) + _item.FileName;
            if (Text != title) Text = title;
            Set(_url, _item.Url);
            Set(_status, StatusLine());
            Color sc = _item.Status == DownloadStatus.Error ? Color.Firebrick : Theme.StatusBlue;
            if (_status.ForeColor != sc) _status.ForeColor = sc;
            Set(_size, _item.Size >= 0 ? IdmSize(_item.Size) : "(desconocido)");
            Set(_done, IdmSize(done) + (_item.Size > 0 ? string.Format(Inv, " ( {0:0.00} % )", Math.Min(100.0, 100.0 * done / _item.Size)) : ""));
            bool running = _item.Status == DownloadStatus.Downloading;
            string speed = running && _item.SpeedBps > 0 ? IdmSpeed(_item.SpeedBps) : "";
            Set(_speed, speed);
            Set(_limSpeed, speed);
            Set(_eta, running && _item.SpeedBps > 0 && _item.Remaining > 0 ? IdmEta(_item.Remaining, _item.SpeedBps) : "");
            Set(_resume, _item.Size < 0 && _item.Status != DownloadStatus.Complete && done == 0 ? "(desconocido)" : (_item.Resumable ? "Sí" : "No"));
            Set(_saveTo, _item.FilePath ?? "");

            bool active = _item.Status == DownloadStatus.Downloading || _item.Status == DownloadStatus.Connecting;
            if (_item.Size > 0 || _item.Status == DownloadStatus.Complete)
            {
                _bar.Marquee = false;
                _bar.Value = _item.Size > 0 ? (int)(1000.0 * done / _item.Size) : 1000;
            }
            else _bar.Marquee = active; // tamaño desconocido: el bloque verde va y viene mientras descarga

            bool stopping = IsStopping;
            string pauseText = Lang.T(IsRunning || stopping ? "Pausar" : "Iniciar");
            if (_pause.Text != pauseText) _pause.Text = pauseText;
            _pause.Enabled = _item.Status != DownloadStatus.Complete && !stopping;
            UpdateArrow(active && !stopping);
            if (_expanded) { _seg.Invalidate(); RefreshConnections(); }
        }

        // La flecha verde se mueve solo mientras descarga; en pausa, con error o al terminar vuelve el icono normal.
        void UpdateArrow(bool on)
        {
            if (on)
            {
                if (_arrowTimer == null)
                {
                    _arrowTimer = new Timer(); _arrowTimer.Interval = ActivityArrow.Interval;
                    _arrowTimer.Tick += delegate
                    {
                        // Minimizada no se ve la barra de título: no se gasta nada.
                        if (IsDisposed || !Visible || WindowState == FormWindowState.Minimized) return;
                        _arrowFrame = (_arrowFrame + 1) % ActivityArrow.Frames;
                        ActivityArrow.SetCaptionIcon(this, ActivityArrow.Frame(_arrowFrame));
                    };
                }
                if (!_arrowTimer.Enabled) { ActivityArrow.SetCaptionIcon(this, ActivityArrow.Frame(_arrowFrame)); _arrowTimer.Start(); }
            }
            else if (_arrowTimer != null && _arrowTimer.Enabled)
            {
                _arrowTimer.Stop();
                ActivityArrow.SetCaptionIcon(this, ActivityArrow.AppSmall);
            }
        }

        // Para las pruebas: ¿se está moviendo la flecha?
        public bool ArrowAnimating { get { return _arrowTimer != null && _arrowTimer.Enabled; } }

        string StatusLine()
        {
            string quota = QuotaStatus(_item);
            if (quota != null) return quota;
            switch (_item.Status)
            {
                case DownloadStatus.Downloading: return "Recibiendo datos...";
                case DownloadStatus.Connecting: return "Conectando...";
                case DownloadStatus.Complete: return "Completo.";
                case DownloadStatus.Error: return "Error: " + (_item.Error ?? "");
                case DownloadStatus.Paused: return "Detenida";
            }
            return StatusText(_item.Status);
        }

        // Tabla de conexiones como IDM: todas las filas en malva, "1  783.937 KB  Recibiendo datos...".
        void RefreshConnections()
        {
            DownloadEngine eng = _item.Engine;
            ConnectionInfo[] rows = eng != null ? eng.Connections : new ConnectionInfo[0];
            _conns.BeginUpdate();
            try
            {
                while (_conns.Items.Count > rows.Length) _conns.Items.RemoveAt(_conns.Items.Count - 1);
                for (int i = 0; i < rows.Length; i++)
                {
                    if (i >= _conns.Items.Count)
                    {
                        ListViewItem n = new ListViewItem(""); n.SubItems.Add(""); n.SubItems.Add("");
                        n.BackColor = Theme.ConnRow; n.UseItemStyleForSubItems = true;
                        _conns.Items.Add(n);
                    }
                    ListViewItem li = _conns.Items[i];
                    string num = rows[i].Number.ToString(Inv), got = IdmSize(rows[i].Bytes), info = Lang.T(rows[i].Info ?? "");
                    if (li.Text != num) li.Text = num;
                    if (li.SubItems[1].Text != got) li.SubItems[1].Text = got;
                    if (li.SubItems[2].Text != info) li.SubItems[2].Text = info;
                }
            }
            finally { _conns.EndUpdate(); }
        }

        // Límites de descarga (Opciones > Conexión): mientras se espera al periodo siguiente, la descarga no está
        // "recibiendo datos" a 0 B/s sino esperando. null si no es el caso. Sin traducir (Lang.T al mostrarlo).
        public static string QuotaStatus(DownloadItem it)
        {
            if (it == null || it.Status != DownloadStatus.Downloading) return null;
            DateTime until = DownloadQuota.BlockedUntilUtc;
            if (until <= DateTime.UtcNow) return null;
            return "Límite de descarga alcanzado: sigue a las " + until.ToLocalTime().ToString("HH:mm", Inv);
        }

        public static string StatusText(DownloadStatus s)
        {
            switch (s)
            {
                case DownloadStatus.Queued: return Lang.T("En cola");
                case DownloadStatus.Connecting: return Lang.T("Conectando");
                case DownloadStatus.Downloading: return Lang.T("Descargando");
                case DownloadStatus.Paused: return Lang.T("Detenida");
                case DownloadStatus.Complete: return Lang.T("Completo");
                case DownloadStatus.Error: return Lang.T("Error");
            }
            return s.ToString();
        }
    }
}
