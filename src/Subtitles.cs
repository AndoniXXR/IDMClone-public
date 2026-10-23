using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace IDMClone
{
    // Subtítulos del panel "Descargar este video" (como IDM): se bajan aparte, junto al vídeo, con su mismo nombre y el
    // idioma delante de la extensión ("Mi vídeo.es.vtt"). Salen de:
    //   - HLS: #EXT-X-MEDIA:TYPE=SUBTITLES (lista de trozos WebVTT, que se juntan en un solo .vtt);
    //   - DASH: AdaptationSet text/vtt o application/ttml+xml (TTML se pasa a SRT);
    //   - un archivo .vtt/.srt suelto (<track> del <video> o pedido por el reproductor).
    // Opción "Convertir los subtítulos WebVTT a SRT" (Opciones > General > panel > Editar).

    // Una frase de unos subtítulos.
    public class SubtitleCue
    {
        public double Start, End;   // segundos
        public string Id;           // identificador WebVTT (opcional)
        public string Settings;     // ajustes de posición WebVTT ("line:90% align:center")
        public string Text;         // texto (con las marcas <i>, <b>... de WebVTT)
    }

    public static class Subtitles
    {
        // Texto de un archivo: con BOM, UTF-8 válido o, si no, Windows-1252 (los .srt antiguos).
        public static string DecodeText(byte[] data)
        {
            if (data == null) return "";
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return Encoding.UTF8.GetString(data, 3, data.Length - 3);
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return Encoding.Unicode.GetString(data, 2, data.Length - 2);
            if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
            try { return new UTF8Encoding(false, true).GetString(data); }
            catch (DecoderFallbackException) { return Encoding.GetEncoding(1252).GetString(data); }
        }

        static readonly Regex SrtStart = new Regex(@"^\s*\d+\s*\r?\n\s*\d+:\d{1,2}:\d{1,2}[,.]\d+\s*-->", RegexOptions.Compiled);
        static readonly Regex TtmlRoot = new Regex(@"<([A-Za-z_][\w.-]*:)?tt[\s>]", RegexOptions.Compiled);

        // "vtt", "srt", "ttml" o null (otro formato, o no son subtítulos).
        public static string DetectFormat(string text)
        {
            string t = (text ?? "").TrimStart('﻿', ' ', '\t', '\r', '\n');
            if (t.StartsWith("WEBVTT", StringComparison.Ordinal)) return "vtt";
            if (SrtStart.IsMatch(t)) return "srt";
            if (t.StartsWith("<", StringComparison.Ordinal) && TtmlRoot.IsMatch(t)) return "ttml";
            return null;
        }

        public static bool LooksLikeHtml(string text)
        {
            string t = (text ?? "").TrimStart('﻿', ' ', '\t', '\r', '\n').ToLowerInvariant();
            return t.StartsWith("<!doctype html") || t.StartsWith("<html");
        }

        // --- WebVTT / SRT ---

        static readonly Regex TimeRx = new Regex(@"^(?:(\d+):)?(\d{1,2}):(\d{1,2})(?:[.,](\d{1,3}))?$", RegexOptions.Compiled);

        // "01:02:03.456", "02:03.456" (WebVTT) o "01:02:03,456" (SRT).
        public static bool TryParseTime(string s, out double sec)
        {
            sec = 0;
            Match m = TimeRx.Match((s ?? "").Trim());
            if (!m.Success) return false;
            long h = m.Groups[1].Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            int mi = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), se = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            int ms = m.Groups[4].Success ? int.Parse(m.Groups[4].Value.PadRight(3, '0'), CultureInfo.InvariantCulture) : 0;
            sec = h * 3600 + mi * 60 + se + ms / 1000.0;
            return true;
        }

        // 00:00:01.500 (WebVTT, sep '.') o 00:00:01,500 (SRT, sep ',').
        public static string FormatTime(double sec, char sep)
        {
            long ms = (long)Math.Round(Math.Max(0, sec) * 1000);
            long h = ms / 3600000, m = ms / 60000 % 60, s = ms / 1000 % 60;
            return h.ToString("00", CultureInfo.InvariantCulture) + ":" + m.ToString("00", CultureInfo.InvariantCulture) + ":" +
                   s.ToString("00", CultureInfo.InvariantCulture) + sep + (ms % 1000).ToString("000", CultureInfo.InvariantCulture);
        }

        public static List<SubtitleCue> ParseVtt(string text)
        {
            bool hasMap; long ts; double local;
            return ParseBlocks(text, out hasMap, out ts, out local);
        }

        public static List<SubtitleCue> ParseSrt(string text) { return ParseVtt(text); }

        // Bloques separados por líneas en blanco: [identificador] / inicio --> fin [ajustes] / texto.
        // WebVTT: la cabecera (WEBVTT, X-TIMESTAMP-MAP) y los bloques NOTE/STYLE/REGION no son frases.
        static List<SubtitleCue> ParseBlocks(string text, out bool hasMap, out long mpegts, out double local)
        {
            hasMap = false; mpegts = 0; local = 0;
            List<SubtitleCue> cues = new List<SubtitleCue>();
            string[] lines = (text ?? "").TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int i = 0;
            if (lines.Length > 0 && lines[0].StartsWith("WEBVTT", StringComparison.Ordinal))
            {
                for (; i < lines.Length && lines[i].Trim().Length > 0; i++)
                {
                    string l = lines[i].Trim();
                    if (!l.StartsWith("X-TIMESTAMP-MAP=", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (string part in l.Substring(16).Split(','))
                    {
                        int c = part.IndexOf(':');
                        if (c < 0) continue;
                        string k = part.Substring(0, c).Trim().ToUpperInvariant(), v = part.Substring(c + 1).Trim();
                        if (k == "MPEGTS") { if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out mpegts)) hasMap = true; }
                        else if (k == "LOCAL") TryParseTime(v, out local);
                    }
                }
            }
            List<string> block = new List<string>();
            for (; i <= lines.Length; i++)
            {
                string l = i < lines.Length ? lines[i] : "";
                if (l.Trim().Length == 0) { if (block.Count > 0) { AddBlock(block, cues); block.Clear(); } continue; }
                block.Add(l);
            }
            return cues;
        }

        static void AddBlock(List<string> block, List<SubtitleCue> cues)
        {
            string head = block[0].TrimStart();
            if (head.StartsWith("NOTE", StringComparison.Ordinal) || head.StartsWith("STYLE", StringComparison.Ordinal) || head.StartsWith("REGION", StringComparison.Ordinal)) return;
            int tl = block[0].Contains("-->") ? 0 : block.Count > 1 && block[1].Contains("-->") ? 1 : -1;
            if (tl < 0) return;
            string timing = block[tl];
            int arrow = timing.IndexOf("-->", StringComparison.Ordinal);
            string right = timing.Substring(arrow + 3).Trim();
            int sp = right.IndexOfAny(new char[] { ' ', '\t' });
            double start, end;
            if (!TryParseTime(timing.Substring(0, arrow), out start) || !TryParseTime(sp < 0 ? right : right.Substring(0, sp), out end)) return;
            SubtitleCue c = new SubtitleCue();
            c.Start = start; c.End = end;
            c.Id = tl == 1 ? block[0].Trim() : null;
            c.Settings = sp < 0 ? null : right.Substring(sp + 1).Trim();
            c.Text = string.Join("\n", block.GetRange(tl + 1, block.Count - tl - 1).ToArray());
            cues.Add(c);
        }

        // Trozos WebVTT de una lista HLS -> una sola lista de frases. Cada trozo repite la cabecera; si su X-TIMESTAMP-MAP
        // cambia de un trozo a otro, sus tiempos son relativos a ese trozo y se desplazan (90 kHz, como el vídeo). Las frases
        // repetidas en dos trozos seguidos (las que cruzan el corte) quedan una sola vez.
        public static List<SubtitleCue> JoinVtt(IList<string> segments)
        {
            List<SubtitleCue> all = new List<SubtitleCue>();
            bool baseSet = false; long baseTs = 0; double baseLocal = 0;
            foreach (string seg in segments)
            {
                bool hasMap; long ts; double local;
                List<SubtitleCue> cues = ParseBlocks(seg, out hasMap, out ts, out local);
                double offset = 0;
                if (hasMap)
                {
                    if (!baseSet) { baseSet = true; baseTs = ts; baseLocal = local; }
                    else
                    {
                        long d = ts - baseTs;
                        if (d < -(1L << 32)) d += 1L << 33; // el reloj MPEG-TS de 33 bits dio la vuelta
                        offset = d / 90000.0 - (local - baseLocal);
                    }
                }
                foreach (SubtitleCue c in cues) { c.Start += offset; c.End += offset; all.Add(c); }
            }
            return Tidy(all);
        }

        // Ordenadas por tiempo (sin cambiar el orden de las que empiezan a la vez); sin repetidas; las partidas en dos
        // trozos (mismo texto, una empieza donde acaba la otra) se juntan.
        static List<SubtitleCue> Tidy(List<SubtitleCue> cues)
        {
            List<SubtitleCue> sorted = cues.OrderBy(c => c.Start).ToList();
            List<SubtitleCue> r = new List<SubtitleCue>();
            HashSet<string> seen = new HashSet<string>();
            foreach (SubtitleCue c in sorted)
            {
                string key = Math.Round(c.Start * 1000) + "|" + Math.Round(c.End * 1000) + "|" + c.Text;
                if (!seen.Add(key)) continue;
                SubtitleCue last = r.Count > 0 ? r[r.Count - 1] : null;
                if (last != null && last.Text == c.Text && Math.Abs(last.End - c.Start) < 0.002 && last.Settings == c.Settings)
                {
                    last.End = Math.Max(last.End, c.End);
                    continue;
                }
                r.Add(c);
            }
            return r;
        }

        public static string WriteVtt(IList<SubtitleCue> cues)
        {
            StringBuilder sb = new StringBuilder("WEBVTT\n\n");
            foreach (SubtitleCue c in cues)
            {
                if (!string.IsNullOrEmpty(c.Id)) sb.Append(c.Id).Append('\n');
                sb.Append(FormatTime(c.Start, '.')).Append(" --> ").Append(FormatTime(c.End, '.'));
                if (!string.IsNullOrEmpty(c.Settings)) sb.Append(' ').Append(c.Settings);
                sb.Append('\n').Append(c.Text ?? "").Append("\n\n");
            }
            return sb.ToString();
        }

        static readonly Regex TagRx = new Regex(@"<(/?)([^>\s./]*)[^>]*>", RegexOptions.Compiled);

        // Texto WebVTT -> SRT: solo quedan <i>, <b> y <u>; las voces, clases y marcas de tiempo se quitan.
        public static string SrtText(string vtt)
        {
            string t = TagRx.Replace(vtt ?? "", delegate(Match m)
            {
                string n = m.Groups[2].Value.ToLowerInvariant();
                return n == "i" || n == "b" || n == "u" ? "<" + m.Groups[1].Value + n + ">" : "";
            });
            t = WebUtility.HtmlDecode(t.Replace("&lrm;", "").Replace("&rlm;", ""));
            List<string> lines = new List<string>();
            foreach (string l in t.Replace("\r\n", "\n").Split('\n')) if (l.Trim().Length > 0) lines.Add(l.TrimEnd());
            return string.Join("\r\n", lines.ToArray());
        }

        public static string WriteSrt(IList<SubtitleCue> cues)
        {
            StringBuilder sb = new StringBuilder();
            int n = 0;
            foreach (SubtitleCue c in cues)
            {
                string text = SrtText(c.Text);
                if (text.Length == 0) continue;
                sb.Append(++n).Append("\r\n");
                sb.Append(FormatTime(c.Start, ',')).Append(" --> ").Append(FormatTime(Math.Max(c.End, c.Start), ',')).Append("\r\n");
                sb.Append(text).Append("\r\n\r\n");
            }
            return sb.ToString();
        }

        // --- TTML (DFXP) ---

        public static List<SubtitleCue> ParseTtml(string xml)
        {
            List<SubtitleCue> cues = new List<SubtitleCue>();
            XmlDocument doc = new XmlDocument();
            doc.XmlResolver = null;
            XmlReaderSettings rs = new XmlReaderSettings(); rs.DtdProcessing = DtdProcessing.Ignore; rs.XmlResolver = null;
            using (XmlReader r = XmlReader.Create(new StringReader((xml ?? "").TrimStart('﻿')), rs)) doc.Load(r);
            XmlElement root = doc.DocumentElement;
            if (root == null || root.LocalName != "tt") return cues;
            double tickRate = 0, frameRate = 30;
            foreach (XmlAttribute a in root.Attributes)
            {
                double v;
                if (a.LocalName == "tickRate" && double.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0) tickRate = v;
                else if (a.LocalName == "frameRate" && double.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0) frameRate = v;
            }
            foreach (XmlAttribute a in root.Attributes)
                if (a.LocalName == "frameRateMultiplier")
                {
                    string[] p = a.Value.Trim().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    double x, y;
                    if (p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y) && y > 0) frameRate = frameRate * x / y;
                }
            if (tickRate <= 0) tickRate = frameRate; // TTML: sin tickRate, los "t" van a la velocidad de imagen
            foreach (XmlNode node in root.SelectNodes("//*[local-name()='p']"))
            {
                XmlElement p = (XmlElement)node;
                double begin, end, dur;
                bool hasBegin = TtmlTime(p.GetAttribute("begin"), tickRate, frameRate, out begin);
                bool hasEnd = TtmlTime(p.GetAttribute("end"), tickRate, frameRate, out end);
                bool hasDur = TtmlTime(p.GetAttribute("dur"), tickRate, frameRate, out dur);
                if (!hasBegin && !hasEnd) continue;
                // Los contenedores (body, div) pueden llevar su propio inicio: los tiempos de dentro son relativos a él.
                double offset = 0;
                for (XmlNode up = p.ParentNode; up is XmlElement; up = up.ParentNode)
                {
                    double b;
                    if (TtmlTime(((XmlElement)up).GetAttribute("begin"), tickRate, frameRate, out b)) offset += b;
                }
                SubtitleCue c = new SubtitleCue();
                c.Start = offset + (hasBegin ? begin : 0);
                c.End = hasEnd ? offset + end : c.Start + (hasDur ? dur : 0);
                c.Text = TtmlText(p);
                if (c.Text.Length > 0) cues.Add(c);
            }
            return Tidy(cues);
        }

        static readonly Regex TtClock = new Regex(@"^(\d+):(\d{2}):(\d{2})(?:\.(\d+))?$", RegexOptions.Compiled);
        static readonly Regex TtFrames = new Regex(@"^(\d+):(\d{2}):(\d{2}):(\d+)(?:\.\d+)?$", RegexOptions.Compiled);
        static readonly Regex TtOffset = new Regex(@"^(\d+(?:\.\d+)?)(h|ms|m|s|f|t)$", RegexOptions.Compiled);

        // Expresiones de tiempo TTML: 00:00:01.500, 00:00:01:12 (imágenes), 1.5s, 1500ms, 15000000t...
        static bool TtmlTime(string s, double tickRate, double frameRate, out double sec)
        {
            sec = 0;
            s = (s ?? "").Trim();
            if (s.Length == 0) return false;
            Match m = TtClock.Match(s);
            if (m.Success)
            {
                sec = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60 +
                      int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                if (m.Groups[4].Success) sec += double.Parse("0." + m.Groups[4].Value, CultureInfo.InvariantCulture);
                return true;
            }
            m = TtFrames.Match(s);
            if (m.Success)
            {
                sec = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60 +
                      int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) + int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) / frameRate;
                return true;
            }
            m = TtOffset.Match(s);
            if (!m.Success) return false;
            double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            switch (m.Groups[2].Value)
            {
                case "h": sec = v * 3600; break;
                case "m": sec = v * 60; break;
                case "s": sec = v; break;
                case "ms": sec = v / 1000; break;
                case "f": sec = v / frameRate; break;
                default: sec = v / tickRate; break;
            }
            return true;
        }

        // Texto de un <p>: los <br/> son saltos de línea; los espacios del XML se juntan; la cursiva pasa a <i>.
        static string TtmlText(XmlElement p)
        {
            StringBuilder sb = new StringBuilder();
            AppendTtml(p, sb);
            string t = Regex.Replace(sb.ToString(), @"\s+", " ");
            List<string> lines = new List<string>();
            foreach (string l in t.Split('\u0001')) if (l.Trim().Length > 0) lines.Add(l.Trim());
            return string.Join("\n", lines.ToArray());
        }

        static void AppendTtml(XmlNode n, StringBuilder sb)
        {
            foreach (XmlNode c in n.ChildNodes)
            {
                if (c is XmlText || c is XmlWhitespace || c is XmlSignificantWhitespace || c is XmlCDataSection) { sb.Append(c.Value); continue; }
                XmlElement e = c as XmlElement;
                if (e == null) continue;
                if (e.LocalName == "br") { sb.Append('\u0001'); continue; }
                bool italic = false;
                foreach (XmlAttribute a in e.Attributes) if (a.LocalName == "fontStyle" && a.Value == "italic") italic = true;
                if (italic) sb.Append("<i>");
                AppendTtml(e, sb);
                if (italic) sb.Append("</i>");
            }
        }

        // --- Resultado ---

        // Archivo final a partir de lo bajado (uno o varios trozos). WebVTT se deja como está (o pasa a SRT con la opción),
        // los trozos se juntan, y TTML pasa a SRT. ext = extensión del resultado, o null si el formato no se conoce
        // (se guarda tal cual).
        public static byte[] Build(IList<byte[]> parts, bool toSrt, out string ext)
        {
            ext = null;
            if (parts == null || parts.Count == 0) return new byte[0];
            List<string> texts = new List<string>();
            foreach (byte[] b in parts) texts.Add(DecodeText(b));
            string fmt = DetectFormat(texts[0]);
            UTF8Encoding utf8 = new UTF8Encoding(false);
            if (fmt == "vtt")
            {
                if (parts.Count == 1 && !toSrt) { ext = ".vtt"; return parts[0]; }
                List<SubtitleCue> cues = JoinVtt(texts);
                if (toSrt) { ext = ".srt"; return Utf8Bom(WriteSrt(cues)); }
                ext = ".vtt"; return utf8.GetBytes(WriteVtt(cues));
            }
            if (fmt == "srt")
            {
                ext = ".srt";
                if (parts.Count == 1) return parts[0];
                List<SubtitleCue> all = new List<SubtitleCue>();
                foreach (string t in texts) all.AddRange(ParseSrt(t));
                return Utf8Bom(WriteSrt(Tidy(all)));
            }
            if (fmt == "ttml")
            {
                List<SubtitleCue> all = new List<SubtitleCue>();
                try { foreach (string t in texts) all.AddRange(ParseTtml(t)); }
                catch (XmlException) { all.Clear(); }
                if (all.Count > 0) { ext = ".srt"; return Utf8Bom(WriteSrt(Tidy(all))); }
                if (parts.Count == 1) { ext = ".ttml"; return parts[0]; }
            }
            // Formato desconocido (.ass, .sbv...): tal cual, los trozos uno detrás de otro.
            using (MemoryStream ms = new MemoryStream())
            {
                foreach (byte[] b in parts) ms.Write(b, 0, b.Length);
                return ms.ToArray();
            }
        }

        static byte[] Utf8Bom(string s) { return new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(s)).ToArray(); }
    }

    public partial class DownloadEngine
    {
        public const string SubtitleKind = "subs"; // MediaKind de una descarga de subtítulos (MediaVideo = la pista)

        // Descarga de unos subtítulos: Url es la lista HLS/DASH (y MediaVideo, la clave de la pista) o el archivo mismo.
        void RunSubtitles()
        {
            _item.Error = null;
            _item.Status = DownloadStatus.Connecting;
            _item.Resumable = false; // archivos pequeños: al reanudar se vuelven a pedir enteros
            string dir = MediaWorkDir(_item, _cfg);
            Directory.CreateDirectory(dir);
            lock (_item.Sync) { _item.Segments.Clear(); _item.Segments.Add(new Segment(0, 0, long.MaxValue)); }
            ConnectionInfo ci = NewConnection("Conectando...");
            List<byte[]> parts;
            try { parts = FetchSubtitleParts(ci); }
            catch (OperationCanceledException) { SetInfo(ci, "Detenida"); MediaPaused(); return; }
            if (_stop) { SetInfo(ci, "Detenida"); MediaPaused(); return; }

            string ext;
            byte[] data = Subtitles.Build(parts, _cfg.SubtitlesToSrt, out ext);
            if (ext == null && Subtitles.LooksLikeHtml(Subtitles.DecodeText(parts[0])))
                throw new FatalDownloadException("El servidor no devolvió unos subtítulos sino una página web.");
            string outPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(_item.FilePath) + (ext ?? Path.GetExtension(_item.FilePath)));
            File.WriteAllBytes(outPath, data);
            if (ext != null) SetOutput(outPath);
            _item.TempPath = outPath;
            MoveToDestination();
            DeleteMediaWork(_item, _cfg);
            long size = new FileInfo(_item.FilePath).Length;
            lock (_item.Sync) { _item.Segments.Clear(); _item.Segments.Add(new Segment(0, size, size)); }
            _item.Size = size;
            _item.Status = DownloadStatus.Complete;
            _item.DeleteState();
            SetInfo(ci, "Terminada");
        }

        List<byte[]> FetchSubtitleParts(ConnectionInfo ci)
        {
            string final;
            byte[] first = FetchBytes(_item.Url, -1, -1, out final);
            Counted(ci, first.Length);
            string text = Subtitles.DecodeText(first);
            if (!MediaManifest.LooksLikeHls(text) && !MediaManifest.LooksLikeDash(text)) return new List<byte[]> { first }; // el archivo mismo

            MediaManifest m;
            try { m = MediaManifest.Parse(text, final); }
            catch (Exception ex)
            {
                if (ex is FormatException || ex is XmlException)
                    throw new FatalDownloadException("La lista de reproducción del vídeo no es válida: " + ex.Message);
                throw;
            }
            MediaTrack t = FindSubtitleTrack(m, _item.MediaVideo);
            if (t == null)
                throw new FatalDownloadException(m.Subtitles.Count == 0 ? "La lista de reproducción no tiene subtítulos." : "La lista de reproducción ya no tiene esos subtítulos.");
            if (t.Playlist == null) t.Playlist = LoadPlaylist(t.Key);
            MediaPlaylist p = t.Playlist;
            if (p.Protected)
                throw new FatalDownloadException("IDMClone no puede descargar estos subtítulos porque están protegidos (DRM).");
            if (t.SubtitleFormat == "mp4" || p.Init != null)
                throw new FatalDownloadException("Estos subtítulos van dentro de trozos MP4: IDMClone no puede guardarlos aparte.");
            if (p.Segments.Count == 0) throw new FatalDownloadException("La lista de los subtítulos está vacía.");

            List<byte[]> parts = new List<byte[]>();
            _item.Status = DownloadStatus.Downloading;
            for (int i = 0; i < p.Segments.Count; i++)
            {
                if (_stop) throw new OperationCanceledException();
                MediaSegment s = p.Segments[i];
                SetInfo(ci, "Subtítulos, trozo " + (i + 1) + " de " + p.Segments.Count);
                string f;
                byte[] b = FetchBytes(s.Url, s.RangeStart, s.RangeLength, out f);
                if (!string.IsNullOrEmpty(s.KeyUrl)) b = DecryptSubtitle(b, s);
                Counted(ci, b.Length);
                parts.Add(b);
            }
            return parts;
        }

        // La pista elegida; si su dirección cambió (lleva un token que caduca), la de la misma ruta; sin elegir, la primera.
        static MediaTrack FindSubtitleTrack(MediaManifest m, string key)
        {
            if (string.IsNullOrEmpty(key)) return m.Subtitles.Count > 0 ? m.Subtitles[0] : null;
            MediaTrack t = m.FindSubtitle(key);
            if (t != null) return t;
            string path = key.Split('?')[0];
            foreach (MediaTrack s in m.Subtitles) if (s.Key.Split('?')[0] == path) return s;
            return m.Subtitles.Count == 1 ? m.Subtitles[0] : null;
        }

        void Counted(ConnectionInfo ci, int bytes)
        {
            lock (_item.Sync) _item.Segments[0].Current += bytes;
            System.Threading.Interlocked.Add(ref ci.Bytes, bytes);
        }

        // Trozo cifrado con AES-128 (raro en subtítulos, pero HLS lo permite).
        byte[] DecryptSubtitle(byte[] data, MediaSegment s)
        {
            byte[] iv = s.IV;
            if (iv == null)
            {
                iv = new byte[16];
                for (int i = 0; i < 8; i++) iv[15 - i] = (byte)(s.Sequence >> (i * 8));
            }
            try
            {
                using (Aes aes = Aes.Create())
                {
                    aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7; aes.Key = MediaKey(s.KeyUrl); aes.IV = iv;
                    using (ICryptoTransform d = aes.CreateDecryptor()) return d.TransformFinalBlock(data, 0, data.Length);
                }
            }
            catch (CryptographicException) { throw new FatalDownloadException("No se pudieron descifrar los subtítulos (clave AES-128 incorrecta)."); }
        }
    }
}
