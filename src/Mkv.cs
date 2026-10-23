using System;
using System.Collections.Generic;
using System.IO;

namespace IDMClone
{
    // Unión de vídeo y audio WebM/Matroska (DASH con VP9/Opus...): se copian los clusters de los dos archivos
    // ordenados por tiempo, el audio con otro número de pista, y se añade un índice (Cues) para poder saltar.
    public static class MkvMerge
    {
        const uint IdEbml = 0x1A45DFA3, IdSegment = 0x18538067, IdSeekHead = 0x114D9B74, IdInfo = 0x1549A966,
                   IdTracks = 0x1654AE6B, IdCluster = 0x1F43B675, IdCues = 0x1C53BB6B, IdTrackEntry = 0xAE,
                   IdTrackNumber = 0xD7, IdTrackUid = 0x73C5, IdTrackType = 0x83, IdTimecode = 0xE7, IdSimpleBlock = 0xA3,
                   IdBlockGroup = 0xA0, IdBlock = 0xA1, IdTimecodeScale = 0x2AD7B1, IdSeek = 0x4DBB, IdSeekId = 0x53AB,
                   IdSeekPos = 0x53AC, IdCuePoint = 0xBB, IdCueTime = 0xB3, IdCueTrackPos = 0xB7, IdCueTrack = 0xF7, IdCueClusterPos = 0xF1;

        public static bool LooksLikeWebm(byte[] head)
        {
            return head.Length >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3;
        }

        struct El { public uint Id; public long Start, DataStart, Size; public long End { get { return Size < 0 ? -1 : DataStart + Size; } } }

        class Parsed
        {
            public string Path;
            public byte[] EbmlHeader, Info;
            public long TimecodeScale = 1000000;
            public readonly List<byte[]> TrackEntries = new List<byte[]>();
            public readonly List<long[]> Clusters = new List<long[]>(); // {inicio, tamaño total, timecode}
        }

        static long ReadVint(Stream s, bool keepMarker, out int len)
        {
            int b = s.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            len = 1; int mask = 0x80;
            while (len <= 8 && (b & mask) == 0) { len++; mask >>= 1; }
            if (len > 8) throw new FormatException("EBML no válido.");
            long v = keepMarker ? b : b & (mask - 1);
            bool allOnes = (b & (mask - 1)) == mask - 1;
            for (int i = 1; i < len; i++)
            {
                int x = s.ReadByte(); if (x < 0) throw new EndOfStreamException();
                v = v << 8 | (uint)x;
                if (x != 0xFF) allOnes = false;
            }
            if (!keepMarker && allOnes) return -1; // tamaño desconocido
            return v;
        }

        static El ReadEl(Stream s, long pos)
        {
            s.Position = pos;
            El e = new El(); e.Start = pos; int l;
            e.Id = (uint)ReadVint(s, true, out l);
            e.Size = ReadVint(s, false, out l);
            e.DataStart = s.Position;
            return e;
        }

        static byte[] Bytes(Stream s, long pos, long n) { return Bmff.Read(s, pos, (int)n); }

        static ulong Uint(byte[] d) { ulong v = 0; foreach (byte b in d) v = v << 8 | b; return v; }

