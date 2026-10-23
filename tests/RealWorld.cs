using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace IDMClone.Tests
{
    // Pruebas con archivos reales de internet (no forma parte de los tests automáticos).
    //   IDMCloneTests.exe --real <carpeta> [conexiones] [url ...]
    // Sin URLs usa la lista de abajo. Imprime una línea por descarga para poder tabular el resultado.
    public static partial class TestMain
    {
        class RealFile
        {
            public string Type, Url, HashAlgo, Hash;
            public RealFile(string type, string url, string algo, string hash) { Type = type; Url = url; HashAlgo = algo; Hash = hash; }
        }

        // Wikimedia (upload.wikimedia.org) limita las conexiones simultáneas: se prueba aparte, con 2.
        public static readonly string[] WikimediaFiles = {
            "https://upload.wikimedia.org/wikipedia/commons/0/0d/Hubble_ultra_deep_field_high_rez_edit1.jpg|SHA1|96e100c47fe04a534c3ecf69d969e04fe54c5353",
            "https://upload.wikimedia.org/wikipedia/commons/d/d0/Caminandes-_Llama_Drama_-_Short_Movie.ogv|SHA1|c64f247c8fb73c96aa57285077ef9105948ae9dc",
            "https://upload.wikimedia.org/wikipedia/commons/e/e6/Ludwig_van_Beethoven_-_symphony_no._5_in_c_minor%2C_op._67_-_i._allegro_con_brio.ogg|SHA1|d5e4ddcb1c4f7d08bca0540c46a0fdb9d6d7a404",
        };

        static readonly RealFile[] RealFiles = {
            new RealFile("Imagen",     "https://images-assets.nasa.gov/image/as11-40-5874/as11-40-5874~orig.jpg", null, null),
            new RealFile("Vídeo",      "https://archive.org/download/BigBuckBunny_124/Content/big_buck_bunny_720p_surround.mp4", "SHA1", "3bdcef855ca17af11e96b8f8dbaeb4d3e54b996a"),
            new RealFile("Audio",      "https://archive.org/download/alice_in_wonderland_librivox/wonderland_ch_01.mp3", "SHA1", "ab84a908e1734d6ad2f403d25be8428858990d26"),
            new RealFile("Programa",   "https://www.python.org/ftp/python/3.13.0/python-3.13.0-amd64.exe", null, null),
            new RealFile("Programa",   "https://github.com/notepad-plus-plus/notepad-plus-plus/releases/download/v8.6.9/npp.8.6.9.Installer.x64.exe", null, null),
            new RealFile("Programa",   "https://www.7-zip.org/a/7z2409-x64.exe", null, null),
            new RealFile("Documento",  "https://arxiv.org/pdf/1706.03762", null, null),
            new RealFile("Documento",  "https://calibre-ebook.com/downloads/demos/demo.docx", null, null),
            new RealFile("Comprimido", "https://www.python.org/ftp/python/3.13.0/python-3.13.0-embed-amd64.zip", null, null),
        };

        static int RunReal(string[] args)
        {
            string outDir = null; int conns = 8; List<string> urls = new List<string>();
            string proxy = null, proxyAuth = null, pac = null, temp = null;
            List<SiteLogin> logins = new List<SiteLogin>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--real" && i + 1 < args.Length) { outDir = args[++i]; continue; }
                // Opciones de red para probarlas contra servidores reales.
                if (args[i] == "--proxy" && i + 1 < args.Length) { proxy = args[++i]; continue; }
                if (args[i] == "--proxyauth" && i + 1 < args.Length) { proxyAuth = args[++i]; continue; }
                if (args[i] == "--pac" && i + 1 < args.Length) { pac = args[++i]; continue; }
                if (args[i] == "--temp" && i + 1 < args.Length) { temp = args[++i]; continue; }
                if (args[i] == "--login" && i + 3 < args.Length)
                {
                    SiteLogin l = new SiteLogin(); l.Site = args[++i]; l.User = args[++i]; l.Password = args[++i];
                    logins.Add(l); continue;
                }
                int n; if (int.TryParse(args[i], out n)) { conns = n; continue; }
                if (args[i] == "--wikimedia") { urls.AddRange(WikimediaFiles); continue; }
                if (Util.LooksLikeUrl(args[i])) urls.Add(args[i]);
            }
            Directory.CreateDirectory(outDir);
            AppConfig.HomeOverride = Path.Combine(outDir, "_datos");
            AppConfig cfg = AppConfig.CreateDefault();
            cfg.DefaultFolder = outDir;
            foreach (Category c in cfg.Categories) c.Folder = Path.Combine(outDir, c.Name);
            cfg.MaxConnections = conns;
            cfg.RetryDelaySec = 2;
            if (proxy != null)
            {
                int c = proxy.LastIndexOf(':');
                cfg.ProxyMode = "manual"; cfg.ProxyHost = proxy.Substring(0, c); cfg.ProxyPort = int.Parse(proxy.Substring(c + 1));
                if (proxyAuth != null)
                {
                    string[] up = proxyAuth.Split(new char[] { ':' }, 2);
                    cfg.ProxyUser = up[0]; cfg.ProxyPasswordEnc = Util.Protect(up[1]);
                }
            }
            if (pac != null) { cfg.ProxyMode = "pac"; cfg.PacUrl = pac; }
            if (temp != null) { cfg.UseTempFolder = true; cfg.TempFolder = temp; }
            cfg.SiteLogins.AddRange(logins);

            List<RealFile> list = new List<RealFile>();
            if (urls.Count == 0) list.AddRange(RealFiles);
            else foreach (string u in urls)
            {
                // "url|SHA1|hash" o solo "url".
                string[] p = u.Split('|');
                list.Add(new RealFile("-", p[0], p.Length >= 3 ? p[1] : null, p.Length >= 3 ? p[2] : null));
            }

            Console.WriteLine("TIPO\tESTADO\tNOMBRE\tCATEGORIA\tTAMANO\tSEGUNDOS\tVELOCIDAD_MBs\tMAX_CONEX\tSEGMENTOS\tSHA256\tOFICIAL\tRUTA\tERROR");
            foreach (RealFile rf in list)
            {
                DownloadManager m = new DownloadManager(cfg);
                m.Persist = false;
                Stopwatch sw = Stopwatch.StartNew();
                DownloadItem it = m.Add(rf.Url, null, null, true);
                int maxConn = 0;
                DateTime limit = DateTime.Now.AddMinutes(15);
                while (DateTime.Now < limit)
                {
                    maxConn = Math.Max(maxConn, it.ActiveConnections);
                    bool running = it.Engine != null && it.Engine.IsRunning;
                    if (!running && (it.Status == DownloadStatus.Complete || it.Status == DownloadStatus.Error)) break;
                    Thread.Sleep(100);
                }
                sw.Stop();
                string sha256 = "", official = "-";
                if (it.Status == DownloadStatus.Complete)
                {
                    sha256 = HashFile(it.FilePath, "SHA256");
                    if (rf.HashAlgo != null)
                        official = HashFile(it.FilePath, rf.HashAlgo).Equals(rf.Hash, StringComparison.OrdinalIgnoreCase) ? rf.HashAlgo + " OK" : rf.HashAlgo + " DISTINTO";
                }
                double secs = sw.Elapsed.TotalSeconds;
                double mbs = it.Size > 0 ? it.Size / 1048576.0 / secs : 0;
                Console.WriteLine(string.Join("\t", new string[] {
                    rf.Type, it.Status.ToString(), it.FileName, it.Category, it.Size.ToString(CultureInfo.InvariantCulture),
                    secs.ToString("0.0", CultureInfo.InvariantCulture), mbs.ToString("0.00", CultureInfo.InvariantCulture),
                    maxConn.ToString(), it.Segments.Count.ToString(), sha256, official, it.FilePath, it.Error ?? "" }));
            }
            return 0;
        }

        static string HashFile(string path, string algo)
        {
            using (HashAlgorithm h = algo == "SHA1" ? (HashAlgorithm)SHA1.Create() : SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
                return BitConverter.ToString(h.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }
    }
}
