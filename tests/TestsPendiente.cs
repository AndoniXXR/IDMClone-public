using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace IDMClone.Tests
{
    // Descargas que terminan con "Información del archivo a descargar" aún abierto. Como IDM (comprobado en IDM 6.43):
    // el archivo se queda en la carpeta temporal hasta confirmar; "Iniciar descarga" o "Descargar más tarde" lo pasan
    // una sola vez a la ruta elegida y "Cancelar" lo borra. Si la ruta elegida ya existe: "Sí" la reemplaza, "No" abre
    // "Guardar como" y "Cancelar" no añade la descarga.
    public static partial class TestMain
    {
        static void RunPendiente()
        {
            Run("pendiente: termina con el diálogo abierto y no aparece en el destino hasta confirmar", TP_HeldUntilConfirm);
            Run("pendiente: cambiar carpeta y nombre de una ya terminada la mueve una sola vez", TP_HeldMoveFolder);
            Run("pendiente: Cancelar una ya terminada borra el temporal y no toca el destino", TP_HeldCancel);
            Run("pendiente: ya existe -> Sí reemplaza el archivo", TP_ExistsYes);
            Run("pendiente: ya existe -> No abre Guardar como (y cerrarlo vuelve al diálogo)", TP_ExistsNo);
            Run("pendiente: ya existe -> Cancelar no añade la descarga", TP_ExistsCancel);
            Run("pendiente: ya existe -> Sí con la descarga aún en curso", TP_ExistsYesWhileRunning);
            Run("pendiente: Descargar más tarde de una ya terminada la coloca en su sitio", TP_HeldLater);
            Run("pendiente: si no se puede mover al confirmar sigue pendiente y se puede reintentar", TP_ConfirmFails);
            Run("pendiente: vídeo HLS terminado con el diálogo abierto espera con su carpeta de trabajo", TP_MediaHeld);
            Run("pendiente: ventana principal, Descargar más tarde de una terminada avisa de Descarga completada", TP_MainLater);
            Run("pendiente: subtítulos terminados con el diálogo abierto esperan a confirmar", TP_SubsHeld);
            Run("pendiente: vídeo con el audio aparte: el audio espera y va junto al vídeo a la ruta elegida", TP_SeparateAudioHeld);
            Run("pendiente: Sí, reemplazar y falla el movimiento -> el archivo del usuario sigue intacto", TP_OverwriteFailKeepsOriginal);
            Run("pendiente: el nombre se ocupó mientras esperaba sin cambiar la ruta -> también pregunta", TP_HeldDestTaken);
            Run("pendiente: fecha del servidor en el archivo colocado al confirmar", TP_HeldServerDate);
            Run("pendiente: el antivirus analiza el archivo ya en su sitio, no en la temporal", TP_HeldVirusCheck);
            Run("pendiente: salir de IDMClone con el diálogo abierto la cancela sin dejar el temporal", TP_ExitWithDialog);
            Run("pendiente: cerrar el diálogo mientras mueve el archivo al confirmar (termina o falla)", TP_CloseDuringConfirm);
            Run("pendiente: cerrar el diálogo con el aviso de ya existe abierto (al salir) no sigue ni falla", TP_CloseDuringExistsPrompt);
            Run("pendiente: Cancelar mientras baja no borra un archivo del usuario que apareció en el destino", TP_CancelRunningKeepsUserFile);
        }

        static FileInfoForm NewHeld(DownloadManager m, string name, long size, string tag)
        {
            FileInfoForm f = NewInfo(m, "range", name, size, tag, true);
            DownloadItem it = f.Item;
            Check(PumpUntil(delegate { return it.Status == DownloadStatus.Complete && (it.Engine == null || !it.Engine.IsRunning); }, 30),
                "No terminó en segundo plano: " + it.Status + " " + it.Error);
            return f;
        }

        static AppConfig PendCfg(string tag)
        {
            AppConfig c = Cfg(); c.UseTempFolder = true; c.TempFolder = Path.Combine(Home, "tmp-pend-" + tag);
            return c;
        }

        // Lo que se espera de una descarga retenida: en la temporal, nada en el destino, aún fuera de la lista.
        static void CheckHeld(DownloadManager m, DownloadItem it, long size)
        {
            Check(it.Pending && it.HeldInTemp, "No quedó retenida (pendiente " + it.Pending + ", retenida " + it.HeldInTemp + ")");
            Check(!string.IsNullOrEmpty(it.TempPath) && File.Exists(it.TempPath) && new FileInfo(it.TempPath).Length == size, "El temporal no está completo: " + it.TempPath);
            Check(!File.Exists(it.FilePath), "Ya está en el destino sin haber confirmado: " + it.FilePath);
            m.SaveList();
            Check(!File.Exists(AppConfig.DownloadsPath) || !File.ReadAllText(AppConfig.DownloadsPath).Contains(Path.GetFileName(it.FilePath)), "La pendiente está en la lista guardada");
        }

        static void CheckPlaced(DownloadItem it, string path, long size, string temp)
        {
            Check(!it.Pending && !it.HeldInTemp, "Sigue pendiente o retenida");
            Check(it.FilePath == path, "Ruta final: " + it.FilePath + " (esperada " + path + ")");
            Check(it.TempPath == null && !File.Exists(temp), "Quedó el temporal: " + temp);
            Check(!Directory.Exists(Path.GetDirectoryName(temp)), "Quedó la carpeta del temporal");
            AssertComplete(it, size);
        }

        static void TP_HeldUntilConfirm()
        {
            UseFreshHome("pend-held");
            DownloadManager m = new DownloadManager(PendCfg("held"));
            long size = 300 * 1024;
            using (FileInfoForm f = NewHeld(m, "retenido.bin", size, "held"))
            {
                DownloadItem it = f.Item;
                PumpUntil(delegate { return false; }, 1); // por si algo lo moviera tarde
                CheckHeld(m, it, size);
                string path = it.FilePath, temp = it.TempPath;
                f.Start();
                Check(f.Choice == "start", "Elección: " + f.Choice);
                CheckPlaced(it, path, size, temp);
                Check(m.Items.Contains(it), "No está en la lista");
                m.SaveList();
                Check(File.ReadAllText(AppConfig.DownloadsPath).Contains("retenido.bin"), "No está en la lista guardada");
                Check(it.Engine == null || !it.Engine.IsRunning, "Se volvió a descargar");
            }
        }

        static void TP_HeldMoveFolder()
        {
            DownloadManager m = new DownloadManager(PendCfg("move"));
            long size = 250 * 1024;
            using (FileInfoForm f = NewHeld(m, "mover-pend.bin", size, "move"))
            {
                DownloadItem it = f.Item;
                CheckHeld(m, it, size);
                string first = it.FilePath, temp = it.TempPath;
                string moved = Path.Combine(Dl, "otra-carpeta-pend", "movido.bin");
                Field<TextBox>(f, "_path").Text = moved;
                f.Start();
                CheckPlaced(it, moved, size, temp);
                Check(!File.Exists(first), "Quedó una copia en la carpeta original: " + first);
                Check(Directory.GetFiles(Path.GetDirectoryName(moved)).Length == 1, "Hay más de un archivo en la carpeta nueva");
            }
        }

        static void TP_HeldCancel()
        {
            DownloadManager m = new DownloadManager(PendCfg("cancel"));
            using (FileInfoForm f = NewHeld(m, "cancelar-terminado.bin", 200 * 1024, "hcancel"))
            {
                DownloadItem it = f.Item;
                string temp = it.TempPath, path = it.FilePath;
                // Un archivo que aparece con ese nombre mientras tanto no es de la descarga: no se borra.
                File.WriteAllText(path, "del usuario");
                f.Cancel();
                Check(f.Choice == "cancel" && !m.Items.Contains(it), "No se descartó");
                Check(!File.Exists(temp) && !Directory.Exists(Path.GetDirectoryName(temp)), "Quedó el temporal: " + temp);
                Check(File.Exists(path) && File.ReadAllText(path) == "del usuario", "Se borró o cambió el archivo del usuario");
                File.Delete(path);
            }
        }

        static void TP_ExistsYes()
        {
            DownloadManager m = new DownloadManager(PendCfg("yes"));
            long size = 220 * 1024;
            string existing = Path.Combine(Dl, "existe-si.bin");
            File.WriteAllText(existing, "viejo");
            List<string> asked = new List<string>();
            FileInfoForm.ExistsHook = delegate(string p) { asked.Add(p); return DialogResult.Yes; };
            try
            {
                using (FileInfoForm f = NewHeld(m, "existe-si-nuevo.bin", size, "yes"))
                {
                    DownloadItem it = f.Item;
                    CheckHeld(m, it, size);
                    string temp = it.TempPath;
                    Field<TextBox>(f, "_path").Text = existing;
                    f.Start();
                    Check(asked.Count == 1 && asked[0] == existing, "No preguntó si reemplazar: " + asked.Count);
                    CheckPlaced(it, existing, size, temp);
                    Check(!File.Exists(Path.Combine(Dl, "existe-si_1.bin")), "Se guardó con otro nombre en vez de reemplazar");
                }
            }
            finally { FileInfoForm.ExistsHook = null; }
        }

        static void TP_ExistsNo()
        {
            DownloadManager m = new DownloadManager(PendCfg("no"));
            long size = 210 * 1024;
            string existing = Path.Combine(Dl, "existe-no.bin");
            File.WriteAllText(existing, "conservar");
            string other = Path.Combine(Dl, "existe-no-otro.bin");
            string answer = null; int saveAs = 0;
            FileInfoForm.ExistsHook = delegate { return DialogResult.No; };
            FileInfoForm.SaveAsHook = delegate(string p) { saveAs++; Check(p == existing, "Guardar como partió de " + p); return answer; };
            try
            {
                using (FileInfoForm f = NewHeld(m, "existe-no-nuevo.bin", size, "no"))
                {
                    DownloadItem it = f.Item;
                    string temp = it.TempPath;
                    Field<TextBox>(f, "_path").Text = existing;
                    f.Start(); // cierra "Guardar como" sin elegir: vuelve al diálogo
                    Check(saveAs == 1, "No abrió Guardar como");
                    Check(f.Choice == null, "Se cerró el diálogo: " + f.Choice);
                    CheckHeld(m, it, size);
                    answer = other;
                    f.Start();
                    Check(saveAs == 2 && f.Choice == "start", "Elección: " + f.Choice);
                    CheckPlaced(it, other, size, temp);
                    Check(File.ReadAllText(existing) == "conservar", "Se tocó el archivo que ya existía");
                }
            }
            finally { FileInfoForm.ExistsHook = null; FileInfoForm.SaveAsHook = null; }
        }

        static void TP_ExistsCancel()
        {
            DownloadManager m = new DownloadManager(PendCfg("excancel"));
            string existing = Path.Combine(Dl, "existe-cancelar.bin");
            File.WriteAllText(existing, "conservar");
            FileInfoForm.ExistsHook = delegate { return DialogResult.Cancel; };
            try
            {
                using (FileInfoForm f = NewHeld(m, "existe-cancelar-nuevo.bin", 200 * 1024, "excancel"))
                {
                    DownloadItem it = f.Item;
                    string temp = it.TempPath, path = it.FilePath;
                    Field<TextBox>(f, "_path").Text = existing;
                    f.Start();
                    Check(f.Choice == "cancel" && !m.Items.Contains(it), "No se descartó la descarga: " + f.Choice);
                    Check(!File.Exists(temp), "Quedó el temporal");
                    Check(!File.Exists(path), "Quedó un archivo en el destino: " + path);
                    Check(File.ReadAllText(existing) == "conservar", "Se tocó el archivo que ya existía");
                }
            }
            finally { FileInfoForm.ExistsHook = null; }
        }

        static void TP_ExistsYesWhileRunning()
        {
            DownloadManager m = new DownloadManager(PendCfg("yesrun"));
            long size = 1536 * 1024;
            string existing = Path.Combine(Dl, "existe-en-curso.bin");
            File.WriteAllText(existing, "viejo");
            FileInfoForm.ExistsHook = delegate { return DialogResult.Yes; };
            try
            {
                using (FileInfoForm f = NewInfo(m, "range,slow", "en-curso-pend.bin", size, "yesrun", true))
                {
                    DownloadItem it = f.Item;
                    Check(PumpUntil(delegate { return it.Downloaded > 100 * 1024; }, 20), "No empezó a bajar");
                    Check(it.Status != DownloadStatus.Complete, "Terminó demasiado pronto para la prueba");
                    Check(File.ReadAllText(existing) == "viejo", "Se tocó el archivo antes de confirmar");
                    Field<TextBox>(f, "_path").Text = existing;
                    f.Start();
                    Check(f.Choice == "start" && !it.Pending, "No se confirmó");
                    WaitEnd(it, 60);
                    AssertComplete(it, size);
                    Check(it.FilePath == existing && new FileInfo(existing).Length == size, "No reemplazó: " + it.FilePath);
                    Check(!File.Exists(Path.Combine(Dl, "existe-en-curso_1.bin")), "Se guardó con otro nombre");
                }
            }
            finally { FileInfoForm.ExistsHook = null; }
        }

        static void TP_HeldLater()
        {
            DownloadManager m = new DownloadManager(PendCfg("later"));
            long size = 230 * 1024;
            using (FileInfoForm f = NewHeld(m, "luego-terminado.bin", size, "hlater"))
            {
                DownloadItem it = f.Item;
                CheckHeld(m, it, size);
                string path = it.FilePath, temp = it.TempPath;
                f.Later();
                Check(f.Choice == "later", "Elección: " + f.Choice);
                CheckPlaced(it, path, size, temp);
                Check(it.Queue == DownloadItem.NoQueue, "Una terminada quedó en la cola " + it.Queue);
                Check(m.Items.Contains(it), "No está en la lista");
            }
        }

        static void TP_ConfirmFails()
        {
            DownloadManager m = new DownloadManager(PendCfg("fail"));
            long size = 200 * 1024;
            using (FileInfoForm f = NewHeld(m, "no-se-mueve.bin", size, "fail"))
            {
                DownloadItem it = f.Item;
                string temp = it.TempPath, good = it.FilePath;
                // La "carpeta" de destino es un archivo: no se puede crear.
                string blocker = Path.Combine(Dl, "soy-un-archivo-pend");
                File.WriteAllText(blocker, "x");
                it.FilePath = Path.Combine(blocker, "no-se-mueve.bin");
                bool threw = false;
                try { m.Confirm(it); } catch (IOException) { threw = true; }
                Check(threw, "Confirm no avisó de que no se pudo mover");
                Check(it.Pending && it.HeldInTemp && File.Exists(temp) && new FileInfo(temp).Length == size, "Se perdió el temporal o dejó de estar pendiente");
                Check(!File.Exists(good), "Apareció algo en el destino: " + good);
                it.FilePath = good;
                m.Confirm(it);
                CheckPlaced(it, good, size, temp);
                File.Delete(blocker);
            }
        }

        static void TP_MediaHeld()
        {
            AppConfig c = PendCfg("media");
            DownloadManager m = new DownloadManager(c);
            string master = PutHls("hls-pend", 3);
            AddOptions o = new AddOptions();
            o.Url = master; o.MediaKind = "hls"; o.FileName = "Vídeo pendiente.mp4"; o.Folder = Dl; o.Pending = true; o.StartNow = true;
            DownloadItem it = m.Add(o);
            using (FileInfoForm f = new FileInfoForm(c, m, it))
            {
                Check(PumpUntil(delegate { return it.Status == DownloadStatus.Complete && (it.Engine == null || !it.Engine.IsRunning); }, 30),
                    "No terminó: " + it.Status + " " + it.Error);
                string work = DownloadEngine.MediaWorkDir(it, c);
                long size = new FileInfo(it.TempPath).Length;
                CheckHeld(m, it, size);
                Check(Directory.Exists(work) && it.TempPath.StartsWith(work, StringComparison.OrdinalIgnoreCase), "El vídeo no espera en su carpeta de trabajo: " + it.TempPath);
                string moved = Path.Combine(Dl, "Vídeo confirmado.mp4");
                Field<TextBox>(f, "_path").Text = moved;
                f.Start();
                Check(!it.Pending && !it.HeldInTemp && it.FilePath == moved && File.Exists(moved), "No se colocó: " + it.FilePath);
                Check(new FileInfo(moved).Length == size && Tracks(moved).Count == 2, "El vídeo colocado no está entero");
                Check(it.TempPath == null && !Directory.Exists(work), "Quedó la carpeta de trabajo");
                Check(!File.Exists(Path.Combine(Dl, "Vídeo pendiente.mp4")), "Quedó una copia con el nombre provisional");
            }
            // Y cancelar un vídeo retenido borra su carpeta de trabajo.
            o = new AddOptions();
            o.Url = master; o.MediaKind = "hls"; o.FileName = "Vídeo cancelado.mp4"; o.Folder = Dl; o.Pending = true; o.StartNow = true;
            DownloadItem it2 = m.Add(o);
            using (FileInfoForm f = new FileInfoForm(c, m, it2))
            {
                Check(PumpUntil(delegate { return it2.HeldInTemp && (it2.Engine == null || !it2.Engine.IsRunning); }, 30), "No quedó retenido: " + it2.Status);
                string work = DownloadEngine.MediaWorkDir(it2, c);
                f.Cancel();
                Check(!Directory.Exists(work) && !File.Exists(Path.Combine(Dl, "Vídeo cancelado.mp4")), "Quedó rastro del vídeo cancelado");
            }
        }

        static void TP_MainLater()
        {
            MainForm f = NewMainForm();
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                AppConfig c = m.Config;
                c.ShowFileInfoDialog = true; c.StartWhileInfoDialog = true; c.ShowCompleteDialog = true; c.ProgressDialogMode = "normal";
                c.UseTempFolder = true; c.TempFolder = Path.Combine(Home, "tmp-pend-main");
                AddOptions o = new AddOptions(); o.Url = S.Url("range", "rapido-luego.bin", 200 * 1024, NewId("pmain")); o.Folder = Dl; o.StartNow = true;
                DownloadItem it = f.NewDownload(o, true);
                FileInfoForm info = null;
                foreach (Form x in Application.OpenForms) if (x is FileInfoForm) info = (FileInfoForm)x;
                Check(info != null, "No apareció el diálogo");
                Check(PumpUntil(delegate { return it.Status == DownloadStatus.Complete; }, 20), "No terminó en segundo plano");
                PumpUntil(delegate { return false; }, 1);
                Check(CountOpenForms(typeof(CompleteForm)) == 0, "«Descarga completada» salió con el diálogo aún abierto");
                Check(!File.Exists(it.FilePath), "El archivo ya está en el destino antes de confirmar");
                info.Hide(); // oculto: sin el aviso modal de "ya bajó una parte" (no hay quien lo cierre en la prueba)
                info.Later();
                PumpUntil(delegate { return CountOpenForms(typeof(CompleteForm)) == 1; }, 5);
                Check(CountOpenForms(typeof(CompleteForm)) == 1, "No apareció «Descarga completada» con Descargar más tarde");
                PumpUntil(delegate { return false; }, 1);
                Check(CountOpenForms(typeof(CompleteForm)) == 1, "«Descarga completada» salió dos veces");
                Check(File.Exists(it.FilePath) && !it.Pending, "No se colocó el archivo: " + it.FilePath);
                AssertComplete(it, 200 * 1024);
            }
            finally { CloseOpenForms(typeof(CompleteForm)); CloseOpenForms(typeof(ProgressForm)); DisposeMainForm(f); }
        }
        static DownloadItem AddPendingMedia(DownloadManager m, string url, string kind, string name, string video, string audio)
        {
            AddOptions o = new AddOptions();
            o.Url = url; o.MediaKind = kind; o.FileName = name; o.MediaVideo = video; o.MediaAudio = audio; o.Folder = Dl;
            o.Pending = true; o.StartNow = true;
            return m.Add(o);
        }

        static bool WaitHeld(DownloadItem it, int seconds)
        {
            return PumpUntil(delegate { return it.HeldInTemp && (it.Engine == null || !it.Engine.IsRunning); }, seconds);
        }

        static void TP_SubsHeld()
        {
            AppConfig c = PendCfg("subs");
            DownloadManager m = new DownloadManager(c);
            string master = HlsSubsMaster("subs-pend");
            DownloadItem it = AddPendingMedia(m, master, DownloadEngine.SubtitleKind, "Pendiente.es.vtt", S.Base + "/s/subs-pend/subs/es.m3u8", null);
            using (FileInfoForm f = new FileInfoForm(c, m, it))
            {
                Check(WaitHeld(it, 20), "No quedaron retenidos: " + it.Status + " " + it.Error);
                CheckHeld(m, it, new FileInfo(it.TempPath).Length);
                string work = DownloadEngine.MediaWorkDir(it, c);
                f.Start();
                Check(!it.Pending && File.Exists(it.FilePath) && Path.GetFileName(it.FilePath) == "Pendiente.es.vtt", "No se colocaron: " + it.FilePath);
                Check(File.ReadAllText(it.FilePath).StartsWith("WEBVTT"), "Contenido de los subtítulos");
                Check(!Directory.Exists(work), "Quedó la carpeta de trabajo");
            }
        }

        static void TP_SeparateAudioHeld()
        {
            AppConfig c = PendCfg("audio"); c.MergeAudioVideo = false;
            DownloadManager m = new DownloadManager(c);
            string url = PutDash("dash-pend", 3);
            DownloadItem it = AddPendingMedia(m, url, "dash", "aparte-pend.mp4", "v", null);
            using (FileInfoForm f = new FileInfoForm(c, m, it))
            {
                Check(WaitHeld(it, 30), "No quedó retenido: " + it.Status + " " + it.Error);
                Check(Directory.GetFiles(Dl, "aparte-pend*").Length == 0, "Algo del vídeo ya está en el destino");
                Check(it.SeparateAudio != null && File.Exists(it.SeparateAudio), "El audio no espera en la carpeta de trabajo");
                string moved = Path.Combine(Dl, "pend-audio", "renombrado.mp4");
                Field<TextBox>(f, "_path").Text = moved;
                f.Start();
                Check(it.FilePath == moved && File.Exists(moved) && Tracks(moved)[0].IsVideo, "Vídeo: " + it.FilePath);
                string audio = Path.Combine(Dl, "pend-audio", "renombrado (audio).m4a");
                Check(File.Exists(audio) && Tracks(audio).Count == 1, "El audio no fue junto al vídeo");
                Check(it.Description != null && it.Description.Contains("renombrado (audio).m4a"), "Nota: " + it.Description);
                Check(it.SeparateAudio == null && !Directory.Exists(DownloadEngine.MediaWorkDir(it, c)), "Quedó la carpeta de trabajo");
                Check(Directory.GetFiles(Dl, "aparte-pend*").Length == 0, "Quedó algo con el nombre provisional");
            }
            DownloadItem it2 = AddPendingMedia(m, url, "dash", "aparte-cancelado.mp4", "v", null);
            using (FileInfoForm f = new FileInfoForm(c, m, it2))
            {
                Check(WaitHeld(it2, 30), "No quedó retenido (2): " + it2.Status + " " + it2.Error);
                f.Cancel();
                Check(Directory.GetFiles(Dl, "aparte-cancelado*").Length == 0, "Quedó el vídeo o el audio cancelado");
                Check(!Directory.Exists(DownloadEngine.MediaWorkDir(it2, c)), "Quedó la carpeta de trabajo (2)");
            }
        }

        static void TP_OverwriteFailKeepsOriginal()
        {
            DownloadManager m = new DownloadManager(PendCfg("owfail"));
            long size = 240 * 1024;
            using (FileInfoForm f = NewHeld(m, "reemplazo-falla.bin", size, "owfail"))
            {
                DownloadItem it = f.Item;
                string temp = it.TempPath;
                string existing = Path.Combine(Dl, "conservar-si-falla.bin");
                File.WriteAllText(existing, "original");
                it.FilePath = existing; it.Overwrite = true; // lo que deja "Sí, reemplazar"
                bool threw = false;
                using (new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.None)) // p. ej. el antivirus
                {
                    try { m.Confirm(it); } catch (IOException) { threw = true; }
                }
                Check(threw, "Confirm no avisó de que no se pudo mover");
                Check(File.Exists(existing) && File.ReadAllText(existing) == "original", "Se perdió el archivo del usuario");
                Check(it.Pending && it.HeldInTemp && File.Exists(it.TempPath), "Se perdió la descarga");
                m.Confirm(it);
                CheckPlaced(it, existing, size, temp);
                Check(!File.Exists(existing + ".idmclone-viejo"), "Quedó apartado el archivo anterior");
            }
        }

        static void TP_HeldDestTaken()
        {
            DownloadManager m = new DownloadManager(PendCfg("taken"));
            long size = 200 * 1024;
            int asked = 0;
            FileInfoForm.ExistsHook = delegate { asked++; return DialogResult.Yes; };
            try
            {
                using (FileInfoForm f = NewHeld(m, "ocupado-pend.bin", size, "taken"))
                {
                    DownloadItem it = f.Item;
                    string path = it.FilePath, temp = it.TempPath;
                    File.WriteAllText(path, "apareció mientras tanto");
                    f.Start();
                    Check(asked == 1, "No preguntó: " + asked);
                    CheckPlaced(it, path, size, temp);
                    Check(Directory.GetFiles(Dl, "ocupado-pend*").Length == 1, "Se guardó con otro nombre sin preguntar");
                }
            }
            finally { FileInfoForm.ExistsHook = null; }
        }

        static void TP_HeldServerDate()
        {
            AppConfig c = PendCfg("date"); c.SetFileDateFromServer = true;
            DownloadManager m = new DownloadManager(c);
            using (FileInfoForm f = NewHeld(m, "fecha-pend.bin", 200 * 1024, "pdate"))
            {
                DownloadItem it = f.Item;
                Field<TextBox>(f, "_path").Text = Path.Combine(Dl, "fecha-pend-movido.bin");
                f.Start();
                AssertComplete(it, 200 * 1024);
                CheckServerDate(it.FilePath, ServerDate);
            }
        }

        static FileInfoForm OpenInfo()
        {
            foreach (Form x in Application.OpenForms) if (x is FileInfoForm) return (FileInfoForm)x;
            return null;
        }

        static void TP_HeldVirusCheck()
        {
            Func<System.Diagnostics.ProcessStartInfo, System.Diagnostics.Process> saved = VirusCheck.Starter;
            List<string> scanned = new List<string>();
            VirusCheck.Starter = delegate(System.Diagnostics.ProcessStartInfo psi) { scanned.Add(psi.Arguments); return null; };
            MainForm f = NewMainForm();
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                AppConfig c = m.Config;
                c.ShowFileInfoDialog = true; c.StartWhileInfoDialog = true; c.ShowCompleteDialog = false; c.ProgressDialogMode = "normal";
                c.UseTempFolder = true; c.TempFolder = Path.Combine(Home, "tmp-pend-av");
                c.AntivirusPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                c.AntivirusArgs = "/c rem [File]";
                AddOptions o = new AddOptions(); o.Url = S.Url("range", "analizar-pend.exe", 200 * 1024, NewId("pav")); o.Folder = Dl; o.StartNow = true;
                DownloadItem it = f.NewDownload(o, true);
                FileInfoForm info = OpenInfo();
                Check(info != null, "No apareció el diálogo");
                Check(PumpUntil(delegate { return it.Status == DownloadStatus.Complete; }, 20), "No terminó en segundo plano");
                PumpUntil(delegate { return false; }, 1);
                Check(scanned.Count == 0, "Se analizó con el archivo aún en la temporal: " + string.Join(" | ", scanned.ToArray()));
                info.Hide();
                info.Start();
                Check(PumpUntil(delegate { return scanned.Count > 0; }, 10), "No se analizó al confirmar");
                PumpUntil(delegate { return false; }, 1);
                Check(scanned.Count == 1 && scanned[0] == "/c rem \"" + it.FilePath + "\"", "Análisis: " + string.Join(" | ", scanned.ToArray()));
            }
            finally { VirusCheck.Starter = saved; CloseOpenForms(typeof(CompleteForm)); DisposeMainForm(f); }
        }

        static void TP_ExitWithDialog()
        {
            MainForm f = NewMainForm();
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                AppConfig c = m.Config;
                c.ShowFileInfoDialog = true; c.StartWhileInfoDialog = true; c.ProgressDialogMode = "normal";
                c.UseTempFolder = true; c.TempFolder = Path.Combine(Home, "tmp-pend-exit");
                AddOptions o = new AddOptions(); o.Url = S.Url("range", "al-salir.bin", 200 * 1024, NewId("pexit")); o.Folder = Dl; o.StartNow = true;
                DownloadItem it = f.NewDownload(o, true);
                FileInfoForm info = OpenInfo();
                Check(info != null, "No apareció el diálogo");
                Check(WaitHeld(it, 20), "No quedó retenida");
                string temp = it.TempPath;
                typeof(MainForm).GetMethod("CloseFileInfoDialogs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, null);
                Check(info.Choice == "cancel" && OpenInfo() == null, "El diálogo no se cerró como Cancelar: " + info.Choice);
                Check(!m.Items.Contains(it) && !File.Exists(temp) && !File.Exists(it.FilePath), "Quedó rastro de la descarga");
            }
            finally { CloseOpenForms(typeof(FileInfoForm)); DisposeMainForm(f); }
        }
        // El diálogo se cierra (p. ej. al salir) mientras "Iniciar descarga" mueve el archivo, que tarda porque algo lo
        // tiene abierto: decide el movimiento, una sola vez. Si se suelta pronto, queda confirmada; si no, cancelada.
        static void TP_CloseDuringConfirm()
        {
            foreach (int lockMs in new int[] { 450, 2500 })
            {
                DownloadManager m = new DownloadManager(PendCfg("closing" + lockMs));
                long size = 200 * 1024;
                FileInfoForm f = NewHeld(m, "cerrar-moviendo-" + lockMs + ".bin", size, "closing");
                DownloadItem it = f.Item;
                string temp = it.TempPath, path = it.FilePath;
                int decided = 0;
                f.Decided += delegate { decided++; };
                FileStream hold = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.None);
                System.Threading.Timer release = new System.Threading.Timer(delegate { hold.Dispose(); }, null, lockMs, System.Threading.Timeout.Infinite);
                Timer close = new Timer(); close.Interval = 350;
                close.Tick += delegate { close.Stop(); f.Close(); };
                try
                {
                    IntPtr h = f.Handle;
                    close.Start();
                    f.Start(); // no hay "Msg.Show" que pueda quedarse esperando: al fallar ya está cerrado
                    Check(decided == 1, lockMs + " ms: se decidió " + decided + " veces (" + f.Choice + ")");
                    if (lockMs < 1000)
                    {
                        Check(f.Choice == "start" && m.Items.Contains(it) && !it.Pending, lockMs + " ms: no quedó confirmada: " + f.Choice);
                        CheckPlaced(it, path, size, temp);
                    }
                    else
                    {
                        Check(f.Choice == "cancel" && !m.Items.Contains(it), lockMs + " ms: no quedó cancelada: " + f.Choice);
                        Check(!File.Exists(path), lockMs + " ms: apareció en el destino");
                    }
                }
                finally { close.Dispose(); release.Dispose(); hold.Dispose(); f.Dispose(); }
            }
        }
        // Al salir de IDMClone se cierra el diálogo aunque esté preguntando si reemplazar: el aviso vuelve sin respuesta
        // (None) y el diálogo, ya cancelado, no debe seguir (ni "Guardar como" sobre un diálogo cerrado).
        static void TP_CloseDuringExistsPrompt()
        {
            DownloadManager m = new DownloadManager(PendCfg("closeask"));
            string existing = Path.Combine(Dl, "existe-al-salir.bin");
            File.WriteAllText(existing, "conservar");
            FileInfoForm f = NewHeld(m, "existe-al-salir-nuevo.bin", 200 * 1024, "closeask");
            int decided = 0, saveAs = 0;
            f.Decided += delegate { decided++; };
            FileInfoForm.ExistsHook = delegate { f.Close(); return DialogResult.None; };
            FileInfoForm.SaveAsHook = delegate { saveAs++; return null; };
            try
            {
                IntPtr h = f.Handle;
                DownloadItem it = f.Item;
                string temp = it.TempPath;
                Field<TextBox>(f, "_path").Text = existing;
                f.Start();
                Check(decided == 1 && f.Choice == "cancel", "Elección " + f.Choice + ", decidida " + decided + " veces");
                Check(saveAs == 0, "Abrió Guardar como con el diálogo ya cerrado");
                Check(!m.Items.Contains(it) && !File.Exists(temp), "Quedó la descarga o su temporal");
                Check(File.ReadAllText(existing) == "conservar", "Se tocó el archivo que ya existía");
            }
            finally { FileInfoForm.ExistsHook = null; FileInfoForm.SaveAsHook = null; f.Dispose(); }
        }

        static void TP_CancelRunningKeepsUserFile()
        {
            DownloadManager m = new DownloadManager(PendCfg("cancelrun"));
            using (FileInfoForm f = NewInfo(m, "range,veryslow", "cancelar-bajando-pend.bin", 2 * 1024 * 1024, "cancelrun", true))
            {
                DownloadItem it = f.Item;
                Check(PumpUntil(delegate { return it.Downloaded > 20 * 1024; }, 20), "No empezó a bajar");
                File.WriteAllText(it.FilePath, "del usuario");
                string temp = it.TempPath;
                f.Cancel();
                Check(!m.Items.Contains(it) && !File.Exists(temp), "Quedó la descarga o su temporal");
                Check(File.Exists(it.FilePath) && File.ReadAllText(it.FilePath) == "del usuario", "Se borró el archivo del usuario");
                File.Delete(it.FilePath);
            }
        }
    }
}
