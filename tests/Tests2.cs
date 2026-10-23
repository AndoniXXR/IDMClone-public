using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IDMClone.Tests
{
    // Tests de la fase 2: fallos medios del motor, cola, programador e interfaz.
    public static partial class TestMain
    {
        static TestFtpServer Ftp;

        static void RunPhase2()
        {
            Ftp = new TestFtpServer();
            Ftp.Start();

            // --- Motor ---
            Run("f2 motor: 404 falla enseguida sin 20 reintentos", T2_NotFoundFast);
            Run("f2 motor: 403 falla enseguida con mensaje claro", T2_ForbiddenFast);
            Run("f2 motor: 503 pasajero se reintenta y completa", T2_Transient503);
            Run("f2 motor: archivo cambiado en el servidor -> se baja la versión nueva (ETag/If-Range)", T2_FileChanged);
            Run("f2 motor: pausar durante la conexión inicial deja Pausado, no Error", T2_PauseDuringProbe);
            Run("real: CDN con ETags distintos para los mismos bytes (arXiv) no es 'archivo cambiado'", T2_EtagFlap);
            Run("real: servidor que limita conexiones (429, Wikimedia) -> se reducen y completa", T2_TooManyConnections);
            Run("real: TLS 1.2 activado para HTTPS", T2_Tls12Enabled);
            Run("real: servidor que bloquea 10 min (429 Retry-After 600) -> error claro sin insistir", T2_LongBan);
            Run("f2 motor: Content-Disposition (RFC 6266/5987)", T2_ContentDispositionParser);
            Run("f2 motor: nombre del servidor + categoría y carpeta recalculadas", T2_ServerNameAndCategory);
            Run("f2 motor: el nombre elegido por el usuario no se cambia", T2_UserNameKept);
            Run("f2 motor: FTP con varias conexiones", T2_FtpMulti);
            Run("f2 motor: FTP pausar y reanudar (REST)", T2_FtpResume);
            Run("f2 motor: FTP archivo inexistente (550) falla enseguida", T2_FtpMissing);

            // --- Cola y programador ---
            Run("f2 cola: 1 archivo a la vez, sale de la cola al completar, aviso de fin", T2_QueueOneAtATime);
            Run("f2 cola: fallido pasa al final, se reintenta y no bloquea a los demás", T2_QueueFailedToEnd);
            Run("f2 cola: con límite de reintentos la cola termina", T2_QueueRetryLimit);
            Run("f2 cola: detener la cola no para las descargas manuales", T2_QueueStopOnlyOwn);
            Run("f2 cola: una descarga detenida por el usuario no se relanza", T2_QueueRespectsUserStop);
            Run("f2 programador: diario, días, recuperación y sin repetir", T2_SchedulerDaily);
            Run("f2 programador: cruce de medianoche y parada independiente", T2_SchedulerMidnightAndStop);
            Run("f2 programador: una vez en fecha y cambio de hora (DST)", T2_SchedulerOnceAndDst);
            Run("f2 gestor: velocidad suavizada", T2_SpeedSmoothing);
            Run("f2 gestor: importar saca todas las URL del texto", T2_ExtractUrls);

            // --- Interfaz ---
            Run("f2 gui: ventana principal con menú arriba, barra debajo, árbol y lista", T2_MainLayout);
            Run("f2 gui: diálogos sin controles superpuestos", T2_DialogsNoOverlap);
            Run("f2 gui: Añadir URL elige la categoría por la extensión", T2_AddUrlCategory);
            Run("f2 gui: una sola ventana de progreso por descarga", T2_SingleProgressWindow);
            Run("f2 gui: iniciar con Windows (clave Run con /onboot)", T2_StartupRegistry);
            Run("f2 e2e: /onboot arranca oculto y /show lo muestra", T2_E2E_OnBoot);

            Ftp.Stop();
        }

        // ------------------------------------------------------------ motor

        static void T2_NotFoundFast()
        {
            Stopwatch sw = Stopwatch.StartNew();
            DownloadItem it = Download(NewMgr(), "notfound", 1000, "nf", 30);
            Check(it.Status == DownloadStatus.Error, "Estado " + it.Status);
            Check(sw.Elapsed.TotalSeconds < 3, "Tardó " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s: siguió reintentando un 404");
            Check(it.Error != null && it.Error.Contains("404"), "Mensaje: " + it.Error);
            Check(S.Count(IdOf(it) + ":req") <= 2, "Peticiones al servidor: " + S.Count(IdOf(it) + ":req"));
        }

        static void T2_ForbiddenFast()
        {
            Stopwatch sw = Stopwatch.StartNew();
            DownloadItem it = Download(NewMgr(), "forbidden", 1000, "fb", 30);
            Check(it.Status == DownloadStatus.Error, "Estado " + it.Status);
            Check(sw.Elapsed.TotalSeconds < 3, "Tardó demasiado");
            Check(it.Error != null && it.Error.Contains("403"), "Mensaje: " + it.Error);
        }

        static void T2_Transient503()
        {
            DownloadItem it = Download(NewMgr(), "range,flaky503", 600 * 1024, "flaky", 30);
            AssertComplete(it, 600 * 1024);
        }

        static void T2_FileChanged()
        {
            DownloadManager m = NewMgr();
            long size = 2 * 1024 * 1024;
            string id = NewId("changed");
            DownloadItem it = m.Add(S.Url("range,veryslow", "changed.bin", size, id), Dl, null, true);
            WaitProgress(it, 300 * 1024, 30);
            m.StopItem(it); WaitEnd(it, 10);
            S.SetVersion(id, 2); // el archivo cambia en el servidor mientras está en pausa
            m.StartItem(it);
            WaitEnd(it, 90);
            Check(it.Status == DownloadStatus.Complete, "Estado " + it.Status + " " + it.Error);
            Check(FileSha(it.FilePath) == TestServer.Sha256Of(size, 2), "El archivo mezcla la versión vieja y la nueva");
            Check(S.Count(id + ":ifrange-miss") >= 1, "No se envió If-Range (el servidor no detectó el cambio por esa vía)");
        }

        static void T2_PauseDuringProbe()
        {
            DownloadManager m = NewMgr();
            DownloadItem it = m.Add(S.Url("range,slowhead", "probe.bin", 100 * 1024, NewId("probe")), Dl, null, true);
            Thread.Sleep(500);
            Stopwatch sw = Stopwatch.StartNew();
            m.StopItem(it);
            WaitEnd(it, 10);
            Check(it.Status == DownloadStatus.Paused, "Estado " + it.Status + " (" + it.Error + "), se esperaba Pausado");
            Check(sw.Elapsed.TotalSeconds < 2, "La pausa durante la conexión tardó " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
        }

        static void T2_EtagFlap()
        {
            DownloadItem it = Download(NewMgr(), "range,slow,etagflap", 2 * 1024 * 1024, "flap", 60);
            AssertComplete(it, 2 * 1024 * 1024);
            Check(S.Count(IdOf(it) + ":ranged") >= 3, "No usó varias conexiones");
        }

        static void T2_TooManyConnections()
        {
            long size = 3 * 1024 * 1024;
            DownloadItem it = Download(NewMgr(), "range,slow,limit2", size, "limit", 90);
            AssertComplete(it, size);
            Check(S.Count(IdOf(it) + ":429") >= 1, "El servidor no llegó a limitar (la prueba no probó nada)");
            Check(S.Count(IdOf(it) + ":429") < 40, "Siguió insistiendo con demasiadas conexiones: " + S.Count(IdOf(it) + ":429") + " respuestas 429");
        }

        static void T2_LongBan()
        {
            Stopwatch sw = Stopwatch.StartNew();
            DownloadItem it = Download(NewMgr(), "ban", 1000, "ban", 30);
            Check(it.Status == DownloadStatus.Error, "Estado " + it.Status);
            Check(sw.Elapsed.TotalSeconds < 3, "Tardó " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s insistiendo");
            Check(it.Error != null && it.Error.Contains("10 min"), "Mensaje: " + it.Error);
        }

        static void T2_Tls12Enabled()
        {
            DownloadEngine.ConfigureNetwork();
            Check((System.Net.ServicePointManager.SecurityProtocol & System.Net.SecurityProtocolType.Tls12) != 0,
                  "TLS 1.2 no está activado: " + System.Net.ServicePointManager.SecurityProtocol);
            object[] attrs = typeof(DownloadEngine).Assembly.GetCustomAttributes(typeof(System.Runtime.Versioning.TargetFrameworkAttribute), false);
            Check(attrs.Length == 1, "Falta el atributo TargetFramework (el .exe correría en modo de compatibilidad antiguo)");
        }

        static void T2_ContentDispositionParser()
        {
            string[,] cases = {
                { "attachment; filename=\"a.zip\"", "a.zip" },
                { "attachment; filename=b.rar", "b.rar" },
                { "attachment; filename*=UTF-8''informe%20final%20%E2%82%AC.pdf; filename=\"fallback.pdf\"", "informe final €.pdf" },
                { "attachment; filename=\"fallback.pdf\"; filename*=UTF-8''%C3%B1and%C3%BA.txt", "ñandú.txt" },
                { "attachment; filename=\"con;punto y coma.zip\"", "con;punto y coma.zip" },
                { "inline; filename=\"C:\\\\malo\\\\ruta.exe\"", "ruta.exe" },
                { "attachment; filename=\"../../escape.bin\"", "escape.bin" },
                { "filename=solo.iso", "solo.iso" },
                { "attachment; filename*=iso-8859-1'es'caf%E9.doc", "café.doc" },
                { "attachment", null },
            };
            for (int i = 0; i < cases.GetLength(0); i++)
            {
                string got = Util.FileNameFromContentDisposition(cases[i, 0]);
                Check(got == cases[i, 1], "«" + cases[i, 0] + "» -> «" + got + "», se esperaba «" + cases[i, 1] + "»");
            }
            Check(Util.IsGenericName("get.php") && Util.IsGenericName("download") && !Util.IsGenericName("a.zip"), "IsGenericName");
        }

        static AppConfig CfgWithCategoryFolders()
        {
            AppConfig c = Cfg();
            foreach (Category cat in c.Categories) cat.Folder = Path.Combine(Dl, "cat-" + cat.Name);
            return c;
        }

        static void T2_ServerNameAndCategory()
        {
            AppConfig c = CfgWithCategoryFolders();
            DownloadManager m = new DownloadManager(c);
            long size = 300 * 1024;
            DownloadItem it = m.Add(S.Url("range,cd", "get.php", size, NewId("cd")), null, null, true);
            WaitEnd(it, 30);
            AssertComplete(it, size);
            Check(it.FileName == "informe final €.pdf", "Nombre: " + it.FileName);
            Check(it.Category == "Documentos", "Categoría: " + it.Category);
            Check(string.Equals(Path.GetDirectoryName(it.FilePath), Path.Combine(Dl, "cat-Documentos"), StringComparison.OrdinalIgnoreCase),
                  "Carpeta: " + Path.GetDirectoryName(it.FilePath));
            Check(!File.Exists(Path.Combine(Path.Combine(Dl, "cat-General"), "get.php")), "Quedó el archivo provisional get.php");
        }

        static void T2_UserNameKept()
        {
            DownloadManager m = NewMgr();
            DownloadItem it = m.Add(S.Url("range,cd", "get.php", 100 * 1024, NewId("cdu")), Dl, "mi-nombre.bin", true);
            WaitEnd(it, 30);
            AssertComplete(it, 100 * 1024);
            Check(it.FileName == "mi-nombre.bin", "Se cambió el nombre elegido: " + it.FileName);
        }

        static void T2_FtpMulti()
        {
            long size = 3 * 1024 * 1024 + 5;
            string id = NewId("ftp");
            DownloadManager m = NewMgr();
            DownloadItem it = m.Add(Ftp.Url("normal", "ftp.bin", size, id), Dl, null, true);
            WaitEnd(it, 60);
            AssertComplete(it, size);
            Check(Ftp.Count(id + ":retr") >= 2, "Solo se usó " + Ftp.Count(id + ":retr") + " conexión FTP");
        }

        static void T2_FtpResume()
        {
            long size = 2 * 1024 * 1024;
            string id = NewId("ftpr");
            DownloadManager m = NewMgr();
            DownloadItem it = m.Add(Ftp.Url("slow", "ftpr.bin", size, id), Dl, null, true);
            WaitProgress(it, 300 * 1024, 30);
            m.StopItem(it); WaitEnd(it, 10);
            Check(it.Status == DownloadStatus.Paused, "Estado tras pausar: " + it.Status);
            m.StartItem(it);
            WaitEnd(it, 60);
            AssertComplete(it, size);
            Check(Ftp.Count(id + ":rest") >= 1, "No se usó REST para continuar");
        }

        static void T2_FtpMissing()
        {
            Stopwatch sw = Stopwatch.StartNew();
            DownloadManager m = NewMgr();
            DownloadItem it = m.Add(Ftp.Url("missing", "nada.bin", 10, NewId("ftpm")), Dl, null, true);
            WaitEnd(it, 30);
            Check(it.Status == DownloadStatus.Error, "Estado " + it.Status);
            Check(sw.Elapsed.TotalSeconds < 4, "Tardó demasiado: " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
            Check(it.Error != null && it.Error.Contains("550"), "Mensaje: " + it.Error);
        }

        // ------------------------------------------------------------ cola

        static DownloadManager QueueMgr(int maxActive)
        {
            DownloadManager m = NewMgr();
            m.Schedule = new Schedule();
            m.Schedule.MaxActive = maxActive;
            m.QueueRetryDelaySec = 1;
            return m;
        }

        static int RunningCount(DownloadManager m)
        {
            int n = 0;
            foreach (DownloadItem it in m.Snapshot()) if (it.Engine != null && it.Engine.IsRunning) n++;
            return n;
        }

        static bool WaitUntil(Func<bool> cond, int seconds)
        {
            DateTime limit = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < limit) { if (cond()) return true; Thread.Sleep(50); }
            return cond();
        }

        static void T2_QueueOneAtATime()
        {
            DownloadManager m = QueueMgr(1);
            bool finished = false;
            m.QueueFinished += delegate { finished = true; };
            List<DownloadItem> items = new List<DownloadItem>();
            for (int i = 0; i < 3; i++) items.Add(m.Add(S.Url("range,slow", "q" + i + ".bin", 400 * 1024, NewId("q")), Dl, null, false));
            foreach (DownloadItem it in items) Check(it.Queue == DownloadItem.MainQueue, "No entró en la cola principal");
            int maxSeen = 0;
            m.StartQueue();
            bool done = WaitUntil(delegate
            {
                maxSeen = Math.Max(maxSeen, RunningCount(m));
                return finished;
            }, 60);
            Check(done, "La cola no terminó");
            Check(maxSeen == 1, "Hubo " + maxSeen + " descargas a la vez (límite 1)");
            foreach (DownloadItem it in items)
            {
                AssertComplete(it, 400 * 1024);
                Check(it.Queue == DownloadItem.NoQueue, "Una descarga completada sigue en la cola");
            }
            Check(!m.QueueRunning, "La cola sigue marcada como en marcha");
        }

        static void T2_QueueFailedToEnd()
        {
            DownloadManager m = QueueMgr(1);
            string badId = NewId("bad");
            DownloadItem bad = m.Add(S.Url("notfound", "bad.bin", 10, badId), Dl, null, false);
            DownloadItem good1 = m.Add(S.Url("range,slow", "good1.bin", 300 * 1024, NewId("g1")), Dl, null, false);
            DownloadItem good2 = m.Add(S.Url("range,slow", "good2.bin", 300 * 1024, NewId("g2")), Dl, null, false);
            m.StartQueue();
            bool ok = WaitUntil(delegate { return good1.Status == DownloadStatus.Complete && good2.Status == DownloadStatus.Complete; }, 60);
            Check(ok, "El archivo fallido bloqueó la cola (good1=" + good1.Status + ", good2=" + good2.Status + ")");
            Check(bad.QueueOrder > good2.QueueOrder || bad.QueueFailures > 0, "El fallido no pasó al final de la cola");
            bool retried = WaitUntil(delegate { return S.Count(badId + ":req") >= 3; }, 15);
            Check(retried, "El fallido no se reintentó (peticiones: " + S.Count(badId + ":req") + ")");
            Check(m.QueueRunning, "La cola debería seguir reintentando (sin límite de reintentos)");
            m.StopQueue();
        }

        static void T2_QueueRetryLimit()
        {
            DownloadManager m = QueueMgr(1);
            m.Schedule.RetryLimitEnabled = true;
            m.Schedule.RetryLimit = 2;
            bool finished = false;
            m.QueueFinished += delegate { finished = true; };
            string badId = NewId("lim");
            m.Add(S.Url("notfound", "lim.bin", 10, badId), Dl, null, false);
            m.StartQueue();
            Check(WaitUntil(delegate { return finished; }, 30), "La cola no terminó tras agotar los reintentos");
            Check(S.Count(badId + ":req") == 2, "Intentos: " + S.Count(badId + ":req") + " (esperados 2)");
        }

        static void T2_QueueStopOnlyOwn()
        {
            DownloadManager m = QueueMgr(1);
            DownloadItem manual = m.Add(S.Url("range,veryslow", "manual.bin", 3 * 1024 * 1024, NewId("man")), Dl, null, true);
            DownloadItem queued = m.Add(S.Url("range,veryslow", "queued.bin", 3 * 1024 * 1024, NewId("que")), Dl, null, false);
            m.StartQueue();
            Check(WaitUntil(delegate { return queued.Engine != null && queued.Engine.IsRunning; }, 10), "La cola no lanzó su descarga");
            m.StopQueue();
            WaitEnd(queued, 10);
            Check(queued.Status == DownloadStatus.Paused, "La de la cola debería estar pausada: " + queued.Status);
            Check(manual.Engine != null && manual.Engine.IsRunning && !manual.Engine.StopRequested, "Detener la cola paró una descarga manual");
            m.StopItem(manual); WaitEnd(manual, 10);
        }

        static void T2_QueueRespectsUserStop()
        {
            DownloadManager m = QueueMgr(2);
            DownloadItem a = m.Add(S.Url("range,veryslow", "us1.bin", 3 * 1024 * 1024, NewId("us1")), Dl, null, false);
            DownloadItem b = m.Add(S.Url("range,veryslow", "us2.bin", 3 * 1024 * 1024, NewId("us2")), Dl, null, false);
            m.StartQueue();
            Check(WaitUntil(delegate { return RunningCount(m) == 2; }, 10), "No arrancaron las 2");
            m.StopItem(a);
            WaitEnd(a, 10);
            Thread.Sleep(2500); // el temporizador de la cola pasa al menos una vez
            Check(a.Status == DownloadStatus.Paused && (a.Engine == null || !a.Engine.IsRunning), "La cola relanzó una descarga que el usuario detuvo");
            m.StopAll();
            WaitEnd(b, 10);
        }

        static DateTime T(int y, int mo, int d, int h, int mi) { return new DateTime(y, mo, d, h, mi, 0); }

        static void T2_SchedulerDaily()
        {
            Schedule s = new Schedule();
            s.Enabled = true; s.StartHour = 2; s.StartMinute = 0;
            // 2026-10-15 es jueves.
            Check(s.CheckNow(T(2026, 10, 15, 1, 59)) == null, "Disparó antes de hora");
            Check(s.CheckNow(T(2026, 10, 15, 2, 0)) == "start", "No disparó a su hora");
            Check(s.CheckNow(T(2026, 10, 15, 2, 0)) == null, "Disparó dos veces en el mismo minuto");
            Check(s.CheckNow(T(2026, 10, 15, 9, 0)) == null, "Volvió a disparar el mismo día");
            // Recuperación: el PC estuvo suspendido a las 02:00 y despierta a las 02:07.
            Check(s.CheckNow(T(2026, 10, 16, 2, 7)) == "start", "No recuperó el disparo perdido");
            // Días: sábado desmarcado.
            s.Days[6] = false;
            Check(s.CheckNow(T(2026, 10, 17, 2, 0)) == null, "Disparó un sábado desmarcado");
            Check(s.CheckNow(T(2026, 10, 18, 2, 0)) == "start", "No disparó el domingo");
            // Con hora de parada pasada, un inicio perdido ya no se recupera.
            Schedule w = new Schedule(); w.Enabled = true; w.StartHour = 8; w.StopEnabled = true; w.StopHour = 10;
            Check(w.CheckNow(T(2026, 10, 15, 11, 0)) != "start", "Arrancó fuera de la ventana inicio-parada");
        }

        static void T2_SchedulerMidnightAndStop()
        {
            Schedule s = new Schedule();
            s.Enabled = true; s.StartHour = 23; s.StopEnabled = true; s.StopHour = 2;
            s.Days = new bool[7]; s.Days[4] = true; // solo jueves
            Check(s.CheckNow(T(2026, 10, 15, 23, 0)) == "start", "No arrancó el jueves a las 23:00");
            Check(s.CheckNow(T(2026, 10, 15, 23, 30)) == null, "Paró justo después de arrancar");
            Check(s.CheckNow(T(2026, 10, 16, 1, 59)) == null, "Paró antes de hora");
            Check(s.CheckNow(T(2026, 10, 16, 2, 0)) == "stop", "No paró a las 02:00 del viernes (día no marcado)");
            Check(s.CheckNow(T(2026, 10, 16, 2, 1)) == null, "Paró dos veces");

            // "Detener a las" funciona aunque "Iniciar a las" esté desmarcado (como IDM).
            Schedule p = new Schedule(); p.Enabled = false; p.StopEnabled = true; p.StopHour = 18;
            Check(p.CheckNow(T(2026, 10, 15, 17, 59)) == null, "Paró antes");
            Check(p.CheckNow(T(2026, 10, 15, 18, 0)) == "stop", "No paró sin inicio programado");
        }

        static void T2_SchedulerOnceAndDst()
        {
            Schedule s = new Schedule();
            s.Enabled = true; s.Once = true; s.OnceDate = "2026-10-20"; s.StartHour = 9;
            Check(s.CheckNow(T(2026, 10, 19, 9, 0)) == null, "Disparó en otra fecha");
            Check(s.CheckNow(T(2026, 10, 20, 9, 0)) == "start", "No disparó en su fecha");
            Check(s.CheckNow(T(2026, 10, 21, 9, 0)) == null, "Repitió un inicio de una sola vez");

            // Cambio de hora (Europa): el 29-03-2026 las 02:00-03:00 no existen. Con inicio a las 02:30
            // el primer sondeo es a las 03:00 y debe arrancar igualmente.
            Schedule d = new Schedule(); d.Enabled = true; d.StartHour = 2; d.StartMinute = 30;
            Check(d.CheckNow(T(2026, 3, 29, 3, 0)) == "start", "No arrancó el día del cambio de hora");

            // Se guarda y se recarga sin perder el último disparo.
            UseFreshHome("sched");
            s.Save();
            Schedule r = Schedule.Load();
            Check(r.CheckNow(T(2026, 10, 20, 9, 1)) == null, "Tras recargar repitió el disparo");
        }

        static void T2_SpeedSmoothing()
        {
            DownloadManager m = NewMgr();
            DownloadItem it = m.Add(S.Url("range,veryslow", "speed.bin", 16 * 1024 * 1024, NewId("spd")), Dl, null, true);
            List<long> samples = new List<long>();
            // Esperar a que termine de añadir conexiones (4 -> 8): ahí la velocidad sube de verdad.
            for (int i = 0; i < 7; i++) { m.Tick(); Thread.Sleep(500); }
            DateTime limit = DateTime.Now.AddSeconds(4);
            while (DateTime.Now < limit) { m.Tick(); if (it.SpeedBps > 0) samples.Add(it.SpeedBps); Thread.Sleep(500); }
            m.StopItem(it); WaitEnd(it, 10);
            Check(samples.Count >= 4, "Pocas muestras de velocidad: " + samples.Count);
            // La velocidad sube al ir añadiendo conexiones, pero sin saltos bruscos entre medidas seguidas.
            for (int i = 2; i < samples.Count; i++)
            {
                double ratio = samples[i] / (double)samples[i - 1];
                Check(ratio < 1.6 && ratio > 0.6, "La velocidad salta de " + samples[i - 1] + " a " + samples[i] + " B/s en medio segundo");
            }
        }

        static void T2_ExtractUrls()
        {
            string text = "Enlaces:\nhttp://a.com/x.zip, (https://b.com/y.rar) y ftp://c.com/z.iso.\nOtra vez http://a.com/x.zip <http://d.com/w.pdf>";
            List<string> u = Util.ExtractUrls(text);
            Check(u.Count == 4, "Encontradas " + u.Count + ": " + string.Join(" | ", u.ToArray()));
            Check(u.Contains("http://a.com/x.zip") && u.Contains("https://b.com/y.rar") && u.Contains("ftp://c.com/z.iso") && u.Contains("http://d.com/w.pdf"),
                  "URL mal extraídas: " + string.Join(" | ", u.ToArray()));
        }

        // ------------------------------------------------------------ interfaz

        static T Field<T>(object o, string name)
        {
            FieldInfo f = o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            return (T)f.GetValue(o);
        }

        static void DisposeMainForm(MainForm f)
        {
            NotifyIcon tray = Field<NotifyIcon>(f, "_tray");
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            f.Dispose();
        }

        static MainForm NewMainForm()
        {
            UseFreshHome("gui-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            DownloadManager m = NewMgr();
            m.Schedule = new Schedule();
            MainForm f = new MainForm(m, m.Config, m.Schedule);
            f.StartHidden = true;
            IntPtr h = f.Handle;
            f.PerformLayout();
            return f;
        }

        static void T2_MainLayout()
        {
            MainForm f = NewMainForm();
            try
            {
                Control menu = Field<Control>(f, "_menu"), bar = Field<Control>(f, "_toolbar"), tree = Field<Control>(f, "_catPanel"),
                        split = Field<Control>(f, "_treeSplitter"), list = Field<Control>(f, "_list");
                Check(menu.Top == 0, "El menú no está arriba del todo (Top=" + menu.Top + ")");
                Check(bar.Top >= menu.Bottom, "La barra de herramientas está encima del menú");
                Check(tree.Top >= bar.Bottom, "El árbol invade la zona de la barra/menú");
                Check(split.Left >= tree.Right - 1, "El separador no está a la derecha del árbol");
                Check(list.Left >= split.Right - 1 && list.Top >= bar.Bottom, "La lista no está a la derecha del árbol");
                // Como IDM: sin barra de estado; la lista llega hasta abajo.
                Check(list.Bottom == f.ClientSize.Height && tree.Bottom == f.ClientSize.Height, "La lista/el árbol no llegan abajo (hay barra de estado)");
            }
            finally { DisposeMainForm(f); }
        }

        static void CheckNoOverlap(Form f, string name)
        {
            IntPtr h = f.Handle;
            f.PerformLayout();
            CheckChildrenNoOverlap(f, f.ClientRectangle, name);
            // Y dentro de cada pestaña.
            foreach (Control c in f.Controls)
            {
                TabControl tc = c as TabControl;
                if (tc == null) continue;
                // Las pestañas no visibles aún no tienen su tamaño: se usa el área útil del control.
                Rectangle area = new Rectangle(Point.Empty, tc.DisplayRectangle.Size);
                foreach (TabPage p in tc.TabPages) CheckChildrenNoOverlap(p, area, name + " / " + p.Text);
            }
        }

        static void CheckChildrenNoOverlap(Control parent, Rectangle client, string name)
        {
            List<Control> cs = new List<Control>();
            foreach (Control c in parent.Controls) cs.Add(c);
            for (int i = 0; i < cs.Count; i++)
            {
                Rectangle a = cs[i].Bounds;
                Check(client.Contains(a), name + ": «" + cs[i].Text + "» se sale de la ventana");
                for (int j = i + 1; j < cs.Count; j++)
                {
                    Rectangle b = cs[j].Bounds;
                    Rectangle ai = a; ai.Inflate(-1, -1);
                    Check(!ai.IntersectsWith(b), name + ": «" + cs[i].Text + "» (" + cs[i].GetType().Name + ") se superpone a «" + cs[j].Text + "» (" + cs[j].GetType().Name + ")");
                }
            }
        }

        static void T2_DialogsNoOverlap()
        {
            AppConfig c = Cfg();
            using (Form f = new AddUrlForm("http://x/y.zip")) CheckNoOverlap(f, "Agregar URL");
            using (Form f = new OptionsForm(c)) CheckNoOverlap(f, "Opciones");
            using (Form f = new SchedulerForm(new Schedule(), null)) CheckNoOverlap(f, "Programador");
            using (Form f = new DeleteConfirmForm(2)) CheckNoOverlap(f, "Eliminar");
            DownloadItem it = new DownloadItem(); it.FilePath = Path.Combine(Dl, "x.bin"); it.Url = "http://x/x.bin";
            using (Form f = new ProgressForm(it, NewMgr())) CheckNoOverlap(f, "Progreso");
        }

        // "Información del archivo a descargar": categoría y carpeta según el tipo de archivo.
        static void T2_AddUrlCategory()
        {
            AppConfig c = CfgWithCategoryFolders();
            DownloadManager m = new DownloadManager(c);
            AddOptions o = new AddOptions(); o.Url = "http://servidor/carpeta/pelicula.mp4"; o.Pending = true;
            DownloadItem it = m.Add(o);
            using (FileInfoForm f = new FileInfoForm(c, m, it))
            {
                ComboBox cat = Field<ComboBox>(f, "_cat");
                TextBox path = Field<TextBox>(f, "_path");
                Check(cat.SelectedItem != null && cat.SelectedItem.ToString() == "Vídeo", "Categoría: " + cat.SelectedItem);
                Check(path.Text == Path.Combine(Dl, "cat-Vídeo", "pelicula.mp4"), "Ruta: " + path.Text);
                f.Cancel();
            }
        }

        static void T2_SingleProgressWindow()
        {
            MainForm f = NewMainForm();
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                DownloadItem it = m.Add(S.Url("range", "pw.bin", 10, NewId("pw")), Dl, null, false);
                List<DownloadItem> one = new List<DownloadItem>(); one.Add(it);
                f.ShowProgress(one);
                f.ShowProgress(one);
                Application.DoEvents();
                Check(f.OpenProgressWindows == 1, "Se abrieron " + f.OpenProgressWindows + " ventanas de progreso para la misma descarga");
                foreach (Form open in new List<Form>(f.OwnedForms)) open.Close();
                Application.DoEvents();
                Check(f.OpenProgressWindows == 0, "La ventana cerrada sigue registrada");
            }
            finally { DisposeMainForm(f); }
        }

        static void T2_StartupRegistry()
        {
            string saved = Startup.RunKeyPath;
            Startup.RunKeyPath = @"Software\IDMCloneTests\Run";
            try
            {
                string exe = @"C:\ruta de prueba\IDMClone.exe";
                Startup.Set(true, exe);
                Check(Startup.IsEnabled(exe), "No quedó activado");
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Startup.RunKeyPath))
                    Check(((string)k.GetValue(Startup.ValueName)) == "\"" + exe + "\" /onboot", "Valor: " + k.GetValue(Startup.ValueName));
                Startup.Set(false, exe);
                Check(!Startup.IsEnabled(exe), "No se desactivó");
            }
            finally
            {
                Startup.RunKeyPath = saved;
                try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\IDMCloneTests", false); } catch { }
            }
        }

        static void T2_E2E_OnBoot()
        {
            RequireExe();
            UseFreshHome("e2e-onboot");
            Cfg().Save();
            ProcessStartInfo psi = new ProcessStartInfo(ExePath, "/onboot");
            psi.UseShellExecute = false;
            psi.EnvironmentVariables["IDMCLONE_HOME"] = AppConfig.Dir;
            Process p = Process.Start(psi);
            try
            {
                Check(WaitUntil(delegate { string x = Ipc.Request("{\"type\":\"ping\"}", 300, 2000); return x != null && x.Contains("\"gui\":true"); }, 20), "La GUI no arrancó");
                p.Refresh();
                Check(p.MainWindowHandle == IntPtr.Zero, "Con /onboot la ventana se mostró");
                string r = Ipc.Request("/show", 1000, 5000);
                Check(r != null && r.Contains("\"ok\":true"), "Respuesta a /show: " + r);
                Check(WaitUntil(delegate { p.Refresh(); return p.MainWindowHandle != IntPtr.Zero; }, 5), "/show no mostró la ventana");
            }
            finally { try { if (!p.HasExited) p.Kill(); p.WaitForExit(5000); } catch { } }
        }
    }
}
