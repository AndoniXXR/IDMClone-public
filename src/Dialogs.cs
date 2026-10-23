using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace IDMClone
{
    // Base de los diálogos: diseñados a 96 ppp y escalados al DPI real de la pantalla.
    public class DialogBase : Form
    {
        protected DialogBase()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Theme.UiFont; // como IDM: Tahoma 8
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            Icon = AppIcon.Small;
        }

        // Idioma de la interfaz: se traduce al abrirse.
        protected override void OnLoad(EventArgs e)
        {
            Lang.Apply(this);
            base.OnLoad(e);
        }

        // Ya escalada a la pantalla y traducida: casillas de varias líneas con el alto que piden (Dpi.FitWrappedChecks).
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Dpi.FitWrappedChecks(this);
            if (!Modal) CloseOnDialogResult(this);
        }

        // Ventana no modal (Show): un botón con DialogResult (Aceptar, Cancelar, y Esc a través de CancelButton) no la
        // cierra por sí solo. Se cierra tras los manejadores del botón, salvo que alguno lo anule (DialogResult = None).
        void CloseOnDialogResult(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                Button b = c as Button;
                if (b != null && b.DialogResult != DialogResult.None)
                    b.Click += delegate { if (!Modal && !IsDisposed && DialogResult != DialogResult.None) Close(); };
                if (c.HasChildren) CloseOnDialogResult(c);
            }
        }

        // Llamar al final del constructor de cada diálogo.
        protected void EndLayout()
        {
            ResumeLayout(false);
            PerformLayout();
        }

        protected Label AddLabel(string text, int x, int y, int w)
        {
            Label l = new Label(); l.Text = text; l.AutoSize = false; l.TextAlign = ContentAlignment.MiddleLeft;
            l.SetBounds(x, y, w, 23); Controls.Add(l); return l;
        }

        protected Button AddButton(string text, int x, int y, int w, DialogResult r)
        {
            Button b = new Button(); b.Text = text; b.SetBounds(x, y, w, 28); b.DialogResult = r; Controls.Add(b); return b;
        }

        protected static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }

    // "Confirmar eliminación de descargas" (IDM, captura 76): por defecto solo quita de la lista; borrar del
    // disco exige marcar la casilla a propósito. Botones Sí (por defecto) y No.
    public class DeleteConfirmForm : DialogBase
    {
        CheckBox _delFiles, _dontAsk;
        public bool DeleteFiles { get { return _delFiles.Checked; } }
        public bool DontShowAgain { get { return _dontAsk.Checked; } }

        public DeleteConfirmForm(int count)
        {
            Text = "Confirmar eliminación de descargas";
            ClientSize = new Size(412, 134);
            OptUi.Lbl(this, "¿Seguro que deseas eliminar las descargas de la lista de descargas de IDMClone?", 10, 8, 392, 30);
            _delFiles = OptUi.Chk(this, "Eliminar también los archivos descargados del disco duro. ¡Cuidado!, esta opción no es reversible.", 10, 40, 392, 30);
            _dontAsk = OptUi.Chk(this, "No mostrar este diálogo otra vez", 10, 74, 392);
            Button yes = OptUi.Btn(this, "Sí", 122, 102); yes.DialogResult = DialogResult.OK;
            Button no = OptUi.Btn(this, "No", 216, 102); no.DialogResult = DialogResult.Cancel;
            AcceptButton = yes; CancelButton = no;
            EndLayout();
        }

        // Pregunta antes de eliminar (salvo que se marcara "No mostrar este diálogo otra vez"; entonces solo
        // se quitan de la lista, nunca se borran archivos). Devuelve false si el usuario dijo No.
        public static bool Ask(IWin32Window owner, AppConfig cfg, int count, out bool deleteFiles)
        {
            deleteFiles = false;
            if (cfg != null && cfg.SkipDeleteConfirm) return true;
            using (DeleteConfirmForm f = new DeleteConfirmForm(count))
            {
                if (f.ShowDialog(owner) != DialogResult.OK) return false;
                deleteFiles = f.DeleteFiles;
                if (f.DontShowAgain && cfg != null) { cfg.SkipDeleteConfirm = true; cfg.Save(); }
                return true;
            }
        }
    }

    // "Programador" de IDM (capturas 17 a 18): a la izquierda las colas (principal, sincronización y las propias)
    // y el nodo "Límites de descarga"; a la derecha, para cada cola, las pestañas "Programar" (horario, repetir y
    // qué hacer al terminar) y "Archivos en la cola". Aplicar guarda; Cerrar (o la X) aplica lo pendiente.
    public class SchedulerForm : DialogBase
    {
        readonly DownloadManager _mgr;
        ListView _queues;
        Button _delQueue, _startNow, _stopNow, _apply;
        Label _title;
        Panel _right, _limitsPanel;
        TabControl _tabs;
        // Programar
        RadioButton _modeOnce, _modePeriodic, _once, _daily;
        CheckBox _atStartup, _enabled, _repeat, _stopEnabled, _retryLimit, _runFile, _exitDone, _shutdownDone, _force;
        Label _dailyLabel, _repeatHL, _repeatML;
        DateTimePicker _start, _stop, _onceDate;
        NumericUpDown _retries, _repeatH, _repeatM, _maxActive;
        TextBox _runFilePath;
        Button _runBrowse;
        ComboBox _shutdownMode;
        CheckBox[] _days;
        // Archivos en la cola
        ListView _files;
        // Límites de descarga (los mismos que Opciones > Conexión)
        CheckBox _quota, _quotaWarn;
        NumericUpDown _quotaMB, _quotaHours;
        Control[] _quotaControls;

        QueueDef _shown;
        bool _limitsShown, _loading, _dirty;
        const string LimitsTag = "limits";

        public SchedulerForm(Schedule s, DownloadManager mgr) : this(mgr ?? MemoryManager(s), QueueDef.MainId) { }

        static DownloadManager MemoryManager(Schedule s)
        {
            DownloadManager m = new DownloadManager(AppConfig.CreateDefault());
            m.Persist = false;
            if (s != null) m.Schedule = s;
            return m;
        }

        public SchedulerForm(DownloadManager mgr, int selectQueue)
        {
            _mgr = mgr;
            Text = "Programador";
            ClientSize = new Size(643, 458);
            _loading = true;

            PictureBox clock = OptUi.Add(this, new PictureBox(), 14, 4, 32, 32);
            clock.SizeMode = PictureBoxSizeMode.Zoom; clock.Image = OptIcons.Page("clock", Dpi.Px(32));
            Label ql = OptUi.Lbl(this, "Colas", 50, 22, 136); ql.TextAlign = ContentAlignment.MiddleCenter;
            _queues = OptUi.Add(this, new ListView(), 14, 42, 172, 380);
            _queues.View = View.Details; _queues.HeaderStyle = ColumnHeaderStyle.None; _queues.MultiSelect = false;
            _queues.HideSelection = false; _queues.LabelEdit = true;
            _queues.Columns.Add("", Dpi.Px(166));
            ImageList il = new ImageList(); il.ColorDepth = ColorDepth.Depth32Bit; il.ImageSize = new Size(Dpi.Px(16), Dpi.Px(16));
            foreach (string k in new string[] { "q-main", "q-sync", "q-limits" }) il.Images.Add(k, OptIcons.Page(k, Dpi.Px(16)));
            _queues.SmallImageList = il;
            _queues.Resize += delegate { if (_queues.Columns.Count > 0) _queues.Columns[0].Width = Math.Max(20, _queues.ClientSize.Width - 2); };
            _queues.SelectedIndexChanged += delegate { OnQueueSelected(); };
            _queues.BeforeLabelEdit += delegate(object o, LabelEditEventArgs e)
            {
                QueueDef q = _queues.Items[e.Item].Tag as QueueDef;
                if (q == null || q.IsMain || q.IsSync) e.CancelEdit = true;
            };
            _queues.AfterLabelEdit += delegate(object o, LabelEditEventArgs e)
            {
                QueueDef q = _queues.Items[e.Item].Tag as QueueDef;
                if (q == null || e.Label == null || e.Label.Trim().Length == 0) { e.CancelEdit = true; return; }
                q.Name = e.Label.Trim();
                if (q == _shown) _title.Text = QName(q);
                MarkDirty();
            };
            Button add = OptUi.Btn(this, "Nueva cola", 14, 428, 80);
            add.Click += delegate { NewQueue(); };
            _delQueue = OptUi.Btn(this, "Eliminar", 106, 428, 80);
            _delQueue.Click += delegate { DeleteQueue(); };
            OptUi.VLine(this, 200, 4, 450);

            _right = OptUi.Add(this, new Panel(), 208, 0, 431, 422);
            _title = OptUi.Lbl(_right, "", 0, 4, 431); _title.TextAlign = ContentAlignment.MiddleCenter;
            _tabs = OptUi.Add(_right, new TabControl(), 0, 22, 431, 398);
            BuildScheduleTab();
            BuildFilesTab();
            BuildLimitsPanel();

            _startNow = OptUi.Btn(this, "Iniciar ahora", 210, 428, 78);
            _startNow.Click += delegate { Apply(); if (_shown != null) _mgr.StartQueue(_shown.Id); RefreshFiles(); UpdateButtons(); };
            _stopNow = OptUi.Btn(this, "Detener", 294, 428, 78);
            _stopNow.Click += delegate { if (_shown != null) _mgr.StopQueue(_shown.Id); RefreshFiles(); UpdateButtons(); };
            Button help = OptUi.Btn(this, "Ayuda", 384, 428, 78);
            help.Click += delegate { OptionsHelp.Open(this, "programador"); };
            _apply = OptUi.Btn(this, "Aplicar", 474, 428, 78);
            _apply.Click += delegate { Apply(); };
            Button close = OptUi.Btn(this, "Cerrar", 558, 428, 78);
            close.DialogResult = DialogResult.OK;
            AcceptButton = close;

            HookDirty(_right);
            _loading = false;
            FillQueues(selectQueue);
            _dirty = false; _apply.Enabled = false;
            EndLayout();
        }

        // F1: la misma ayuda que el botón "Ayuda".
        protected override void OnHelpRequested(HelpEventArgs hevent)
        {
            hevent.Handled = true;
            OptionsHelp.Open(this, "programador");
        }

        // Cerrar (o la X) aplica lo que quede pendiente.
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_dirty) Apply();
            base.OnFormClosing(e);
        }

        void HookDirty(Control c)
        {
            foreach (Control k in c.Controls)
            {
                if (k is CheckBox) ((CheckBox)k).CheckedChanged += delegate { MarkDirty(); };
                else if (k is RadioButton) ((RadioButton)k).CheckedChanged += delegate { MarkDirty(); };
                else if (k is NumericUpDown) ((NumericUpDown)k).ValueChanged += delegate { MarkDirty(); };
                else if (k is DateTimePicker) ((DateTimePicker)k).ValueChanged += delegate { MarkDirty(); };
                else if (k is TextBox) k.TextChanged += delegate { MarkDirty(); };
                else if (k is ComboBox) ((ComboBox)k).SelectedIndexChanged += delegate { MarkDirty(); };
                HookDirty(k);
            }
        }

        void MarkDirty()
        {
            if (_loading) return;
            _dirty = true; _apply.Enabled = true;
            UpdateEnabled();
        }

        // Nombre como en IDM ("Descarga principal de la cola"...), sin cambiar lo guardado.
        static string QName(QueueDef q) { return Lang.T(MainForm.QueueLabel(q)); }

        static readonly string[] DayNames = { "Domingo", "Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado" };

        void BuildScheduleTab()
        {
            TabPage p = new TabPage("Programar"); p.UseVisualStyleBackColor = true; _tabs.TabPages.Add(p);
            const int R = 413;
            // En su propio panel: si no, serían del mismo grupo que "una vez a las" / "Diariamente".
            Panel mode = OptUi.Add(p, new Panel(), 4, 4, R - 4, 21);
            _modeOnce = OptUi.Radio(mode, "Descargar una sola vez", 4, 2, 196);
            _modePeriodic = OptUi.Radio(mode, "Sincronización periódica", 210, 2, 196);
            _modePeriodic.CheckedChanged += delegate
            {
                // Al pasar una cola propia a "Sincronización periódica" se propone repetirla.
                if (!_loading && _modePeriodic.Checked && !_repeat.Checked) _repeat.Checked = true;
            };
            OptUi.Line(p, 8, 28, R - 8);
            _atStartup = OptUi.Chk(p, "Comenzar la descarga al arrancar IDMClone", 8, 34, R - 8);
            OptUi.Line(p, 8, 56, R - 8);
            _enabled = OptUi.Chk(p, "Iniciar descarga a las", 8, 62, 150);
            _start = Time(p, 164, 60);
            _once = OptUi.Radio(p, "una vez a las", 24, 88, 110);
            _onceDate = OptUi.Add(p, new DateTimePicker(), 138, 86, 210, 20); _onceDate.Format = DateTimePickerFormat.Long;
            _repeat = OptUi.Chk(p, "Iniciar otra vez todo", 24, 88, 130);
            _repeatH = OptUi.Num(p, 0, 999, 2, 158, 86, 46);
            _repeatHL = OptUi.Lbl(p, "horas", 208, 88, 40);
            _repeatM = OptUi.Num(p, 0, 59, 0, 252, 86, 42);
            _repeatML = OptUi.Lbl(p, "minutos", 298, 88, 70);
            _daily = OptUi.Radio(p, "Diariamente", 24, 112, 94);
            _dailyLabel = OptUi.Lbl(p, "Diariamente", 24, 112, 94);
            _days = new CheckBox[7];
            for (int i = 0; i < 7; i++)
                _days[i] = OptUi.Chk(p, DayNames[i], 122 + (i / 3) * 104, 112 + (i % 3) * 18, 100);
            OptUi.Line(p, 8, 170, R - 8);
            _stopEnabled = OptUi.Chk(p, "Detener descarga a las", 8, 176, 150);
            _stop = Time(p, 164, 174);
            _retryLimit = OptUi.Chk(p, "Número de reintentos por cada archivo si la descarga falla :", 8, 200, 330);
            _retries = OptUi.Num(p, 1, 1000, 10, 342, 198, 56);
            OptUi.Line(p, 8, 224, R - 8);
            _runFile = OptUi.Chk(p, "Abrir el siguiente archivo cuando finalice:", 8, 230, 330);
            _runFilePath = OptUi.Box(p, "", 8, 250, R - 48);
            _runBrowse = OptUi.Btn(p, "...", R - 34, 249, 34);
            _runBrowse.Click += delegate
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Filter = "Todos los archivos|*.*";
                    if (d.ShowDialog(this) == DialogResult.OK) { _runFilePath.Text = d.FileName; _runFile.Checked = true; }
                }
            };
            CheckBox hangup = OptUi.Chk(p, "Colgar el módem al finalizar", 8, 278, 300);
            hangup.Enabled = false;   // IDMClone no marca conexiones de módem
            _exitDone = OptUi.Chk(p, "Salir de IDMClone al finalizar", 8, 296, 300);
            _shutdownDone = OptUi.Chk(p, "Apagar equipo al finalizar", 8, 314, 270);
            // Apagar, suspender o hibernar (como en las "Opciones de finalización" de la ventana de progreso).
            _shutdownMode = OptUi.Combo(p, R - 118, 312, 118, true, FinishOptions.ModeNames);
            _shutdownMode.SelectedIndex = 0;
            // Cierra los programas abiertos sin dejarles guardar (shutdown /f): solo con "Apagar equipo".
            _force = OptUi.Chk(p, "Forzar el fin de procesos", 24, 334, 260);
        }

        static DateTimePicker Time(Control p, int x, int y)
        {
            DateTimePicker t = OptUi.Add(p, new DateTimePicker(), x, y, 110, 20);
            t.Format = DateTimePickerFormat.Time; t.ShowUpDown = true; return t;
        }

        void BuildFilesTab()
        {
            TabPage p = new TabPage("Archivos en la cola"); p.UseVisualStyleBackColor = true; _tabs.TabPages.Add(p);
            OptUi.LblRight(p, "Descargar", 8, 10, 60);
            _maxActive = OptUi.Num(p, 1, 32, 1, 72, 8, 42);
            OptUi.Lbl(p, "archivos al mismo tiempo", 120, 10, 200);
            _files = OptUi.Add(p, new ListView(), 8, 34, 405, 296);
            _files.View = View.Details; _files.FullRowSelect = true; _files.HideSelection = false; _files.GridLines = true;
            _files.Columns.Add("Nombre de Archivo", Dpi.Px(180)); _files.Columns.Add("Tamaño", Dpi.Px(70));
            _files.Columns.Add("Estado", Dpi.Px(60)); _files.Columns.Add("Tiempo restante", Dpi.Px(80));
            ToolTip tip = new ToolTip();
            Button down = IconButton(p, "down", 8, 336); tip.SetToolTip(down, Lang.T("Bajar"));
            down.Click += delegate { MoveSelected(1); };
            Button up = IconButton(p, "up", 38, 336); tip.SetToolTip(up, Lang.T("Subir"));
            up.Click += delegate { MoveSelected(-1); };
            Button remove = IconButton(p, "remove", 68, 336); tip.SetToolTip(remove, Lang.T("Quitar de la cola"));
            remove.Click += delegate
            {
                foreach (ListViewItem li in _files.SelectedItems) _mgr.SetQueue((DownloadItem)li.Tag, DownloadItem.NoQueue);
                RefreshFiles();
            };
        }

        static Button IconButton(Control p, string icon, int x, int y)
        {
            Button b = OptUi.Add(p, new Button(), x, y, 26, 26);
            b.Image = OptIcons.Page(icon, Dpi.Px(18)); b.UseVisualStyleBackColor = true;
            return b;
        }

        void BuildLimitsPanel()
        {
            AppConfig c = _mgr.Config;
            _limitsPanel = OptUi.Add(_right, new Panel(), 0, 0, 431, 420);
            _limitsPanel.Visible = false;
            _quota = OptUi.Chk(_limitsPanel, "Límites de descarga", 30, 40, 300);
            Label l1 = OptUi.LblRight(_limitsPanel, "No descargar más de", 30, 66, 150);
            _quotaMB = OptUi.Num(_limitsPanel, 1, 1000000, c.QuotaMB, 186, 64, 64);
            Label l2 = OptUi.Lbl(_limitsPanel, "MBytes", 256, 66, 80);
            Label l3 = OptUi.LblRight(_limitsPanel, "cada", 30, 92, 150);
            _quotaHours = OptUi.Num(_limitsPanel, 1, 168, c.QuotaHours, 186, 90, 64);
            Label l4 = OptUi.Lbl(_limitsPanel, "Horas", 256, 92, 80);
            _quotaWarn = OptUi.Chk(_limitsPanel, "Mostrar advertencia antes de detener descargas", 30, 120, 370);
            OptUi.Note(_limitsPanel, "El límite vale para todas las descargas (también está en Opciones > Conexión). Al alcanzarlo, las descargas esperan y siguen solas en el periodo siguiente.", 30, 150, 380, 44);
            _quota.Checked = c.QuotaEnabled; _quotaWarn.Checked = c.QuotaWarning;
            _quotaControls = new Control[] { l1, _quotaMB, l2, l3, _quotaHours, l4, _quotaWarn };
        }

        void FillQueues(int select)
        {
            _loading = true;
            _queues.BeginUpdate();
            _queues.Items.Clear();
            foreach (QueueDef q in _mgr.Queues.Queues)
            {
                ListViewItem li = new ListViewItem(QName(q), q.IsSync ? "q-sync" : "q-main"); li.Tag = q;
                _queues.Items.Add(li);
            }
            ListViewItem lim = new ListViewItem(Lang.T("Límites de descarga"), "q-limits"); lim.Tag = LimitsTag;
            _queues.Items.Add(lim);
            _queues.EndUpdate();
            _loading = false;
            ListViewItem pick = null;
            foreach (ListViewItem li in _queues.Items)
            {
                QueueDef q = li.Tag as QueueDef;
                if ((q != null && q.Id == select) || (q == null && select < 0)) pick = li;
            }
            if (pick == null) pick = _queues.Items[0];
            pick.Selected = true; pick.Focused = true;
            if (_shown == null && !_limitsShown) OnQueueSelected();
        }

        void OnQueueSelected()
        {
            if (_loading || _queues.SelectedItems.Count == 0) return;
            object tag = _queues.SelectedItems[0].Tag;
            if (tag == (object)_shown && _shown != null) return;
            if (LimitsTag.Equals(tag) && _limitsShown) return;
            bool wasDirty = _dirty;
            SaveShown();
            _loading = true;
            QueueDef q = tag as QueueDef;
            _limitsShown = q == null;
            _limitsPanel.Visible = _limitsShown;
            _tabs.Visible = _title.Visible = !_limitsShown;
            if (q != null) ShowQueue(q); else _shown = null;
            _loading = false;
            _dirty = wasDirty; _apply.Enabled = wasDirty;
            UpdateEnabled();
            UpdateButtons();
        }

        void UpdateButtons()
        {
            bool custom = _shown != null && !_shown.IsMain && !_shown.IsSync;
            _delQueue.Enabled = custom;
            bool running = _shown != null && _mgr.IsQueueRunning(_shown.Id);
            _startNow.Enabled = _shown != null && !running;
            _stopNow.Enabled = running;
        }

        // Activa cada control según las casillas, como IDM (los días solo con "Iniciar descarga a las", etc.).
        void UpdateEnabled()
        {
            if (_limitsShown || _shown != null)
            {
                foreach (Control c in _quotaControls) c.Enabled = _quota.Checked;
            }
            if (_shown == null) return;
            bool periodic = _modePeriodic.Checked, start = _enabled.Checked;
            _once.Visible = _onceDate.Visible = _daily.Visible = !periodic;
            _repeat.Visible = _repeatH.Visible = _repeatHL.Visible = _repeatM.Visible = _repeatML.Visible = _dailyLabel.Visible = periodic;
            _start.Enabled = start;
            _once.Enabled = _daily.Enabled = start;
            _onceDate.Enabled = start && _once.Checked;
            _dailyLabel.Enabled = start;
            foreach (CheckBox d in _days) d.Enabled = start && (periodic || _daily.Checked);
            _repeatH.Enabled = _repeatM.Enabled = _repeatHL.Enabled = _repeatML.Enabled = _repeat.Checked;
            _stop.Enabled = _stopEnabled.Checked;
            _retries.Enabled = _retryLimit.Checked;
            _runFilePath.Enabled = _runBrowse.Enabled = _runFile.Checked;
            _shutdownMode.Enabled = _force.Enabled = _shutdownDone.Checked;
        }

        void ShowQueue(QueueDef q)
        {
            _shown = q;
            Schedule s = q.Schedule;
            _title.Text = QName(q);
            // La principal descarga una sola vez y la de sincronización es periódica (como IDM); las propias,
            // lo que se elija ("periódica" = volver a empezar cada cierto tiempo).
            _modeOnce.Enabled = !q.IsSync;
            _modePeriodic.Enabled = !q.IsMain || q.RepeatEnabled;
            bool periodic = q.IsSync || q.RepeatEnabled;
            _modePeriodic.Checked = periodic; _modeOnce.Checked = !periodic;
            _atStartup.Checked = s.StartAtStartup;
            _enabled.Checked = s.Enabled;
            _start.Value = new DateTime(2000, 1, 1, Clamp(s.StartHour, 0, 23), Clamp(s.StartMinute, 0, 59), 0);
            DateTime od;
            _onceDate.Value = DateTime.TryParseExact(s.OnceDate ?? "", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out od) ? od : DateTime.Today;
            _once.Checked = s.Once; _daily.Checked = !s.Once;
            for (int i = 0; i < 7; i++) _days[i].Checked = s.Days != null && i < s.Days.Length && s.Days[i];
            _repeat.Checked = q.RepeatEnabled;
            _repeatH.Value = Clamp(q.RepeatHours, 0, 999); _repeatM.Value = Clamp(q.RepeatMinutes, 0, 59);
            _stopEnabled.Checked = s.StopEnabled;
            _stop.Value = new DateTime(2000, 1, 1, Clamp(s.StopHour, 0, 23), Clamp(s.StopMinute, 0, 59), 0);
            _retryLimit.Checked = s.RetryLimitEnabled; _retries.Value = Clamp(s.RetryLimit, 1, 1000);
            _runFile.Checked = q.RunFileWhenDone; _runFilePath.Text = q.RunFile ?? "";
            _exitDone.Checked = q.ExitWhenDone; _shutdownDone.Checked = q.ShutdownWhenDone;
            _shutdownMode.SelectedIndex = Math.Max(0, Array.IndexOf(FinishOptions.Modes, q.EffectiveShutdownMode));
            _force.Checked = q.ForceTerminate;
            _maxActive.Value = Clamp(s.MaxActive, 1, 32);
            RefreshFiles();
        }

        // Pasa lo que hay en pantalla a la cola mostrada (en memoria).
        void SaveShown()
        {
            QueueDef q = _shown;
            if (q == null) return;
            Schedule s = q.Schedule;
            bool periodic = _modePeriodic.Checked;
            bool once = !periodic && _once.Checked;
            bool startChanged = s.Enabled != _enabled.Checked || s.StartHour != _start.Value.Hour || s.StartMinute != _start.Value.Minute || s.Once != once;
            s.StartAtStartup = _atStartup.Checked;
            s.Enabled = _enabled.Checked;
            s.StartHour = _start.Value.Hour; s.StartMinute = _start.Value.Minute;
            s.Once = once;
            s.OnceDate = _onceDate.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            if (s.Days == null || s.Days.Length != 7) s.Days = new bool[7];
            for (int i = 0; i < 7; i++) s.Days[i] = _days[i].Checked;
            s.StopEnabled = _stopEnabled.Checked;
            s.StopHour = _stop.Value.Hour; s.StopMinute = _stop.Value.Minute;
            s.MaxActive = (int)_maxActive.Value;
            s.RetryLimitEnabled = _retryLimit.Checked;
            s.RetryLimit = (int)_retries.Value;
            // Una hora de inicio nueva no debe dispararse por una ocurrencia ya pasada de hoy.
            if (startChanged) s.LastStartFired = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            q.RepeatEnabled = periodic && _repeat.Checked;
            q.RepeatHours = (int)_repeatH.Value; q.RepeatMinutes = (int)_repeatM.Value;
            if (q.RepeatEnabled && q.RepeatHours == 0 && q.RepeatMinutes == 0) q.RepeatMinutes = 1;
            q.RunFileWhenDone = _runFile.Checked && _runFilePath.Text.Trim().Length > 0;
            q.RunFile = _runFilePath.Text.Trim();
            q.ExitWhenDone = _exitDone.Checked;
            q.ShutdownWhenDone = _shutdownDone.Checked;
            int mi = _shutdownMode.SelectedIndex;
            q.ShutdownMode = mi >= 0 && mi < FinishOptions.Modes.Length ? FinishOptions.Modes[mi] : FinishOptions.Shutdown;
            q.ForceTerminate = _force.Checked;
        }

        public void Apply()
        {
            SaveShown();
            _mgr.SaveQueues();
            _mgr.PumpQueue();
            AppConfig c = _mgr.Config;
            if (c != null && (c.QuotaEnabled != _quota.Checked || c.QuotaMB != (int)_quotaMB.Value || c.QuotaHours != (int)_quotaHours.Value || c.QuotaWarning != _quotaWarn.Checked))
            {
                c.QuotaEnabled = _quota.Checked; c.QuotaMB = (int)_quotaMB.Value; c.QuotaHours = (int)_quotaHours.Value; c.QuotaWarning = _quotaWarn.Checked;
                if (_mgr.Persist) c.Save();
            }
            foreach (ListViewItem li in _queues.Items) { QueueDef q = li.Tag as QueueDef; if (q != null && li.Text != QName(q)) li.Text = QName(q); }
            _dirty = false; _apply.Enabled = false;
            UpdateButtons();
        }

        void NewQueue()
        {
            SaveShown();
            using (TextInputForm f = new TextInputForm("Introducir nombre de la cola", "Introducir un nombre para la nueva cola que se mostrará en la lista de colas", "Cola # " + (_mgr.Queues.Queues.Count + 1)))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                QueueDef q = _mgr.Queues.Add(f.Value);
                _mgr.SaveQueues();
                _shown = null; _limitsShown = false;
                FillQueues(q.Id);
            }
        }

        void DeleteQueue()
        {
            QueueDef q = _shown;
            if (q == null || q.IsMain || q.IsSync) return;
            if (Msg.Show(this, "¿Seguro que deseas eliminar " + q.Name + "? Sus archivos se quedan en la lista.", "Programador",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _shown = null;
            _mgr.DeleteQueue(q);
            FillQueues(QueueDef.MainId);
        }

        void RefreshFiles()
        {
            if (_shown == null) return;
            _files.BeginUpdate();
            _files.Items.Clear();
            foreach (DownloadItem it in _mgr.ItemsInQueue(_shown.Id))
            {
                ListViewItem li = new ListViewItem(it.FileName);
                li.SubItems.Add(it.Size >= 0 ? Fmt.Size(it.Size) : "");
                li.SubItems.Add(ListColumns.StatusText(it));
                li.SubItems.Add(it.Status == DownloadStatus.Downloading && it.SpeedBps > 0 ? Fmt.Eta(it.Remaining, it.SpeedBps) : "");
                li.Tag = it;
                _files.Items.Add(li);
            }
            _files.EndUpdate();
        }

        void MoveSelected(int dir)
        {
            if (_files.SelectedItems.Count != 1) return;
            DownloadItem it = (DownloadItem)_files.SelectedItems[0].Tag;
            _mgr.MoveInQueue(it, dir);
            RefreshFiles();
            foreach (ListViewItem li in _files.Items) if (li.Tag == it) { li.Selected = true; li.EnsureVisible(); }
        }
    }
}
