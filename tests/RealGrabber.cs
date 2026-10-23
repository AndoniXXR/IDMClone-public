using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace IDMClone.Tests
{
    // Grabber contra un sitio real (no forma parte de los tests automáticos).
    //   IDMCloneTests.exe --grabber <carpeta> <url> <plantilla 0-7> [niveles|todo] [descargar]
    // Explora con la plantilla indicada, muestra lo encontrado y, con "descargar", lo baja todo.
    public static partial class TestMain
    {
        static int RunGrabberReal(string[] args)
        {
            int at = Array.IndexOf(args, "--grabber");
            string dir = args[at + 1], url = args[at + 2];
            int template = int.Parse(args[at + 3]);
            string levels = args.Length > at + 4 ? args[at + 4] : "todo";
            bool download = Array.IndexOf(args, "descargar") >= 0;
            Directory.CreateDirectory(dir);
            AppConfig.HomeOverride = Path.Combine(dir, "_datos");
            AppConfig cfg = AppConfig.CreateDefault();
            cfg.DefaultFolder = dir;
            foreach (Category c in cfg.Categories) c.Folder = Path.Combine(dir, c.Name);
            GrabberProject p = GrabberProject.CreateDefault();
            p.StartUrl = url;
            p.ApplyTemplate(template, cfg);
            if (levels != "todo") { p.WholeSite = false; p.LevelsSite = int.Parse(levels); }
            Console.WriteLine("Plantilla: " + GrabberProject.Templates[template] + ", filtro: " + p.IncludeFilter + (p.WholeSite ? ", sitio completo" : ", niveles: " + p.LevelsSite));
            GrabberEngine e = new GrabberEngine(p, cfg);
            ManualResetEvent explored = new ManualResetEvent(false), downloaded = new ManualResetEvent(false);
            e.ExploreFinished += delegate { explored.Set(); };
            e.DownloadFinished += delegate { downloaded.Set(); };
            Stopwatch sw = Stopwatch.StartNew();
            e.StartExploring();
            while (!explored.WaitOne(5000))
            {
                int pd, pt, f, m, d; e.Counts(out pd, out pt, out f, out m, out d);
                Console.WriteLine("  ... páginas " + pd + "/" + pt + ", archivos " + f);
                if (sw.Elapsed.TotalMinutes > 10) { e.StopExploring(); explored.WaitOne(30000); break; }
            }
            int pd2, pt2, files, matched, done;
            e.Counts(out pd2, out pt2, out files, out matched, out done);
            Console.WriteLine(string.Format("EXPLORADO: {0} páginas, {1} archivos, examinados {2}, en {3:0.0} s", pd2, files, e.Examined, sw.Elapsed.TotalSeconds));
            Dictionary<string, int> types = new Dictionary<string, int>();
            long bytes = 0;
            foreach (GrabberFile f in p.Files) { int n; types.TryGetValue(f.Type, out n); types[f.Type] = n + 1; if (f.Size > 0) bytes += f.Size; }
            foreach (KeyValuePair<string, int> kv in types) Console.WriteLine("  " + kv.Value + " x " + kv.Key);
            Console.WriteLine(string.Format("  tamaño total conocido: {0:0.0} MB", bytes / 1048576.0));
            for (int i = 0; i < Math.Min(5, p.Files.Count); i++) Console.WriteLine("  ej.: " + p.Files[i].Name + " (" + p.Files[i].Size + ") <- " + p.Files[i].Referer);
            if (!download) return 0;
            sw.Restart();
            e.StartDownloading(null);
            downloaded.WaitOne(TimeSpan.FromMinutes(15));
            e.Counts(out pd2, out pt2, out files, out matched, out done);
            int errors = p.Files.FindAll(delegate(GrabberFile f) { return f.Status != "Completo"; }).Count;
            Console.WriteLine(string.Format("DESCARGADO: {0} de {1} en {2:0.0} s ({3} sin completar)", done, files, sw.Elapsed.TotalSeconds, errors));
            foreach (GrabberFile f in p.Files) if (f.Status != "Completo") Console.WriteLine("  sin completar: " + f.Name + " " + f.Status + " " + f.Url);
            // Las imágenes JPEG/PNG bajadas son imágenes de verdad.
            int bad = 0, imgs = 0;
            foreach (GrabberFile f in p.Files)
            {
                if (f.Status != "Completo" || string.IsNullOrEmpty(f.SaveTo)) continue;
                string ext = Path.GetExtension(f.SaveTo).ToLowerInvariant();
                if (ext != ".jpg" && ext != ".jpeg" && ext != ".png") continue;
                imgs++;
                byte[] h = new byte[4];
                using (FileStream fs = File.OpenRead(f.SaveTo)) fs.Read(h, 0, 4);
                bool ok = ext == ".png" ? h[0] == 0x89 && h[1] == (byte)'P' : h[0] == 0xFF && h[1] == 0xD8;
                if (!ok) bad++;
            }
            Console.WriteLine("IMÁGENES VÁLIDAS: " + (imgs - bad) + " de " + imgs);
            string index = null;
            foreach (GrabberFile f in p.Files) if (f.IsPage && f.Url == p.StartUrl || f.IsPage && index == null) index = f.SaveTo;
            if (index != null) Console.WriteLine("PÁGINA: " + index);
            return errors == 0 && bad == 0 ? 0 : 1;
        }
    }
}
