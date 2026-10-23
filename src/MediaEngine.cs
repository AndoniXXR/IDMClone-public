using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace IDMClone
{
    // Descarga de vídeo por streaming (HLS/DASH): baja los trozos con varias conexiones a la carpeta temporal
    // (DwnlData\<id>), descifra los AES-128, y al terminar une los trozos y mezcla vídeo y audio en un archivo.
    // Se reanuda por trozos: los que ya están en disco no se vuelven a bajar.
    public partial class DownloadEngine
    {
        class MediaJob
        {
            public char Track;            // 'v' o 'a'
            public long Index;            // 0 = inicialización (en directo: número de orden del trozo + 1)
            public int Chunk = -1;        // trozo de un archivo grande partido por rangos
            public string Url;
            public long RangeStart = -1, RangeLength = -1;
            public string KeyUrl; public byte[] IV; public long Sequence;
            public double Order;          // posición en el tiempo (para bajar vídeo y audio a la par)
            public string Path;
            public bool Done;
            public double Duration;       // segundos de vídeo (para el tiempo grabado en directo)
        }

        const long MediaChunk = 2L << 20;              // archivo único (DASH SegmentBase): trozos de 2 MB
        readonly Dictionary<string, byte[]> _keys = new Dictionary<string, byte[]>();
        long _mediaDoneBytes, _mediaDoneCount, _mediaDoneInit; int _mediaTotal;
        bool _live;               // emisión en directo: se graba hasta que se detiene o termina
        volatile bool _liveFinishing; // al detener una grabación: se une lo grabado (no es una pausa)
        bool MediaCancelled { get { return _stop && !_liveFinishing; } }
        const int LiveStartSegments = 3; // como los reproductores: se empieza cerca del final de la emisión

        public static string MediaWorkDir(DownloadItem it, AppConfig cfg) { return Path.Combine(cfg.TempFolderOrDefault, it.Id); }

        public static void DeleteMediaWork(DownloadItem it, AppConfig cfg)
        {
            try
            {
                string d = MediaWorkDir(it, cfg);
                if (Path.GetFileName(d) == it.Id && Directory.Exists(d)) Directory.Delete(d, true);
            }
            catch { }
        }

        void RunMedia()
        {
            if (_item.MediaKind == SubtitleKind) { RunSubtitles(); return; } // solo los subtítulos (Subtitles.cs)
            _item.Error = null;
            _item.Status = DownloadStatus.Connecting;
            string dir = MediaWorkDir(_item, _cfg);
            Directory.CreateDirectory(dir);

            MediaManifest man; MediaTrack v, a;
            ResolveMedia(out man, out v, out a);
            if (_stop) { MediaPaused(); return; }
            if (_live) { RunLive(dir, man, v, a); return; }

            List<MediaJob> jobs = new List<MediaJob>();
            if (v != null) AddMediaJobs(jobs, 'v', v.Playlist, dir);
            if (a != null) AddMediaJobs(jobs, 'a', a.Playlist, dir);
            if (_stop) { MediaPaused(); return; }
            jobs.Sort(delegate(MediaJob x, MediaJob y) { int r = x.Order.CompareTo(y.Order); return r != 0 ? r : x.Track.CompareTo(y.Track); });
            StartMediaProgress(jobs);
            _item.Status = DownloadStatus.Downloading;
            _item.SaveState();

            string host;
            try { host = new Uri(jobs.Count > 0 ? jobs[jobs.Count - 1].Url : _item.Url).Host; } catch { host = ""; }
            RunMediaWorkers(jobs, host);

            if (_stop) { MediaPaused(); return; }
            if (_fatal != null) { _item.Error = _fatal; _item.Status = DownloadStatus.Error; _item.SaveState(); return; }
            int missing = 0; foreach (MediaJob j in jobs) if (!j.Done) missing++;
            if (missing > 0)
            {
                _item.Status = DownloadStatus.Error;
                _item.Error = "Faltan " + missing + " trozos del vídeo" + (string.IsNullOrEmpty(_lastError) ? "." : ": " + _lastError);
                _item.SaveState();
                return;
            }

            // Unir y mezclar (IDM: "Mezclar secuencias de audio y vídeo en un solo archivo").
            lock (_conns) foreach (ConnectionInfo c in _conns) c.Info = "Uniendo los trozos del vídeo...";
            string output;
            try { output = AssembleMedia(dir, jobs, v, a); }
            catch (OperationCanceledException) { MediaPaused(); return; }
            _item.TempPath = output;
            MoveToDestination();
            DeleteMediaWork(_item, _cfg);
            long size = new FileInfo(_item.FilePath).Length;
            lock (_item.Sync) { _item.Segments.Clear(); _item.Segments.Add(new Segment(0, size, size)); }
            _item.Size = size;
            _item.Status = DownloadStatus.Complete;
            _item.DeleteState();
        }

        // --- Emisión en directo ---
        // Como IDM: se graba desde cerca del final de la emisión, bajando los trozos nuevos según aparecen.
        // Al detenerla (o cuando la emisión termina) se une lo grabado en un archivo normal.
        void RunLive(string dir, MediaManifest man, MediaTrack v, MediaTrack a)
        {
            _item.Size = -1; _item.Resumable = false;
            Dictionary<char, HashSet<long>> have = new Dictionary<char, HashSet<long>>();
            have['v'] = new HashSet<long>(); have['a'] = new HashSet<long>();
            long existing = 0;
            foreach (MediaJob j in RecordedJobs(dir)) { have[j.Track].Add(j.Index); existing += new FileInfo(j.Path).Length; }
            bool resumed = have['v'].Count + have['a'].Count > 0;
            lock (_item.Sync) { _item.Segments.Clear(); _item.Segments.Add(new Segment(0, existing, long.MaxValue)); }
            _item.Status = DownloadStatus.Downloading;
            _item.SaveState();
            string host;
            try { host = new Uri(_item.Url).Host; } catch { host = ""; }
            double recorded = 0;
            bool first = true, ended = false;
            int failures = 0;
            while (true)
            {
                List<MediaJob> batch = new List<MediaJob>();
                double wait = 4;
                foreach (MediaTrack t in new MediaTrack[] { v, a })
                {
                    if (t == null || _stop) continue;
                    char track = t == v ? 'v' : 'a';
                    MediaPlaylist p = t.Playlist;
                    if (!first)
                    {
                        try { p = RefreshLive(man, t); failures = 0; }
                        catch (OperationCanceledException) { continue; }
                        catch (Exception ex) { _lastError = ex.Message; if (++failures >= 3) ended = true; continue; }
                        if (p == null) { ended = true; continue; }
                        t.Playlist = p;
                    }
                    if (!p.Live) ended = true; // la emisión ha terminado (EXT-X-ENDLIST / MPD estático)
                    wait = Math.Min(wait, Math.Max(1, (p.TargetDuration > 0 ? p.TargetDuration : 4) / 2));
                    if (p.Init != null && !have[track].Contains(0)) { batch.Add(NewJob(track, 0, -1, p.Init, -1, dir)); have[track].Add(0); }
                    long maxHave = 0;
                    foreach (long x in have[track]) maxHave = Math.Max(maxHave, x);
                    int from = first && !resumed ? Math.Max(0, p.Segments.Count - LiveStartSegments) : 0;
                    for (int i = from; i < p.Segments.Count; i++)
                    {
                        MediaSegment s = p.Segments[i];
                        long idx = s.Sequence + 1;
                        // Solo lo nuevo: lo anterior a lo ya grabado es el pasado de la emisión (no se graba).
                        if (have[track].Contains(idx) || (!first || resumed) && idx <= maxHave) continue;
                        have[track].Add(idx);
                        MediaJob j = NewJob(track, idx, -1, s, idx, dir);
                        j.Duration = s.Duration;
                        batch.Add(j);
                    }
                }
                first = false;
                if (batch.Count > 0)
                {
                    batch.Sort(delegate(MediaJob x, MediaJob y) { int r = x.Index.CompareTo(y.Index); return r != 0 ? r : x.Track.CompareTo(y.Track); });
                    RunMediaWorkers(batch, host);
                    char main = v != null ? 'v' : 'a';
                    foreach (MediaJob j in batch) if (j.Done && j.Track == main && j.Index > 0) recorded += j.Duration;
                }
                lock (_conns) foreach (ConnectionInfo c in _conns) c.Info = "Grabando en directo: " + Clock(recorded);
                if (_stop || ended || _fatal != null) break; // un error definitivo no se arregla pidiendo más trozos
                for (int ms = 0; ms < wait * 1000 && !_stop; ms += 100) Thread.Sleep(100);
                if (_stop) break;
            }

            // Detener una grabación en directo es terminarla: se une lo grabado.
            _liveFinishing = true;
            List<MediaJob> done = RecordedJobs(dir);
            if (!done.Exists(delegate(MediaJob j) { return j.Index > 0; }))
            {
                _item.Status = DownloadStatus.Error;
                string why = _fatal ?? _lastError;
                _item.Error = "No se grabó nada de la emisión en directo" + (string.IsNullOrEmpty(why) ? "." : ": " + why);
                _item.SaveState();
                return;
            }
            lock (_conns) foreach (ConnectionInfo c in _conns) c.Info = "Uniendo los trozos del vídeo...";
            string output = AssembleMedia(dir, done, v, a);
            _item.TempPath = output;
            MoveToDestination();
            DeleteMediaWork(_item, _cfg);
            long size = new FileInfo(_item.FilePath).Length;
            lock (_item.Sync) { _item.Segments.Clear(); _item.Segments.Add(new Segment(0, size, size)); }
            _item.Size = size;
            _item.Status = DownloadStatus.Complete;
            _item.DeleteState();
        }

        static string Clock(double seconds)
        {
            TimeSpan t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return ((int)t.TotalHours).ToString("00") + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
        }

        // Trozos ya grabados en la carpeta de trabajo (v00000.seg = inicialización, v<orden+1>.seg = trozos).
        static List<MediaJob> RecordedJobs(string dir)
        {
            List<MediaJob> r = new List<MediaJob>();
            if (!Directory.Exists(dir)) return r;
            foreach (string f in Directory.GetFiles(dir, "*.seg"))
            {
                string n = Path.GetFileNameWithoutExtension(f);
                long idx;
                if (n.Length < 2 || n[0] != 'v' && n[0] != 'a' || !long.TryParse(n.Substring(1), out idx)) continue;
                MediaJob j = new MediaJob(); j.Track = n[0]; j.Index = idx; j.Path = f; j.Done = true;
                r.Add(j);
            }
            return r;
        }

        // Lista actualizada de una emisión en directo (HLS: su lista; DASH: el MPD otra vez, calculado a esta hora).
        MediaPlaylist RefreshLive(MediaManifest man, MediaTrack t)
        {
            if (man.Kind == "hls") return LoadPlaylist(t.Key);
            string final;
            string text = FetchText(_item.Url, out final);
            MediaManifest m = Dash.Parse(text, final, DateTime.UtcNow);
            MediaTrack n = t.Kind == "audio" ? m.FindAudio(t.Key) : m.FindVideo(t.Key);
            if (n == null) return null;
            n.Playlist.Live = m.Live;
            return n.Playlist;
        }

        void MediaPaused()
        {
            _item.Status = DownloadStatus.Paused;
            _item.SaveState();
        }

        // --- Lista de reproducción y pistas elegidas ---

        bool _probeOnly; // sonda del panel: sin reintentos (el navegador espera la respuesta)

        string FetchText(string url, out string finalUrl) { return Subtitles.DecodeText(FetchBytes(url, -1, -1, out finalUrl)); }

        // Un archivo pequeño entero (lista de reproducción, trozo de subtítulos), o el rango pedido.
        byte[] FetchBytes(string url, long rangeStart, long rangeLength, out string finalUrl)
        {
            int busy = 0; // 429/503 seguidos
            for (int attempt = 1; ; attempt++)
            {
                int waitMs = -1;
                HttpWebRequest req = (HttpWebRequest)NewRequest(url);
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                if (rangeStart >= 0 && rangeLength > 0) req.AddRange(rangeStart, rangeStart + rangeLength - 1);
                Register(req);
                try
                {
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (Stream s = resp.GetResponseStream())
                    using (MemoryStream ms = new MemoryStream())
                    {
                        finalUrl = resp.ResponseUri.AbsoluteUri;
                        s.CopyTo(ms);
                        byte[] data = ms.ToArray();
                        // Rango pedido y servidor que manda el archivo entero: se recorta.
                        if (rangeStart >= 0 && rangeLength > 0 && resp.StatusCode != HttpStatusCode.PartialContent && data.Length > rangeStart)
                        {
                            byte[] cut = new byte[Math.Min(rangeLength, data.Length - rangeStart)];
                            Buffer.BlockCopy(data, (int)rangeStart, cut, 0, cut.Length);
                            data = cut;
                        }
                        return data;
                    }
                }
                catch (WebException wex)
                {
                    if (_stop) throw new OperationCanceledException();
                    string target = Util.RedirectTarget(wex);
                    if (target != null && attempt < 10) { url = target; continue; }
                    // Sin conexión directa: por el proxy del navegador, como las demás descargas (no gasta intento).
                    if (!_probeOnly && TrySwitchToBrowserProxy(req, wex)) { attempt--; continue; }
                    string perm = PermanentError(wex);
                    if (perm != null) throw new FatalDownloadException(perm);
                    if (_probeOnly || attempt >= Math.Max(1, _cfg.RetryCount)) throw;
                    _lastError = Describe(wex);
                    // 429/503: lo que pida el servidor (Retry-After; si es demasiado, error con el motivo) o cada vez un poco más.
                    if (IsBusy(wex)) waitMs = BusyWaitMs(wex, ++busy);
                }
                finally { Unregister(req); }
                if (waitMs >= 0) SleepMs(waitMs); else SleepRetry();
                if (_stop) throw new OperationCanceledException();
            }
        }

        void ResolveMedia(out MediaManifest man, out MediaTrack v, out MediaTrack a)
        {
            // Vídeo y audio en archivos aparte que el reproductor pide a trozos (como Facebook o Instagram).
            if (_item.MediaKind == "pair")
            {
                man = new MediaManifest(); man.Kind = "pair"; man.Url = _item.Url;
                v = string.IsNullOrEmpty(_item.MediaVideo) ? null : WholeFile("video", _item.MediaVideo);
                a = string.IsNullOrEmpty(_item.MediaAudio) || _item.MediaAudio == "-" ? null : WholeFile("audio", _item.MediaAudio);
                if (v == null && a == null) throw new FatalDownloadException("La lista de reproducción no tiene ningún vídeo.");
                return;
            }
            string final;
            string text = FetchText(_item.Url, out final);
            try { man = MediaManifest.Parse(text, final); }
            catch (Exception ex)
            {
                if (ex is FormatException || ex is System.Xml.XmlException)
                    throw new FatalDownloadException("La lista de reproducción del vídeo no es válida: " + ex.Message);
                throw;
            }

            bool noAudio = _item.MediaAudio == "-";
            bool audioOnly = string.IsNullOrEmpty(_item.MediaVideo) && !string.IsNullOrEmpty(_item.MediaAudio) && !noAudio;
            v = audioOnly ? null : (man.FindVideo(_item.MediaVideo) ?? (man.Videos.Count > 0 ? man.Videos[0] : null));
            if (v == null && !audioOnly && man.Audios.Count > 0) audioOnly = true;
            a = null;
            if (!noAudio)
            {
                a = man.FindAudio(_item.MediaAudio);
                if (a == null) a = v != null ? man.AudioFor(v) : (man.Audios.Count > 0 ? man.Audios[0] : null);
            }
            if (v == null && a == null) throw new FatalDownloadException("La lista de reproducción no tiene ningún vídeo.");
            if (v != null && v.Playlist == null) v.Playlist = LoadPlaylist(v.Key);
            if (a != null && a.Playlist == null) a.Playlist = LoadPlaylist(a.Key);
            foreach (MediaTrack t in new MediaTrack[] { v, a })
            {
                if (t == null) continue;
                if (t.Playlist.Protected || man.Protected && man.Kind == "dash")
                    throw new FatalDownloadException("IDMClone no puede descargar este contenido de stream porque está protegido (DRM). " +
                        "No se pueden eludir las medidas técnicas de protección del audio y del vídeo.");
                if (t.Playlist.Live || man.Live) { _live = true; continue; } // se graba (como IDM)
                if (t.Playlist.Segments.Count == 0) throw new FatalDownloadException("La lista de reproducción del vídeo está vacía.");
            }
        }

        // Panel "Descargar este video": calidades de la lista que reproduce la página, para elegir una.
        public static string ProbeMedia(AppConfig cfg, string url, string cookies, string referrer, string userAgent)
        {
            DownloadItem it = new DownloadItem();
            it.Id = "probe"; it.Url = url; it.FilePath = Path.Combine(Path.GetTempPath(), "idmclone-probe");
            it.Cookies = string.IsNullOrEmpty(cookies) ? null : cookies;
            it.Referrer = string.IsNullOrEmpty(referrer) ? null : referrer;
            it.UserAgentOverride = string.IsNullOrEmpty(userAgent) ? null : userAgent;
            DownloadEngine e = new DownloadEngine(it, cfg);
            e._probeOnly = true;
            string final;
            string text = e.FetchText(url, out final);
            MediaManifest m = MediaManifest.Parse(text, final);
            // HLS: la duración está en la lista de cada calidad (se mira la más ligera).
            if (m.Kind == "hls" && m.Duration <= 0 && m.Videos.Count > 0)
            {
                // La más ligera y, si falla, la mejor.
                foreach (MediaTrack t in new MediaTrack[] { m.Videos[m.Videos.Count - 1], m.Videos[0] })
                {
                    try
                    {
                        MediaPlaylist p = t.Playlist ?? e.LoadPlaylist(t.Key);
                        m.Duration = p.Duration; m.Live = m.Live || p.Live; m.Protected = m.Protected || p.Protected;
                        break;
                    }
                    catch (Exception) { }
                }
            }
            List<object> items = new List<object>();
            foreach (MediaTrack v in m.Videos)
            {
                MediaTrack a = m.AudioFor(v);
                Dictionary<string, object> o = new Dictionary<string, object>();
                o["kind"] = "video"; o["video"] = v.Key; o["audio"] = a != null ? a.Key : null;
                o["width"] = v.Width; o["height"] = v.Height;
                long bw = v.Bandwidth + (a != null && v.Kind == "video" ? a.Bandwidth : 0);
                o["bandwidth"] = bw;
                o["size"] = !m.Live && m.Duration > 0 && bw > 0 ? (long)(bw / 8.0 * m.Duration) : -1; // en directo no hay tamaño
                o["ext"] = m.Kind == "dash" && v.Container == "webm" ? ".webm" : ".mp4";
                o["codecs"] = v.Codecs;
                items.Add(o);
            }
            // "solo audio": la mejor pista de cada idioma (como IDM, que ofrece también el audio aparte).
            Dictionary<string, MediaTrack> byLang = new Dictionary<string, MediaTrack>();
            foreach (MediaTrack a in m.Audios)
            {
                string lang = a.Language ?? "";
                MediaTrack cur;
                if (!byLang.TryGetValue(lang, out cur) || (a.Container == "mp4" && cur.Container != "mp4") || (a.Container == cur.Container && a.Bandwidth > cur.Bandwidth))
                    byLang[lang] = a;
            }
            foreach (MediaTrack a in byLang.Values)
            {
                Dictionary<string, object> o = new Dictionary<string, object>();
                o["kind"] = "audio"; o["video"] = null; o["audio"] = a.Key; o["language"] = a.Language;
                o["bandwidth"] = a.Bandwidth;
                o["size"] = !m.Live && m.Duration > 0 && a.Bandwidth > 0 ? (long)(a.Bandwidth / 8.0 * m.Duration) : -1;
                o["ext"] = a.Container == "webm" ? ".webm" : ".m4a";
                items.Add(o);
            }
            // Subtítulos (como IDM): se bajan aparte, junto al vídeo. Los que van dentro de trozos MP4 no se ofrecen.
            foreach (MediaTrack s in m.Subtitles)
            {
                string fmt = s.SubtitleFormat;
                if (fmt == "mp4" || s.Playlist != null && (s.Playlist.Protected || s.Playlist.Init != null)) continue;
                Dictionary<string, object> o = new Dictionary<string, object>();
                o["kind"] = "subtitle"; o["subtitle"] = s.Key; o["language"] = s.Language; o["name"] = s.Name;
                o["ext"] = fmt == "ttml" || cfg.SubtitlesToSrt ? ".srt" : ".vtt"; // el formato del archivo que sale
                o["size"] = -1;
                items.Add(o);
            }
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["ok"] = true; r["kind"] = m.Kind; r["url"] = final; r["duration"] = Math.Round(m.Duration, 2);
            r["live"] = m.Live; r["protected"] = m.Protected; r["items"] = items;
            // Las listas de cada calidad: la extensión no las toma por vídeos nuevos.
            List<string> children = new List<string>();
            foreach (MediaTrack t in m.Videos) if (m.Kind == "hls" && t.Key != final) children.Add(t.Key);
            foreach (MediaTrack t in m.Audios) if (m.Kind == "hls") children.Add(t.Key);
            foreach (MediaTrack t in m.Subtitles) if (m.Kind == "hls" && t.Key != final) children.Add(t.Key);
            r["children"] = children;
            return new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(r);
        }

        static MediaTrack WholeFile(string kind, string url)
        {
            MediaTrack t = new MediaTrack(); t.Kind = kind; t.Key = url;
            t.Playlist = new MediaPlaylist();
            MediaSegment s = new MediaSegment(); s.Url = url;
            t.Playlist.Segments.Add(s);
            return t;
        }

        MediaPlaylist LoadPlaylist(string url)
        {
            string final;
            string text = FetchText(url, out final);
            if (!MediaManifest.LooksLikeHls(text)) throw new FatalDownloadException("La lista de una calidad del vídeo no es válida.");
            return Hls.ParseMedia(text, final);
        }

        // --- Trozos ---

        void AddMediaJobs(List<MediaJob> jobs, char track, MediaPlaylist p, string dir)
        {
            double total = Math.Max(0.001, p.Duration);
            if (p.Init != null) jobs.Add(NewJob(track, 0, -1, p.Init, -1, dir));
            // Un único archivo con todo (DASH SegmentBase): se parte por rangos para bajarlo con varias conexiones.
            if (p.Segments.Count == 1 && p.Segments[0].RangeStart < 0 && p.Init == null)
            {
                MediaSegment whole = p.Segments[0];
                long size = RemoteSize(whole.Url);
                if (size > MediaChunk * 2)
                {
                    int n = (int)((size + MediaChunk - 1) / MediaChunk);
                    for (int c = 0; c < n; c++)
                    {
                        MediaJob j = NewJob(track, 1, c, whole, c / (double)n, dir);
                        j.RangeStart = c * MediaChunk; j.RangeLength = Math.Min(MediaChunk, size - j.RangeStart);
                        jobs.Add(j);
                    }
                    return;
                }
            }
            double t = 0;
            for (int i = 0; i < p.Segments.Count; i++)
            {
                jobs.Add(NewJob(track, i + 1, -1, p.Segments[i], t / total, dir));
                t += p.Segments[i].Duration;
            }
        }

        static MediaJob NewJob(char track, long index, int chunk, MediaSegment s, double order, string dir)
        {
            MediaJob j = new MediaJob();
            j.Track = track; j.Index = index; j.Chunk = chunk; j.Url = s.Url;
            j.RangeStart = s.RangeStart; j.RangeLength = s.RangeLength;
            j.KeyUrl = s.KeyUrl; j.IV = s.IV; j.Sequence = s.Sequence;
            j.Order = index == 0 ? -1 : order;
            j.Path = Path.Combine(dir, track + index.ToString("D5") + (chunk >= 0 ? "-" + chunk.ToString("D5") : "") + ".seg");
            j.Done = File.Exists(j.Path);
            return j;
        }

        // Tamaño del archivo en el servidor (Range 0-0), o -1 si no admite rangos.
        long RemoteSize(string url)
        {
            HttpWebRequest req = (HttpWebRequest)NewRequest(url);
            req.AddRange(0L, 0L);
            Register(req);
            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    long s0, s1, total;
                    if (resp.StatusCode == HttpStatusCode.PartialContent && ParseContentRange(resp.Headers["Content-Range"], out s0, out s1, out total)) return total;
                    return -1;
                }
            }
            catch (WebException) { return -1; }
            finally { Unregister(req); }
        }

        void StartMediaProgress(List<MediaJob> jobs)
        {
            _mediaTotal = 0; _mediaDoneBytes = 0; _mediaDoneCount = 0; _mediaDoneInit = 0;
            foreach (MediaJob j in jobs)
            {
                if (j.Index > 0) _mediaTotal++;
                if (!j.Done) continue;
                long len = new FileInfo(j.Path).Length;
                if (j.Index == 0) _mediaDoneInit += len; else { _mediaDoneBytes += len; _mediaDoneCount++; }
            }
            lock (_item.Sync)
            {
                _item.Segments.Clear();
                _item.Segments.Add(new Segment(0, _mediaDoneBytes + _mediaDoneInit, Math.Max(1, EstimateMediaSize())));
            }
            _item.Size = EstimateMediaSize();
            _item.Resumable = true;
        }

        // Tamaño total estimado: lo bajado + la media de los trozos por los que faltan.
        long EstimateMediaSize()
        {
            if (_mediaDoneCount == 0) return -1;
            return _mediaDoneInit + (long)(_mediaDoneBytes / (double)_mediaDoneCount * _mediaTotal);
        }

        void MediaJobFinished(MediaJob j, long bytes)
        {
            if (_live) return; // en directo no hay tamaño total que estimar
            lock (_item.Sync)
            {
                if (j.Index == 0) _mediaDoneInit += bytes; else { _mediaDoneBytes += bytes; _mediaDoneCount++; }
                long est = EstimateMediaSize();
                Segment s = _item.Segments[0];
                if (est > 0) { s.End = Math.Max(est, s.Current); _item.Size = s.End; }
            }
        }

        void RunMediaWorkers(List<MediaJob> jobs, string host)
        {
            Queue<MediaJob> q = new Queue<MediaJob>();
            foreach (MediaJob j in jobs) if (!j.Done) q.Enqueue(j);
            int n = Math.Max(1, Math.Min(_cfg.ConnectionsFor(host), q.Count));
            List<Thread> threads = new List<Thread>();
            for (int i = 0; i < n; i++)
            {
                Thread t = new Thread(delegate() { MediaWorker(q); });
                t.IsBackground = true; t.Name = "idm-media-" + _item.Id + "-" + (i + 1);
                threads.Add(t); t.Start();
            }
            foreach (Thread t in threads) t.Join();
        }

        void MediaWorker(Queue<MediaJob> q)
        {
            ConnectionInfo ci = NewConnection("Conectando...");
            try
            {
                while (!Halted)
                {
                    MediaJob j;
                    lock (q) { if (q.Count == 0) break; j = q.Dequeue(); }
                    SetInfo(ci, (j.Track == 'v' ? "Vídeo" : "Audio") + ", trozo " + j.Index + (j.Chunk >= 0 ? "." + (j.Chunk + 1) : "") + (_live ? "" : " de " + Math.Max(1, _mediaTotal)));
                    if (!DownloadMediaJob(j, ci) && !Halted) SetInfo(ci, "Error: " + _lastError);
                }
            }
            catch (Exception ex)
            {
                // Red de seguridad: un error sin capturar en este hilo cerraría IDMClone entero.
                if (!_stop && _fatal == null) { _fatal = ex.Message; AbortActive(); }
            }
            SetInfo(ci, _stop ? "Detenida" : "Terminada");
        }

        bool DownloadMediaJob(MediaJob j, ConnectionInfo ci)
        {
            int attempts = Math.Max(1, _cfg.RetryCount);
            string part = j.Path + ".part";
            int busy = 0; // 429/503 seguidos
            for (int attempt = 1; ; attempt++)
            {
                if (Halted) return false;
                long got = 0;
                int waitMs = -1;
                HttpWebRequest req = null;
                try
                {
                    req = (HttpWebRequest)NewRequest(j.Url);
                    long limit = long.MaxValue;
                    if (j.RangeStart >= 0) { req.AddRange(j.RangeStart, j.RangeStart + j.RangeLength - 1); limit = j.RangeLength; }
                    Register(req);
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (Stream net = resp.GetResponseStream())
                    using (FileStream fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        // Si el servidor no respeta el rango, se salta hasta donde empieza el trozo.
                        long skip = j.RangeStart > 0 && resp.StatusCode != HttpStatusCode.PartialContent ? j.RangeStart : 0;
                        if (limit == long.MaxValue && resp.ContentLength > 0) limit = resp.ContentLength;
                        byte[] buf = new byte[BufferSize];
                        while (got < limit && !Halted)
                        {
                            int want = (int)Math.Min(buf.Length, skip > 0 ? skip : limit - got);
                            int globalLimit = _cfg.EffectiveSpeedLimitKBps, itemLimit = ItemSpeedLimit;
                            want = TokenBucket.MaxChunk(TokenBucket.MaxChunk(want, globalLimit), itemLimit);
                            if (want <= 0) break;
                            int read = net.Read(buf, 0, want);
                            if (read <= 0) break;
                            SpeedLimiter.Consume(read, globalLimit, delegate { return Halted; });
                            _itemBucket.Consume(read, itemLimit, delegate { return Halted; });
                            DownloadQuota.Consume(read, _cfg, delegate { return Halted; });
                            if (skip > 0) { skip -= read; continue; }
                            fs.Write(buf, 0, read);
                            got += read;
                            lock (_item.Sync) _item.Segments[0].Current += read;
                            Interlocked.Add(ref ci.Bytes, read);
                        }
                    }
                    if (Halted) { try { req.Abort(); } catch { } throw new OperationCanceledException(); }
                    if (limit != long.MaxValue && got < limit) throw new IOException("La conexión se cortó a mitad de un trozo.");
                    long size = FinishMediaJob(j, part);
                    MediaJobFinished(j, size);
                    return true;
                }
                catch (OperationCanceledException) { Rollback(got); TryDelete(part); return false; }
                catch (WebException wex)
                {
                    Rollback(got);
                    if (_stop) return false;
                    string target = Util.RedirectTarget(wex);
                    if (target != null && attempt < 10) { j.Url = target; TryDelete(part); continue; }
                    if (got == 0 && TrySwitchToBrowserProxy(req, wex)) { attempt--; continue; } // proxy del navegador
                    string perm = PermanentError(wex);
                    if (perm != null && _live) { _lastError = perm; return false; } // en directo un trozo caducado se salta
                    if (perm != null) { _fatal = perm; AbortActive(); return false; }
                    _lastError = Describe(wex);
                    if (IsBusy(wex))
                    {
                        try { waitMs = BusyWaitMs(wex, ++busy); }
                        catch (FatalDownloadException f)
                        {
                            // Pide esperar demasiado: en directo ese trozo se salta (como uno caducado); si no, error.
                            if (_live) { _lastError = f.Message; return false; }
                            _fatal = f.Message; AbortActive(); return false;
                        }
                    }
                }
                catch (IOException ex) { Rollback(got); _lastError = ex.Message; }
                catch (CryptographicException) { Rollback(got); _fatal = "No se pudo descifrar el vídeo (clave AES-128 incorrecta)."; return false; }
                catch (Exception ex)
                {
                    // Cualquier otro error (clave con una dirección no válida, archivo bloqueado por el antivirus...):
                    // la descarga acaba en error en vez de cerrar IDMClone. Si se estaba parando, no es un error.
                    Rollback(got); TryDelete(part); TryDelete(j.Path + ".dec");
                    if (Halted) return false;
                    _fatal = ex.Message; AbortActive(); return false;
                }
                finally { Unregister(req); }
                if (attempt >= attempts) return false;
                if (waitMs >= 0) SleepMs(waitMs); else SleepRetry();
            }
        }

        void Rollback(long got) { if (got > 0) lock (_item.Sync) _item.Segments[0].Current -= got; }

        static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

        // Trozo bajado: se descifra si va cifrado (HLS AES-128) y pasa a .seg (así cuenta como terminado).
        long FinishMediaJob(MediaJob j, string part)
        {
            TryDelete(j.Path);
            if (!string.IsNullOrEmpty(j.KeyUrl))
            {
                byte[] key = MediaKey(j.KeyUrl);
                byte[] iv = j.IV;
                if (iv == null)
                {
                    iv = new byte[16];
                    for (int i = 0; i < 8; i++) iv[15 - i] = (byte)(j.Sequence >> (i * 8));
                }
                using (Aes aes = Aes.Create())
                {
                    aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7; aes.Key = key; aes.IV = iv;
                    using (FileStream input = new FileStream(part, FileMode.Open, FileAccess.Read))
                    using (CryptoStream cs = new CryptoStream(input, aes.CreateDecryptor(), CryptoStreamMode.Read))
                    using (FileStream output = new FileStream(j.Path + ".dec", FileMode.Create, FileAccess.Write))
                        cs.CopyTo(output);
                }
                File.Delete(part);
                File.Move(j.Path + ".dec", j.Path);
            }
            else File.Move(part, j.Path);
            j.Done = true;
            return new FileInfo(j.Path).Length;
        }

        byte[] MediaKey(string url)
        {
            lock (_keys)
            {
                byte[] k;
                if (_keys.TryGetValue(url, out k)) return k;
                HttpWebRequest req = (HttpWebRequest)NewRequest(url);
                Register(req);
                try
                {
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (Stream s = resp.GetResponseStream())
                    using (MemoryStream m = new MemoryStream())
                    {
                        s.CopyTo(m);
                        k = m.ToArray();
                    }
                }
                finally { Unregister(req); }
                if (k.Length != 16) throw new CryptographicException("Clave AES-128 de " + k.Length + " bytes.");
                _keys[url] = k;
                return k;
            }
        }

        // --- Unión ---

        static string Sniff(string path)
        {
            byte[] head = new byte[400];
            int n;
            using (FileStream f = File.OpenRead(path)) n = f.Read(head, 0, head.Length);
            if (n < head.Length) Array.Resize(ref head, n);
            if (TsDemux.LooksLikeTs(head)) return "ts";
            if (MkvMerge.LooksLikeWebm(head)) return "webm";
            if (Bmff.IsMp4(head)) return "mp4";
            if (n >= 3 && head[0] == 'I' && head[1] == 'D' && head[2] == '3' || n >= 2 && head[0] == 0xFF && (head[1] & 0xF0) == 0xF0) return "aac";
            return "bin";
        }

        string ConcatTrack(string dir, List<MediaJob> jobs, char track, string name)
        {
            List<MediaJob> mine = jobs.FindAll(delegate(MediaJob j) { return j.Track == track; });
            mine.Sort(delegate(MediaJob x, MediaJob y) { int r = x.Index.CompareTo(y.Index); return r != 0 ? r : x.Chunk.CompareTo(y.Chunk); });
            string outPath = Path.Combine(dir, name);
            using (FileStream o = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                foreach (MediaJob j in mine)
                {
                    if (MediaCancelled) throw new OperationCanceledException();
                    using (FileStream i = File.OpenRead(j.Path)) i.CopyTo(o, 1 << 20);
                }
            return outPath;
        }

        List<MediaTrackData> TracksOf(string path, string format, string dir)
        {
            if (format == "ts") return TsDemux.Demux(path, dir, delegate { return MediaCancelled; });
            return Fmp4Reader.Read(path, 0);
        }

        static string AudioExt(string format) { return format == "webm" ? ".webm" : format == "aac" ? ".aac" : format == "ts" ? ".ts" : ".m4a"; }

        // Devuelve el archivo final (en la carpeta de trabajo); ajusta la extensión de FilePath.
        string AssembleMedia(string dir, List<MediaJob> jobs, MediaTrack v, MediaTrack a)
        {
            string vRaw = v != null ? ConcatTrack(dir, jobs, 'v', "video.raw") : null;
            string aRaw = a != null ? ConcatTrack(dir, jobs, 'a', "audio.raw") : null;
            string vFmt = vRaw != null ? Sniff(vRaw) : null, aFmt = aRaw != null ? Sniff(aRaw) : null;
            string name = Path.GetFileNameWithoutExtension(_item.FilePath);
            Func<bool> cancelled = delegate { return MediaCancelled; };

            if (vRaw != null && aRaw != null && _cfg.MergeAudioVideo)
            {
                bool mp4Family = (vFmt == "mp4" || vFmt == "ts") && (aFmt == "mp4" || aFmt == "ts");
                if (mp4Family)
                {
                    try
                    {
                        List<MediaTrackData> tracks = TracksOf(vRaw, vFmt, dir).FindAll(delegate(MediaTrackData t) { return t.IsVideo; });
                        tracks.AddRange(TracksOf(aRaw, aFmt, dir).FindAll(delegate(MediaTrackData t) { return !t.IsVideo; }));
                        if (tracks.Count > 0)
                        {
                            string outMp4 = Path.Combine(dir, name + ".mp4");
                            Mp4Writer.Write(outMp4, tracks, cancelled);
                            return SetOutput(outMp4);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Trace("no se pudo mezclar en MP4: " + ex.Message); }
                }
                else if (vFmt == "webm" && aFmt == "webm")
                {
                    try
                    {
                        string outWebm = Path.Combine(dir, name + ".webm");
                        MkvMerge.Merge(vRaw, aRaw, outWebm, cancelled);
                        return SetOutput(outWebm);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Trace("no se pudo mezclar en WebM: " + ex.Message); }
                }
            }

            // Una sola pista, o vídeo y audio por separado (formatos distintos, o la opción de mezclar desactivada).
            string main = vRaw != null ? SingleTrack(dir, vRaw, vFmt, name, a == null) : SingleTrack(dir, aRaw, aFmt, name, true);
            if (vRaw != null && aRaw != null)
            {
                string audioOut = SingleTrack(dir, aRaw, aFmt, name + " (audio)", true);
                string dest = Path.Combine(Path.GetDirectoryName(_item.FilePath), Path.GetFileName(audioOut));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                dest = PathReserver != null ? PathReserver(dest) : Util.UniquePath(dest);
                MoveFile(audioOut, dest);
                string note = _cfg.MergeAudioVideo
                    ? "El video no se ha mezclado con el audio porque la secuencia de audio tiene un formato diferente. El audio se guardó aparte: " + Path.GetFileName(dest)
                    : "Audio guardado aparte: " + Path.GetFileName(dest);
                _item.Description = string.IsNullOrEmpty(_item.Description) ? note : _item.Description + " — " + note;
            }
            return SetOutput(main);
        }

        // Una pista sola: TS -> MP4 si se puede; MP4 fragmentado -> MP4 normal; WebM tal cual.
        string SingleTrack(string dir, string raw, string fmt, string name, bool audioOnlyFile)
        {
            if (fmt == "ts" || fmt == "mp4")
            {
                try
                {
                    List<MediaTrackData> tracks = TracksOf(raw, fmt, dir);
                    bool hasVideo = tracks.Exists(delegate(MediaTrackData t) { return t.IsVideo; });
                    string outPath = Path.Combine(dir, name + (hasVideo ? ".mp4" : ".m4a"));
                    Mp4Writer.Write(outPath, tracks, delegate { return MediaCancelled; });
                    return outPath;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Trace("se deja sin convertir: " + ex.Message); }
            }
            string ext = fmt == "ts" ? ".ts" : fmt == "webm" ? ".webm" : fmt == "aac" ? ".aac" : fmt == "mp4" ? (audioOnlyFile ? ".m4a" : ".mp4") : ".bin";
            string target = Path.Combine(dir, name + ext);
            TryDelete(target);
            File.Move(raw, target);
            return target;
        }

        // El nombre final lleva la extensión del archivo que ha salido (.mp4, .webm, .ts...).
        string SetOutput(string workFile)
        {
            string ext = Path.GetExtension(workFile);
            if (!string.Equals(Path.GetExtension(_item.FilePath), ext, StringComparison.OrdinalIgnoreCase))
            {
                _item.FilePath = Path.ChangeExtension(_item.FilePath, ext);
                Action<DownloadItem> h = PathChanged; if (h != null) h(_item);
            }
            return workFile;
        }
    }
}
