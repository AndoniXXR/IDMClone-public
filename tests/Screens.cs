using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace IDMClone.Tests
{
    // Capturas de las ventanas para revisarlas a ojo (no forma parte de los tests automáticos).
    //   IDMCloneTests.exe --screens <carpeta>
    // Muestra cada ventana con datos de prueba reales (servidor local) y guarda un PNG por ventana.
    public static partial class TestMain
    {
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        // --lng-dump: textos de todas las ventanas capturadas (para el archivo de idioma).
        static List<string> _texts;

        // --tidy: en la imagen, direcciones y rutas normales en vez de las del servidor de pruebas (para las tiendas).
        static bool _tidy;

        static string TidyText(string t)
        {
            if (string.IsNullOrEmpty(t)) return t;
            t = System.Text.RegularExpressions.Regex.Replace(t, @"http://127\.0\.0\.1:\d+/f/[^/\s]+/([^?\r\n]+)(\?[^\s]*)?", m => "https://descargas.ejemplo.com/" + m.Groups[1].Value.Replace(" ", "%20"));
            t = t.Replace(Dl, @"C:\Users\Usuario\Downloads").Replace(Home, @"C:\Users\Usuario\AppData\Roaming\IDMClone");
            return t;
        }

        static void Tidy(Control c)
        {
            if (c is TextBox || c is Label) { string t = TidyText(c.Text); if (t != c.Text) c.Text = t; }
            foreach (Control k in c.Controls) Tidy(k);
        }

        static void Snap(Form f, string dir, string name)
        {
            if (_tidy) { Tidy(f); f.Text = TidyText(f.Text); }
            if (_texts != null) { Lang.Collect(f, _texts); if (f.MainMenuStrip != null) Lang.CollectItems(f.MainMenuStrip.Items, _texts); }
            for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(60); }
            using (Bitmap b = new Bitmap(f.Width, f.Height))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    IntPtr hdc = g.GetHdc();
                    PrintWindow(f.Handle, hdc, 2); // PW_RENDERFULLCONTENT
                    g.ReleaseHdc(hdc);
                }
                b.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png);
            }
        }

        static void SnapScreen(Rectangle r, string dir, string name)
        {
            for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(60); }
            using (Bitmap b = new Bitmap(r.Width, r.Height))
            {
                using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                b.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png);
            }
        }

        static void Pump(int ms)
        {
            DateTime end = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(30); }
        }

        static float _shot = 1f;
        // --real-dpi: la escala de verdad de Windows (Configuración > Pantalla), sin simular nada. Las pruebas normales
        // no declaran la escala (Windows les dice 96 ppp y dan lo mismo en cualquier pantalla); con esta opción el proceso
        // se declara consciente de la escala, como IDMClone.exe con su manifiesto, antes de abrir ninguna ventana.
        static bool _realDpi;
        static void Sim(Form f) { if (!_realDpi) SimulateDpi(f, _shot); }
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        // Nombre de archivo a partir del texto de una pestaña ("Proxy/Socks" -> "proxy-socks").
        static string SafeName(string text)
        {
            string s = text.ToLowerInvariant().Replace(' ', '-');
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '-');
            while (s.Contains("--")) s = s.Replace("--", "-");
            return s.Trim('-');
        }

        static int RunScreens(string[] args)
        {
            foreach (string a in args) if (a == "--real-dpi") _realDpi = true;
            if (_realDpi) SetProcessDPIAware(); // antes de cualquier ventana (y de leer Dpi.Factor)
            // Como la aplicación de verdad (Program.Main): estilos visuales y texto GDI; si no, las capturas salen
            // con controles clásicos y espacios comidos.
            Application.EnableVisualStyles();
            try { Application.SetCompatibleTextRenderingDefault(false); } catch (InvalidOperationException) { }
            string dir = null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--screens") dir = args[i + 1];
                if (args[i] == "--tidy") _tidy = true;
                if (args[i] == "--scale") _shot = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
            }
            if (_realDpi)
            {
                Dpi.Override = 0; _shot = Dpi.Factor;
                Console.WriteLine("Escala real de Windows: " + Math.Round(_shot * 100) + " %");
            }
            else Dpi.Override = _shot; // simula una pantalla al 125 % / 150 % sin tocar Windows
            string dumpTo = null;
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--lng-dump") dumpTo = args[i + 1];
            if (dumpTo != null) _texts = new List<string>();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--lang") Lang.LoadFile(args[i + 1]);
            Directory.CreateDirectory(dir);
            Home = Path.Combine(Path.GetTempPath(), "idmclone-screens-" + DateTime.Now.ToString("HHmmss"));
            Dl = Path.Combine(Home, "dl");
            Directory.CreateDirectory(Dl);
            AppConfig.HomeOverride = Home;
            S = new TestServer(); S.Start();

            AppConfig c = Cfg();
            c.ShowCompleteDialog = false; // se abre a mano para la captura
            c.ProgressDialogMode = "none";
            c.TempFolder = Path.Combine(Home, "DwnlData");
            c.Save();
            DownloadManager m = new DownloadManager(c);
            m.Schedule = new Schedule();
            List<DownloadItem> done = new List<DownloadItem>();
            foreach (string n in new string[] { "proyecto-final.zip", "manual de usuario.pdf", "vacaciones 2026.mp4", "instalador-v2.exe", "podcast-episodio-12.mp3" })
            {
                DownloadItem d = m.Add(S.Url("range", n, (n.Length * 397 + 50) * 1024, NewId("s")), null, null, true);
                done.Add(d);
            }
            foreach (DownloadItem d in done) WaitEnd(d, 30);
            done[1].Description = "Documentación del curso";
            DownloadItem running = m.Add(S.Url("range,veryslow", "imagen-del-sistema.iso", 40L * 1024 * 1024, NewId("run")), null, null, true);
            DownloadItem paused = m.Add(S.Url("range,slow", "musica-en-cola.flac", 6L * 1024 * 1024, NewId("pau")), null, null, true);
            WaitProgress(paused, 900 * 1024, 20);
            m.StopItem(paused); WaitEnd(paused, 10);
            DownloadItem failed = m.Add(S.Url("notfound", "enlace-caducado.rar", 1024, NewId("err")), null, null, true);
            WaitEnd(failed, 10);
            m.Add(S.Url("range", "pendiente-cola.7z", 3L * 1024 * 1024, NewId("q")), null, null, false);

            MainForm f = new MainForm(m, c, m.Schedule);
            Sim(f);
            f.Show();
            f.WindowState = FormWindowState.Normal;
            f.Size = new Size((int)(1180 * _shot), (int)(600 * _shot));
            f.Location = new Point(40, 40);
            Pump(3500);
            Snap(f, dir, "01-principal");

            // Menú contextual sobre la descarga en curso.
            ListView list = Field<ListView>(f, "_list");
            foreach (ListViewItem li in list.Items) li.Selected = li.Tag == running;
            ContextMenuStrip cm = list.ContextMenuStrip;
            Point at = list.PointToScreen(new Point(260, 60));
            cm.Show(at);
            Pump(400);
            SnapScreen(new Rectangle(at.X - 10, at.Y - 10, cm.Width + 20, cm.Height + 20), dir, "02-menu-contextual");
            cm.Close();

            // Menú Descargas.
            MenuStrip menu = Field<MenuStrip>(f, "_menu");
            ToolStripMenuItem dlMenu = (ToolStripMenuItem)menu.Items[2];
            dlMenu.ShowDropDown();
            Pump(300);
            Rectangle mr = dlMenu.DropDown.Bounds;
            SnapScreen(new Rectangle(mr.X - 4, mr.Y - 30, mr.Width + 8, mr.Height + 34), dir, "03-menu-descargas");
            dlMenu.HideDropDown();

            // Menú del panel Categorías (clic derecho sobre "Vídeo").
            TreeView tree = Field<TreeView>(f, "_tree");
            foreach (TreeNode n in tree.Nodes[0].Nodes) if (n.Text == "Vídeo" || n.Text == "Video") tree.SelectedNode = n;
            Rectangle nb = tree.SelectedNode.Bounds;
            Point tp = tree.PointToScreen(new Point(nb.Right - 10, nb.Bottom - 4));
            tree.ContextMenuStrip.Show(tp);
            Pump(400);
            SnapScreen(new Rectangle(tp.X - 10, tp.Y - 10, tree.ContextMenuStrip.Width + 20, tree.ContextMenuStrip.Height + 20), dir, "03b-menu-categorias");
            tree.ContextMenuStrip.Close();

            // Ventana de progreso con la tabla de conexiones y sus pestañas.
            f.ShowProgress(new List<DownloadItem> { running });
            Pump(2500);
            ProgressForm pf = null;
            foreach (Form o in Application.OpenForms) if (o is ProgressForm) pf = (ProgressForm)o;
            Sim(pf);
            Snap(pf, dir, "04-progreso");
            TabControl tabs = Field<TabControl>(pf, "_tabs");
            tabs.SelectedIndex = 1; Snap(pf, dir, "05-progreso-limite");
            tabs.SelectedIndex = 2; Snap(pf, dir, "06-progreso-finalizacion");
            pf.SetExpanded(false); tabs.SelectedIndex = 0; Snap(pf, dir, "07-progreso-sin-detalles");
            pf.Close();

            // Información del archivo (bajando mientras se muestra).
            c.ShowFileInfoDialog = true; c.StartWhileInfoDialog = true;
            AddOptions ao = new AddOptions(); ao.Url = S.Url("range,slow", "presentacion-ventas.pptx", 3L * 1024 * 1024, NewId("info")); ao.StartNow = true;
            f.NewDownload(ao, true);
            Pump(1500);
            foreach (Form o in Application.OpenForms) if (o is FileInfoForm) { Sim(o); Snap(o, dir, "08-informacion-archivo"); ((FileInfoForm)o).Cancel(); break; }

            using (AddUrlForm a = new AddUrlForm("https://ejemplo.com/descargas/archivo.zip")) { Sim(a); a.Show(); Snap(a, dir, "09-agregar-url"); }
            CompleteForm cf = new CompleteForm(done[1]); Sim(cf); cf.Show(); Snap(cf, dir, "10-descarga-completada"); cf.Close();
            using (PropertiesForm p = new PropertiesForm(c, m, done[1])) { Sim(p); p.Show(); Snap(p, dir, "11-propiedades"); }
            using (ColumnsForm cl = new ColumnsForm(c.Columns)) { Sim(cl); cl.Show(); Snap(cl, dir, "12-columnas"); }
            using (DuplicateForm d = new DuplicateForm(done[0])) { Sim(d); d.Show(); Snap(d, dir, "13-duplicado"); }
            using (AutoCancelForm ac = new AutoCancelForm("http://127.0.0.1:18765/f/range/documento.pdf?id=3")) { Sim(ac); ac.Show(); Snap(ac, dir, "13b-cancelar-captura-automatica"); }
            using (QueuePickForm q = new QueuePickForm()) { Sim(q); q.Show(); Snap(q, dir, "14-descargar-mas-tarde"); }
            using (RefreshWaitForm r = new RefreshWaitForm(failed)) { Sim(r); r.Show(); Snap(r, dir, "15-actualizar-direccion"); }
            using (SpeedLimiterForm s = new SpeedLimiterForm(c)) { Sim(s); s.Show(); Snap(s, dir, "16-limitador"); }
            // Solo para enseñar el Planificador: sin horario activo y sin acciones al terminar.
            QueueDef night = m.Queues.Add("Descargas nocturnas"); night.RepeatEnabled = true; night.RepeatHours = 6;
            using (SchedulerForm sf = new SchedulerForm(m, night.Id))
            {
                Sim(sf); sf.Show(); Snap(sf, dir, "17-planificador");
                TabControl st = Field<TabControl>(sf, "_tabs"); st.SelectedIndex = 1;
                ListView ql = Field<ListView>(sf, "_queues"); ql.Items[0].Selected = true;
                Snap(sf, dir, "18-planificador-archivos");
            }
            using (CategoryForm ca = new CategoryForm(c, null))
            {
                Sim(ca); ca.Set("Imágenes", "jpg jpeg png gif webp bmp", "", Path.Combine(c.DefaultFolder, "Images"), true);
                ca.Show(); Snap(ca, dir, "18b-agregar-categoria");
            }
            using (CategoryForm cg = new CategoryForm(c, c.FindCategory("General"))) { Sim(cg); cg.Show(); Snap(cg, dir, "18c-propiedades-general"); }
            List<LinkEntry> links = new List<LinkEntry>();
            foreach (string n in new string[] { "temporada1/capitulo01.mp4", "temporada1/capitulo02.mp4", "docs/guia.pdf", "index.html", "img/portada.jpg", "descargas/instalador.exe", "docs/guia.pdf" })
                links.Add(new LinkEntry("https://ejemplo.com/" + n, System.IO.Path.GetFileNameWithoutExtension(n)));
            using (LinkListForm lf = new LinkListForm(c, m, links, null)) { Sim(lf); lf.Show(); Snap(lf, dir, "19-lista-de-enlaces"); }
            using (BatchForm bf = new BatchForm(null)) { Sim(bf); bf.Set("https://ejemplo.com/fotos/img*.jpg", "1", "250", 3); bf.Show(); Snap(bf, dir, "19b-descarga-por-lotes"); }
            using (OptionsForm op = new OptionsForm(c))
            {
                Sim(op);
                op.Show();
                TabControl ot = Field<TabControl>(op, "_tabs");
                for (int i = 0; i < ot.TabPages.Count; i++) { ot.SelectedIndex = i; Snap(op, dir, "2" + i + "-opciones-" + SafeName(ot.TabPages[i].Text)); }
            }

            using (MediaPanelForm mp = new MediaPanelForm(c)) { Sim(mp); mp.Show(); Snap(mp, dir, "29-panel-de-video"); }

            // Grabber: los 4 pasos y la ventana principal tras explorar el sitio de prueba.
            GrabberForm.AskHook = delegate(string t) { return DialogResult.No; };
            GrabberForm gf = new GrabberForm(c, m, null, PutSite());
            Sim(gf); gf.Show(); gf.Location = new Point(60, 60);
            gf.SetStart(PutSite(), 1); gf.SetName("Fotos del sitio de prueba");
            Snap(gf, dir, "40-grabber-paso1");
            gf.GoNext(); Snap(gf, dir, "41-grabber-paso2");
            gf.GoNext(); Snap(gf, dir, "42-grabber-paso3");
            gf.GoNext(); Snap(gf, dir, "43-grabber-paso4");
            gf.GoNext();
            DateTime until = DateTime.Now.AddSeconds(20);
            while (DateTime.Now < until && (gf.Engine.Exploring || gf.ListCount == 0)) Pump(200);
            Pump(800);
            gf.Size = new Size((int)(860 * _shot), (int)(520 * _shot));
            Pump(300);
            Snap(gf, dir, "44-grabber-principal");
            gf.ClickTool("stats"); Pump(500);
            foreach (Form o in Application.OpenForms) if (o is GrabberStatsForm) { Sim(o); Snap(o, dir, "45-grabber-estadisticas"); }
            gf.Close();
            GrabberForm.AskHook = null;

            // Barra pequeña y sin categorías.
            c.ToolbarLarge = false; c.HideCategories = true;
            f.Close(); // se oculta en la bandeja
            m.StopAllAndWait(3000);
            NotifyIcon tray = Field<NotifyIcon>(f, "_tray");
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            f.Dispose();
            MainForm f2 = new MainForm(m, c, m.Schedule);
            Sim(f2); f2.Show(); f2.Size = new Size((int)(1000 * _shot), (int)(420 * _shot)); f2.Location = new Point(40, 40);
            Pump(1200);
            Snap(f2, dir, "30-principal-botones-pequenos");
            // Estilos de neón (grandes y pequeños), como los de IDM.
            f2.SetToolbarStyle("neon", true); Pump(400); Snap(f2, dir, "31-principal-neon-grandes");
            f2.SetToolbarStyle("neon", false); Pump(400); Snap(f2, dir, "32-principal-neon-pequenos");
            f2.SetToolbarStyle("3d", true);
            using (ToolbarCustomizeForm tc = new ToolbarCustomizeForm(ToolbarCatalog.Parse(null), "3d")) { Sim(tc); tc.Show(); Pump(300); Snap(tc, dir, "33-personalizar-barra"); }
            using (IDMClone.Setup.SetupForm sf = new IDMClone.Setup.SetupForm())
            {
                sf.Quiet = true; Sim(sf); sf.Show(); Pump(300); Snap(sf, dir, "50-instalador-bienvenida");
                System.Reflection.MethodInfo sp = typeof(IDMClone.Setup.SetupForm).GetMethod("ShowPage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                for (int pg = 1; pg <= 4; pg++) { sp.Invoke(sf, new object[] { pg }); Pump(250); Snap(sf, dir, "5" + pg + "-instalador-pagina" + pg); }
                sf.Close();
            }
            using (IDMClone.Setup.UninstallForm uf = new IDMClone.Setup.UninstallForm()) { Sim(uf); uf.Show(); Pump(300); Snap(uf, dir, "55-desinstalador"); }
            using (VirusCheckForm vc = new VirusCheckForm(null, null)) { Sim(vc); vc.Show(); vc.SetExe(VirusCheck.DefenderPath() ?? @"C:\Program Files\Windows Defender\MpCmdRun.exe"); Pump(300); Snap(vc, dir, "34-comprobacion-de-virus"); }
            NotifyIcon tray2 = Field<NotifyIcon>(f2, "_tray");
            if (tray2 != null) { tray2.Visible = false; tray2.Dispose(); }
            f2.Dispose();
            S.Stop();
            Console.WriteLine("Capturas en " + dir);
            if (dumpTo != null)
            {
                File.WriteAllLines(dumpTo, _texts.ToArray(), new System.Text.UTF8Encoding(false));
                Console.WriteLine(_texts.Count + " textos en " + dumpTo);
            }
            return 0;
        }
    }
}
