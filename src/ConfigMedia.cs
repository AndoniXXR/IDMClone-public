using System;
using System.Collections.Generic;

namespace IDMClone
{
    // Opciones nuevas de "Personalizar panel de descarga de IDMClone en los navegadores" (IDM: capturas 29b y 29c).
    // Las de siempre (MediaPanel, MediaPanelMini, MediaPanelMinVideoKB/AudioKB, MediaPanelExcludeSites,
    // MergeAudioVideo) siguen en Core.cs; estas se añaden sin romper las configuraciones existentes.
    public partial class AppConfig
    {
        // "Para reproductores Web": tipos de archivo para los que se muestra el panel, con su tamaño mínimo.
        // Separados por espacios: "flv mp3:50 -wmv" (":n" = tamaño mínimo en KB; "-" = desmarcado). null = los de serie.
        public string MediaPanelTypes { get; set; }
        // "No capturar las descargas automáticamente de los reproductores-web".
        public bool MediaNoAutoCapture { get; set; }
        // Mostrar el panel también para los archivos protegidos (DRM) que IDMClone no puede descargar (avisa de ello).
        // Desmarcada por defecto, como en IDM (las configuraciones guardadas conservan su valor).
        bool _mediaPanelProtected = false;
        public bool MediaPanelProtected { get { return _mediaPanelProtected; } set { _mediaPanelProtected = value; } }
        // Subtítulos del panel: "Convertir los subtítulos WebVTT a SRT" (desmarcada: se guardan como .vtt, igual que los da el sitio).
        public bool SubtitlesToSrt { get; set; }

        // "Para los archivos seleccionados": panel "Descargar con IDMClone" al seleccionar texto con enlaces.
        public int SelectionPanelMode { get; set; }        // 0 mostrar, 1 no mostrar, 2 solo en los sitios de la lista
        public bool SelectionPanelMini { get; set; }
        string _selectionPanelSites = DefaultSelectionSites;
        public string SelectionPanelSites { get { return _selectionPanelSites; } set { _selectionPanelSites = value ?? ""; } }

        public const string DefaultSelectionSites = "mediafire.com mega.nz 1fichier.com rapidgator.net nitroflare.com uptobox.com sendspace.com";

        // Tipos de la lista (en el orden de IDM: FLV, MP3, MP4, M4A, MPG, MPEG, AVI...).
        public static readonly string[] MediaVideoTypes = { "flv", "mp4", "mpg", "mpeg", "avi", "wmv", "asf", "mov", "3gp", "m4v", "mkv", "webm", "ts" };
        public static readonly string[] MediaAudioTypes = { "mp3", "m4a", "aac", "wav", "wma", "ogg", "opus", "flac" };
        public static readonly string[] MediaTypesOrder = { "flv", "mp3", "mp4", "m4a", "mpg", "mpeg", "avi", "wmv", "asf", "mov", "3gp", "m4v", "mkv", "webm", "ts", "aac", "wav", "wma", "ogg", "opus", "flac" };

        public static bool IsMediaAudioType(string ext) { return Array.IndexOf(MediaAudioTypes, (ext ?? "").ToLowerInvariant()) >= 0; }
        public static bool IsMediaVideoType(string ext) { return Array.IndexOf(MediaVideoTypes, (ext ?? "").ToLowerInvariant()) >= 0; }

        // Una fila de la lista "Mostrar panel de descarga para los siguientes tipos de archivo".
        public class MediaTypeRule { public string Ext; public int MinKB; public bool On; }

        // Interpreta MediaPanelTypes ("flv mp3:50 -wmv"). Sin lista guardada (null): los tipos de serie, todos marcados,
        // con el mínimo de vídeo o de audio de siempre. La usan la ventana del panel y el host nativo (getconfig).
        public static List<MediaTypeRule> ParseMediaTypes(AppConfig c)
        {
            List<MediaTypeRule> r = new List<MediaTypeRule>();
            if (c == null) return r;
            if (c.MediaPanelTypes == null)
            {
                foreach (string e in MediaTypesOrder) r.Add(Rule(e, IsMediaAudioType(e) ? c.MediaPanelMinAudioKB : c.MediaPanelMinVideoKB, true));
                return r;
            }
            foreach (string w in c.MediaPanelTypes.Split(new char[] { ' ', '\r', '\n', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                bool on = !w.StartsWith("-");
                string t = on ? w : w.Substring(1);
                int min = 0, colon = t.IndexOf(':');
                if (colon >= 0)
                {
                    int.TryParse(t.Substring(colon + 1), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out min);
                    t = t.Substring(0, colon);
                }
                t = t.Trim().TrimStart('.').ToLowerInvariant();
                if (t.Length == 0) continue;
                bool dup = false;
                foreach (MediaTypeRule x in r) if (x.Ext == t) { dup = true; break; }
                if (!dup) r.Add(Rule(t, Math.Max(0, min), on));
            }
            return r;
        }

        static MediaTypeRule Rule(string ext, int minKB, bool on) { MediaTypeRule m = new MediaTypeRule(); m.Ext = ext; m.MinKB = minKB; m.On = on; return m; }

        // Copia las opciones nuevas del panel (para la copia temporal que usa Opciones > General).
        public static void CopyMediaExtras(AppConfig from, AppConfig to)
        {
            if (from == null || to == null) return;
            to.MediaPanelTypes = from.MediaPanelTypes; to.MediaNoAutoCapture = from.MediaNoAutoCapture; to.MediaPanelProtected = from.MediaPanelProtected;
            to.SubtitlesToSrt = from.SubtitlesToSrt;
            to.SelectionPanelMode = from.SelectionPanelMode; to.SelectionPanelMini = from.SelectionPanelMini; to.SelectionPanelSites = from.SelectionPanelSites;
        }
    }
}
