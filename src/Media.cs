using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace IDMClone
{
    // Vídeo por streaming (HLS .m3u8 y DASH .mpd): lo que el panel "Descargar este video" de IDM sabe bajar.

    // Un trozo del vídeo o del audio (o el de inicialización).
    public class MediaSegment
    {
        public string Url;
        public long RangeStart = -1, RangeLength = -1; // trozo de un archivo mayor (Range)
        public double Duration;
        public string KeyUrl;    // HLS AES-128
        public byte[] IV;        // null = número de secuencia
        public long Sequence;
    }

    public class MediaPlaylist
    {
        public readonly List<MediaSegment> Segments = new List<MediaSegment>();
        public MediaSegment Init;        // EXT-X-MAP / Initialization (fMP4, WebM)
        public bool Live;                // sin EXT-X-ENDLIST / MPD dinámico
        public bool Protected;           // DRM (SAMPLE-AES, Widevine...): no se puede descargar
        public double TargetDuration;    // en directo: cada cuánto hay trozos nuevos (segundos)
        public double Duration
        {
            get { double d = 0; foreach (MediaSegment s in Segments) d += s.Duration; return d; }
        }
    }

    // Una calidad de vídeo, una pista de audio, o un flujo con los dos (HLS "muxed").
    public class MediaTrack
    {
        public string Kind;        // "video", "audio", "muxed" o "subtitle"
        public string Key;         // HLS: URL de su lista; DASH: id de la Representation
        public long Bandwidth;
        public int Width, Height;
        public string Codecs = "";
        public string Language, Name;
        public string AudioGroup;  // HLS: grupo de audio que usa esta calidad
        public string MimeType = "";
        public bool Default;
        public MediaPlaylist Playlist; // DASH: ya calculada; HLS: se baja aparte
        public bool HasAudio { get { return Kind != "video"; } }

        public string Label
        {
            get
            {
                string r = Height > 0 ? Width + "x" + Height : (Kind == "audio" ? "audio" : "");
                if (Bandwidth > 0) r += (r.Length > 0 ? ", " : "") + FormatRate(Bandwidth);
                if (Kind == "audio" && !string.IsNullOrEmpty(Language)) r += " (" + Language + ")";
                return r;
            }
        }

        public static string FormatRate(long bps)
        {
            if (bps >= 1000000) return (bps / 1000000.0).ToString("0.#", CultureInfo.InvariantCulture) + " Mbps";
            return (bps / 1000) + " kbps";
        }

        // Formato del contenedor según los códecs / tipo MIME: "webm" o "mp4" (o "ts", que se decide al bajar).
        public string Container
        {
            get
            {
                string m = (MimeType ?? "").ToLowerInvariant(), c = (Codecs ?? "").ToLowerInvariant();
                if (m.Contains("webm")) return "webm";
                if (m.Contains("mp4")) return "mp4";
                if (c == "vp8" || c == "vp9" || c.StartsWith("vorbis")) return "webm";
                return "mp4";
            }
        }

        // Subtítulos: "vtt" (WebVTT), "ttml" (TTML/DFXP en XML) o "mp4" (dentro de trozos MP4: no se bajan aparte).
        public string SubtitleFormat
        {
            get
            {
                string m = (MimeType ?? "").ToLowerInvariant(), c = (Codecs ?? "").ToLowerInvariant();
                if (m.Contains("mp4")) return "mp4";
                if (m.Contains("ttml") || m.Contains("dfxp") || c.StartsWith("stpp")) return "ttml";
                return "vtt";
            }
        }
    }

    public class MediaManifest
    {
        public string Kind;                 // "hls" o "dash"
        public string Url;
        public readonly List<MediaTrack> Videos = new List<MediaTrack>(); // calidades (video o muxed)
        public readonly List<MediaTrack> Audios = new List<MediaTrack>(); // pistas de audio aparte
        public readonly List<MediaTrack> Subtitles = new List<MediaTrack>(); // subtítulos (HLS SUBTITLES, DASH text/vtt o TTML)
        public double Duration;             // segundos (0 = desconocida)
        public bool Live, Protected;

        public MediaTrack FindVideo(string key) { return Find(Videos, key); }
        public MediaTrack FindAudio(string key) { return Find(Audios, key); }
        public MediaTrack FindSubtitle(string key) { return Find(Subtitles, key); }

        static MediaTrack Find(List<MediaTrack> l, string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            foreach (MediaTrack t in l) if (t.Key == key) return t;
            return null;
        }

        // Audio que va con una calidad: el de su grupo (el marcado por defecto) o, en DASH, el de más calidad.
        public MediaTrack AudioFor(MediaTrack video)
        {
            if (video == null || Audios.Count == 0) return null;
            if (Kind == "hls")
            {
                if (string.IsNullOrEmpty(video.AudioGroup)) return null;
                MediaTrack first = null;
                foreach (MediaTrack a in Audios)
                {
                    if (a.AudioGroup != video.AudioGroup || a.Playlist == null && string.IsNullOrEmpty(a.Key)) continue;
                    if (a.Default) return a;
                    if (first == null) first = a;
                }
                return first;
            }
            // DASH: mismo contenedor si lo hay (mp4 con mp4, webm con webm), y la mejor calidad.
            MediaTrack best = null;
            foreach (MediaTrack a in Audios)
            {
                bool sameBox = a.Container == video.Container;
                if (best == null) { best = a; continue; }
                bool bestSame = best.Container == video.Container;
                if (sameBox && !bestSame || sameBox == bestSame && a.Bandwidth > best.Bandwidth) best = a;
            }
            return best;
        }

        public static bool LooksLikeHls(string text) { return text != null && text.TrimStart('﻿', ' ', '\r', '\n', '\t').StartsWith("#EXTM3U"); }
        public static bool LooksLikeDash(string text) { return text != null && text.IndexOf("<MPD", StringComparison.Ordinal) >= 0; }

        public static MediaManifest Parse(string text, string url)
        {
            if (LooksLikeHls(text)) return Hls.ParseMaster(text, url);
            if (LooksLikeDash(text)) return Dash.Parse(text, url);
            throw new FormatException("No es una lista de reproducción HLS (.m3u8) ni DASH (.mpd).");
        }
    }

    public static class Hls
    {
        public static string Resolve(string baseUrl, string rel)
        {
            if (string.IsNullOrEmpty(rel)) return baseUrl;
            Uri r;
            if (Uri.TryCreate(rel, UriKind.Absolute, out r) && (r.Scheme == "http" || r.Scheme == "https")) return r.AbsoluteUri;
            return new Uri(new Uri(baseUrl), rel).AbsoluteUri;
        }

        // ATRIBUTO=valor,OTRO="valor, con comas"
        public static Dictionary<string, string> Attributes(string s)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            while (i < s.Length)
            {
                int eq = s.IndexOf('=', i);
                if (eq < 0) break;
                string name = s.Substring(i, eq - i).Trim().TrimStart(',').Trim();
                i = eq + 1;
                string val;
                if (i < s.Length && s[i] == '"')
                {
                    int end = s.IndexOf('"', i + 1);
                    if (end < 0) end = s.Length;
                    val = s.Substring(i + 1, end - i - 1);
                    i = end + 1;
                    int comma = s.IndexOf(',', i);
                    i = comma < 0 ? s.Length : comma + 1;
                }
                else
                {
                    int comma = s.IndexOf(',', i);
                    if (comma < 0) comma = s.Length;
                    val = s.Substring(i, comma - i).Trim();
                    i = comma + 1;
                }
                if (name.Length > 0) d[name] = val;
            }
            return d;
        }

        static string Get(Dictionary<string, string> d, string k) { string v; return d.TryGetValue(k, out v) ? v : null; }

        static IEnumerable<string> Lines(string text)
        {
            foreach (string l in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string t = l.Trim().TrimStart('﻿');
                if (t.Length > 0) yield return t;
            }
        }

        public static bool IsMaster(string text) { return text.IndexOf("#EXT-X-STREAM-INF", StringComparison.Ordinal) >= 0; }

        // Lista maestra (calidades y pistas de audio). Si es ya la lista de un flujo, una sola "calidad".
        public static MediaManifest ParseMaster(string text, string url)
        {
            MediaManifest m = new MediaManifest(); m.Kind = "hls"; m.Url = url;
            if (!IsMaster(text))
            {
                MediaTrack only = new MediaTrack(); only.Kind = "muxed"; only.Key = url;
                only.Playlist = ParseMedia(text, url);
                // La lista de unos subtítulos (trozos .vtt) no es un vídeo.
                if (IsSubtitlePlaylist(only.Playlist)) { only.Kind = "subtitle"; only.MimeType = "text/vtt"; m.Subtitles.Add(only); return m; }
                m.Videos.Add(only);
                m.Duration = only.Playlist.Duration; m.Live = only.Playlist.Live; m.Protected = only.Playlist.Protected;
                return m;
            }
            Dictionary<string, string> pending = null;
            HashSet<string> seen = new HashSet<string>();
            foreach (string line in Lines(text))
            {
                if (line.StartsWith("#EXT-X-STREAM-INF:")) { pending = Attributes(line.Substring(18)); continue; }
                if (line.StartsWith("#EXT-X-MEDIA:"))
                {
                    Dictionary<string, string> a = Attributes(line.Substring(13));
                    if (string.Equals(Get(a, "TYPE"), "SUBTITLES", StringComparison.OrdinalIgnoreCase))
                    {
                        // Subtítulos WebVTT en su propia lista (los CLOSED-CAPTIONS van dentro del vídeo: no se pueden bajar aparte).
                        string su = Get(a, "URI");
                        if (string.IsNullOrEmpty(su)) continue;
                        MediaTrack s = new MediaTrack(); s.Kind = "subtitle"; s.Key = Resolve(url, su); s.MimeType = "text/vtt";
                        s.AudioGroup = Get(a, "GROUP-ID"); s.Language = Get(a, "LANGUAGE"); s.Name = Get(a, "NAME");
                        s.Default = string.Equals(Get(a, "DEFAULT"), "YES", StringComparison.OrdinalIgnoreCase);
                        if (seen.Add("s:" + s.Key)) m.Subtitles.Add(s);
                        continue;
                    }
                    if (!string.Equals(Get(a, "TYPE"), "AUDIO", StringComparison.OrdinalIgnoreCase)) continue;
                    string uri = Get(a, "URI");
                    if (string.IsNullOrEmpty(uri)) continue; // el audio va dentro del vídeo
                    MediaTrack t = new MediaTrack(); t.Kind = "audio"; t.Key = Resolve(url, uri);
                    t.AudioGroup = Get(a, "GROUP-ID"); t.Language = Get(a, "LANGUAGE"); t.Name = Get(a, "NAME");
                    t.Default = string.Equals(Get(a, "DEFAULT"), "YES", StringComparison.OrdinalIgnoreCase);
                    string ch = Get(a, "CHANNELS"); t.Codecs = "";
                    if (seen.Add("a:" + t.Key)) m.Audios.Add(t);
                    continue;
                }
                if (line.StartsWith("#EXT-X-SESSION-KEY:"))
                {
                    string method = Get(Attributes(line.Substring(19)), "METHOD") ?? "";
                    if (method != "NONE" && method != "AES-128") m.Protected = true;
                    continue;
                }
                if (line.StartsWith("#")) continue;
                if (pending == null) continue;
                MediaTrack v = new MediaTrack(); v.Key = Resolve(url, line);
                long bw; long.TryParse(Get(pending, "BANDWIDTH") ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out bw); v.Bandwidth = bw;
                v.Codecs = Get(pending, "CODECS") ?? "";
                string res = Get(pending, "RESOLUTION");
                if (res != null)
                {
                    string[] p = res.ToLowerInvariant().Split('x');
                    if (p.Length == 2) { int.TryParse(p[0], out v.Width); int.TryParse(p[1], out v.Height); }
                }
                v.AudioGroup = Get(pending, "AUDIO");
                // Sin resolución y solo con códec de audio: es una pista solo de audio.
                bool audioOnly = res == null && v.Codecs.Length > 0 && v.Codecs.IndexOf("avc", StringComparison.OrdinalIgnoreCase) < 0
                                 && v.Codecs.IndexOf("hvc", StringComparison.OrdinalIgnoreCase) < 0 && v.Codecs.IndexOf("hev", StringComparison.OrdinalIgnoreCase) < 0
                                 && v.Codecs.IndexOf("vp0", StringComparison.OrdinalIgnoreCase) < 0 && v.Codecs.IndexOf("av01", StringComparison.OrdinalIgnoreCase) < 0;
                v.Kind = audioOnly ? "audio" : (string.IsNullOrEmpty(v.AudioGroup) || !HasAudioGroup(m, v.AudioGroup) ? "muxed" : "video");
                pending = null;
                if (!seen.Add("v:" + v.Key)) continue;
                if (audioOnly) m.Audios.Add(v); else m.Videos.Add(v);
            }
            // Las calidades vistas antes que su #EXT-X-MEDIA (orden raro) se corrigen ahora.
            foreach (MediaTrack v in m.Videos)
                if (v.Kind == "muxed" && !string.IsNullOrEmpty(v.AudioGroup) && HasAudioGroup(m, v.AudioGroup)) v.Kind = "video";
            m.Videos.Sort(delegate(MediaTrack a, MediaTrack b)
            {
                int c = (b.Height * (long)b.Width).CompareTo(a.Height * (long)a.Width);
                return c != 0 ? c : b.Bandwidth.CompareTo(a.Bandwidth);
            });
            return m;
        }

        static bool HasAudioGroup(MediaManifest m, string group)
        {
            foreach (MediaTrack a in m.Audios) if (a.AudioGroup == group) return true;
            return false;
        }

        // ¿Es la lista de unos subtítulos? Todos sus trozos son .vtt/.webvtt (o .srt).
        public static bool IsSubtitlePlaylist(MediaPlaylist p)
        {
            if (p == null || p.Segments.Count == 0 || p.Init != null) return false;
            foreach (MediaSegment s in p.Segments)
            {
                string path = s.Url;
                try { path = new Uri(s.Url).AbsolutePath; } catch { }
                string e = System.IO.Path.GetExtension(path ?? "").ToLowerInvariant();
                if (e != ".vtt" && e != ".webvtt" && e != ".srt") return false;
            }
            return true;
        }

        // Lista de un flujo: sus trozos, claves AES-128, EXT-X-MAP y rangos.
        public static MediaPlaylist ParseMedia(string text, string url)
        {
            MediaPlaylist p = new MediaPlaylist();
            p.Live = text.IndexOf("#EXT-X-ENDLIST", StringComparison.Ordinal) < 0
                     && text.IndexOf("#EXT-X-PLAYLIST-TYPE:VOD", StringComparison.Ordinal) < 0;
            long seq = 0;
            double dur = 0;
            string keyUrl = null; byte[] iv = null;
            long nextOffset = 0; string lastRangeUrl = null;
            long rangeLen = -1, rangeStart = -1;
            foreach (string line in Lines(text))
            {
                if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:")) { long.TryParse(line.Substring(22).Trim(), out seq); continue; }
                if (line.StartsWith("#EXT-X-TARGETDURATION:")) { double.TryParse(line.Substring(22).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out p.TargetDuration); continue; }
                if (line.StartsWith("#EXTINF:"))
                {
                    string v = line.Substring(8); int c = v.IndexOf(','); if (c >= 0) v = v.Substring(0, c);
                    double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out dur);
                    continue;
                }
                if (line.StartsWith("#EXT-X-BYTERANGE:"))
                {
                    ParseRange(line.Substring(17).Trim(), out rangeLen, out rangeStart);
                    continue;
                }
                if (line.StartsWith("#EXT-X-KEY:"))
                {
                    Dictionary<string, string> a = Attributes(line.Substring(11));
                    string method = (Get(a, "METHOD") ?? "NONE").ToUpperInvariant();
                    string fmt = Get(a, "KEYFORMAT");
                    if (method == "NONE") { keyUrl = null; iv = null; }
                    else if (method == "AES-128" && (fmt == null || fmt == "identity"))
                    {
                        keyUrl = Resolve(url, Get(a, "URI"));
                        string ivs = Get(a, "IV");
                        iv = ivs != null ? HexBytes(ivs) : null;
                    }
                    else p.Protected = true;
                    continue;
                }
                if (line.StartsWith("#EXT-X-MAP:"))
                {
                    Dictionary<string, string> a = Attributes(line.Substring(11));
                    MediaSegment init = new MediaSegment(); init.Url = Resolve(url, Get(a, "URI"));
                    string br = Get(a, "BYTERANGE");
                    if (br != null) { long l, s; ParseRange(br, out l, out s); init.RangeLength = l; init.RangeStart = s < 0 ? 0 : s; }
                    init.KeyUrl = keyUrl; init.IV = iv;
                    if (p.Init == null) p.Init = init;
                    continue;
                }
                if (line.StartsWith("#")) continue;
                MediaSegment seg = new MediaSegment();
                seg.Url = Resolve(url, line);
                seg.Duration = dur; seg.Sequence = seq++;
                seg.KeyUrl = keyUrl; seg.IV = iv;
                if (rangeLen >= 0)
                {
                    seg.RangeLength = rangeLen;
                    seg.RangeStart = rangeStart >= 0 ? rangeStart : (seg.Url == lastRangeUrl ? nextOffset : 0);
                    nextOffset = seg.RangeStart + rangeLen; lastRangeUrl = seg.Url;
                }
                p.Segments.Add(seg);
                dur = 0; rangeLen = -1; rangeStart = -1;
            }
            return p;
        }

        // "longitud[@inicio]"
        static void ParseRange(string s, out long len, out long start)
        {
            start = -1;
            int at = s.IndexOf('@');
            long.TryParse(at < 0 ? s : s.Substring(0, at), out len);
            if (at >= 0) long.TryParse(s.Substring(at + 1), out start);
        }

        public static byte[] HexBytes(string hex)
        {
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex.Substring(2);
            hex = hex.PadLeft(32, '0');
            byte[] b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber);
            return b;
        }
    }

    public static class Dash
    {
        // Duración ISO 8601: PT634.566S, PT0H10M54.00S, P1DT2H...
        public static double ParseDuration(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            Match m = Regex.Match(s.Trim(), @"^P(?:(\d+(?:\.\d+)?)Y)?(?:(\d+(?:\.\d+)?)M)?(?:(\d+(?:\.\d+)?)D)?(?:T(?:(\d+(?:\.\d+)?)H)?(?:(\d+(?:\.\d+)?)M)?(?:(\d+(?:\.\d+)?)S)?)?$");
            if (!m.Success) return 0;
            double[] mult = { 365 * 86400.0, 30 * 86400.0, 86400, 3600, 60, 1 };
            double r = 0;
            for (int i = 0; i < 6; i++)
                if (m.Groups[i + 1].Success) r += double.Parse(m.Groups[i + 1].Value, CultureInfo.InvariantCulture) * mult[i];
            return r;
        }

        static IEnumerable<XmlElement> Children(XmlNode n, string name)
        {
            foreach (XmlNode c in n.ChildNodes)
            {
                XmlElement e = c as XmlElement;
                if (e != null && e.LocalName == name) yield return e;
            }
        }

        static XmlElement Child(XmlNode n, string name)
        {
            foreach (XmlElement e in Children(n, name)) return e;
            return null;
        }

        static string Attr(XmlElement e, string name) { return e != null && e.HasAttribute(name) ? e.GetAttribute(name) : null; }

        static long AttrLong(XmlElement e, string name, long def)
        {
            string v = Attr(e, name); long r;
            return v != null && long.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : def;
        }

        static string BaseOf(XmlElement e, string parentBase)
        {
            XmlElement b = Child(e, "BaseURL");
            return b != null && b.InnerText.Trim().Length > 0 ? Hls.Resolve(parentBase, b.InnerText.Trim()) : parentBase;
        }

        static bool HasProtection(XmlElement e) { return Child(e, "ContentProtection") != null; }

        public static MediaManifest Parse(string xml, string url) { return Parse(xml, url, DateTime.UtcNow); }

        // Fecha ISO 8601 (availabilityStartTime).
        static DateTime ParseDate(string s)
        {
            DateTime d;
            if (!string.IsNullOrEmpty(s) && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d)) return d;
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        // En directo (type="dynamic") se calculan los trozos ya publicados a la hora "now".
        public static MediaManifest Parse(string xml, string url, DateTime now)
        {
            XmlDocument doc = new XmlDocument();
            doc.XmlResolver = null;
            XmlReaderSettings rs = new XmlReaderSettings(); rs.DtdProcessing = DtdProcessing.Ignore; rs.XmlResolver = null;
            using (XmlReader r = XmlReader.Create(new System.IO.StringReader(xml.TrimStart('﻿')), rs)) doc.Load(r);
            XmlElement mpd = doc.DocumentElement;
            if (mpd == null || mpd.LocalName != "MPD") throw new FormatException("No es un manifiesto DASH.");

            MediaManifest m = new MediaManifest(); m.Kind = "dash"; m.Url = url;
            m.Live = string.Equals(Attr(mpd, "type"), "dynamic", StringComparison.OrdinalIgnoreCase);
            m.Duration = m.Live ? 0 : ParseDuration(Attr(mpd, "mediaPresentationDuration"));
            double update = ParseDuration(Attr(mpd, "minimumUpdatePeriod"));
            string mpdBase = BaseOf(mpd, url);
            XmlElement period = Child(mpd, "Period");
            if (period == null) throw new FormatException("El manifiesto DASH no tiene ningún periodo.");
            double periodDur = m.Live ? 0 : ParseDuration(Attr(period, "duration"));
            if (periodDur <= 0) periodDur = m.Duration;
            if (m.Duration <= 0) m.Duration = periodDur;
            // Segundos transcurridos desde el inicio del periodo (solo en directo).
            double elapsed = m.Live ? (now - ParseDate(Attr(mpd, "availabilityStartTime"))).TotalSeconds - ParseDuration(Attr(period, "start")) : -1;
            string periodBase = BaseOf(period, mpdBase);

            foreach (XmlElement set in Children(period, "AdaptationSet"))
            {
                string setBase = BaseOf(set, periodBase);
                string setMime = Attr(set, "mimeType") ?? "";
                string setType = Attr(set, "contentType") ?? "";
                bool setProt = HasProtection(set);
                foreach (XmlElement rep in Children(set, "Representation"))
                {
                    string mime = Attr(rep, "mimeType") ?? setMime;
                    string type = setType.Length > 0 ? setType : (mime.Contains("/") ? mime.Substring(0, mime.IndexOf('/')) : "");
                    string repCodecs = (Attr(rep, "codecs") ?? Attr(set, "codecs") ?? "").ToLowerInvariant();
                    if (type == "text" || IsSubtitleMime(mime) || repCodecs.StartsWith("stpp") || repCodecs.StartsWith("wvtt"))
                    {
                        AddSubtitle(m, period, set, rep, setBase, mime, periodDur, elapsed);
                        continue;
                    }
                    if (type != "video" && type != "audio") continue; // imágenes (miniaturas)...
                    MediaTrack t = new MediaTrack();
                    t.Kind = type; t.Key = Attr(rep, "id") ?? (m.Videos.Count + m.Audios.Count).ToString(CultureInfo.InvariantCulture);
                    t.MimeType = mime;
                    t.Codecs = Attr(rep, "codecs") ?? Attr(set, "codecs") ?? "";
                    t.Bandwidth = AttrLong(rep, "bandwidth", 0);
                    t.Width = (int)AttrLong(rep, "width", AttrLong(set, "width", 0));
                    t.Height = (int)AttrLong(rep, "height", AttrLong(set, "height", 0));
                    t.Language = Attr(set, "lang");
                    string repBase = BaseOf(rep, setBase);
                    t.Playlist = Segments(period, set, rep, repBase, t, periodDur, elapsed);
                    t.Playlist.Protected = setProt || HasProtection(rep);
                    if (t.Playlist.Protected) m.Protected = true;
                    t.Playlist.Live = m.Live;
                    if (m.Live && update > 0 && (t.Playlist.TargetDuration <= 0 || update < t.Playlist.TargetDuration)) t.Playlist.TargetDuration = update;
                    if (type == "video") m.Videos.Add(t); else m.Audios.Add(t);
                }
            }
            m.Videos.Sort(delegate(MediaTrack a, MediaTrack b)
            {
                int c = (b.Height * (long)b.Width).CompareTo(a.Height * (long)a.Width);
                return c != 0 ? c : b.Bandwidth.CompareTo(a.Bandwidth);
            });
            m.Audios.Sort(delegate(MediaTrack a, MediaTrack b) { return b.Bandwidth.CompareTo(a.Bandwidth); });
            return m;
        }

        static bool IsSubtitleMime(string mime)
        {
            string x = (mime ?? "").ToLowerInvariant();
            return x == "text/vtt" || x.Contains("ttml") || x.Contains("dfxp");
        }

        // Una Representation de subtítulos (text/vtt, application/ttml+xml o application/mp4 con stpp/wvtt).
        // Si sus trozos no se pueden calcular, se deja fuera sin estropear el resto del manifiesto.
        static void AddSubtitle(MediaManifest m, XmlElement period, XmlElement set, XmlElement rep, string setBase, string mime, double periodDur, double elapsed)
        {
            MediaTrack t = new MediaTrack();
            t.Kind = "subtitle";
            t.Key = Attr(rep, "id") ?? "sub" + m.Subtitles.Count.ToString(CultureInfo.InvariantCulture);
            t.MimeType = mime;
            t.Codecs = Attr(rep, "codecs") ?? Attr(set, "codecs") ?? "";
            t.Bandwidth = AttrLong(rep, "bandwidth", 0);
            t.Language = Attr(rep, "lang") ?? Attr(set, "lang");
            XmlElement label = Child(rep, "Label") ?? Child(set, "Label");
            if (label != null && label.InnerText.Trim().Length > 0) t.Name = label.InnerText.Trim();
            try
            {
                t.Playlist = Segments(period, set, rep, BaseOf(rep, setBase), t, periodDur, elapsed);
                t.Playlist.Protected = HasProtection(set) || HasProtection(rep);
                t.Playlist.Live = elapsed >= 0;
            }
            catch (FormatException) { return; }
            catch (OverflowException) { return; }
            m.Subtitles.Add(t);
        }

        // Atributo de SegmentTemplate/SegmentList heredado: Representation > AdaptationSet > Period.
        static string Inherited(string attr, params XmlElement[] chain)
        {
            foreach (XmlElement e in chain) { string v = Attr(e, attr); if (v != null) return v; }
            return null;
        }

        const int LiveWindow = 10; // en directo: trozos publicados más recientes que se ofrecen

        static MediaPlaylist Segments(XmlElement period, XmlElement set, XmlElement rep, string baseUrl, MediaTrack t, double periodDur, double elapsed)
        {
            bool live = elapsed >= 0;
            MediaPlaylist p = new MediaPlaylist();
            XmlElement[] tpl = { Child(rep, "SegmentTemplate"), Child(set, "SegmentTemplate"), Child(period, "SegmentTemplate") };
            XmlElement[] lst = { Child(rep, "SegmentList"), Child(set, "SegmentList"), Child(period, "SegmentList") };
            bool hasTpl = tpl[0] != null || tpl[1] != null || tpl[2] != null;
            bool hasList = lst[0] != null || lst[1] != null || lst[2] != null;

            if (hasTpl)
            {
                string media = Inherited("media", tpl), init = Inherited("initialization", tpl);
                long timescale = long.Parse(Inherited("timescale", tpl) ?? "1", CultureInfo.InvariantCulture);
                long startNumber = long.Parse(Inherited("startNumber", tpl) ?? "1", CultureInfo.InvariantCulture);
                long pto = long.Parse(Inherited("presentationTimeOffset", tpl) ?? "0", CultureInfo.InvariantCulture);
                if (init != null) { p.Init = new MediaSegment(); p.Init.Url = Hls.Resolve(baseUrl, Expand(init, t, 0, 0)); }
                XmlElement timeline = null;
                foreach (XmlElement e in tpl) if (e != null && (timeline = Child(e, "SegmentTimeline")) != null) break;
                if (timeline != null)
                {
                    long time = 0, number = startNumber;
                    long end = live ? pto + (long)Math.Floor(elapsed * timescale) : periodDur > 0 ? pto + (long)Math.Round(periodDur * timescale) : long.MaxValue;
                    bool first = true;
                    foreach (XmlElement s in Children(timeline, "S"))
                    {
                        long st = AttrLong(s, "t", -1), d = AttrLong(s, "d", 0), r = AttrLong(s, "r", 0);
                        if (st >= 0) time = st; else if (first) time = pto;
                        first = false;
                        if (d <= 0) continue;
                        if (r < 0) r = end == long.MaxValue ? 0 : Math.Max(0, (long)Math.Ceiling((end - time) / (double)d) - 1);
                        long skip = 0;
                        if (live && r + 1 > LiveWindow * 2)
                        {
                            // En directo solo interesan los últimos: se salta lo anterior sin generarlo.
                            long done = Math.Max(0, Math.Min(r + 1, (end - time) / d));
                            skip = Math.Max(0, done - LiveWindow);
                            time += skip * d; number += skip;
                        }
                        for (long i = skip; i <= r; i++)
                        {
                            // En directo solo los trozos ya terminados; el número de orden es su tiempo.
                            if (live && time + d > end) break;
                            MediaSegment seg = new MediaSegment();
                            seg.Url = Hls.Resolve(baseUrl, Expand(media, t, number, time));
                            seg.Duration = d / (double)timescale; seg.Sequence = live ? time : number;
                            p.Segments.Add(seg);
                            p.TargetDuration = Math.Max(p.TargetDuration, seg.Duration);
                            time += d; number++;
                        }
                    }
                    if (live && p.Segments.Count > LiveWindow) p.Segments.RemoveRange(0, p.Segments.Count - LiveWindow);
                }
                else
                {
                    long dur = long.Parse(Inherited("duration", tpl) ?? "0", CultureInfo.InvariantCulture);
                    if (dur <= 0) throw new FormatException("SegmentTemplate sin duración ni SegmentTimeline.");
                    if (live)
                    {
                        // Trozo k publicado cuando termina: k completos = transcurrido / duración.
                        double segSec = dur / (double)timescale;
                        long available = (long)Math.Floor(elapsed / segSec);
                        for (long k = Math.Max(0, available - LiveWindow); k < available; k++)
                        {
                            MediaSegment seg = new MediaSegment();
                            long number = startNumber + k;
                            seg.Url = Hls.Resolve(baseUrl, Expand(media, t, number, pto + k * dur));
                            seg.Duration = segSec; seg.Sequence = number;
                            p.Segments.Add(seg);
                        }
                        p.TargetDuration = segSec;
                        return p;
                    }
                    long count = (long)Math.Ceiling(periodDur * timescale / dur - 1e-9);
                    if (count <= 0) throw new FormatException("No se conoce la duración del vídeo DASH.");
                    for (long i = 0; i < count; i++)
                    {
                        MediaSegment seg = new MediaSegment();
                        long number = startNumber + i;
                        seg.Url = Hls.Resolve(baseUrl, Expand(media, t, number, i * dur));
                        seg.Duration = Math.Min(dur / (double)timescale, periodDur - i * dur / (double)timescale);
                        seg.Sequence = number;
                        p.Segments.Add(seg);
                    }
                }
                return p;
            }

            if (hasList)
            {
                XmlElement l = lst[0] ?? lst[1] ?? lst[2];
                long timescale = AttrLong(l, "timescale", 1), dur = AttrLong(l, "duration", 0);
                XmlElement ini = Child(l, "Initialization");
                if (ini != null)
                {
                    p.Init = new MediaSegment();
                    p.Init.Url = Attr(ini, "sourceURL") != null ? Hls.Resolve(baseUrl, Attr(ini, "sourceURL")) : baseUrl;
                    SetRange(p.Init, Attr(ini, "range"));
                }
                long n = 0;
                foreach (XmlElement su in Children(l, "SegmentURL"))
                {
                    MediaSegment seg = new MediaSegment();
                    seg.Url = Attr(su, "media") != null ? Hls.Resolve(baseUrl, Attr(su, "media")) : baseUrl;
                    SetRange(seg, Attr(su, "mediaRange"));
                    seg.Duration = dur > 0 ? dur / (double)timescale : 0; seg.Sequence = n++;
                    p.Segments.Add(seg);
                }
                return p;
            }

            // SegmentBase o solo BaseURL: un único archivo con todo (se parte por rangos al bajarlo).
            MediaSegment whole = new MediaSegment();
            whole.Url = baseUrl; whole.Duration = periodDur;
            p.Segments.Add(whole);
            return p;
        }

        static void SetRange(MediaSegment s, string range)
        {
            if (string.IsNullOrEmpty(range)) return;
            string[] p = range.Split('-');
            long a, b;
            if (p.Length == 2 && long.TryParse(p[0], out a) && long.TryParse(p[1], out b)) { s.RangeStart = a; s.RangeLength = b - a + 1; }
        }

        // $RepresentationID$, $Number%05d$, $Time$, $Bandwidth$, $$
        public static string Expand(string tpl, MediaTrack t, long number, long time)
        {
            if (tpl == null) return null;
            return Regex.Replace(tpl, @"\$(RepresentationID|Number|Time|Bandwidth|)(%0(\d+)d)?\$", delegate(Match m)
            {
                string id = m.Groups[1].Value;
                if (id == "") return "$";
                long v;
                if (id == "RepresentationID") return t.Key;
                if (id == "Number") v = number; else if (id == "Time") v = time; else v = t.Bandwidth;
                return m.Groups[3].Success ? v.ToString("D" + m.Groups[3].Value, CultureInfo.InvariantCulture) : v.ToString(CultureInfo.InvariantCulture);
            });
        }
    }
}
