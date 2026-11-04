using System;
using System.Collections.Generic;

namespace IDMClone
{
    // Descargas de YouTube ("mediaKind":"youtube"): la dirección es la de la página (watch?v=<id>) y MediaVideo/MediaAudio
    // los formatos elegidos ("<itag>" o "<itag>:<pista>"; MediaAudio "-" = sin audio). Las direcciones de los archivos
    // caducan, así que se piden de nuevo cada vez que la descarga empieza o se reanuda; luego se bajan como un "pair"
    // (vídeo y audio sueltos, a trozos, y se mezclan al final) o, si es un formato con el audio incluido, como uno solo.
    // Los trozos ya bajados se conservan: su nombre no depende de la dirección.
    public partial class DownloadEngine
    {
        bool _ytRetried; // ya se volvieron a pedir las direcciones tras un 403 en esta ejecución
        string _ytClient, _ytBadClient; // cliente de las direcciones actuales / el de las que dieron 403
        volatile string _statusNote;

        // Qué hace la descarga mientras está "Conectando" (sin traducir: Lang.T al mostrarlo), o null. La ventana de
        // progreso y la lista lo enseñan en vez de "Conectando...".
        public string StatusNote { get { return _statusNote; } }

        public const string AskingYouTubeText = "Pidiendo a YouTube las direcciones del vídeo...";

        void ResolveYouTube(out MediaManifest man, out MediaTrack v, out MediaTrack a)
        {
            string id;
            if (!YouTube.TryVideoId(_item.Url, out id)) throw new FatalDownloadException("La dirección del vídeo de YouTube no es válida.");
            YtQuery q = new YtQuery();
            q.VideoId = id;
            q.Cookies = string.IsNullOrEmpty(_item.Cookies) ? null : _item.Cookies;
            q.LoggedIn = q.Cookies != null && YouTube.CookiesLoggedIn(q.Cookies);
            q.UserAgent = _item.UserAgentOverride;
            q.Status = delegate(string t) { _statusNote = t; };
            q.LastClient = _ytBadClient;
            _statusNote = AskingYouTubeText;
            YtVideo vid;
            try { vid = YouTube.Resolve(_cfg, q); }
            catch (YouTubeException ex) { throw new FatalDownloadException(ex.Message); }
            finally { _statusNote = null; }
            if (_stop) throw new OperationCanceledException();
            _ytClient = vid.Client;
            if (vid.Live) { ResolveYouTubeLive(vid, out man, out v, out a); return; }
            YtFormat fv, fa;
            ResolveYouTubeTracks(vid, _item.MediaVideo, _item.MediaAudio, out fv, out fa);
            v = fv != null ? YouTubeTrack("video", fv) : null;
            a = fa != null ? YouTubeTrack("audio", fa) : null;
            man = new MediaManifest(); man.Kind = "pair"; man.Url = _item.Url;
            _ytClient = vid.Client;
            Trace("youtube: " + id + " con el cliente " + vid.Client + (v != null ? ", vídeo " + _item.MediaVideo : "") + (a != null ? ", audio " + _item.MediaAudio : ""));
        }

        // Emisión en directo: su lista HLS (recién pedida) y la calidad elegida por su clave estable (YouTube.LiveKey);
        // luego se graba como cualquier directo HLS (RunLive).
        void ResolveYouTubeLive(YtVideo vid, out MediaManifest man, out MediaTrack v, out MediaTrack a)
        {
            if (string.IsNullOrEmpty(vid.HlsUrl)) throw new FatalDownloadException("YouTube no da la lista de esta emisión en directo.");
            string final;
            string text = FetchText(vid.HlsUrl, out final);
            try { man = MediaManifest.Parse(text, final); }
            catch (Exception ex) { throw new FatalDownloadException("La lista de la emisión en directo no es válida: " + ex.Message); }
            v = null;
            for (int i = 0; i < man.Videos.Count && v == null; i++)
                if (YouTube.LiveKey(man.Videos[i].Key, i) == _item.MediaVideo) v = man.Videos[i];
            if (v == null && man.Videos.Count > 0) v = man.Videos[0]; // la calidad ya no está: la mejor
            if (v == null) throw new FatalDownloadException("La emisión en directo no tiene ningún vídeo.");
            a = _item.MediaAudio == "-" ? null : man.AudioFor(v);
            if (v.Playlist == null) v.Playlist = LoadPlaylist(v.Key);
            if (a != null && a.Playlist == null) a.Playlist = LoadPlaylist(a.Key);
            foreach (MediaTrack t in new MediaTrack[] { v, a })
            {
                if (t == null) continue;
                if (t.Playlist.Protected || man.Protected)
                    throw new FatalDownloadException("IDMClone no puede descargar este contenido de stream porque está protegido (DRM). " +
                        "No se pueden eludir las medidas técnicas de protección del audio y del vídeo.");
                if (t.Playlist.Live || man.Live) _live = true;
                else if (t.Playlist.Segments.Count == 0) throw new FatalDownloadException("La lista de reproducción del vídeo está vacía.");
            }
            Trace("youtube: directo " + vid.Id + ", calidad " + _item.MediaVideo + " -> " + v.Key);
        }

        // Los formatos elegidos en la respuesta nueva (también lo usan las pruebas).
        internal static void ResolveYouTubeTracks(YtVideo vid, string videoKey, string audioKey, out YtFormat fv, out YtFormat fa)
        {
            bool noAudio = audioKey == "-";
            fv = string.IsNullOrEmpty(videoKey) ? null : vid.Find(videoKey);
            if (fv == null && !string.IsNullOrEmpty(videoKey)) throw new FatalDownloadException("YouTube ya no ofrece la calidad elegida (formato " + videoKey + ").");
            fa = null;
            if (!noAudio && !string.IsNullOrEmpty(audioKey) && (fv == null || !fv.Muxed))
            {
                fa = vid.Find(audioKey);
                if (fa == null) throw new FatalDownloadException("YouTube ya no ofrece el audio elegido (formato " + audioKey + ").");
            }
            if (fv == null && fa == null) throw new FatalDownloadException("No se eligió ningún formato del vídeo de YouTube.");
        }

        // Un formato como archivo único. YouTube sirve a la velocidad de reproducción lo que se pide SIN "Range" (un
        // archivo de 3 MB tardaba 100 s; con "Range: bytes=0-..." 0,3 s): los grandes ya se piden a trozos con rango
        // (AddMediaJobs); a los pequeños se les pone aquí el rango del archivo entero.
        MediaTrack YouTubeTrack(string kind, YtFormat f)
        {
            MediaTrack t = WholeFile(kind, f.Url);
            long size = f.SizeExact ? f.Size : RemoteSize(f.Url);
            if (size > 0 && size <= MediaChunk * 2)
            {
                MediaSegment s = t.Playlist.Segments[0];
                s.RangeStart = 0; s.RangeLength = size;
            }
            return t;
        }

        // Tras un 403 (dirección caducada o rechazada) se piden direcciones nuevas una vez y se sigue donde iba.
        bool YouTubeRetryAfter403()
        {
            if (_item.MediaKind != YouTube.Kind || _ytRetried || _stop || _fatal == null || _fatal.IndexOf("(403)", StringComparison.Ordinal) < 0) return false;
            _ytRetried = true;
            _ytBadClient = _ytClient;
            Trace("youtube: 403 en un trozo; se vuelven a pedir las direcciones");
            _fatal = null; _item.Error = null; _lastError = null;
            return true;
        }
    }
}
