using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace IDMClone.Tests
{
    // Tests del bloque 4a: ventanas, menús y comportamiento de la interfaz como IDM.
    public static partial class TestMain
    {
        static void RunPhase4a()
        {
            Run("4a motor: tabla de conexiones (N., Descargado, Info)", T4a_ConnectionTable);
            Run("4a motor: límite de velocidad de una sola descarga", T4a_ItemSpeedLimit);
            Run("4a motor: limitador global Activar/Desactivar", T4a_GlobalLimiterToggle);
            Run("4a Agregar URL con autorización (usuario y contraseña de la descarga)", T4a_AddUrlAuth);
            Run("4a Información del archivo: baja mientras se muestra e Iniciar descarga sigue", T4a_InfoStart);
            Run("4a Información del archivo: Cancelar la descarta sin dejar rastro", T4a_InfoCancel);
            Run("4a Información del archivo: Descargar más tarde la deja en la cola con lo bajado", T4a_InfoLater);
            Run("4a Redescargar sustituye al archivo existente", T4a_Redownload);
            Run("4a Mover/Renombrar: completa y a medio descargar", T4a_MoveRename);
            Run("4a Actualizar dirección de descarga con el enlace nuevo del navegador", T4a_RefreshAddress);
            Run("4a Enlace duplicado: numerar, sobrescribir o continuar", T4a_Duplicates);
            Run("4a Descarga completada: aparece al terminar salvo que se desactive", T4a_CompleteDialog);
            Run("4a Opciones de finalización: salir al terminar", T4a_CompletionActions);
            Run("4a Sonidos: descarga completa, fallida y cola", T4a_Sounds);
            Run("4a Columnas: configurables, ordenables y guardadas", T4a_Columns);
            Run("4a Propiedades: dirección, nombre, descripción y usuario", T4a_Properties);
            Run("4a Navegador con diálogo: lo confirmado queda en la lista guardada", T4a_BrowserWithDialog);
            Run("4a Información del archivo: si termina antes de confirmar, el aviso espera y se puede mover", T4a_InfoFinishedBeforeConfirm);
            Run("4a Host nativo: la extensión recibe los tipos de archivo de Opciones", T4a_HostConfig);
            Run("4a Ventanas nuevas sin controles superpuestos", T4a_DialogsNoOverlap);
        }

        static bool PumpUntil(Func<bool> cond, int seconds)
        {
            DateTime limit = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < limit) { Application.DoEvents(); if (cond()) return true; Thread.Sleep(30); }
            Application.DoEvents();
            return cond();
        }

        static void CloseOpenForms(Type t)
        {
            List<Form> l = new List<Form>();
            foreach (Form f in Application.OpenForms) if (t.IsInstanceOfType(f)) l.Add(f);
            foreach (Form f in l) f.Close();
            Application.DoEvents();
        }

        static int CountOpenForms(Type t)
        {
            int n = 0;
            foreach (Form f in Application.OpenForms) if (t.IsInstanceOfType(f)) n++;
            return n;
        }

        static void T4a_ConnectionTable()
        {
            long size = 4 * 1024 * 1024;
            DownloadManager m = NewMgr();
            DownloadItem it = m.Add(S.Url("range,slow", "tabla.bin", size, NewId("tabla")), Dl, null, true);
            bool sawReceiving = false;
            int maxRows = 0;
            Check(WaitUntil(delegate
            {
                ConnectionInfo[] rows = it.Engine != null ? it.Engine.Connections : new ConnectionInfo[0];
                maxRows = Math.Max(maxRows, rows.Length);
                foreach (ConnectionInfo r in rows) if (r.Info == "Recibiendo datos...") sawReceiving = true;
                return it.Status == DownloadStatus.Complete;
            }, 60), "No terminó: " + it.Status + " " + it.Error);
            AssertComplete(it, size);
            ConnectionInfo[] end = it.Engine.Connections;
            long sum = 0;
            for (int i = 0; i < end.Length; i++)
            {
                Check(end[i].Number == i + 1, "Numeración: " + end[i].Number);
                sum += end[i].Bytes;
            }
            Check(sawReceiving, "Ninguna conexión mostró «Recibiendo datos...»");
            Check(maxRows >= 2, "Solo hubo " + maxRows + " conexión(es) en la tabla");
            Check(sum == size, "La columna Descargado suma " + sum + " y el archivo tiene " + size);
            Check(Array.TrueForAll(end, delegate(ConnectionInfo r) { return r.Info == "Completo."; }), "Al terminar no todas dicen «Completo.»: " + string.Join(" | ", Array.ConvertAll(end, delegate(ConnectionInfo r) { return r.Info; })));
        }

        static void T4a_ItemSpeedLimit()
        {
            long size = 1024 * 1024;
            DownloadManager m = NewMgr();
            AddOptions o = new AddOptions(); o.Url = S.Url("range", "lim.bin", size, NewId("lim")); o.Folder = Dl;
            DownloadItem it = m.Add(o);
            it.SpeedLimitOn = true; it.SpeedLimitKBps = 256;
            Stopwatch sw = Stopwatch.StartNew();
            m.StartItem(it);
            WaitEnd(it, 30);
            AssertComplete(it, size);
            // 1 MB a 256 KB/s: unos 3 s (el primer segundo puede ir en ráfaga).
            Check(sw.Elapsed.TotalSeconds >= 2.5, "No se limitó: " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
            Check(sw.Elapsed.TotalSeconds < 9, "Demasiado lento: " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");

            // Sin "Recordar...", al detener se olvida el límite.
            DownloadItem it2 = m.Add(S.Url("range,veryslow", "lim2.bin", size, NewId("lim2")), Dl, null, false);
            it2.SpeedLimitOn = true; it2.SpeedLimitKBps = 50;
            m.StopItem(it2);
            Check(!it2.SpeedLimitOn, "El límite sigue tras detener sin «Recordar»");
        }

        static void T4a_GlobalLimiterToggle()
        {
            AppConfig c = Cfg();
            c.SpeedLimitKBps = 300; c.SpeedLimiterOn = false;
            Check(c.EffectiveSpeedLimitKBps == 0, "Desactivado debería ser 0");
            c.SpeedLimiterOn = true;
            Check(c.EffectiveSpeedLimitKBps == 300, "Activado: " + c.EffectiveSpeedLimitKBps);
            AppConfig old = Cfg(); old.SpeedLimiterOn = null; old.SpeedLimitKBps = 120;
            Check(old.EffectiveSpeedLimitKBps == 120, "Configuración antigua (sin la casilla): " + old.EffectiveSpeedLimitKBps);
            long size = 768 * 1024;
            Stopwatch sw = Stopwatch.StartNew();
            DownloadItem it = Download(new DownloadManager(c), "range", size, "glim", 30);
            AssertComplete(it, size);
            Check(sw.Elapsed.TotalSeconds >= 1.4, "El limitador global activado no limitó: " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
        }

        static void T4a_AddUrlAuth()
        {
            UseFreshHome("auth4a");
            DownloadManager m = NewMgr();
            AddOptions o = new AddOptions(); o.Url = S.Url("range,basicauth", "priv.bin", 300 * 1024, NewId("priv")); o.Folder = Dl;
            o.LoginUser = "usuario"; o.LoginPassword = "clave"; o.StartNow = true;
            DownloadItem it = m.Add(o);
            WaitEnd(it, 30);
            AssertComplete(it, 300 * 1024);
            m.SaveList();
            string json = File.ReadAllText(AppConfig.DownloadsPath);
            Check(json.Contains("\"LoginUser\":\"usuario\"") && !json.Contains("clave"), "La contraseña de la descarga no se guarda cifrada");
            DownloadManager m2 = NewMgr(); m2.LoadList();
            Check(m2.Items.Count == 1 && m2.Items[0].LoginPassword == "clave", "No se recuperó el usuario/contraseña de la descarga");
        }

        static FileInfoForm NewInfo(DownloadManager m, string modes, string name, long size, string tag, bool start)
        {
            AddOptions o = new AddOptions(); o.Url = S.Url(modes, name, size, NewId(tag)); o.Folder = Dl; o.FileName = name;
            o.Pending = true; o.StartNow = start;
            DownloadItem it = m.Add(o);
            return new FileInfoForm(m.Config, m, it);
        }

        static void T4a_InfoStart()
        {
            UseFreshHome("info-start");
            AppConfig c = Cfg(); c.UseTempFolder = true; c.TempFolder = Path.Combine(Home, "tmp-info");
            DownloadManager m = new DownloadManager(c);
            long size = 2 * 1024 * 1024;
            using (FileInfoForm f = NewInfo(m, "range,slow", "mientras.bin", size, "info", true))
            {
                DownloadItem it = f.Item;
                Check(PumpUntil(delegate { return it.Downloaded > 100 * 1024; }, 20), "No empezó a bajar mientras se mostraba el diálogo");
                m.SaveList();
                Check(!File.ReadAllText(AppConfig.DownloadsPath).Contains("mientras.bin"), "La descarga pendiente ya está en la lista guardada");
                Field<TextBox>(f, "_path").Text = Path.Combine(Dl, "renombrado-info.bin");
                Field<TextBox>(f, "_desc").Text = "mi descripción";
                f.Start();
                Check(f.Choice == "start" && !it.Pending, "No se confirmó");
                WaitEnd(it, 60);
                AssertComplete(it, size);
                Check(it.FilePath == Path.Combine(Dl, "renombrado-info.bin"), "Ruta final: " + it.FilePath);
                Check(it.Description == "mi descripción", "Descripción: " + it.Description);
                m.SaveList();
                Check(File.ReadAllText(AppConfig.DownloadsPath).Contains("renombrado-info.bin"), "Tras «Iniciar descarga» no está en la lista guardada");
            }
        }

        static void T4a_InfoCancel()
        {
            AppConfig c = Cfg(); c.UseTempFolder = true; c.TempFolder = Path.Combine(Home, "tmp-cancel");
            DownloadManager m = new DownloadManager(c);
            using (FileInfoForm f = NewInfo(m, "range,veryslow", "cancelado.bin", 2 * 1024 * 1024, "cancel", true))
            {
                DownloadItem it = f.Item;
                Check(PumpUntil(delegate { return it.Downloaded > 20 * 1024; }, 20), "No empezó a bajar");
                string temp = it.TempPath;
                f.Cancel();
                Check(f.Choice == "cancel", "Elección: " + f.Choice);
                Check(!m.Items.Contains(it), "Sigue en la lista");
                Check(temp != null && !File.Exists(temp), "Quedó el temporal: " + temp);
                Check(!File.Exists(Path.Combine(Dl, "cancelado.bin")), "Quedó un archivo en el destino");
            }
        }

        static void T4a_InfoLater()
        {
            AppConfig c = Cfg(); c.UseTempFolder = true; c.TempFolder = Path.Combine(Home, "tmp-later");
            DownloadManager m = new DownloadManager(c);
            long size = 1536 * 1024;
            using (FileInfoForm f = NewInfo(m, "range,slow", "luego.bin", size, "later", true))
            {
                DownloadItem it = f.Item;
                Check(PumpUntil(delegate { return it.Downloaded > 100 * 1024; }, 20), "No empezó a bajar");
                f.Later(); // sin ventana visible no se muestra el panel de colas: cola principal
                Check(f.Choice == "later" && !it.Pending, "No se confirmó");
                Check(it.Engine == null || !it.Engine.IsRunning, "Sigue descargando");
                Check(it.Queue == DownloadItem.MainQueue && it.Downloaded > 0, "Cola " + it.Queue + ", bajado " + it.Downloaded);
                m.StartQueue();
                WaitEnd(it, 60);
                AssertComplete(it, size);
            }
        }

        static void T4a_Redownload()
        {
            AppConfig c = Cfg(); c.UseTempFolder = true; c.TempFolder = Path.Combine(Home, "tmp-redl");
            DownloadManager m = new DownloadManager(c);
            long size = 600 * 1024;
            DownloadItem it = m.Add(S.Url("range", "otra-vez.bin", size, NewId("redl")), Dl, null, true);
            WaitEnd(it, 30);
            AssertComplete(it, size);
            string path = it.FilePath;
            File.WriteAllText(path, "contenido estropeado");
            m.Redownload(it);
            WaitEnd(it, 30);
            AssertComplete(it, size);
            Check(it.FilePath == path, "Se guardó con otro nombre: " + it.FilePath);
            Check(!File.Exists(Path.Combine(Dl, "otra-vez_1.bin")) && !File.Exists(Path.Combine(Dl, "otra-vez (1).bin")), "Quedó una copia numerada");
        }

        static void T4a_MoveRename()
        {
            AppConfig c = Cfg(); c.UseTempFolder = true; c.TempFolder = Path.Combine(Home, "tmp-move");
            DownloadManager m = new DownloadManager(c);
            DownloadItem done = m.Add(S.Url("range", "mover.bin", 200 * 1024, NewId("move")), Dl, null, true);
            WaitEnd(done, 30);
            AssertComplete(done, 200 * 1024);
            string old = done.FilePath, dest = Path.Combine(Dl, "movidos", "nuevo-nombre.bin");
            m.MoveRename(done, dest);
            Check(!File.Exists(old) && File.Exists(dest) && done.FilePath == dest, "No se movió el archivo completo");
            AssertComplete(done, 200 * 1024);

            long size = 1024 * 1024;
            DownloadItem half = m.Add(S.Url("range,slow", "a-medias.bin", size, NewId("move2")), Dl, null, true);
            WaitProgress(half, 150 * 1024, 20);
            string dest2 = Path.Combine(Dl, "movidos", "a-medias-renombrado.bin");
            m.MoveRename(half, dest2);
            WaitEnd(half, 60);
            AssertComplete(half, size);
            Check(half.FilePath == dest2, "Ruta final: " + half.FilePath);
            Check(!File.Exists(Path.Combine(Dl, "a-medias.bin")), "Quedó el archivo con el nombre viejo");
        }

        static void T4a_RefreshAddress()
        {
            MainForm f = NewMainForm();
            Func<string, bool> savedHook = MainForm.ConfirmHook;
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                string id = NewId("refresh");
                long size = 300 * 1024;
                DownloadItem it = m.Add(S.Url("notfound", "caducado.bin", size, id), Dl, null, true);
                WaitEnd(it, 20);
                Check(it.Status == DownloadStatus.Error, "Debería fallar: " + it.Status);
                f.RefreshAddress(it);
                Check(f.WaitingForNewAddress, "No quedó esperando la nueva dirección");
                string asked = null;
                MainForm.ConfirmHook = delegate(string q) { asked = q; return true; };
                string newUrl = S.Url("range", "caducado.bin", size, id + "-nuevo");
                string r = f.HandleCommand("{\"type\":\"download\",\"url\":" + Ipc.JsonQuote(newUrl) + ",\"filename\":\"caducado.bin\",\"cookies\":\"s=1\"}");
                Check(r.Contains("\"ok\":true"), "Respuesta: " + r);
                Check(PumpUntil(delegate { return it.Url == newUrl; }, 10), "No se aplicó la nueva dirección");
                Check(asked != null && asked.Contains("caducado.bin"), "No se preguntó si era la nueva dirección");
                Check(PumpUntil(delegate { return it.Status == DownloadStatus.Complete; }, 30), "No terminó con la dirección nueva: " + it.Status + " " + it.Error);
                AssertComplete(it, size);
                Check(m.Snapshot().Count == 1, "Se añadió una descarga nueva en vez de actualizar la existente");
                Check(!f.WaitingForNewAddress, "Sigue esperando");
                Check(it.Cookies == "s=1", "No se guardaron las cookies nuevas");
            }
            finally { MainForm.ConfirmHook = savedHook; CloseOpenForms(typeof(RefreshWaitForm)); DisposeMainForm(f); }
        }

        static void T4a_Duplicates()
        {
            MainForm f = NewMainForm();
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                AppConfig c = m.Config;
                c.UseTempFolder = true; c.TempFolder = Path.Combine(Home, "tmp-dup");
                long size = 100 * 1024;
                string url = S.Url("range", "doble.bin", size, NewId("dup"));
                AddOptions o = new AddOptions(); o.Url = url; o.Folder = Dl; o.StartNow = true;
                DownloadItem first = f.NewDownload(o, false);
                WaitEnd(first, 30);
                AssertComplete(first, size);

                c.DuplicateAction = "number";
                o = new AddOptions(); o.Url = url; o.Folder = Dl; o.StartNow = true;
                DownloadItem second = f.NewDownload(o, false);
                WaitEnd(second, 30);
                Check(second != first && second.FilePath != first.FilePath, "Numerar: misma ruta " + second.FilePath);
                AssertComplete(second, size);

                c.DuplicateAction = "resume";
                o = new AddOptions(); o.Url = url; o.Folder = Dl; o.StartNow = true;
                int before = m.Snapshot().Count;
                DownloadItem third = f.NewDownload(o, false);
                Check(third == first || third == second, "Continuar: debía devolver la existente");
                Check(m.Snapshot().Count == before, "Continuar: se añadió otra");

                c.DuplicateAction = "overwrite";
                File.WriteAllText(first.FilePath, "viejo");
                string path = first.FilePath;
                // Hay dos con esa dirección: se sustituye la primera que aparece en la lista.
                DownloadItem target = m.FindDuplicate(url, null);
                path = target.FilePath;
                o = new AddOptions(); o.Url = url; o.Folder = Dl; o.StartNow = true;
                DownloadItem fourth = f.NewDownload(o, false);
                Check(!m.Snapshot().Contains(target), "Sobrescribir: la anterior sigue en la lista");
                WaitEnd(fourth, 30);
                AssertComplete(fourth, size);
                Check(fourth.FilePath == path, "Sobrescribir: otra ruta " + fourth.FilePath + " en vez de " + path);
            }
            finally { DisposeMainForm(f); }
        }

        static void T4a_CompleteDialog()
        {
            MainForm f = NewMainForm();
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                m.Config.ShowCompleteDialog = true;
                DownloadItem it = m.Add(S.Url("range", "fin.bin", 64 * 1024, NewId("fin")), Dl, null, true);
                Check(PumpUntil(delegate { return CountOpenForms(typeof(CompleteForm)) == 1; }, 20), "No apareció «Descarga completada»");
                CloseOpenForms(typeof(CompleteForm));

                DownloadItem it2 = m.Add(S.Url("range", "fin2.bin", 64 * 1024, NewId("fin2")), Dl, null, false);
                it2.ShowCompleteDialog = false; // pestaña "Opciones de finalización" de su ventana de progreso
                m.StartItem(it2);
                Check(PumpUntil(delegate { return it2.Status == DownloadStatus.Complete; }, 20), "No terminó");
                PumpUntil(delegate { return false; }, 1);
                Check(CountOpenForms(typeof(CompleteForm)) == 0, "Apareció aunque estaba desactivado para esa descarga");

                // Las que lanza la cola no muestran el diálogo (como IDM).
                DownloadItem it3 = m.Add(S.Url("range", "fin3.bin", 64 * 1024, NewId("fin3")), Dl, null, false);
                m.StartQueue();
                Check(PumpUntil(delegate { return it3.Status == DownloadStatus.Complete; }, 20), "La cola no la terminó");
                PumpUntil(delegate { return false; }, 1);
                Check(CountOpenForms(typeof(CompleteForm)) == 0, "Apareció para una descarga de la cola");
            }
            finally { CloseOpenForms(typeof(CompleteForm)); DisposeMainForm(f); }
        }

        static void T4a_CompletionActions()
        {
            MainForm f = NewMainForm();
            Func<string, bool> savedHook = MainForm.ConfirmHook;
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                bool exited = false;
                f.ExitHook = delegate { exited = true; };
                MainForm.ConfirmHook = delegate(string q) { return true; };

                DownloadItem a = m.Add(S.Url("range", "salir.bin", 64 * 1024, NewId("exit")), Dl, null, false);
                a.ExitWhenDone = true;
                m.StartItem(a);
                Check(PumpUntil(delegate { return exited; }, 20), "No salió al terminar");
                // "Apagar equipo al finalizar" no se prueba a propósito.
            }
            finally { MainForm.ConfirmHook = savedHook; DisposeMainForm(f); }
        }

        static void T4a_Sounds()
        {
            MainForm f = NewMainForm();
            Action<string> saved = MainForm.SoundHook;
            List<string> played = new List<string>();
            try
            {
                MainForm.SoundHook = delegate(string ev) { lock (played) played.Add(ev); };
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                foreach (SoundSetting s in m.Config.Sounds) s.Enabled = s.Event != SoundSetting.QueueStart;
                DownloadItem ok = m.Add(S.Url("range", "suena.bin", 32 * 1024, NewId("snd")), Dl, null, true);
                Check(PumpUntil(delegate { lock (played) return played.Contains(SoundSetting.Complete); }, 20), "No sonó «Descarga completa»");
                DownloadItem bad = m.Add(S.Url("notfound", "falla.bin", 32 * 1024, NewId("snd2")), Dl, null, true);
                Check(PumpUntil(delegate { lock (played) return played.Contains(SoundSetting.Failed); }, 20), "No sonó «Descarga fallida»");
                m.Remove(bad, false); // si no, la cola la reintentaría cada 30 s (como IDM) y no terminaría
                m.StartQueue();
                Check(PumpUntil(delegate { lock (played) return played.Contains(SoundSetting.QueueStop); }, 20), "No sonó «Proceso de colas detenido/finalizado»");
                lock (played) Check(!played.Contains(SoundSetting.QueueStart), "Sonó un evento desactivado");
            }
            finally { MainForm.SoundHook = saved; DisposeMainForm(f); }
        }

        static void T4a_Columns()
        {
            MainForm f = NewMainForm();
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                DownloadItem small = m.Add(S.Url("range", "b-pequeno.bin", 10 * 1024, NewId("cs")), Dl, null, true);
                DownloadItem big = m.Add(S.Url("range", "a-grande.bin", 300 * 1024, NewId("cb")), Dl, null, true);
                WaitEnd(small, 20); WaitEnd(big, 20);
                ListView list = Field<ListView>(f, "_list");
                IntPtr lh = list.Handle; // la ventana está oculta: el orden lo hace el control nativo
                f.ApplyColumns("size:90,name:200,status:80,url:150");
                Check(list.Columns.Count == 4, "Columnas: " + list.Columns.Count);
                Check(list.Columns[0].Text == "Nombre de Archivo", "La primera de la colección debe ser el nombre (lleva el icono)");
                Check(list.Columns[0].DisplayIndex == 1 && list.Columns[1].Text == "Tamaño" && list.Columns[1].DisplayIndex == 0, "No respeta el orden pedido");
                PumpUntil(delegate { return list.Items.Count == 2; }, 5);
                f.SortBy("size", true);
                Check(((DownloadItem)list.Items[0].Tag) == big, "Orden por tamaño descendente");
                f.SortBy("name", false);
                Check(((DownloadItem)list.Items[0].Tag) == big, "Orden por nombre (a-grande primero)");
                Check(list.Items[0].SubItems[2].Text == "Completo", "Estado: " + list.Items[0].SubItems[2].Text);
                Check(!string.IsNullOrEmpty(list.Items[0].ImageKey), "Sin icono del tipo de archivo");
                AppConfig saved = AppConfig.Load();
                Check(saved.Columns == "size:90,name:200,status:80,url:150" && saved.SortColumn == "name", "No se guardó: " + saved.Columns + " / " + saved.SortColumn);
                // Configuración rara: no rompe y siempre está el nombre.
                List<KeyValuePair<ColumnDef, int>> p = ListColumns.Parse("x:1,size:abc,size:20,,eta");
                Check(p.Count == 3 && p[0].Key.Id == "name" && p[1].Value == ListColumns.ById("size").Width, "Parse: " + ListColumns.Format(p));
            }
            finally { DisposeMainForm(f); }
        }

        static void T4a_Properties()
        {
            DownloadManager m = NewMgr();
            DownloadItem it = m.Add(S.Url("range", "props.bin", 50 * 1024, NewId("props")), Dl, null, true);
            WaitEnd(it, 20);
            AssertComplete(it, 50 * 1024);
            string newUrl = it.Url + "&otro=1";
            using (PropertiesForm f = new PropertiesForm(m.Config, m, it))
            {
                Field<TextBox>(f, "_name").Text = "props-renombrado.bin";
                Field<TextBox>(f, "_url").Text = newUrl;
                Field<TextBox>(f, "_desc").Text = "copia de seguridad";
                Field<TextBox>(f, "_user").Text = "ana";
                Field<TextBox>(f, "_pass").Text = "secreto";
                Check(f.Apply(), "Apply falló");
            }
            Check(it.FilePath == Path.Combine(Dl, "props-renombrado.bin") && File.Exists(it.FilePath), "No se renombró: " + it.FilePath);
            Check(it.Url == newUrl && it.Description == "copia de seguridad", "Dirección/descripción");
            Check(it.LoginUser == "ana" && it.LoginPassword == "secreto", "Usuario/contraseña");
        }

        static void T4a_BrowserWithDialog()
        {
            MainForm f = NewMainForm();
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                m.Config.ShowFileInfoDialog = true; m.Config.StartWhileInfoDialog = true;
                m.Config.UseTempFolder = true; m.Config.TempFolder = Path.Combine(Home, "tmp-browser");
                string url = S.Url("range,slow", "del-navegador.zip", 1024 * 1024, NewId("brw"));
                string r = f.HandleCommand("{\"type\":\"download\",\"url\":" + Ipc.JsonQuote(url) + ",\"referrer\":\"http://127.0.0.1/page\",\"cookies\":\"a=1\",\"filename\":\"del-navegador.zip\",\"userAgent\":\"UA/1\",\"incognito\":false,\"size\":0}");
                Check(r.Contains("\"ok\":true"), "Respuesta: " + r);
                FileInfoForm info = null;
                Check(PumpUntil(delegate { foreach (Form o in Application.OpenForms) if (o is FileInfoForm) info = (FileInfoForm)o; return info != null; }, 10), "No apareció el diálogo");
                Check(!info.Item.Transient, "Una descarga normal se marcó como privada");
                info.Start();
                Check(PumpUntil(delegate { return info.Item.Status == DownloadStatus.Complete; }, 30), "No terminó");
                m.SaveList();
                Check(File.ReadAllText(AppConfig.DownloadsPath).Contains("del-navegador.zip"), "No está en downloads.json: " + File.ReadAllText(AppConfig.DownloadsPath));
            }
            finally { CloseOpenForms(typeof(ProgressForm)); DisposeMainForm(f); }
        }

        static void T4a_InfoFinishedBeforeConfirm()
        {
            MainForm f = NewMainForm();
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                AppConfig c = m.Config;
                c.ShowFileInfoDialog = true; c.StartWhileInfoDialog = true; c.ShowCompleteDialog = true; c.ProgressDialogMode = "normal";
                c.UseTempFolder = true; c.TempFolder = Path.Combine(Home, "tmp-early");
                AddOptions o = new AddOptions(); o.Url = S.Url("range", "rapido.bin", 200 * 1024, NewId("early")); o.Folder = Dl; o.StartNow = true;
                DownloadItem it = f.NewDownload(o, true);
                FileInfoForm info = null;
                foreach (Form x in Application.OpenForms) if (x is FileInfoForm) info = (FileInfoForm)x;
                Check(info != null, "No apareció el diálogo");
                Check(PumpUntil(delegate { return it.Status == DownloadStatus.Complete; }, 20), "No terminó en segundo plano");
                PumpUntil(delegate { return false; }, 1);
                Check(CountOpenForms(typeof(CompleteForm)) == 0, "«Descarga completada» salió con el diálogo aún abierto");
                string moved = Path.Combine(Dl, "rapido-movido.bin");
                Field<TextBox>(info, "_path").Text = moved;
                info.Start();
                PumpUntil(delegate { return CountOpenForms(typeof(CompleteForm)) == 1; }, 5);
                Check(CountOpenForms(typeof(CompleteForm)) == 1, "No apareció «Descarga completada» al confirmar");
                Check(CountOpenForms(typeof(ProgressForm)) == 0, "Se abrió una ventana de progreso para una descarga ya terminada");
                Check(it.FilePath == moved && File.Exists(moved) && !File.Exists(Path.Combine(Dl, "rapido.bin")), "No se movió el archivo terminado: " + it.FilePath);
                AssertComplete(it, 200 * 1024);
            }
            finally { CloseOpenForms(typeof(CompleteForm)); CloseOpenForms(typeof(ProgressForm)); DisposeMainForm(f); }
        }

        static void T4a_HostConfig()
        {
            UseFreshHome("hostcfg");
            AppConfig c = Cfg();
            c.CaptureExts = "ABC R0* ZIP";
            c.CaptureExcludeSites = "*.bloqueado.com otro.net/videos/*";
            c.Save();
            string r = NativeHost.HandleMessage("{\"type\":\"getconfig\",\"rid\":3}");
            Check(r.StartsWith("{\"rid\":3,"), "Sin rid: " + r);
            Check(r.Contains("\"exts\":[\"abc\",\"r0*\",\"zip\"]"), "Tipos: " + r);
            Check(r.Contains("\"exclude\":[\"*.bloqueado.com\",\"otro.net/videos/*\"]"), "Sitios: " + r);
        }

        static void T4a_DialogsNoOverlap()
        {
            AppConfig c = Cfg();
            DownloadManager m = new DownloadManager(c);
            AddOptions o = new AddOptions(); o.Url = "http://x/y.zip"; o.Pending = true;
            DownloadItem it = m.Add(o);
            using (FileInfoForm f = new FileInfoForm(c, m, it)) { CheckNoOverlap(f, "Información del archivo"); f.Cancel(); }
            DownloadItem done = new DownloadItem(); done.FilePath = Path.Combine(Dl, "x.pdf"); done.Url = "http://x/x.pdf"; done.Size = 1000; done.Status = DownloadStatus.Complete;
            using (Form f = new CompleteForm(done)) CheckNoOverlap(f, "Descarga completada");
            using (Form f = new PropertiesForm(c, m, done)) CheckNoOverlap(f, "Propiedades");
            using (Form f = new ColumnsForm(c.Columns)) CheckNoOverlap(f, "Columnas");
            using (Form f = new DuplicateForm(done)) CheckNoOverlap(f, "Enlace duplicado");
            using (Form f = new QueuePickForm()) CheckNoOverlap(f, "Descargar más tarde");
            using (Form f = new RefreshWaitForm(done)) CheckNoOverlap(f, "Actualizar dirección");
            using (Form f = new ShutdownForm("Prueba", 30)) CheckNoOverlap(f, "Apagar");
            using (Form f = new SpeedLimiterForm(c)) CheckNoOverlap(f, "Limitador");
            using (Form f = new OptionsForm(c)) CheckNoOverlap(f, "Opciones");
            using (ProgressForm f = new ProgressForm(done, m)) CheckNoOverlap(f, "Progreso");
        }
    }
}
