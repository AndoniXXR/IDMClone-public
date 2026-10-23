using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace IDMClone.Tests
{
    // Vídeo por streaming real (no forma parte de los tests automáticos).
    //   IDMCloneTests.exe --media <carpeta> <hls|dash> <url> [calidad|-] [audio|-] [conexiones] [nomerge]
    // "calidad" = clave de la lista (URL HLS o id DASH); "-" = la que elija IDMClone. Imprime el resultado
    // y la estructura del archivo que sale (pistas, códecs, duración).
    public static partial class TestMain
    {
        static int RunMediaReal(string[] args)
        {
            int at = Array.IndexOf(args, "--media");
            string outDir = args[at + 1], kind = args[at + 2], url = args[at + 3];
            string video = args.Length > at + 4 && args[at + 4] != "-" ? args[at + 4] : null;
            string audio = args.Length > at + 5 && args[at + 5] != "_" ? args[at + 5] : null;
            if (audio == "*") audio = null;
            int conns = args.Length > at + 6 ? int.Parse(args[at + 6]) : 8;
            bool noMerge = Array.IndexOf(args, "nomerge") >= 0;
            Directory.CreateDirectory(outDir);
            AppConfig.HomeOverride = Path.Combine(outDir, "_datos");
            AppConfig cfg = AppConfig.CreateDefault();
            cfg.DefaultFolder = outDir;
            foreach (Category c in cfg.Categories) c.Folder = outDir;
            cfg.MaxConnections = conns; cfg.RetryDelaySec = 2;
            cfg.MergeAudioVideo = !noMerge;
            DownloadManager m = new DownloadManager(cfg);
            m.Persist = false;
            AddOptions o = new AddOptions();
            o.Url = url; o.FileName = "video-" + kind + ".mp4"; o.StartNow = true;
            o.MediaKind = kind; o.MediaVideo = video; o.MediaAudio = audio;
            Stopwatch sw = Stopwatch.StartNew();
            DownloadItem it = m.Add(o);
            DateTime limit = DateTime.Now.AddMinutes(20);
            long lastShown = 0;
            // Directo: "stop=<segundos>" detiene (y termina) la grabación pasado ese tiempo.
            int stopAfter = 0;
            foreach (string x in args) if (x.StartsWith("stop=")) stopAfter = int.Parse(x.Substring(5));
            bool stopped = false;
            while (DateTime.Now < limit)
            {
                if (stopAfter > 0 && !stopped && sw.Elapsed.TotalSeconds >= stopAfter) { stopped = true; Console.WriteLine("  ... detener"); m.StopItem(it); }
                bool running = it.Engine != null && it.Engine.IsRunning;
                if (!running && (it.Status == DownloadStatus.Complete || it.Status == DownloadStatus.Error)) break;
                if (sw.ElapsedMilliseconds - lastShown > 5000)
                {
                    lastShown = sw.ElapsedMilliseconds;
                    Console.WriteLine(string.Format("  ... {0} {1:0.0} MB de {2:0.0} MB, {3} conexiones", it.Status, it.Downloaded / 1048576.0, it.Size / 1048576.0, it.ActiveConnections));
                }
                Thread.Sleep(200);
            }
            sw.Stop();
            Console.WriteLine("ESTADO: " + it.Status + (it.Error != null ? " (" + it.Error + ")" : ""));
            Console.WriteLine("ARCHIVO: " + it.FilePath);
            if (!string.IsNullOrEmpty(it.Description)) Console.WriteLine("NOTA: " + it.Description);
            if (it.Status != DownloadStatus.Complete) return 1;
            long size = new FileInfo(it.FilePath).Length;
            Console.WriteLine(string.Format("TAMAÑO: {0:0.00} MB en {1:0.0} s ({2:0.00} MB/s)", size / 1048576.0, sw.Elapsed.TotalSeconds, size / 1048576.0 / sw.Elapsed.TotalSeconds));
            DescribeMedia(it.FilePath);
            foreach (string f in Directory.GetFiles(Path.GetDirectoryName(it.FilePath), "*(audio)*")) { Console.WriteLine("AUDIO APARTE: " + f); DescribeMedia(f); }
            return 0;
        }

        static void DescribeMedia(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".mp4" || ext == ".m4a")
            {
                foreach (MediaTrackData t in Fmp4Reader.Read(path, 0))
                    Console.WriteLine(string.Format("  pista {0} {1} {2}x{3} muestras={4} claves={5} duración={6:0.00} s",
                        t.Handler, t.Codec, t.Width, t.Height, t.Samples.Count, t.Samples.FindAll(delegate(MediaSample s) { return s.Sync; }).Count,
                        t.TotalDuration / (double)t.Timescale));
            }
            else Console.WriteLine("  (" + ext + ")");
        }
    }
}
