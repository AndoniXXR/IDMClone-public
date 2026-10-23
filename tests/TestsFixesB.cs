using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;

namespace IDMClone.Tests
{
    // Arreglos de la auditoría (B): errores en los hilos de vídeo, credenciales del Grabber solo para su
    // sitio y descarga por lotes con rangos enormes.
    // OJO: con el código sin arreglar, los tests de vídeo, aviso final y antivirus CIERRAN el proceso de
    // pruebas (excepción sin capturar en un hilo de fondo); los de lotes se agotan por tiempo.
    public static partial class TestMain
    {
        static void RunFixesB()
        {
            Run("arreglo B: VOD HLS con clave data: -> error y el proceso sigue vivo", TB_HlsDataKeyVod);
            Run("arreglo B: directo HLS con clave data: -> error y el proceso sigue vivo", TB_HlsDataKeyLive);
            Run("arreglo B: aviso de fin que falla no cierra el proceso y el gestor sigue", TB_FinishedHandlerThrows);
            Run("arreglo B: comprobacion de virus con aviso que falla no cierra el proceso", TB_VirusDoneThrows);
            Run("arreglo B: crash.log en la carpeta de datos", TB_CrashLog);
            Run("arreglo B: grabber: usuario y clave del proyecto solo para su servidor", TB_GrabberAuthOnlySameHost);
            Run("arreglo B: lotes hasta int.MaxValue terminan", TB_BatchMaxValue);
            Run("arreglo B: lotes con rango desbordado se limitan a 100001", TB_BatchOverflow);
            Run("arreglo B: lotes: casos de siempre sin cambios", TB_BatchUnchanged);
            Run("arreglo C: grabber: IsProjectHost (www, https->http, otro servidor, StartUrl no válida)", TC_GrabberIsProjectHost);
            Run("arreglo C: grabber: NewRequest pone las credenciales del proyecto solo para su servidor", TC_GrabberNewRequestCreds);
            Run("arreglo C: quitar una descarga de una cola en marcha no la relanza", TC_RemoveFromRunningQueue);
            Run("arreglo C: quitar la activa de una cola (1 a la vez) -> la cola sigue con la siguiente", TC_RemoveActiveQueueContinues);
            Run("arreglo C: quitar no borra un archivo que es de otra descarga o que iba a sustituirse", TC_RemoveKeepsOthersFile);
            Run("arreglo C: Hold/Release paran y reanudan (o no) la descarga", TC_HoldRelease);
            Run("arreglo C: SaveState guarda aunque el archivo de trabajo esté bloqueado", TC_SaveStateLockedWorkFile);
        }

        // ------------------------------------------------------------------ vídeo (5)

        const string DataKeyLine = "#EXT-X-KEY:METHOD=AES-128,URI=\"data:text/plain;base64,AAECAwQFBgcICQoLDA0ODw==\"\n";

        static void TB_HlsDataKeyVod()
        {
            string dir = "hls-datakey-" + NewId("v");
            for (int i = 0; i < 3; i++) S.Put(dir + "/seg" + i + ".ts", MakeTsSegment(i), "video/mp2t");
            string url = S.Put(dir + "/v.m3u8", HlsMedia(3, "seg{0}.ts", DataKeyLine), "application/vnd.apple.mpegurl");
            DownloadManager m = NewMgr();
            DownloadItem it = AddMedia(m, url, "hls", "clave-data.mp4", null, null, true);
            WaitEnd(it, 30);
            Thread.Sleep(300); // por si algún hilo de trabajo aún fuera a fallar
            Check(it.Status == DownloadStatus.Error, "Estado: " + it.Status + " " + it.Error);
            Check(!string.IsNullOrEmpty(it.Error), "Sin mensaje de error");
            Check(it.Engine == null || !it.Engine.IsRunning, "El motor sigue en marcha");
            Check(!File.Exists(it.FilePath) || new FileInfo(it.FilePath).Length == 0, "Quedó un archivo de vídeo con datos");
        }

