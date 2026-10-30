using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace IDMClone
{
    // ListView con doble búfer: sin parpadeo al refrescar cada medio segundo.
    public class DownloadListView : ListView
    {
        public DownloadListView()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct HDITEM
        {
            public int mask, cxy; public IntPtr pszText, hbm; public int cchTextMax, fmt; public IntPtr lParam; public int iImage, iOrder, type; public IntPtr pvFilter; public int state;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct LVITEM
        {
            public int mask, iItem, iSubItem, state, stateMask; public IntPtr pszText; public int cchTextMax, iImage; public IntPtr lParam;
            public int iIndent, iGroupId, cColumns; public IntPtr puColumns, piColFmt; public int iGroup;
        }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", EntryPoint = "SendMessage")] static extern IntPtr SendMessageHd(IntPtr h, int msg, IntPtr w, ref HDITEM l);
        [DllImport("user32.dll", EntryPoint = "SendMessage", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageLv(IntPtr h, int msg, IntPtr w, ref LVITEM l);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        const int LVM_GETHEADER = 0x101F, HDM_GETITEM = 0x120B, HDM_SETITEM = 0x120C, HDI_FORMAT = 0x4, HDF_SORTUP = 0x400, HDF_SORTDOWN = 0x200;
        const int LVM_SETEXTENDEDLISTVIEWSTYLE = 0x1036, LVS_EX_SUBITEMIMAGES = 0x2, LVM_SETITEMW = 0x104C, LVIF_IMAGE = 0x2, I_IMAGENONE = -2;

        // Iconos en las columnas que no son la primera (la "Q" de IDM lleva el icono de la cola).
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SendMessage(Handle, LVM_SETEXTENDEDLISTVIEWSTYLE, new IntPtr(LVS_EX_SUBITEMIMAGES), new IntPtr(LVS_EX_SUBITEMIMAGES));
        }

        public void SetSubItemImage(int index, int subItem, int image)
        {
            if (!IsHandleCreated || index < 0) return;
            LVITEM it = new LVITEM(); it.mask = LVIF_IMAGE; it.iItem = index; it.iSubItem = subItem; it.iImage = image < 0 ? I_IMAGENONE : image;
            SendMessageLv(Handle, LVM_SETITEMW, IntPtr.Zero, ref it);
        }

        // ¿Está el ratón (en coordenadas de pantalla) sobre la cabecera de las columnas?
        public bool IsOverHeader(Point screen)
        {
            if (!IsHandleCreated) return false;
            IntPtr header = SendMessage(Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
            RECT r;
            if (header == IntPtr.Zero || !GetWindowRect(header, out r)) return false;
            return screen.X >= r.Left && screen.X < r.Right && screen.Y >= r.Top && screen.Y < r.Bottom;
        }

        // Flecha de orden en la cabecera de la columna (como el Explorador).
        public void SetSortArrow(int column, bool descending)
        {
            if (!IsHandleCreated) return;
            IntPtr header = SendMessage(Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < Columns.Count; i++)
            {
                HDITEM item = new HDITEM(); item.mask = HDI_FORMAT;
                SendMessageHd(header, HDM_GETITEM, new IntPtr(i), ref item);
                item.fmt &= ~(HDF_SORTUP | HDF_SORTDOWN);
                if (i == column) item.fmt |= descending ? HDF_SORTDOWN : HDF_SORTUP;
                SendMessageHd(header, HDM_SETITEM, new IntPtr(i), ref item);
            }
        }
    }

    // Ventana principal al estilo de IDM: menús Tareas/Archivo/Descargas/Ver/Ayuda, barra de botones grandes,
    // panel "Categorías" y lista de descargas con columnas configurables.
    public class MainForm : Form
    {
        readonly DownloadManager _mgr;
        readonly AppConfig _cfg;
        readonly Schedule _sched;

        MenuStrip _menu;
        ToolStrip _toolbar;
        Panel _catPanel;
        TreeView _tree;
        Splitter _treeSplitter;
        DownloadListView _list;
        NotifyIcon _tray;
        ContextMenuStrip _listMenu, _headerMenu;
        ImageList _fileIcons, _treeIcons;
        Timer _uiTimer, _schedTimer, _updTimer;
        ToolStripMenuItem _miHideCats, _miArrange;
        FindQuery _find;  // última búsqueda (Buscar Siguiente)
        string _filter = "__all__";
        // Icono puesto en la columna "Q" de cada fila (para no repetir mensajes al control).
        readonly Dictionary<ListViewItem, int> _qImage = new Dictionary<ListViewItem, int>();
        readonly Dictionary<string, ListViewItem> _rows = new Dictionary<string, ListViewItem>();
        readonly Dictionary<string, ProgressForm> _progress = new Dictionary<string, ProgressForm>();
        readonly HashSet<string> _silent = new HashSet<string>(); // descargas de /n: sin ventanas
        readonly HashSet<string> _finishedWhilePending = new HashSet<string>(); // terminaron con "Información del archivo" abierto
        List<KeyValuePair<ColumnDef, int>> _layout;
        ColumnDef[] _colByIndex = new ColumnDef[0];
        bool _allowShow = true;
        FormWindowState _lastState = FormWindowState.Normal;

        // Para los tests: confirmar la nueva dirección, el apagado y los sonidos sin ventanas ni altavoces.
        public static Func<string, bool> ConfirmHook;
        public static Action ShutdownAction = delegate { Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false }); };
        public static Action<string> SoundHook;
        public Action ExitHook;

        // Arranque con /onboot: la ventana empieza oculta en la bandeja (como IDM).
        public bool StartHidden { set { _allowShow = !value; } }

        public MainForm(DownloadManager mgr, AppConfig cfg, Schedule sched)
        {
            _mgr = mgr; _cfg = cfg; _sched = sched;
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None; // se escala a mano al terminar (Dpi.ScaleForm, como los diálogos)
            Text = "IDMClone";
            Icon = AppIcon.Main;
            Font = Theme.UiFont; // como IDM: Tahoma 8 en la lista, el árbol y la barra (los menús, la del sistema)
            ClientSize = new Size(860, 460);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(600, 360);

            BuildList();
            BuildTree();     // panel "Categorías" + separador
            BuildToolbar();
            BuildMenu();
            // Orden de acoplamiento: WinForms acopla del último añadido al primero, así que
            // el menú (último) va arriba del todo, debajo la barra, las categorías a la izquierda
            // con su separador y la lista ocupa el resto. Como IDM, no hay barra de estado: lo que
            // decía (descargas en curso, velocidad, cola, limitador) va en el globo del icono de la bandeja.
            Controls.Add(_list);
            Controls.Add(_treeSplitter);
            Controls.Add(_catPanel);
            Controls.Add(_toolbar);
            Controls.Add(_menu);
            MainMenuStrip = _menu;
            BuildTray();
            ApplyCategoriesVisible();
            ResumeLayout(false);
            Dpi.ScaleForm(this);
            PerformLayout();

            _mgr.ItemFinished += OnItemFinished;
            _mgr.QueueFinished += OnQueueFinished;
            _mgr.QueueStarted += delegate { UiPost(delegate { PlaySound(SoundSetting.QueueStart); }); };
            _mgr.QueueStopped += delegate { UiPost(delegate { PlaySound(SoundSetting.QueueStop); }); };
            DownloadQuota.LimitReached += OnQuotaLimitReached; // evento estático: se quita en Dispose

            _uiTimer = new Timer(); _uiTimer.Interval = 500; _uiTimer.Tick += delegate { _mgr.Tick(); RefreshList(); UpdateStatus(); UpdateToolbarState(); }; _uiTimer.Start();
            _schedTimer = new Timer(); _schedTimer.Interval = 5000; _schedTimer.Tick += delegate { CheckSchedule(); ShowPendingUpdate(); }; _schedTimer.Start();
            // Comprobación semanal de actualizaciones (como IDM): la primera, 2 minutos después de abrir; luego cada hora
            // se mira si ya toca (IDMClone suele pasar semanas abierto en la bandeja).
            _updTimer = new Timer(); _updTimer.Interval = 2 * 60 * 1000;
            _updTimer.Tick += delegate { _updTimer.Interval = 60 * 60 * 1000; if (Updater.IsCheckDue(_cfg, DateTime.UtcNow)) AutoCheckForUpdates(); };
            _updTimer.Start();

            RefreshTree();
            RefreshList();
        }

        protected override void SetVisibleCore(bool value)
        {
            if (!_allowShow)
            {
                value = false;
                if (!IsHandleCreated) CreateHandle();
            }
            base.SetVisibleCore(value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Lang.Apply(this);
            try { AddClipboardFormatListener(Handle); } catch { }
            if (_cfg.DropTargetVisible) UiPost(delegate { SetDropTarget(true); });
            UpdateSortArrow();
        }

        protected override void Dispose(bool disposing)
        {
            DownloadQuota.LimitReached -= OnQuotaLimitReached;
            base.Dispose(disposing);
        }

        // Límites de descarga (Opciones > Conexión): al alcanzar la cuota, si está marcada "Mostrar advertencia
        // antes de detener descargas", un aviso en la bandeja por periodo. Llega desde un hilo de descarga.
        DateTime _quotaWarned = DateTime.MinValue;

        void OnQuotaLimitReached(DateTime untilUtc)
        {
            UiPost(delegate
            {
                UpdateStatus();
                RefreshList();
                if (!_cfg.QuotaWarning || untilUtc == _quotaWarned) return;
                _quotaWarned = untilUtc;
                Notice(8000, "Límites de descarga", "Se alcanzó el límite de descarga; las descargas siguen a las " + untilUtc.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), ToolTipIcon.Warning);
            });
        }

        // Aviso en la bandeja (traducido).
        void Balloon(int ms, string title, string text, ToolTipIcon icon)
        {
            if (_tray != null) _tray.ShowBalloonTip(ms, Lang.T(title), Lang.T(text), icon);
        }

        void UiPost(MethodInvoker a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { }
        }

        // --- Menús (como IDM 6.43: Tareas, Archivo, Descargas, Ver, Ayuda; sin el menú comercial "Registro") ---
        void BuildMenu()
        {
            _menu = new MenuStrip();
            _menu.Renderer = IdmMenuRenderer.Instance;

            ToolStripMenuItem tasks = new ToolStripMenuItem("&Tareas");
            Item(tasks, "Agregar nueva descarga", delegate { AddUrl(null); }, Keys.Control | Keys.N);
            Item(tasks, "Añadir descarga por lotes", delegate { AddBatch(); });
            Item(tasks, "Añadir descarga por lotes desde portapapeles", delegate { BatchFromClipboard(); });
            Item(tasks, "Iniciar captura del sitio", delegate { OpenGrabber(null, null); });
            Sep(tasks);
            ToolStripMenuItem drop = Item(tasks, "Mostrar arrastrar destino", delegate { SetDropTarget(!_cfg.DropTargetVisible); });
            tasks.DropDownOpening += delegate { drop.Checked = _cfg.DropTargetVisible; };
            Sep(tasks);
            ToolStripMenuItem exp = Sub(tasks, "Exportar");
            Item(exp, "A un archivo de exportación de IDM", delegate { ExportEf2(); });
            Item(exp, "A un archivo de texto", delegate { ExportToText(); });
            ToolStripMenuItem imp = Sub(tasks, "Importar");
            Item(imp, "Desde un archivo de exportación de IDM", delegate { ImportEf2(); });
            Item(imp, "Desde un archivo de texto", delegate { ImportFromText(); });
            Sep(tasks);
            Item(tasks, "Salir", delegate { ExitApp(); });

            ToolStripMenuItem file = new ToolStripMenuItem("&Archivo");
            ToolStripMenuItem fStop = Item(file, "Detener descarga", delegate { StopSelected(); });
            ToolStripMenuItem fRemove = Item(file, "Quitar", delegate { DeleteSelected(); });
            ToolStripMenuItem fNow = Item(file, "Descargar ahora", delegate { ResumeSelected(); });
            ToolStripMenuItem fRedl = Item(file, "Redescargar", delegate { RedownloadSelected(); });
            file.DropDownOpening += delegate
            {
                SelectionState s = State(Selected());
                fStop.Enabled = s.AnyRunning;
                fRemove.Enabled = fRedl.Enabled = s.Count > 0;
                fNow.Enabled = s.AnyStartable;
            };

            ToolStripMenuItem dl = new ToolStripMenuItem("&Descargas");
            ToolStripMenuItem pauseAll = Item(dl, "Pausar todas", delegate { PauseAll(); });
            ToolStripMenuItem stopAll = Item(dl, "Detener todas", delegate { _mgr.StopAll(); });
            Sep(dl);
            Item(dl, "Eliminar todas las completadas", delegate { DeleteCompleted(); });
            Sep(dl);
            Item(dl, "Buscar (Ctrl+F)", delegate { Find(); }, Keys.Control | Keys.F);
            Item(dl, "Buscar Siguiente (F3)", delegate { FindNext(); }, Keys.F3);
            Sep(dl);
            Item(dl, "Planificador", delegate { ShowScheduler(); });
            dl.DropDownItems.Add(QueueSubmenu("Iniciar cola", true));
            dl.DropDownItems.Add(QueueSubmenu("detener cola", false));
            Sep(dl);
            dl.DropDownItems.Add(LimiterSubmenu());
            Sep(dl);
            Item(dl, "Opciones", delegate { ShowOptions(null); });
            dl.DropDownOpening += delegate
            {
                bool running = AnyRunning();
                pauseAll.Enabled = running;
                stopAll.Enabled = running || AnyQueueActive();
            };

            ToolStripMenuItem view = new ToolStripMenuItem("&Ver");
            _miHideCats = Item(view, "Ocultar categorías", delegate { _cfg.HideCategories = !_cfg.HideCategories; _cfg.Save(); ApplyCategoriesVisible(); });
            _miArrange = Sub(view, "Organizar archivos");
            foreach (string id in ListColumns.SortOrder)
            {
                ColumnDef col = ListColumns.ById(id);
                if (col == null || col.SortTitle == null) continue;
                ToolStripMenuItem mi = Item(_miArrange, col.SortTitle, delegate { SortBy(col.Id, false); });
                mi.Name = "radio_sort_" + col.Id;
                mi.Tag = col.Id;
            }
            _miArrange.DropDownOpening += delegate { foreach (ToolStripItem i in _miArrange.DropDownItems) { ToolStripMenuItem m = i as ToolStripMenuItem; if (m != null) m.Checked = (string)m.Tag == _cfg.SortColumn; } };
            ToolStripMenuItem bar = Sub(view, "Barra de herramientas");
            bar.DropDownItems.Add("(estilos)");
            bar.DropDownOpening += delegate { bar.DropDownItems.Clear(); bar.DropDownItems.AddRange(ToolbarViewItems()); Lang.Items(bar.DropDownItems); };
            ToolStripMenuItem trayStyle = Sub(view, "IDMClone bandeja de iconos");
            ToolStripMenuItem t3d = Radio(trayStyle, "Estilo 3D", delegate { SetTrayStyle("3d"); });
            ToolStripMenuItem tClassic = Radio(trayStyle, "Estilo Clásico", delegate { SetTrayStyle("classic"); });
            ToolStripMenuItem tNone = Radio(trayStyle, "No mostrar", delegate { SetTrayStyle("none"); });
            trayStyle.DropDownOpening += delegate
            {
                string s = _cfg.TrayIconStyleOrDefault;
                t3d.Checked = s == "3d"; tClassic.Checked = s == "classic"; tNone.Checked = s == "none";
            };
            Item(view, "Personalizar lista de URL...", delegate { ChooseColumns(); });
            ToolStripMenuItem font = Sub(view, "Fuente");
            Item(font, "Elegir Fuente...", delegate { ChooseListFont(); });
            Item(font, "Restablecer fuente por defecto", delegate { SetListFont(null); });
            view.DropDownOpening += delegate { _miHideCats.Text = _cfg.HideCategories ? "Mostrar categorías" : "Ocultar categorías"; };
            // Idioma (como IDM: archivos .lng en la carpeta Languages).
            ToolStripMenuItem lang = new ToolStripMenuItem("Idioma");
            lang.DropDownItems.Add("(idiomas)");
            lang.DropDownOpening += delegate
            {
                lang.DropDownItems.Clear();
                ToolStripMenuItem es = Radio(lang, Lang.BuiltIn, delegate { SetLanguage(Lang.BuiltIn); });
                es.Checked = Lang.Current == Lang.BuiltIn;
                foreach (Lang.LangFile l in Lang.Available())
                {
                    string name = l.Name;
                    ToolStripMenuItem mi = Radio(lang, name, delegate { SetLanguage(name); });
                    mi.Checked = Lang.Current == name;
                    if (!string.IsNullOrEmpty(l.Author)) mi.ToolTipText = l.Author;
                }
            };
            Sep(view);
            view.DropDownItems.Add(lang);

            // Ayuda: lo de IDM que no abre páginas web (páginas HTML locales de la carpeta help).
            ToolStripMenuItem help = new ToolStripMenuItem("A&yuda");
            Item(help, "Contenidos de ayuda", delegate { ShowHelp("index"); });
            Item(help, "Programador y colas", delegate { ShowHelp("programador"); });
            Item(help, "Ayuda de Grabber", delegate { ShowHelp("grabber"); });
            Sep(help);
            Item(help, "Comprobar actualizaciones...", delegate { CheckForUpdates(); });
            Sep(help);
            Item(help, "Acerca de IDMClone", delegate { About(); });

            _menu.Items.Add(tasks); _menu.Items.Add(file); _menu.Items.Add(dl); _menu.Items.Add(view); _menu.Items.Add(help);
            _menu.Dock = DockStyle.Top;
        }

        static ToolStripMenuItem Item(ToolStripMenuItem parent, string text, EventHandler h) { return Item(parent, text, h, Keys.None); }

        // Como IDM, los menús no enseñan la combinación de teclas (Ctrl+N, Ctrl+F y F3 funcionan igual).
        static ToolStripMenuItem Item(ToolStripMenuItem parent, string text, EventHandler h, Keys keys)
        {
            ToolStripMenuItem i = new ToolStripMenuItem(text, null, h);
            if (keys != Keys.None) { i.ShortcutKeys = keys; i.ShowShortcutKeys = false; }
            parent.DropDownItems.Add(i);
            return i;
        }

        // Opción excluyente: se marca con "•" en vez de "✓" (IdmMenuRenderer).
        static ToolStripMenuItem Radio(ToolStripMenuItem parent, string text, EventHandler h)
        {
            ToolStripMenuItem i = Item(parent, text, h);
            i.Name = "radio";
            return i;
        }

        static ToolStripMenuItem RadioItem(string text, bool on, EventHandler h)
        {
            ToolStripMenuItem i = new ToolStripMenuItem(text, null, h);
            i.Name = "radio"; i.Checked = on;
            return i;
        }

        static ToolStripMenuItem Sub(ToolStripMenuItem parent, string text)
        {
            ToolStripMenuItem i = new ToolStripMenuItem(text);
            parent.DropDownItems.Add(i);
            return i;
        }

        static void Sep(ToolStripMenuItem parent) { parent.DropDownItems.Add(new ToolStripSeparator()); }

        // Nombre de una cola tal como lo muestra IDM ("Descarga principal de la cola"...). Solo se cambia
        // el texto en pantalla: queues.json guarda el nombre de siempre.
        public static string QueueLabel(QueueDef q)
        {
            if (q == null) return "";
            if (q.IsMain && (q.Name == "Cola principal" || string.IsNullOrEmpty(q.Name))) return "Descarga principal de la cola";
            if (q.IsSync && (q.Name == "Cola de sincronización" || string.IsNullOrEmpty(q.Name))) return "Sincronización de la cola";
            return q.Name;
        }

        // Nombre de una categoría como en IDM ("Comprimido", "Video"). Tampoco cambia lo guardado.
        public static string CategoryLabel(Category c)
        {
            if (c == null) return "";
            if (c.Name == "Comprimidos") return "Comprimido";
            if (c.Name == "Vídeo") return "Video";
            return c.Name;
        }

        // "Iniciar cola" / "detener cola" (menú Descargas): una entrada por cola, como IDM.
        ToolStripMenuItem QueueSubmenu(string text, bool start)
        {
            ToolStripMenuItem m = new ToolStripMenuItem(text);
            m.DropDownItems.Add("(colas)");
            m.DropDownOpening += delegate
            {
                m.DropDownItems.Clear();
                FillQueueItems(m.DropDownItems, start);
            };
            return m;
        }

        void FillQueueItems(ToolStripItemCollection items, bool start)
        {
            foreach (QueueDef q in _mgr.Queues.Queues)
            {
                QueueDef qq = q;
                ToolStripMenuItem mi = new ToolStripMenuItem((start ? "Iniciar " : "detener ") + QueueLabel(q), null, delegate { if (start) _mgr.StartQueue(qq.Id); else _mgr.StopQueue(qq.Id); });
                mi.Enabled = start != _mgr.IsQueueRunning(q.Id);
                items.Add(mi);
            }
            Lang.Items(items);
        }

        // "Limitador de Velocidad": Activar / • Desactivar | Configurar... (menú Descargas y bandeja).
        ToolStripMenuItem LimiterSubmenu()
        {
            ToolStripMenuItem lim = new ToolStripMenuItem("Limitador de Velocidad");
            ToolStripMenuItem on = Radio(lim, "Activar", delegate { SetLimiter(true); });
            ToolStripMenuItem off = Radio(lim, "Desactivar", delegate { SetLimiter(false); });
            Sep(lim);
            Item(lim, "Configurar...", delegate { using (SpeedLimiterForm f = new SpeedLimiterForm(_cfg)) f.ShowDialog(Visible ? this : null); UpdateStatus(); });
            lim.DropDownOpening += delegate { bool active = _cfg.EffectiveSpeedLimitKBps > 0; on.Checked = active; off.Checked = !active; };
            return lim;
        }

        // --- Barra de herramientas: botones grandes con el texto debajo, como IDM ---
        void BuildToolbar()
        {
            _toolbar = new ToolStrip();
            _toolbar.GripStyle = ToolStripGripStyle.Hidden;
            _toolbar.Dock = DockStyle.Top;
            _toolbar.Padding = new Padding(4, 2, 4, 2);
            _toolbar.Renderer = new ToolbarRenderer(); // lisa, #F0F0F0, resaltado azul claro al pasar el ratón (como IDM)
            _toolbar.Font = Theme.UiFont;
            // Como IDM: clic derecho en la barra para el estilo y "Personalizar...".
            ContextMenuStrip cm = new ContextMenuStrip();
            cm.Renderer = IdmMenuRenderer.Instance;
            cm.Items.Add("(estilos)");
            cm.Opening += delegate { cm.Items.Clear(); cm.Items.AddRange(ToolbarMenuItems()); Lang.Items(cm.Items); };
            _toolbar.ContextMenuStrip = cm;
            FillToolbar();
        }

        // Clic derecho en la barra (IDM): Personalizar... | Botones pequeños, Botones grandes | Neon, 3D Style.
        ToolStripItem[] ToolbarMenuItems()
        {
            List<ToolStripItem> r = new List<ToolStripItem>();
            r.Add(new ToolStripMenuItem("Personalizar...", null, delegate { CustomizeToolbar(); }));
            r.Add(new ToolStripSeparator());
            r.Add(RadioItem("Botones pequeños", !_cfg.ToolbarLarge, delegate { SetToolbarStyle(_cfg.ToolbarStyle, false); }));
            r.Add(RadioItem("Botones grandes", _cfg.ToolbarLarge, delegate { SetToolbarStyle(_cfg.ToolbarStyle, true); }));
            r.Add(new ToolStripSeparator());
            r.Add(RadioItem("Neon", _cfg.ToolbarStyle == "neon", delegate { SetToolbarStyle("neon", _cfg.ToolbarLarge); }));
            r.Add(RadioItem("3D Style", _cfg.ToolbarStyle != "neon", delegate { SetToolbarStyle("3d", _cfg.ToolbarLarge); }));
            return r.ToArray();
        }

        // Ver > Barra de herramientas (IDM): lo mismo, con "Personalizar barra de herramientas..." y los grandes primero.
        ToolStripItem[] ToolbarViewItems()
        {
            List<ToolStripItem> r = new List<ToolStripItem>();
            r.Add(new ToolStripMenuItem("Personalizar barra de herramientas...", null, delegate { CustomizeToolbar(); }));
            r.Add(new ToolStripSeparator());
            r.Add(RadioItem("Botones grandes", _cfg.ToolbarLarge, delegate { SetToolbarStyle(_cfg.ToolbarStyle, true); }));
            r.Add(RadioItem("Botones pequeños", !_cfg.ToolbarLarge, delegate { SetToolbarStyle(_cfg.ToolbarStyle, false); }));
            r.Add(new ToolStripSeparator());
            r.Add(RadioItem("Neon", _cfg.ToolbarStyle == "neon", delegate { SetToolbarStyle("neon", _cfg.ToolbarLarge); }));
            r.Add(RadioItem("3D Style", _cfg.ToolbarStyle != "neon", delegate { SetToolbarStyle("3d", _cfg.ToolbarLarge); }));
            return r.ToArray();
        }

        public void SetToolbarStyle(string style, bool large)
        {
            _cfg.ToolbarStyle = style == "neon" ? "neon" : "3d";
            _cfg.ToolbarLarge = large;
            _cfg.Save();
            FillToolbar();
        }

        void CustomizeToolbar()
        {
            using (ToolbarCustomizeForm f = new ToolbarCustomizeForm(ToolbarButtonIds(), _cfg.ToolbarStyle))
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    string v = string.Join(",", f.Result.ToArray());
                    _cfg.ToolbarButtons = v == ToolbarCatalog.Default ? null : v;
                    _cfg.Save();
                    FillToolbar();
                }
        }

        // Botones de la barra en orden ("|" = separador), sin repetidos ni desconocidos.
        List<string> ToolbarButtonIds() { return ToolbarCatalog.Parse(_cfg.ToolbarButtons); }

        void FillToolbar()
        {
            _toolbar.SuspendLayout();
            _toolbar.Items.Clear();
            int size = ToolbarCatalog.IconSize(_cfg.ToolbarLarge); // IDM: 32 px con texto debajo, o 26 px sin texto
            _toolbar.ImageScalingSize = new Size(size, size);
            foreach (string id in ToolbarButtonIds())
            {
                if (id == "|") { _toolbar.Items.Add(new ToolStripSeparator()); continue; }
                ToolbarCatalog.Button d = ToolbarCatalog.Find(id);
                switch (id)
                {
                    case "add": Btn(d, delegate { AddUrl(null); }); break;
                    case "resume": Btn(d, delegate { ResumeSelected(); }); break;
                    case "stop": Btn(d, delegate { StopSelected(); }); break;
                    case "stopall": Btn(d, delegate { _mgr.StopAll(); }); break;
                    case "delete": Btn(d, delegate { DeleteSelected(); }); break;
                    case "deletecompleted": Btn(d, delegate { DeleteCompleted(); }); break;
                    case "options": Btn(d, delegate { ShowOptions(null); }); break;
                    case "scheduler": Btn(d, delegate { ShowScheduler(); }); break;
                    case "startqueue": QueueBtn(d, true); break;
                    case "stopqueue": QueueBtn(d, false); break;
                    case "grabber": Btn(d, delegate { OpenGrabber(null, null); }); break;
                }
            }
            Lang.Items(_toolbar.Items);
            _toolbar.ResumeLayout();
        }

        void Btn(ToolbarCatalog.Button d, EventHandler h)
        {
            ToolStripButton b = new ToolStripButton(d.Text, Glyphs.Get(d.Id, _toolbar.ImageScalingSize.Width, _cfg.ToolbarStyle));
            b.Name = "tb_" + d.Id;
            b.ToolTipText = d.Tip;
            b.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            b.TextImageRelation = _cfg.ToolbarLarge ? TextImageRelation.ImageAboveText : TextImageRelation.ImageBeforeText;
            b.ImageScaling = ToolStripItemImageScaling.None;
            if (_cfg.ToolbarLarge) { b.AutoSize = true; b.Padding = new Padding(4, 0, 4, 0); }
            else { b.DisplayStyle = ToolStripItemDisplayStyle.Image; b.Padding = ToolbarCatalog.SmallPadding; } // IDM: los pequeños, sin texto y separados
            b.Click += h;
            _toolbar.Items.Add(b);
        }

        // Como IDM: el botón actúa sobre la cola principal y su flecha deja elegir otra cola.
        void QueueBtn(ToolbarCatalog.Button d, bool start)
        {
            ToolStripSplitButton b = new ToolStripSplitButton(d.Text, Glyphs.Get(d.Id, _toolbar.ImageScalingSize.Width, _cfg.ToolbarStyle));
            b.Name = "tb_" + d.Id;
            b.ToolTipText = d.Tip;
            b.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            b.TextImageRelation = _cfg.ToolbarLarge ? TextImageRelation.ImageAboveText : TextImageRelation.ImageBeforeText;
            b.ImageScaling = ToolStripItemImageScaling.None;
            b.ButtonClick += delegate { if (start) _mgr.StartQueue(); else _mgr.StopQueue(); };
            // La flecha abre lo mismo que Descargas > Iniciar cola / detener cola.
            b.DropDown.Renderer = IdmMenuRenderer.Instance;
            b.DropDownOpening += delegate
            {
                b.DropDownItems.Clear();
                FillQueueItems(b.DropDownItems, start);
            };
            b.DropDownItems.Add("(colas)");
            if (!_cfg.ToolbarLarge) { b.DisplayStyle = ToolStripItemDisplayStyle.Image; b.Padding = ToolbarCatalog.SmallPadding; }
            _toolbar.Items.Add(b);
        }

        // --- Panel "Categorías" ---
        void BuildTree()
        {
            _catPanel = new Panel();
            _catPanel.Dock = DockStyle.Left;
            _catPanel.Width = 150;
            _tree = new TreeView();
            _tree.Dock = DockStyle.Fill;
            _tree.HideSelection = false;
            _tree.ShowLines = true;
            _treeIcons = new ImageList(); _treeIcons.ColorDepth = ColorDepth.Depth32Bit; _treeIcons.ImageSize = new Size(Dpi.Px(16), Dpi.Px(16));
            foreach (string g in new string[] { "folder", "folder-open", "folder-unfinished", "folder-finished", "queues", "queue", "queue-sync", "grabber", "project",
                                                 "cat-general", "cat-compressed", "cat-documents", "cat-music", "cat-programs", "cat-video" }) _treeIcons.Images.Add(g, Glyphs.Get(g, Dpi.Px(16)));
            _tree.ImageList = _treeIcons;
            // Alto de fila según la letra (que crece con la escala de la pantalla) y los iconos.
            _tree.FontChanged += delegate { FitTreeRows(); };
            FitTreeRows();
            _tree.AfterSelect += delegate(object s, TreeViewEventArgs e)
            {
                if (e.Node != null && e.Node.Tag != null) { _filter = e.Node.Tag.ToString(); RefreshList(); }
            };
            // Clic derecho: el menú de IDM según el nodo (categoría, cola, proyecto del Grabber).
            _tree.NodeMouseClick += delegate(object s, TreeNodeMouseClickEventArgs e) { if (e.Button == MouseButtons.Right) _tree.SelectedNode = e.Node; };
            _tree.NodeMouseDoubleClick += delegate(object s, TreeNodeMouseClickEventArgs e)
            {
                string t = e.Node != null && e.Node.Tag != null ? e.Node.Tag.ToString() : "";
                if (t.StartsWith("grb:")) OpenGrabber(t.Substring(4), null);
            };
            _tree.ContextMenuStrip = BuildTreeMenu();
            // Cabecera como la de IDM: "Categorías" y una × para ocultar el panel.
            Panel head = new Panel(); head.Dock = DockStyle.Top; head.Height = 18; head.BackColor = SystemColors.Control;
            Label title = new Label(); title.Text = "Categorías"; title.Dock = DockStyle.Fill; title.TextAlign = ContentAlignment.MiddleLeft; title.Padding = new Padding(2, 0, 0, 0);
            Label close = new Label(); close.Text = "×"; close.Dock = DockStyle.Right; close.Width = 18; close.TextAlign = ContentAlignment.MiddleCenter;
            close.Font = new Font("Tahoma", 9.75f, FontStyle.Bold, GraphicsUnit.Point); close.Cursor = Cursors.Hand;
            close.Click += delegate { _cfg.HideCategories = true; _cfg.Save(); ApplyCategoriesVisible(); };
            head.Controls.Add(title); head.Controls.Add(close);
            _catPanel.Controls.Add(_tree);
            _catPanel.Controls.Add(head);
            _treeSplitter = new Splitter();
            _treeSplitter.Dock = DockStyle.Left;
            _treeSplitter.Width = 3;
            _treeSplitter.MinSize = 100;
        }

        string SelectedTag()
        {
            return _tree.SelectedNode != null && _tree.SelectedNode.Tag != null ? _tree.SelectedNode.Tag.ToString() : "";
        }

        // El menú se rehace al abrirse, como en IDM: categoría (Examinar, Propiedades | Agregar categoría | Eliminar),
        // cola (Iniciar ahora, detener | Editar cola, Programar | Eliminar | Crear nueva cola) o Grabber.
        ContextMenuStrip BuildTreeMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Renderer = IdmMenuRenderer.Instance;
            FillTreeMenu(m.Items, "__all__");
            m.Opening += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                m.Items.Clear();
                FillTreeMenu(m.Items, SelectedTag());
                Lang.Items(m.Items);
                if (m.Items.Count == 0) e.Cancel = true;
            };
            return m;
        }

        void FillTreeMenu(ToolStripItemCollection items, string tag)
        {
            int qid;
            if (tag.StartsWith("q:") && int.TryParse(tag.Substring(2), out qid))
            {
                QueueDef q = _mgr.Queues.Find(qid);
                bool run = _mgr.IsQueueRunning(qid);
                items.Add(MI("Iniciar ahora", !run, delegate { _mgr.StartQueue(qid); }));
                items.Add(MI("detener", run, delegate { _mgr.StopQueue(qid); }));
                items.Add(new ToolStripSeparator());
                items.Add(MI("Editar cola", true, delegate { ShowScheduler(qid, 1); }));
                items.Add(MI("Programar", true, delegate { ShowScheduler(qid, 0); }));
                items.Add(new ToolStripSeparator());
                items.Add(MI("Eliminar", q != null && !q.IsMain && !q.IsSync, delegate { DeleteQueue(q); }));
                items.Add(new ToolStripSeparator());
                items.Add(MI("Crear nueva cola", true, delegate { CreateQueue(); }));
                return;
            }
            if (tag == "__queues__")
            {
                items.Add(MI("Crear nueva cola", true, delegate { CreateQueue(); }));
                items.Add(MI("Planificador", true, delegate { ShowScheduler(); }));
                return;
            }
            if (tag == "__grabber__")
            {
                items.Add(MI("Iniciar captura del sitio", true, delegate { OpenGrabber(null, null); }));
                return;
            }
            if (tag.StartsWith("grb:"))
            {
                string n = tag.Substring(4);
                items.Add(MI("Abrir proyecto", true, delegate { OpenGrabber(n, null); }));
                items.Add(MI("Eliminar proyecto", true, delegate { DeleteGrabberProject(n); }));
                return;
            }
            // Categorías ("Todas las descargas", "No finalizadas" y "Finalizadas" son la General).
            Category c = CategoryOfTag(tag);
            items.Add(MI("Examinar", c != null, delegate { BrowseCategory(c); }));
            items.Add(MI("Propiedades", c != null, delegate { EditCategory(c); }));
            items.Add(new ToolStripSeparator());
            items.Add(MI("Agregar categoría", true, delegate { EditCategory(null); }));
            items.Add(new ToolStripSeparator());
            items.Add(MI("Eliminar", c != null && !c.IsGeneral, delegate { DeleteCategory(c); }));
        }

        static ToolStripMenuItem MI(string text, bool enabled, EventHandler h)
        {
            ToolStripMenuItem i = new ToolStripMenuItem(text, null, h);
            i.Enabled = enabled;
            return i;
        }

        // Categoría a la que se refiere un nodo del árbol.
        Category CategoryOfTag(string tag)
        {
            if (tag == "__all__" || tag == "__unfinished__" || tag == "__done__") return _cfg.FindCategory(Category.GeneralName);
            string name = CategoryNameOfTag(tag);
            return name != null ? _cfg.FindCategory(name) : null;
        }

        static string CategoryNameOfTag(string tag)
        {
            if (tag == null) return null;
            if (tag.StartsWith("cat:")) return tag.Substring(4);
            if (tag.StartsWith("uf:")) return tag.Substring(3);
            if (tag.StartsWith("fin:")) return tag.Substring(4);
            return null;
        }

        // "Examinar": abre la carpeta de la categoría.
        void BrowseCategory(Category c)
        {
            if (c == null) return;
            string dir = string.IsNullOrEmpty(c.Folder) ? _cfg.DefaultFolder : c.Folder;
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Shell.OpenFolderWindow(dir);
            }
            catch (Exception ex) { Msg.Show(this, ex.Message, "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        // "Crear nueva cola": pide el nombre ("Cola # 3"...) y la añade a la lista de colas.
        public QueueDef CreateQueue()
        {
            string suggested = "Cola # " + (_mgr.Queues.Queues.Count + 1);
            string name;
            if (QueueNameHook != null) name = QueueNameHook(suggested);
            else
                using (QueueNameForm f = new QueueNameForm(suggested))
                {
                    if (f.ShowDialog(Visible ? this : null) != DialogResult.OK) return null;
                    name = f.QueueName;
                }
            if (string.IsNullOrEmpty(name)) return null;
            QueueDef q = _mgr.Queues.Add(name.Trim());
            _mgr.SaveQueues();
            RefreshTree();
            return q;
        }

        // Para las pruebas: el nombre de la cola nueva sin mostrar la ventana.
        public static Func<string, string> QueueNameHook;

        public bool DeleteQueue(QueueDef q)
        {
            if (q == null || q.IsMain || q.IsSync) return false;
            string ask = "¿Seguro que deseas eliminar " + QueueLabel(q) + "? Sus archivos se quedan en la lista.";
            bool yes = ConfirmHook != null ? ConfirmHook(ask)
                : Msg.Show(this, ask, "IDMClone", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            if (!yes) return false;
            _mgr.DeleteQueue(q);
            if (_filter == "q:" + q.Id) _filter = "__queues__";
            RefreshTree();
            RefreshList();
            return true;
        }

        // --- Grabber (capturador de sitios) ---
        GrabberForm _grabber;

        // Abre el Grabber: un proyecto nuevo (opcionalmente con su página de inicio) o uno guardado.
        public GrabberForm OpenGrabber(string projectName, string startUrl)
        {
            if (_grabber != null && !_grabber.IsDisposed)
            {
                if (projectName != null && !string.Equals(_grabber.Project.Name, projectName, StringComparison.CurrentCultureIgnoreCase))
                    Msg.Show(this, "Por favor, cierra primero el proyecto de Grabber que está abierto.", "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Information);
                _grabber.WindowState = FormWindowState.Normal;
                _grabber.Activate();
                return _grabber;
            }
            GrabberProject p = null;
            if (projectName != null)
            {
                try { p = GrabberProject.Load(projectName); }
                catch (Exception ex) { Msg.Show(this, ex.Message, "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); return null; }
            }
            _grabber = new GrabberForm(_cfg, _mgr, p, startUrl);
            _grabber.ProjectsChanged += delegate { RefreshTree(); };
            _grabber.FormClosed += delegate { _grabber = null; RefreshTree(); RefreshList(); };
            _grabber.Show();
            return _grabber;
        }

        public bool DeleteGrabberProject(string name)
        {
            string q = "¿Deseas eliminar el proyecto de Grabber \"" + name + "\"?";
            bool yes = ConfirmHook != null ? ConfirmHook(q)
                : Msg.Show(this, q, "Eliminando proyecto de Grabber", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            if (!yes) return false;
            try { GrabberProject.Delete(name); }
            catch (Exception ex) { Msg.Show(this, ex.Message, "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            RefreshTree();
            return true;
        }

        // Ventana "Agregando categoría" (original == null) o "Editando propiedades de categoría".
        public void EditCategory(Category original)
        {
            using (CategoryForm f = new CategoryForm(_cfg, original))
            {
                if (f.ShowDialog(this) != DialogResult.OK || f.Result == null) return;
                ApplyCategoryEdit(original, f.Result);
            }
        }

        // Guarda la categoría, renombra la de las descargas que la tenían y ofrece (como IDM) colocar
        // en ella las descargas antiguas de esos tipos de archivo, sin mover nada del disco.
        public Category ApplyCategoryEdit(Category original, Category edited)
        {
            string oldName = original != null ? original.Name : null;
            string oldTypes = original != null ? (original.Extensions ?? "") + "|" + (original.Sites ?? "") : null;
            Category c = _cfg.ApplyCategory(original, edited);
            _cfg.Save();
            if (oldName != null && oldName != c.Name)
            {
                _mgr.RenameCategory(oldName, c.Name);
                if (CategoryNameOfTag(_filter) == oldName) _filter = _filter.Substring(0, _filter.IndexOf(':') + 1) + c.Name;
            }
            string types = (c.Extensions ?? "") + "|" + (c.Sites ?? "");
            if (!c.IsGeneral && c.Extensions.Length > 0 && types != oldTypes)
            {
                string q = "¿Te gustaría colocar todas las descargas antiguas con tipo de archivo \"" + c.Extensions
                    + "\" en esta categoría? No se moverán los archivos en el disco, sino que se asociarán con esta categoría.";
                bool yes = ConfirmHook != null ? ConfirmHook(q)
                    : Msg.Show(this, q, "Categorías de IDMClone", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                if (yes) _mgr.AssignOldDownloads(c);
            }
            if (original == null) _filter = "cat:" + c.Name;
            RefreshTree();
            RefreshList();
            return c;
        }

        public bool DeleteCategory(Category c)
        {
            if (c == null || c.IsGeneral) return false;
            string q = "¿Realmente quieres eliminar la categoría \"" + CategoryLabel(c) + "\" de la lista de categorías de IDMClone?";
            bool yes = ConfirmHook != null ? ConfirmHook(q)
                : Msg.Show(this, q, "Eliminando categoría", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            if (!yes) return false;
            _cfg.RemoveCategory(c);
            _cfg.Save();
            // Sus descargas pasan a la categoría que les toque por tipo (los archivos no se tocan).
            _mgr.ReassignDeletedCategory(c.Name);
            if (CategoryNameOfTag(_filter) == c.Name) _filter = "__all__";
            RefreshTree();
            RefreshList();
            return true;
        }

        // Cambia el idioma de la interfaz al momento (todas las ventanas abiertas) y lo recuerda.
        public void SetLanguage(string name)
        {
            if (!Lang.Load(name)) { Msg.Show(this, "No se encuentra el archivo del idioma \"" + name + "\".", "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            _cfg.Language = name == Lang.BuiltIn ? null : name;
            // Las carpetas de las categorías de siempre pasan al idioma nuevo (Comprimidos <-> Compressed...), con sus archivos.
            bool foldersChanged;
            List<string> notRenamed = CategoryFolders.Sync(_cfg, _mgr, _grabber != null && !_grabber.IsDisposed ? _grabber.Project : null, out foldersChanged);
            _cfg.Save();
            // La letra de la interfaz depende del idioma (chino: Microsoft YaHei UI; ver Theme.UiFont).
            if (Font.Name != Theme.UiFamily)
            {
                Font = Font.Size == Theme.UiFont.Size ? Theme.UiFont : new Font(Theme.UiFamily, Font.Size, Font.Style, Font.Unit);
                _toolbar.Font = _toolbar.Font.Size == Theme.UiFont.Size ? Theme.UiFont : new Font(Theme.UiFamily, _toolbar.Font.Size, _toolbar.Font.Style, _toolbar.Font.Unit);
            }
            BuildColumns();
            RefreshTree();
            Lang.Apply(this); // la ventana principal puede estar oculta en la bandeja
            Lang.ApplyAll();
            if (_tray != null && _tray.ContextMenuStrip != null) Lang.Items(_tray.ContextMenuStrip.Items);
            if (_headerMenu != null) Lang.Items(_headerMenu.Items);
            RefreshList();
            UpdateStatus();
            ShellMenu.Relabel(Application.ExecutablePath); // "Descargar con IDMClone" del Explorador, en el idioma nuevo
            if (notRenamed.Count > 0)
            {
                List<string> lines = new List<string>();
                foreach (string p in notRenamed) lines.Add(Lang.T(p));
                string text = Lang.T("Estas carpetas de categorías no se pudieron pasar al idioma nuevo y se quedan como estaban:") + "\n\n" + string.Join("\n", lines.ToArray());
                if (FolderNoticeHook != null) FolderNoticeHook(text);
                else Msg.Show(this, text, "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // Filas del árbol de ~17 px a 100 % (IDM), más si la letra es mayor.
        void FitTreeRows() { _tree.ItemHeight = Math.Max(_tree.Font.Height + Dpi.Px(4), Dpi.Px(17)); }

        void ApplyCategoriesVisible()
        {
            bool show = !_cfg.HideCategories;
            _catPanel.Visible = show;
            _treeSplitter.Visible = show;
        }

        // --- Lista de descargas ---
        void BuildList()
        {
            _list = new DownloadListView();
            // Agrupado: Ctrl+A o Mayús+clic cambian la selección elemento a elemento; la barra se actualiza una vez.
            _list.SelectedIndexChanged += delegate
            {
                if (_toolbarStatePending || !IsHandleCreated) return;
                _toolbarStatePending = true;
                BeginInvoke((MethodInvoker)delegate { _toolbarStatePending = false; UpdateToolbarState(); });
            };
            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            // Como IDM: cuadrícula gris clara y selección azul de fila completa.
            _list.FullRowSelect = true;
            _list.GridLines = true;
            _list.MultiSelect = true;
            _list.HideSelection = false;
            _list.ShowItemToolTips = true;
            _list.AllowColumnReorder = true;
            Font lf = _cfg.CreateListFont();
            if (lf != null) _list.Font = lf;
            _fileIcons = new ImageList(); _fileIcons.ColorDepth = ColorDepth.Depth32Bit; _fileIcons.ImageSize = new Size(Dpi.Px(16), Dpi.Px(16));
            _fileIcons.Images.Add(QueueImageKey, Glyphs.Get("queue", Dpi.Px(16)));
            _list.SmallImageList = _fileIcons;
            _list.ListViewItemSorter = new RowComparer(this);
            _list.HandleCreated += delegate { _qImage.Clear(); UpdateSortArrow(); UiPost(delegate { RefreshList(); }); };
            BuildColumns();
            _list.ColumnClick += delegate(object s, ColumnClickEventArgs e)
            {
                ColumnDef c = e.Column < _colByIndex.Length ? _colByIndex[e.Column] : null;
                if (c != null) SortBy(c.Id, c.Id == _cfg.SortColumn ? !_cfg.SortDescending : false);
            };
            _list.ColumnWidthChanged += delegate { if (!_buildingColumns) SaveLayout(); };
            _list.ColumnReordered += delegate { if (!_buildingColumns) UiPost(delegate { SaveLayout(); }); };
            _list.DoubleClick += delegate { DoubleClickSelected(); };
            // Arrastrar enlaces desde el navegador a la lista (como IDM).
            _list.AllowDrop = true;
            _list.DragEnter += delegate(object s, DragEventArgs e) { e.Effect = DropData.HasLinks(e.Data) ? DragDropEffects.Copy | DragDropEffects.Link : DragDropEffects.None; };
            _list.DragDrop += delegate(object s, DragEventArgs e) { LinksDropped(DropData.Links(e.Data)); };
            _list.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
                else if (e.KeyCode == Keys.Enter) { DoubleClickSelected(); e.Handled = true; }
                else if (e.KeyCode == Keys.M && e.Control) { MoveRenameSelected(); e.Handled = true; } // "Mover/Renombrar (Ctrl-M)"
                else if (e.KeyCode == Keys.A && e.Control) { foreach (ListViewItem i in _list.Items) i.Selected = true; e.Handled = true; }
            };
            BuildListMenu();
        }

        bool _buildingColumns;

        // "Nombre de archivo" va siempre primera en la colección (lleva el icono); el orden visible es DisplayIndex.
        void BuildColumns()
        {
            _buildingColumns = true;
            _layout = ListColumns.Parse(_cfg.Columns);
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                _rows.Clear();
                _qImage.Clear();
                _list.Columns.Clear();
                List<KeyValuePair<ColumnDef, int>> coll = new List<KeyValuePair<ColumnDef, int>>();
                foreach (KeyValuePair<ColumnDef, int> kv in _layout) if (kv.Key.Id == "name") coll.Add(kv);
                foreach (KeyValuePair<ColumnDef, int> kv in _layout) if (kv.Key.Id != "name") coll.Add(kv);
                _colByIndex = new ColumnDef[coll.Count];
                for (int i = 0; i < coll.Count; i++)
                {
                    // Los anchos se guardan a escala 100 %: en pantalla se multiplican por la escala.
                    ColumnHeader h = _list.Columns.Add(Lang.T(coll[i].Key.Title), Dpi.Px(coll[i].Value), i == 0 ? HorizontalAlignment.Left : coll[i].Key.Align);
                    h.Tag = coll[i].Key;
                    _colByIndex[i] = coll[i].Key;
                }
                for (int d = 0; d < _layout.Count; d++)
                    foreach (ColumnHeader h in _list.Columns)
                        if (h.Tag == _layout[d].Key) { h.DisplayIndex = d; break; }
            }
            finally { _list.EndUpdate(); _buildingColumns = false; }
            UpdateSortArrow();
        }

        void SaveLayout()
        {
            List<ColumnHeader> hs = new List<ColumnHeader>();
            foreach (ColumnHeader h in _list.Columns) hs.Add(h);
            hs.Sort(delegate(ColumnHeader a, ColumnHeader b) { return a.DisplayIndex.CompareTo(b.DisplayIndex); });
            List<KeyValuePair<ColumnDef, int>> cols = new List<KeyValuePair<ColumnDef, int>>();
            foreach (ColumnHeader h in hs) cols.Add(new KeyValuePair<ColumnDef, int>((ColumnDef)h.Tag, (int)Math.Round(h.Width / Dpi.Factor)));
            string layout = ListColumns.Format(cols);
            if (layout != _cfg.Columns) { _cfg.Columns = layout; _cfg.Save(); }
        }

        void ChooseColumns()
        {
            SaveLayout();
            using (ColumnsForm f = new ColumnsForm(_cfg.Columns))
            {
                if (f.ShowDialog(this) != DialogResult.OK || f.ResultLayout == null) return;
                ApplyColumns(f.ResultLayout);
            }
        }

        public void ApplyColumns(string layout)
        {
            _cfg.Columns = layout;
            _cfg.Save();
            BuildColumns();
            RefreshList();
        }

        public void SortBy(string column, bool descending)
        {
            _cfg.SortColumn = column;
            _cfg.SortDescending = descending;
            _cfg.Save();
            _list.Sort();
            UpdateSortArrow();
        }

        void UpdateSortArrow()
        {
            int idx = -1;
            for (int i = 0; i < _colByIndex.Length; i++) if (_colByIndex[i].Id == _cfg.SortColumn) idx = i;
            _list.SetSortArrow(idx, _cfg.SortDescending);
        }

        class RowComparer : System.Collections.IComparer
        {
            readonly MainForm _f;
            public RowComparer(MainForm f) { _f = f; }
            public int Compare(object x, object y)
            {
                DownloadItem a = ((ListViewItem)x).Tag as DownloadItem, b = ((ListViewItem)y).Tag as DownloadItem;
                if (a == null || b == null) return 0;
                ColumnDef c = ListColumns.ById(_f._cfg.SortColumn) ?? ListColumns.ById("added");
                int r = c.Compare(a, b);
                if (r == 0) r = a.QueueOrder.CompareTo(b.QueueOrder);
                return _f._cfg.SortDescending ? -r : r;
            }
        }

        // Menú de clic derecho sobre las descargas, como el de IDM (sin iconos):
        // Abrir, Abrir con..., Abrir carpeta | Mover/Renombrar (Ctrl-M) | Redescargar | Reanudar descarga, Detener descarga |
        // Actualizar dirección de descarga | Quitar | Añadir a la cola ►, Borrar desde cola de descarga | Al hacer doble clic ► | propiedades
        void BuildListMenu()
        {
            _listMenu = new ContextMenuStrip();
            _listMenu.Renderer = IdmMenuRenderer.Instance;
            ToolStripItem open = _listMenu.Items.Add("Abrir", null, delegate { DownloadItem it = One(); if (it != null) Shell.Open(this, it.FilePath); });
            ToolStripItem openWith = _listMenu.Items.Add("Abrir con...", null, delegate { DownloadItem it = One(); if (it != null) Shell.OpenWith(this, it.FilePath); });
            ToolStripItem folder = _listMenu.Items.Add("Abrir carpeta", null, delegate { DownloadItem it = One(); if (it != null) Shell.OpenFolder(it.FilePath); });
            _listMenu.Items.Add(new ToolStripSeparator());
            ToolStripItem move = _listMenu.Items.Add("Mover/Renombrar (Ctrl-M)", null, delegate { MoveRenameSelected(); });
            _listMenu.Items.Add(new ToolStripSeparator());
            ToolStripItem redl = _listMenu.Items.Add("Redescargar", null, delegate { RedownloadSelected(); });
            _listMenu.Items.Add(new ToolStripSeparator());
            ToolStripItem resume = _listMenu.Items.Add("Reanudar descarga", null, delegate { ResumeSelected(); });
            ToolStripItem stop = _listMenu.Items.Add("Detener descarga", null, delegate { StopSelected(); });
            _listMenu.Items.Add(new ToolStripSeparator());
            ToolStripItem refresh = _listMenu.Items.Add("Actualizar dirección de descarga", null, delegate { DownloadItem it = One(); if (it != null) RefreshAddress(it); });
            _listMenu.Items.Add(new ToolStripSeparator());
            ToolStripItem remove = _listMenu.Items.Add("Quitar", null, delegate { DeleteSelected(); });
            _listMenu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem addQ = new ToolStripMenuItem("Añadir a la cola");
            _listMenu.Items.Add(addQ);
            addQ.DropDownItems.Add("(colas)");
            addQ.DropDownOpening += delegate
            {
                addQ.DropDownItems.Clear();
                // Las completadas solo pueden ir a la de sincronización (para vigilar si cambian), como en IDM.
                bool allComplete = true;
                foreach (DownloadItem it in Selected()) if (it.Status != DownloadStatus.Complete) allComplete = false;
                foreach (QueueDef q in _mgr.Queues.Queues)
                {
                    if (allComplete && !q.IsSync) continue;
                    QueueDef qq = q;
                    addQ.DropDownItems.Add(new ToolStripMenuItem(QueueLabel(q), null, delegate { AddSelectedToQueue(qq); }));
                }
                addQ.DropDownItems.Add(new ToolStripMenuItem("Crear nueva cola", null, delegate { QueueDef nq = CreateQueue(); if (nq != null) AddSelectedToQueue(nq); }));
                Lang.Items(addQ.DropDownItems);
            };
            ToolStripItem delQ = _listMenu.Items.Add("Borrar desde cola de descarga", null, delegate { foreach (DownloadItem it in Selected()) _mgr.SetQueue(it, DownloadItem.NoQueue); RefreshList(); });
            _listMenu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem dbl = new ToolStripMenuItem("Al hacer doble clic");
            ToolStripMenuItem dblOpen = Radio(dbl, "Abrir", delegate { _cfg.DoubleClickAction = "open"; _cfg.Save(); });
            ToolStripMenuItem dblProps = Radio(dbl, "propiedades", delegate { _cfg.DoubleClickAction = "properties"; _cfg.Save(); });
            _listMenu.Items.Add(dbl);
            _listMenu.Items.Add(new ToolStripSeparator());
            ToolStripItem props = _listMenu.Items.Add("propiedades", null, delegate { PropertiesSelected(); });
            _listMenu.Opening += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                // Clic derecho en la cabecera: solo "Columnas..." (como IDM).
                if (_list.IsOverHeader(Control.MousePosition))
                {
                    e.Cancel = true;
                    _headerMenu.Show(Control.MousePosition);
                    return;
                }
                List<DownloadItem> sel = Selected();
                if (sel.Count == 0) { e.Cancel = true; return; }
                SelectionState st = State(sel);
                bool one = sel.Count == 1;
                bool complete = one && sel[0].Status == DownloadStatus.Complete;
                open.Enabled = openWith.Enabled = complete; // abrir un archivo a medias lo mostraría corrupto
                folder.Enabled = one;
                move.Enabled = one;
                redl.Enabled = true;
                resume.Enabled = st.AnyStartable;
                stop.Enabled = st.AnyRunning;
                refresh.Enabled = one && sel[0].Status != DownloadStatus.Complete && !sel[0].Url.StartsWith("ftp", StringComparison.OrdinalIgnoreCase);
                remove.Enabled = true;
                addQ.Enabled = true;
                delQ.Enabled = st.AnyInQueue;
                props.Enabled = one;
                dblOpen.Checked = _cfg.DoubleClickAction != "properties";
                dblProps.Checked = _cfg.DoubleClickAction == "properties";
            };
            _list.ContextMenuStrip = _listMenu;

            _headerMenu = new ContextMenuStrip();
            _headerMenu.Renderer = IdmMenuRenderer.Instance;
            _headerMenu.Items.Add("Columnas...", null, delegate { ChooseColumns(); });
            Lang.Items(_headerMenu.Items);
        }

        void AddSelectedToQueue(QueueDef q)
        {
            foreach (DownloadItem it in Selected()) if (it.Status != DownloadStatus.Complete || q.IsSync) _mgr.SetQueue(it, q.Id);
            RefreshList();
        }

        // Qué se puede hacer con las descargas elegidas (menús Archivo y de clic derecho).
        struct SelectionState { public int Count; public bool AnyRunning, AnyStartable, AnyInQueue; }

        static SelectionState State(List<DownloadItem> sel)
        {
            SelectionState s = new SelectionState();
            s.Count = sel.Count;
            foreach (DownloadItem it in sel)
            {
                if (IsRunning(it)) s.AnyRunning = true;
                else if (it.Status != DownloadStatus.Complete) s.AnyStartable = true;
                if (it.Queue != DownloadItem.NoQueue) s.AnyInQueue = true;
            }
            return s;
        }

        static bool IsRunning(DownloadItem it) { return it.Engine != null && it.Engine.IsRunning && !it.Engine.StopRequested; }

        // Como IDM: Reanudar, Detener, Detener todas y Eliminar se ven en gris cuando no hay nada a lo que aplicarse.
        bool _toolbarStatePending;

        void UpdateToolbarState()
        {
            if (_toolbar == null || _list == null) return;
            SelectionState s = State(Selected());
            SetToolbarEnabled("resume", s.AnyStartable);
            SetToolbarEnabled("stop", s.AnyRunning);
            SetToolbarEnabled("stopall", AnyRunning() || AnyQueueActive()); // también para una cola entre dos archivos
            SetToolbarEnabled("delete", s.Count > 0);
        }

        void SetToolbarEnabled(string id, bool on)
        {
            foreach (ToolStripItem b in _toolbar.Items.Find("tb_" + id, false)) if (b.Enabled != on) b.Enabled = on;
        }

        bool AnyRunning()
        {
            foreach (DownloadItem it in _mgr.Snapshot()) if (IsRunning(it)) return true;
            return false;
        }

        bool AnyQueueActive()
        {
            foreach (QueueDef q in _mgr.Queues.Queues) if (_mgr.IsQueueRunning(q.Id)) return true;
            return false;
        }

        // "Pausar todas": detiene lo que está bajando (las colas en marcha, también) y lo deja en pausa;
        // cada descarga se reanuda luego a mano o con su cola.
        public void PauseAll()
        {
            foreach (QueueDef q in _mgr.Queues.Queues.ToArray()) if (_mgr.IsQueueRunning(q.Id)) _mgr.StopQueue(q.Id);
            foreach (DownloadItem it in _mgr.Snapshot()) if (IsRunning(it)) _mgr.StopItem(it);
            RefreshList();
        }

        // --- Icono de la bandeja (como el de IDM, sin Registro, Contáctanos ni Pedir en línea) ---
        void BuildTray()
        {
            _tray = new NotifyIcon();
            _tray.Icon = TrayIconFor(_cfg.TrayIconStyleOrDefault);
            _tray.Text = "IDMClone";
            _tray.Visible = _cfg.TrayIconStyleOrDefault != "none";
            _tray.DoubleClick += delegate { ShowFromTray(); };
            ContextMenuStrip cm = new ContextMenuStrip();
            cm.Renderer = IdmMenuRenderer.Instance;
            cm.Items.Add("Ayuda", null, delegate { ShowHelp("index"); });
            cm.Items.Add("Acerca de IDMClone", null, delegate { About(); });
            cm.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem lim = LimiterSubmenu();
            lim.DropDownDirection = ToolStripDropDownDirection.Left; // como IDM: se abre hacia la izquierda
            cm.Items.Add(lim);
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add("Mostrar ventana de enlaces", null, delegate { ShowLinksWindow(); });
            cm.Items.Add("Restaurar", null, delegate { ShowFromTray(); });
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add("Salir", null, delegate { ExitApp(); });
            _tray.ContextMenuStrip = cm;
            Lang.Items(cm.Items);
        }

        static Icon TrayIconFor(string style) { return style == "classic" ? TrayIcons.Classic : AppIcon.Small; }

        // Ver > Bandeja de iconos: Estilo 3D, Estilo Clásico o No mostrar.
        public void SetTrayStyle(string style)
        {
            _cfg.TrayIconStyle = style == "classic" || style == "none" ? style : null;
            _cfg.Save();
            if (_tray == null) return;
            _tray.Icon = TrayIconFor(_cfg.TrayIconStyleOrDefault);
            _tray.Visible = _cfg.TrayIconStyleOrDefault != "none";
            UpdateStatus();
        }

        // La ventana "Descargar los enlaces..." que esté abierta (para "Mostrar ventana de enlaces").
        static LinkListForm OpenLinksWindow()
        {
            foreach (Form f in Application.OpenForms) { LinkListForm l = f as LinkListForm; if (l != null && !l.IsDisposed) return l; }
            return null;
        }

        void ShowLinksWindow()
        {
            LinkListForm l = OpenLinksWindow();
            if (l != null) BringToFront(l);
            else Msg.Show(Visible ? this : null, "No hay ninguna ventana de enlaces abierta.", "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // --- Árbol de categorías ---
        string CategoryIconKey(Category c)
        {
            // Como IDM: icono propio para Comprimido, Documentos, Música, Programas y Video; si no, el del tipo de archivo.
            string glyph = Glyphs.CategoryGlyph(c.Name, c.Extensions);
            if (glyph != null) return glyph;
            string ext = (c.Extensions ?? "").Trim();
            int sp = ext.IndexOf(' ');
            if (sp > 0) ext = ext.Substring(0, sp);
            if (ext.Length == 0) return "folder";
            string key = "ext:" + ext.ToLowerInvariant();
            if (!_treeIcons.Images.ContainsKey(key)) _treeIcons.Images.Add(key, ShellIcons.For("x." + ext, Dpi.Factor > 1.01f));
            return key;
        }

        // Como IDM: "Todas las descargas" con las categorías (sin "General", que es la de todo lo demás) en orden
        // alfabético, "No finalizadas" y "Finalizadas" (cada una con sus categorías, plegadas), "Proyectos Grabber" y "Colas".
        void RefreshTree()
        {
            string keep = _filter;
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            List<Category> cats = new List<Category>();
            foreach (Category c in _cfg.Categories) if (c != null && !c.IsGeneral) cats.Add(c);
            cats.Sort(delegate(Category a, Category b) { return string.Compare(Lang.T(CategoryLabel(a)), Lang.T(CategoryLabel(b)), StringComparison.CurrentCultureIgnoreCase); });
            TreeNode all = Node("Todas las descargas", "__all__", "folder-open"); _tree.Nodes.Add(all);
            foreach (Category c in cats) all.Nodes.Add(Node(CategoryLabel(c), "cat:" + c.Name, CategoryIconKey(c)));
            TreeNode unfinished = Node("No finalizadas", "__unfinished__", "folder-unfinished"); _tree.Nodes.Add(unfinished);
            foreach (Category c in cats) unfinished.Nodes.Add(Node(CategoryLabel(c), "uf:" + c.Name, CategoryIconKey(c)));
            TreeNode finished = Node("Finalizadas", "__done__", "folder-finished"); _tree.Nodes.Add(finished);
            foreach (Category c in cats) finished.Nodes.Add(Node(CategoryLabel(c), "fin:" + c.Name, CategoryIconKey(c)));
            // Proyectos guardados del Grabber (como en IDM, antes de las colas).
            TreeNode grb = Node("Proyectos Grabber", "__grabber__", "grabber"); _tree.Nodes.Add(grb);
            foreach (string n in GrabberProject.SavedNames()) grb.Nodes.Add(Node(n, "grb:" + n, "project"));
            TreeNode queues = Node("Colas", "__queues__", "queues"); _tree.Nodes.Add(queues);
            foreach (QueueDef q in _mgr.Queues.Queues) queues.Nodes.Add(Node(QueueLabel(q), "q:" + q.Id, q.IsSync ? "queue-sync" : "queue"));
            all.Expand(); queues.Expand(); grb.Expand();
            // Conservar el filtro que estaba seleccionado.
            TreeNode sel = FindNode(_tree.Nodes, keep) ?? all;
            if (sel.Parent != null && !sel.Parent.IsExpanded) sel.Parent.Expand();
            _tree.SelectedNode = sel;
            _tree.EndUpdate();
        }

        static TreeNode Node(string text, string tag, string icon)
        {
            TreeNode n = new TreeNode(Lang.T(text)); n.Tag = tag; n.ImageKey = icon; n.SelectedImageKey = icon; return n;
        }

        static TreeNode FindNode(TreeNodeCollection nodes, string tag)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.Tag != null && n.Tag.ToString() == tag) return n;
                TreeNode r = FindNode(n.Nodes, tag);
                if (r != null) return r;
            }
            return null;
        }

        bool Passes(DownloadItem it)
        {
            if (it.Pending) return false; // aún en el diálogo "Información del archivo"
            switch (_filter)
            {
                case "__all__": return true;
                case "__done__": return it.Status == DownloadStatus.Complete;
                case "__unfinished__": return it.Status != DownloadStatus.Complete;
                case "__queues__": return it.Queue != DownloadItem.NoQueue;
            }
            int qid;
            if (_filter != null && _filter.StartsWith("q:") && int.TryParse(_filter.Substring(2), out qid)) return it.Queue == qid;
            if (_filter != null && _filter.StartsWith("cat:")) return it.Category == _filter.Substring(4);
            if (_filter != null && _filter.StartsWith("uf:")) return it.Status != DownloadStatus.Complete && it.Category == _filter.Substring(3);
            if (_filter != null && _filter.StartsWith("fin:")) return it.Status == DownloadStatus.Complete && it.Category == _filter.Substring(4);
            return true;
        }

        // --- Lista ---
        const string QueueImageKey = "__cola";

        string IconKey(DownloadItem it)
        {
            string ext = ShellIcons.ExtensionOf(it.FileName);
            string key = ext.Length > 0 ? ext : "(sin)";
            if (!_fileIcons.Images.ContainsKey(key)) _fileIcons.Images.Add(key, ShellIcons.For(it.FileName, Dpi.Factor > 1.01f)); // a más escala, el icono grande reducido se ve nítido
            return key;
        }

        void RefreshList()
        {
            if (_list == null) return;
            HashSet<string> seen = new HashSet<string>();
            List<DownloadItem> snapshot = _mgr.Snapshot();
            bool structural = false, resort = false;
            int sortIdx = -1, qIdx = -1;
            for (int i = 0; i < _colByIndex.Length; i++)
            {
                if (_colByIndex[i].Id == _cfg.SortColumn) sortIdx = i;
                if (_colByIndex[i].Id == "q") qIdx = i;
            }
            // En la columna "Q" el orden depende de la cola, que no se ve como texto.
            bool sortByQueue = sortIdx >= 0 && sortIdx == qIdx;

            foreach (DownloadItem it in snapshot)
            {
                if (!Passes(it)) continue;
                seen.Add(it.Id);
                ListViewItem lvi;
                if (!_rows.TryGetValue(it.Id, out lvi))
                {
                    if (!structural) { _list.BeginUpdate(); structural = true; }
                    lvi = new ListViewItem(it.FileName);
                    for (int i = 1; i < _colByIndex.Length; i++) lvi.SubItems.Add("");
                    lvi.Tag = it;
                    _rows[it.Id] = lvi;
                    _list.Items.Add(lvi);
                    resort = true;
                }
                string key = IconKey(it);
                if (lvi.ImageKey != key) lvi.ImageKey = key;
                for (int i = 0; i < _colByIndex.Length; i++)
                {
                    string text = _colByIndex[i].Text(it) ?? "";
                    if (_colByIndex[i].Id == "status") { string quota = ProgressForm.QuotaStatus(it); if (quota != null) text = Lang.T(quota); }
                    if (lvi.SubItems[i].Text != text) { lvi.SubItems[i].Text = text; if (i == sortIdx) resort = true; }
                }
                if (sortByQueue)
                {
                    int want = ListColumns.ShowsQueue(it) ? it.Queue + 1 : 0, had;
                    if (_qImage.TryGetValue(lvi, out had) && had != want) resort = true;
                }
                string tip = it.Url + (string.IsNullOrEmpty(it.Error) ? "" : "\n" + it.Error);
                if (lvi.ToolTipText != tip) lvi.ToolTipText = tip;
            }

            // Quitar filas de items ya no visibles.
            List<string> toRemove = new List<string>();
            foreach (KeyValuePair<string, ListViewItem> kv in _rows)
                if (!seen.Contains(kv.Key)) toRemove.Add(kv.Key);
            if (toRemove.Count > 0 && !structural) { _list.BeginUpdate(); structural = true; }
            foreach (string id in toRemove) { _qImage.Remove(_rows[id]); _list.Items.Remove(_rows[id]); _rows.Remove(id); }

            if (resort) _list.Sort();
            if (structural) _list.EndUpdate();
            UpdateQueueIcons(qIdx);
        }

        // Icono de la cola en la columna "Q" (el control nativo lo guarda en cada fila: solo se manda si cambia).
        void UpdateQueueIcons(int qIdx)
        {
            if (qIdx < 0 || !_list.IsHandleCreated) return;
            int image = _fileIcons.Images.IndexOfKey(QueueImageKey);
            foreach (ListViewItem lvi in _list.Items)
            {
                DownloadItem it = lvi.Tag as DownloadItem;
                if (it == null) continue;
                int want = ListColumns.ShowsQueue(it) ? it.Queue + 1 : 0, had; // 0 = sin cola; si no, la cola + 1 (también para ordenar)
                if (_qImage.TryGetValue(lvi, out had) && had == want) continue;
                _list.SetSubItemImage(lvi.Index, qIdx, want > 0 ? image : -1);
                _qImage[lvi] = want;
            }
        }

        // Sin barra de estado (IDM no la tiene): el resumen va en el texto del icono de la bandeja.
        void UpdateStatus()
        {
            if (_tray == null) return;
            int dl = 0; long speed = 0;
            foreach (DownloadItem it in _mgr.Snapshot())
                if (it.Status == DownloadStatus.Downloading) { dl++; speed += it.SpeedBps; }
            string text = "IDMClone";
            if (dl > 0) text += "\n" + Lang.T("Descargando: " + dl) + "  " + ListColumns.Speed(speed);
            if (AnyQueueActive()) text += "\n" + Lang.T("Cola: en marcha");
            int lim = _cfg.EffectiveSpeedLimitKBps;
            if (lim > 0) text += "\n" + Lang.T("Limitador: " + lim + " KB/s");
            DateTime quota = DownloadQuota.BlockedUntilUtc;
            if (quota > DateTime.UtcNow) text += "\n" + Lang.T("Límite de descarga alcanzado: sigue a las " + quota.ToLocalTime().ToString("HH:mm"));
            if (text.Length > 63) text = text.Substring(0, 63); // límite de Windows para este texto
            if (_tray.Text != text) _tray.Text = text;
        }

        // --- Buscar (Ctrl+F) / Buscar Siguiente (F3) ---
        public void Find()
        {
            FindQuery q;
            using (FindForm f = new FindForm(_find))
            {
                if (f.ShowDialog(Visible ? this : null) != DialogResult.OK) return;
                q = f.Query;
            }
            if (string.IsNullOrEmpty(q.Text)) return;
            _find = q;
            FindNext();
        }

        // Selecciona la siguiente descarga de la lista que coincide (desde la seleccionada, dando la vuelta).
        public DownloadItem FindNext()
        {
            if (_find == null || string.IsNullOrEmpty(_find.Text)) { Find(); return null; }
            int n = _list.Items.Count;
            int start = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[_list.SelectedIndices.Count - 1] + 1 : 0;
            for (int k = 0; k < n; k++)
            {
                ListViewItem lvi = _list.Items[(start + k) % n];
                DownloadItem it = lvi.Tag as DownloadItem;
                if (!_find.Matches(it)) continue;
                _list.SelectedItems.Clear();
                lvi.Selected = true; lvi.Focused = true;
                lvi.EnsureVisible();
                return it;
            }
            if (FindNotFoundHook != null) FindNotFoundHook(_find.Text);
            else Msg.Show(this, "No se encuentra \"" + _find.Text + "\".", "Buscar Archivo", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        // Para las pruebas: qué buscar sin mostrar la ventana, y el aviso de "no se encuentra".
        public void SetFind(FindQuery q) { _find = q; }
        public static Action<string> FindNotFoundHook;

        // --- Ver > Fuente ---
        void ChooseListFont()
        {
            using (FontDialog d = new FontDialog())
            {
                d.Font = _list.Font;
                d.ShowEffects = false;
                d.FontMustExist = true;
                d.MinSize = 6; d.MaxSize = 36; // lo que admite AppConfig.CreateListFont
                try { if (d.ShowDialog(this) != DialogResult.OK) return; }
                catch (ArgumentException) { Msg.Show(this, "Solo se pueden usar fuentes TrueType.", "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                SetListFont(d.Font);
            }
        }

        // null = la de IDM (Tahoma 8).
        public void SetListFont(Font f)
        {
            _cfg.ListFont = AppConfig.FormatFont(f);
            _cfg.Save();
            Font use = _cfg.CreateListFont();
            Font old = _list.Font;
            if (use == null) _list.ResetFont(); else _list.Font = use;
            if (old != null && old != Font && old != Theme.UiFont && old != _list.Font) old.Dispose();
        }

        // --- Altas de descargas ---
        public void AddUrl(string preset)
        {
            using (AddUrlForm f = new AddUrlForm(preset, _cfg)) // con el historial de direcciones
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                AddOptions o = new AddOptions();
                o.Url = f.ResultUrl; o.LoginUser = f.ResultUser; o.LoginPassword = f.ResultPassword; o.StartNow = true;
                try { NewDownload(o, true); }
                catch (Exception ex) { Msg.Show(this, ex.Message, "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        // ¿Esta alta podría mostrar algún diálogo? Entonces no se hace dentro de una orden por tubería.
        bool MayAsk(string url)
        {
            return _cfg.ShowFileInfoDialog || (_cfg.DuplicateAction == "ask" && _mgr.FindDuplicate(url, null) != null);
        }

        // Punto común para las descargas nuevas (Agregar URL, navegador, línea de comandos).
        // interactive = se pueden mostrar diálogos ("Enlace duplicado", "Información del archivo", progreso).
        public DownloadItem NewDownload(AddOptions o, bool interactive)
        {
            _lastAddQueuedOnly = false;
            // Opciones > Descargas: sin el diálogo de inicio, "No iniciar la descarga, sólo añadir archivos a la
            // cola": lo que llega (navegador, portapapeles, Agregar URL, enlace soltado) espera en la cola principal,
            // sin ventana de progreso. La línea de comandos (/a) y las listas de enlaces van por su cuenta.
            bool queueOnly = interactive && o.StartNow && !_cfg.ShowFileInfoDialog && _cfg.AddToQueueWithoutDialog;
            string overwritePath = null;
            DownloadItem dup = _mgr.FindDuplicate(o);
            if (dup != null)
            {
                string action = _cfg.DuplicateAction;
                if (action == "ask")
                {
                    if (!interactive) action = "number";
                    else
                        using (DuplicateForm d = new DuplicateForm(dup))
                        {
                            if (d.ShowDialog(Visible ? this : null) != DialogResult.OK) return null;
                            action = d.Action;
                            if (d.Remember) { _cfg.DuplicateAction = action; _cfg.Save(); }
                        }
                }
                if (action == "resume")
                {
                    if (dup.Status == DownloadStatus.Complete) { if (interactive) ShowComplete(dup); }
                    else if (queueOnly)
                    {
                        // Sigue esperando su turno: a la cola principal si no estaba en ninguna, sin arrancarla.
                        bool running = dup.Engine != null && dup.Engine.IsRunning;
                        if (!running)
                        {
                            // Como StartItem: que la cola pueda relanzarla aunque el usuario la detuviera o fallara.
                            dup.UserStopped = false; dup.QueueFailures = 0; dup.QueueRetryAfter = DateTime.MinValue;
                            if (dup.Queue == DownloadItem.NoQueue) _mgr.SetQueue(dup, DownloadItem.MainQueue);
                            if (dup.Queue == DownloadItem.MainQueue) QueuedOnlyNotice(dup);
                            else { _lastAddQueuedOnly = true; Notice(3000, "IDMClone", "La descarga sigue en su cola: " + dup.FileName, ToolTipIcon.Info); }
                            if (_mgr.IsQueueRunning(dup.Queue)) _mgr.PumpQueue();
                        }
                    }
                    else { _mgr.StartItem(dup); if (interactive) ShowProgressFor(dup); }
                    return dup;
                }
                if (action == "overwrite")
                {
                    // El archivo que ya hay no se toca: la descarga nueva lo sustituye al terminar (o, sin
                    // carpeta temporal, al empezar a bajar). Así "Cancelar" lo deja todo como estaba.
                    overwritePath = dup.FilePath;
                    o.Folder = Path.GetDirectoryName(overwritePath);
                    o.FileName = Path.GetFileName(overwritePath);
                    o.OverwritePath = overwritePath;
                }
            }

            DownloadItem it;
            if (interactive && _cfg.ShowFileInfoDialog)
            {
                o.Pending = true;
                // Como IDM: empieza a bajar mientras se muestra el diálogo (necesita la carpeta temporal,
                // porque el nombre y la carpeta aún pueden cambiar). Si va a sustituir a otro archivo, no:
                // podría terminar y reemplazarlo antes de que el usuario decida.
                bool start = o.StartNow;
                o.StartNow = start && _cfg.StartWhileInfoDialog && _cfg.UseTempFolder && overwritePath == null;
                // El duplicado sigue en la lista hasta que se confirme; mientras, retenido: parado y sin que
                // la cola lo relance (sin carpeta temporal los dos escribirían en el mismo archivo).
                bool dupUserStopped = false, dupByQueue = false, dupWasRunning = false;
                if (overwritePath != null)
                {
                    dupUserStopped = dup.UserStopped; dupByQueue = dup.StartedByQueue;
                    dupWasRunning = _mgr.Hold(dup);
                }
                FileInfoForm f;
                DownloadItem added = null;
                try
                {
                    added = _mgr.Add(o);
                    f = ShowFileInfo(added);
                }
                catch
                {
                    // Sin diálogo no hay decisión: la descarga nueva (pendiente) sobra y el duplicado no se queda retenido.
                    if (added != null) _mgr.Remove(added, false);
                    if (overwritePath != null) _mgr.Release(dup, dupUserStopped, dupWasRunning, dupByQueue);
                    throw;
                }
                it = added;
                if (overwritePath != null)
                {
                    DownloadItem replaced = dup;
                    f.Decided += delegate(FileInfoForm x)
                    {
                        bool replaces = x.Choice != "cancel" && string.Equals(x.Item.FilePath, overwritePath, StringComparison.OrdinalIgnoreCase);
                        if (replaces) _mgr.Remove(replaced, false);
                        else _mgr.Release(replaced, dupUserStopped, dupWasRunning, dupByQueue);
                        RefreshList();
                    };
                }
                if (o.AutoCaptured)
                {
                    string url = o.Url;
                    f.Decided += delegate(FileInfoForm x) { if (x.Choice == "cancel") UiPost(delegate { AutoCaptureCancelled(url); }); };
                }
                return it;
            }
            if (overwritePath != null) _mgr.Remove(dup, false);
            if (queueOnly) { o.StartNow = false; o.Queue = DownloadItem.MainQueue; }
            it = _mgr.Add(o);
            if (o.StartNow && interactive) ShowProgressFor(it);
            if (queueOnly) QueuedOnlyNotice(it);
            RefreshList();
            return it;
        }

        // Como IDM: al cancelar OTRA VEZ (en menos de una hora) una descarga que el navegador pasó solo desde el mismo
        // sitio, se ofrece añadir el sitio o un patrón de direcciones a las excepciones ("Cancelando una descarga
        // iniciada automáticamente"). Las pruebas contestan con AutoCancelHook (null = no se mostró).
        readonly Dictionary<string, DateTime> _autoCancelled = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        public static Func<AutoCancelForm, DialogResult> AutoCancelHook;

        public void AutoCaptureCancelled(string url)
        {
            string host = AutoCancelForm.HostOf(url);
            if (host.Length == 0) return;
            DateTime prev;
            bool again = _autoCancelled.TryGetValue(host, out prev) && DateTime.Now - prev < TimeSpan.FromHours(1);
            _autoCancelled[host] = DateTime.Now;
            if (!again || !_cfg.AskOnAutoCaptureCancel) return;
            _autoCancelled.Remove(host); // la próxima vez vuelven a hacer falta dos
            using (AutoCancelForm f = new AutoCancelForm(url))
            {
                DialogResult r = AutoCancelHook != null ? AutoCancelHook(f) : f.ShowDialog(Visible ? this : null);
                bool changed = false;
                if (f.DontAskAgain) { _cfg.AskOnAutoCaptureCancel = false; changed = true; }
                if (r == DialogResult.OK)
                {
                    if (f.ExcludeSite && _cfg.AddCaptureExclusion(f.SiteHost)) changed = true;
                    if (f.ExcludePattern && _cfg.AddCaptureExclusion(f.Pattern)) changed = true;
                }
                if (changed) _cfg.Save();
            }
        }

        // La última alta de NewDownload se quedó en la cola principal sin empezar (por la opción de arriba).
        bool _lastAddQueuedOnly;
        // Para las pruebas: recibe el aviso de la bandeja en vez de mostrarlo.
        public static Action<string> NoticeHook;

        void QueuedOnlyNotice(DownloadItem it)
        {
            _lastAddQueuedOnly = true;
            Notice(3000, "IDMClone", "Añadida a la cola principal: " + it.FileName, ToolTipIcon.Info);
        }

        // Globo de la bandeja que las pruebas pueden interceptar.
        void Notice(int ms, string title, string text, ToolTipIcon icon)
        {
            if (NoticeHook != null) { NoticeHook(text); return; }
            if (_tray != null) Balloon(ms, title, text, icon);
        }

        public FileInfoForm ShowFileInfo(DownloadItem it)
        {
            FileInfoForm f = new FileInfoForm(_cfg, _mgr, it);
            f.Decided += delegate(FileInfoForm x)
            {
                DownloadItem d = x.Item;
                if (_reallyExit || IsDisposed) return; // saliendo de IDMClone: sin avisos nuevos
                // Terminó mientras se mostraba el diálogo y OnItemFinished calló porque estaba pendiente: el aviso toca
                // ahora ("Descarga completada", también con "Descargar más tarde", como IDM), con el archivo ya en la ruta
                // elegida. Si OnItemFinished aún no ha llegado, avisará él (así no sale dos veces).
                bool finished = _finishedWhilePending.Remove(d.Id);
                if ((x.Choice == "start" || x.Choice == "later") && d.Status == DownloadStatus.Complete)
                {
                    if (finished)
                    {
                        CheckForViruses(d); // al terminar aún estaba en la carpeta temporal: se analiza ya en su sitio
                        if (d.ShowCompleteDialog ?? _cfg.ShowCompleteDialog) ShowComplete(d);
                        else if (_tray != null) Balloon(4000, "Descarga completada", d.FileName, ToolTipIcon.Info);
                    }
                }
                else if (x.Choice == "start") ShowProgressFor(d);
                RefreshList();
            };
            f.Show();
            BringToFront(f);
            return f;
        }

        static void BringToFront(Form f)
        {
            if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal;
            f.Activate();
            f.TopMost = true; f.TopMost = false; // delante aunque el navegador tenga el foco
        }

        // Ventana de progreso al iniciar una descarga, según Opciones > Descargas.
        public void ShowProgressFor(DownloadItem it)
        {
            if (_cfg.ProgressDialogMode == "none" || _silent.Contains(it.Id) || it.Status == DownloadStatus.Complete) return;
            List<DownloadItem> one = new List<DownloadItem>(); one.Add(it);
            ShowProgress(one);
            ProgressForm pf;
            if (_cfg.ProgressDialogMode == "minimized" && _progress.TryGetValue(it.Id, out pf)) pf.WindowState = FormWindowState.Minimized;
        }

        public void ShowComplete(DownloadItem it)
        {
            CompleteForm f = new CompleteForm(it);
            f.FormClosed += delegate { if (f.DontShowAgain) { _cfg.ShowCompleteDialog = false; _cfg.Save(); } };
            f.Show();
            BringToFront(f);
        }

        // Tareas > Importar > Desde un archivo de texto: saca todas las URL del texto (como IDM) y las añade a la cola principal.
        void ImportFromText()
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = "Archivos de texto (*.txt)|*.txt|Todos los archivos|*.*";
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                ShowLinksFromFile(ofd.FileName, "Importar enlaces a IDMClone");
            }
        }

        // Lista de enlaces con las direcciones de un archivo de texto o de una página guardada (.htm/.html):
        // Importar y "Descargar los enlaces con IDMClone" del menú contextual del Explorador.
        public LinkListForm ShowLinksFromFile(string file, string title)
        {
            IWin32Window owner = Visible ? this : null;
            List<string> urls;
            try { urls = ShellMenu.LinksInFile(file); }
            catch (Exception ex) { Msg.Show(owner, "No se pudo leer el archivo: " + ex.Message, "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); return null; }
            if (urls.Count == 0) { Msg.Show(owner, "El archivo no contiene enlaces web.", "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Information); return null; }
            List<LinkEntry> links = new List<LinkEntry>();
            foreach (string u in urls) links.Add(new LinkEntry(u, ""));
            return ShowLinks(links, title);
        }

        // Tareas > Exportar > A un archivo de texto: una dirección por línea (las seleccionadas o todas).
        void ExportToText()
        {
            List<DownloadItem> items = Selected();
            if (items.Count == 0) items = _mgr.Snapshot();
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "Archivos de texto (*.txt)|*.txt";
                d.FileName = "descargas.txt";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                StringBuilder sb = new StringBuilder();
                foreach (DownloadItem it in items) if (!it.Pending) sb.AppendLine(it.Url);
                try { File.WriteAllText(d.FileName, sb.ToString(), new UTF8Encoding(false)); }
                catch (Exception ex) { Msg.Show(this, ex.Message, "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        // Lista de enlaces ("Descargar todos los enlaces", lotes, importar, portapapeles).
        public LinkListForm ShowLinks(List<LinkEntry> links, string title)
        {
            if (links == null || links.Count == 0) return null;
            LinkListForm f = new LinkListForm(_cfg, _mgr, links, title);
            f.FormClosed += delegate { if (f.DialogResult == DialogResult.OK) { foreach (DownloadItem it in f.Added) if (it.Queue == DownloadItem.NoQueue) ShowProgressFor(it); } RefreshList(); };
            f.Show();
            BringToFront(f);
            return f;
        }

        void AddBatch()
        {
            using (BatchForm f = new BatchForm(null))
                if (f.ShowDialog(this) == DialogResult.OK && f.Result != null) ShowLinks(f.Result, "Añadir descarga por lotes");
        }

        void BatchFromClipboard()
        {
            string text = null;
            try { text = Clipboard.GetText(); } catch { }
            List<LinkEntry> links = new List<LinkEntry>();
            foreach (string u in Util.ExtractUrls(text ?? "")) links.Add(new LinkEntry(u, ""));
            if (links.Count == 0) { Msg.Show(this, "¡No se encuentra ninguna dirección web en el portapapeles!", "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            ShowLinks(links, "Añadir descarga por lotes desde portapapeles");
        }

        // Enlaces soltados en la lista o en el destino de arrastre: uno solo va como "Agregar URL".
        public void LinksDropped(List<string> urls)
        {
            if (urls == null || urls.Count == 0) return;
            if (urls.Count == 1)
            {
                AddOptions o = new AddOptions(); o.Url = urls[0]; o.StartNow = true;
                try { NewDownload(o, true); } catch (Exception ex) { Msg.Show(this, ex.Message, "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                return;
            }
            List<LinkEntry> links = new List<LinkEntry>();
            foreach (string u in urls) links.Add(new LinkEntry(u, ""));
            ShowLinks(links, "Descargar los enlaces soltados con IDMClone");
        }

        void ImportEf2()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "Archivos de exportación de IDM (*.ef2)|*.ef2|Todos los archivos|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                List<LinkEntry> links;
                try { links = Ef2.Parse(File.ReadAllText(d.FileName)); }
                catch (Exception ex) { Msg.Show(this, "No se puede leer el archivo: " + ex.Message, "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                if (links.Count == 0) { Msg.Show(this, "El archivo no es un archivo de exportación válido o no contiene enlaces.", "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                ShowLinks(links, "Importar a IDMClone");
            }
        }

        void ExportEf2()
        {
            List<DownloadItem> items = Selected();
            if (items.Count == 0) items = _mgr.Snapshot();
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "Archivos de exportación de IDM (*.ef2)|*.ef2";
                d.FileName = "descargas.ef2";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(d.FileName, Ef2.Export(items), new UTF8Encoding(false)); }
                catch (Exception ex) { Msg.Show(this, ex.Message, "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        // --- Destino de arrastre flotante ---
        DropTargetForm _dropTarget;

        public void SetDropTarget(bool show)
        {
            _cfg.DropTargetVisible = show;
            _cfg.Save();
            if (!show) { if (_dropTarget != null) _dropTarget.Hide(); return; }
            if (_dropTarget == null)
            {
                _dropTarget = new DropTargetForm();
                _dropTarget.Dropped += delegate(List<string> l) { LinksDropped(l); };
                _dropTarget.OpenMain += delegate { ShowFromTray(); };
                _dropTarget.AddUrl += delegate { ShowFromTray(); AddUrl(null); };
                _dropTarget.Closed2 += delegate { _cfg.DropTargetVisible = false; _cfg.Save(); };
                _dropTarget.LocationChanged += delegate { if (_dropTarget.Visible) { _cfg.DropTargetX = _dropTarget.Left; _cfg.DropTargetY = _dropTarget.Top; } };
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                Point p = _cfg.DropTargetX >= 0 ? new Point(_cfg.DropTargetX, _cfg.DropTargetY) : new Point(wa.Right - Dpi.Px(120), wa.Top + Dpi.Px(120));
                bool onScreen = false;
                foreach (Screen sc in Screen.AllScreens) if (sc.WorkingArea.Contains(p)) onScreen = true;
                _dropTarget.Location = onScreen ? p : new Point(wa.Right - Dpi.Px(120), wa.Top + Dpi.Px(120));
            }
            _dropTarget.Show();
        }

        public DropTargetForm DropTarget { get { return _dropTarget; } }

        // --- Portapapeles (Opciones > General: "Comenzar a descargar las URLs copiadas al portapapeles") ---
        [DllImport("user32.dll", SetLastError = true)] static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        const int WM_CLIPBOARDUPDATE = 0x031D;
        string _lastClip;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE && _cfg.MonitorClipboard)
            {
                string text = null;
                try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }
                if (text != null) UiPost(delegate { OnClipboardText(text); });
            }
            base.WndProc(ref m);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            try { RemoveClipboardFormatListener(Handle); } catch { }
            base.OnHandleDestroyed(e);
        }

        // Como IDM: una sola dirección copiada cuyo tipo de archivo está en Opciones > Tipos de archivo.
        public DownloadItem OnClipboardText(string text)
        {
            if (!_cfg.MonitorClipboard || string.IsNullOrEmpty(text)) return null;
            string t = text.Trim();
            if (t.IndexOfAny(new char[] { ' ', '\r', '\n', '\t' }) >= 0 || !Util.LooksLikeUrl(t) || t == _lastClip) return null;
            _lastClip = t;
            string ext = LinkListForm.ExtOf(t);
            bool watched = false;
            foreach (string e in (_cfg.CaptureExts ?? "").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (ext.Length > 0 && Util.WildcardMatch(ext, e)) { watched = true; break; }
            if (!watched) return null;
            AddOptions o = new AddOptions(); o.Url = t; o.StartNow = true;
            try { return NewDownload(o, true); } catch { return null; }
        }

        List<DownloadItem> Selected()
        {
            List<DownloadItem> r = new List<DownloadItem>();
            foreach (ListViewItem lvi in _list.SelectedItems) { DownloadItem it = lvi.Tag as DownloadItem; if (it != null) r.Add(it); }
            return r;
        }

        DownloadItem One() { List<DownloadItem> s = Selected(); return s.Count > 0 ? s[0] : null; }

        void ResumeSelected()
        {
            foreach (DownloadItem it in Selected())
            {
                if (it.Status == DownloadStatus.Complete) continue;
                bool running = it.Engine != null && it.Engine.IsRunning && !it.Engine.StopRequested;
                _mgr.StartItem(it);
                if (!running) ShowProgressFor(it);
            }
        }

        void StopSelected() { foreach (DownloadItem it in Selected()) _mgr.StopItem(it); }

        void RedownloadSelected()
        {
            foreach (DownloadItem it in Selected())
            {
                _mgr.Redownload(it);
                ShowProgressFor(it);
            }
            RefreshList();
        }

        void DeleteSelected()
        {
            List<DownloadItem> sel = Selected();
            if (sel.Count == 0) return;
            // "Confirmar eliminación de descargas" (IDM); con "No mostrar este diálogo otra vez" solo se quitan de la lista.
            bool delFile = false;
            // Mayús+Supr (o Mayús al elegir Quitar) vuelve a preguntar aunque se marcara "No mostrar".
            if (!_cfg.SkipDeleteConfirm || (Control.ModifierKeys & Keys.Shift) != 0)
                using (DeleteDownloadsForm f = new DeleteDownloadsForm())
                {
                    if (f.ShowDialog(this) != DialogResult.OK) return;
                    delFile = f.DeleteFiles;
                    if (f.DontAskAgain) { _cfg.SkipDeleteConfirm = true; _cfg.Save(); }
                }
            Cursor = Cursors.WaitCursor;
            try
            {
                foreach (DownloadItem it in sel)
                {
                    ProgressForm pf;
                    if (_progress.TryGetValue(it.Id, out pf)) pf.Close();
                    _mgr.Remove(it, delFile);
                }
            }
            finally { Cursor = Cursors.Default; }
            RefreshList();
        }

        void DeleteCompleted()
        {
            bool any = false;
            foreach (DownloadItem it in _mgr.Snapshot()) if (it.Status == DownloadStatus.Complete) { any = true; break; }
            if (!any) return;
            if (Msg.Show(this, "¿Estás seguro de que quieres quitar de la lista todas las descargas completadas?\n(Los archivos no se borran del disco.)",
                "IDMClone", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _mgr.RemoveCompleted();
            RefreshList();
        }

        void DoubleClickSelected()
        {
            DownloadItem it = One();
            if (it == null) return;
            if (_cfg.DoubleClickAction == "properties") { ShowProperties(it); return; }
            if (it.Status == DownloadStatus.Complete) { Shell.Open(this, it.FilePath); return; }
            List<DownloadItem> one = new List<DownloadItem>(); one.Add(it);
            ShowProgress(one);
        }

        void PropertiesSelected() { DownloadItem it = One(); if (it != null) ShowProperties(it); }

        void ShowProperties(DownloadItem it)
        {
            using (PropertiesForm f = new PropertiesForm(_cfg, _mgr, it)) f.ShowDialog(this);
            RefreshList();
        }

        void MoveRenameSelected()
        {
            DownloadItem it = One();
            if (it == null) return;
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Title = "Mover/Renombrar";
                try { d.InitialDirectory = Path.GetDirectoryName(it.FilePath); d.FileName = it.FileName; } catch { }
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { _mgr.MoveRename(it, d.FileName); }
                catch (Exception ex) { Msg.Show(this, ex.Message, "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
            RefreshList();
        }

        // Una sola ventana de progreso por descarga: si ya está abierta, se trae al frente.
        public void ShowProgress(List<DownloadItem> items)
        {
            foreach (DownloadItem it in items)
            {
                ProgressForm pf;
                if (_progress.TryGetValue(it.Id, out pf) && !pf.IsDisposed)
                {
                    if (pf.WindowState == FormWindowState.Minimized) pf.WindowState = FormWindowState.Normal;
                    pf.Activate();
                    continue;
                }
                pf = new ProgressForm(it, _mgr);
                string id = it.Id;
                pf.FormClosed += delegate { _progress.Remove(id); };
                _progress[id] = pf;
                pf.Show(this);
            }
        }

        public int OpenProgressWindows { get { return _progress.Count; } }

        // --- Actualizar dirección de descarga ---
        DownloadItem _refreshTarget;
        RefreshWaitForm _refreshForm;

        public void RefreshAddress(DownloadItem it)
        {
            if (_refreshTarget != null)
            {
                Msg.Show(this, "IDMClone ya está esperando una nueva dirección para otra descarga. Solo se puede actualizar una a la vez.", "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (it.Engine != null && it.Engine.IsRunning) _mgr.StopItem(it);
            _refreshTarget = it;
            _refreshForm = new RefreshWaitForm(it);
            _refreshForm.FormClosed += delegate { _refreshTarget = null; _refreshForm = null; };
            _refreshForm.Show(this);
            if (!string.IsNullOrEmpty(it.Referrer) && Util.LooksLikeUrl(it.Referrer))
                try { Process.Start(it.Referrer); } catch { }
        }

        public bool WaitingForNewAddress { get { return _refreshTarget != null; } }

        void OfferNewAddress(DownloadItem target, AddOptions o)
        {
            string q = "¿Esta es la nueva dirección para la descarga que expiró?\n\nArchivo: " + target.FileName + "\nDirección nueva: " + o.Url;
            bool yes = ConfirmHook != null ? ConfirmHook(q)
                : Msg.Show(this, q, "Actualizar dirección de descarga", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            if (!yes) { NewDownload(o, true); return; }
            if (_refreshForm != null) _refreshForm.Close();
            _mgr.ApplyNewAddress(target, o.Url, o.Cookies, o.Referrer, o.UserAgent);
            if (_tray != null) Balloon(3000, "IDMClone", "La nueva dirección de " + target.FileName + " se recibió correctamente.", ToolTipIcon.Info);
            ShowProgressFor(target);
            RefreshList();
        }

        void SetLimiter(bool on)
        {
            if (on && _cfg.SpeedLimitKBps <= 0) _cfg.SpeedLimitKBps = 500;
            _cfg.SpeedLimiterOn = on;
            _cfg.Save();
            UpdateStatus();
        }

        public void ShowOptions(string tab)
        {
            using (OptionsForm f = new OptionsForm(_cfg))
            {
                if (tab != null) f.SelectTab(tab);
                if (Visible) f.ShowDialog(this); else { f.StartPosition = FormStartPosition.CenterScreen; f.ShowDialog(); }
                if (f.DialogResult == DialogResult.OK) RefreshTree();
            }
        }

        void ShowScheduler()
        {
            int sel = QueueDef.MainId, qid;
            if (_filter != null && _filter.StartsWith("q:") && int.TryParse(_filter.Substring(2), out qid)) sel = qid;
            ShowScheduler(sel, -1);
        }

        // Planificador con una cola elegida; tab = pestaña (0 "Programar", 1 "Archivos en la cola", -1 la de siempre).
        void ShowScheduler(int queueId, int tab)
        {
            using (SchedulerForm f = new SchedulerForm(_mgr, queueId))
            {
                TabControl tc = tab >= 0 ? FindTabs(f) : null;
                if (tc != null && tab < tc.TabCount) tc.SelectedIndex = tab;
                f.ShowDialog(Visible ? this : null);
            }
            RefreshTree();
            RefreshList();
        }

        static TabControl FindTabs(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                TabControl tc = c as TabControl ?? FindTabs(c);
                if (tc != null) return tc;
            }
            return null;
        }

        // "Acerca de IDMClone": ventana pequeña como la de IDM.
        void About()
        {
            using (AboutForm f = new AboutForm())
            {
                if (Visible) f.ShowDialog(this); else { f.StartPosition = FormStartPosition.CenterScreen; f.ShowDialog(); }
            }
        }

        // --- Actualizaciones: Ayuda > Comprobar actualizaciones... y la comprobación semanal (ver Updater.cs) ---
        // Las pruebas contestan la ventana con UpdateFormHook (y no se lanza nada: Updater.LaunchHook).
        public static Func<UpdateForm, DialogResult> UpdateFormHook;
        UpdateForm _updateForm;
        bool _updChecking;

        public void CheckForUpdates() { ShowUpdate(null); }

        // Automática: en segundo plano y en silencio si falla (sin red, servidor caído, repositorio privado...).
        // Solo si hay una versión más nueva sale la ventana "Hay una nueva versión...".
        public void AutoCheckForUpdates()
        {
            if (_updChecking || _updateForm != null) return;
            _updChecking = true;
            AppConfig cfg = _cfg;
            System.Threading.Thread th = new System.Threading.Thread(delegate()
            {
                UpdateInfo info = null;
                try { info = Updater.Fetch(cfg); } catch { }
                UiPost(delegate
                {
                    _updChecking = false;
                    Updater.RecordCheck(_cfg, DateTime.UtcNow, info != null);
                    _cfg.Save();
                    if (Updater.IsNewer(info)) { _pendingUpdate = info; ShowPendingUpdate(); }
                });
            });
            th.IsBackground = true; th.Name = "Actualizaciones";
            th.Start();
        }

        public bool UpdateCheckRunning { get { return _updChecking; } }

        // La versión nueva que encontró la comprobación automática, a la espera de poder enseñarla: si el usuario tiene
        // abierta otra ventana modal (Opciones, un mensaje...), no se le abre otra encima; se reintenta cada 5 s.
        UpdateInfo _pendingUpdate;

        void ShowPendingUpdate()
        {
            if (_pendingUpdate == null || IsDisposed || _updateForm != null) return;
            if (!Enabled) return;
            foreach (Form f in Application.OpenForms) if (f != this && f.Visible && f.Modal) return;
            UpdateInfo info = _pendingUpdate;
            _pendingUpdate = null;
            ShowUpdate(info);
        }

        void ShowUpdate(UpdateInfo info)
        {
            if (_updateForm != null && !_updateForm.IsDisposed) { _updateForm.Activate(); return; }
            string installer = null;
            using (UpdateForm f = new UpdateForm(_cfg, info))
            {
                _updateForm = f;
                DialogResult r;
                try
                {
                    if (UpdateFormHook != null) r = UpdateFormHook(f);
                    else if (Visible && WindowState != FormWindowState.Minimized) r = f.ShowDialog(this);
                    else { f.StartPosition = FormStartPosition.CenterScreen; r = f.ShowDialog(); }
                }
                finally { _updateForm = null; }
                if (r == DialogResult.OK) installer = f.InstallerPath;
            }
            if (installer == null) return;
            // El instalador cierra IDMClone (orden /exit) y espera a que salga; aquí se sale ya para no hacerle esperar.
            if (!Updater.Launch(installer, Application.ExecutablePath))
            {
                Msg.Show(Visible ? this : null, "No se pudo ejecutar el instalador de la actualización.", "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (ExitHook != null) ExitHook(); else ExitApp();
        }

        // Ayuda: páginas HTML locales que acompañan al programa (nunca una página web).
        public void ShowHelp(string topic)
        {
            OptionsHelp.Open(Visible ? this : null, topic);
        }

        public void CheckSchedule() { CheckSchedule(DateTime.Now); }

        public void CheckSchedule(DateTime now)
        {
            bool dirty = false;
            foreach (QueueDef q in _mgr.Queues.Queues.ToArray())
            {
                string act = q.Schedule.CheckNow(now);
                if (act != null) dirty = true; // recordar el disparo aunque la app se cierre
                if (act == "start") _mgr.StartQueue(q.Id);
                else if (act == "stop") _mgr.StopQueue(q.Id);
                // "Repetir la descarga cada...": vuelve a empezar la cola cuando pasa el intervalo.
                else if (!_mgr.IsQueueRunning(q.Id) && q.RepeatDue(now))
                {
                    q.LastFinished = null;
                    dirty = true;
                    _mgr.StartQueue(q.Id);
                }
            }
            if (dirty) _mgr.SaveQueues();
        }

        void PlaySound(string ev)
        {
            SoundSetting s = _cfg.SoundFor(ev);
            if (s == null || !s.Enabled) return;
            if (SoundHook != null) { SoundHook(ev); return; }
            try { if (File.Exists(s.File)) new SoundPlayer(s.File).Play(); } catch { }
        }

        // --- IPC: comandos recibidos de otra instancia (CLI o host nativo) ---
        // Se ejecuta en el hilo de la ventana y devuelve la respuesta JSON para quien lo pidió.
        public string HandleCommand(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return Ipc.ErrorJson("Orden vacía.");
            if (IsDisposed || !IsHandleCreated) return Ipc.ErrorJson("IDMClone se está cerrando.");
            cmd = cmd.Trim();
            try
            {
                return (string)Invoke((Func<string>)delegate
                {
                    try
                    {
                        if (cmd.StartsWith("{")) return HandleJsonCommand(cmd);
                        // El instalador cierra IDMClone así antes de actualizarlo o desinstalarlo.
                        if (cmd == "/exit") { UiPost(delegate { ExitApp(); }); return Ipc.OkJson(); }
                        return ApplyCli(CliArgs.Parse(cmd));
                    }
                    catch (Exception ex) { return Ipc.ErrorJson(ex.Message); }
                });
            }
            catch (Exception ex) { return Ipc.ErrorJson(ex.Message); }
        }

        // Aplica una orden de línea de comandos (al arrancar o llegada de otra instancia), como IDMan.exe:
        // primero añade la URL, luego /s inicia la cola; /n no muestra nada; /q sale al terminar.
        public string ApplyCli(CliArgs a)
        {
            string reply = Ipc.OkJson();
            // Menú contextual del Explorador: "Descargar con IDMClone" en un acceso directo de Internet (.url).
            if (!string.IsNullOrEmpty(a.UrlFile) && string.IsNullOrEmpty(a.Url))
            {
                string url = null, error = null;
                // Por si el Explorador pasa ya la dirección en vez de la ruta del acceso directo.
                if (Util.LooksLikeUrl(a.UrlFile)) url = a.UrlFile.Trim();
                else try { url = ShellMenu.ReadShortcutUrl(a.UrlFile); }
                catch (Exception ex) { error = ex.Message; }
                if (url != null) a.Url = url;
                else
                {
                    string msg = error != null ? "No se pudo leer el archivo: " + error : "El acceso directo no lleva a una dirección web (http, https o ftp).";
                    UiPost(delegate { Msg.Show(Visible ? this : null, msg, "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning); });
                    reply = Ipc.ErrorJson(Lang.T(msg));
                }
            }
            // "Descargar los enlaces con IDMClone" en un .txt, .htm o .html: la lista de enlaces, como Importar.
            if (!string.IsNullOrEmpty(a.LinksFile))
            {
                string file = a.LinksFile;
                UiPost(delegate { ShowLinksFromFile(file, "Descargar los enlaces con IDMClone"); });
            }
            if (!string.IsNullOrEmpty(a.Url))
            {
                AddOptions o = new AddOptions();
                o.Url = a.Url; o.Folder = a.Path; o.FileName = a.FileName; o.StartNow = !a.AddOnly;
                bool interactive = !a.Silent && !a.AddOnly && !a.OnBoot;
                if (interactive && MayAsk(a.Url))
                {
                    // Con diálogos de por medio no se hace esperar a quien mandó la orden.
                    bool quit = a.QuitWhenDone;
                    UiPost(delegate { DownloadItem x = NewDownload(o, true); if (x != null && quit) _quitWhenDone.Add(x.Id); });
                }
                else
                {
                    DownloadItem it = NewDownload(o, interactive);
                    if (it != null)
                    {
                        if (a.Silent) _silent.Add(it.Id);
                        if (a.QuitWhenDone) _quitWhenDone.Add(it.Id);
                        reply = "{\"ok\":true,\"id\":" + Ipc.JsonQuote(it.Id) + "}";
                    }
                }
                RefreshList();
            }
            if (a.StartQueue) _mgr.StartQueue();
            // La lista de enlaces (o el aviso) sale sola: no hace falta sacar la ventana principal de la bandeja.
            bool quiet = a.Silent || a.OnBoot || a.Tray || ((a.StartQueue || a.HasFiles) && string.IsNullOrEmpty(a.Url));
            if (!quiet) ShowFromTray();
            return reply;
        }

        // /q: descargas tras las que hay que salir de IDMClone (cuando ya no quede nada descargando).
        readonly HashSet<string> _quitWhenDone = new HashSet<string>();
        bool _quitPending;

        void CheckQuitWhenDone()
        {
            if (!_quitPending) return;
            foreach (DownloadItem it in _mgr.Snapshot())
                if (it.Engine != null && it.Engine.IsRunning) return;
            ExitApp();
        }

        // Petición de la extensión (vía host nativo): {"type":"download","url":...,"cookies":...,"referrer":...,"filename":...,"userAgent":...}
        string HandleJsonCommand(string json)
        {
            Dictionary<string, object> d;
            try { d = AppConfig.NewSerializer().Deserialize<Dictionary<string, object>>(json); }
            catch { return Ipc.ErrorJson("Mensaje no válido."); }
            if (d == null) return Ipc.ErrorJson("Mensaje vacío.");
            string type = Get(d, "type");
            if (type == "ping") return "{\"ok\":true,\"gui\":true}";
            if (type == "startqueue") { _mgr.StartQueue(); return Ipc.OkJson(); }
            if (type == "stopall") { _mgr.StopAll(); return Ipc.OkJson(); } // "Detener todas" (también termina las grabaciones en directo)
            if (type == "showoptions") { UiPost(delegate { ShowFromTray(); ShowOptions(Get(d, "tab")); }); return Ipc.OkJson(); }
            if (type == "links")
            {
                // "Descargar todos los enlaces" / "los enlaces seleccionados" desde el navegador.
                List<LinkEntry> links = LinksFromJson(d);
                if (links.Count == 0) return Ipc.ErrorJson("La página no tiene enlaces.");
                string title = Get(d, "selection").Equals("true", StringComparison.OrdinalIgnoreCase) ? "Descargar los enlaces seleccionados con IDMClone" : "Descargar todos los enlaces con IDMClone";
                UiPost(delegate { ShowLinks(links, title); });
                return "{\"ok\":true,\"count\":" + links.Count + "}";
            }
            string url = Get(d, "url");
            if (!Util.LooksLikeUrl(url)) return Ipc.ErrorJson("URL no válida: " + url);
            string name = Get(d, "filename");
            AddOptions o = new AddOptions();
            o.Url = url; o.FileName = name; o.StartNow = true;
            o.Cookies = Get(d, "cookies"); o.Referrer = Get(d, "referrer"); o.UserAgent = Get(d, "userAgent");
            o.PostData = Get(d, "postData");
            if (o.PostData.Length == 0) o.PostData = null;
            o.AutoCaptured = Get(d, "auto").Equals("true", StringComparison.OrdinalIgnoreCase);
            // #17 Proxy HTTP que usó el navegador (extensión 1.4), por si falla la descarga directa.
            string bp = Get(d, "browserProxy");
            if (NativeHost.ValidBrowserProxy(bp)) o.BrowserProxy = bp;
            // Panel "Descargar este video": lista HLS/DASH y la calidad elegida (o los subtítulos: "subs").
            string mk = Get(d, "mediaKind");
            if (mk == "hls" || mk == "dash" || mk == "pair" || mk == DownloadEngine.SubtitleKind)
            {
                o.MediaKind = mk;
                o.MediaVideo = Get(d, "mediaVideo").Length > 0 ? Get(d, "mediaVideo") : null;
                o.MediaAudio = Get(d, "mediaAudio").Length > 0 ? Get(d, "mediaAudio") : null;
            }
            if (o.Cookies.Length == 0) o.Cookies = null;
            if (o.Referrer.Length == 0) o.Referrer = null;
            if (o.UserAgent.Length == 0) o.UserAgent = null;
            // Navegación privada: la descarga se hace, pero no queda en la lista guardada en disco.
            o.Transient = Get(d, "incognito").Equals("true", StringComparison.OrdinalIgnoreCase);

            // "Actualizar dirección de descarga": se esperaba el enlace nuevo de una descarga caducada.
            if (_refreshTarget != null)
            {
                DownloadItem target = _refreshTarget;
                UiPost(delegate { OfferNewAddress(target, o); });
                return Ipc.OkJson();
            }
            if (MayAsk(url))
            {
                UiPost(delegate { NewDownload(o, true); });
                return Ipc.OkJson();
            }
            DownloadItem it = NewDownload(o, true);
            RefreshList();
            if (it == null) return Ipc.OkJson();
            if (_tray != null && !_lastAddQueuedOnly) Balloon(3000, "IDMClone", "Descarga recibida del navegador: " + (string.IsNullOrEmpty(name) ? url : name), ToolTipIcon.Info);
            return "{\"ok\":true,\"id\":" + Ipc.JsonQuote(it.Id) + "}";
        }

        static string Get(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : ""; }

        // {"links":[{"url":..,"text":..}], "referrer":.., "userAgent":.., "cookies":{"servidor":"a=1; b=2"}}
        static List<LinkEntry> LinksFromJson(Dictionary<string, object> d)
        {
            List<LinkEntry> r = new List<LinkEntry>();
            object lo, co;
            Dictionary<string, object> cookies = d.TryGetValue("cookies", out co) ? co as Dictionary<string, object> : null;
            System.Collections.IEnumerable arr = d.TryGetValue("links", out lo) ? lo as System.Collections.IEnumerable : null;
            if (arr == null || lo is string) return r;
            string referrer = Get(d, "referrer"), ua = Get(d, "userAgent");
            foreach (object x in arr)
            {
                Dictionary<string, object> l = x as Dictionary<string, object>;
                if (l == null) continue;
                string url = Get(l, "url");
                if (!Util.LooksLikeUrl(url)) continue;
                LinkEntry e = new LinkEntry(url, Get(l, "text").Trim());
                e.Referrer = referrer.Length > 0 ? referrer : null;
                e.UserAgent = ua.Length > 0 ? ua : null;
                try
                {
                    object c;
                    if (cookies != null && cookies.TryGetValue(new Uri(url).Host, out c) && c != null && c.ToString().Length > 0) e.Cookies = c.ToString();
                }
                catch { }
                r.Add(e);
            }
            return r;
        }

        void OnItemFinished(DownloadItem it)
        {
            UiPost(delegate
            {
                ProgressForm pf;
                if (it.Status == DownloadStatus.Complete)
                {
                    PlaySound(SoundSetting.Complete);
                    if (!it.Pending) CheckForViruses(it); // pendiente: cuando se confirme y esté en su sitio (ShowFileInfo)
                    // Como IDM: al terminar se cierra la ventana de progreso y aparece "Descarga completada".
                    if (_progress.TryGetValue(it.Id, out pf)) pf.Close();
                    // Si aún está abierto "Información del archivo", el aviso espera a que el usuario decida.
                    bool show = (it.ShowCompleteDialog ?? _cfg.ShowCompleteDialog) && !it.LastRunByQueue && !_silent.Contains(it.Id) && !it.Pending;
                    if (show) ShowComplete(it);
                    else if (it.Pending) { if (!it.Removed) _finishedWhilePending.Add(it.Id); } // lo dice ShowFileInfo al decidir
                    else if (_tray != null) Balloon(4000, "Descarga completada", it.FileName, ToolTipIcon.Info);
                    if (_quitWhenDone.Remove(it.Id)) _quitPending = true;
                    _silent.Remove(it.Id);
                    if (it.ShutdownWhenDone)
                    {
                        it.ShutdownWhenDone = false;
                        string mode = FinishOptions.ModeOf(it);
                        ShutdownAfter("IDMClone ha terminado la descarga de " + it.FileName + ". Según la configuración de esta descarga, el equipo " + ShutdownVerb(mode) + ".", mode, FinishOptions.ForceOf(it));
                    }
                    else if (it.ExitWhenDone) { it.ExitWhenDone = false; ExitAfterDownload(); }
                }
                else if (it.Status == DownloadStatus.Error)
                {
                    PlaySound(SoundSetting.Failed);
                    if (_tray != null && !it.LastRunByQueue) Balloon(4000, "Descarga fallida", it.FileName + "\n" + it.Error, ToolTipIcon.Warning);
                }
                RefreshList();
                CheckQuitWhenDone();
            });
        }

        // Opciones > Descargas > comprobación de virus: el antivirus elegido analiza el archivo terminado.
        void CheckForViruses(DownloadItem it)
        {
            string file = it.FilePath, name = it.FileName, exe = _cfg.AntivirusPath;
            VirusCheck.Scan(_cfg, file, delegate(int code)
            {
                if (code == VirusCheck.DefenderThreatFound && VirusCheck.IsDefender(exe))
                    UiPost(delegate { if (_tray != null) Balloon(8000, "Comprobación de virus", Lang.T("Microsoft Defender ha encontrado una amenaza en " + name), ToolTipIcon.Warning); });
            });
        }

        void ExitAfterDownload()
        {
            bool others = false;
            foreach (DownloadItem x in _mgr.Snapshot()) if (x.Engine != null && x.Engine.IsRunning) { others = true; break; }
            if (others)
            {
                string q = "IDMClone ha terminado una descarga y, según su configuración, va a salir. Hay otras descargas en curso que se detendrán. ¿Salir?";
                bool yes = ConfirmHook != null ? ConfirmHook(q) : Msg.Show(this, q, "IDMClone", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;
                if (!yes) return;
            }
            if (ExitHook != null) ExitHook(); else ExitApp();
        }

        static string ShutdownVerb(string mode)
        {
            return mode == FinishOptions.Sleep ? "se suspenderá" : mode == FinishOptions.Hibernate ? "se hibernará" : "se apagará";
        }

        // Apagar, suspender o hibernar (Opciones de finalización de la descarga), con la cuenta atrás de IDM.
        void ShutdownAfter(string reason, string mode, bool force)
        {
            bool yes;
            if (ConfirmHook != null) yes = ConfirmHook(reason);
            else using (ShutdownForm f = new ShutdownForm(reason, 30, mode)) yes = f.ShowDialog() == DialogResult.OK;
            if (!yes) return;
            _mgr.StopAllAndWait(3000);
            _mgr.SaveList();
            FinishOptions.Run(mode, force); // apagar sin forzar = ShutdownAction (el de las pruebas)
        }

        // Para los tests: qué se abriría al terminar una cola ("Abrir el siguiente archivo").
        public static Action<string> RunFileHook;
        // Para los tests: el aviso de carpetas de categorías que no se pudieron pasar al idioma nuevo.
        public static Action<string> FolderNoticeHook;

        void OnQueueFinished(QueueDef q)
        {
            UiPost(delegate
            {
                PlaySound(SoundSetting.QueueStop);
                if (_tray != null) Balloon(4000, "IDMClone", QueueLabel(q) + " ha terminado.", ToolTipIcon.Info);
                UpdateStatus();
                RefreshList();
                // Si la cola no tenía nada que hacer, no se abre nada, ni se sale, ni se apaga el equipo.
                if (q.ProcessedThisRun == 0) return;
                if (q.RunFileWhenDone && !string.IsNullOrEmpty(q.RunFile))
                {
                    if (RunFileHook != null) RunFileHook(q.RunFile);
                    else try { Process.Start(q.RunFile); } catch (Exception ex) { if (_tray != null) Balloon(4000, "IDMClone", "No se pudo abrir " + q.RunFile + ": " + ex.Message, ToolTipIcon.Warning); }
                }
                if (q.ShutdownWhenDone)
                {
                    string mode = q.EffectiveShutdownMode;
                    ShutdownAfter("IDMClone ha terminado de procesar " + QueueLabel(q) + ". Según la configuración del planificador, el equipo " + ShutdownVerb(mode) + ".", mode, q.ForceTerminate);
                }
                else if (q.ExitWhenDone) ExitAfterDownload();
            });
        }

        public void ShowFromTray()
        {
            _allowShow = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = _lastState;
            Activate();
            // Traer al frente aunque otra app tenga el foco (al llegar una orden por IPC).
            TopMost = true; TopMost = false;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState != FormWindowState.Minimized) _lastState = WindowState;
        }

        // --- Cierre: minimizar a bandeja salvo salida explícita ---
        bool _reallyExit;

        void ExitApp() { _reallyExit = true; Close(); }

        static void CloseFileInfoDialogs()
        {
            List<Form> open = new List<Form>();
            foreach (Form f in Application.OpenForms) if (f is FileInfoForm) open.Add(f);
            foreach (Form f in open) { try { f.Close(); } catch { } }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                // Sin icono en la bandeja (Ver > Bandeja de iconos > No mostrar) no se podría volver: se minimiza.
                if (_tray != null && !_tray.Visible) WindowState = FormWindowState.Minimized; else Hide();
                return;
            }
            SaveLayout();
            // "Información del archivo" abierto al salir: como cerrarlo con la X (Cancelar), así no queda nada a medias en
            // la carpeta temporal.
            CloseFileInfoDialogs();
            _mgr.StopAllAndWait(3000);
            _mgr.SaveList();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            base.OnFormClosing(e);
        }
    }
}
