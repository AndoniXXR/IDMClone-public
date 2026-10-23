using System;
using System.Collections.Generic;
using System.IO;

namespace IDMClone
{
    // MPEG-TS (trozos .ts de HLS) -> pistas para Mp4Writer. Vídeo H.264 y audio AAC (ADTS), lo habitual en HLS.
    // Los datos de cada pista se copian a un archivo propio (en el .ts van intercalados en paquetes de 188 bytes).
    public static class TsDemux
    {
        const int Packet = 188;

        class Pes
        {
            public int Pid, StreamType;
            public MemoryStream Data = new MemoryStream();
            public bool Open;
        }

        class VideoOut
        {
            public FileStream File; public string Path;
            public bool Hevc;
            public byte[] Vps, Sps, Pps;
            public readonly List<long> Dts = new List<long>(), Pts = new List<long>();
            public readonly List<long> Offsets = new List<long>();
            public readonly List<int> Sizes = new List<int>();
            public readonly List<bool> Sync = new List<bool>();
            public long LastTs = -1;
        }

        class AudioOut
        {
            public FileStream File; public string Path;
            public byte[] Pending = new byte[0];
            public int Profile = -1, Freq, Channels;
            public long FirstPts = -1;
            public readonly List<long> Offsets = new List<long>();
            public readonly List<int> Sizes = new List<int>();
        }

        static readonly int[] Rates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };

        public static bool LooksLikeTs(byte[] head)
        {
            return head.Length >= Packet + 1 && head[0] == 0x47 && head[Packet] == 0x47;
        }