        static void TB_HlsDataKeyLive()
        {
            // Sin EXT-X-ENDLIST: es una emisión en directo.
            string dir = "live-datakey-" + NewId("l");
            for (int i = 0; i < 3; i++) S.Put(dir + "/seg" + i + ".ts", MakeTsSegment(i), "video/mp2t");
            StringBuilder sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n");
            sb.Append(DataKeyLine);
            for (int i = 0; i < 3; i++) sb.Append("#EXTINF:2.000,\nseg").Append(i).Append(".ts\n");
            string url = S.Put(dir + "/live.m3u8", sb.ToString(), "application/vnd.apple.mpegurl");
            DownloadManager m = NewMgr();
            DownloadItem it = AddMedia(m, url, "hls", "directo-clave-data.mp4", null, null, true);
            WaitEnd(it, 30);
            Thread.Sleep(300);
            Check(it.Status == DownloadStatus.Error, "Estado: " + it.Status + " " + it.Error);
            Check(!string.IsNullOrEmpty(it.Error) && it.Error.Contains("No se grabó nada"), "Error: " + it.Error);
            // El motivo (el error definitivo) va en el mensaje, no solo "No se grabó nada...".
            Check(it.Error.Length > "No se grabó nada de la emisión en directo.".Length, "Falta el motivo: " + it.Error);
        }

        // DownloadEngine.Run: un error en los avisos de fin (Finished -> ItemFinished) no tumba el proceso.
        static void TB_FinishedHandlerThrows()
        {
            DownloadManager m = NewMgr();
            int calls = 0;
            m.ItemFinished += delegate(DownloadItem x) { Interlocked.Increment(ref calls); throw new InvalidOperationException("prueba: aviso que falla"); };
            DownloadItem it = Download(m, "range", 64 * 1024, "fin-falla", 30);
            Thread.Sleep(500);
            AssertComplete(it, 64 * 1024);
            Check(calls == 1, "Avisos: " + calls);
            DownloadItem it2 = Download(m, "range", 32 * 1024, "fin-falla2", 30);
            Thread.Sleep(300);
            AssertComplete(it2, 32 * 1024);
            Check(calls == 2, "Avisos tras la segunda: " + calls);
        }

        static void TB_VirusDoneThrows()
        {
            Func<ProcessStartInfo, Process> saved = VirusCheck.Starter;
            try
            {
                VirusCheck.Starter = delegate(ProcessStartInfo psi) { return (Process)null; };
                AppConfig c = Cfg();
                string f = Path.Combine(Dl, "virus-" + NewId("v") + ".bin");
                File.WriteAllText(f, "x");
                c.AntivirusPath = f;
                ManualResetEvent called = new ManualResetEvent(false);
                Check(VirusCheck.Scan(c, f, delegate(int code) { called.Set(); throw new InvalidOperationException("prueba: aviso del antivirus que falla"); }), "No se lanzó la comprobación");
                Check(called.WaitOne(5000), "No se llamó al aviso");
                Thread.Sleep(1500); // sin el arreglo, el proceso se cierra en este intervalo
            }
            finally { VirusCheck.Starter = saved; }
        }

