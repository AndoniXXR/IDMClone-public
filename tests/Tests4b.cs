using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace IDMClone.Tests
{
    // Tests del bloque 4b: varias colas, sincronización, acciones al terminar, lotes, lista de enlaces,
    // portapapeles, arrastrar, .ef2 de IDM y descargas tras un formulario (POST).
    public static partial class TestMain
    {
        static void RunPhase4b()
        {
            Run("4b colas: varias a la vez, cada una con sus archivos simultáneos", T4b_ParallelQueues);
            Run("4b colas: detener una no detiene la otra", T4b_StopOneQueue);
            Run("4b cola de sincronización: vuelve a bajar solo lo que cambió en el servidor", T4b_SyncQueue);
            Run("4b colas: repetir cada X tiempo (programador)", T4b_Repeat);
            Run("4b colas: al terminar abre un archivo y sale; vacía no hace nada", T4b_QueueFinishActions);
            Run("4b colas: se guardan y se migra el programador antiguo", T4b_QueuesPersist);
            Run("4b Planificador: orden en la cola y eliminar una cola", T4b_QueueOrderAndDelete);
            Run("4b descarga por lotes: * con números, ceros y letras", T4b_Batch);
            Run("4b lista de enlaces: filtros, marcados por tipo, carpeta y cola", T4b_LinkList);
            Run("4b portapapeles: una URL de un tipo vigilado empieza la descarga", T4b_Clipboard);
            Run("4b arrastrar: un enlace o varios", T4b_DragDrop);
            Run("4b exportar e importar el formato de IDM (.ef2)", T4b_Ef2);
            Run("4b navegador: «Descargar todos los enlaces» abre la lista", T4b_BrowserLinks);
            Run("4b POST: descarga que solo da el sitio tras enviar el formulario", T4b_Post);
            Run("4b host: la extensión recibe las teclas especiales", T4b_HostKeys);
        }

        static DownloadManager QueueMgr()
        {
            DownloadManager m = NewMgr();
            m.QueueRetryDelaySec = 2;
            return m;
        }

        static void T4b_ParallelQueues()
        {
            DownloadManager m = QueueMgr();
            QueueDef mine = m.Queues.Add("Mi cola");
            mine.Schedule.MaxActive = 1;
            m.Schedule.MaxActive = 2;
            List<DownloadItem> a = new List<DownloadItem>(), b = new List<DownloadItem>();
            for (int i = 0; i < 3; i++) { AddOptions o = new AddOptions(); o.Url = S.Url("range,slow", "c" + i + ".bin", 300 * 1024, NewId("pq")); o.Folder = Dl; o.Queue = mine.Id; a.Add(m.Add(o)); }
            for (int i = 0; i < 2; i++) { AddOptions o = new AddOptions(); o.Url = S.Url("range,slow", "p" + i + ".bin", 300 * 1024, NewId("pp")); o.Folder = Dl; b.Add(m.Add(o)); }
            List<int> finished = new List<int>();
            m.QueueFinished += delegate(QueueDef q) { lock (finished) finished.Add(q.Id); };
            m.StartQueue(mine.Id);
            m.StartQueue(QueueDef.MainId);
            int maxMine = 0, maxMain = 0;
            Check(WaitUntil(delegate
            {
                int x = 0, y = 0;
                foreach (DownloadItem it in a) if (it.Engine != null && it.Engine.IsRunning) x++;
                foreach (DownloadItem it in b) if (it.Engine != null && it.Engine.IsRunning) y++;
                maxMine = Math.Max(maxMine, x); maxMain = Math.Max(maxMain, y);
                lock (finished) return finished.Count == 2;
            }, 60), "Las colas no terminaron: " + string.Join(",", finished.ConvertAll(delegate(int i) { return i.ToString(); }).ToArray()));
            foreach (DownloadItem it in a) AssertComplete(it, 300 * 1024);
            foreach (DownloadItem it in b) AssertComplete(it, 300 * 1024);
            Check(maxMine == 1, "«Mi cola» bajó " + maxMine + " a la vez (debía ser 1)");
            Check(maxMain == 2, "La cola principal bajó " + maxMain + " a la vez (debía ser 2)");
            Check(finished.Contains(mine.Id) && finished.Contains(QueueDef.MainId), "Avisos de fin: " + finished.Count);
            Check(a[0].Queue == DownloadItem.NoQueue, "Los completados salen de su cola (como IDM)");
        }

        static void T4b_StopOneQueue()
        {
            DownloadManager m = QueueMgr();
            QueueDef mine = m.Queues.Add("Otra");
            AddOptions o1 = new AddOptions(); o1.Url = S.Url("range,veryslow", "q1.bin", 2 * 1024 * 1024, NewId("s1")); o1.Folder = Dl; o1.Queue = mine.Id;
            AddOptions o2 = new AddOptions(); o2.Url = S.Url("range,veryslow", "q2.bin", 2 * 1024 * 1024, NewId("s2")); o2.Folder = Dl;
            DownloadItem x = m.Add(o1), y = m.Add(o2);
            m.StartQueue(mine.Id); m.StartQueue();
            Check(WaitUntil(delegate { return x.Downloaded > 0 && y.Downloaded > 0; }, 20), "No empezaron");
            m.StopQueue(mine.Id);
            Check(WaitUntil(delegate { return x.Engine == null || !x.Engine.IsRunning; }, 10), "La cola detenida sigue descargando");
            Check(y.Engine != null && y.Engine.IsRunning && m.QueueRunning, "Detener «Otra» paró también la principal");
            Check(!m.IsQueueRunning(mine.Id), "«Otra» sigue en marcha");
            m.StopAllAndWait(5000);
        }

        static void T4b_SyncQueue()
        {
            DownloadManager m = QueueMgr();
            string idA = NewId("syncA"), idB = NewId("syncB");
            long size = 200 * 1024;
            AddOptions oa = new AddOptions(); oa.Url = S.Url("range", "vigilado-a.bin", size, idA); oa.Folder = Dl; oa.Queue = QueueDef.SyncId; oa.StartNow = true;
            AddOptions ob = new AddOptions(); ob.Url = S.Url("range", "vigilado-b.bin", size, idB); ob.Folder = Dl; ob.Queue = QueueDef.SyncId; ob.StartNow = true;
            DownloadItem a = m.Add(oa), b = m.Add(ob);
            WaitEnd(a, 30); WaitEnd(b, 30);
            AssertComplete(a, size); AssertComplete(b, size);
            Check(a.Queue == QueueDef.SyncId, "En la cola de sincronización los completados se quedan");
            DateTime bTime = File.GetLastWriteTimeUtc(b.FilePath);
            int bReq = S.Count(idB + ":req");
            S.SetVersion(idA, 2);   // cambia en el servidor (otro contenido y otra fecha)
            bool done = false;
            m.QueueFinished += delegate(QueueDef q) { if (q.IsSync) done = true; };
            m.StartQueue(QueueDef.SyncId);
            Check(WaitUntil(delegate { return done; }, 30), "La cola de sincronización no terminó");
            Check(a.Status == DownloadStatus.Complete && TestServer.Sha256Of(size, 2).Equals(Sha(a.FilePath), StringComparison.OrdinalIgnoreCase), "El archivo cambiado no se actualizó");
            Check(a.FilePath == Path.Combine(Dl, "vigilado-a.bin"), "Se guardó con otro nombre: " + a.FilePath);
            Check(File.GetLastWriteTimeUtc(b.FilePath) == bTime, "Se volvió a bajar uno que no había cambiado");
            Check(S.Count(idB + ":req") == bReq + 1, "Para el que no cambió debía bastar una comprobación (" + (S.Count(idB + ":req") - bReq) + " peticiones)");
        }

        static string Sha(string path)
        {
            using (System.Security.Cryptography.SHA256 h = System.Security.Cryptography.SHA256.Create())
            using (FileStream fs = File.OpenRead(path)) return BitConverter.ToString(h.ComputeHash(fs)).Replace("-", "");
        }

        static void T4b_Repeat()
        {
            QueueDef q = new QueueDef(); q.RepeatEnabled = true; q.RepeatHours = 0; q.RepeatMinutes = 30;
            DateTime now = new DateTime(2026, 10, 15, 12, 0, 0);
            q.LastFinished = now.AddMinutes(-10).ToString("yyyy-MM-dd HH:mm:ss");
            Check(!q.RepeatDue(now), "No debía repetirse aún");
            q.LastFinished = now.AddMinutes(-31).ToString("yyyy-MM-dd HH:mm:ss");
            Check(q.RepeatDue(now), "Debía repetirse");
            q.RepeatEnabled = false;
            Check(!q.RepeatDue(now), "Sin marcar no se repite");

            MainForm f = NewMainForm();
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                QueueDef sync = m.Queues.Sync;
                sync.RepeatEnabled = true; sync.RepeatHours = 1; sync.RepeatMinutes = 0;
                sync.LastFinished = DateTime.Now.AddHours(-2).ToString("yyyy-MM-dd HH:mm:ss");
                bool started = false;
                m.QueueStarted += delegate(QueueDef x) { if (x.IsSync) started = true; };
                f.CheckSchedule(DateTime.Now);
                Check(started, "El programador no repitió la cola de sincronización");
            }
            finally { DisposeMainForm(f); }
        }

        static void T4b_QueueFinishActions()
        {
            MainForm f = NewMainForm();
            Func<string, bool> savedConfirm = MainForm.ConfirmHook;
            Action<string> savedRun = MainForm.RunFileHook;
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                string ran = null; bool exited = false;
                MainForm.RunFileHook = delegate(string p) { ran = p; };
                MainForm.ConfirmHook = delegate(string question) { return true; };
                f.ExitHook = delegate { exited = true; };

                QueueDef q = m.Queues.Add("Nocturna");
                q.RunFileWhenDone = true; q.RunFile = @"C:\scripts\al-terminar.bat"; q.ExitWhenDone = true;
                AddOptions o = new AddOptions(); o.Url = S.Url("range", "noche.bin", 64 * 1024, NewId("night")); o.Folder = Dl; o.Queue = q.Id;
                m.Add(o);
                m.StartQueue(q.Id);
                Check(PumpUntil(delegate { return exited; }, 20), "No salió al terminar la cola");
                Check(ran == q.RunFile, "No abrió el archivo indicado: " + ran);

                // Una cola que no tenía nada que descargar no hace nada al terminar (ni sale ni abre nada).
                exited = false; ran = null;
                QueueDef empty = m.Queues.Add("Vacía");
                empty.RunFileWhenDone = true; empty.RunFile = @"C:\scripts\otro.bat"; empty.ExitWhenDone = true;
                bool finished = false;
                m.QueueFinished += delegate(QueueDef x) { if (x == empty) finished = true; };
                m.StartQueue(empty.Id);
                Check(PumpUntil(delegate { return finished; }, 10), "La cola vacía no terminó");
                PumpUntil(delegate { return false; }, 1);
                Check(!exited && ran == null, "Una cola vacía no debe salir ni abrir nada al terminar");
                // "Apagar el equipo" no se prueba a propósito.
            }
            finally { MainForm.ConfirmHook = savedConfirm; MainForm.RunFileHook = savedRun; DisposeMainForm(f); }
        }

        static void T4b_QueuesPersist()
        {
            UseFreshHome("queues");
            Schedule old = new Schedule(); old.Enabled = true; old.StartHour = 7; old.MaxActive = 3; old.Save();
            QueueSet s = QueueSet.Load();
            Check(s.Main.Schedule.StartHour == 7 && s.Main.Schedule.MaxActive == 3, "No se migró el programador antiguo");
            Check(s.Sync != null && s.Sync.Name == "Cola de sincronización", "Falta la cola de sincronización");
            QueueDef q = s.Add("Fin de semana");
            q.Schedule.Days = new bool[] { true, false, false, false, false, false, true };
            q.ExitWhenDone = true;
            s.Save();
            QueueSet r = QueueSet.Load();
            QueueDef q2 = r.Find(q.Id);
            Check(q2 != null && q2.Name == "Fin de semana" && q2.ExitWhenDone && q2.Schedule.Days[6] && !q2.Schedule.Days[1], "No se guardó la cola nueva");
            Check(r.Queues.Count == 3, "Colas: " + r.Queues.Count);
            File.WriteAllText(QueueSet.FilePath, "{ esto no es json");
            QueueSet broken = QueueSet.Load();
            Check(broken.Main != null && broken.Sync != null, "Con queues.json dañado no hay colas");
        }

        static void T4b_QueueOrderAndDelete()
        {
            DownloadManager m = QueueMgr();
            QueueDef q = m.Queues.Add("Ordenada");
            List<DownloadItem> l = new List<DownloadItem>();
            for (int i = 0; i < 3; i++) { AddOptions o = new AddOptions(); o.Url = "http://127.0.0.1:1/f/range/o" + i + ".bin"; o.Folder = Dl; o.Queue = q.Id; l.Add(m.Add(o)); }
            m.MoveInQueue(l[2], -1);
            List<DownloadItem> now = m.ItemsInQueue(q.Id);
            Check(now[0] == l[0] && now[1] == l[2] && now[2] == l[1], "Subir no cambió el orden");
            m.MoveInQueue(l[0], -1); // ya es el primero: no cambia
            Check(m.ItemsInQueue(q.Id)[0] == l[0], "Subir el primero lo movió");
            Check(!m.DeleteQueue(m.Queues.Main), "Se pudo eliminar la cola principal");
            Check(m.DeleteQueue(q), "No se eliminó la cola");
            Check(m.Queues.Find(q.Id) == null && l[0].Queue == DownloadItem.NoQueue && m.Snapshot().Count == 3, "Al eliminar la cola sus archivos deben quedar en la lista sin cola");
            using (SchedulerForm f = new SchedulerForm(m, QueueDef.SyncId)) { CheckNoOverlap(f, "Planificador"); f.Apply(); }
        }

        static void T4b_Batch()
        {
            List<string> n = Util.ExpandBatch("http://s.com/foto*.jpg", "8", "11", 3);
            Check(n.Count == 4 && n[0] == "http://s.com/foto008.jpg" && n[3] == "http://s.com/foto011.jpg", "Números: " + string.Join(" ", n.ToArray()));
            List<string> l = Util.ExpandBatch("http://s.com/parte-*.rar", "a", "c", 1);
            Check(l.Count == 3 && l[2] == "http://s.com/parte-c.rar", "Letras: " + string.Join(" ", l.ToArray()));
            Check(Util.ExpandBatch("http://s.com/sin-comodin.zip", "1", "3", 1).Count == 0, "Sin * no debe generar nada");
            Check(Util.ExpandBatch("http://s.com/x*.zip", "5", "3", 1).Count == 3, "Rango al revés");
            using (BatchForm f = new BatchForm(null))
            {
                f.Set("http://s.com/cap*.mp4", "1", "12", 2);
                Check(f.Build() && f.Result.Count == 12 && f.Result[0].Url == "http://s.com/cap01.mp4", "El diálogo no generó los enlaces");
            }
        }

        static void T4b_LinkList()
        {
            DownloadManager m = QueueMgr();
            List<LinkEntry> links = new List<LinkEntry>();
            links.Add(new LinkEntry(S.Url("range", "paquete.zip", 50 * 1024, NewId("ll1")), "Paquete"));
            links.Add(new LinkEntry(S.Url("range", "manual.pdf", 50 * 1024, NewId("ll2")), "Manual"));
            links.Add(new LinkEntry("http://127.0.0.1:1/pagina.html", "Otra página"));
            links.Add(new LinkEntry("http://127.0.0.1:1/foto.png", "Foto"));
            links.Add(new LinkEntry(links[0].Url, "Paquete otra vez"));
            using (LinkListForm f = new LinkListForm(m.Config, m, links, null))
            {
                Check(f.VisibleCount == 3, "Visibles con html y repetidos ocultos: " + f.VisibleCount);
                Check(f.CheckedCount == 2, "Marcados al principio (zip y pdf, no la imagen): " + f.CheckedCount);
                Field<CheckBox>(f, "_hideHtml").Checked = false;
                Check(f.VisibleCount == 4 && f.CheckedCount == 2, "Al mostrar html: " + f.VisibleCount + "/" + f.CheckedCount);
                Field<CheckBox>(f, "_useText").Checked = true;
                string folder = Path.Combine(Dl, "enlaces");
                f.UseOneFolder(folder);
                f.SelectQueue(0); // cola principal, sin iniciar
                Check(f.AddChecked(), "No se añadieron");
                Check(f.Added.Count == 2, "Añadidos: " + f.Added.Count);
                foreach (DownloadItem it in f.Added)
                {
                    Check(Path.GetDirectoryName(it.FilePath) == folder, "Carpeta: " + it.FilePath);
                    Check(it.Queue == QueueDef.MainId && (it.Engine == null || !it.Engine.IsRunning), "Debía quedar en la cola principal sin empezar");
                }
                Check(f.Added[0].Description == "Paquete", "Descripción desde el texto del enlace: " + f.Added[0].Description);
            }
        }

        static void T4b_Clipboard()
        {
            MainForm f = NewMainForm();
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                m.Config.MonitorClipboard = true;
                string url = S.Url("range", "copiado.zip", 32 * 1024, NewId("clip"));
                DownloadItem it = f.OnClipboardText("  " + url + "\r\n");
                Check(it != null && it.Url == url, "No empezó la descarga de la URL copiada");
                Check(f.OnClipboardText(url) == null, "La misma URL copiada otra vez no debe repetirse");
                Check(f.OnClipboardText("http://127.0.0.1:1/pagina.html") == null, "Una página web no es una descarga");
                Check(f.OnClipboardText("texto normal con http://x.com/a.zip dentro") == null, "Texto con más cosas no es una URL copiada");
                m.Config.MonitorClipboard = false;
                Check(f.OnClipboardText(S.Url("range", "otra.zip", 1024, NewId("clip2"))) == null, "Desactivado no debe hacer nada");
                WaitEnd(it, 20);
                AssertComplete(it, 32 * 1024);
            }
            finally { DisposeMainForm(f); }
        }

        static void T4b_DragDrop()
        {
            DataObject one = new DataObject();
            one.SetData("UniformResourceLocatorW", new MemoryStream(Encoding.Unicode.GetBytes("https://x.com/arrastrado.zip\0")));
            one.SetData(DataFormats.UnicodeText, "https://x.com/arrastrado.zip");
            List<string> l1 = DropData.Links(one);
            Check(l1.Count == 1 && l1[0] == "https://x.com/arrastrado.zip", "Enlace arrastrado: " + string.Join(" ", l1.ToArray()));
            DataObject text = new DataObject(DataFormats.UnicodeText, "uno https://a.com/1.zip y dos https://b.com/2.pdf");
            Check(DropData.Links(text).Count == 2, "Texto con dos enlaces");

            MainForm f = NewMainForm();
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                f.LinksDropped(new List<string> { S.Url("range", "soltado.zip", 16 * 1024, NewId("drop")) });
                Check(m.Snapshot().Count == 1, "Soltar un enlace no lo añadió");
                f.LinksDropped(new List<string> { "https://a.com/1.zip", "https://b.com/2.pdf" });
                Check(PumpUntil(delegate { return CountOpenForms(typeof(LinkListForm)) == 1; }, 5), "Soltar varios enlaces no abrió la lista");
                CloseOpenForms(typeof(LinkListForm));
                f.SetDropTarget(true);
                Check(f.DropTarget != null && f.DropTarget.Visible && f.DropTarget.TopMost && f.DropTarget.AllowDrop, "No apareció el destino de arrastre");
                f.SetDropTarget(false);
                Check(!f.DropTarget.Visible, "No se ocultó el destino de arrastre");
            }
            finally { CloseOpenForms(typeof(LinkListForm)); if (f.DropTarget != null) f.DropTarget.Close(); DisposeMainForm(f); }
        }

        static void T4b_Ef2()
        {
            DownloadItem a = new DownloadItem(); a.Url = "https://x.com/a.zip"; a.Referrer = "https://x.com/"; a.Cookies = "s=1; t=2"; a.UserAgent2("Mozilla/5.0 Prueba");
            DownloadItem b = new DownloadItem(); b.Url = "https://y.org/b.pdf"; b.PostData = "token=secreto";
            string ef2 = Ef2.Export(new DownloadItem[] { a, b });
            Check(ef2.StartsWith("<\r\nhttps://x.com/a.zip\r\nreferer: https://x.com/\r\nUser-Agent: Mozilla/5.0 Prueba\r\ncookie: s=1; t=2\r\n>\r\n"), "Formato: " + ef2);
            List<LinkEntry> back = Ef2.Parse(ef2);
            Check(back.Count == 2 && back[0].Referrer == "https://x.com/" && back[0].Cookies == "s=1; t=2" && back[0].UserAgent == "Mozilla/5.0 Prueba" && back[1].PostData == "token=secreto", "Ida y vuelta");
            // Un archivo como los que exporta IDM (con líneas en blanco y finales \n).
            List<LinkEntry> idm = Ef2.Parse("<\nhttp://www.ejemplo.com/archivo.zip\nreferer: http://www.ejemplo.com/\n>\n\n<\nhttp://otro.com/b.rar\n>\n<\nno es una url\n>\n");
            Check(idm.Count == 2 && idm[1].Url == "http://otro.com/b.rar", "Archivo de IDM: " + idm.Count);
        }

        static void T4b_BrowserLinks()
        {
            MainForm f = NewMainForm();
            try
            {
                string json = "{\"type\":\"links\",\"referrer\":\"https://x.com/pagina\",\"userAgent\":\"UA/2\",\"selection\":false,\"cookies\":{\"x.com\":\"sesion=abc\"}," +
                              "\"links\":[{\"url\":\"https://x.com/a.zip\",\"text\":\"A\"},{\"url\":\"https://y.org/b.pdf\",\"text\":\"B\"},{\"url\":\"javascript:void(0)\",\"text\":\"no\"}]}";
                string r = f.HandleCommand(json);
                Check(r.Contains("\"ok\":true") && r.Contains("\"count\":2"), "Respuesta: " + r);
                LinkListForm list = null;
                Check(PumpUntil(delegate { foreach (Form x in Application.OpenForms) if (x is LinkListForm) list = (LinkListForm)x; return list != null; }, 5), "No apareció la lista de enlaces");
                List<LinkEntry> all = Field<List<LinkEntry>>(list, "_all");
                Check(all.Count == 2 && all[0].Cookies == "sesion=abc" && all[1].Cookies == null && all[0].Referrer == "https://x.com/pagina" && all[1].UserAgent == "UA/2", "Datos de los enlaces");
                Check(list.Text == "Descargar todos los enlaces con IDMClone", "Título: " + list.Text);
            }
            finally { CloseOpenForms(typeof(LinkListForm)); DisposeMainForm(f); }
        }

        static void T4b_Post()
        {
            DownloadManager m = NewMgr();
            long size = 1024 * 1024;
            string id = NewId("post");
            AddOptions o = new AddOptions(); o.Url = S.Url("range,slow,post", "informe.pdf", size, id); o.Folder = Dl; o.StartNow = true; o.PostData = "token=secreto&tipo=pdf";
            DownloadItem it = m.Add(o);
            WaitEnd(it, 60);
            AssertComplete(it, size);
            Check(S.Count(id + ":post") >= 2 && S.Count(id + ":nopost") == 0, "Conexiones con POST: " + S.Count(id + ":post") + ", sin formulario: " + S.Count(id + ":nopost"));
            UseFreshHome("post-save");
            DownloadManager m2 = NewMgr();
            DownloadItem p = m2.Add(o.Url, Dl, null, false); p.PostData = o.PostData;
            m2.SaveList();
            string list = File.ReadAllText(AppConfig.DownloadsPath);
            Check(!list.Contains("secreto"), "El formulario se guardó en claro");
            DownloadManager m3 = NewMgr(); m3.LoadList();
            Check(m3.Items[0].PostData == o.PostData, "No se recuperó el formulario al reiniciar");
            DownloadItem bad = Download(NewMgr(), "range,post", 10 * 1024, "nopost", 20);
            Check(bad.Status == DownloadStatus.Error && bad.Error.Contains("403"), "Sin formulario debía fallar: " + bad.Status + " " + bad.Error);
        }

        static void T4b_HostKeys()
        {
            UseFreshHome("hostkeys");
            AppConfig c = Cfg(); c.UseKeyToPrevent = true; c.PreventKeys = "alt"; c.UseKeyToForce = true; c.ForceKeys = "ctrl+alt"; c.Save();
            string r = NativeHost.HandleMessage("{\"type\":\"getconfig\",\"rid\":1}");
            Check(r.Contains("\"preventKeys\":\"alt\"") && r.Contains("\"forceKeys\":\"ctrl+alt\""), "Teclas: " + r);
            c.UseKeyToForce = false; c.Save();
            r = NativeHost.HandleMessage("{\"type\":\"getconfig\",\"rid\":2}");
            Check(r.Contains("\"forceKeys\":\"\""), "Desactivada debe ir vacía: " + r);
            AppConfig d = AppConfig.CreateDefault();
            Check(d.UseKeyToPrevent && d.PreventKeys == "del" && !d.UseKeyToForce && d.ForceKeys == "ins" && !d.MonitorClipboard, "Valores por defecto distintos de los de IDM");
        }
    }

    static class ItemTestExt
    {
        public static void UserAgent2(this DownloadItem it, string ua) { it.UserAgentOverride = ua; }
    }
}
