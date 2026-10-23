using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace IDMClone.Tests
{
    // Arreglos G: revisión del reparto a ciegas en 4 conexiones, la reconexión inmediata y el tope tras 429.
    // Todo con SHA-256: las juntas entre trozos no pueden quedar con bytes equivocados.
    public static partial class TestMain
    {
        static void RunFixesG()
        {
            Run("arreglo G: tamaños pequeños e impares con range,slow (SHA)", TG_OddSizesSlow);
            Run("arreglo G: tamaños impares con range rápido (SHA)", TG_OddSizesFast);
            Run("arreglo G: cortes con rangos y reparto a ciegas (SHA)", TG_CutsSha);
            Run("arreglo G: parar justo tras el primer byte y reanudar (SHA)", TG_StopEarlyResume);
            Run("arreglo G: parar a mitad dos veces y reanudar (SHA)", TG_StopMidResume);
            Run("arreglo G: limit2 con 32 conexiones configuradas", TG_Limit2With32);
            Run("arreglo G: servidor limitado por conexión pasa de 4 conexiones", TG_PerConnThrottleGrows);
        }

        static void TG_OddSizesSlow()
        {
            long[] sizes = { 1, 131071, 524287, 524288, 524289, 1048579, 3L * 1048576 + 7, 5L * 1048576 + 1 };
            foreach (long size in sizes)
            {
                DownloadItem it = Download(NewMgr(), "range,slow", size, "godd" + size, 60);
                try { AssertComplete(it, size); }
                catch (Exception ex) { throw new Exception("tamaño " + size + ": " + ex.Message); }
            }
        }

        static void TG_OddSizesFast()
        {
            long[] sizes = { 2, 65537, 262145, 524289, 2L * 1048576 + 3, 9L * 1048576 + 11 };
            foreach (long size in sizes)
            {
                DownloadItem it = Download(NewMgr(), "range", size, "goddf" + size, 60);
                try { AssertComplete(it, size); }
                catch (Exception ex) { throw new Exception("tamaño " + size + ": " + ex.Message); }
            }
        }

        static void TG_CutsSha()
        {
            long size = 4L * 1048576 + 5;
            DownloadItem it = Download(NewMgr(), "range,slow,cut", size, "gcut", 60);
            AssertComplete(it, size);
            long size2 = 2L * 1048576 + 3;
            DownloadItem it2 = Download(NewMgr(), "range,cutall", size2, "gcutall", 90);
            AssertComplete(it2, size2);
        }

        static void StopAndResume(DownloadManager m, DownloadItem it, long atBytes)
        {
            WaitProgress(it, atBytes, 30);
            m.StopItem(it);
            WaitEnd(it, 15);
            Check(it.Status == DownloadStatus.Paused, "Estado tras parar: " + it.Status);
            m.StartItem(it);
        }

        static void TG_StopEarlyResume()
        {
            DownloadManager m = NewMgr();
            long size = 6L * 1048576 + 13;
            string id = NewId("gstop1");
            DownloadItem it = m.Add(S.Url("range,slow", "parar-pronto.bin", size, id), Dl, null, true);
            StopAndResume(m, it, 1);
            WaitEnd(it, 60);
            AssertComplete(it, size);
        }

        static void TG_StopMidResume()
        {
            DownloadManager m = NewMgr();
            long size = 8L * 1048576 + 3;
            string id = NewId("gstop2");
            DownloadItem it = m.Add(S.Url("range,slow,cut", "parar-mitad.bin", size, id), Dl, null, true);
            StopAndResume(m, it, size / 3);
            StopAndResume(m, it, size * 2 / 3);
            WaitEnd(it, 60);
            AssertComplete(it, size);
        }

        static void TG_Limit2With32()
        {
            AppConfig c = Cfg();
            c.MaxConnections = 32;
            DownloadManager m = new DownloadManager(c);
            long size = 8L * 1024 * 1024;
            string id = NewId("glimit32");
            Stopwatch sw = Stopwatch.StartNew();
            DownloadItem it = m.Add(S.Url("range,limit2,slow", "dos32.bin", size, id), Dl, null, true);
            int samples = 0, two = 0;
            DateTime limit = DateTime.Now.AddSeconds(90);
            while (DateTime.Now < limit)
            {
                bool running = it.Engine != null && it.Engine.IsRunning;
                if (!running && (it.Status == DownloadStatus.Complete || it.Status == DownloadStatus.Error || it.Status == DownloadStatus.Paused)) break;
                if (it.Downloaded > 0) { samples++; if (S.Count(id + ":conc") >= 2) two++; }
                Thread.Sleep(100);
            }
            double t = sw.Elapsed.TotalSeconds;
            AssertComplete(it, size);
            int n429 = S.Count(id + ":429"), maxc = S.Count(id + ":maxconc");
            double frac = samples > 0 ? two / (double)samples : 0;
            double single = size / (16.0 * 1024 / 0.030);
            Console.WriteLine("        (" + t.ToString("0.0") + " s, una conexión ~" + single.ToString("0.0") + " s; 429: " + n429 +
                              "; peticiones " + S.Count(id + ":req") + "; 2 conexiones el " + (frac * 100).ToString("0") + " % del tiempo)");
            Check(n429 >= 1, "El servidor no llegó a limitar (la prueba no probó nada)");
            Check(n429 < 40, "Demasiadas respuestas 429: " + n429);
            Check(maxc == 2, "Conexiones simultáneas máximas en el servidor: " + maxc);
            Check(frac >= 0.6, "Solo hubo 2 conexiones el " + (frac * 100).ToString("0") + " % del tiempo");
            Check(t < single * 0.8, "Tardó " + t.ToString("0.0") + " s; con una sola conexión serían ~" + single.ToString("0.0") + " s");
        }

        // "slow" limita cada conexión (~530 KB/s): 8 conexiones bajan el doble que 4. Antes se probaba
        // 1 -> 2 -> 4 -> 8; ahora la primera medida (ya con 4) es solo la referencia y la segunda vuelve a
        // medir 4 frente a 4, así que el crecimiento se para en 4 aunque 8 serían más rápidas.
        static void TG_PerConnThrottleGrows()
        {
            DownloadManager m = NewMgr(); // MaxConnections = 8
            long size = 24L * 1024 * 1024;
            string id = NewId("gthrottle");
            Stopwatch sw = Stopwatch.StartNew();
            DownloadItem it = m.Add(S.Url("range,slow", "porconexion.bin", size, id), Dl, null, true);
            WaitEnd(it, 120);
            double t = sw.Elapsed.TotalSeconds;
            AssertComplete(it, size);
            int maxc = S.Count(id + ":maxconc");
            Console.WriteLine("        (" + t.ToString("0.0") + " s; máximo de conexiones simultáneas " + maxc + ")");
            Check(maxc > 4, "Con un límite por conexión no pasó de " + maxc + " conexiones (8 configuradas)");
        }
    }
}
