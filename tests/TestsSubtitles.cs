using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace IDMClone.Tests
{
    // Subtítulos del panel "Descargar este video" (como IDM): pistas SUBTITLES de HLS (trozos WebVTT que se juntan),
    // AdaptationSet de texto de DASH (WebVTT y TTML -> SRT), y archivos sueltos (<track> del vídeo) por el mensaje
    // "download" de la extensión. Se guardan junto al vídeo: "Nombre.<idioma>.vtt/.srt".
    public static partial class TestMain
    {
        static void RunSubtitles()
        {
            Run("subtítulos: lectura de HLS (SUBTITLES) y DASH (text/vtt, TTML, stpp en MP4)", TS_Parse);
            Run("subtítulos: unir trozos WebVTT (cabeceras, X-TIMESTAMP-MAP, repetidos) y pasar a SRT", TS_JoinAndSrt);
            Run("subtítulos: TTML -> SRT (reloj, ticks, imágenes, <br/>, cursiva)", TS_Ttml);
            Run("subtítulos: sonda del panel (elementos de subtítulos y listas hijas)", TS_Probe);
            Run("subtítulos: HLS -> un solo .vtt junto al vídeo (y .srt con la opción)", TS_HlsDownload);
            Run("subtítulos: DASH WebVTT y TTML", TS_DashDownload);
            Run("subtítulos: <track> del vídeo por el mensaje de la extensión (carpeta del vídeo, no es duplicado)", TS_TrackMessage);
            Run("subtítulos y trozos de vídeo: 429 espera lo que pide el servidor (Retry-After), no \"Reintentar cada\"", TS_Busy429);
            Run("subtítulos y trozos de vídeo: sin conexión directa se bajan por el proxy del navegador", TS_BrowserProxy);
        }

        static void TS_Busy429()
        {
            DownloadManager m = NewMgr();
            m.Config.RetryCount = 3; m.Config.RetryDelaySec = 10; // con la espera normal, dos 429 costarían 20 s
            string master = HlsSubsMaster("subs-busy");
            S.BusyStatic("subs-busy/subs/es/1.vtt", 2, "1");
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            DownloadItem it = AddSubs(m, master, "Ocupado.es.vtt", S.Base + "/s/subs-busy/subs/es.m3u8");
            WaitEnd(it, 30);
            Check(it.Status == DownloadStatus.Complete, "Subtítulos: " + it.Status + " " + it.Error);
            Check(sw.Elapsed.TotalSeconds < 7, "Subtítulos: tardó " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s (debía esperar 1 s por 429)");
            CheckEq(3, S.StaticHits("subs-busy/subs/es/1.vtt"), "Peticiones del trozo con 429");

            // Pide esperar 10 min: error enseguida con el motivo (se podrá reanudar), sin gastar los reintentos.
            S.BusyStatic("subs-busy/subs/es/0.vtt", 5, "600");
            sw = System.Diagnostics.Stopwatch.StartNew();
            DownloadItem ban = AddSubs(m, master, "Bloqueado.es.vtt", S.Base + "/s/subs-busy/subs/es.m3u8");
            WaitEnd(ban, 30);
            Check(ban.Status == DownloadStatus.Error && ban.Error != null && ban.Error.Contains("pide esperar"), "Retry-After largo: " + ban.Status + " " + ban.Error);
            Check(sw.Elapsed.TotalSeconds < 4, "Retry-After largo: tardó " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");

            // Trozos de vídeo HLS (varias conexiones): lo mismo.
            string hls = PutHls("hls-busy", 3);
            S.BusyStatic("hls-busy/seg1.ts", 2, "1");
            sw = System.Diagnostics.Stopwatch.StartNew();
            DownloadItem v = AddMedia(m, hls, "hls", "Ocupado.mp4", null, null, true);
            WaitEnd(v, 30);
            Check(v.Status == DownloadStatus.Complete, "Vídeo: " + v.Status + " " + v.Error);
            Check(sw.Elapsed.TotalSeconds < 7, "Vídeo: tardó " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
            CheckEq(3, S.StaticHits("hls-busy/seg1.ts"), "Peticiones del trozo de vídeo con 429");
        }

        static void TS_BrowserProxy()
        {
            DownloadManager m = NewMgr();
            m.Config.ProxyMode = "none";
            HlsSubsMaster("subs-px");
            PutHls("hls-px", 3);
            // descargas.invalid: no se llega directamente; el TestServer hace también de proxy (cuenta lo que le llega por él).
            string via = "http://descargas.invalid:" + S.Port + "/s/";
            AddOptions o = new AddOptions();
            o.Url = via + "subs-px/master.m3u8"; o.MediaKind = DownloadEngine.SubtitleKind; o.FileName = "Proxy.es.vtt";
            o.MediaVideo = via + "subs-px/subs/es.m3u8"; o.StartNow = true; o.Folder = Dl; o.BrowserProxy = "127.0.0.1:" + S.Port;
            DownloadItem it = m.Add(o);
            WaitEnd(it, 30);
            Check(it.Status == DownloadStatus.Complete, "Subtítulos: " + it.Status + " " + it.Error);
            Check(S.StaticProxied("subs-px/master.m3u8") >= 1 && S.StaticProxied("subs-px/subs/es/1.vtt") >= 1, "Subtítulos: no fueron por el proxy del navegador");

            o = new AddOptions();
            o.Url = via + "hls-px/master.m3u8"; o.MediaKind = "hls"; o.FileName = "Proxy.mp4";
            o.StartNow = true; o.Folder = Dl; o.BrowserProxy = "127.0.0.1:" + S.Port;
            DownloadItem v = m.Add(o);
            WaitEnd(v, 30);
            Check(v.Status == DownloadStatus.Complete, "Vídeo: " + v.Status + " " + v.Error);
            Check(S.StaticProxied("hls-px/seg2.ts") >= 1, "Vídeo: los trozos no fueron por el proxy del navegador");

            // La lista sí llega directa y solo los trozos (en otro servidor) necesitan el proxy: cambia en el trozo.
            PutHls("hls-px2", 3);
            S.Put("hls-px2/v.m3u8", HlsMedia(3, via + "hls-px2/seg{0}.ts", null), "application/vnd.apple.mpegurl");
            o = new AddOptions();
            o.Url = S.Base + "/s/hls-px2/master.m3u8"; o.MediaKind = "hls"; o.FileName = "ProxyTrozos.mp4";
            o.StartNow = true; o.Folder = Dl; o.BrowserProxy = "127.0.0.1:" + S.Port;
            DownloadItem v2 = m.Add(o);
            WaitEnd(v2, 30);
            Check(v2.Status == DownloadStatus.Complete, "Trozos: " + v2.Status + " " + v2.Error);
            Check(S.StaticProxied("hls-px2/v.m3u8") == 0 && S.StaticProxied("hls-px2/seg0.ts") >= 1, "Trozos: la lista debía ir directa y los trozos por el proxy (" +
                  S.StaticProxied("hls-px2/v.m3u8") + ", " + S.StaticProxied("hls-px2/seg0.ts") + ")");

            // Sin "Usar el proxy del navegador si falla" (Opciones > Proxy/Socks): error, no se desvía.
            m.Config.UseBrowserProxyOnError = false;
            o.FileName = "SinProxy.mp4";
            DownloadItem no = m.Add(o);
            WaitEnd(no, 30);
            Check(no.Status == DownloadStatus.Error, "Con la opción quitada: " + no.Status);
        }

        // Trozo WebVTT de una lista HLS, con su cabecera y su X-TIMESTAMP-MAP.
        static string VttSeg(long mpegts, string cues)
        {
            return "WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:" + mpegts + ",LOCAL:00:00:00.000\n\n" + cues;
        }

        static string HlsSubsMaster(string dir)
        {
            S.Put(dir + "/v.m3u8", HlsMedia(3, "seg{0}.ts", null), "application/vnd.apple.mpegurl");
            // Español: todos los trozos con el mismo mapa (tiempos ya absolutos); una frase cruza el corte y se repite.
            S.Put(dir + "/subs/es/0.vtt", VttSeg(900000, "1\n00:00:00.500 --> 00:00:01.500\nHola\n\n2\n00:00:01.800 --> 00:00:02.400 line:90%\n<i>que tal</i>\n"), "text/vtt");
            S.Put(dir + "/subs/es/1.vtt", VttSeg(900000, "2\n00:00:01.800 --> 00:00:02.400 line:90%\n<i>que tal</i>\n\nNOTE comentario\n\n3\n00:00:03.000 --> 00:00:03.900\n<v Ana>Adiós &amp; gracias</v>\n"), "text/vtt");
            S.Put(dir + "/subs/es/2.vtt", "WEBVTT\n\n", "text/vtt"); // trozo sin frases
            S.Put(dir + "/subs/es.m3u8", "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\nes/0.vtt\n#EXTINF:2,\nes/1.vtt\n#EXTINF:2,\nes/2.vtt\n#EXT-X-ENDLIST\n", "application/vnd.apple.mpegurl");
            // Inglés: cada trozo con su mapa y tiempos relativos al trozo.
            S.Put(dir + "/subs/en/0.vtt", VttSeg(900000, "00:00.500 --> 00:01.000\nHello\n"), "text/vtt");
            S.Put(dir + "/subs/en/1.vtt", VttSeg(900000 + 2 * 90000, "00:00.250 --> 00:00.750\nBye\n"), "text/vtt");
            S.Put(dir + "/subs/en.m3u8", "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\nen/0.vtt\n#EXTINF:2,\nen/1.vtt\n#EXT-X-ENDLIST\n", "application/vnd.apple.mpegurl");
            return S.Put(dir + "/master.m3u8",
                "#EXTM3U\n" +
                "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"Español\",LANGUAGE=\"es\",DEFAULT=YES,AUTOSELECT=YES,URI=\"subs/es.m3u8\"\n" +
                "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"English\",LANGUAGE=\"en\",URI=\"subs/en.m3u8\"\n" +
                "#EXT-X-MEDIA:TYPE=CLOSED-CAPTIONS,GROUP-ID=\"cc\",NAME=\"CC\",INSTREAM-ID=\"CC1\"\n" +
                "#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=320x180,CODECS=\"avc1.42c01e,mp4a.40.2\",SUBTITLES=\"subs\"\nv.m3u8\n",
                "application/vnd.apple.mpegurl");
        }

        const string TtmlDoc = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:ttp=\"http://www.w3.org/ns/ttml#parameter\" xmlns:tts=\"http://www.w3.org/ns/ttml#styling\" ttp:tickRate=\"10000000\" ttp:frameRate=\"25\">" +
            "<body><div>" +
            "<p begin=\"00:00:01.000\" end=\"00:00:02.500\">Primera<br/>línea  dos</p>\n" +
            "<p begin=\"30000000t\" dur=\"10000000t\"><span tts:fontStyle=\"italic\">Cursiva</span> &amp; normal</p>" +
            "<p begin=\"00:00:05:12\" end=\"6.5s\">Imágenes</p>" +
            "</div><div begin=\"10s\"><p begin=\"500ms\" end=\"1s\">Desplazada</p></div></body></tt>";

        static string DashSubsMpd(string dir)
        {
            S.Put(dir + "/subs-es.vtt", "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHola DASH\n", "text/vtt");
            S.Put(dir + "/subs-fr.ttml", TtmlDoc, "application/ttml+xml");
            string mpd = "<?xml version=\"1.0\"?><MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT4S\"><Period>" +
                "<AdaptationSet mimeType=\"video/mp4\" contentType=\"video\"><SegmentTemplate timescale=\"1000\" duration=\"2000\" startNumber=\"1\" media=\"$RepresentationID$/$Number%03d$.m4s\" initialization=\"$RepresentationID$/init.mp4\"/>" +
                "<Representation id=\"v\" bandwidth=\"500000\" width=\"320\" height=\"180\" codecs=\"avc1.42c01e\"/></AdaptationSet>" +
                "<AdaptationSet mimeType=\"text/vtt\" lang=\"es\"><Role schemeIdUri=\"urn:mpeg:dash:role:2011\" value=\"subtitle\"/>" +
                "<Representation id=\"sub-es\" bandwidth=\"256\"><BaseURL>subs-es.vtt</BaseURL></Representation></AdaptationSet>" +
                "<AdaptationSet mimeType=\"application/ttml+xml\" lang=\"fr\"><Label>Français</Label>" +
                "<Representation id=\"sub-fr\" bandwidth=\"256\"><BaseURL>subs-fr.ttml</BaseURL></Representation></AdaptationSet>" +
                "<AdaptationSet mimeType=\"application/mp4\" contentType=\"text\" lang=\"de\"><SegmentTemplate timescale=\"1000\" duration=\"2000\" media=\"de/$Number$.m4s\" initialization=\"de/init.mp4\"/>" +
                "<Representation id=\"sub-de\" codecs=\"stpp\" bandwidth=\"256\"/></AdaptationSet>" +
                "<AdaptationSet mimeType=\"text/vtt\" lang=\"it\"><SegmentTemplate media=\"it/$Number$.vtt\"/><Representation id=\"roto\"/></AdaptationSet>" +
                "</Period></MPD>";
            return S.Put(dir + "/manifest.mpd", mpd, "application/dash+xml");
        }

        static void TS_Parse()
        {
            string master = HlsSubsMaster("subs-parse");
            MediaManifest m = MediaManifest.Parse(Fetch(master), master);
            Check(m.Videos.Count == 1 && m.Audios.Count == 0, "Calidades/audios: " + m.Videos.Count + "/" + m.Audios.Count);
            Check(m.Subtitles.Count == 2, "Subtítulos (sin los CLOSED-CAPTIONS): " + m.Subtitles.Count);
            Check(m.Subtitles[0].Language == "es" && m.Subtitles[0].Name == "Español" && m.Subtitles[0].Default && m.Subtitles[0].Key.EndsWith("/subs-parse/subs/es.m3u8"), "Pista es");
            Check(m.Subtitles[1].SubtitleFormat == "vtt", "Formato HLS");
            // La lista de unos subtítulos sola no es un vídeo.
            MediaManifest lone = MediaManifest.Parse(Fetch(S.Base + "/s/subs-parse/subs/es.m3u8"), S.Base + "/s/subs-parse/subs/es.m3u8");
            Check(lone.Videos.Count == 0 && lone.Subtitles.Count == 1 && lone.Subtitles[0].Playlist.Segments.Count == 3, "Lista de subtítulos suelta");

            string mpdUrl = DashSubsMpd("subs-parse-dash");
            MediaManifest d = Dash.Parse(Fetch(mpdUrl), mpdUrl);
            Check(d.Videos.Count == 1, "DASH: vídeo");
            CheckEq(3, d.Subtitles.Count, "DASH: subtítulos (el que no tiene trozos calculables se deja fuera)");
            MediaTrack es = d.FindSubtitle("sub-es"), fr = d.FindSubtitle("sub-fr"), de = d.FindSubtitle("sub-de");
            Check(es != null && es.Language == "es" && es.SubtitleFormat == "vtt" && es.Playlist.Segments.Count == 1 && es.Playlist.Segments[0].Url.EndsWith("/subs-es.vtt"), "DASH es");
            Check(fr != null && fr.SubtitleFormat == "ttml" && fr.Name == "Français", "DASH fr (TTML)");
            Check(de != null && de.SubtitleFormat == "mp4" && de.Playlist.Init != null, "DASH de (stpp en MP4)");
        }

        static void TS_JoinAndSrt()
        {
            List<string> segs = new List<string>();
            segs.Add(VttSeg(900000, "a\n00:00:00.500 --> 00:00:01.500\nUno\n\n00:00:01.900 --> 00:00:02.000\nPartida\n"));
            segs.Add("WEBVTT\r\nX-TIMESTAMP-MAP=LOCAL:00:00:00.000,MPEGTS:900000\r\n\r\n00:00:02.000 --> 00:00:02.600\r\nPartida\r\n\r\n00:00:00.500 --> 00:00:01.500\r\nUno\r\n");
            List<SubtitleCue> cues = Subtitles.JoinVtt(segs);
            CheckEq(2, cues.Count, "Frases tras quitar la repetida y juntar la partida");
            Check(cues[0].Id == "a" && cues[0].Text == "Uno" && Math.Abs(cues[1].Start - 1.9) < 1e-6 && Math.Abs(cues[1].End - 2.6) < 1e-6, "Frases: " + Subtitles.WriteVtt(cues));
            // Mapas distintos: tiempos relativos a cada trozo (2 s más en el segundo).
            List<SubtitleCue> rel = Subtitles.JoinVtt(new string[] { VttSeg(900000, "00:00.500 --> 00:01.000\nA\n"), VttSeg(1080000, "00:00.250 --> 00:00.750\nB\n") });
            Check(rel.Count == 2 && Math.Abs(rel[1].Start - 2.25) < 1e-6 && Math.Abs(rel[1].End - 2.75) < 1e-6, "Desplazamiento X-TIMESTAMP-MAP: " + Subtitles.WriteVtt(rel));
            string vtt = Subtitles.WriteVtt(rel);
            Check(vtt.StartsWith("WEBVTT\n\n") && vtt.Contains("00:00:02.250 --> 00:00:02.750\nB\n") && vtt.IndexOf("WEBVTT", 1) < 0, "VTT unido: " + vtt);
            // SRT: numeradas, coma decimal, solo <i>/<b>/<u>, entidades.
            List<SubtitleCue> one = Subtitles.ParseVtt("WEBVTT\n\nx\n01:02:03.004 --> 01:02:04.000 align:start\n<v Ana><c.amarillo>Hola</c> <i>tú</i> &amp; <00:00:01.000>yo</v>\n");
            string srt = Subtitles.WriteSrt(one);
            CheckEq("1\r\n01:02:03,004 --> 01:02:04,000\r\nHola <i>tú</i> & yo\r\n\r\n", srt, "SRT");
            Check(Subtitles.DetectFormat("﻿1\r\n00:00:01,000 --> 00:00:02,000\r\nhola\r\n") == "srt" && Subtitles.DetectFormat("WEBVTT") == "vtt" && Subtitles.DetectFormat(TtmlDoc) == "ttml" && Subtitles.DetectFormat("<html>") == null, "Detectar el formato");
            // Un .srt en Windows-1252 se lee bien.
            Check(Subtitles.DecodeText(Encoding.GetEncoding(1252).GetBytes("canción")) == "canción", "Windows-1252");
        }

        static void TS_Ttml()
        {
            List<SubtitleCue> c = Subtitles.ParseTtml(TtmlDoc);
            CheckEq(4, c.Count, "Frases TTML");
            Check(c[0].Text == "Primera\nlínea dos" && c[0].Start == 1 && c[0].End == 2.5, "Reloj y <br/>: " + c[0].Text);
            Check(c[1].Text == "<i>Cursiva</i> & normal" && c[1].Start == 3 && c[1].End == 4, "Ticks y cursiva: " + c[1].Text + " " + c[1].Start + "-" + c[1].End);
            Check(Math.Abs(c[2].Start - 5.48) < 1e-6 && c[2].End == 6.5, "Imágenes (25 por segundo): " + c[2].Start);
            Check(c[3].Start == 10.5 && c[3].End == 11, "Inicio del div: " + c[3].Start);
            string ext;
            byte[] srt = Subtitles.Build(new List<byte[]> { Encoding.UTF8.GetBytes(TtmlDoc) }, false, out ext);
            string s = Encoding.UTF8.GetString(srt);
            Check(ext == ".srt" && s.StartsWith("﻿1\r\n00:00:01,000 --> 00:00:02,500\r\nPrimera\r\nlínea dos\r\n\r\n2\r\n"), "TTML -> SRT: " + s);
        }

        static string Fetch(string url)
        {
            using (System.Net.WebClient wc = new System.Net.WebClient()) { wc.Encoding = Encoding.UTF8; return wc.DownloadString(url); }
        }

        static Dictionary<string, object> Dict(object o) { return (Dictionary<string, object>)o; }

        static void TS_Probe()
        {
            string master = HlsSubsMaster("subs-probe");
            Dictionary<string, object> r = AppConfig.NewSerializer().Deserialize<Dictionary<string, object>>(NativeHost.ProbeReply("{\"type\":\"probe\",\"url\":\"" + master + "\"}"));
            object[] items = Arr(r["items"]);
            CheckEq(3, items.Length, "Elementos (1 calidad + 2 subtítulos)");
            Dictionary<string, object> es = Dict(items[1]);
            Check((string)es["kind"] == "subtitle" && (string)es["language"] == "es" && (string)es["name"] == "Español" && (string)es["ext"] == ".vtt" && ((string)es["subtitle"]).EndsWith("/subs-probe/subs/es.m3u8"), "Elemento es: " + new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(es));
            object[] children = Arr(r["children"]);
            bool hasEs = false;
            foreach (object c in children) if (((string)c).EndsWith("/subs-probe/subs/es.m3u8")) hasEs = true;
            Check(hasEs, "La lista de los subtítulos es hija (la extensión no la toma por otro vídeo)");
            // La lista de los subtítulos, si llega sola: sin calidades de vídeo.
            r = AppConfig.NewSerializer().Deserialize<Dictionary<string, object>>(NativeHost.ProbeReply("{\"type\":\"probe\",\"url\":\"" + S.Base + "/s/subs-probe/subs/en.m3u8\"}"));
            items = Arr(r["items"]);
            Check(items.Length == 1 && (string)Dict(items[0])["kind"] == "subtitle", "Lista suelta: " + items.Length);

            string mpd = DashSubsMpd("subs-probe-dash");
            r = AppConfig.NewSerializer().Deserialize<Dictionary<string, object>>(NativeHost.ProbeReply("{\"type\":\"probe\",\"url\":\"" + mpd + "\"}"));
            items = Arr(r["items"]);
            List<string> subs = new List<string>();
            foreach (object o in items) if ((string)Dict(o)["kind"] == "subtitle") subs.Add((string)Dict(o)["subtitle"] + (string)Dict(o)["ext"]);
            CheckEq("sub-es.vtt sub-fr.srt", string.Join(" ", subs.ToArray()), "DASH: subtítulos ofrecidos (TTML sale en SRT; el de MP4 no)");
        }

        static DownloadItem AddSubs(DownloadManager m, string url, string name, string key)
        {
            AddOptions o = new AddOptions();
            o.Url = url; o.MediaKind = DownloadEngine.SubtitleKind; o.FileName = name; o.MediaVideo = key; o.StartNow = true;
            o.Folder = Dl;
            return m.Add(o);
        }

        static void TS_HlsDownload()
        {
            string master = HlsSubsMaster("subs-hls");
            DownloadManager m = NewMgr();
            DownloadItem it = AddSubs(m, master, "Mi vídeo.es.vtt", S.Base + "/s/subs-hls/subs/es.m3u8");
            WaitEnd(it, 20);
            Check(it.Status == DownloadStatus.Complete, "Estado: " + it.Status + " " + it.Error);
            CheckEq("Mi vídeo.es.vtt", Path.GetFileName(it.FilePath), "Nombre");
            string vtt = File.ReadAllText(it.FilePath, Encoding.UTF8);
            CheckEq("WEBVTT\n\n1\n00:00:00.500 --> 00:00:01.500\nHola\n\n2\n00:00:01.800 --> 00:00:02.400 line:90%\n<i>que tal</i>\n\n3\n00:00:03.000 --> 00:00:03.900\n<v Ana>Adiós &amp; gracias</v>\n\n", vtt, "VTT unido");
            Check(it.Size == new FileInfo(it.FilePath).Length && it.Downloaded == it.Size, "Tamaño: " + it.Size + "/" + it.Downloaded);
            Check(!Directory.Exists(DownloadEngine.MediaWorkDir(it, m.Config)), "Queda la carpeta de trabajo");

            // Con "Convertir los subtítulos WebVTT a SRT": .srt (tiempos del inglés desplazados por su X-TIMESTAMP-MAP).
            m.Config.SubtitlesToSrt = true;
            DownloadItem en = AddSubs(m, master, "Mi vídeo.en.vtt", S.Base + "/s/subs-hls/subs/en.m3u8");
            WaitEnd(en, 20);
            Check(en.Status == DownloadStatus.Complete, "Estado (SRT): " + en.Status + " " + en.Error);
            CheckEq("Mi vídeo.en.srt", Path.GetFileName(en.FilePath), "Nombre (SRT)");
            CheckEq("1\r\n00:00:00,500 --> 00:00:01,000\r\nHello\r\n\r\n2\r\n00:00:02,250 --> 00:00:02,750\r\nBye\r\n\r\n", File.ReadAllText(en.FilePath, Encoding.UTF8), "SRT");

            // Un trozo que falta: error claro, sin archivo a medias.
            m.Config.RetryCount = 1;
            S.Put("subs-hls/subs/rota.m3u8", "#EXTM3U\n#EXTINF:2,\nes/0.vtt\n#EXTINF:2,\nno-existe.vtt\n#EXT-X-ENDLIST\n", "application/vnd.apple.mpegurl");
            DownloadItem bad = AddSubs(m, S.Base + "/s/subs-hls/subs/rota.m3u8", "Rota.vtt", null);
            WaitEnd(bad, 20);
            Check(bad.Status == DownloadStatus.Error && !string.IsNullOrEmpty(bad.Error), "Trozo inexistente: " + bad.Status + " " + bad.Error);
            Check(!File.Exists(Path.Combine(Dl, "Rota.vtt")) || new FileInfo(Path.Combine(Dl, "Rota.vtt")).Length == 0, "Quedó un archivo a medias");
        }

        static void TS_DashDownload()
        {
            string mpd = DashSubsMpd("subs-dash");
            DownloadManager m = NewMgr();
            DownloadItem es = AddSubs(m, mpd, "Clip.es.vtt", "sub-es");
            DownloadItem fr = AddSubs(m, mpd, "Clip.fr.ttml", "sub-fr");
            DownloadItem de = AddSubs(m, mpd, "Clip.de.vtt", "sub-de");
            WaitEnd(es, 20); WaitEnd(fr, 20); WaitEnd(de, 20);
            Check(es.Status == DownloadStatus.Complete, "es: " + es.Status + " " + es.Error);
            CheckEq("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHola DASH\n", File.ReadAllText(es.FilePath, Encoding.UTF8), "DASH WebVTT tal cual");
            Check(fr.Status == DownloadStatus.Complete && Path.GetFileName(fr.FilePath) == "Clip.fr.srt", "fr: " + fr.Status + " " + fr.Error + " " + fr.FilePath);
            Check(File.ReadAllText(fr.FilePath, Encoding.UTF8).Contains("4\r\n00:00:10,500 --> 00:00:11,000\r\nDesplazada"), "TTML -> SRT");
            Check(de.Status == DownloadStatus.Error && de.Error.Contains("MP4"), "stpp en MP4: " + de.Status + " " + de.Error);
        }

        static void TS_TrackMessage()
        {
            MainForm f = NewMainForm();
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                m.Config.ProgressDialogMode = "none";
                string videoDir = Path.Combine(Dl, "videos-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                foreach (Category c in m.Config.Categories) if (c.Name == "Vídeo" || c.Name == "Video") c.Folder = videoDir;
                Check(Path.GetDirectoryName(Path.Combine(m.Config.FolderFor("x.mp4"), "x")) == videoDir, "La prueba necesita la categoría de vídeo");
                string track = S.Put("subs-track/pista.es.vtt", "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nDesde <track>\n", "text/vtt");
                // Un vídeo con la misma dirección ya en la lista no hace que los subtítulos sean un duplicado.
                AddOptions v = new AddOptions(); v.Url = track; v.Folder = Dl; v.FileName = "otro.vtt"; m.Add(v);
                m.Config.DuplicateAction = "ask";
                MethodInfo h = typeof(MainForm).GetMethod("HandleJsonCommand", BindingFlags.NonPublic | BindingFlags.Instance);
                string reply = (string)h.Invoke(f, new object[] { "{\"type\":\"download\",\"url\":\"" + track + "\",\"filename\":\"Mi vídeo.es.vtt\",\"referrer\":\"http://pagina.local/v\",\"mediaKind\":\"subs\",\"mediaVideo\":\"\"}" });
                Check(reply.Contains("\"ok\":true"), "Respuesta: " + reply);
                DownloadItem it = null;
                PumpUntil(delegate { foreach (DownloadItem x in m.Snapshot()) if (x.MediaKind == "subs") it = x; return it != null; }, 5);
                Check(it != null, "No se añadió la descarga de los subtítulos");
                WaitEnd(it, 20);
                Check(it.Status == DownloadStatus.Complete, "Estado: " + it.Status + " " + it.Error);
                CheckEq(Path.Combine(videoDir, "Mi vídeo.es.vtt"), it.FilePath, "Ruta (carpeta del vídeo, nombre del vídeo + idioma)");
                Check(File.ReadAllText(it.FilePath, Encoding.UTF8).Contains("Desde <track>"), "Contenido");
                Check(it.MediaVideo == null, "Sin pista (archivo suelto)");
                // Un .srt suelto se guarda tal cual (aunque esté en Windows-1252).
                byte[] latin = Encoding.GetEncoding(1252).GetBytes("1\r\n00:00:01,000 --> 00:00:02,000\r\ncanción\r\n");
                string srtUrl = S.Put("subs-track/pista.srt", latin, "application/x-subrip");
                reply = (string)h.Invoke(f, new object[] { "{\"type\":\"download\",\"url\":\"" + srtUrl + "\",\"filename\":\"Mi vídeo.srt\",\"mediaKind\":\"subs\"}" });
                DownloadItem srt = null;
                PumpUntil(delegate { foreach (DownloadItem x in m.Snapshot()) if (x.Url == srtUrl) srt = x; return srt != null; }, 5);
                Check(srt != null, "No se añadió el .srt: " + reply);
                WaitEnd(srt, 20);
                Check(srt.Status == DownloadStatus.Complete && Convert.ToBase64String(File.ReadAllBytes(srt.FilePath)) == Convert.ToBase64String(latin), "SRT tal cual: " + srt.Status + " " + srt.Error);
                // Una página web en vez de subtítulos: error.
                string html = S.Put("subs-track/login.vtt", "<!DOCTYPE html><html><body>Entra</body></html>", "text/html");
                h.Invoke(f, new object[] { "{\"type\":\"download\",\"url\":\"" + html + "\",\"filename\":\"x.vtt\",\"mediaKind\":\"subs\"}" });
                DownloadItem web = null;
                PumpUntil(delegate { foreach (DownloadItem x in m.Snapshot()) if (x.Url == html) web = x; return web != null; }, 5);
                Check(web != null, "No se añadió la página");
                WaitEnd(web, 20);
                Check(web.Status == DownloadStatus.Error && web.Error.Contains("página web"), "Página web: " + web.Status + " " + web.Error);
            }
            finally { CloseOpenForms(typeof(ProgressForm)); DisposeMainForm(f); }
        }
    }
}
