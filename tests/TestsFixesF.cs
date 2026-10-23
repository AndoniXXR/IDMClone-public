using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace IDMClone.Tests
{
    // Arreglos F: motor más parecido a IDM 6.43 (medido): 4 conexiones de entrada, reconexión inmediata
    // tras un corte con datos y reintentos breves ante un 429/503 momentáneo antes de retirar la conexión.
    public static partial class TestMain
    {
        static void RunFixesF()
        {
            Run("arreglo F: cortes -> reconecta al momento (sin esperar RetryDelaySec)", TF2_CutsReconnectNow);
            Run("arreglo F: descarga nueva abre 4 conexiones enseguida", TF2_FourConnectionsAtStart);
            Run("arreglo F: servidor de 2 conexiones (429) mantiene 2 casi todo el tiempo", TF2_Limit2KeepsTwo);
            Run("arreglo F: servidor que corta todas las respuestas (con rangos) completa sin peticiones de más", TF2_CutAllRanged);
            Run("arreglo F: servidor sin rangos que corta todas las respuestas no reconecta en bucle", TF2_CutAllNoRangeNoTightLoop);
        }

        // Con RetryDelaySec = 5 (el valor por defecto), el código anterior esperaba 5 s tras cada corte.
        static void TF2_CutsReconnectNow()
        {
            AppConfig c = Cfg();
            c.RetryDelaySec = 5;
            DownloadManager m = new DownloadManager(c);
            long size = 8L * 1024 * 1024;
            string id = NewId("fcortes");
            Stopwatch sw = Stopwatch.StartNew();
            DownloadItem it = m.Add(S.Url("range,cut", "cortes.bin", size, id), Dl, null, true);
            WaitEnd(it, 60);
            double t = sw.Elapsed.TotalSeconds;
            AssertComplete(it, size);
            int cuts = S.Count(id + ":cutcount");
            Check(cuts >= 2, "El servidor no llegó a cortar dos veces (la prueba no probó nada): " + cuts);
            Check(t < 4, "Tardó " + t.ToString("0.0") + " s con " + cuts + " respuestas (debería reconectar al momento)");
            Console.WriteLine("        (cortes: " + t.ToString("0.00") + " s, " + S.Count(id + ":req") + " peticiones)");
        }

        // IDM abre 4 conexiones casi a la vez. Antes: 1 -> 2 (a 1,2 s) -> 4 (a ~1,8 s o más).
        static void TF2_FourConnectionsAtStart()
        {
            DownloadManager m = NewMgr();
            long size = 8L * 1024 * 1024;
            string id = NewId("f4conex");
            DownloadItem it = m.Add(S.Url("range,slow", "cuatro.bin", size, id), Dl, null, true);
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                double tFirst = -1, tFour = -1;
                int maxActive = 0;
                while (sw.Elapsed.TotalSeconds < 10)
                {
                    double now = sw.Elapsed.TotalSeconds;
                    if (tFirst < 0 && it.Downloaded > 0) tFirst = now;
                    maxActive = Math.Max(maxActive, it.ActiveConnections);
                    if (tFirst >= 0 && S.Count(id + ":maxconc") >= 4) { tFour = now; break; }
                    if (it.Status == DownloadStatus.Complete || it.Status == DownloadStatus.Error) break;
                    Thread.Sleep(10);
                }
                Check(tFirst >= 0, "No llegaron datos: " + it.Status + " " + it.Error);
                Check(tFour >= 0, "Nunca hubo 4 conexiones recibiendo a la vez (máx. servidor " + S.Count(id + ":maxconc") + ", activas " + maxActive + ")");
                double d = tFour - tFirst;
                Console.WriteLine("        (4 conexiones a los " + d.ToString("0.00") + " s del primer byte; " + tFour.ToString("0.00") + " s desde Add)");
                Check(d < 1.5, "4 conexiones a los " + d.ToString("0.00") + " s del primer byte (se esperaba ~1 s)");
            }
            finally
            {
                m.StopItem(it);
                try { WaitEnd(it, 10); } catch { }
                m.Remove(it, true);
            }
        }

        // Como Wikimedia: más de 2 conexiones -> 429. Con una sola conexión: 16 KB / 30 ms ~ 530 KB/s -> 8 MB en ~15,8 s.
        static void TF2_Limit2KeepsTwo()
        {
            DownloadManager m = NewMgr();
            long size = 8L * 1024 * 1024;
            string id = NewId("flimit2");
            Stopwatch sw = Stopwatch.StartNew();
            DownloadItem it = m.Add(S.Url("range,limit2,slow", "dos.bin", size, id), Dl, null, true);
            int samples = 0, two = 0;
            DateTime limit = DateTime.Now.AddSeconds(90);
            while (DateTime.Now < limit)
            {
                bool running = it.Engine != null && it.Engine.IsRunning;
                if (!running && (it.Status == DownloadStatus.Complete || it.Status == DownloadStatus.Error || it.Status == DownloadStatus.Paused)) break;
                if (it.Downloaded > 0)
                {
                    samples++;
                    if (S.Count(id + ":conc") >= 2) two++;
                }
                Thread.Sleep(100);
            }
            double t = sw.Elapsed.TotalSeconds;
            AssertComplete(it, size);
            int n429 = S.Count(id + ":429"), maxc = S.Count(id + ":maxconc");
            double frac = samples > 0 ? two / (double)samples : 0;
            double single = size / (16.0 * 1024 / 0.030);
            Console.WriteLine("        (" + t.ToString("0.0") + " s, una conexión ~" + single.ToString("0.0") + " s; 429: " + n429 +
                              "; 2 conexiones el " + (frac * 100).ToString("0") + " % del tiempo)");
            Check(n429 >= 1, "El servidor no llegó a limitar (la prueba no probó nada)");
            Check(n429 < 40, "Demasiadas respuestas 429: " + n429);
            Check(maxc == 2, "Conexiones simultáneas máximas en el servidor: " + maxc);
            Check(frac >= 0.6, "Solo hubo 2 conexiones el " + (frac * 100).ToString("0") + " % del tiempo");
            Check(t < single * 0.8, "Tardó " + t.ToString("0.0") + " s; con una sola conexión serían ~" + single.ToString("0.0") + " s");
        }

        // Cada respuesta trae 8 KB y se corta: con rangos se avanza en cada intento, así que debe completar
        // y sin peticiones que no traigan nada (bucle sin progreso).
        static void TF2_CutAllRanged()
        {
            DownloadManager m = NewMgr();
            long size = 512 * 1024;
            string id = NewId("fcutall");
            Stopwatch sw = Stopwatch.StartNew();
            DownloadItem it = m.Add(S.Url("range,cutall", "cortatodo.bin", size, id), Dl, null, true);
            WaitEnd(it, 60);
            double t = sw.Elapsed.TotalSeconds;
            AssertComplete(it, size);
            int req = S.Count(id + ":req");
            int minimum = (int)(size / 8192);
            Console.WriteLine("        (" + req + " peticiones en " + t.ToString("0.00") + " s; mínimo " + minimum + ")");
            Check(req <= minimum * 3 / 2 + 10, "Demasiadas peticiones: " + req + " (mínimo " + minimum + ")");
        }

        // Sin rangos cada intento empieza de 0: si el servidor corta siempre tras 8 KB, "hubo datos" no es
        // progreso real. No puede convertirse en un bucle de peticiones sin pausa.
        static void TF2_CutAllNoRangeNoTightLoop()
        {
            DownloadManager m = NewMgr(); // RetryDelaySec = 1
            long size = 256 * 1024;
            string id = NewId("fcutallnr");
            DownloadItem it = m.Add(S.Url("norange,cutall", "sinrangos-cortatodo.bin", size, id), Dl, null, true);
            int req;
            double secs = 3;
            try
            {
                Thread.Sleep((int)(secs * 1000));
                req = S.Count(id + ":req");
            }
            finally
            {
                m.StopItem(it);
                try { WaitEnd(it, 10); } catch { }
                m.Remove(it, true);
            }
            Console.WriteLine("        (" + req + " peticiones en " + secs + " s; estado " + it.Status + ")");
            Check(S.Count(id + ":cutcount") >= 1, "El servidor no llegó a cortar (la prueba no probó nada)");
            Check(req / secs <= 5, "Bucle de reconexión: " + req + " peticiones en " + secs + " s");
        }
    }
}
