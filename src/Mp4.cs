using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace IDMClone
{
    // Unión de pistas en MP4 sin programas externos (IDM: "Mezclar secuencias de audio y vídeo en un solo
    // archivo"). Lee MP4 fragmentado (DASH, HLS fMP4) y lo que saca TsDemux, y escribe un MP4 normal.

    public struct MediaSample
    {
        public int Source;      // índice del archivo de origen (MediaTrackData.Sources)
        public long Offset;
        public int Size;
        public uint Duration;
        public int Cts;         // composición - decodificación
        public bool Sync;
    }

    public class MediaTrackData
    {
        public string Handler;          // "vide" o "soun"
        public uint Timescale;
        public byte[] SampleEntry;      // caja de la descripción de muestras (avc1, mp4a, hvc1, Opus...)
        public int Width, Height;       // para tkhd
        public ushort Language = 0x55C4; // "und"
        public long FirstDts;           // decodificación de la primera muestra (en su escala)
        public long EditMediaTime = -1; // lista de edición original (retardo de B-frames, cebado de AAC)
        public readonly List<string> Sources = new List<string>();
        public readonly List<MediaSample> Samples = new List<MediaSample>();

        public bool IsVideo { get { return Handler == "vide"; } }
        public long TotalDuration { get { long d = 0; foreach (MediaSample s in Samples) d += s.Duration; return d; } }
        public double StartSeconds { get { return Timescale > 0 ? FirstDts / (double)Timescale : 0; } }
        public string Codec { get { return SampleEntry != null && SampleEntry.Length >= 8 ? Encoding.ASCII.GetString(SampleEntry, 4, 4) : "?"; } }
    }

    // Lectura de cajas ISO BMFF.
    public static class Bmff
    {
        public struct Box
        {
            public string Type;
            public long Start, Size;
            public int Header;
            public long DataStart { get { return Start + Header; } }
            public long End { get { return Start + Size; } }
        }

        public static uint U32(byte[] b, int o) { return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]); }
        public static ulong U64(byte[] b, int o) { return (ulong)U32(b, o) << 32 | U32(b, o + 4); }
        public static ushort U16(byte[] b, int o) { return (ushort)(b[o] << 8 | b[o + 1]); }

        public static byte[] Read(Stream s, long pos, int n)
        {
            byte[] b = new byte[n];
            s.Position = pos;
            int got = 0;
            while (got < n) { int r = s.Read(b, got, n - got); if (r <= 0) throw new EndOfStreamException(); got += r; }
            return b;
        }

        // Cajas entre start y end (sin entrar en ellas).
        public static List<Box> List(Stream s, long start, long end)
        {
            List<Box> r = new List<Box>();
            long pos = start;
            byte[] h = new byte[16];
            while (pos + 8 <= end)
            {
                s.Position = pos;
                if (s.Read(h, 0, 8) < 8) break;
                long size = U32(h, 0);
                string type = Encoding.ASCII.GetString(h, 4, 4);
                int header = 8;
                if (size == 1) { if (s.Read(h, 8, 8) < 8) break; size = (long)U64(h, 8); header = 16; }
                else if (size == 0) size = end - pos;
                if (size < header || pos + size > end) break; // caja cortada (archivo incompleto)
                Box b = new Box(); b.Type = type; b.Start = pos; b.Size = size; b.Header = header;
                r.Add(b);
                pos += size;
            }
            return r;
        }

        public static Box? Find(List<Box> l, string type)
        {
            foreach (Box b in l) if (b.Type == type) return b;
            return null;
        }

        public static List<Box> Children(Stream s, Box b) { return List(s, b.DataStart, b.End); }
        public static List<Box> Children(Stream s, Box b, int skip) { return List(s, b.DataStart + skip, b.End); }
        public static byte[] Payload(Stream s, Box b) { return Read(s, b.DataStart, (int)(b.Size - b.Header)); }
        public static byte[] Whole(Stream s, Box b) { return Read(s, b.Start, (int)b.Size); }

        public static bool IsMp4(byte[] head)
        {
            if (head.Length < 8) return false;
            string t = Encoding.ASCII.GetString(head, 4, 4);
            return t == "ftyp" || t == "styp" || t == "moov" || t == "moof" || t == "sidx" || t == "free" || t == "emsg";
        }
    }

    // MP4 fragmentado (o normal) -> pistas con sus muestras.
    public static class Fmp4Reader
    {
        class Trex { public uint Desc = 1, Dur, Size, Flags; }

        public static List<MediaTrackData> Read(string path, int sourceIndexBase)
        {
            using (FileStream s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                List<Bmff.Box> top = Bmff.List(s, 0, s.Length);
                Bmff.Box? moov = Bmff.Find(top, "moov");
                if (moov == null) throw new FormatException("El MP4 no tiene 'moov'.");
                Dictionary<uint, MediaTrackData> byId = new Dictionary<uint, MediaTrackData>();
                Dictionary<uint, Trex> trex = new Dictionary<uint, Trex>();
                List<MediaTrackData> order = new List<MediaTrackData>();
                Dictionary<uint, Bmff.Box> stbls = new Dictionary<uint, Bmff.Box>();
                foreach (Bmff.Box c in Bmff.Children(s, moov.Value))
                {
                    if (c.Type == "trak")
                    {
                        uint id; Bmff.Box stbl;
                        MediaTrackData t = ReadTrak(s, c, out id, out stbl);
                        if (t == null) continue;
                        t.Sources.Add(path);
                        byId[id] = t; order.Add(t); stbls[id] = stbl;
                    }
                    else if (c.Type == "mvex")
                        foreach (Bmff.Box x in Bmff.Children(s, c))
                            if (x.Type == "trex")
                            {
                                byte[] p = Bmff.Payload(s, x);
                                Trex tr = new Trex(); uint id = Bmff.U32(p, 4);
                                tr.Desc = Bmff.U32(p, 8); tr.Dur = Bmff.U32(p, 12); tr.Size = Bmff.U32(p, 16); tr.Flags = Bmff.U32(p, 20);
                                trex[id] = tr;
                            }
                }
                // MP4 normal (no fragmentado): las muestras están en la tabla stbl.
                foreach (KeyValuePair<uint, Bmff.Box> kv in stbls) ReadStbl(s, kv.Value, byId[kv.Key]);

                Dictionary<uint, long> nextDts = new Dictionary<uint, long>();
                bool firstFragment = true;
                foreach (Bmff.Box b in top)
                {
                    if (b.Type != "moof") continue;
                    foreach (Bmff.Box traf in Bmff.Children(s, b))
                    {
                        if (traf.Type != "traf") continue;
                        ReadTraf(s, b, traf, byId, trex, nextDts, firstFragment);
                    }
                    firstFragment = false;
                }
                return order;
            }
        }

        // Tipos de pista ("vide", "soun") de una inicialización MP4 ya bajada (EXT-X-MAP): para saber si es solo audio.
        public static List<string> Handlers(byte[] init)
        {
            List<string> r = new List<string>();
            using (MemoryStream s = new MemoryStream(init, false))
            {
                Bmff.Box? moov = Bmff.Find(Bmff.List(s, 0, s.Length), "moov");
                if (moov == null) return r;
                foreach (Bmff.Box c in Bmff.Children(s, moov.Value))
                {
                    if (c.Type != "trak") continue;
                    uint id; Bmff.Box stbl;
                    MediaTrackData t = ReadTrak(s, c, out id, out stbl);
                    if (t != null) r.Add(t.Handler);
                }
            }
            return r;
        }

        static MediaTrackData ReadTrak(Stream s, Bmff.Box trak, out uint id, out Bmff.Box stblOut)
        {
            id = 0; stblOut = new Bmff.Box();
            MediaTrackData t = new MediaTrackData();
            foreach (Bmff.Box c in Bmff.Children(s, trak))
            {
                if (c.Type == "tkhd")
                {
                    byte[] p = Bmff.Payload(s, c);
                    int v = p[0];
                    id = Bmff.U32(p, v == 1 ? 20 : 12);
                    int wOff = v == 1 ? 88 : 76;
                    if (p.Length >= wOff + 8) { t.Width = (int)(Bmff.U32(p, wOff) >> 16); t.Height = (int)(Bmff.U32(p, wOff + 4) >> 16); }
                }
                else if (c.Type == "edts")
                {
                    foreach (Bmff.Box e in Bmff.Children(s, c))
                        if (e.Type == "elst")
                        {
                            byte[] p = Bmff.Payload(s, e);
                            int v = p[0]; uint n = Bmff.U32(p, 4); int o = 8;
                            for (uint i = 0; i < n && o < p.Length; i++)
                            {
                                long mt = v == 1 ? (long)Bmff.U64(p, o + 8) : (int)Bmff.U32(p, o + 4);
                                o += v == 1 ? 20 : 12;
                                if (mt >= 0) { t.EditMediaTime = mt; break; }
                            }
                        }
                }
                else if (c.Type == "mdia")
                {
                    foreach (Bmff.Box m in Bmff.Children(s, c))
                    {
                        if (m.Type == "mdhd")
                        {
                            byte[] p = Bmff.Payload(s, m);
                            int v = p[0];
                            t.Timescale = Bmff.U32(p, v == 1 ? 20 : 12);
                            t.Language = Bmff.U16(p, v == 1 ? 32 : 20);
                        }
                        else if (m.Type == "hdlr") t.Handler = Encoding.ASCII.GetString(Bmff.Payload(s, m), 8, 4);
                        else if (m.Type == "minf")
                            foreach (Bmff.Box n in Bmff.Children(s, m))
                                if (n.Type == "stbl")
                                {
                                    stblOut = n;
                                    foreach (Bmff.Box d in Bmff.Children(s, n))
                                        if (d.Type == "stsd")
                                        {
                                            List<Bmff.Box> entries = Bmff.Children(s, d, 8);
                                            if (entries.Count > 0) t.SampleEntry = Bmff.Whole(s, entries[0]);
                                        }
                                }
                    }
                }
            }
            if (t.Handler != "vide" && t.Handler != "soun") return null; // subtítulos, pistas de pistas...
            if (t.SampleEntry == null || t.Timescale == 0) return null;
            string codec = t.Codec;
            if (codec == "encv" || codec == "enca") throw new NotSupportedException("El vídeo está protegido (cifrado) y no se puede descargar.");
            return t;
        }

        // MP4 normal: stts/ctts/stss/stsz/stsc/stco.
        static void ReadStbl(Stream s, Bmff.Box stbl, MediaTrackData t)
        {
            byte[] stts = null, ctts = null, stss = null, stsz = null, stsc = null, stco = null; bool co64 = false;
            foreach (Bmff.Box b in Bmff.Children(s, stbl))
            {
                switch (b.Type)
                {
                    case "stts": stts = Bmff.Payload(s, b); break;
                    case "ctts": ctts = Bmff.Payload(s, b); break;
                    case "stss": stss = Bmff.Payload(s, b); break;
                    case "stsz": stsz = Bmff.Payload(s, b); break;
                    case "stsc": stsc = Bmff.Payload(s, b); break;
                    case "stco": stco = Bmff.Payload(s, b); break;
                    case "co64": stco = Bmff.Payload(s, b); co64 = true; break;
                }
            }
            if (stsz == null || stco == null || stsc == null || stts == null) return;
            uint fixedSize = Bmff.U32(stsz, 4), count = Bmff.U32(stsz, 8);
            if (count == 0) return;
            uint[] durs = new uint[count]; int idx = 0;
            for (uint i = 0, n = Bmff.U32(stts, 4); i < n; i++)
            {
                uint c = Bmff.U32(stts, 8 + (int)i * 8), d = Bmff.U32(stts, 12 + (int)i * 8);
                for (uint k = 0; k < c && idx < count; k++) durs[idx++] = d;
            }
            int[] cts = new int[count]; idx = 0;
            if (ctts != null)
                for (uint i = 0, n = Bmff.U32(ctts, 4); i < n; i++)
                {
                    uint c = Bmff.U32(ctts, 8 + (int)i * 8); int o = (int)Bmff.U32(ctts, 12 + (int)i * 8);
                    for (uint k = 0; k < c && idx < count; k++) cts[idx++] = o;
                }
            HashSet<uint> sync = null;
            if (stss != null) { sync = new HashSet<uint>(); for (uint i = 0, n = Bmff.U32(stss, 4); i < n; i++) sync.Add(Bmff.U32(stss, 8 + (int)i * 4)); }
            uint chunks = Bmff.U32(stco, 4);
            uint stscN = Bmff.U32(stsc, 4);
            uint sample = 0;
            for (uint e = 0; e < stscN; e++)
            {
                uint first = Bmff.U32(stsc, 8 + (int)e * 12), per = Bmff.U32(stsc, 12 + (int)e * 12);
                uint last = e + 1 < stscN ? Bmff.U32(stsc, 8 + (int)(e + 1) * 12) - 1 : chunks;
                for (uint ch = first; ch <= last && sample < count; ch++)
                {
                    long off = co64 ? (long)Bmff.U64(stco, 8 + (int)(ch - 1) * 8) : Bmff.U32(stco, 8 + (int)(ch - 1) * 4);
                    for (uint k = 0; k < per && sample < count; k++)
                    {
                        MediaSample m = new MediaSample();
                        m.Source = 0; m.Offset = off;
                        m.Size = (int)(fixedSize != 0 ? fixedSize : Bmff.U32(stsz, 12 + (int)sample * 4));
                        m.Duration = durs[sample]; m.Cts = cts[sample];
                        m.Sync = sync == null || sync.Contains(sample + 1);
                        t.Samples.Add(m);
                        off += m.Size; sample++;
                    }
                }
            }
        }

        static void ReadTraf(Stream s, Bmff.Box moof, Bmff.Box traf, Dictionary<uint, MediaTrackData> byId, Dictionary<uint, Trex> trexs,
                             Dictionary<uint, long> nextDts, bool firstFragment)
        {
            List<Bmff.Box> kids = Bmff.Children(s, traf);
            Bmff.Box? tfhdBox = Bmff.Find(kids, "tfhd");
            if (tfhdBox == null) return;
            byte[] h = Bmff.Payload(s, tfhdBox.Value);
            uint flags = (uint)(h[1] << 16 | h[2] << 8 | h[3]);
            uint id = Bmff.U32(h, 4);
            MediaTrackData t;
            if (!byId.TryGetValue(id, out t)) return;
            Trex tr; if (!trexs.TryGetValue(id, out tr)) tr = new Trex();
            int o = 8;
            long baseOffset = moof.Start;
            if ((flags & 0x1) != 0) { baseOffset = (long)Bmff.U64(h, o); o += 8; }
            if ((flags & 0x2) != 0) o += 4;
            uint defDur = tr.Dur, defSize = tr.Size, defFlags = tr.Flags;
            if ((flags & 0x8) != 0) { defDur = Bmff.U32(h, o); o += 4; }
            if ((flags & 0x10) != 0) { defSize = Bmff.U32(h, o); o += 4; }
            if ((flags & 0x20) != 0) { defFlags = Bmff.U32(h, o); o += 4; }

            long dts;
            if (!nextDts.TryGetValue(id, out dts)) dts = 0;
            Bmff.Box? tfdt = Bmff.Find(kids, "tfdt");
            if (tfdt != null)
            {
                byte[] p = Bmff.Payload(s, tfdt.Value);
                dts = p[0] == 1 ? (long)Bmff.U64(p, 4) : Bmff.U32(p, 4);
            }
            if (t.Samples.Count == 0) t.FirstDts = dts;

            long dataPtr = baseOffset;
            foreach (Bmff.Box k in kids)
            {
                if (k.Type != "trun") continue;
                byte[] p = Bmff.Payload(s, k);
                int ver = p[0];
                uint tf = (uint)(p[1] << 16 | p[2] << 8 | p[3]);
                uint n = Bmff.U32(p, 4);
                int q = 8;
                if ((tf & 0x1) != 0) { dataPtr = baseOffset + (int)Bmff.U32(p, q); q += 4; }
                uint firstFlags = defFlags; bool hasFirst = false;
                if ((tf & 0x4) != 0) { firstFlags = Bmff.U32(p, q); q += 4; hasFirst = true; }
                for (uint i = 0; i < n; i++)
                {
                    uint dur = defDur, size = defSize, fl = (i == 0 && hasFirst) ? firstFlags : defFlags; int cts = 0;
                    if ((tf & 0x100) != 0) { dur = Bmff.U32(p, q); q += 4; }
                    if ((tf & 0x200) != 0) { size = Bmff.U32(p, q); q += 4; }
                    if ((tf & 0x400) != 0) { uint f2 = Bmff.U32(p, q); q += 4; if (!(i == 0 && hasFirst)) fl = f2; }
                    if ((tf & 0x800) != 0) { uint c = Bmff.U32(p, q); q += 4; cts = ver == 0 ? (int)Math.Min(c, int.MaxValue) : (int)c; }
                    MediaSample m = new MediaSample();
                    m.Source = 0; m.Offset = dataPtr; m.Size = (int)size; m.Duration = dur; m.Cts = cts;
                    m.Sync = !t.IsVideo || (fl & 0x10000) == 0;
                    t.Samples.Add(m);
                    dataPtr += size; dts += dur;
                }
            }
            nextDts[id] = dts;
        }
    }

    // Escritor de MP4 normal (moov delante, mdat detrás, pistas intercaladas por segundos).
    public static class Mp4Writer
    {
        const uint MovieTimescale = 1000;

        class Chunk { public MediaTrackData Track; public int First, Count; public double Time; public long Offset; }

        public static void Write(string outPath, List<MediaTrackData> tracks, Func<bool> cancelled)
        {
            if (tracks.Count == 0) throw new InvalidOperationException("No hay pistas que escribir.");
            // Trozos de ~1 s por pista, ordenados por tiempo: el archivo se puede reproducir mientras se lee.
            List<Chunk> chunks = new List<Chunk>();
            Dictionary<MediaTrackData, List<Chunk>> perTrack = new Dictionary<MediaTrackData, List<Chunk>>();
            double minStart = double.MaxValue;
            foreach (MediaTrackData t in tracks) minStart = Math.Min(minStart, t.StartSeconds);
            foreach (MediaTrackData t in tracks)
            {
                List<Chunk> mine = new List<Chunk>();
                long time = 0; int i = 0;
                while (i < t.Samples.Count)
                {
                    Chunk c = new Chunk(); c.Track = t; c.First = i; c.Time = time / (double)t.Timescale + (t.StartSeconds - minStart);
                    long limit = time + t.Timescale; long bytes = 0;
                    while (i < t.Samples.Count && (c.Count == 0 || time < limit && bytes < 4 << 20))
                    {
                        time += t.Samples[i].Duration; bytes += t.Samples[i].Size; c.Count++; i++;
                    }
                    mine.Add(c); chunks.Add(c);
                }
                perTrack[t] = mine;
            }
            chunks.Sort(delegate(Chunk a, Chunk b) { int r = a.Time.CompareTo(b.Time); return r != 0 ? r : (a.Track.IsVideo ? 0 : 1).CompareTo(b.Track.IsVideo ? 0 : 1); });

            byte[] ftyp = Ftyp();
            // El tamaño de moov no depende de los desplazamientos (co64 de 8 bytes): primero se mide.
            byte[] moov = Moov(tracks, perTrack, minStart);
            long mdatStart = ftyp.Length + moov.Length;
            long pos = mdatStart + 16;
            foreach (Chunk c in chunks)
            {
                c.Offset = pos;
                for (int k = 0; k < c.Count; k++) pos += c.Track.Samples[c.First + k].Size;
            }
            moov = Moov(tracks, perTrack, minStart);
            long mdatSize = pos - mdatStart;

            Dictionary<string, FileStream> open = new Dictionary<string, FileStream>();
            try
            {
                using (FileStream o = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    o.Write(ftyp, 0, ftyp.Length);
                    o.Write(moov, 0, moov.Length);
                    BoxBuilder mh = new BoxBuilder(); mh.U32(1); mh.Ascii("mdat"); mh.U64((ulong)mdatSize);
                    byte[] mhb = mh.ToArray(); o.Write(mhb, 0, mhb.Length);
                    byte[] buf = new byte[1 << 16];
                    foreach (Chunk c in chunks)
                    {
                        if (cancelled != null && cancelled()) throw new OperationCanceledException();
                        for (int k = 0; k < c.Count; k++)
                        {
                            MediaSample sm = c.Track.Samples[c.First + k];
                            string src = c.Track.Sources[sm.Source];
                            FileStream fs;
                            if (!open.TryGetValue(src, out fs)) { fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16); open[src] = fs; }
                            fs.Position = sm.Offset;
                            int left = sm.Size;
                            while (left > 0)
                            {
                                int r = fs.Read(buf, 0, Math.Min(buf.Length, left));
                                if (r <= 0) throw new EndOfStreamException("Falta un trozo del vídeo en " + Path.GetFileName(src));
                                o.Write(buf, 0, r); left -= r;
                            }
                        }
                    }
                }
            }
            finally { foreach (FileStream f in open.Values) f.Dispose(); }
        }

        static byte[] Ftyp()
        {
            BoxBuilder b = new BoxBuilder();
            b.Begin("ftyp"); b.Ascii("isom"); b.U32(0x200); b.Ascii("isom"); b.Ascii("iso2"); b.Ascii("avc1"); b.Ascii("mp41"); b.End();
            return b.ToArray();
        }

        static long ToMovie(long v, uint ts) { return ts == 0 ? 0 : (long)Math.Round(v * (double)MovieTimescale / ts); }

        static byte[] Moov(List<MediaTrackData> tracks, Dictionary<MediaTrackData, List<Chunk>> perTrack, double minStart)
        {
            BoxBuilder b = new BoxBuilder();
            long movieDur = 0;
            foreach (MediaTrackData t in tracks)
                movieDur = Math.Max(movieDur, ToMovie(t.TotalDuration, t.Timescale) + (long)Math.Round((t.StartSeconds - minStart) * MovieTimescale));
            b.Begin("moov");
            b.Begin("mvhd"); b.U32(0); b.U32(0); b.U32(0); b.U32(MovieTimescale); b.U32((uint)movieDur);
            b.U32(0x00010000); b.U16(0x0100); b.Zero(10); Matrix(b); b.Zero(24); b.U32((uint)tracks.Count + 1); b.End();
            for (int i = 0; i < tracks.Count; i++) Trak(b, tracks[i], (uint)(i + 1), perTrack[tracks[i]], minStart);
            b.End();
            return b.ToArray();
        }

        static void Matrix(BoxBuilder b)
        {
            b.U32(0x00010000); b.U32(0); b.U32(0); b.U32(0); b.U32(0x00010000); b.U32(0); b.U32(0); b.U32(0); b.U32(0x40000000);
        }

        static void Trak(BoxBuilder b, MediaTrackData t, uint id, List<Chunk> chunks, double minStart)
        {
            long mediaDur = t.TotalDuration;
            long delayMovie = (long)Math.Round((t.StartSeconds - minStart) * MovieTimescale);
            long mediaTime = t.EditMediaTime >= 0 ? t.EditMediaTime : 0;
            long trackDurMovie = ToMovie(Math.Max(0, mediaDur - mediaTime), t.Timescale) + delayMovie;
            b.Begin("trak");
            b.Begin("tkhd"); b.U32(3); b.U32(0); b.U32(0); b.U32(id); b.U32(0); b.U32((uint)trackDurMovie); b.Zero(8);
            b.U16(0); b.U16(0); b.U16((ushort)(t.IsVideo ? 0 : 0x0100)); b.U16(0); Matrix(b);
            b.U32((uint)(t.IsVideo ? t.Width : 0) << 16); b.U32((uint)(t.IsVideo ? t.Height : 0) << 16); b.End();
            // Lista de edición: retardo hasta que empieza esta pista y salto inicial de la original.
            if (delayMovie > 0 || mediaTime > 0)
            {
                b.Begin("edts"); b.Begin("elst"); b.U32(0);
                b.U32(delayMovie > 0 ? 2u : 1u);
                if (delayMovie > 0) { b.U32((uint)delayMovie); b.U32(0xFFFFFFFF); b.U32(0x00010000); }
                b.U32((uint)ToMovie(Math.Max(0, mediaDur - mediaTime), t.Timescale)); b.U32((uint)mediaTime); b.U32(0x00010000);
                b.End(); b.End();
            }
            b.Begin("mdia");
            b.Begin("mdhd"); b.U32(0); b.U32(0); b.U32(0); b.U32(t.Timescale); b.U32((uint)Math.Min(mediaDur, uint.MaxValue)); b.U16(t.Language); b.U16(0); b.End();
            b.Begin("hdlr"); b.U32(0); b.U32(0); b.Ascii(t.Handler); b.Zero(12); b.Bytes(Encoding.ASCII.GetBytes(t.IsVideo ? "VideoHandler\0" : "SoundHandler\0")); b.End();
            b.Begin("minf");
            if (t.IsVideo) { b.Begin("vmhd"); b.U32(1); b.Zero(8); b.End(); }
            else { b.Begin("smhd"); b.U32(0); b.Zero(4); b.End(); }
            b.Begin("dinf"); b.Begin("dref"); b.U32(0); b.U32(1); b.Begin("url "); b.U32(1); b.End(); b.End(); b.End();
            b.Begin("stbl");
            b.Begin("stsd"); b.U32(0); b.U32(1); b.Bytes(t.SampleEntry); b.End();
            // stts
            List<KeyValuePair<uint, uint>> runs = new List<KeyValuePair<uint, uint>>();
            foreach (MediaSample s in t.Samples)
            {
                if (runs.Count > 0 && runs[runs.Count - 1].Value == s.Duration) runs[runs.Count - 1] = new KeyValuePair<uint, uint>(runs[runs.Count - 1].Key + 1, s.Duration);
                else runs.Add(new KeyValuePair<uint, uint>(1, s.Duration));
            }
            b.Begin("stts"); b.U32(0); b.U32((uint)runs.Count); foreach (KeyValuePair<uint, uint> r in runs) { b.U32(r.Key); b.U32(r.Value); } b.End();
            // ctts
            bool anyCts = false, negative = false;
            foreach (MediaSample s in t.Samples) { if (s.Cts != 0) anyCts = true; if (s.Cts < 0) negative = true; }
            if (anyCts)
            {
                List<KeyValuePair<uint, int>> cr = new List<KeyValuePair<uint, int>>();
                foreach (MediaSample s in t.Samples)
                {
                    if (cr.Count > 0 && cr[cr.Count - 1].Value == s.Cts) cr[cr.Count - 1] = new KeyValuePair<uint, int>(cr[cr.Count - 1].Key + 1, s.Cts);
                    else cr.Add(new KeyValuePair<uint, int>(1, s.Cts));
                }
                b.Begin("ctts"); b.U32(negative ? 0x01000000u : 0u); b.U32((uint)cr.Count); foreach (KeyValuePair<uint, int> r in cr) { b.U32(r.Key); b.U32((uint)r.Value); } b.End();
            }
            // stss (solo si hay muestras que no son clave)
            bool allSync = true; foreach (MediaSample s in t.Samples) if (!s.Sync) { allSync = false; break; }
            if (!allSync)
            {
                List<uint> keys = new List<uint>();
                for (int i = 0; i < t.Samples.Count; i++) if (t.Samples[i].Sync) keys.Add((uint)i + 1);
                b.Begin("stss"); b.U32(0); b.U32((uint)keys.Count); foreach (uint k in keys) b.U32(k); b.End();
            }
            // stsc
            List<uint[]> sc = new List<uint[]>();
            for (int i = 0; i < chunks.Count; i++)
                if (sc.Count == 0 || sc[sc.Count - 1][1] != chunks[i].Count) sc.Add(new uint[] { (uint)i + 1, (uint)chunks[i].Count });
            b.Begin("stsc"); b.U32(0); b.U32((uint)sc.Count); foreach (uint[] e in sc) { b.U32(e[0]); b.U32(e[1]); b.U32(1); } b.End();
            // stsz
            b.Begin("stsz"); b.U32(0); b.U32(0); b.U32((uint)t.Samples.Count); foreach (MediaSample s in t.Samples) b.U32((uint)s.Size); b.End();
            // co64
            b.Begin("co64"); b.U32(0); b.U32((uint)chunks.Count); foreach (Chunk c in chunks) b.U64((ulong)c.Offset); b.End();
            b.End(); // stbl
            b.End(); // minf
            b.End(); // mdia
            b.End(); // trak
        }
    }

    // Escritura de cajas big-endian.
    public class BoxBuilder
    {
        readonly MemoryStream _m = new MemoryStream();
        readonly Stack<long> _open = new Stack<long>();

        public void Begin(string type) { _open.Push(_m.Position); U32(0); Ascii(type); }
        public void End()
        {
            long start = _open.Pop(), end = _m.Position;
            _m.Position = start; U32((uint)(end - start)); _m.Position = end;
        }
        public void U8(byte v) { _m.WriteByte(v); }
        public void U16(ushort v) { _m.WriteByte((byte)(v >> 8)); _m.WriteByte((byte)v); }
        public void U24(uint v) { _m.WriteByte((byte)(v >> 16)); _m.WriteByte((byte)(v >> 8)); _m.WriteByte((byte)v); }
        public void U32(uint v) { U16((ushort)(v >> 16)); U16((ushort)v); }
        public void U64(ulong v) { U32((uint)(v >> 32)); U32((uint)v); }
        public void Ascii(string s) { byte[] b = Encoding.ASCII.GetBytes(s); _m.Write(b, 0, b.Length); }
        public void Bytes(byte[] b) { _m.Write(b, 0, b.Length); }
        public void Zero(int n) { for (int i = 0; i < n; i++) _m.WriteByte(0); }
        public byte[] ToArray() { return _m.ToArray(); }
    }
}