        static Parsed Parse(string path)
        {
            Parsed p = new Parsed(); p.Path = path;
            using (FileStream s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                Walk(s, 0, s.Length, p, true);
            if (p.EbmlHeader == null || p.TrackEntries.Count == 0) throw new FormatException("WebM sin cabecera o sin pistas: " + System.IO.Path.GetFileName(path));
            return p;
        }

        static void Walk(Stream s, long start, long end, Parsed p, bool top)
        {
            long pos = start;
            while (pos < end)
            {
                El e;
                try { e = ReadEl(s, pos); } catch (EndOfStreamException) { break; }
                long elEnd = e.Size < 0 ? end : Math.Min(end, e.End);
                switch (e.Id)
                {
                    case IdEbml: if (p.EbmlHeader == null) p.EbmlHeader = Bytes(s, e.Start, elEnd - e.Start); break;
                    case IdSegment: Walk(s, e.DataStart, elEnd, p, false); break;
                    case IdInfo:
                        if (p.Info == null)
                        {
                            p.Info = Bytes(s, e.Start, elEnd - e.Start);
                            for (long q = e.DataStart; q < elEnd; )
                            {
                                El c = ReadEl(s, q);
                                if (c.Id == IdTimecodeScale) p.TimecodeScale = (long)Uint(Bytes(s, c.DataStart, c.Size));
                                q = c.End;
                            }
                        }
                        break;
                    case IdTracks:
                        if (p.TrackEntries.Count == 0)
                            for (long q = e.DataStart; q < elEnd; )
                            {
                                El c = ReadEl(s, q);
                                if (c.Id == IdTrackEntry) p.TrackEntries.Add(Bytes(s, c.Start, c.End - c.Start));
                                q = c.End;
                            }
                        break;
                    case IdCluster:
                        if (e.Size < 0) throw new NotSupportedException("WebM con clusters de tamaño desconocido.");
                        long tc = 0;
                        for (long q = e.DataStart; q < elEnd; )
                        {
                            El c = ReadEl(s, q);
                            if (c.Id == IdTimecode) { tc = (long)Uint(Bytes(s, c.DataStart, c.Size)); break; }
                            q = c.End;
                        }
                        p.Clusters.Add(new long[] { e.Start, elEnd - e.Start, tc });
                        break;
                }
                if (e.Size < 0 && e.Id != IdSegment) break;
                pos = elEnd;
                if (e.Id == IdSegment && e.Size < 0) break;
            }
        }

        // Valor de un hijo de un TrackEntry (número, UID, tipo).
        static ulong TrackField(byte[] entry, uint id)
        {
            using (MemoryStream s = new MemoryStream(entry))
            {
                El e = ReadEl(s, 0);
                for (long q = e.DataStart; q < e.End; )
                {
                    El c = ReadEl(s, q);
                    if (c.Id == id) return Uint(Bytes(s, c.DataStart, c.Size));
                    q = c.End;
                }
            }
            return 0;
        }

        static byte[] RewriteTrack(byte[] entry, ulong number, ulong uid)
        {
            using (MemoryStream s = new MemoryStream(entry))
            {
                El e = ReadEl(s, 0);
                MemoryStream body = new MemoryStream();
                for (long q = e.DataStart; q < e.End; )
                {
                    El c = ReadEl(s, q);
                    if (c.Id == IdTrackNumber) WriteUint(body, IdTrackNumber, number);
                    else if (c.Id == IdTrackUid) WriteUint(body, IdTrackUid, uid);
                    else { byte[] raw = Bytes(s, c.Start, c.End - c.Start); body.Write(raw, 0, raw.Length); }
                    q = c.End;
                }
                MemoryStream o = new MemoryStream();
                WriteEl(o, IdTrackEntry, body.ToArray());
                return o.ToArray();
            }
        }

        static void WriteId(Stream o, uint id)
        {
            if (id > 0xFFFFFF) o.WriteByte((byte)(id >> 24));
            if (id > 0xFFFF) o.WriteByte((byte)(id >> 16));
            if (id > 0xFF) o.WriteByte((byte)(id >> 8));
            o.WriteByte((byte)id);
        }

        static void WriteSize8(Stream o, long size)
        {
            o.WriteByte(0x01);
            for (int i = 6; i >= 0; i--) o.WriteByte((byte)(size >> (i * 8)));
        }

        static void WriteEl(Stream o, uint id, byte[] data) { WriteId(o, id); WriteSize8(o, data.Length); o.Write(data, 0, data.Length); }

        static void WriteUint(Stream o, uint id, ulong v)
        {
            int n = 1; while (n < 8 && v >> (n * 8) != 0) n++;
            byte[] d = new byte[n];
            for (int i = 0; i < n; i++) d[n - 1 - i] = (byte)(v >> (i * 8));
            WriteEl(o, id, d);
        }

        static void WriteUint8(Stream o, uint id, ulong v) // tamaño fijo: para posiciones calculadas de antemano
        {
            byte[] d = new byte[8];
            for (int i = 0; i < 8; i++) d[7 - i] = (byte)(v >> (i * 8));
            WriteEl(o, id, d);
        }

        public static void Merge(string videoPath, string audioPath, string outPath, Func<bool> cancelled)
        {
            Parsed v = Parse(videoPath), a = Parse(audioPath);
            if (v.TimecodeScale != a.TimecodeScale) throw new NotSupportedException("El vídeo y el audio WebM usan escalas de tiempo distintas.");
            ulong vNum = TrackField(v.TrackEntries[0], IdTrackNumber), aNum = TrackField(a.TrackEntries[0], IdTrackNumber);
            ulong vUid = TrackField(v.TrackEntries[0], IdTrackUid), aUid = TrackField(a.TrackEntries[0], IdTrackUid);
            ulong newNum = aNum == vNum ? vNum + 1 : aNum;
            if (newNum > 126 || aNum > 126) throw new NotSupportedException("Número de pista WebM no admitido.");
            ulong newUid = aUid == vUid ? aUid + 1 : aUid;
            byte[] audioEntry = RewriteTrack(a.TrackEntries[0], newNum, newUid);

            // Orden de los clusters por tiempo (a igual tiempo, primero el vídeo).
            List<long[]> order = new List<long[]>(); // {archivo 0/1, inicio, tamaño, timecode}
            foreach (long[] c in v.Clusters) order.Add(new long[] { 0, c[0], c[1], c[2] });
            foreach (long[] c in a.Clusters) order.Add(new long[] { 1, c[0], c[1], c[2] });
            order.Sort(delegate(long[] x, long[] y) { int r = x[3].CompareTo(y[3]); return r != 0 ? r : x[0].CompareTo(y[0]); });

            // Disposición: SeekHead (tamaño fijo) | Info | Tracks | Clusters... | Cues.
            MemoryStream tracks = new MemoryStream();
            foreach (byte[] t in v.TrackEntries) { tracks.Write(t, 0, t.Length); break; }
            tracks.Write(audioEntry, 0, audioEntry.Length);
            MemoryStream tracksEl = new MemoryStream(); WriteEl(tracksEl, IdTracks, tracks.ToArray());
            byte[] tracksBytes = tracksEl.ToArray();
            byte[] seekHeadProbe = SeekHead(0, 0, 0);
            long infoPos = seekHeadProbe.Length, tracksPos = infoPos + v.Info.Length, clusterPos = tracksPos + tracksBytes.Length;
            long pos = clusterPos;
            List<long[]> cues = new List<long[]>(); // {timecode, posición relativa}
            foreach (long[] c in order)
            {
                if (c[0] == 0) cues.Add(new long[] { c[3], pos });
                pos += c[2];
            }
            MemoryStream cuesBody = new MemoryStream();
            foreach (long[] q in cues)
            {
                MemoryStream tp = new MemoryStream(); WriteUint(tp, IdCueTrack, vNum); WriteUint8(tp, IdCueClusterPos, (ulong)q[1]);
                MemoryStream cp = new MemoryStream(); WriteUint(cp, IdCueTime, (ulong)q[0]); WriteEl(cp, IdCueTrackPos, tp.ToArray());
                WriteEl(cuesBody, IdCuePoint, cp.ToArray());
            }
            MemoryStream cuesEl = new MemoryStream(); WriteEl(cuesEl, IdCues, cuesBody.ToArray());
            byte[] cuesBytes = cuesEl.ToArray();
            long cuesPos = pos;
            byte[] seekHead = SeekHead(infoPos, tracksPos, cuesPos);
            long segmentSize = cuesPos + cuesBytes.Length;

            using (FileStream o = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (FileStream vs = new FileStream(videoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (FileStream asr = new FileStream(audioPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                o.Write(v.EbmlHeader, 0, v.EbmlHeader.Length);
                WriteId(o, IdSegment); WriteSize8(o, segmentSize);
                o.Write(seekHead, 0, seekHead.Length);
                o.Write(v.Info, 0, v.Info.Length);
                o.Write(tracksBytes, 0, tracksBytes.Length);
                foreach (long[] c in order)
                {
                    if (cancelled != null && cancelled()) throw new OperationCanceledException();
                    byte[] data = Bytes(c[0] == 0 ? (Stream)vs : asr, c[1], c[2]);
                    if (c[0] == 1 && newNum != aNum) PatchTrackNumbers(data, (byte)(0x80 | (int)newNum));
                    o.Write(data, 0, data.Length);
                }
                o.Write(cuesBytes, 0, cuesBytes.Length);
            }
        }

        static byte[] SeekHead(long info, long tracks, long cues)
        {
            MemoryStream body = new MemoryStream();
            foreach (long[] e in new long[][] { new long[] { IdInfo, info }, new long[] { IdTracks, tracks }, new long[] { IdCues, cues } })
            {
                MemoryStream id = new MemoryStream(); WriteId(id, (uint)e[0]);
                MemoryStream seek = new MemoryStream(); WriteEl(seek, IdSeekId, id.ToArray()); WriteUint8(seek, IdSeekPos, (ulong)e[1]);
                WriteEl(body, IdSeek, seek.ToArray());
            }
            MemoryStream o = new MemoryStream(); WriteEl(o, IdSeekHead, body.ToArray());
            return o.ToArray();
        }

        // Cambia el número de pista (primer byte) de cada SimpleBlock/Block del cluster.
        static void PatchTrackNumbers(byte[] cluster, byte newTrackByte)
        {
            using (MemoryStream s = new MemoryStream(cluster))
            {
                El c = ReadEl(s, 0);
                for (long q = c.DataStart; q < cluster.Length; )
                {
                    El e = ReadEl(s, q);
                    if (e.Size < 0) break;
                    if (e.Id == IdSimpleBlock) cluster[e.DataStart] = newTrackByte;
                    else if (e.Id == IdBlockGroup)
                        for (long g = e.DataStart; g < e.End; )
                        {
                            El b = ReadEl(s, g);
                            if (b.Id == IdBlock) cluster[b.DataStart] = newTrackByte;
                            g = b.End;
                        }
                    q = e.End;
                }
            }
        }
    }
}
