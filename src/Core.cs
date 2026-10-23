// IDMClone - núcleo legítimo de un gestor de descargas al estilo IDM.
// Reproduce: segmentación dinámica, reanudación, colas, categorías, programador, CLI.
// NO incluye: driver de kernel, interceptación del tráfico de otras apps, ni ganchos globales.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace IDMClone
{
    public enum DownloadStatus { Queued, Connecting, Downloading, Paused, Complete, Error }

    // Un segmento del archivo. Current y End se modifican en caliente (segmentación dinámica).
    public class Segment
    {
        public long Start { get; set; }
        public long Current { get; set; }
        public long End { get; set; } // exclusivo
        [ScriptIgnore] public bool Active { get; set; }
        [ScriptIgnore] public bool Failed { get; set; } // agotó sus reintentos en esta sesión
        // Conexión actual del segmento (para estimar su velocidad al decidir si conviene partirlo).
        [ScriptIgnore] public long ConnStartTicks { get; set; }
        [ScriptIgnore] public long ConnBytes { get; set; }

        [ScriptIgnore] public long Remaining { get { return End - Current; } }
        [ScriptIgnore] public long Done { get { return Current - Start; } }

        public Segment() { }
        public Segment(long start, long current, long end) { Start = start; Current = current; End = end; }
    }

    // Una categoría: carpeta por defecto + extensiones asociadas (como en IDM).
    public class Category
    {
        public const string GeneralName = "General";

        public string Name { get; set; }
        public string Folder { get; set; }
        public string Extensions { get; set; } // separadas por espacios, sin punto
        // "Poner automáticamente a esta categoría los archivos solamente de los siguientes sitios"
        // (separados por espacios, admiten *). Vacío = de cualquier sitio.
        public string Sites { get; set; }
        // "Recordar la última ruta de guardado": la carpeta elegida al descargar pasa a ser la de la categoría.
        public bool RememberLastPath { get; set; }

        public Category() { }
        public Category(string name, string folder, string ext) { Name = name; Folder = folder; Extensions = ext; }

        public bool IsGeneral { get { return Name == GeneralName; } }
        public bool HasSites { get { return !string.IsNullOrEmpty(Sites) && Sites.Trim().Length > 0; } }

        public bool Matches(string fileName) { return Matches(fileName, null, null); }

        // El tipo de archivo coincide y, si la categoría tiene sitios, la descarga viene de uno de ellos
        // (la dirección del archivo o la página desde la que se descarga).
        public bool Matches(string fileName, string url, string referrer)
        {
            if (!HasExtension(fileName)) return false;
            if (!HasSites) return true;
            return FromSites(url) || FromSites(referrer);
        }

        public bool HasExtension(string fileName)
        {
            if (string.IsNullOrEmpty(Extensions) || string.IsNullOrEmpty(fileName)) return false;
            string ext = Path.GetExtension(fileName);
            if (string.IsNullOrEmpty(ext)) return false;
            ext = ext.TrimStart('.').ToLowerInvariant();
            foreach (string e in ExtensionList())
                if (e == ext || (e.IndexOf('*') >= 0 && Util.WildcardMatch(ext, e))) return true;
            return false;
        }

        public List<string> ExtensionList()
        {
            List<string> r = new List<string>();
            foreach (string e in (Extensions ?? "").Split(new char[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string v = e.Trim().TrimStart('.').ToLowerInvariant();
                if (v.Length > 0 && !r.Contains(v)) r.Add(v);
            }
            return r;
        }

        bool FromSites(string url)
        {
            Uri u;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            foreach (string s in Sites.Split(new char[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                if (Util.UrlMatchesPattern(u, s)) return true;
            return false;
        }

        public Category Clone()
        {
            Category c = new Category(Name, Folder, Extensions); c.Sites = Sites; c.RememberLastPath = RememberLastPath; return c;
        }
    }

    // Excepción de conexiones por servidor (IDM: Opciones -> Conexión -> Excepciones).
    public class ServerLimit
    {
        public string Host { get; set; }        // "ejemplo.com", "*.ejemplo.com"
        public int MaxConnections { get; set; }
    }

    // Inicio de sesión por sitio (IDM: Opciones -> Inicios de sesión). La contraseña va cifrada (DPAPI).
    public class SiteLogin
    {
        public string Site { get; set; }        // "servidor.com", "*.servidor.com/privado/*"
        public string User { get; set; }
        public string PasswordEnc { get; set; }
        [ScriptIgnore] public string Password { get { return Util.Unprotect(PasswordEnc) ?? ""; } set { PasswordEnc = Util.Protect(value ?? ""); } }
    }

    // Un sonido de Opciones > Sonidos: evento, activado y archivo .wav.
    public class SoundSetting
    {
        public const string Complete = "complete", Failed = "failed", QueueStart = "queuestart", QueueStop = "queuestop";
        public string Event { get; set; }
        public bool Enabled { get; set; }
        public string File { get; set; }

        public static string Title(string ev)
        {
            switch (ev)
            {
                case Complete: return "Descarga completa";
                case Failed: return "Descarga fallida";
                case QueueStart: return "Proceso de colas iniciado";
                case QueueStop: return "Proceso de colas detenido/finalizado";
            }
            return ev;
        }

        public static List<SoundSetting> Defaults()
        {
            string media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
            List<SoundSetting> l = new List<SoundSetting>();
            l.Add(new SoundSetting { Event = Complete, File = Path.Combine(media, "tada.wav") });
            l.Add(new SoundSetting { Event = Failed, File = Path.Combine(media, "Windows Critical Stop.wav") });
            l.Add(new SoundSetting { Event = QueueStart, File = Path.Combine(media, "Windows Notify System Generic.wav") });
            l.Add(new SoundSetting { Event = QueueStop, File = Path.Combine(media, "Windows Notify System Generic.wav") });
            return l;
        }
    }

    // Configuración global (se guarda en %APPDATA%\IDMClone\config.json).
    public partial class AppConfig   // partial: cada área de la interfaz puede añadir sus opciones en su archivo
    {
        public int MaxConnections { get; set; }
        public long MinSplitSize { get; set; }     // no partir segmentos menores que esto
        public int SpeedLimitKBps { get; set; }    // 0 = sin límite
        public string DefaultFolder { get; set; }
        public string UserAgent { get; set; }
        public int RetryCount { get; set; }
        public int RetryDelaySec { get; set; }
        public bool LaunchOnStart { get; set; }
        public List<Category> Categories { get; set; }

        // Carpeta temporal (IDM: DwnlData). Lo descargado se guarda allí y se mueve al destino al terminar.
        public bool UseTempFolder { get; set; }
        public string TempFolder { get; set; }

        // Conexión
        public List<ServerLimit> ServerLimits { get; set; }
        public bool QuotaEnabled { get; set; }     // "no más de QuotaMB cada QuotaHours horas"
        public int QuotaMB { get; set; }
        public int QuotaHours { get; set; }

        // Proxy: "system" (el de Windows), "none", "manual" o "pac".
        public string ProxyMode { get; set; }
        public string ProxyHost { get; set; }
        public int ProxyPort { get; set; }
        public string ProxyUser { get; set; }
        public string ProxyPasswordEnc { get; set; }
        public string ProxyBypass { get; set; }    // separados por ';'
        public string PacUrl { get; set; }

        public List<SiteLogin> SiteLogins { get; set; }

        // Limitador de velocidad global (IDM: Descargas > Limitador de velocidad > Activar/Desactivar).
        // null en configuraciones antiguas: entonces vale "SpeedLimitKBps > 0".
        public bool? SpeedLimiterOn { get; set; }
        [ScriptIgnore] public int EffectiveSpeedLimitKBps
        {
            get { bool on = SpeedLimiterOn ?? SpeedLimitKBps > 0; return on ? Math.Max(1, SpeedLimitKBps) : 0; }
        }

        // Opciones > Descargas (como IDM).
        public bool ShowFileInfoDialog { get; set; }     // "Mostrar diálogo de inicio de descarga"
        public bool StartWhileInfoDialog { get; set; }   // empezar a bajar mientras se muestra ese diálogo
        public string ProgressDialogMode { get; set; }   // "normal", "minimized" o "none"
        public bool ShowCompleteDialog { get; set; }     // "Mostrar diálogo de descarga completa"
        public bool ShowQueuePicker { get; set; }        // panel de colas al pulsar "Descargar más tarde"
        public string DuplicateAction { get; set; }      // "ask", "number", "overwrite", "resume"
        public string DoubleClickAction { get; set; }    // "open" o "properties"
        // "Configuración de la comprobación de virus": antivirus que se ejecuta al terminar cada descarga.
        public string AntivirusPath { get; set; }        // vacío = no comprobar
        public string AntivirusArgs { get; set; }        // "[File]" = ruta del archivo descargado

        // Opciones > General > "Panel de descarga de IDM para reproductores-web" (como IDM).
        public bool MediaPanel { get; set; }              // mostrar "Descargar este video" sobre los vídeos
        public bool MediaPanelMini { get; set; }          // "Modo mini" en vez de "Modo completo"
        public string MediaPanelExcludeSites { get; set; } // "No mostrar el panel de descarga de IDM de los siguientes sitios"
        public int MediaPanelMinVideoKB { get; set; }     // no mostrarlo para vídeos más pequeños (si se sabe el tamaño)
        public int MediaPanelMinAudioKB { get; set; }
        public bool MergeAudioVideo { get; set; }         // "Mezclar secuencias de audio y vídeo en un solo archivo"
        public string Language { get; set; }              // Ver > Idioma (null = español)

        // Opciones > Tipos de archivo: lo que la extensión del navegador captura.
        public string CaptureExts { get; set; }          // separados por espacios; admite comodines (r0*)
        public string CaptureExcludeSites { get; set; }  // sitios/direcciones que no se capturan (con *)

        // Opciones > General (como IDM): vigilar el portapapeles y teclas para no capturar / forzar la captura
        // en el navegador ("alt", "ctrl", "shift", "del" / "ins", combinables con "+").
        public bool MonitorClipboard { get; set; }
        public bool UseKeyToPrevent { get; set; }
        public string PreventKeys { get; set; }
        public bool UseKeyToForce { get; set; }
        public string ForceKeys { get; set; }
        // Destino de arrastre flotante (Tareas > Mostrar destino de arrastre).
        public bool DropTargetVisible { get; set; }
        public int DropTargetX { get; set; }
        public int DropTargetY { get; set; }

        // Opciones > Sonidos.
        public List<SoundSetting> Sounds { get; set; }

        // Ventana principal: columnas ("id:ancho" en orden; solo las visibles), orden y barra.
        public string Columns { get; set; }
        public string SortColumn { get; set; }
        public bool SortDescending { get; set; }
        public bool ToolbarLarge { get; set; }
        public string ToolbarStyle { get; set; }         // "3d" o "neon" (como los estilos de IDM)
        public string ToolbarButtons { get; set; }       // botones en orden ("|" = separador); null = los de siempre
        public bool HideCategories { get; set; }

        public const string DefaultCaptureExts =
            "3GP 7Z AAC ACE AIF APK ARJ ASF AVI BIN BZ2 EXE GZ GZIP IMG ISO LZH M4A M4V MKV MOV MP3 MP4 MPA MPE MPEG MPG " +
            "MSI MSU OGG OGV PDF PLJ PPS PPT QT R0* R1* RA RAR RM RMVB SEA SIT SITX TAR TIF TIFF WAV WMA WMV Z ZIP";
        public const string DefaultCaptureExclude = "*.update.microsoft.com download.windowsupdate.com *.meebo.com/*.mp3 images.apple.com/*.mov";
        public const string DefaultColumns = ListColumns.IdmDefault; // columnas, orden y anchos de IDM

        public string TempFolderOrDefault
        {
            get { return string.IsNullOrEmpty(TempFolder) ? Path.Combine(Dir, "DwnlData") : TempFolder; }
        }

        // Máximo de conexiones para un servidor (la excepción más restrictiva que case).
        public int ConnectionsFor(string host)
        {
            int n = MaxConnections;
            if (ServerLimits != null && !string.IsNullOrEmpty(host))
                foreach (ServerLimit l in ServerLimits)
                    if (l != null && l.MaxConnections > 0 && Util.HostMatches(host, l.Host)) n = Math.Min(n, l.MaxConnections);
            return Math.Max(1, n);
        }

        public SiteLogin LoginFor(Uri uri)
        {
            if (SiteLogins == null || uri == null) return null;
            foreach (SiteLogin s in SiteLogins)
                if (s != null && !string.IsNullOrEmpty(s.User) && Util.UrlMatchesPattern(uri, s.Site)) return s;
            return null;
        }

        // Carpeta de datos. Se puede cambiar con la variable de entorno IDMCLONE_HOME
        // (la usan los tests para no tocar los datos reales del usuario).
        public static string HomeOverride;

        public static string Dir
        {
            get
            {
                if (!string.IsNullOrEmpty(HomeOverride)) return HomeOverride;
                string env = Environment.GetEnvironmentVariable("IDMCLONE_HOME");
                if (!string.IsNullOrEmpty(env)) return env;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IDMClone");
            }
        }

        public static bool IsCustomHome
        {
            get { return !string.IsNullOrEmpty(HomeOverride) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IDMCLONE_HOME")); }
        }

        // Aviso de la última carga (p. ej. config.json dañado), para mostrarlo en la GUI.
        public static string LoadWarning;
        public static string ConfigPath { get { return Path.Combine(Dir, "config.json"); } }
        public static string StateDir { get { return Path.Combine(Dir, "state"); } }
        public static string DownloadsPath { get { return Path.Combine(Dir, "downloads.json"); } }

        public static AppConfig CreateDefault()
        {
            AppConfig c = new AppConfig();
            c.MaxConnections = 8;
            c.MinSplitSize = 256 * 1024;
            c.SpeedLimitKBps = 0;
            c.RetryCount = 20;
            c.RetryDelaySec = 5;
            c.LaunchOnStart = false;
            c.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) IDMClone/1.0";
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string dl = Path.Combine(home, "Downloads");
            c.DefaultFolder = dl;
            c.Categories = new List<Category>();
            c.Categories.Add(new Category("General", dl, ""));
            c.Categories.Add(new Category("Comprimidos", Path.Combine(dl, "Compressed"), "zip rar 7z gz tar bz2 arj lzh ace sit"));
            c.Categories.Add(new Category("Documentos", Path.Combine(dl, "Documents"), "pdf doc docx xls xlsx ppt pptx txt rtf odt"));
            c.Categories.Add(new Category("Música", Path.Combine(dl, "Music"), "mp3 wav wma m4a aac ogg flac"));
            c.Categories.Add(new Category("Vídeo", Path.Combine(dl, "Video"), "mp4 mkv avi mov wmv flv mpg mpeg m4v webm 3gp"));
            c.Categories.Add(new Category("Programas", Path.Combine(dl, "Programs"), "exe msi msu cab bin iso img apk"));
            c.UseTempFolder = true;
            c.TempFolder = null; // = %APPDATA%\IDMClone\DwnlData
            c.ServerLimits = new List<ServerLimit>();
            c.QuotaEnabled = false; c.QuotaMB = 150; c.QuotaHours = 4; // ejemplo de la ayuda de IDM
            c.ProxyMode = "system";
            c.ProxyPort = 8080;
            c.SiteLogins = new List<SiteLogin>();
            c.ShowFileInfoDialog = true;
            c.StartWhileInfoDialog = true;
            c.UseKeyToPrevent = true; c.PreventKeys = "del";   // valores de IDM
            c.UseKeyToForce = false; c.ForceKeys = "ins";
            c.DropTargetX = -1; c.DropTargetY = -1;
            c.ProgressDialogMode = "normal";
            c.ShowCompleteDialog = true;
            c.ShowQueuePicker = true;
            c.DuplicateAction = "ask";
            c.DoubleClickAction = "properties"; // como IDM: doble clic = Propiedades
            c.CaptureExts = DefaultCaptureExts;
            c.CaptureExcludeSites = DefaultCaptureExclude;
            c.Sounds = SoundSetting.Defaults();
            c.Columns = DefaultColumns;
            c.SortColumn = "added";
            c.ToolbarLarge = true;
            c.ToolbarStyle = "neon"; // como IDM: botones de neón por defecto
            c.MediaPanel = true; c.MergeAudioVideo = true;
            c.MediaPanelMinVideoKB = 1024; c.MediaPanelMinAudioKB = 300;
            c.MediaPanelExcludeSites = "";
            return c;
        }

        public string FolderFor(string fileName) { return FolderFor(fileName, null, null); }
        public string CategoryNameFor(string fileName) { return CategoryNameFor(fileName, null, null); }

        public string FolderFor(string fileName, string url, string referrer)
        {
            Category c = CategoryFor(fileName, url, referrer);
            return c != null && !string.IsNullOrEmpty(c.Folder) ? c.Folder : DefaultFolder;
        }

        public string CategoryNameFor(string fileName, string url, string referrer)
        {
            Category c = CategoryFor(fileName, url, referrer);
            return c != null ? c.Name : Category.GeneralName;
        }

        // Categoría de un archivo (null = General). Las que se limitan a unos sitios van primero:
        // así "Vídeos de ejemplo.com" gana a "Vídeo" para los .mp4 de ese sitio.
        public Category CategoryFor(string fileName, string url, string referrer)
        {
            if (Categories == null) return null;
            foreach (Category cat in Categories)
                if (cat != null && cat.HasSites && cat.Matches(fileName, url, referrer)) return cat;
            foreach (Category cat in Categories)
                if (cat != null && !cat.HasSites && !cat.IsGeneral && cat.Matches(fileName, url, referrer)) return cat;
            return null;
        }

        public Category FindCategory(string name)
        {
            if (Categories == null || name == null) return null;
            foreach (Category c in Categories) if (c != null && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        // --- Agregar / editar / eliminar categorías (árbol "Categorías" de IDM) ---

        // Mensaje de error, o null si la categoría se puede guardar.
        public string ValidateCategory(Category edited, Category original)
        {
            string name = (edited.Name ?? "").Trim();
            if (name.Length == 0) return "Por favor rellena el campo \"Nombre de categoría\".";
            if (name.IndexOfAny(new char[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
                return "El nombre de la categoría no puede llevar \\ / : * ? \" < > |";
            Category same = FindCategory(name);
            if (same != null && same != original) return "Ya existe una categoría llamada \"" + same.Name + "\".";
            if (original != null && original.IsGeneral && name != Category.GeneralName) return "La categoría \"General\" no se puede renombrar.";
            if (string.IsNullOrEmpty((edited.Folder ?? "").Trim())) return "Elige la carpeta donde se guardarán las descargas de esta categoría.";
            return null;
        }

        // Guarda una categoría nueva (original == null) o los cambios de una existente.
        // Un tipo de archivo pertenece a una sola categoría: si se pone aquí, se quita de las demás
        // (salvo de las limitadas a unos sitios, que van por delante de todos modos).
        public Category ApplyCategory(Category original, Category edited)
        {
            Category target = original;
            if (target == null) { target = new Category(); Categories.Add(target); }
            target.Name = edited.Name.Trim();
            target.Folder = edited.Folder.Trim();
            target.Sites = string.IsNullOrEmpty(edited.Sites) ? null : edited.Sites.Trim();
            target.RememberLastPath = edited.RememberLastPath;
            target.Extensions = target.IsGeneral ? "" : string.Join(" ", edited.ExtensionList().ToArray());
            if (target.IsGeneral) DefaultFolder = target.Folder;
            if (!target.HasSites)
            {
                List<string> mine = target.ExtensionList();
                foreach (Category other in Categories)
                {
                    if (other == target || other.HasSites || mine.Count == 0) continue;
                    List<string> keep = other.ExtensionList();
                    if (keep.RemoveAll(delegate(string e) { return mine.Contains(e); }) > 0) other.Extensions = string.Join(" ", keep.ToArray());
                }
            }
            return target;
        }

        // "General" no se puede eliminar.
        public bool RemoveCategory(Category c)
        {
            if (c == null || c.IsGeneral) return false;
            return Categories.Remove(c);
        }

        public static AppConfig Load()
        {
            AppConfig c;
            string warning;
            bool ok = Util.TryLoadJson<AppConfig>(ConfigPath,
                delegate(string json) { return CreateDefaultForUpgrade(NewSerializer().Deserialize<AppConfig>(json), json); },
                out c, out warning);
            LoadWarning = warning;
            if (ok && c != null)
            {
                c.Normalize();
                return c;
            }
            // Sin archivo, o dañado y ya apartado como .corrupt: empezar con los valores por defecto.
            AppConfig def = CreateDefault();
            def.Save();
            return def;
        }

        // Corrige valores ausentes o fuera de rango (config antigua o editada a mano).
        public void Normalize()
        {
            AppConfig d = CreateDefault();
            if (Categories == null || Categories.Count == 0) Categories = d.Categories;
            Categories.RemoveAll(delegate(Category x) { return x == null || string.IsNullOrEmpty(x.Name); });
            if (Categories.Count == 0) Categories = d.Categories;
            if (string.IsNullOrEmpty(DefaultFolder)) DefaultFolder = d.DefaultFolder;
            // "General" siempre existe (IDM no deja borrarla) y su carpeta es la carpeta por defecto.
            Category general = Categories.Find(delegate(Category x) { return x.Name == Category.GeneralName; });
            if (general == null) { general = new Category(Category.GeneralName, DefaultFolder, ""); Categories.Insert(0, general); }
            general.Folder = DefaultFolder; general.Extensions = ""; general.Sites = null;
            foreach (Category cat in Categories)
                if (string.IsNullOrEmpty(cat.Folder)) cat.Folder = DefaultFolder;
            if (MaxConnections < 1) MaxConnections = d.MaxConnections;
            if (MaxConnections > 32) MaxConnections = 32;
            if (MinSplitSize < MinAllowedSplit) MinSplitSize = d.MinSplitSize;
            if (SpeedLimitKBps < 0) SpeedLimitKBps = 0;
            if (RetryCount < 0) RetryCount = d.RetryCount;
            if (RetryDelaySec < 1) RetryDelaySec = d.RetryDelaySec;
            if (string.IsNullOrEmpty(UserAgent)) UserAgent = d.UserAgent;
            if (ServerLimits == null) ServerLimits = new List<ServerLimit>();
            ServerLimits.RemoveAll(delegate(ServerLimit x) { return x == null || string.IsNullOrEmpty(x.Host) || x.MaxConnections < 1; });
            if (SiteLogins == null) SiteLogins = new List<SiteLogin>();
            SiteLogins.RemoveAll(delegate(SiteLogin x) { return x == null || string.IsNullOrEmpty(x.Site); });
            if (QuotaMB < 1) QuotaMB = d.QuotaMB;
            if (QuotaHours < 1) QuotaHours = d.QuotaHours;
            if (ProxyMode != "none" && ProxyMode != "manual" && ProxyMode != "pac") ProxyMode = "system";
            if (ProxyPort < 1 || ProxyPort > 65535) ProxyPort = d.ProxyPort;
            if (ProgressDialogMode != "minimized" && ProgressDialogMode != "none") ProgressDialogMode = "normal";
            if (DuplicateAction != "number" && DuplicateAction != "overwrite" && DuplicateAction != "resume") DuplicateAction = "ask";
            if (DoubleClickAction != "properties") DoubleClickAction = "open";
            ToolbarStyle = Glyphs.NormalizeStyle(ToolbarStyle);
            if (CaptureExts == null) CaptureExts = d.CaptureExts;
            if (CaptureExcludeSites == null) CaptureExcludeSites = d.CaptureExcludeSites;
            if (Sounds == null) Sounds = new List<SoundSetting>();
            Sounds.RemoveAll(delegate(SoundSetting x) { return x == null || string.IsNullOrEmpty(x.Event); });
            foreach (SoundSetting def in d.Sounds)
                if (!Sounds.Exists(delegate(SoundSetting x) { return x.Event == def.Event; })) Sounds.Add(def);
            if (string.IsNullOrEmpty(Columns) || Columns.IndexOf("name", StringComparison.Ordinal) < 0) Columns = d.Columns;
            if (string.IsNullOrEmpty(SortColumn)) SortColumn = d.SortColumn;
            if (string.IsNullOrEmpty(PreventKeys)) PreventKeys = d.PreventKeys;
            if (string.IsNullOrEmpty(ForceKeys)) ForceKeys = d.ForceKeys;
        }

        public SoundSetting SoundFor(string ev)
        {
            if (Sounds != null) foreach (SoundSetting x in Sounds) if (x.Event == ev) return x;
            return null;
        }

        // Para configuraciones guardadas antes de existir la opción (JavaScriptSerializer deja false).
        public static AppConfig CreateDefaultForUpgrade(AppConfig loaded, string rawJson)
        {
            if (rawJson == null) return loaded;
            if (rawJson.IndexOf("\"UseTempFolder\"", StringComparison.Ordinal) < 0) loaded.UseTempFolder = true;
            // Opciones que en IDM vienen activadas.
            if (rawJson.IndexOf("\"ShowFileInfoDialog\"", StringComparison.Ordinal) < 0) loaded.ShowFileInfoDialog = true;
            if (rawJson.IndexOf("\"StartWhileInfoDialog\"", StringComparison.Ordinal) < 0) loaded.StartWhileInfoDialog = true;
            if (rawJson.IndexOf("\"ShowCompleteDialog\"", StringComparison.Ordinal) < 0) loaded.ShowCompleteDialog = true;
            if (rawJson.IndexOf("\"ShowQueuePicker\"", StringComparison.Ordinal) < 0) loaded.ShowQueuePicker = true;
            if (rawJson.IndexOf("\"ToolbarLarge\"", StringComparison.Ordinal) < 0) loaded.ToolbarLarge = true;
            if (rawJson.IndexOf("\"UseKeyToPrevent\"", StringComparison.Ordinal) < 0) loaded.UseKeyToPrevent = true;
            if (rawJson.IndexOf("\"DropTargetX\"", StringComparison.Ordinal) < 0) { loaded.DropTargetX = -1; loaded.DropTargetY = -1; }
            if (rawJson.IndexOf("\"MediaPanel\"", StringComparison.Ordinal) < 0)
            {
                loaded.MediaPanel = true; loaded.MergeAudioVideo = true;
                loaded.MediaPanelMinVideoKB = 1024; loaded.MediaPanelMinAudioKB = 300;
            }
            // Proxy por protocolo y "Proxy DNS" (nuevos): una configuración de antes sigue funcionando igual que antes.
            // Antes el SOCKS mandaba siempre el nombre al servidor (como Proxy DNS marcado), el FTP iba directo y, con
            // SOCKS activo, todo iba por el SOCKS aunque hubiera un proxy HTTP escrito.
            if (rawJson.IndexOf("\"SocksProxyDns\"", StringComparison.Ordinal) < 0) loaded.SocksProxyDns = true;
            if (rawJson.IndexOf("\"ProxyFtp\"", StringComparison.Ordinal) < 0)
            {
                loaded.ProxyFtp = false;
                if (loaded.SocksEnabled) { loaded.ProxyHttp = false; loaded.ProxyHttps = false; }
            }
            return loaded;
        }

        // El motor lee en bloques de 64 KB; un segmento nunca se parte por debajo de 2 bloques.
        public const long MinAllowedSplit = 128 * 1024;

        public void Save()
        {
            try { Util.WriteAllTextAtomic(ConfigPath, NewSerializer().Serialize(this)); }
            catch { }
        }

        public static JavaScriptSerializer NewSerializer()
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            return js;
        }
    }

    // Utilidades de formato (tamaños, velocidad, tiempo).
    public static class Fmt
    {
        public static string Size(long bytes)
        {
            if (bytes < 0) return "?";
            double b = bytes;
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
            return string.Format(CultureInfo.InvariantCulture, (i == 0 ? "{0:0}" : "{0:0.0}") + " {1}", b, u[i]);
        }

        public static string Speed(long bps) { return IdmSpeed(bps, 2); }

        // Velocidad como IDM, igual en la lista, la ventana de progreso, la bandeja y la consola:
        // "523.40 KB/sec", "1.01 MB/sec" (2 decimales en la lista, 3 en la ventana de progreso).
        // Pasa a la unidad siguiente al llegar a 1000, como IDM: nunca "1016.75 KB/sec", sino "0.99 MB/sec".
        public static string IdmSpeed(long bps, int decimals)
        {
            if (bps <= 0) return "";
            string[] u = { "KB", "MB", "GB" };
            double v = bps / 1024.0;
            int i = 0;
            decimals = Math.Max(0, Math.Min(6, decimals));
            while (i < u.Length - 1 && Math.Round(v, decimals) >= 1000) { v /= 1024; i++; }
            string fmt = decimals > 0 ? "{0:0." + new string('0', decimals) + "} {1}/sec" : "{0:0} {1}/sec";
            return string.Format(CultureInfo.InvariantCulture, fmt, v, u[i]);
        }

        public static string Eta(long remaining, long bps)
        {
            if (bps <= 0 || remaining < 0) return "";
            long sec = remaining / bps;
            TimeSpan t = TimeSpan.FromSeconds(sec);
            if (t.TotalHours >= 1) return string.Format("{0}h {1}m", (int)t.TotalHours, t.Minutes);
            if (t.TotalMinutes >= 1) return string.Format("{0}m {1}s", t.Minutes, t.Seconds);
            return string.Format("{0}s", t.Seconds);
        }

        public static string Percent(long done, long total)
        {
            if (total <= 0) return "";
            double p = 100.0 * done / total;
            if (p > 100) p = 100;
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0}%", p);
        }
    }
}
