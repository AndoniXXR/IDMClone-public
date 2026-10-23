using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace IDMClone.Tests
{
    // Tests del vídeo por streaming (fase 5): listas HLS/DASH, descarga por trozos, AES-128, reanudar,
    // TS -> MP4, unión de vídeo y audio (MP4 y WebM) y la sonda del panel "Descargar este video".
    // Todo con contenido sintético servido por el servidor de pruebas (paquetes TS, MP4 fragmentado y
    // WebM de verdad, con datos de relleno en vez de imagen y sonido).
    public static partial class TestMain
    {
        static void RunMedia()
        {
            Run("vídeo: lista maestra HLS (calidades, audio aparte, rangos, claves, EXT-X-MAP)", TM_HlsParse);
            Run("vídeo: manifiesto DASH (plantillas $Number%05d$/$Time$, SegmentTimeline, lista, archivo único, DRM)", TM_DashParse);
            Run("vídeo: SPS H.264 -> tamaño de imagen (con recorte)", TM_SpsSize);
            Run("vídeo: HLS .ts -> MP4 con vídeo y audio sincronizados", TM_HlsTsToMp4);
            Run("vídeo: HLS cifrado AES-128 (IV explícito y por secuencia)", TM_HlsAes);
            Run("vídeo: pausar y reanudar no vuelve a bajar los trozos que ya están", TM_HlsResume);
            Run("vídeo: trozo que no existe, DRM y lista no válida dan error claro", TM_HlsErrors);
            Run("vídeo: DASH MP4 fragmentado, mezcla de vídeo y audio en un MP4", TM_DashMerge);
            Run("vídeo: solo audio y sin mezclar (vídeo y audio por separado)", TM_DashAudioOnlyAndSeparate);
            Run("vídeo: unión WebM (VP9 + Opus) con pista renumerada e índice", TM_WebmMerge);
            Run("vídeo: sonda del panel (calidades, tamaño estimado, listas hijas)", TM_Probe);
            Run("vídeo: opciones del panel y mezcla en Opciones > General", TM_PanelOptions);
        }

        // ------------------------------------------------------------------ generadores

        class BitW
        {
            readonly List<byte> _b = new List<byte>(); int _cur, _n;
            public void U(int bits, int v) { for (int i = bits - 1; i >= 0; i--) Bit(v >> i & 1); }
            public void Bit(int v) { _cur = _cur << 1 | v; if (++_n == 8) { _b.Add((byte)_cur); _cur = 0; _n = 0; } }
            public void Ue(int v) { int x = v + 1, len = 0; for (int t = x; t > 1; t >>= 1) len++; U(len, 0); U(len + 1, x); }
            public byte[] Done() { Bit(1); while (_n != 0) Bit(0); return _b.ToArray(); }
        }

        static byte[] Ebsp(byte[] rbsp)
        {
            List<byte> r = new List<byte>(); int zeros = 0;
            foreach (byte x in rbsp)
            {
                if (zeros >= 2 && x <= 3) { r.Add(3); zeros = 0; }
                r.Add(x); zeros = x == 0 ? zeros + 1 : 0;
            }
            return r.ToArray();
        }

        // SPS baseline de 320x180 (20x12 macrobloques, recorte de 12 líneas abajo).
        static byte[] TestSps()
        {
            BitW w = new BitW();
            w.U(8, 66); w.U(8, 0xC0); w.U(8, 30); w.Ue(0); w.Ue(0); w.Ue(2); w.Ue(1); w.Bit(0);
            w.Ue(19); w.Ue(11); w.Bit(1); w.Bit(1); w.Bit(1); w.Ue(0); w.Ue(0); w.Ue(0); w.Ue(6); w.Bit(0);
            byte[] body = Ebsp(w.Done());
            byte[] nal = new byte[body.Length + 1]; nal[0] = 0x67; Buffer.BlockCopy(body, 0, nal, 1, body.Length);
            return nal;
        }
        static readonly byte[] TestPps = { 0x68, 0xCE, 0x38, 0x80 };

        class TsW
        {
            public readonly MemoryStream O = new MemoryStream();
            readonly Dictionary<int, int> _cc = new Dictionary<int, int>();
            int Cc(int pid) { int c; _cc.TryGetValue(pid, out c); _cc[pid] = (c + 1) & 15; return c; }

            void Packet(int pid, bool pusi, byte[] payload, int off, int len)
            {
                byte[] p = new byte[188];
                p[0] = 0x47; p[1] = (byte)((pusi ? 0x40 : 0) | pid >> 8); p[2] = (byte)pid;
                int stuff = 184 - len;
                if (stuff > 0)
                {
                    p[3] = (byte)(0x30 | Cc(pid));
                    p[4] = (byte)(stuff - 1);
                    if (stuff > 1) { p[5] = 0; for (int i = 6; i < 4 + stuff; i++) p[i] = 0xFF; }
                }
                else p[3] = (byte)(0x10 | Cc(pid));
                Buffer.BlockCopy(payload, off, p, 4 + Math.Max(0, stuff), len);
                O.Write(p, 0, 188);
            }

            public void Psi(int pid, byte[] section)
            {
                byte[] d = new byte[section.Length + 1]; Buffer.BlockCopy(section, 0, d, 1, section.Length);
                Packet(pid, true, d, 0, d.Length);
            }

            static void Ts(List<byte> b, int marker, long t)
            {
                b.Add((byte)(marker << 4 | (int)(t >> 29 & 0x0E) | 1));
                b.Add((byte)(t >> 22)); b.Add((byte)((t >> 14 & 0xFE) | 1)); b.Add((byte)(t >> 7)); b.Add((byte)((t << 1 & 0xFE) | 1));
            }

            public void Pes(int pid, int sid, long pts, long dts, byte[] data, bool video)
            {
                List<byte> h = new List<byte> { 0, 0, 1, (byte)sid, 0, 0, 0x80 };
                bool both = dts >= 0 && dts != pts;
                h.Add((byte)(both ? 0xC0 : 0x80)); h.Add((byte)(both ? 10 : 5));
                Ts(h, both ? 3 : 2, pts); if (both) Ts(h, 1, dts);
                int len = h.Count - 6 + data.Length;
                if (!video) { h[4] = (byte)(len >> 8); h[5] = (byte)len; }
                byte[] all = new byte[h.Count + data.Length];
                h.CopyTo(all); Buffer.BlockCopy(data, 0, all, h.Count, data.Length);
                for (int off = 0; off < all.Length; off += 184) Packet(pid, off == 0, all, off, Math.Min(184, all.Length - off));
            }
        }

        static readonly byte[] Pat = { 0x00, 0xB0, 0x0D, 0x00, 0x01, 0xC1, 0x00, 0x00, 0x00, 0x01, 0xF0, 0x00, 0, 0, 0, 0 };
        static readonly byte[] PmtAv = { 0x02, 0xB0, 0x17, 0x00, 0x01, 0xC1, 0x00, 0x00, 0xE1, 0x00, 0xF0, 0x00,
                                         0x1B, 0xE1, 0x00, 0xF0, 0x00, 0x0F, 0xE1, 0x01, 0xF0, 0x00, 0, 0, 0, 0 };

        const int Fps = 25, FramesPerSeg = 50;
        const double AudioFrameSec = 1024.0 / 44100;

        // Trozo HLS de 2 s: 50 imágenes (IDR al principio) y los fotogramas AAC de ese intervalo.
        static byte[] MakeTsSegment(int seg)
        {
            TsW w = new TsW();
            w.Psi(0, Pat); w.Psi(0x1000, PmtAv);
            byte[] sps = TestSps();
            double t0 = seg * FramesPerSeg / (double)Fps, t1 = (seg + 1) * FramesPerSeg / (double)Fps;
            int a0 = (int)Math.Ceiling(t0 / AudioFrameSec - 1e-9), a1 = (int)Math.Ceiling(t1 / AudioFrameSec - 1e-9);
            int nextAudio = a0;
            for (int i = 0; i < FramesPerSeg; i++)
            {
                int f = seg * FramesPerSeg + i;
                MemoryStream au = new MemoryStream();
                Action<byte[]> nal = delegate(byte[] n) { au.Write(new byte[] { 0, 0, 0, 1 }, 0, 4); au.Write(n, 0, n.Length); };
                nal(new byte[] { 0x09, 0xF0 });
                if (i == 0) { nal(sps); nal(TestPps); }
                byte[] slice = new byte[150 + f % 7];
                slice[0] = (byte)(i == 0 ? 0x65 : 0x41);
                for (int k = 1; k < slice.Length; k++) slice[k] = (byte)(0x20 + (f + k) % 64);
                nal(slice);
                long pts = 9000 + f * 3600L;
                w.Pes(0x100, 0xE0, pts, pts, au.ToArray(), true);
                // Audio hasta el tiempo de esta imagen, en PES de 5 fotogramas.
                double until = Math.Min(t1, (f + 1) / (double)Fps);
                while (nextAudio < a1 && nextAudio * AudioFrameSec < until)
                {
                    MemoryStream ad = new MemoryStream();
                    long apts = 9000 + (long)Math.Round(nextAudio * AudioFrameSec * 90000);
                    for (int k = 0; k < 5 && nextAudio < a1; k++, nextAudio++)
                    {
                        int len = 7 + 60;
                        byte[] fr = new byte[len];
                        fr[0] = 0xFF; fr[1] = 0xF1; fr[2] = (byte)(1 << 6 | 4 << 2 | 0); fr[3] = (byte)(2 << 6 | len >> 11);
                        fr[4] = (byte)(len >> 3); fr[5] = (byte)((len & 7) << 5 | 0x1F); fr[6] = 0xFC;
                        for (int q = 7; q < len; q++) fr[q] = (byte)(0x21 + nextAudio % 50);
                        ad.Write(fr, 0, len);
                    }
                    w.Pes(0x101, 0xC0, apts, -1, ad.ToArray(), false);
                }
            }
            return w.O.ToArray();
        }

        static int TotalAudioFrames(int segs) { return (int)Math.Ceiling(segs * FramesPerSeg / (double)Fps / AudioFrameSec - 1e-9); }

        static string HlsMedia(int segs, string segName, string extra)
        {
            StringBuilder sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n");
            if (extra != null) sb.Append(extra);
            for (int i = 0; i < segs; i++) sb.Append("#EXTINF:2.000,\n").Append(string.Format(segName, i)).Append('\n');
            sb.Append("#EXT-X-ENDLIST\n");
            return sb.ToString();
        }

        static string PutHls(string dir, int segs)
        {
            for (int i = 0; i < segs; i++) S.Put(dir + "/seg" + i + ".ts", MakeTsSegment(i), "video/mp2t");
            S.Put(dir + "/v.m3u8", HlsMedia(segs, "seg{0}.ts", null), "application/vnd.apple.mpegurl");
            return S.Put(dir + "/master.m3u8",
                "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=320x180,CODECS=\"avc1.42c01e,mp4a.40.2\"\nv.m3u8\n" +
                "#EXT-X-STREAM-INF:BANDWIDTH=300000,RESOLUTION=160x90,CODECS=\"avc1.42c01e,mp4a.40.2\"\nbaja/v.m3u8\n", "application/vnd.apple.mpegurl");
        }

        static DownloadItem AddMedia(DownloadManager m, string url, string kind, string name, string video, string audio, bool start)
        {
            AddOptions o = new AddOptions();
            o.Url = url; o.MediaKind = kind; o.FileName = name; o.MediaVideo = video; o.MediaAudio = audio; o.StartNow = start;
            o.Folder = Dl;
            return m.Add(o);
        }

        static List<MediaTrackData> Tracks(string path) { return Fmp4Reader.Read(path, 0); }

        static byte[] SampleBytes(string path, MediaSample s)
        {
            using (FileStream f = File.OpenRead(path)) return Bmff.Read(f, s.Offset, s.Size);
        }

        // ------------------------------------------------------------------ listas

        static void TM_HlsParse()
        {
            string master = "#EXTM3U\n" +
                "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",LANGUAGE=\"es\",NAME=\"Español\",DEFAULT=YES,URI=\"audio/es.m3u8\"\n" +
                "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",LANGUAGE=\"en\",NAME=\"English, US\",URI=\"audio/en.m3u8\"\n" +
                "#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,CODECS=\"avc1.4d401e,mp4a.40.2\",AUDIO=\"aud\"\n360/v.m3u8\n" +
                "#EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=90000,URI=\"iframes.m3u8\"\n" +
                "#EXT-X-STREAM-INF:BANDWIDTH=2400000,RESOLUTION=1280x720,CODECS=\"avc1.64001f,mp4a.40.2\",AUDIO=\"aud\"\nhttps://otro.cdn.com/720/v.m3u8?t=1\n" +
                "#EXT-X-STREAM-INF:BANDWIDTH=64000,CODECS=\"mp4a.40.2\"\naudio-solo.m3u8\n";
            MediaManifest m = Hls.ParseMaster(master, "https://ej.com/vid/master.m3u8");
            Check(m.Videos.Count == 2 && m.Videos[0].Height == 720 && m.Videos[1].Height == 360, "Calidades: " + m.Videos.Count);
            Check(m.Videos[0].Key == "https://otro.cdn.com/720/v.m3u8?t=1" && m.Videos[1].Key == "https://ej.com/vid/360/v.m3u8", "URLs de calidades");
            Check(m.Videos[0].Kind == "video" && m.Videos[0].AudioGroup == "aud", "Audio aparte no reconocido");
            Check(m.Audios.Count == 3, "Pistas de audio: " + m.Audios.Count);
            MediaTrack a = m.AudioFor(m.Videos[1]);
            Check(a != null && a.Language == "es" && a.Key == "https://ej.com/vid/audio/es.m3u8", "Audio por defecto del grupo");
            Check(Hls.Attributes("NAME=\"English, US\",URI=\"x\"")["NAME"] == "English, US", "Atributo con coma");

            string media = "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:7\n#EXT-X-MAP:URI=\"init.mp4\",BYTERANGE=\"720@0\"\n" +
                "#EXT-X-KEY:METHOD=AES-128,URI=\"k.bin\",IV=0x000102030405060708090A0B0C0D0E0F\n" +
                "#EXTINF:4.0,\n#EXT-X-BYTERANGE:1000@720\ntodo.mp4\n#EXTINF:4.0,\n#EXT-X-BYTERANGE:1500\ntodo.mp4\n" +
                "#EXT-X-KEY:METHOD=NONE\n#EXTINF:2.5,\nfin.mp4\n#EXT-X-ENDLIST\n";
            MediaPlaylist p = Hls.ParseMedia(media, "https://ej.com/vid/360/v.m3u8");
            Check(!p.Live && p.Segments.Count == 3 && Math.Abs(p.Duration - 10.5) < 1e-6, "Trozos/duración");
            Check(p.Init != null && p.Init.Url == "https://ej.com/vid/360/init.mp4" && p.Init.RangeLength == 720, "EXT-X-MAP");
            Check(p.Segments[0].RangeStart == 720 && p.Segments[0].RangeLength == 1000, "BYTERANGE con inicio");
            Check(p.Segments[1].RangeStart == 1720 && p.Segments[1].RangeLength == 1500, "BYTERANGE seguido");
            Check(p.Segments[0].KeyUrl == "https://ej.com/vid/360/k.bin" && p.Segments[0].IV[15] == 0x0F && p.Segments[0].Sequence == 7, "Clave/IV/secuencia");
            Check(p.Segments[2].KeyUrl == null, "METHOD=NONE no quitó la clave");
            Check(Hls.ParseMedia("#EXTM3U\n#EXTINF:2,\na.ts\n", "https://ej.com/l.m3u8").Live, "Sin ENDLIST debería ser en directo");
            Check(Hls.ParseMedia("#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://x\",KEYFORMAT=\"com.apple.streamingkeydelivery\"\n#EXTINF:2,\na.ts\n#EXT-X-ENDLIST\n", "https://ej.com/l.m3u8").Protected, "SAMPLE-AES no es protegido");
            MediaManifest single = MediaManifest.Parse(HlsMedia(3, "s{0}.ts", null), "https://ej.com/l.m3u8");
            Check(single.Videos.Count == 1 && single.Videos[0].Kind == "muxed" && Math.Abs(single.Duration - 6) < 1e-6, "Lista de un solo flujo");
        }

        static void TM_DashParse()
        {
            string mpd = "<?xml version=\"1.0\"?><MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT0H0M10.5S\">" +
                "<BaseURL>https://cdn.ej.com/v/</BaseURL><Period>" +
                "<AdaptationSet mimeType=\"video/mp4\" contentType=\"video\"><SegmentTemplate timescale=\"1000\" duration=\"4000\" startNumber=\"1\" media=\"$RepresentationID$/s$Number%05d$.m4s\" initialization=\"$RepresentationID$/init.mp4\"/>" +
                "<Representation id=\"v720\" bandwidth=\"2000000\" width=\"1280\" height=\"720\" codecs=\"avc1.64001f\"/>" +
                "<Representation id=\"v360\" bandwidth=\"600000\" width=\"640\" height=\"360\" codecs=\"avc1.4d401e\"/></AdaptationSet>" +
                "<AdaptationSet mimeType=\"audio/mp4\" lang=\"es\"><Representation id=\"a1\" bandwidth=\"128000\" codecs=\"mp4a.40.2\">" +
                "<SegmentTemplate timescale=\"48000\" media=\"a/$Time$.m4s\" initialization=\"a/init.mp4\"><SegmentTimeline><S t=\"0\" d=\"96000\" r=\"3\"/><S d=\"48000\" r=\"-1\"/></SegmentTimeline></SegmentTemplate>" +
                "</Representation></AdaptationSet>" +
                "<AdaptationSet mimeType=\"video/webm\"><Representation id=\"w\" bandwidth=\"300000\" width=\"426\" height=\"240\" codecs=\"vp9\"><BaseURL>webm/w.webm</BaseURL><SegmentBase indexRange=\"100-200\"/></Representation></AdaptationSet>" +
                "<AdaptationSet mimeType=\"video/mp4\"><Representation id=\"lst\" bandwidth=\"100000\" width=\"160\" height=\"90\"><SegmentList timescale=\"1\" duration=\"5\"><Initialization sourceURL=\"l/i.mp4\" range=\"0-99\"/><SegmentURL media=\"l/1.m4s\"/><SegmentURL media=\"l/2.m4s\" mediaRange=\"10-19\"/></SegmentList></Representation></AdaptationSet>" +
                "<AdaptationSet mimeType=\"text/vtt\"><Representation id=\"sub\" bandwidth=\"100\"/></AdaptationSet>" +
                "</Period></MPD>";
            MediaManifest m = Dash.Parse(mpd, "https://ej.com/manifest.mpd");
            Check(Math.Abs(m.Duration - 10.5) < 1e-6 && !m.Live && !m.Protected, "Duración: " + m.Duration);
            Check(m.Videos.Count == 4 && m.Audios.Count == 1, "Pistas: " + m.Videos.Count + "/" + m.Audios.Count);
            MediaTrack v = m.FindVideo("v720");
            Check(v.Playlist.Segments.Count == 3, "Trozos con duración fija: " + v.Playlist.Segments.Count);
            Check(v.Playlist.Init.Url == "https://cdn.ej.com/v/v720/init.mp4" && v.Playlist.Segments[2].Url == "https://cdn.ej.com/v/v720/s00003.m4s", "Plantilla $Number%05d$: " + v.Playlist.Segments[2].Url);
            Check(Math.Abs(v.Playlist.Segments[2].Duration - 2.5) < 1e-6, "Último trozo más corto");
            MediaTrack a = m.Audios[0];
            // r=3: 4 trozos de 2 s; r=-1: de 1 s hasta el final del periodo (10,5 s) -> 8, 9 y 10 s.
            Check(a.Playlist.Segments.Count == 7, "SegmentTimeline r=3 y r=-1: " + a.Playlist.Segments.Count);
            Check(a.Playlist.Segments[4].Url == "https://cdn.ej.com/v/a/384000.m4s" && a.Playlist.Segments[6].Url == "https://cdn.ej.com/v/a/480000.m4s", "$Time$: " + a.Playlist.Segments[6].Url);
            MediaTrack w = m.FindVideo("w");
            Check(w.Container == "webm" && w.Playlist.Segments.Count == 1 && w.Playlist.Segments[0].Url == "https://cdn.ej.com/v/webm/w.webm", "Archivo único (SegmentBase)");
            MediaTrack l = m.FindVideo("lst");
            Check(l.Playlist.Init.RangeLength == 100 && l.Playlist.Segments.Count == 2 && l.Playlist.Segments[1].RangeStart == 10 && l.Playlist.Segments[1].RangeLength == 10, "SegmentList");
            Check(m.AudioFor(v) == a, "Audio para la calidad");
            Check(m.Videos[0].Key == "v720", "Orden por calidad");
            Check(Dash.ParseDuration("PT1H2M3.5S") == 3723.5 && Dash.ParseDuration("PT634.566S") == 634.566, "Duraciones ISO 8601");
            string drm = mpd.Replace("<Representation id=\"v720\"", "<ContentProtection schemeIdUri=\"urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed\"/><Representation id=\"v720\"");
            Check(Dash.Parse(drm, "https://ej.com/m.mpd").Protected, "ContentProtection no detectado");
            Check(Dash.Parse(mpd.Replace("type=\"static\"", "type=\"dynamic\""), "https://ej.com/m.mpd").Live, "MPD dinámico no es en directo");
        }

        static void TM_SpsSize()
        {
            int w, h;
            H264.Size(TestSps(), out w, out h);
            Check(w == 320 && h == 180, "SPS: " + w + "x" + h);
        }

        // ------------------------------------------------------------------ HLS

        static void TM_HlsTsToMp4()
        {
            DownloadManager m = NewMgr();
            string master = PutHls("hls-ts", 5);
            DownloadItem it = AddMedia(m, master, "hls", "Mi película.mp4", null, null, true);
            WaitEnd(it, 30);
            Check(it.Status == DownloadStatus.Complete, "Estado: " + it.Status + " " + it.Error);
            Check(Path.GetFileName(it.FilePath) == "Mi película.mp4" && File.Exists(it.FilePath), "Archivo: " + it.FilePath);
            Check(it.Category == "Vídeo", "Categoría: " + it.Category);
            List<MediaTrackData> t = Tracks(it.FilePath);
            Check(t.Count == 2, "Pistas: " + t.Count);
            MediaTrackData v = t.Find(delegate(MediaTrackData x) { return x.IsVideo; }), a = t.Find(delegate(MediaTrackData x) { return !x.IsVideo; });
            Check(v.Codec == "avc1" && v.Width == 320 && v.Height == 180, "Vídeo: " + v.Codec + " " + v.Width + "x" + v.Height);
            Check(v.Samples.Count == 5 * FramesPerSeg && v.Samples.FindAll(delegate(MediaSample s) { return s.Sync; }).Count == 5, "Imágenes/claves: " + v.Samples.Count);
            Check(Math.Abs(v.TotalDuration / (double)v.Timescale - 10.0) < 0.05, "Duración vídeo: " + v.TotalDuration / (double)v.Timescale);
            Check(a.Codec == "mp4a" && a.Timescale == 44100 && a.Samples.Count == TotalAudioFrames(5), "Audio: " + a.Samples.Count + " de " + TotalAudioFrames(5));
            // La primera imagen es la IDR con su longitud delante (sin delimitador ni SPS/PPS dentro).
            byte[] first = SampleBytes(it.FilePath, v.Samples[0]);
            Check(first.Length > 5 && first[4] == 0x65 && (first[0] << 24 | first[1] << 16 | first[2] << 8 | first[3]) == first.Length - 4, "Primera muestra de vídeo");
            byte[] af = SampleBytes(it.FilePath, a.Samples[3]);
            Check(af.Length == 60 && af[0] == 0x21 + 3, "Muestra de audio (sin cabecera ADTS)");
            Check(!Directory.Exists(DownloadEngine.MediaWorkDir(it, m.Config)), "Quedó la carpeta de trozos");
        }

        static byte[] AesEncrypt(byte[] data, byte[] key, byte[] iv)
        {
            using (Aes aes = Aes.Create())
            {
                aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7; aes.Key = key; aes.IV = iv;
                using (ICryptoTransform e = aes.CreateEncryptor()) return e.TransformFinalBlock(data, 0, data.Length);
            }
        }

        static void TM_HlsAes()
        {
            byte[] key = new byte[16]; for (int i = 0; i < 16; i++) key[i] = (byte)(i * 7 + 3);
            byte[] iv = new byte[16]; for (int i = 0; i < 16; i++) iv[i] = (byte)(0xA0 + i);
            S.Put("hls-aes/k.bin", key, "application/octet-stream");
            StringBuilder pl = new StringBuilder("#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:40\n");
            for (int i = 0; i < 4; i++)
            {
                // Los dos primeros con IV explícito; los demás, el número de secuencia (40, 41...).
                byte[] segIv = i < 2 ? iv : new byte[16];
                if (i >= 2) { long seq = 40 + i; for (int k = 0; k < 8; k++) segIv[15 - k] = (byte)(seq >> (k * 8)); }
                if (i == 0) pl.Append("#EXT-X-KEY:METHOD=AES-128,URI=\"k.bin\",IV=0xA0A1A2A3A4A5A6A7A8A9AAABACADAEAF\n");
                if (i == 2) pl.Append("#EXT-X-KEY:METHOD=AES-128,URI=\"k.bin\"\n");
                S.Put("hls-aes/s" + i + ".ts", AesEncrypt(MakeTsSegment(i), key, segIv), "video/mp2t");
                pl.Append("#EXTINF:2.0,\ns").Append(i).Append(".ts\n");
            }
            pl.Append("#EXT-X-ENDLIST\n");
            string url = S.Put("hls-aes/v.m3u8", pl.ToString(), "application/vnd.apple.mpegurl");
            DownloadManager m = NewMgr();
            DownloadItem it = AddMedia(m, url, "hls", "cifrado.mp4", null, null, true);
            WaitEnd(it, 30);
            Check(it.Status == DownloadStatus.Complete, "Estado: " + it.Status + " " + it.Error);
            List<MediaTrackData> t = Tracks(it.FilePath);
            Check(t.Count == 2 && t[0].Samples.Count == 4 * FramesPerSeg, "No se descifró bien: " + (t.Count > 0 ? t[0].Samples.Count : 0));
            Check(S.StaticHits("hls-aes/k.bin") == 1, "La clave se pidió " + S.StaticHits("hls-aes/k.bin") + " veces");
        }

        static void TM_HlsResume()
        {
            const int segs = 12;
            for (int i = 0; i < segs; i++) S.Put("hls-res/seg" + i + ".ts", MakeTsSegment(i), "video/mp2t");
            string url = S.Put("hls-res/v.m3u8", HlsMedia(segs, "seg{0}.ts", null), "application/vnd.apple.mpegurl");
            DownloadManager m = NewMgr();
            DownloadItem it = AddMedia(m, url, "hls", "reanudar.mp4", null, null, false);
            it.SpeedLimitOn = true; it.SpeedLimitKBps = 40; // ~1 trozo cada 0,5 s
            m.StartItem(it);
            DateTime limit = DateTime.Now.AddSeconds(20);
            string work = DownloadEngine.MediaWorkDir(it, m.Config);
            while (DateTime.Now < limit && (!Directory.Exists(work) || Directory.GetFiles(work, "*.seg").Length < 3)) Thread.Sleep(50);
            m.StopItem(it); WaitEnd(it, 10);
            Check(it.Status == DownloadStatus.Paused, "Tras pausar: " + it.Status);
            string[] done = Directory.GetFiles(DownloadEngine.MediaWorkDir(it, m.Config), "*.seg");
            Check(done.Length >= 3 && done.Length < segs, "Trozos al pausar: " + done.Length);
            Check(it.Downloaded > 0 && it.Size > it.Downloaded, "Progreso estimado: " + it.Downloaded + "/" + it.Size);
            int[] before = new int[segs];
            for (int i = 0; i < segs; i++) before[i] = S.StaticHits("hls-res/seg" + i + ".ts");
            it.SpeedLimitOn = false;
            m.StartItem(it); WaitEnd(it, 30);
            Check(it.Status == DownloadStatus.Complete, "Estado: " + it.Status + " " + it.Error);
            foreach (string f in done)
            {
                int idx = int.Parse(Path.GetFileNameWithoutExtension(f).Substring(1), CultureInfo.InvariantCulture) - 1;
                Check(S.StaticHits("hls-res/seg" + idx + ".ts") == before[idx], "Se volvió a bajar el trozo " + idx);
            }
            Check(Tracks(it.FilePath)[0].Samples.Count == segs * FramesPerSeg, "Faltan imágenes tras reanudar");
            Check(it.Size == new FileInfo(it.FilePath).Length && it.Downloaded == it.Size, "Tamaño final");
        }

        static void TM_HlsErrors()
        {
            DownloadManager m = NewMgr();
            m.Config.RetryCount = 2;
            for (int i = 0; i < 4; i++) S.Put("hls-err/seg" + i + ".ts", MakeTsSegment(i), "video/mp2t");
            S.FailStatic("hls-err/seg2.ts", 404);
            string url = S.Put("hls-err/v.m3u8", HlsMedia(4, "seg{0}.ts", null), "application/vnd.apple.mpegurl");
            DownloadItem it = AddMedia(m, url, "hls", "roto.mp4", null, null, true);
            WaitEnd(it, 30);
            Check(it.Status == DownloadStatus.Error && it.Error.Contains("404"), "404: " + it.Status + " " + it.Error);

            string drm = S.Put("hls-err/drm.m3u8", "#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://k\",KEYFORMAT=\"com.apple.streamingkeydelivery\"\n#EXTINF:2,\nseg0.ts\n#EXT-X-ENDLIST\n", "application/vnd.apple.mpegurl");
            it = AddMedia(m, drm, "hls", "drm.mp4", null, null, true);
            WaitEnd(it, 30);
            Check(it.Status == DownloadStatus.Error && it.Error.Contains("protegido"), "DRM: " + it.Error);

            string notList = S.Put("hls-err/no.m3u8", "<html>hola</html>", "text/html");
            it = AddMedia(m, notList, "hls", "nolista.mp4", null, null, true);
            WaitEnd(it, 30);
            Check(it.Status == DownloadStatus.Error && it.Error.Contains("no es válida"), "No es lista: " + it.Error);
        }

        // ------------------------------------------------------------------ DASH (MP4 fragmentado)

        static byte[] FInit(uint id, bool video, uint timescale)
        {
            BoxBuilder b = new BoxBuilder();
            b.Begin("ftyp"); b.Ascii("iso6"); b.U32(0); b.Ascii("iso6"); b.Ascii("dash"); b.End();
            b.Begin("moov");
            b.Begin("mvhd"); b.U32(0); b.U32(0); b.U32(0); b.U32(1000); b.U32(0); b.U32(0x10000); b.U16(0x100); b.Zero(10);
            b.U32(0x10000); b.Zero(12); b.U32(0x10000); b.Zero(12); b.U32(0x40000000); b.Zero(24); b.U32(id + 1); b.End();
            b.Begin("trak");
            b.Begin("tkhd"); b.U32(3); b.U32(0); b.U32(0); b.U32(id); b.U32(0); b.U32(0); b.Zero(8); b.Zero(8);
            b.U32(0x10000); b.Zero(12); b.U32(0x10000); b.Zero(12); b.U32(0x40000000); b.U32(video ? 320u << 16 : 0); b.U32(video ? 180u << 16 : 0); b.End();
            b.Begin("mdia");
            b.Begin("mdhd"); b.U32(0); b.U32(0); b.U32(0); b.U32(timescale); b.U32(0); b.U16(0x15C7); b.U16(0); b.End();
            b.Begin("hdlr"); b.U32(0); b.U32(0); b.Ascii(video ? "vide" : "soun"); b.Zero(13); b.End();
            b.Begin("minf");
            if (video) { b.Begin("vmhd"); b.U32(1); b.Zero(8); b.End(); } else { b.Begin("smhd"); b.Zero(8); b.End(); }
            b.Begin("dinf"); b.Begin("dref"); b.U32(0); b.U32(1); b.Begin("url "); b.U32(1); b.End(); b.End(); b.End();
            b.Begin("stbl");
            b.Begin("stsd"); b.U32(0); b.U32(1);
            b.Bytes(video ? TsDemux.Avc1(320, 180, TestSps(), TestPps) : TsDemux.Mp4a(2, 48000, new byte[] { 0x11, 0x90 }));
            b.End();
            foreach (string e in new string[] { "stts", "stsc", "stco" }) { b.Begin(e); b.U32(0); b.U32(0); b.End(); }
            b.Begin("stsz"); b.U32(0); b.U32(0); b.U32(0); b.End();
            b.End(); b.End(); b.End(); b.End();
            b.Begin("mvex"); b.Begin("trex"); b.U32(0); b.U32(id); b.U32(1); b.U32(0); b.U32(0); b.U32(0); b.End(); b.End();
            b.End();
            return b.ToArray();
        }

        // Fragmento: moof (tfhd con base en moof, tfdt, trun con tamaños, duraciones y banderas) + mdat.
        static byte[] FSeg(uint seq, uint id, long dts, int count, uint dur, bool video, int tag)
        {
            List<byte[]> samples = new List<byte[]>();
            for (int i = 0; i < count; i++)
            {
                byte[] s = new byte[40 + (i * 13 + tag) % 50];
                for (int k = 0; k < s.Length; k++) s[k] = (byte)(tag * 31 + i * 7 + k);
                samples.Add(s);
            }
            Func<int, byte[]> moof = delegate(int dataOffset)
            {
                BoxBuilder b = new BoxBuilder();
                b.Begin("moof");
                b.Begin("mfhd"); b.U32(0); b.U32(seq); b.End();
                b.Begin("traf");
                b.Begin("tfhd"); b.U32(0x020000); b.U32(id); b.End();
                b.Begin("tfdt"); b.U32(0x01000000); b.U64((ulong)dts); b.End();
                b.Begin("trun"); b.U32(0x000701); b.U32((uint)count); b.U32((uint)dataOffset);
                for (int i = 0; i < count; i++)
                {
                    b.U32(dur); b.U32((uint)samples[i].Length);
                    b.U32(!video || i == 0 ? 0x02000000u : 0x01010000u);
                }
                b.End(); b.End(); b.End();
                return b.ToArray();
            };
            int moofSize = moof(0).Length;
            MemoryStream o = new MemoryStream();
            byte[] mf = moof(moofSize + 8);
            o.Write(mf, 0, mf.Length);
            int total = 8; foreach (byte[] s in samples) total += s.Length;
            o.Write(new byte[] { (byte)(total >> 24), (byte)(total >> 16), (byte)(total >> 8), (byte)total }, 0, 4);
            o.Write(Encoding.ASCII.GetBytes("mdat"), 0, 4);
            foreach (byte[] s in samples) o.Write(s, 0, s.Length);
            return o.ToArray();
        }

        static string PutDash(string dir, int segs)
        {
            S.Put(dir + "/v/init.mp4", FInit(1, true, 12800), "video/mp4");
            S.Put(dir + "/a/init.mp4", FInit(1, false, 48000), "audio/mp4");
            for (int i = 0; i < segs; i++)
            {
                S.Put(dir + "/v/" + (i + 1).ToString("D3") + ".m4s", FSeg((uint)i + 1, 1, i * 50 * 512L, 50, 512, true, i), "video/mp4");
                S.Put(dir + "/a/" + (i + 1).ToString("D3") + ".m4s", FSeg((uint)i + 1, 1, i * 94 * 1024L, 94, 1024, false, 100 + i), "audio/mp4");
            }
            string mpd = "<?xml version=\"1.0\"?><MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT" + (segs * 2) + "S\"><Period>" +
                "<AdaptationSet mimeType=\"video/mp4\" contentType=\"video\"><SegmentTemplate timescale=\"1000\" duration=\"2000\" startNumber=\"1\" media=\"$RepresentationID$/$Number%03d$.m4s\" initialization=\"$RepresentationID$/init.mp4\"/>" +
                "<Representation id=\"v\" bandwidth=\"500000\" width=\"320\" height=\"180\" codecs=\"avc1.42c01e\"/></AdaptationSet>" +
                "<AdaptationSet mimeType=\"audio/mp4\" contentType=\"audio\" lang=\"es\"><SegmentTemplate timescale=\"1000\" duration=\"2000\" startNumber=\"1\" media=\"$RepresentationID$/$Number%03d$.m4s\" initialization=\"$RepresentationID$/init.mp4\"/>" +
                "<Representation id=\"a\" bandwidth=\"96000\" codecs=\"mp4a.40.2\"/></AdaptationSet></Period></MPD>";
            return S.Put(dir + "/manifest.mpd", mpd, "application/dash+xml");
        }

        static void TM_DashMerge()
        {
            DownloadManager m = NewMgr();
            string url = PutDash("dash1", 4);
            DownloadItem it = AddMedia(m, url, "dash", "dash.mp4", "v", null, true);
            WaitEnd(it, 30);
            Check(it.Status == DownloadStatus.Complete, "Estado: " + it.Status + " " + it.Error);
            List<MediaTrackData> t = Tracks(it.FilePath);
            Check(t.Count == 2, "Pistas: " + t.Count);
            MediaTrackData v = t[0].IsVideo ? t[0] : t[1], a = t[0].IsVideo ? t[1] : t[0];
            Check(v.Samples.Count == 200 && v.Timescale == 12800 && v.TotalDuration == 200 * 512, "Vídeo: " + v.Samples.Count + " " + v.TotalDuration);
            Check(v.Samples.FindAll(delegate(MediaSample s) { return s.Sync; }).Count == 4, "Imágenes clave");
            Check(a.Samples.Count == 4 * 94 && a.Timescale == 48000, "Audio: " + a.Samples.Count);
            // Los datos de cada muestra son los del fragmento original (trozo 3, muestra 7 del vídeo).
            byte[] got = SampleBytes(it.FilePath, v.Samples[2 * 50 + 7]);
            Check(got.Length == 40 + (7 * 13 + 2) % 50 && got[0] == (byte)(2 * 31 + 7 * 7) && got[5] == (byte)(2 * 31 + 7 * 7 + 5), "Contenido de una muestra de vídeo");
            byte[] ga = SampleBytes(it.FilePath, a.Samples[94 + 3]);
            Check(ga[0] == unchecked((byte)(101 * 31 + 3 * 7)), "Contenido de una muestra de audio");
            Check(v.Width == 320 && v.Height == 180, "tkhd: " + v.Width + "x" + v.Height);
        }

        static void TM_DashAudioOnlyAndSeparate()
        {
            DownloadManager m = NewMgr();
            string url = PutDash("dash2", 3);
            DownloadItem au = AddMedia(m, url, "dash", "pista.m4a", null, "a", true);
            WaitEnd(au, 30);
            Check(au.Status == DownloadStatus.Complete && Path.GetExtension(au.FilePath) == ".m4a", "Solo audio: " + au.Status + " " + au.FilePath + " " + au.Error);
            List<MediaTrackData> t = Tracks(au.FilePath);
            Check(t.Count == 1 && !t[0].IsVideo && t[0].Samples.Count == 3 * 94, "Pistas del .m4a");

            m.Config.MergeAudioVideo = false;
            DownloadItem sep = AddMedia(m, url, "dash", "aparte.mp4", "v", null, true);
            WaitEnd(sep, 30);
            Check(sep.Status == DownloadStatus.Complete, "Sin mezclar: " + sep.Status + " " + sep.Error);
            Check(Tracks(sep.FilePath).Count == 1 && Tracks(sep.FilePath)[0].IsVideo, "El vídeo lleva audio");
            string audio = Path.Combine(Path.GetDirectoryName(sep.FilePath), "aparte (audio).m4a");
            Check(File.Exists(audio) && Tracks(audio).Count == 1, "Falta el audio aparte");
            Check(sep.Description != null && sep.Description.Contains("aparte (audio).m4a"), "Nota: " + sep.Description);
        }

        // ------------------------------------------------------------------ WebM

        static byte[] El(uint id, byte[] data)
        {
            MemoryStream o = new MemoryStream();
            if (id > 0xFFFFFF) o.WriteByte((byte)(id >> 24));
            if (id > 0xFFFF) o.WriteByte((byte)(id >> 16));
            if (id > 0xFF) o.WriteByte((byte)(id >> 8));
            o.WriteByte((byte)id);
            o.WriteByte(0x01); for (int i = 6; i >= 0; i--) o.WriteByte((byte)((long)data.Length >> (i * 8)));
            o.Write(data, 0, data.Length);
            return o.ToArray();
        }
        static byte[] EU(uint id, ulong v) { return El(id, new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v }); }
        static byte[] Cat(params byte[][] parts) { MemoryStream o = new MemoryStream(); foreach (byte[] p in parts) o.Write(p, 0, p.Length); return o.ToArray(); }

        static byte[] MakeWebm(int trackNo, ulong uid, string codec, int type, long[] clusterTimes, byte tag)
        {
            List<byte[]> parts = new List<byte[]>();
            parts.Add(El(0x1549A966, EU(0x2AD7B1, 1000000)));
            parts.Add(El(0x1654AE6B, El(0xAE, Cat(EU(0xD7, (ulong)trackNo), EU(0x73C5, uid), EU(0x83, (ulong)type), El(0x86, Encoding.ASCII.GetBytes(codec))))));
            foreach (long tc in clusterTimes)
            {
                byte[] block = { (byte)(0x80 | trackNo), 0, 0, 0x80, tag, (byte)tc, 0x55 };
                parts.Add(El(0x1F43B675, Cat(EU(0xE7, (ulong)tc), El(0xA3, block), El(0xA0, El(0xA1, block)))));
            }
            return Cat(El(0x1A45DFA3, El(0x4282, Encoding.ASCII.GetBytes("webm"))), El(0x18538067, Cat(parts.ToArray())));
        }

        // Lector mínimo de EBML para comprobar el resultado.
        static List<long[]> EbmlChildren(byte[] d, long start, long end)
        {
            List<long[]> r = new List<long[]>(); // {id, inicioDatos, tamaño, inicio}
            long p = start;
            while (p < end)
            {
                long s0 = p;
                int b = d[p]; int len = 1; while ((b & (0x80 >> (len - 1))) == 0) len++;
                long id = 0; for (int i = 0; i < len; i++) id = id << 8 | d[p + i]; p += len;
                b = d[p]; int sl = 1; while ((b & (0x80 >> (sl - 1))) == 0) sl++;
                long size = b & (0xFF >> sl); for (int i = 1; i < sl; i++) size = size << 8 | d[p + i]; p += sl;
                r.Add(new long[] { id, p, size, s0 });
                p += size;
            }
            return r;
        }

        static void TM_WebmMerge()
        {
            string dir = Path.Combine(Dl, "webm-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(dir);
            string v = Path.Combine(dir, "v.webm"), a = Path.Combine(dir, "a.webm"), o = Path.Combine(dir, "o.webm");
            File.WriteAllBytes(v, MakeWebm(1, 77, "V_VP9", 1, new long[] { 0, 2000, 4000 }, 0xAA));
            File.WriteAllBytes(a, MakeWebm(1, 77, "A_OPUS", 2, new long[] { 0, 1000, 2000, 3000, 4000, 5000 }, 0xBB));
            MkvMerge.Merge(v, a, o, null);
            byte[] d = File.ReadAllBytes(o);
            List<long[]> top = EbmlChildren(d, 0, d.Length);
            Check(top.Count == 2 && top[0][0] == 0x1A45DFA3 && top[1][0] == 0x18538067, "Cabecera/segmento");
            List<long[]> seg = EbmlChildren(d, top[1][1], top[1][1] + top[1][2]);
            Check(seg[0][0] == 0x114D9B74 && seg[1][0] == 0x1549A966 && seg[2][0] == 0x1654AE6B && seg[seg.Count - 1][0] == 0x1C53BB6B, "Orden SeekHead/Info/Tracks/.../Cues");
            List<long[]> tracks = EbmlChildren(d, seg[2][1], seg[2][1] + seg[2][2]);
            Check(tracks.Count == 2, "TrackEntry: " + tracks.Count);
            List<long> numbers = new List<long>(), uids = new List<long>();
            foreach (long[] te in tracks)
                foreach (long[] c in EbmlChildren(d, te[1], te[1] + te[2]))
                {
                    if (c[0] == 0xD7) numbers.Add(d[c[1] + c[2] - 1]);
                    if (c[0] == 0x73C5) uids.Add(d[c[1] + c[2] - 1]);
                }
            Check(numbers.Count == 2 && numbers[0] == 1 && numbers[1] == 2 && uids[0] != uids[1], "Números/UID de pista");
            List<long> times = new List<long>(); int audioBlocks = 0, videoBlocks = 0;
            foreach (long[] c in seg)
            {
                if (c[0] != 0x1F43B675) continue;
                foreach (long[] e in EbmlChildren(d, c[1], c[1] + c[2]))
                {
                    if (e[0] == 0xE7) times.Add(d[e[1] + e[2] - 2] << 8 | d[e[1] + e[2] - 1]);
                    long blk = e[0] == 0xA3 ? e[1] : -1;
                    if (e[0] == 0xA0) blk = EbmlChildren(d, e[1], e[1] + e[2])[0][1];
                    if (blk < 0) continue;
                    if (d[blk + 4] == 0xBB) { audioBlocks++; Check(d[blk] == 0x82, "Bloque de audio sin renumerar"); }
                    else { videoBlocks++; Check(d[blk] == 0x81, "Bloque de vídeo cambiado"); }
                }
            }
            Check(times.Count == 9 && audioBlocks == 12 && videoBlocks == 6, "Clusters/bloques: " + times.Count + " " + audioBlocks + " " + videoBlocks);
            for (int i = 1; i < times.Count; i++) Check(times[i] >= times[i - 1], "Clusters fuera de orden");
            // La SeekHead apunta de verdad a Cues.
            long segData = top[1][1];
            List<long[]> cues = EbmlChildren(d, seg[seg.Count - 1][1], seg[seg.Count - 1][1] + seg[seg.Count - 1][2]);
            Check(cues.Count == 3, "Puntos de índice (uno por cluster de vídeo): " + cues.Count);
            List<long[]> seeks = EbmlChildren(d, seg[0][1], seg[0][1] + seg[0][2]);
            long cuesPos = 0;
            foreach (long[] c in EbmlChildren(d, seeks[2][1], seeks[2][1] + seeks[2][2])) if (c[0] == 0x53AC) for (int i = 0; i < c[2]; i++) cuesPos = cuesPos << 8 | d[c[1] + i];
            Check(segData + cuesPos == seg[seg.Count - 1][3], "SeekHead -> Cues");
        }

        // ------------------------------------------------------------------ panel

        static object[] Arr(object o) { System.Collections.ArrayList l = o as System.Collections.ArrayList; return l != null ? l.ToArray() : (object[])o; }

        static void TM_Probe()
        {
            string master = PutHls("hls-probe", 3);
            string json = NativeHost.ProbeReply("{\"type\":\"probe\",\"url\":\"" + master + "\",\"cookies\":\"a=1\",\"referrer\":\"http://pagina.local/\"}");
            Dictionary<string, object> r = AppConfig.NewSerializer().Deserialize<Dictionary<string, object>>(json);
            Check(r["ok"].Equals(true) && (string)r["kind"] == "hls", "Respuesta: " + json);
            Check(Math.Abs(Convert.ToDouble(r["duration"]) - 6.0) < 1e-6, "Duración: " + r["duration"]);
            object[] items = Arr(r["items"]);
            Check(items.Length == 2, "Calidades: " + items.Length);
            Dictionary<string, object> best = (Dictionary<string, object>)items[0];
            Check(Convert.ToInt32(best["height"]) == 180 && (string)best["ext"] == ".mp4" && Convert.ToInt64(best["size"]) == 900000 / 8 * 6, "Mejor calidad: " + new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(best));
            object[] children = Arr(r["children"]);
            Check(children.Length == 2 && ((string)children[0]).EndsWith("/hls-probe/v.m3u8"), "Listas hijas");

            string dash = PutDash("dash-probe", 2);
            r = AppConfig.NewSerializer().Deserialize<Dictionary<string, object>>(NativeHost.ProbeReply("{\"type\":\"probe\",\"url\":\"" + dash + "\"}"));
            items = Arr(r["items"]);
            Check(items.Length == 2 && (string)((Dictionary<string, object>)items[1])["kind"] == "audio" && (string)((Dictionary<string, object>)items[1])["ext"] == ".m4a", "DASH: vídeo + solo audio");
            Check((string)((Dictionary<string, object>)items[0])["audio"] == "a", "La calidad lleva su audio");
            string err = NativeHost.ProbeReply("{\"type\":\"probe\",\"url\":\"" + S.Base + "/s/no-existe.m3u8\"}");
            Check(err.Contains("\"ok\":false"), "Lista inexistente: " + err);
        }

        static void TM_PanelOptions()
        {
            UseFreshHome("media-opts");
            AppConfig c = Cfg();
            Check(c.MediaPanel && c.MergeAudioVideo && !c.MediaPanelMini, "Valores por defecto (como IDM)");
            using (MediaPanelForm f = new MediaPanelForm(c))
            {
                IntPtr h = f.Handle;
                f.Set(true, true, 2048, 100, "youtube.com\r\n*.ejemplo.com", false);
                f.Apply();
            }
            Check(c.MediaPanelMini && c.MediaPanelMinVideoKB == 2048 && c.MediaPanelMinAudioKB == 100 && !c.MergeAudioVideo, "No se guardaron las opciones");
            Check(c.MediaPanelExcludeSites == "youtube.com *.ejemplo.com", "Sitios: " + c.MediaPanelExcludeSites);
            c.Save();
            string json = NativeHost.ConfigReply();
            Check(json.Contains("\"mediaPanel\":true") && json.Contains("\"mediaMini\":true") && json.Contains("\"minVideoKB\":2048") && json.Contains("\"*.ejemplo.com\""), "getconfig: " + json);
            // Una configuración antigua (sin estas claves) recibe los valores de IDM.
            File.WriteAllText(AppConfig.ConfigPath, "{\"MaxConnections\":8}");
            AppConfig old = AppConfig.Load();
            Check(old.MediaPanel && old.MergeAudioVideo && old.MediaPanelMinVideoKB == 1024, "Config antigua");
        }
    }
}
