using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace IDMClone.Tests
{
    // Opciones que antes solo se guardaban (área 2): ver scratchpad\opciones-adorno.md.
    //   #4 "No iniciar la descarga, sólo añadir archivos a la cola", #14 modo de apagado y "Forzar el fin de procesos"
    //   del Programador, #3 pestañas de la ventana de progreso, #7 aviso de los límites de descarga y la ayuda local.
    public static partial class TestMain
    {
        static void RunOpciones2()
        {
            Run("opción 2: cola sin diálogo: lo añadido a mano queda en la cola principal, sin progreso", TO2_QueueOnly);
            Run("opción 2: cola sin diálogo: desmarcada (o con el diálogo de inicio) la descarga empieza como siempre", TO2_QueueOnlyOff);
            Run("opción 2: cola sin diálogo: portapapeles y navegador también quedan en la cola", TO2_QueueOnlyClipboardBrowser);
            Run("opción 2: programador: al terminar la cola hiberna forzando, según la cola", TO2_QueueShutdownMode);
            Run("opción 2: programador: modo de apagado y forzar se guardan; queues.json antiguo carga", TO2_QueueShutdownPersist);
            Run("opción 2: programador: el combo y forzar funcionan en la ventana", TO2_SchedulerForceUi);
            Run("opción 2: progreso: pestañas según Opciones y botón Ocultar pestaña", TO2_ProgressTabs);
            Run("opción 2: límites de descarga: aviso una vez por periodo y estado de espera", TO2_QuotaWarning);
            Run("opción 2: ayuda: cada tema tiene su página local (y en inglés)", TO2_HelpPages);
            Run("opción 2: ayuda: botones y menú Ayuda abren el tema de su ventana", TO2_HelpOpen);
        }

        // ------------------------------------------------------------------ #4

        static void TO2_QueueOnly()
        {
            MainForm f = NewMainForm();
            Action<string> savedNotice = MainForm.NoticeHook;
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                m.Config.ShowFileInfoDialog = false; m.Config.AddToQueueWithoutDialog = true; m.Config.ProgressDialogMode = "normal";
                List<string> notices = new List<string>();
                MainForm.NoticeHook = delegate(string t) { notices.Add(t); };
                string id = NewId("qonly");
                AddOptions o = new AddOptions(); o.Url = S.Url("range", "solo-cola.bin", 256 * 1024, id); o.Folder = Dl; o.StartNow = true;
                DownloadItem it = f.NewDownload(o, true);
                Check(it != null, "No se añadió");
                Check(it.Status == DownloadStatus.Queued && it.Engine == null, "Empezó a descargar: " + it.Status);
                Check(it.Queue == DownloadItem.MainQueue, "No está en la cola principal: " + it.Queue);
                PumpUntil(delegate { return false; }, 1);
                Check(S.Count(id + ":req") == 0, "Se pidió al servidor sin iniciar la cola");
                Check(CountOpenForms(typeof(ProgressForm)) == 0, "Se abrió la ventana de progreso");
                Check(notices.Count == 1 && notices[0].Contains("Añadida a la cola principal") && notices[0].Contains("solo-cola.bin"), "Aviso: " + string.Join(" | ", notices.ToArray()));
                // El mismo enlace otra vez con "continuar": sigue en la cola sin empezar, y la cola puede relanzarla.
                m.Config.DuplicateAction = "resume";
                it.UserStopped = true;
                AddOptions again = new AddOptions(); again.Url = o.Url; again.Folder = Dl; again.StartNow = true;
                Check(f.NewDownload(again, true) == it && it.Engine == null && !it.UserStopped && it.Queue == DownloadItem.MainQueue, "El duplicado no quedó en la cola");
                Check(notices.Count == 2, "Avisos: " + string.Join(" | ", notices.ToArray()));
                notices.Clear(); notices.Add("x");
                // La línea de comandos /a sigue igual (sin aviso: no es interactiva).
                CliArgs a = new CliArgs(); a.Url = S.Url("range", "cli-a.bin", 1024, NewId("clia")); a.AddOnly = true; a.Tray = true; // /tray: que no se muestre la ventana
                f.ApplyCli(a);
                Check(notices.Count == 1, "La línea de comandos /a no debe avisar");
                m.StartQueue();
                Check(PumpUntil(delegate { return it.Status == DownloadStatus.Complete; }, 30), "La cola no la descargó: " + it.Status + " " + it.Error);
                AssertComplete(it, 256 * 1024);
            }
            finally { MainForm.NoticeHook = savedNotice; CloseOpenForms(typeof(ProgressForm)); DisposeMainForm(f); }
        }

        static void TO2_QueueOnlyOff()
        {
            MainForm f = NewMainForm();
            Action<string> savedNotice = MainForm.NoticeHook;
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                List<string> notices = new List<string>();
                MainForm.NoticeHook = delegate(string t) { notices.Add(t); };
                m.Config.ShowFileInfoDialog = false; m.Config.AddToQueueWithoutDialog = false; m.Config.ProgressDialogMode = "normal";
                AddOptions o = new AddOptions(); o.Url = S.Url("range,slow", "empieza.bin", 2 * 1024 * 1024, NewId("qoff")); o.Folder = Dl; o.StartNow = true;
                DownloadItem it = f.NewDownload(o, true);
                Check(it.Engine != null, "Sin la opción la descarga debe empezar");
                Check(CountOpenForms(typeof(ProgressForm)) == 1, "Sin la opción debe abrirse la ventana de progreso");
                m.StopItem(it);
                CloseOpenForms(typeof(ProgressForm));

                // Con el diálogo de inicio la opción no cuenta: aparece "Información del archivo".
                m.Config.ShowFileInfoDialog = true; m.Config.AddToQueueWithoutDialog = true; m.Config.StartWhileInfoDialog = false;
                AddOptions o2 = new AddOptions(); o2.Url = S.Url("range", "con-dialogo.bin", 1024, NewId("qdlg")); o2.Folder = Dl; o2.StartNow = true;
                f.NewDownload(o2, true);
                Check(CountOpenForms(typeof(FileInfoForm)) == 1, "Con el diálogo de inicio debe mostrarse Información del archivo");
                Check(notices.Count == 0, "No debe avisar de la cola: " + string.Join(" | ", notices.ToArray()));
            }
            finally { MainForm.NoticeHook = savedNotice; CloseOpenForms(typeof(FileInfoForm)); CloseOpenForms(typeof(ProgressForm)); DisposeMainForm(f); }
        }

        static void TO2_QueueOnlyClipboardBrowser()
        {
            MainForm f = NewMainForm();
            Action<string> savedNotice = MainForm.NoticeHook;
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                m.Config.AddToQueueWithoutDialog = true; m.Config.MonitorClipboard = true; m.Config.ProgressDialogMode = "normal";
                List<string> notices = new List<string>();
                MainForm.NoticeHook = delegate(string t) { notices.Add(t); };
                string clipId = NewId("qclip");
                DownloadItem c = f.OnClipboardText(S.Url("range", "portapapeles.zip", 1024, clipId));
                Check(c != null && c.Status == DownloadStatus.Queued && c.Engine == null && c.Queue == DownloadItem.MainQueue, "El portapapeles no la dejó en la cola");

                string url = S.Url("range", "navegador.zip", 1024, NewId("qbrw"));
                MethodInfo h = typeof(MainForm).GetMethod("HandleJsonCommand", BindingFlags.NonPublic | BindingFlags.Instance);
                string reply = (string)h.Invoke(f, new object[] { "{\"type\":\"download\",\"url\":\"" + url + "\",\"filename\":\"navegador.zip\"}" });
                Check(reply.Contains("\"ok\":true"), "Respuesta: " + reply);
                DownloadItem b = null;
                foreach (DownloadItem x in m.Snapshot()) if (x.Url == url) b = x;
                Check(b != null && b.Status == DownloadStatus.Queued && b.Engine == null && b.Queue == DownloadItem.MainQueue, "El navegador no la dejó en la cola");
                Check(notices.Count == 2, "Avisos: " + string.Join(" | ", notices.ToArray()));
                Check(CountOpenForms(typeof(ProgressForm)) == 0, "Se abrió la ventana de progreso");
            }
            finally { MainForm.NoticeHook = savedNotice; CloseOpenForms(typeof(ProgressForm)); DisposeMainForm(f); }
        }

        // ------------------------------------------------------------------ #14

        static void TO2_QueueShutdownMode()
        {
            MainForm f = NewMainForm();
            Func<string, bool> savedConfirm = MainForm.ConfirmHook;
            Action<string, bool> savedHook = FinishOptions.Hook;
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                string question = null, mode = null; bool force = false, ran = false;
                MainForm.ConfirmHook = delegate(string ask) { question = ask; return true; };   // nunca se apaga de verdad:
                FinishOptions.Hook = delegate(string md, bool fo) { mode = md; force = fo; ran = true; }; // el hook lo recibe
                QueueDef q = m.Queues.Add("Nocturna hiberna");
                q.ShutdownWhenDone = true; q.ShutdownMode = FinishOptions.Hibernate; q.ForceTerminate = true;
                AddOptions o = new AddOptions(); o.Url = S.Url("range", "hiberna.bin", 32 * 1024, NewId("hib")); o.Folder = Dl; o.Queue = q.Id;
                m.Add(o);
                m.StartQueue(q.Id);
                Check(PumpUntil(delegate { return ran; }, 20), "No se pidió hibernar al terminar la cola");
                Check(mode == FinishOptions.Hibernate && force, "Se pidió " + mode + (force ? " forzado" : " sin forzar"));
                Check(question != null && question.Contains("se hibernará"), "Pregunta: " + question);

                // Una cola sin modo guardado (queues.json antiguo) apaga, sin forzar.
                ran = false;
                QueueDef old = m.Queues.Add("Antigua");
                old.ShutdownWhenDone = true;
                AddOptions o2 = new AddOptions(); o2.Url = S.Url("range", "apaga.bin", 16 * 1024, NewId("off")); o2.Folder = Dl; o2.Queue = old.Id;
                m.Add(o2);
                m.StartQueue(old.Id);
                Check(PumpUntil(delegate { return ran; }, 20), "No se pidió apagar al terminar la cola");
                Check(mode == FinishOptions.Shutdown && !force, "Se pidió " + mode + (force ? " forzado" : " sin forzar"));
                Check(question.Contains("se apagará"), "Pregunta: " + question);
            }
            finally { MainForm.ConfirmHook = savedConfirm; FinishOptions.Hook = savedHook; DisposeMainForm(f); }
        }

        static void TO2_QueueShutdownPersist()
        {
            UseFreshHome("o2-queues");
            QueueSet s = QueueSet.Load();
            QueueDef q = s.Add("Suspende");
            q.ShutdownWhenDone = true; q.ShutdownMode = FinishOptions.Sleep; q.ForceTerminate = true;
            s.Save();
            QueueDef r = QueueSet.Load().Find(q.Id);
            Check(r != null && r.ShutdownMode == FinishOptions.Sleep && r.ForceTerminate && r.EffectiveShutdownMode == FinishOptions.Sleep, "No se guardó el modo o forzar");
            string json = File.ReadAllText(QueueSet.FilePath);
            Check(!json.Contains("EffectiveShutdownMode"), "Se guarda una propiedad calculada");

            // queues.json de antes de estas opciones (sin ShutdownMode ni ForceTerminate).
            File.WriteAllText(QueueSet.FilePath, "{\"Queues\":[{\"Id\":0,\"Name\":\"Cola principal\",\"Schedule\":{},\"ShutdownWhenDone\":true}," +
                "{\"Id\":1,\"Name\":\"Cola de sincronización\",\"Schedule\":{}},{\"Id\":5,\"Name\":\"Vieja\",\"Schedule\":{},\"ShutdownWhenDone\":true}]}");
            QueueSet old = QueueSet.Load();
            QueueDef v = old.Find(5);
            Check(v != null && v.ShutdownWhenDone && v.ShutdownMode == null && !v.ForceTerminate && v.EffectiveShutdownMode == FinishOptions.Shutdown, "La cola antigua no cargó bien");
            v.ShutdownMode = "algo-raro";
            Check(v.EffectiveShutdownMode == FinishOptions.Shutdown, "Un modo desconocido debe apagar");
        }

        static void TO2_SchedulerForceUi()
        {
            DownloadManager m = NewMgr(); m.Persist = false;
            QueueDef q = m.Queues.Add("Con apagado");
            using (SchedulerForm f = new SchedulerForm(m, q.Id))
            {
                f.ShowInTaskbar = false; f.Show(); Application.DoEvents(); // la cola se carga al crear la lista
                CheckBox shut = Field<CheckBox>(f, "_shutdownDone"), force = Field<CheckBox>(f, "_force");
                ComboBox modeBox = Field<ComboBox>(f, "_shutdownMode");
                Check(modeBox.Items.Count == FinishOptions.Modes.Length, "El combo tiene " + modeBox.Items.Count + " modos");
                Check(!shut.Checked && !force.Enabled && !modeBox.Enabled, "Sin apagar, forzar y el modo deben estar desactivados (" + shut.Checked + force.Enabled + modeBox.Enabled + ")");
                shut.Checked = true;
                Check(force.Enabled && modeBox.Enabled, "Con apagar, forzar y el modo deben activarse");
                modeBox.SelectedIndex = Array.IndexOf(FinishOptions.Modes, FinishOptions.Hibernate);
                force.Checked = true;
                f.Apply();
                CheckNoOverlap(f, "Programador");
                f.Close();
            }
            Check(q.ShutdownWhenDone && q.ShutdownMode == FinishOptions.Hibernate && q.ForceTerminate, "Aplicar no guardó: " + q.ShutdownMode + " " + q.ForceTerminate);
            using (SchedulerForm f = new SchedulerForm(m, q.Id))
            {
                f.ShowInTaskbar = false; f.Show(); Application.DoEvents();
                Check(Field<ComboBox>(f, "_shutdownMode").SelectedIndex == 2 && Field<CheckBox>(f, "_force").Checked, "Al volver a abrir no se ve lo guardado");
                f.Close();
            }
        }

        // ------------------------------------------------------------------ #3

        static void TO2_ProgressTabs()
        {
            DownloadManager m = NewMgr();
            DownloadItem it = m.Add(S.Url("range", "pestanas.bin", 1024, NewId("tabs")), Dl, null, false);
            AppConfig c = m.Config;
            string limit = Lang.T("Límite de velocidad"), finish = Lang.T("Opciones de finalización");

            c.ProgressShowLimitTab = false;
            using (ProgressForm f = new ProgressForm(it, m))
            {
                TabControl t = Field<TabControl>(f, "_tabs");
                Check(t.TabCount == 2 && !HasTab(t, limit) && HasTab(t, finish), "Sin la pestaña de límite: " + t.TabCount);
            }
            c.ProgressShowFinishTab = false;
            using (ProgressForm f = new ProgressForm(it, m))
            {
                Check(f.VisibleTabCount == 1, "Sin las dos pestañas quedan " + f.VisibleTabCount);
                typeof(ProgressForm).GetMethod("Refresh2", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(f, null); // sigue refrescando sin ellas
            }
            c.ProgressShowLimitTab = c.ProgressShowFinishTab = true;
            c.ProgressShowHideTabsButton = false;
            using (ProgressForm f = new ProgressForm(it, m))
            {
                f.ShowInTaskbar = false; f.Show();
                TabControl t = Field<TabControl>(f, "_tabs");
                foreach (string b in new string[] { "_hideLimit", "_hideFinish" })
                {
                    t.SelectedTab = (TabPage)Field<Button>(f, b).Parent; Application.DoEvents();
                    Check(!Field<Button>(f, b).Visible, b + " visible con la opción desmarcada");
                }
                f.Close();
            }
            c.ProgressShowHideTabsButton = true;
            using (ProgressForm f = new ProgressForm(it, m))
            {
                f.ShowInTaskbar = false; f.Show();
                TabControl t = Field<TabControl>(f, "_tabs");
                Button hide = Field<Button>(f, "_hideLimit");
                t.SelectedTab = (TabPage)hide.Parent; Application.DoEvents();
                Check(hide.Visible, "El botón Ocultar pestaña no se ve");
                hide.PerformClick();
                Check(t.TabCount == 2 && !HasTab(t, limit) && t.SelectedIndex == 0, "Ocultar pestaña no la quitó: " + t.TabCount);
                // Como en IDM (comprobado en IDM 6.43): es permanente, se guarda en las opciones.
                Check(!c.ProgressShowLimitTab, "Ocultar pestaña debe ser permanente (como IDM)");
                Button hideF = Field<Button>(f, "_hideFinish");
                t.SelectedTab = (TabPage)hideF.Parent; Application.DoEvents();
                hideF.PerformClick();
                Check(t.TabCount == 1, "Quedan " + t.TabCount + " pestañas");
                CheckNoOverlap(f, "Progreso con pestañas ocultas");
                f.Close();
            }
            Check(!c.ProgressShowFinishTab, "Ocultar la pestaña de finalización también es permanente");
            using (ProgressForm f = new ProgressForm(it, m)) Check(f.VisibleTabCount == 1, "Una ventana nueva ya no debe tener las pestañas ocultadas");
            c.ProgressShowLimitTab = c.ProgressShowFinishTab = true;
            using (ProgressForm f = new ProgressForm(it, m)) Check(f.VisibleTabCount == 3, "Al reactivarlas en Opciones vuelven las 3 pestañas");
        }

        static bool HasTab(TabControl t, string text)
        {
            foreach (TabPage p in t.TabPages) if (p.Text == text) return true;
            return false;
        }

        // ------------------------------------------------------------------ #7

        static Delegate QuotaHandlers()
        {
            FieldInfo fi = typeof(DownloadQuota).GetField("LimitReached", BindingFlags.NonPublic | BindingFlags.Static);
            return (Delegate)fi.GetValue(null);
        }

        static int QuotaHandlerCount() { Delegate d = QuotaHandlers(); return d == null ? 0 : d.GetInvocationList().Length; }

        static void FireQuota(DateTime untilUtc)
        {
            typeof(DownloadQuota).GetMethod("RaiseLimitReached", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { untilUtc });
        }

        static void TO2_QuotaWarning()
        {
            int before = QuotaHandlerCount();
            MainForm f = NewMainForm();
            Action<string> savedNotice = MainForm.NoticeHook;
            DownloadItem it = null;
            try
            {
                Check(QuotaHandlerCount() == before + 1, "La ventana principal no escucha el límite de descarga");
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                m.Config.QuotaEnabled = true; m.Config.QuotaWarning = true;
                List<string> notices = new List<string>();
                MainForm.NoticeHook = delegate(string t) { notices.Add(t); };
                DateTime until = DateTime.UtcNow.AddMinutes(30);
                string hhmm = until.ToLocalTime().ToString("HH:mm");
                FireQuota(until);
                Check(PumpUntil(delegate { return notices.Count > 0; }, 5), "No avisó al alcanzar el límite");
                Check(notices[0].Contains("Se alcanzó el límite de descarga") && notices[0].Contains(hhmm), "Aviso: " + notices[0]);
                FireQuota(until); // el mismo periodo: no se repite
                PumpUntil(delegate { return false; }, 1);
                Check(notices.Count == 1, "Avisó dos veces en el mismo periodo");
                m.Config.QuotaWarning = false;
                FireQuota(until.AddHours(1));
                PumpUntil(delegate { return false; }, 1);
                Check(notices.Count == 1, "Avisó con la advertencia desmarcada");

                // Mientras se espera, la descarga no dice "Recibiendo datos" a 0 B/s.
                it = m.Add(S.Url("range", "cuota.bin", 1024, NewId("quota")), Dl, null, false);
                it.Status = DownloadStatus.Downloading;
                typeof(DownloadQuota).GetProperty("BlockedUntilUtc").GetSetMethod(true).Invoke(null, new object[] { until });
                string st = ProgressForm.QuotaStatus(it);
                Check(st != null && st.Contains(hhmm), "Estado: " + st);
                typeof(MainForm).GetMethod("RefreshList", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(f, null);
                ListViewItem row = Field<Dictionary<string, ListViewItem>>(f, "_rows")[it.Id];
                bool shown = false;
                foreach (ListViewItem.ListViewSubItem si in row.SubItems) if (si.Text.StartsWith("Límite de descarga alcanzado")) shown = true;
                Check(shown, "La lista no muestra la espera por el límite");
                using (ProgressForm pf = new ProgressForm(it, m))
                    Check(Field<Label>(pf, "_status").Text.Contains(hhmm), "La ventana de progreso dice: " + Field<Label>(pf, "_status").Text);
                DownloadQuota.Reset();
                Check(ProgressForm.QuotaStatus(it) == null, "Sin límite no debe decir que espera");
                it.Status = DownloadStatus.Paused;
            }
            finally { DownloadQuota.Reset(); MainForm.NoticeHook = savedNotice; DisposeMainForm(f); }
            Check(QuotaHandlerCount() == before, "La ventana cerrada sigue suscrita al límite de descarga");
        }

        // ------------------------------------------------------------------ Ayuda

        static string ThisSourceFile([CallerFilePath] string path = "") { return path; }

        // help\ del proyecto: junto a las pruebas compiladas (tests\bin) o, si se compilan fuera, junto a este archivo.
        static string RepoHelpDir()
        {
            string d = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 4 && !string.IsNullOrEmpty(d); i++)
            {
                string h = Path.Combine(d, "help");
                if (File.Exists(Path.Combine(h, "index.html"))) return h;
                d = Path.GetDirectoryName(d.TrimEnd('\\'));
            }
            string src = ThisSourceFile();
            if (!string.IsNullOrEmpty(src))
            {
                string h = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(src)), "help");
                if (File.Exists(Path.Combine(h, "index.html"))) return h;
            }
            throw new SkipException("No se encuentra la carpeta help del proyecto");
        }

        static void TO2_HelpPages()
        {
            string dir = RepoHelpDir();
            string saved = OptionsHelp.DirOverride;
            OptionsHelp.DirOverride = dir;
            try
            {
                foreach (string t in OptionsHelp.Topics)
                {
                    string p = OptionsHelp.Resolve(t);
                    Check(p != null && Path.GetFileName(p) == t + ".html" && Path.GetDirectoryName(p) == dir, "Tema " + t + ": " + p);
                    Check(File.Exists(Path.Combine(Path.Combine(dir, "en"), t + ".html")), "Falta la página en inglés de " + t);
                }
                Check(Path.GetFileName(OptionsHelp.Resolve("no-existe")) == "index.html", "Un tema desconocido debe dar el índice");
                // Todas las páginas de Opciones tienen tema propio.
                using (OptionsForm of = new OptionsForm(NewMgr().Config))
                {
                    TabControl tabs = Field<TabControl>(of, "_tabs");
                    foreach (TabPage p in tabs.TabPages)
                        Check(OptionsHelp.TopicForOptionsPage(p.Name) != "index", "La página " + p.Name + " de Opciones no tiene ayuda");
                }
                // Solo archivos locales: ningún enlace a la web, y todos los enlaces llevan a páginas que existen.
                foreach (string file in Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories))
                {
                    string html = File.ReadAllText(file);
                    foreach (string bad in new string[] { "http:", "https:", "ftp:", "file:", "//", "<script", "url(", "@import" })
                        Check(html.IndexOf(bad, StringComparison.OrdinalIgnoreCase) < 0, "\"" + bad + "\" en " + file + ": la ayuda solo usa archivos locales");
                    foreach (System.Text.RegularExpressions.Match mt in System.Text.RegularExpressions.Regex.Matches(html, "(?:href|src)=\"([^\"]+)\""))
                    {
                        string target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file), mt.Groups[1].Value.Replace('/', '\\')));
                        Check(File.Exists(target), "Enlace roto en " + Path.GetFileName(file) + ": " + mt.Groups[1].Value);
                    }
                }
                // Con la interfaz en inglés, la página en inglés.
                Lang.LoadFile(EnglishFile());
                try
                {
                    string en = OptionsHelp.Resolve("programador");
                    Check(en != null && en.EndsWith(@"\en\programador.html", StringComparison.OrdinalIgnoreCase), "En inglés: " + en);
                    // Chino (sin ayuda propia): la inglesa.
                    Lang.LoadFile(LangFile("zh"));
                    string zh = OptionsHelp.Resolve("programador");
                    Check(zh != null && zh.EndsWith(@"\en\programador.html", StringComparison.OrdinalIgnoreCase), "En chino: " + zh);
                }
                finally { Lang.Reset(); }
            }
            finally { OptionsHelp.DirOverride = saved; }
        }

        static void TO2_HelpOpen()
        {
            string dir = RepoHelpDir();
            string saved = OptionsHelp.DirOverride;
            Action<string> savedHook = OptionsHelp.OpenHook;
            List<string> opened = new List<string>();
            OptionsHelp.DirOverride = dir;
            OptionsHelp.OpenHook = delegate(string p) { opened.Add(p == null ? "(nada)" : Path.GetFileName(p)); }; // no se lanza el navegador
            MainForm mf = null;
            try
            {
                DownloadManager m = NewMgr();
                using (OptionsForm f = new OptionsForm(m.Config))
                {
                    f.SelectTab("downloads"); OptionsHelp.Open(f);
                    f.SelectTab("connection"); OptionsHelp.Open(f);
                }
                using (SchedulerForm f = new SchedulerForm(m, QueueDef.MainId))
                {
                    Button help = null;
                    foreach (Control c in f.Controls) { Button b = c as Button; if (b != null && b.Text == Lang.T("Ayuda")) help = b; }
                    Check(help != null, "El Programador no tiene botón Ayuda");
                    MethodInfo onClick = typeof(Button).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance);
                    onClick.Invoke(help, new object[] { EventArgs.Empty });
                }
                DownloadItem it = m.Add(S.Url("range", "ayuda.bin", 1024, NewId("help")), Dl, null, false);
                using (ProgressForm f = new ProgressForm(it, m)) OptionsHelp.Open(f);
                Check(string.Join(",", opened.ToArray()) == "opciones-descargas.html,opciones-conexion.html,programador.html,progreso.html",
                      "Se abrió: " + string.Join(",", opened.ToArray()));

                opened.Clear();
                mf = NewMainForm();
                ToolStripMenuItem helpMenu = null;
                foreach (ToolStripItem i in Field<MenuStrip>(mf, "_menu").Items) if (i.Text == Lang.T("A&yuda")) helpMenu = (ToolStripMenuItem)i;
                Check(helpMenu != null, "No está el menú Ayuda");
                foreach (string t in new string[] { "Contenidos de ayuda", "Programador y colas", "Ayuda de Grabber" })
                    foreach (ToolStripItem i in helpMenu.DropDownItems) if (i.Text == Lang.T(t)) i.PerformClick();
                Check(string.Join(",", opened.ToArray()) == "index.html,programador.html,grabber.html", "El menú Ayuda abrió: " + string.Join(",", opened.ToArray()));
            }
            finally
            {
                OptionsHelp.DirOverride = saved; OptionsHelp.OpenHook = savedHook;
                if (mf != null) DisposeMainForm(mf);
            }
        }
    }
}