        public static List<MediaTrackData> Demux(string tsPath, string workDir, Func<bool> cancelled)
        {
            Dictionary<int, Pes> streams = new Dictionary<int, Pes>();
            int pmtPid = -1;
            VideoOut video = null; AudioOut audio = null;
            int videoPid = -1, audioPid = -1;
            try
            {
                using (FileStream f = new FileStream(tsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                {
                    byte[] buf = new byte[Packet * 2048];
                    int have = 0;
                    while (true)
                    {
                        if (cancelled != null && cancelled()) throw new OperationCanceledException();
                        int r = f.Read(buf, have, buf.Length - have);
                        have += r;
                        if (have < Packet) break;
                        int pos = 0;
                        while (pos + Packet <= have)
                        {
                            if (buf[pos] != 0x47) { pos++; continue; } // volver a sincronizar
                            HandlePacket(buf, pos, streams, ref pmtPid, ref videoPid, ref audioPid, ref video, ref audio, workDir);
                            pos += Packet;
                        }
                        Buffer.BlockCopy(buf, pos, buf, 0, have - pos);
                        have -= pos;
                        if (r == 0) break;
                    }
                }
                foreach (Pes p in streams.Values) Flush(p, video, audio, videoPid, audioPid);
                List<MediaTrackData> tracks = new List<MediaTrackData>();
                if (video != null && video.Sizes.Count > 0) tracks.Add(BuildVideo(video));
                if (audio != null && audio.Sizes.Count > 0) tracks.Add(BuildAudio(audio));
                if (tracks.Count == 0) throw new NotSupportedException("El vídeo .ts no tiene pistas H.264 ni AAC.");
                return tracks;
            }
            finally
            {
                if (video != null && video.File != null) video.File.Dispose();
                if (audio != null && audio.File != null) audio.File.Dispose();
            }
        }

        static void HandlePacket(byte[] b, int o, Dictionary<int, Pes> streams, ref int pmtPid, ref int videoPid, ref int audioPid,
                                 ref VideoOut video, ref AudioOut audio, string workDir)
        {
            bool pusi = (b[o + 1] & 0x40) != 0;
            int pid = (b[o + 1] & 0x1F) << 8 | b[o + 2];
            int afc = b[o + 3] >> 4 & 3;
            if ((afc & 1) == 0) return; // sin datos
            int p = o + 4;
            if ((afc & 2) != 0) p += 1 + b[o + 4];
            int end = o + Packet;
            if (p >= end) return;

            if (pid == 0)
            {
                if (!pusi) return;
                p += 1 + b[p];
                int secLen = (b[p + 1] & 0x0F) << 8 | b[p + 2];
                for (int i = p + 8; i + 4 <= Math.Min(end, p + 3 + secLen - 4); i += 4)
                {
                    int prog = b[i] << 8 | b[i + 1];
                    if (prog != 0) { pmtPid = (b[i + 2] & 0x1F) << 8 | b[i + 3]; break; }
                }
                return;
            }
            if (pid == pmtPid)
            {
                if (!pusi || streams.Count > 0) return;
                p += 1 + b[p];
                if (b[p] != 2) return;
                int secLen = (b[p + 1] & 0x0F) << 8 | b[p + 2];
                int secEnd = Math.Min(end, p + 3 + secLen - 4);
                int infoLen = (b[p + 10] & 0x0F) << 8 | b[p + 11];
                for (int i = p + 12 + infoLen; i + 5 <= secEnd; )
                {
                    int type = b[i], epid = (b[i + 1] & 0x1F) << 8 | b[i + 2], esLen = (b[i + 3] & 0x0F) << 8 | b[i + 4];
                    Pes s = new Pes(); s.Pid = epid; s.StreamType = type;
                    streams[epid] = s;
                    if ((type == 0x1B || type == 0x24) && videoPid < 0)
                    {
                        videoPid = epid; video = new VideoOut(); video.Hevc = type == 0x24;
                        video.Path = Path.Combine(workDir, "ts-video.es");
                        video.File = new FileStream(video.Path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
                    }
                    else if (type == 0x0F && audioPid < 0)
                    {
                        audioPid = epid; audio = new AudioOut(); audio.Path = Path.Combine(workDir, "ts-audio.aac");
                        audio.File = new FileStream(audio.Path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
                    }
                    else if ((type == 0x03 || type == 0x04 || type == 0x81) && audioPid < 0)
                    {
                        // MP3 o AC-3 en .ts: no se pasan a MP4 (el archivo se queda como .ts).
                        throw new NotSupportedException("Audio no AAC en .ts");
                    }
                    i += 5 + esLen;
                }
                return;
            }
            Pes pes;
            if (!streams.TryGetValue(pid, out pes)) return;
            if (pid != videoPid && pid != audioPid) return;
            if (pusi)
            {
                Flush(pes, video, audio, videoPid, audioPid);
                pes.Open = true;
            }
            if (pes.Open) pes.Data.Write(b, p, end - p);
        }

        static long ReadTs(byte[] b, int o)
        {
            return ((long)(b[o] >> 1 & 7) << 30) | ((long)b[o + 1] << 22) | ((long)(b[o + 2] >> 1) << 15) | ((long)b[o + 3] << 7) | ((long)b[o + 4] >> 1);
        }

        static long Unwrap(long ts, ref long last)
        {
            if (last >= 0)
            {
                while (ts < last - (1L << 32)) ts += 1L << 33;
            }
            last = ts;
            return ts;
        }

        static void Flush(Pes pes, VideoOut video, AudioOut audio, int videoPid, int audioPid)
        {
            if (!pes.Open || pes.Data.Length < 9) { pes.Data.SetLength(0); pes.Open = false; return; }
            byte[] d = pes.Data.ToArray();
            pes.Data.SetLength(0); pes.Open = false;
            if (d[0] != 0 || d[1] != 0 || d[2] != 1) return;
            int flags = d[7], hlen = d[8];
            long pts = -1, dts = -1;
            if ((flags & 0x80) != 0 && d.Length >= 14) pts = ReadTs(d, 9);
            if ((flags & 0xC0) == 0xC0 && d.Length >= 19) dts = ReadTs(d, 14);
            int start = 9 + hlen;
            if (start > d.Length) return;
            if (pes.Pid == videoPid && video != null) Video(video, d, start, pts, dts);
            else if (pes.Pid == audioPid && audio != null) Audio(audio, d, start, pts);
        }

        static void Video(VideoOut v, byte[] d, int start, long pts, long dts)
        {
            if (pts < 0) return;
            if (dts < 0) dts = pts;
            long last = v.LastTs;
            dts = Unwrap(dts, ref last);
            pts = dts + ((pts - dts) % (1L << 33) + (1L << 33)) % (1L << 33);
            if (pts - dts > 90000 * 10) pts = dts; // desfase absurdo: ignorarlo
            v.LastTs = last;
            long offset = v.File.Position;
            int size = 0; bool idr = false;
            foreach (int[] nal in Nals(d, start))
            {
                int len = nal[1];
                if (v.Hevc)
                {
                    if (len < 2) continue;
                    int ht = d[nal[0]] >> 1 & 0x3F;
                    if (ht == 35) continue;                     // delimitador de unidad de acceso
                    if (ht == 32) { if (v.Vps == null) v.Vps = Sub(d, nal[0], len); continue; }
                    if (ht == 33) { if (v.Sps == null) v.Sps = Sub(d, nal[0], len); continue; }
                    if (ht == 34) { if (v.Pps == null) v.Pps = Sub(d, nal[0], len); continue; }
                    if (ht >= 16 && ht <= 21) idr = true;       // IRAP (IDR, CRA, BLA)
                }
                else
                {
                    int type = d[nal[0]] & 0x1F;
                    if (type == 9) continue;                    // delimitador de unidad de acceso
                    if (type == 7) { if (v.Sps == null) v.Sps = Sub(d, nal[0], len); continue; }
                    if (type == 8) { if (v.Pps == null) v.Pps = Sub(d, nal[0], len); continue; }
                    if (type == 5) idr = true;
                }
                v.File.WriteByte((byte)(len >> 24)); v.File.WriteByte((byte)(len >> 16)); v.File.WriteByte((byte)(len >> 8)); v.File.WriteByte((byte)len);
                v.File.Write(d, nal[0], len);
                size += 4 + len;
            }
            if (size == 0) return;
            v.Offsets.Add(offset); v.Sizes.Add(size); v.Dts.Add(dts); v.Pts.Add(pts); v.Sync.Add(idr);
        }

        static byte[] Sub(byte[] d, int o, int n) { byte[] r = new byte[n]; Buffer.BlockCopy(d, o, r, 0, n); return r; }

        // Unidades NAL de un flujo Annex B: {inicio, longitud}.
        static IEnumerable<int[]> Nals(byte[] d, int start)
        {
            int i = start, n = d.Length, nalStart = -1;
            while (i + 3 <= n)
            {
                if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1)
                {
                    if (nalStart >= 0)
                    {
                        int e = i;
                        while (e > nalStart && d[e - 1] == 0) e--;
                        if (e > nalStart) yield return new int[] { nalStart, e - nalStart };
                    }
                    i += 3; nalStart = i;
                    continue;
                }
                i++;
            }
            if (nalStart >= 0 && nalStart < n)
            {
                int e = n;
                while (e > nalStart && d[e - 1] == 0) e--;
                if (e > nalStart) yield return new int[] { nalStart, e - nalStart };
            }
        }

        static void Audio(AudioOut a, byte[] d, int start, long pts)
        {
            if (a.FirstPts < 0 && pts >= 0) a.FirstPts = pts;
            byte[] buf = new byte[a.Pending.Length + d.Length - start];
            Buffer.BlockCopy(a.Pending, 0, buf, 0, a.Pending.Length);
            Buffer.BlockCopy(d, start, buf, a.Pending.Length, d.Length - start);
            int i = 0;
            while (i + 7 <= buf.Length)
            {
                if (buf[i] != 0xFF || (buf[i + 1] & 0xF0) != 0xF0) { i++; continue; }
                bool noCrc = (buf[i + 1] & 1) != 0;
                int profile = buf[i + 2] >> 6 & 3, sfi = buf[i + 2] >> 2 & 0xF;
                int ch = (buf[i + 2] & 1) << 2 | buf[i + 3] >> 6 & 3;
                int len = (buf[i + 3] & 3) << 11 | buf[i + 4] << 3 | buf[i + 5] >> 5 & 7;
                int hdr = noCrc ? 7 : 9;
                if (len < hdr || sfi >= Rates.Length) { i++; continue; }
                if (i + len > buf.Length) break; // sigue en el siguiente PES
                if (a.Profile < 0) { a.Profile = profile; a.Freq = sfi; a.Channels = ch; }
                a.Offsets.Add(a.File.Position); a.Sizes.Add(len - hdr);
                a.File.Write(buf, i + hdr, len - hdr);
                i += len;
            }
            a.Pending = Sub(buf, i, buf.Length - i);
        }

        static MediaTrackData BuildVideo(VideoOut v)
        {
            if (v.Sps == null || v.Pps == null || v.Hevc && v.Vps == null) throw new NotSupportedException("El vídeo .ts no trae sus parámetros (SPS/PPS).");
            v.File.Flush();
            int w, h;
            byte[] entry;
            if (v.Hevc) entry = H265.Hvc1(v.Vps, v.Sps, v.Pps, out w, out h);
            else { H264.Size(v.Sps, out w, out h); entry = Avc1(w, h, v.Sps, v.Pps); }
            MediaTrackData t = new MediaTrackData();
            t.Handler = "vide"; t.Timescale = 90000; t.Width = w; t.Height = h;
            t.Sources.Add(v.Path);
            t.SampleEntry = entry;
            int n = v.Sizes.Count;
            // El vídeo empieza a verse en su primer PTS; la edición salta el retardo de los B-frames.
            long minCts = long.MaxValue;
            for (int i = 0; i < n; i++)
            {
                MediaSample s = new MediaSample();
                s.Source = 0; s.Offset = v.Offsets[i]; s.Size = v.Sizes[i];
                long next = i + 1 < n ? v.Dts[i + 1] : -1;
                long dur = next > v.Dts[i] ? next - v.Dts[i] : (i > 0 ? (long)t.Samples[i - 1].Duration : 3000);
                if (dur <= 0 || dur > 90000 * 5) dur = i > 0 ? t.Samples[i - 1].Duration : 3000;
                s.Duration = (uint)dur;
                s.Cts = (int)(v.Pts[i] - v.Dts[i]);
                s.Sync = v.Sync[i];
                t.Samples.Add(s);
                minCts = Math.Min(minCts, s.Cts);
            }
            if (t.Samples.Count > 0 && !t.Samples[0].Sync)
            {
                // Sin IDR al principio: el primero se marca como clave para que el archivo empiece a reproducirse.
                MediaSample s0 = t.Samples[0]; s0.Sync = true; t.Samples[0] = s0;
            }
            t.EditMediaTime = t.Samples.Count > 0 && t.Samples[0].Cts > 0 ? t.Samples[0].Cts : -1;
            t.FirstDts = v.Dts[0] + Math.Max(0, t.Samples[0].Cts);
            return t;
        }

        static MediaTrackData BuildAudio(AudioOut a)
        {
            a.File.Flush();
            int rate = Rates[a.Freq];
            MediaTrackData t = new MediaTrackData();
            t.Handler = "soun"; t.Timescale = (uint)rate;
            t.Sources.Add(a.Path);
            byte[] asc = { (byte)((a.Profile + 1) << 3 | a.Freq >> 1), (byte)((a.Freq & 1) << 7 | a.Channels << 3) };
            t.SampleEntry = Mp4a(a.Channels == 0 ? 2 : a.Channels, rate, asc);
            for (int i = 0; i < a.Sizes.Count; i++)
            {
                MediaSample s = new MediaSample();
                s.Source = 0; s.Offset = a.Offsets[i]; s.Size = a.Sizes[i]; s.Duration = 1024; s.Sync = true;
                t.Samples.Add(s);
            }
            // Mismo reloj que el vídeo (90 kHz -> muestras por segundo) para mantenerlos sincronizados.
            t.FirstDts = a.FirstPts < 0 ? 0 : (long)Math.Round(a.FirstPts * (double)rate / 90000);
            return t;
        }

        public static byte[] Avc1(int w, int h, byte[] sps, byte[] pps)
        {
            BoxBuilder b = new BoxBuilder();
            b.Begin("avc1");
            b.Zero(6); b.U16(1);                 // reservado, índice de referencia de datos
            b.Zero(16);                          // pre_defined, reservado
            b.U16((ushort)w); b.U16((ushort)h);
            b.U32(0x00480000); b.U32(0x00480000); b.U32(0); b.U16(1);
            b.Zero(32);                          // nombre del compresor
            b.U16(0x0018); b.U16(0xFFFF);
            b.Begin("avcC");
            b.U8(1); b.U8(sps[1]); b.U8(sps[2]); b.U8(sps[3]); b.U8(0xFF);
            b.U8(0xE1); b.U16((ushort)sps.Length); b.Bytes(sps);
            b.U8(1); b.U16((ushort)pps.Length); b.Bytes(pps);
            b.End();
            b.End();
            return b.ToArray();
        }

        public static byte[] Mp4a(int channels, int rate, byte[] asc)
        {
            BoxBuilder b = new BoxBuilder();
            b.Begin("mp4a");
            b.Zero(6); b.U16(1);
            b.Zero(8);
            b.U16((ushort)channels); b.U16(16); b.U16(0); b.U16(0);
            b.U32((uint)(rate > 65535 ? 0 : rate) << 16);
            b.Begin("esds");
            b.U32(0);
            int dsi = 2 + asc.Length, dcd = 13 + dsi, es = 3 + 2 + dcd + 3;
            b.U8(0x03); b.U8((byte)es); b.U16(1); b.U8(0);
            b.U8(0x04); b.U8((byte)dcd); b.U8(0x40); b.U8(0x15); b.U24(0); b.U32(0); b.U32(0);
            b.U8(0x05); b.U8((byte)asc.Length); b.Bytes(asc);
            b.U8(0x06); b.U8(1); b.U8(2);
            b.End();
            b.End();
            return b.ToArray();
        }
    }

    // Lo justo de H.264 para el MP4: el tamaño de imagen que dice el SPS.
    public static class H264
    {
        public class Bits
        {
            readonly byte[] _d; int _pos;
            public Bits(byte[] d) { _d = d; }
            public int U(int n) { int v = 0; for (int i = 0; i < n; i++) v = v << 1 | Bit(); return v; }
            public int Bit() { if (_pos >= _d.Length * 8) return 0; int v = _d[_pos >> 3] >> (7 - (_pos & 7)) & 1; _pos++; return v; }
            public int Ue() { int z = 0; while (Bit() == 0 && z < 32) z++; return (1 << z) - 1 + U(z); }
            public int Se() { int k = Ue(); return (k & 1) != 0 ? (k + 1) / 2 : -(k / 2); }
        }

        // Quita los bytes de prevención (00 00 03).
        public static byte[] Rbsp(byte[] nal)
        {
            List<byte> r = new List<byte>(nal.Length);
            int zeros = 0;
            for (int i = 0; i < nal.Length; i++)
            {
                if (zeros >= 2 && nal[i] == 3) { zeros = 0; continue; }
                r.Add(nal[i]);
                zeros = nal[i] == 0 ? zeros + 1 : 0;
            }
            return r.ToArray();
        }

        public static void Size(byte[] spsNal, out int width, out int height)
        {
            Bits b = new Bits(Rbsp(spsNal));
            b.U(8); // cabecera NAL
            int profile = b.U(8); b.U(8); b.U(8); b.Ue();
            int chroma = 1; bool fmoSep = false;
            if (profile == 100 || profile == 110 || profile == 122 || profile == 244 || profile == 44 || profile == 83 || profile == 86
                || profile == 118 || profile == 128 || profile == 138 || profile == 139 || profile == 134 || profile == 135)
            {
                chroma = b.Ue();
                if (chroma == 3) fmoSep = b.Bit() == 1;
                b.Ue(); b.Ue(); b.Bit();
                if (b.Bit() == 1)
                    for (int i = 0; i < (chroma != 3 ? 8 : 12); i++)
                        if (b.Bit() == 1)
                        {
                            int size = i < 6 ? 16 : 64, last = 8, next = 8;
                            for (int j = 0; j < size; j++)
                            {
                                if (next != 0) next = (last + b.Se() + 256) % 256;
                                last = next == 0 ? last : next;
                            }
                        }
            }
            b.Ue();
            int poc = b.Ue();
            if (poc == 0) b.Ue();
            else if (poc == 1) { b.Bit(); b.Se(); b.Se(); int n = b.Ue(); for (int i = 0; i < n; i++) b.Se(); }
            b.Ue(); b.Bit();
            int wMbs = b.Ue() + 1, hMap = b.Ue() + 1;
            int frameMbsOnly = b.Bit();
            if (frameMbsOnly == 0) b.Bit();
            b.Bit();
            int cl = 0, cr = 0, ct = 0, cb = 0;
            if (b.Bit() == 1) { cl = b.Ue(); cr = b.Ue(); ct = b.Ue(); cb = b.Ue(); }
            int arrayType = fmoSep ? 0 : chroma;
            int cropX = arrayType == 0 ? 1 : (arrayType == 3 ? 1 : 2);
            int cropY = (arrayType == 0 || arrayType == 2 || arrayType == 3 ? 1 : 2) * (2 - frameMbsOnly);
            width = wMbs * 16 - (cl + cr) * cropX;
            height = (2 - frameMbsOnly) * hMap * 16 - (ct + cb) * cropY;
        }
    }

    // HEVC (H.265) en .ts: la caja hvcC del MP4 sale del SPS (perfil, nivel, croma, profundidad, tamaño).
    public static class H265
    {
        public static byte[] Hvc1(byte[] vps, byte[] sps, byte[] pps, out int width, out int height)
        {
            H264.Bits b = new H264.Bits(H264.Rbsp(sps));
            b.U(16);                     // cabecera NAL
            b.U(4);                      // sps_video_parameter_set_id
            int maxSub = b.U(3);
            int nesting = b.U(1);
            // profile_tier_level: 2+1+5 bits, 32 de compatibilidad, 48 de restricciones, 8 de nivel.
            int ptl0 = b.U(8);
            uint compat = (uint)b.U(16) << 16 | (uint)b.U(16);
            long constraint = (long)(uint)b.U(16) << 32 | (long)(uint)b.U(16) << 16 | (long)(uint)b.U(16);
            int level = b.U(8);
            bool[] subProfile = new bool[maxSub], subLevel = new bool[maxSub];
            for (int i = 0; i < maxSub; i++) { subProfile[i] = b.Bit() == 1; subLevel[i] = b.Bit() == 1; }
            if (maxSub > 0) for (int i = maxSub; i < 8; i++) b.U(2);
            for (int i = 0; i < maxSub; i++)
            {
                if (subProfile[i]) { b.U(8); b.U(16); b.U(16); b.U(16); b.U(16); b.U(16); }
                if (subLevel[i]) b.U(8);
            }
            b.Ue();                      // sps_seq_parameter_set_id
            int chroma = b.Ue();
            if (chroma == 3) b.Bit();
            width = b.Ue(); height = b.Ue();
            if (b.Bit() == 1)
            {
                int l = b.Ue(), rr = b.Ue(), t = b.Ue(), bo = b.Ue();
                int sx = chroma == 1 || chroma == 2 ? 2 : 1, sy = chroma == 1 ? 2 : 1;
                width -= (l + rr) * sx; height -= (t + bo) * sy;
            }
            int depthLuma = b.Ue(), depthChroma = b.Ue();

            BoxBuilder x = new BoxBuilder();
            x.Begin("hvc1");
            x.Zero(6); x.U16(1); x.Zero(16);
            x.U16((ushort)width); x.U16((ushort)height);
            x.U32(0x00480000); x.U32(0x00480000); x.U32(0); x.U16(1); x.Zero(32); x.U16(0x0018); x.U16(0xFFFF);
            x.Begin("hvcC");
            x.U8(1); x.U8((byte)ptl0); x.U32(compat);
            x.U16((ushort)(constraint >> 32)); x.U32((uint)constraint);
            x.U8((byte)level);
            x.U16(0xF000); x.U8(0xFC); x.U8((byte)(0xFC | chroma)); x.U8((byte)(0xF8 | depthLuma)); x.U8((byte)(0xF8 | depthChroma));
            x.U16(0); x.U8((byte)((maxSub + 1) << 3 | nesting << 2 | 3));
            x.U8(3);
            byte[][] sets = { vps, sps, pps };
            for (int i = 0; i < 3; i++)
            {
                x.U8((byte)(0x80 | (32 + i))); x.U16(1); x.U16((ushort)sets[i].Length); x.Bytes(sets[i]);
            }
            x.End();
            x.End();
            return x.ToArray();
        }
    }
}