        static void TB_CrashLog()
        {
            UseFreshHome("crashlog-" + NewId("c"));
            MethodInfo w = typeof(IDMClone.Program).GetMethod("WriteCrashLog", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Check(w != null, "No existe Program.WriteCrashLog");
            Exception ex;
            try { throw new InvalidOperationException("fallo de prueba ñ"); } catch (Exception e) { ex = e; }
            w.Invoke(null, new object[] { ex });
            w.Invoke(null, new object[] { null });
            string log = Path.Combine(AppConfig.Dir, "crash.log");
            Check(File.Exists(log), "No se escribió " + log);
            string text = File.ReadAllText(log, Encoding.UTF8);
            Check(text.Contains("InvalidOperationException") && text.Contains("fallo de prueba ñ") && text.Contains("TB_CrashLog"), "Contenido: " + text);
            Check(text.Contains("(error desconocido)"), "Sin excepción: " + text);
        }

        // ------------------------------------------------------------------ Grabber (6)

        static void TB_GrabberAuthOnlySameHost()
        {
            string idOk = NewId("gauth-ok"), idOther = NewId("gauth-otro"), idRedir = NewId("gauth-redir");
            string own = S.Url("range,basicauth", "propia.png", 1234, idOk);
            string other = "http://localhost:" + S.Port + "/f/range,basicauth/ajena.png?size=2222&id=" + idOther;
            // 127.0.0.1 que redirige (302) a localhost: las credenciales tampoco deben seguir la redirección.
            string redir = S.Base + "/r/range,basicauth/redirigida.png?size=3333&id=" + idRedir;
            string dir = "gauth-" + NewId("s");
            string start = S.Put(dir + "/index.html", "<!doctype html><html><body><img src=\"" + own + "\"> <img src=\"" + other + "\"> <img src=\"" + redir + "\"></body></html>", "text/html");
            GrabberProject p = GrabberProject.CreateDefault();
            p.StartUrl = start;
            p.ApplyTemplate(1, Cfg()); // todas las imágenes
            p.ThisSiteOnly = false; p.LevelsOther = 0; p.HideDuplicates = false; p.MinKB = 0; p.MaxKB = 0;
            p.UseAuth = true; p.User = "usuario"; p.Password = "clave";
            Explore(p, null);
            GrabberFile fOwn = p.Files.Find(delegate(GrabberFile f) { return f.Name == "propia.png"; });
            GrabberFile fOther = p.Files.Find(delegate(GrabberFile f) { return f.Name == "ajena.png"; });
            Check(fOwn != null && fOther != null, "Archivos: " + Show(Names(p)));
            Check(S.Count(idOther + ":req") >= 1, "No se pidió la imagen de localhost");
            Check(S.Count(idOther + ":authleak") == 0, "Usuario y contraseña enviados a localhost " + S.Count(idOther + ":authleak") + " veces");
            Check(S.Count(idRedir + ":authleak") == 0, "Usuario y contraseña enviados a localhost tras la redirección " + S.Count(idRedir + ":authleak") + " veces");
            Check(fOther.Size == -1, "La de localhost no debería autenticarse: tamaño " + fOther.Size);
            Check(fOwn.Size == 1234, "La del propio sitio debería autenticarse (tamaño conocido): " + fOwn.Size);
        }

        // ------------------------------------------------------------------ lotes (7)

        // ExpandBatch en otro hilo: sin el arreglo no termina nunca (y se come la memoria).
        static List<string> BatchOrTimeout(string pattern, string from, string to, int width)
        {
            List<string> r = null; Exception err = null;
            Thread t = new Thread(delegate() { try { r = Util.ExpandBatch(pattern, from, to, width); } catch (Exception ex) { err = ex; } });
            t.IsBackground = true;
            t.Start();
            if (!t.Join(3000))
            {
                try { t.Abort(); } catch { }
                t.Join(3000);
                r = null;
                GC.Collect();
                throw new Exception("ExpandBatch(\"" + from + "\", \"" + to + "\") no terminó en 3 s");
            }
            if (err != null) throw new Exception("ExpandBatch(\"" + from + "\", \"" + to + "\"): " + err.GetType().Name + ": " + err.Message);
            return r;
        }

        static void TB_BatchMaxValue()
        {
            List<string> l = BatchOrTimeout("f*.zip", "2147483640", "2147483647", 1);
            Check(l.Count == 8 && l[0] == "f2147483640.zip" && l[7] == "f2147483647.zip", "Hasta int.MaxValue: " + l.Count);
            l = BatchOrTimeout("f*.zip", "2147483647", "2147483640", 1);
            Check(l.Count == 8 && l[0] == "f2147483640.zip" && l[7] == "f2147483647.zip", "Al revés: " + l.Count);
            l = BatchOrTimeout("f*.zip", "2147483647", "2147483647", 1);
            Check(l.Count == 1 && l[0] == "f2147483647.zip", "Solo int.MaxValue: " + l.Count);
        }

        static void TB_BatchOverflow()
        {
            List<string> l = BatchOrTimeout("f*.zip", "-2000000000", "2000000000", 1);
            Check(l.Count == 100001 && l[0] == "f-2000000000.zip" && l[100000] == "f-1999900000.zip", "-2000000000..2000000000: " + l.Count);
            l = BatchOrTimeout("f*.zip", "-2147483648", "2147483647", 1);
            Check(l.Count == 100001 && l[0] == "f-2147483648.zip", "int.MinValue..int.MaxValue: " + l.Count);
            l = BatchOrTimeout("f*.zip", "0", "2147483647", 1);
            Check(l.Count == 100001 && l[0] == "f0.zip" && l[100000] == "f100000.zip", "0..int.MaxValue: " + l.Count);
            l = BatchOrTimeout("f*.zip", "-2147483648", "-2147483641", 1);
            Check(l.Count == 8 && l[0] == "f-2147483648.zip" && l[7] == "f-2147483641.zip", "Junto a int.MinValue: " + l.Count);
        }

        static void TB_BatchUnchanged()
        {
            List<string> n = BatchOrTimeout("http://s.com/foto*.jpg", "8", "11", 3);
            Check(n.Count == 4 && n[0] == "http://s.com/foto008.jpg" && n[3] == "http://s.com/foto011.jpg", "Números: " + string.Join(" ", n.ToArray()));
            List<string> l = BatchOrTimeout("http://s.com/parte-*.rar", "a", "c", 1);
            Check(l.Count == 3 && l[0] == "http://s.com/parte-a.rar" && l[2] == "http://s.com/parte-c.rar", "Letras: " + string.Join(" ", l.ToArray()));
            Check(BatchOrTimeout("http://s.com/sin-comodin.zip", "1", "3", 1).Count == 0, "Sin * no debe generar nada");
            l = BatchOrTimeout("http://s.com/x*.zip", "5", "3", 1);
            Check(l.Count == 3 && l[0] == "http://s.com/x3.zip" && l[2] == "http://s.com/x5.zip", "Rango al revés: " + string.Join(" ", l.ToArray()));
            l = BatchOrTimeout("x*.zip", "1", "100001", 1);
            Check(l.Count == 100001 && l[100000] == "x100001.zip", "Justo el tope: " + l.Count);
            l = BatchOrTimeout("x*.zip", "1", "100002", 1);
            Check(l.Count == 100001 && l[100000] == "x100001.zip", "Por encima del tope: " + l.Count);
            l = BatchOrTimeout("x*.zip", "-3", "-1", 2);
            Check(l.Count == 3 && l[0] == "x-03.zip" && l[2] == "x-01.zip", "Negativos con ceros: " + string.Join(" ", l.ToArray()));
            l = BatchOrTimeout("x*.zip", "7", "7", 0);
            Check(l.Count == 1 && l[0] == "x7.zip", "Un solo número (ancho 0): " + string.Join(" ", l.ToArray()));
            Check(BatchOrTimeout("x*.zip", "1", "z", 1).Count == 0 && BatchOrTimeout("x*.zip", "", "3", 1).Count == 0, "Rango no válido");
            Check(BatchOrTimeout("a*b*.zip", "1", "2", 1)[1] == "a2b2.zip", "Varios *");
        }

        // ------------------------------------------------------------------ revisión (C)

        static GrabberEngine TC_Grabber(string start)
        {
            GrabberProject p = GrabberProject.CreateDefault();
            p.StartUrl = start;
            return new GrabberEngine(p, Cfg());
        }

        static bool TC_ProjectHost(string start, string url)
        {
            MethodInfo mi = typeof(GrabberEngine).GetMethod("IsProjectHost", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(mi != null, "No existe GrabberEngine.IsProjectHost");
            return (bool)mi.Invoke(TC_Grabber(start), new object[] { url == null ? null : new Uri(url) });
        }

        static void TC_GrabberIsProjectHost()
        {
            List<string> bad = new List<string>();
            // { inicio, petición, esperado }
            string[,] cases = {
                { "https://ejemplo.com/galeria/", "https://ejemplo.com/a.png", "1" },
                { "https://ejemplo.com/galeria/", "https://www.ejemplo.com/a.png", "1" },
                { "https://www.ejemplo.com/", "https://ejemplo.com/a.png", "1" },
                { "https://WWW.Ejemplo.COM/", "https://ejemplo.com/a.png", "1" },
                { "http://ejemplo.com/", "https://ejemplo.com/a.png", "1" },          // de http a https: sí
                { "http://ejemplo.com/", "http://www.ejemplo.com/a.png", "1" },
                { "https://ejemplo.com/", "http://ejemplo.com/a.png", "0" },          // de https a http: nunca
                { "https://www.ejemplo.com/", "http://ejemplo.com/a.png", "0" },
                { "https://ejemplo.com/", "https://otro.com/a.png", "0" },
                { "https://ejemplo.com/", "https://cdn.ejemplo.com/a.png", "0" },     // subdominio: otro servidor
                { "https://ejemplo.com/", "https://ejemplo.com.otro.net/a.png", "0" },
                { "https://www.ejemplo.com/", "https://wwwejemplo.com/a.png", "0" },
                { "esto no es una url", "https://ejemplo.com/a.png", "0" },
                { "", "https://ejemplo.com/a.png", "0" },
                { "/relativa/index.html", "https://ejemplo.com/a.png", "0" },
            };
            for (int i = 0; i < cases.GetLength(0); i++)
            {
                bool want = cases[i, 2] == "1";
                if (TC_ProjectHost(cases[i, 0], cases[i, 1]) != want) bad.Add(cases[i, 0] + " -> " + cases[i, 1] + " debería ser " + want);
            }
            if (TC_ProjectHost(null, "https://ejemplo.com/a.png")) bad.Add("StartUrl null -> true");
            if (TC_ProjectHost("https://ejemplo.com/", null)) bad.Add("petición null -> true");
            Check(bad.Count == 0, string.Join("; ", bad.ToArray()));
        }

        static void TC_GrabberNewRequestCreds()
        {
            MethodInfo nr = typeof(GrabberEngine).GetMethod("NewRequest", BindingFlags.Instance | BindingFlags.NonPublic, null, new Type[] { typeof(string), typeof(string) }, null);
            Check(nr != null, "No existe GrabberEngine.NewRequest(string, string)");
            GrabberEngine e = TC_Grabber("https://www.ejemplo.com/fotos/");
            e.P.UseAuth = true; e.P.User = "usuario"; e.P.Password = "clave";
            Func<string, NetworkCredential> cred = delegate(string url)
            {
                HttpWebRequest r = (HttpWebRequest)nr.Invoke(e, new object[] { url, null });
                return r.Credentials as NetworkCredential;
            };
            NetworkCredential c = cred("https://ejemplo.com/fotos/a.jpg");
            Check(c != null && c.UserName == "usuario" && c.Password == "clave", "Sin credenciales para el propio sitio (sin www)");
            c = cred("https://www.ejemplo.com/b.jpg");
            Check(c != null && c.UserName == "usuario", "Sin credenciales para el propio sitio (con www)");
            Check(cred("http://www.ejemplo.com/b.jpg") == null, "Credenciales por http con el proyecto en https");
            Check(cred("https://otro.com/b.jpg") == null, "Credenciales para otro servidor");
            // Sin "usar autorización", ni para el propio sitio.
            e.P.UseAuth = false;
            Check(cred("https://ejemplo.com/fotos/a.jpg") == null, "Credenciales sin UseAuth");
            // Usuario vacío: tampoco.
            e.P.UseAuth = true; e.P.User = "";
            Check(cred("https://ejemplo.com/fotos/a.jpg") == null, "Credenciales con usuario vacío");
            // Las de la dirección (usuario:clave@) se siguen usando para cualquier servidor.
            e.P.User = "usuario";
            c = cred("https://pepe:secreto@otro.com/b.jpg");
            Check(c != null && c.UserName == "pepe" && c.Password == "secreto", "Se perdieron las credenciales de la propia dirección");
        }

        // Cola con 2 a la vez y una conexión por descarga: lentas para poder quitarlas a mitad.
        static DownloadManager TC_QueueMgr(int maxActive)
        {
            DownloadManager m = NewMgr();
            m.Config.MaxConnections = 1;
            m.Schedule = new Schedule();
            m.Schedule.MaxActive = maxActive;
            m.QueueRetryDelaySec = 1;
            return m;
        }

        static bool TC_Running(DownloadItem it) { DownloadEngine e = it.Engine; return e != null && e.IsRunning; }

        static void TC_RemoveFromRunningQueue()
        {
            DownloadManager m = TC_QueueMgr(2);
            try
            {
                long size = 4L * 1024 * 1024;
                string idA = NewId("cq-a"), idB = NewId("cq-b");
                DownloadItem a = m.Add(S.Url("range,veryslow", "quitar-a.bin", size, idA), Dl, null, false);
                DownloadItem b = m.Add(S.Url("range,veryslow", "quitar-b.bin", size, idB), Dl, null, false);
                Check(a.Queue == DownloadItem.MainQueue && b.Queue == DownloadItem.MainQueue, "No entraron en la cola principal");
                m.StartQueue();
                WaitProgress(a, 64 * 1024, 30);
                Check(a.StartedByQueue, "La cola no lanzó la descarga");
                string path = a.FilePath;
                m.Remove(a, true);
                Check(!TC_Running(a), "El motor sigue en marcha justo después de quitarla");
                int reqs = S.Count(idA + ":req");
                // Más de una pasada del temporizador de la cola (cada 2 s).
                bool relaunched = WaitUntil(delegate { return TC_Running(a); }, 5);
                Check(!relaunched, "La cola relanzó la descarga quitada");
                Check(S.Count(idA + ":req") == reqs, "Se volvió a pedir al servidor tras quitarla: " + reqs + " -> " + S.Count(idA + ":req"));
                Check(!m.Snapshot().Contains(a), "Sigue en la lista");
                Check(!File.Exists(path), "Quedó el archivo parcial " + path);
                Check(!File.Exists(a.StatePath), "Quedó el estado guardado");
                Check(TC_Running(b) || b.Status == DownloadStatus.Complete, "La otra descarga de la cola se paró: " + b.Status);
                Check(m.QueueRunning, "La cola dejó de estar en marcha");
            }
            finally { m.StopAllAndWait(10000); }
        }

        static void TC_RemoveActiveQueueContinues()
        {
            DownloadManager m = TC_QueueMgr(1);
            try
            {
                long size = 4L * 1024 * 1024;
                DownloadItem a = m.Add(S.Url("range,veryslow", "cola1-a.bin", size, NewId("c1-a")), Dl, null, false);
                DownloadItem b = m.Add(S.Url("range,veryslow", "cola1-b.bin", size, NewId("c1-b")), Dl, null, false);
                m.StartQueue();
                WaitProgress(a, 64 * 1024, 30);
                Check(!TC_Running(b), "Con 1 a la vez la segunda ya estaba en marcha");
                m.Remove(a, true);
                Check(WaitUntil(delegate { return TC_Running(b); }, 5), "La cola no siguió con la siguiente tras quitar la activa: " + b.Status);
                Check(b.StartedByQueue, "La siguiente no la lanzó la cola");
                Check(!TC_Running(a) && !m.Snapshot().Contains(a), "La quitada sigue bajando o en la lista");
                WaitProgress(b, 32 * 1024, 30);
            }
            finally { m.StopAllAndWait(10000); }
        }

        static void TC_RemoveKeepsOthersFile()
        {
            DownloadManager m = NewMgr();
            DownloadItem a = Download(m, "range", 64 * 1024, "c-propio", 30);
            AssertComplete(a, 64 * 1024);
            string path = a.FilePath;

            // Otra descarga de la lista apunta al mismo archivo ("Sobrescribir" aún sin confirmar del todo).
            AddOptions o = new AddOptions(); o.Url = S.Url("range", "c-propio.bin", 64 * 1024, NewId("c-sobre")); o.OverwritePath = path; o.Queue = DownloadItem.NoQueue;
            DownloadItem b = m.Add(o);
            Check(b.FilePath == path && b.Overwrite, "OverwritePath no se respetó: " + b.FilePath);
            Check(a.FilePath == path, "La primera cambió de ruta");
            m.Remove(b, true);
            Check(File.Exists(path) && FileSha(path) == TestServer.Sha256Of(64 * 1024), "Se borró el archivo de la otra descarga");

            // "Sobrescribir" pendiente del diálogo que se cancela: el archivo que iba a sustituir se queda.
            m.Remove(a, false);
            Check(File.Exists(path), "Quitar sin borrar borró el archivo");
            o = new AddOptions(); o.Url = S.Url("range", "c-propio.bin", 64 * 1024, NewId("c-pend")); o.OverwritePath = path; o.Pending = true; o.Queue = DownloadItem.NoQueue;
            DownloadItem c = m.Add(o);
            m.Discard(c);
            Check(File.Exists(path) && FileSha(path) == TestServer.Sha256Of(64 * 1024), "Cancelar el diálogo borró el archivo que iba a sustituir");
            Check(!m.Snapshot().Contains(c), "La cancelada sigue en la lista");

            // Control: sin nadie más usándolo, "quitar y borrar el archivo" sí lo borra.
            DownloadItem d = Download(m, "range", 32 * 1024, "c-borrar", 30);
            AssertComplete(d, 32 * 1024);
            m.Remove(d, true);
            Check(!File.Exists(d.FilePath), "No se borró el archivo propio");
        }

        static void TC_HoldRelease()
        {
            DownloadManager m = NewMgr();
            m.Config.MaxConnections = 1;
            try
            {
                DownloadItem it = m.Add(S.Url("range,veryslow", "retener.bin", 4L * 1024 * 1024, NewId("hold")), Dl, null, true);
                WaitProgress(it, 32 * 1024, 30);
                Check(m.Hold(it), "Hold de una descarga en marcha debe devolver true");
                Check(!TC_Running(it), "Hold no la paró");
                Check(it.UserStopped, "Hold no la marca como detenida por el usuario");
                long got = it.Downloaded;
                m.Release(it, false, true, false);
                Check(WaitUntil(delegate { return TC_Running(it); }, 5), "Release no la reanudó");
                Check(!it.UserStopped, "Sigue marcada como detenida");
                WaitProgress(it, got + 16 * 1024, 30);

                Check(m.Hold(it), "Segundo Hold");
                m.Release(it, false, false, false); // no estaba en marcha: se queda parada
                Thread.Sleep(1000);
                Check(!TC_Running(it), "Release la relanzó aunque no estaba en marcha");
                Check(!it.UserStopped, "Release no restauró UserStopped");
                Check(!m.Hold(it), "Hold de una descarga parada debe devolver false");
                m.Release(it, true, false, false);
                Check(it.UserStopped && !TC_Running(it), "Release no dejó UserStopped como estaba");
            }
            finally { m.StopAllAndWait(10000); }
        }

        static void TC_SaveStateLockedWorkFile()
        {
            UseFreshHome("savestate-" + NewId("s"));
            Directory.CreateDirectory(AppConfig.StateDir);
            DownloadItem it = new DownloadItem();
            it.Id = Util.NewId(); it.Url = "http://ejemplo.com/x.bin";
            it.FilePath = Path.Combine(Dl, "bloqueado-" + it.Id + ".bin");
            it.Segments.Add(new Segment(0, 10, 100));
            File.WriteAllBytes(it.FilePath, new byte[100]);
            try
            {
                // Otro programa lo tiene abierto sin compartir: FlushToDisk no puede abrirlo, pero el estado se guarda.
                using (new FileStream(it.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    it.SaveState();
                Check(File.Exists(it.StatePath), "No se guardó el estado");
                Check(ReadShared(it.StatePath).Contains("\"Current\":10"), "Estado: " + ReadShared(it.StatePath));
                // Sin archivo de trabajo (aún no creado), también.
                File.Delete(it.FilePath);
                it.Segments[0].Current = 20;
                it.SaveState();
                Check(ReadShared(it.StatePath).Contains("\"Current\":20"), "Estado sin archivo: " + ReadShared(it.StatePath));
            }
            finally { it.DeleteState(); try { File.Delete(it.FilePath); } catch { } }
        }
    }
}
