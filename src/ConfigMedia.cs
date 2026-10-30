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

        // Lista de serie de "Para reproductores Web", igual que la de IDM 6.43 (mismo orden, todos marcados); al final,
        // opus y flac, que añade IDMClone. La clase (vídeo/audio/subtítulo) decide qué mínimo general usa un tipo agregado.
        public static readonly string[] MediaVideoTypes = { "flv", "mp4", "mpg", "mpeg", "avi", "wmv", "asf", "rm", "mov", "ogv", "ts", "mkv", "3gp", "qt", "webm", "m4v", "f4v" };
        public static readonly string[] MediaAudioTypes = { "mp3", "m4a", "wma", "wav", "ogg", "aac", "opus", "flac" };
        public static readonly string[] MediaSubtitleTypes = { "vtt", "ttml", "ttml2", "dfxp", "srt" };
        public static readonly string[] MediaTypesOrder = { "flv", "mp3", "mp4", "m4a", "mpg", "mpeg", "avi", "wmv", "wma", "wav", "asf", "rm", "ogg", "mov", "ogv", "ts", "mkv", "aac",
                                                            "vtt", "ttml", "ttml2", "dfxp", "srt", "3gp", "qt", "webm", "m4v", "f4v", "opus", "flac" };

        public static bool IsMediaAudioType(string ext) { return Array.IndexOf(MediaAudioTypes, (ext ?? "").ToLowerInvariant()) >= 0; }
        public static bool IsMediaVideoType(string ext) { return Array.IndexOf(MediaVideoTypes, (ext ?? "").ToLowerInvariant()) >= 0; }
        public static bool IsMediaSubtitleType(string ext) { return Array.IndexOf(MediaSubtitleTypes, (ext ?? "").ToLowerInvariant()) >= 0; }

        // Mínimo de serie de un tipo de la lista, como IDM: solo MP3 (50 KB) y OGG (100 KB); los demás, ninguno
        // (así sale el panel en los vídeos pequeños, como los "GIF" de X/Twitter). No depende de los mínimos generales.
        public static int DefaultMediaMinKB(string ext)
        {
            switch ((ext ?? "").ToLowerInvariant())
            {
                case "mp3": return 50;
                case "ogg": return 100;
                default: return 0;
            }
        }

        // Mínimo de un tipo que se agrega a la lista: el de IDM si es de serie; si no, el general de su clase
        // (los subtítulos, ninguno).
        public static int NewMediaTypeMinKB(AppConfig c, string ext)
        {
            if (Array.IndexOf(MediaTypesOrder, (ext ?? "").ToLowerInvariant()) >= 0) return DefaultMediaMinKB(ext);
            if (c == null || IsMediaSubtitleType(ext)) return 0;
            return IsMediaAudioType(ext) ? c.MediaPanelMinAudioKB : c.MediaPanelMinVideoKB;
        }

        // Una fila de la lista "Mostrar panel de descarga para los siguientes tipos de archivo".
        public class MediaTypeRule { public string Ext; public int MinKB; public bool On; }

        // Interpreta MediaPanelTypes ("flv mp3:50 -wmv"). Sin lista guardada (null): la de serie de IDM, todos marcados,
        // con los mínimos de IDM. La usan la ventana del panel y el host nativo (getconfig).
        public static List<MediaTypeRule> ParseMediaTypes(AppConfig c)
        {
            List<MediaTypeRule> r = new List<MediaTypeRule>();
            if (c == null) return r;
            if (c.MediaPanelTypes == null)
            {
                foreach (string e in MediaTypesOrder) r.Add(Rule(e, DefaultMediaMinKB(e), true));
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

        // Al revés: la lista en el formato de MediaPanelTypes ("flv mp3:50 -wmv").
        public static string MediaTypesText(List<MediaTypeRule> rules)
        {
            List<string> w = new List<string>();
            foreach (MediaTypeRule x in rules)
                w.Add((x.On ? "" : "-") + x.Ext + (x.MinKB > 0 ? ":" + x.MinKB.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""));
            return string.Join(" ", w.ToArray());
        }

        // Configuraciones de antes de la lista de IDM: sin lista guardada, cada tipo usaba el mínimo general de vídeo
        // (1024 KB de serie) o de audio (300 KB). Con esos de serie, pasa a los de IDM (su lista y mínimos generales 0);
        // si el usuario los había cambiado, se guarda la lista con sus mínimos para que todo siga igual que antes.
        internal static void UpgradeMediaTypes(AppConfig c)
        {
            if (c == null || c.MediaPanelTypes != null) return;           // lista personalizada: no se toca
            int v = c.MediaPanelMinVideoKB, a = c.MediaPanelMinAudioKB;
            if (v == 0 && a == 0) return;                                   // ya son los de IDM
            if (v == 1024 && a == 300) { c.MediaPanelMinVideoKB = 0; c.MediaPanelMinAudioKB = 0; return; }
            List<MediaTypeRule> r = new List<MediaTypeRule>();
            foreach (string e in MediaTypesOrder) r.Add(Rule(e, IsMediaAudioType(e) ? a : IsMediaVideoType(e) ? v : 0, true));
            c.MediaPanelTypes = MediaTypesText(r);
        }

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
