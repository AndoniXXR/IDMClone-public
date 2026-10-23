using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace IDMClone.Tests
{
    // Emisiones en directo (se graban hasta detener o hasta que terminan) y vídeo+audio en archivos aparte.
    public static partial class TestMain
    {
        static void RunLive()
        {
            Run("directo HLS: graba desde el final, sin huecos ni repetidos, y detener da el archivo", TV_HlsLiveStop);
            Run("directo HLS: cuando la emisión termina (EXT-X-ENDLIST) acaba sola", TV_HlsLiveEnds);
            Run("directo DASH: MPD dinámico con $Number$ calculado por la hora", TV_DashLive);
            Run("directo: la sonda dice que es en directo", TV_LiveProbe);
            Run("vídeo y audio en archivos aparte (como Facebook): se bajan y se mezclan", TV_Pair);
            Run("panel -> IDMClone con las ventanas de IDM: información del archivo, progreso y descarga completada", TV_PanelDialogs);
        }

        // Lo mismo que hace IDMClone al recibir del navegador la calidad elegida en el panel.
        static void TV_PanelDialogs()
        {
            MainForm f = NewMainForm();
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                AppConfig c = m.Config;
                c.ShowFileInfoDialog = true; c.StartWhileInfoDialog = true; c.ShowCompleteDialog = true; c.ProgressDialogMode = "normal";
                string master = PutHls("hls-panel", 3);
                string r = f.HandleCommand("{\"type\":\"download\",\"url\":" + Ipc.JsonQuote(master) + ",\"referrer\":\"http://127.0.0.1/ver\",\"cookies\":\"\",\"filename\":\"Vídeo del panel.mp4\",\"userAgent\":\"UA/1\",\"incognito\":false,\"mediaKind\":\"hls\",\"mediaVideo\":\"\",\"mediaAudio\":\"\"}");
                Check(r.Contains("\"ok\":true"), "Respuesta: " + r);
                FileInfoForm info = null;
                Check(PumpUntil(delegate { foreach (Form o in Application.OpenForms) if (o is FileInfoForm) info = (FileInfoForm)o; return info != null; }, 10), "No apareció «Información del archivo a descargar»");
                Check(info.Item.IsMedia && info.Item.FileName == "Vídeo del panel.mp4", "Elemento: " + info.Item.FileName + " " + info.Item.MediaKind);
                info.Start();
                Check(PumpUntil(delegate { return info.Item.Status == DownloadStatus.Complete; }, 30), "No terminó: " + info.Item.Status + " " + info.Item.Error);
                CompleteForm done = null;
                Check(PumpUntil(delegate { foreach (Form o in Application.OpenForms) if (o is CompleteForm) done = (CompleteForm)o; return done != null; }, 10), "No apareció «Descarga completada»");
                List<string> buttons = new List<string>();
                foreach (Control x in done.Controls) if (x is Button) buttons.Add(x.Text);
                Check(buttons.Contains("Abrir") && buttons.Contains("Abrir carpeta") && buttons.Contains("Cerrar"), "Botones: " + string.Join(", ", buttons.ToArray()));
                Check(File.Exists(info.Item.FilePath), "Falta el archivo");
            }
            finally { CloseOpenForms(typeof(CompleteForm)); CloseOpenForms(typeof(ProgressForm)); CloseOpenForms(typeof(FileInfoForm)); DisposeMainForm(f); }
        }

        // Emisión HLS simulada: cada "tick" publica un trozo nuevo y quita el más viejo (ventana de 4).
        class LiveHls
        {
            public readonly string Dir; public int Next; readonly object _l = new object();
            public bool Ended;
            public LiveHls(string dir) { Dir = dir; }
            public string Url { get { return S.Base + "/s/" + Dir + "/live.m3u8"; } }
            public void Tick()
            {
                lock (_l)
                {
                    S.Put(Dir + "/seg" + Next + ".ts", MakeTsSegment(Next), "video/mp2t");
                    Next++;
                    int first = Math.Max(0, Next - 4);
                    StringBuilder sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:" + first + "\n");
                    for (int i = first; i < Next; i++) sb.Append("#EXTINF:2.000,\nseg").Append(i).Append(".ts\n");
                    if (Ended) sb.Append("#EXT-X-ENDLIST\n");
                    S.Put(Dir + "/live.m3u8", sb.ToString(), "application/vnd.apple.mpegurl");
                }
            }
        }

        static void TV_HlsLiveStop()
        {
            LiveHls live = new LiveHls("live-" + NewId("h"));
            for (int i = 0; i < 6; i++) live.Tick(); // ya hay emisión: trozos 2..5 en la lista
            DownloadManager m = NewMgr();
            DownloadItem it = AddMedia(m, live.Url, "hls", "directo.mp4", null, null, true);
            // La emisión sigue: un trozo nuevo cada 0,4 s (la lista se vuelve a pedir cada segundo).
            for (int i = 0; i < 8; i++) { Thread.Sleep(400); live.Tick(); }
            Check(it.Status == DownloadStatus.Downloading || it.Status == DownloadStatus.Connecting, "Grabando: " + it.Status + " " + it.Error);
            Thread.Sleep(1500);
            int published = live.Next;
            m.StopItem(it);
            WaitEnd(it, 30);
            Check(it.Status == DownloadStatus.Complete, "Detener debería terminar la grabación: " + it.Status + " " + it.Error);
            // Empieza en los 3 últimos trozos (3, 4, 5) y sigue con todos los nuevos, cada uno una vez.
            int firstRec = -1, lastRec = -1;
            for (int i = 0; i < published; i++)
            {
                int hits = S.StaticHits(live.Dir + "/seg" + i + ".ts");
                Check(hits <= 1, "El trozo " + i + " se bajó " + hits + " veces");
                if (hits == 1) { if (firstRec < 0) firstRec = i; lastRec = i; }
                else if (firstRec >= 0 && i < lastRec) Check(false, "Hueco en el trozo " + i);
            }
            Check(firstRec == 3, "Debería empezar cerca del final (trozo 3), empezó en " + firstRec);
            Check(lastRec >= 10, "No siguió grabando: último " + lastRec);
            int segs = lastRec - firstRec + 1;
            List<MediaTrackData> t = Tracks(it.FilePath);
            MediaTrackData v = t.Find(delegate(MediaTrackData x) { return x.IsVideo; });
            Check(t.Count == 2 && v.Samples.Count == segs * FramesPerSeg, "Imágenes: " + v.Samples.Count + " de " + segs * FramesPerSeg);
            Check(Math.Abs(v.TotalDuration / (double)v.Timescale - segs * 2.0) < 0.1, "Duración: " + v.TotalDuration / (double)v.Timescale);
            Check(it.Size == new FileInfo(it.FilePath).Length, "Tamaño final");
        }

        static void TV_HlsLiveEnds()
        {
            LiveHls live = new LiveHls("live-" + NewId("e"));
            for (int i = 0; i < 3; i++) live.Tick();
            DownloadManager m = NewMgr();
            DownloadItem it = AddMedia(m, live.Url, "hls", "acaba.mp4", null, null, true);
            Thread.Sleep(1200); live.Tick();
            Thread.Sleep(600); live.Ended = true; live.Tick();
            WaitEnd(it, 30);
            Check(it.Status == DownloadStatus.Complete, "Al terminar la emisión: " + it.Status + " " + it.Error);
            Check(Tracks(it.FilePath)[0].Samples.Count == 5 * FramesPerSeg, "Trozos: " + Tracks(it.FilePath)[0].Samples.Count / FramesPerSeg);
        }

        static void TV_DashLive()
        {
            // availabilityStartTime hace 20 s y trozos de 2 s: ya hay 10 publicados (números 1..10).
            string dir = "dlive-" + NewId("d");
            S.Put(dir + "/v/init.mp4", FInit(1, true, 12800), "video/mp4");
            for (int n = 1; n <= 40; n++) S.Put(dir + "/v/" + n + ".m4s", FSeg((uint)n, 1, (n - 1) * 50 * 512L, 50, 512, true, n), "video/mp4");
            string ast = DateTime.UtcNow.AddSeconds(-20).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            string mpd = "<?xml version=\"1.0\"?><MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"dynamic\" availabilityStartTime=\"" + ast + "\" minimumUpdatePeriod=\"PT2S\"><Period start=\"PT0S\">" +
                "<AdaptationSet mimeType=\"video/mp4\" contentType=\"video\"><SegmentTemplate timescale=\"1000\" duration=\"2000\" startNumber=\"1\" media=\"v/$Number$.m4s\" initialization=\"v/init.mp4\"/>" +
                "<Representation id=\"v\" bandwidth=\"500000\" width=\"320\" height=\"180\" codecs=\"avc1.42c01e\"/></AdaptationSet></Period></MPD>";
            string url = S.Put(dir + "/live.mpd", mpd, "application/dash+xml");
            MediaManifest man = Dash.Parse(mpd, url, DateTime.UtcNow);
            List<MediaSegment> segs = man.Videos[0].Playlist.Segments;
            Check(man.Live && segs.Count == 10 && segs[segs.Count - 1].Url.EndsWith("/v/10.m4s"), "Trozos publicados: " + segs.Count + " " + (segs.Count > 0 ? segs[segs.Count - 1].Url : ""));
            DownloadManager m = NewMgr();
            DownloadItem it = AddMedia(m, url, "dash", "directo-dash.mp4", "v", "-", true);
            Thread.Sleep(4500);
            m.StopItem(it); WaitEnd(it, 30);
            Check(it.Status == DownloadStatus.Complete, "Estado: " + it.Status + " " + it.Error);
            int first = -1, last = -1;
            for (int n = 1; n <= 40; n++) if (S.StaticHits(dir + "/v/" + n + ".m4s") > 0) { if (first < 0) first = n; last = n; }
            Check(first == 8 && last >= 11, "Grabó del " + first + " al " + last + " (debería empezar en el 8 y seguir)");
            for (int n = first; n <= last; n++) Check(S.StaticHits(dir + "/v/" + n + ".m4s") == 1, "Trozo " + n + ": " + S.StaticHits(dir + "/v/" + n + ".m4s"));
            Check(Tracks(it.FilePath)[0].Samples.Count == (last - first + 1) * 50, "Muestras: " + Tracks(it.FilePath)[0].Samples.Count);
        }

        static void TV_LiveProbe()
        {
            LiveHls live = new LiveHls("live-" + NewId("p"));
            for (int i = 0; i < 3; i++) live.Tick();
            string json = NativeHost.ProbeReply("{\"type\":\"probe\",\"url\":\"" + live.Url + "\"}");
            Check(json.Contains("\"live\":true") && json.Contains("\"items\":[{"), "Sonda: " + json);
            Check(json.Contains("\"size\":-1") && !System.Text.RegularExpressions.Regex.IsMatch(json, "\"size\":[1-9]"), "En directo no hay tamaño: " + json);
        }

        static void TV_Pair()
        {
            // Archivos MP4 enteros (como los de Facebook): vídeo e init+trozos de audio, cada uno en un archivo.
            MemoryStream v = new MemoryStream(), a = new MemoryStream();
            byte[] vi = FInit(1, true, 12800), ai = FInit(1, false, 48000);
            v.Write(vi, 0, vi.Length); a.Write(ai, 0, ai.Length);
            for (int i = 0; i < 3; i++)
            {
                byte[] vs = FSeg((uint)i + 1, 1, i * 50 * 512L, 50, 512, true, i), aseg = FSeg((uint)i + 1, 1, i * 94 * 1024L, 94, 1024, false, 100 + i);
                v.Write(vs, 0, vs.Length); a.Write(aseg, 0, aseg.Length);
            }
            string dir = "pair-" + NewId("p");
            string vu = S.Put(dir + "/video_720.mp4", v.ToArray(), "video/mp4"), au = S.Put(dir + "/audio_128.mp4", a.ToArray(), "audio/mp4");
            DownloadManager m = NewMgr();
            DownloadItem it = AddMedia(m, vu, "pair", "Clip de red social.mp4", vu, au, true);
            WaitEnd(it, 30);
            Check(it.Status == DownloadStatus.Complete, "Estado: " + it.Status + " " + it.Error);
            List<MediaTrackData> t = Tracks(it.FilePath);
            Check(t.Count == 2 && t.Exists(delegate(MediaTrackData x) { return x.IsVideo && x.Samples.Count == 150; }) && t.Exists(delegate(MediaTrackData x) { return !x.IsVideo && x.Samples.Count == 3 * 94; }), "Pistas mezcladas");
            Check(Path.GetFileName(it.FilePath) == "Clip de red social.mp4", "Nombre: " + it.FilePath);
        }
    }
}
