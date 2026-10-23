using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace IDMClone
{
    // "Grabber" (capturador de sitios) de IDM: explora un sitio web a partir de una página de inicio y
    // encuentra los archivos que pasan los filtros (todas las imágenes, el audio, el sitio entero...).
    // Después se marcan y se descargan desde el propio Grabber o se añaden a la lista principal.

    public class GrabberFilter
    {
        public string Name { get; set; }
        public string Mask { get; set; } // "*.gif,*.jpg" (comas; * comodín; "<start page>" = página de inicio)
        public GrabberFilter() { }
        public GrabberFilter(string name, string mask) { Name = name; Mask = mask; }
        public override string ToString() { return Lang.T(Name) + "  ( " + Mask + " )"; }
    }

    public class GrabberPage
    {
        public string Url { get; set; }
        public string Referer { get; set; }
        public int Level { get; set; }      // niveles de enlace desde la página de inicio
        public int OffLevel { get; set; }   // niveles seguidos fuera del sitio (0 = en el sitio)
        public string Status { get; set; }  // null = pendiente, "Procesada", "Error", "No Encontrado"
        public string FinalUrl { get; set; } // dirección final tras redirecciones (si es otra)
    }

    public class GrabberFile
    {
        public string Url { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }    // tipo para la columna (p. ej. "Imagen JPG")
        public long Size { get; set; }      // -1 = desconocido
        public string Status { get; set; }  // "", "Descargando", "Completo", "Error", "No Encontrado", "Detenido"
        public string LinkText { get; set; }
        public string Referer { get; set; } // página donde se encontró
        public string SaveTo { get; set; }  // ruta del archivo descargado
        public bool Checked { get; set; }
        public bool IsPage { get; set; }    // página web (se puede convertir para navegar sin conexión)
        public GrabberFile() { Size = -1; Status = ""; }
    }

    public class GrabberProject
    {
        public const int Version = 1;
        public int FileVersion { get; set; }
        public string Name { get; set; }
        // Paso 1
        public string StartUrl { get; set; }
        public int Template { get; set; }
        public bool UseAuth { get; set; }
        public string User { get; set; }
        public string PasswordEnc { get; set; }
        public string LogoutPages { get; set; }      // "No abrir la página de desconexión" (con *)
        // "Ingresa el usuario y contraseña manualmente en la siguiente web": antes de explorar se abre esa página en
        // el navegador del usuario y luego se usan las cookies de su sesión. Las cookies NUNCA se guardan aquí.
        public bool ManualLogin { get; set; }
        public string ManualLoginUrl { get; set; }
        // Paso 2
        public int SaveMode { get; set; }            // 0 por categoría, 1 carpeta de una categoría, 2 carpeta
        public string SaveCategory { get; set; }
        public string SaveFolder { get; set; }
        public bool RelativeSubfolders { get; set; }
        public bool ConvertLinks { get; set; }
        public bool Overwrite { get; set; }
        public bool AddOnClose { get; set; }
        // Paso 3
        public bool WholeSite { get; set; }
        public int LevelsSite { get; set; }
        public int LevelsOther { get; set; }
        public bool IgnorePopups { get; set; }
        public bool NoParent { get; set; }
        public bool MainDomain { get; set; }
        public bool ProcessJs { get; set; }
        public string ExploreOnly { get; set; }      // rutas/dominios separados por ;
        public string ExploreNot { get; set; }
        // Paso 4
        public string IncludeFilter { get; set; }    // nombre del filtro
        public string ExcludeFilter { get; set; }    // null = "No usar un filtro excluido"
        public bool ThisSiteOnly { get; set; }
        public bool HideDuplicates { get; set; }
        public bool DownloadAll { get; set; }        // "Iniciar la descarga de todos los archivos coincidentes de una vez"
        public string DownloadOnly { get; set; }
        public string DownloadNot { get; set; }
        public long MinKB { get; set; }              // 0 = sin mínimo
        public long MaxKB { get; set; }              // 0 = sin máximo
        public bool NoLinkedPages { get; set; }      // las páginas enlazadas que no se exploran no se descargan
        // Configuración de Grabber
        public int ExploreAtOnce { get; set; }
        public int DownloadAtOnce { get; set; }
        public bool LinkTextAsDescription { get; set; }
        // Resultados
        public List<GrabberPage> Pages { get; set; }
        public List<GrabberFile> Files { get; set; }

        [ScriptIgnore] public string Password
        {
            get { return string.IsNullOrEmpty(PasswordEnc) ? "" : Util.Unprotect(PasswordEnc); }
            set { PasswordEnc = string.IsNullOrEmpty(value) ? null : Util.Protect(value); }
        }

        public static GrabberProject CreateDefault()
        {
            GrabberProject p = new GrabberProject();
            p.FileVersion = Version;
            p.Template = 0;
            p.SaveMode = 0;
            p.WholeSite = true; p.LevelsSite = 2; p.LevelsOther = 0;
            p.IgnorePopups = true;
            p.IncludeFilter = GrabberFilters.Images;
            p.ThisSiteOnly = true;
            p.ExploreAtOnce = 4; p.DownloadAtOnce = 4;
            p.LinkTextAsDescription = true;
            p.Pages = new List<GrabberPage>();
            p.Files = new List<GrabberFile>();
            return p;
        }

        // --- Plantillas de proyecto (paso 1) ---
        public static readonly string[] Templates = {
            "Configuración personalizada",
            "Todas las Imágenes del sitio web",
            "Archivos de Audio de la página de inicio y del siguiente nivel",
            "Todos los archivos de Video del sitio web",
            "Todos los archivos del sitio web excepto páginas web e imágenes",
            "El sitio web completo",
            "Página de inicio completa con imágenes, estilos, y scripts para navegar sin conexión",
            "Página de inicio completa",
        };

        // Lo que hace cada plantilla en los pasos siguientes (como IDM). La 0 no cambia nada.
        public void ApplyTemplate(int t, AppConfig cfg)
        {
            Template = t;
            if (t == 0) return;
            string host = "sitio";
            try { host = new Uri(StartUrl).Host; } catch { }
            ExcludeFilter = null; ConvertLinks = false; RelativeSubfolders = false; Overwrite = false;
            ThisSiteOnly = true; HideDuplicates = false; NoParent = false; MainDomain = false;
            WholeSite = true; LevelsSite = 2; LevelsOther = 0; SaveMode = 0; NoLinkedPages = false;
            switch (t)
            {
                case 1: IncludeFilter = GrabberFilters.Images; HideDuplicates = true; break;
                case 2: IncludeFilter = GrabberFilters.Audio; WholeSite = false; LevelsSite = 1; ThisSiteOnly = false; break;
                case 3: IncludeFilter = GrabberFilters.Video; ThisSiteOnly = false; break;
                case 4: IncludeFilter = GrabberFilters.All; ExcludeFilter = GrabberFilters.WebAndImages; HideDuplicates = true; break;
                case 5:
                case 6:
                case 7:
                    IncludeFilter = t == 6 ? GrabberFilters.PageWithResources : GrabberFilters.All;
                    // "Página de inicio completa": solo esa página y lo que usa (no las páginas que enlaza).
                    if (t != 5) { WholeSite = false; LevelsSite = 0; NoLinkedPages = true; }
                    SaveMode = 2; RelativeSubfolders = true; ConvertLinks = true; ThisSiteOnly = t != 7;
                    SaveFolder = Path.Combine(cfg != null ? cfg.DefaultFolder ?? "" : "", Util.SanitizeFileName(host));
                    break;
            }
        }

        public bool PagesAreDownloaded
        {
            get
            {
                GrabberFilter f = GrabberFilters.Find(IncludeFilter);
                return f != null && GrabberFilters.MatchesMask("index.html", f.Mask, StartUrl, StartUrl);
            }
        }

        // --- Guardar y cargar (%APPDATA%\IDMClone\grabber\<nombre>.json) ---
        public static string Folder { get { return Path.Combine(AppConfig.Dir, "grabber"); } }
        public static string PathFor(string name) { return Path.Combine(Folder, Util.SanitizeFileName(name) + ".json"); }

        public void Save()
        {
            if (string.IsNullOrEmpty(Name)) throw new InvalidOperationException("El proyecto no tiene nombre.");
            Directory.CreateDirectory(Folder);
            FileVersion = Version;
            string json;
            lock (this) json = AppConfig.NewSerializer().Serialize(this);
            Util.WriteAllTextAtomic(PathFor(Name), json);
        }

        public static GrabberProject Load(string name)
        {
            string p = PathFor(name);
            if (!File.Exists(p)) throw new FileNotFoundException("No existe el proyecto \"" + name + "\".");
            GrabberProject g;
            try { g = AppConfig.NewSerializer().Deserialize<GrabberProject>(File.ReadAllText(p)); }
            catch (Exception) { throw new InvalidDataException("No se puede cargar el proyecto.\r\nEl archivo del proyecto de Grabber está dañado o su versión no es la correcta."); }
            if (g == null) throw new InvalidDataException("No se puede cargar el proyecto.\r\nEl archivo del proyecto de Grabber está dañado o su versión no es la correcta.");
            if (g.Pages == null) g.Pages = new List<GrabberPage>();
            if (g.Files == null) g.Files = new List<GrabberFile>();
            if (g.ExploreAtOnce < 1 || g.ExploreAtOnce > 10) g.ExploreAtOnce = 4;
            if (g.DownloadAtOnce < 1 || g.DownloadAtOnce > 10) g.DownloadAtOnce = 4;
            // Lo que estaba a medias al guardar vuelve a quedar pendiente.
            foreach (GrabberFile f in g.Files) if (f.Status == "Descargando") f.Status = "Detenido";
            return g;
        }

        public static List<string> SavedNames()
        {
            List<string> r = new List<string>();
            if (!Directory.Exists(Folder)) return r;
            foreach (string f in Directory.GetFiles(Folder, "*.json")) r.Add(Path.GetFileNameWithoutExtension(f));
            r.Sort(StringComparer.CurrentCultureIgnoreCase);
            return r;
        }

        public static void Delete(string name) { string p = PathFor(name); if (File.Exists(p)) File.Delete(p); }

        // --- Plantillas propias ("Proyecto > Guardar configuración en una plantilla") ---
        public static string TemplatesFolder { get { return Path.Combine(Folder, "plantillas"); } }

        public GrabberProject SettingsCopy()
        {
            JavaScriptSerializer js = AppConfig.NewSerializer();
            GrabberProject c = js.Deserialize<GrabberProject>(js.Serialize(this));
            c.Pages = new List<GrabberPage>(); c.Files = new List<GrabberFile>();
            c.Name = null;
            return c;
        }

        public void SaveAsTemplate(string templateName)
        {
            foreach (string t in Templates) if (string.Equals(t, templateName, StringComparison.CurrentCultureIgnoreCase))
                throw new ArgumentException("Este nombre está reservado. Por favor, selecciona otro nombre para tu plantilla.");
            GrabberProject c = SettingsCopy();
            c.StartUrl = null; c.UseAuth = false; c.User = null; c.PasswordEnc = null; c.ManualLogin = false; c.ManualLoginUrl = null;
            Directory.CreateDirectory(TemplatesFolder);
            Util.WriteAllTextAtomic(Path.Combine(TemplatesFolder, Util.SanitizeFileName(templateName) + ".json"), AppConfig.NewSerializer().Serialize(c));
        }

        public static List<string> UserTemplates()
        {
            List<string> r = new List<string>();
            if (Directory.Exists(TemplatesFolder)) foreach (string f in Directory.GetFiles(TemplatesFolder, "*.json")) r.Add(Path.GetFileNameWithoutExtension(f));
            return r;
        }

        // Aplica una plantilla propia: copia la configuración de los pasos 2 a 4.
        public void ApplyUserTemplate(string templateName)
        {
            string p = Path.Combine(TemplatesFolder, Util.SanitizeFileName(templateName) + ".json");
            GrabberProject t;
            try { t = AppConfig.NewSerializer().Deserialize<GrabberProject>(File.ReadAllText(p)); }
            catch (Exception) { throw new InvalidDataException("No se puede cargar la plantilla.\r\nLa información del archivo de la plantilla está dañado o su versión no es la correcta."); }
            SaveMode = t.SaveMode; SaveCategory = t.SaveCategory; SaveFolder = t.SaveFolder; RelativeSubfolders = t.RelativeSubfolders;
            ConvertLinks = t.ConvertLinks; Overwrite = t.Overwrite; AddOnClose = t.AddOnClose;
            WholeSite = t.WholeSite; LevelsSite = t.LevelsSite; LevelsOther = t.LevelsOther; IgnorePopups = t.IgnorePopups;
            NoParent = t.NoParent; MainDomain = t.MainDomain; NoLinkedPages = t.NoLinkedPages; ProcessJs = t.ProcessJs; ExploreOnly = t.ExploreOnly; ExploreNot = t.ExploreNot;
            IncludeFilter = t.IncludeFilter; ExcludeFilter = t.ExcludeFilter; ThisSiteOnly = t.ThisSiteOnly; HideDuplicates = t.HideDuplicates;
            DownloadAll = t.DownloadAll; DownloadOnly = t.DownloadOnly; DownloadNot = t.DownloadNot; MinKB = t.MinKB; MaxKB = t.MaxKB;
            ExploreAtOnce = t.ExploreAtOnce; DownloadAtOnce = t.DownloadAtOnce; LinkTextAsDescription = t.LinkTextAsDescription;
            Template = 0;
        }
    }

    // Filtros de archivos del paso 4 (se pueden añadir y editar, como en IDM).
    public static class GrabberFilters
    {
        public const string All = "Todos los Archivos", Images = "Archivos de Imagen", Video = "Archivos de Video", Audio = "Archivos de Audio",
                            Exe = "Archivos Ejecutables", Zip = "Archivos Comprimidos", Pdf = "Archivos de Documentos PDF",
                            Web = "Contenido web", WebAndImages = "Páginas web e imágenes", PageWithResources = "Páginas web, imágenes, estilos y scripts";

        public static List<GrabberFilter> Defaults()
        {
            List<GrabberFilter> l = new List<GrabberFilter>();
            l.Add(new GrabberFilter(All, "*.*"));
            l.Add(new GrabberFilter(Images, "*.gif,*.jpg,*.jpeg,*.jpe,*.bmp,*.png,*.tif,*.tiff,*.webp,*.svg,*.ico"));
            l.Add(new GrabberFilter(Video, "*.avi,*.mpg,*.mpeg,*.mpe,*.mp4,*.m4v,*.mkv,*.mov,*.qt,*.wmv,*.asf,*.flv,*.webm,*.3gp,*.ogv,*.rm,*.rmvb"));
            l.Add(new GrabberFilter(Audio, "*.mp3,*.wav,*.wma,*.m4a,*.aac,*.ogg,*.oga,*.opus,*.flac,*.mid,*.ra,*.aif"));
            l.Add(new GrabberFilter(Exe, "*.exe,*.msi,*.msu,*.apk,*.bat,*.cmd"));
            l.Add(new GrabberFilter(Zip, "*.zip,*.rar,*.7z,*.gz,*.tgz,*.tar,*.bz2,*.arj,*.lzh,*.ace,*.cab,*.iso,*.r0*"));
            l.Add(new GrabberFilter(Pdf, "*.pdf"));
            l.Add(new GrabberFilter(Web, "*.htm,*.html,*.shtml,*.xhtml,*.php,*.asp,*.aspx,*.jsp,*.cfm,*.css,*.js"));
            l.Add(new GrabberFilter(WebAndImages, "*.htm,*.html,*.shtml,*.xhtml,*.php,*.asp,*.aspx,*.jsp,*.cfm,*.css,*.js,*.gif,*.jpg,*.jpeg,*.jpe,*.bmp,*.png,*.tif,*.tiff,*.webp,*.svg,*.ico"));
            l.Add(new GrabberFilter(PageWithResources, "*.htm,*.html,*.shtml,*.xhtml,*.php,*.asp,*.aspx,*.jsp,*.cfm,*.css,*.js,*.gif,*.jpg,*.jpeg,*.jpe,*.bmp,*.png,*.tif,*.tiff,*.webp,*.svg,*.ico,*.woff,*.woff2,*.ttf"));
            return l;
        }

        static List<GrabberFilter> _list;
        static string FiltersPath { get { return Path.Combine(GrabberProject.Folder, "filtros.json"); } }

        public static List<GrabberFilter> List
        {
            get
            {
                if (_list == null)
                {
                    try { if (File.Exists(FiltersPath)) _list = AppConfig.NewSerializer().Deserialize<List<GrabberFilter>>(File.ReadAllText(FiltersPath)); }
                    catch { _list = null; }
                    if (_list == null || _list.Count == 0) _list = Defaults();
                    // Los de serie siempre están (las plantillas los usan).
                    foreach (GrabberFilter d in Defaults()) if (Find(_list, d.Name) == null) _list.Add(d);
                }
                return _list;
            }
        }

        public static void Reload() { _list = null; }

        public static void Save()
        {
            Directory.CreateDirectory(GrabberProject.Folder);
            Util.WriteAllTextAtomic(FiltersPath, AppConfig.NewSerializer().Serialize(List));
        }

        public static GrabberFilter Find(string name) { return Find(List, name); }

        static GrabberFilter Find(List<GrabberFilter> l, string name)
        {
            if (name == null) return null;
            foreach (GrabberFilter f in l) if (string.Equals(f.Name, name, StringComparison.CurrentCultureIgnoreCase)) return f;
            return null;
        }

        // ¿El archivo (o su URL) coincide con la máscara? "*.*" vale para todo.
        public static bool MatchesMask(string name, string mask, string url, string startUrl)
        {
            if (string.IsNullOrEmpty(mask)) return false;
            foreach (string raw in mask.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string m = raw.Trim();
                if (m.Length == 0) continue;
                if (m == "*.*" || m == "*") return true;
                if (m.IndexOf("<start page>", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    m = Regex.Replace(m, Regex.Escape("<start page>"), startUrl ?? "", RegexOptions.IgnoreCase);
                    if (Util.WildcardMatch(url ?? "", m)) return true;
                    continue;
                }
                if (m.IndexOf('/') >= 0) { if (Util.WildcardMatch(url ?? "", m) || Util.WildcardMatch(StripScheme(url), m)) return true; continue; }
                if (Util.WildcardMatch(name ?? "", m)) return true;
            }
            return false;
        }

        public static string StripScheme(string url)
        {
            if (url == null) return "";
            int i = url.IndexOf("://", StringComparison.Ordinal);
            return i >= 0 ? url.Substring(i + 3) : url;
        }
    }

    // Enlaces de una página HTML (y de su CSS y JavaScript sencillo).
    public static class GrabberHtml
    {
        public class Link
        {
            public string Url, Text;
            public bool Page;     // a/area/frame/iframe/meta refresh: se puede explorar
            public bool Popup;    // window.open(...)
        }

        static readonly Regex TagRe = new Regex(@"<(a|area|link|img|script|iframe|frame|embed|source|video|audio|input|body|table|td|th|object|param|track|meta|base)\b([^>]*)>",
                                                RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex AttrRe = new Regex(@"([\w:-]+)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.Compiled);
        static readonly Regex AnchorRe = new Regex(@"<a\b([^>]*)>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        static readonly Regex CssUrlRe = new Regex(@"url\(\s*['""]?([^'"")]+?)['""]?\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ImportRe = new Regex(@"@import\s+['""]([^'""]+)['""]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ScriptRe = new Regex(@"<script\b[^>]*>(.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        static readonly Regex StyleRe = new Regex(@"<style\b[^>]*>(.*?)</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        static readonly Regex JsStringRe = new Regex(@"['""]((?:https?:)?[^'""\s<>()]+\.(?:html?|php|aspx?|jsp|zip|rar|7z|exe|msi|pdf|mp3|mp4|avi|mkv|mov|wmv|flv|webm|ogg|wav|jpe?g|gif|png|bmp|webp|svg|iso|gz|tar|doc|docx|xls|xlsx|ppt|pptx|txt)(?:\?[^'""\s<>]*)?)['""]",
                                                     RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex OpenRe = new Regex(@"window\.open\s*\(\s*['""]([^'""]+)['""]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex CommentRe = new Regex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
        static readonly Regex TagStripRe = new Regex(@"<[^>]+>", RegexOptions.Compiled);

        static Dictionary<string, string> Attrs(string s)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in AttrRe.Matches(s))
            {
                string v = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
                if (!d.ContainsKey(m.Groups[1].Value)) d[m.Groups[1].Value] = WebUtility.HtmlDecode(v).Trim();
            }
            return d;
        }

        public static string Resolve(string baseUrl, string rel)
        {
            if (string.IsNullOrEmpty(rel)) return null;
            rel = rel.Trim();
            if (rel.StartsWith("#") || rel.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) return null;
            Uri u;
            try
            {
                if (!Uri.TryCreate(new Uri(baseUrl), rel, out u)) return null;
            }
            catch { return null; }
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeFtp) return null;
            string s = u.AbsoluteUri;
            int hash = s.IndexOf('#');
            return hash >= 0 ? s.Substring(0, hash) : s;
        }

        public static List<Link> Extract(string html, string pageUrl, bool processJs)
        {
            List<Link> r = new List<Link>();
            Dictionary<string, Link> seen = new Dictionary<string, Link>();
            html = CommentRe.Replace(html, " ");
            string baseUrl = pageUrl;
            Action<string, string, bool, bool> add = delegate(string rel, string text, bool page, bool popup)
            {
                string u = Resolve(baseUrl, rel);
                if (u == null) return;
                Link l;
                if (seen.TryGetValue(u, out l))
                {
                    if (string.IsNullOrEmpty(l.Text) && !string.IsNullOrEmpty(text)) l.Text = text;
                    if (page && !popup) { l.Page = true; l.Popup = false; }
                    return;
                }
                l = new Link(); l.Url = u; l.Text = text; l.Page = page; l.Popup = popup;
                seen[u] = l; r.Add(l);
            };
            // Textos de los enlaces <a>...</a> (para la columna y la descripción).
            Dictionary<string, string> texts = new Dictionary<string, string>();
            foreach (Match m in AnchorRe.Matches(html))
            {
                string href;
                if (!Attrs(m.Groups[1].Value).TryGetValue("href", out href)) continue;
                string t = WebUtility.HtmlDecode(TagStripRe.Replace(m.Groups[2].Value, " "));
                t = Regex.Replace(t, @"\s+", " ").Trim();
                if (t.Length > 200) t = t.Substring(0, 200);
                if (t.Length > 0 && !texts.ContainsKey(href)) texts[href] = t;
            }
            foreach (Match m in TagRe.Matches(html))
            {
                string tag = m.Groups[1].Value.ToLowerInvariant();
                Dictionary<string, string> a = Attrs(m.Groups[2].Value);
                string v;
                switch (tag)
                {
                    case "base":
                        if (a.TryGetValue("href", out v) && Resolve(pageUrl, v) != null) baseUrl = Resolve(pageUrl, v);
                        break;
                    case "a":
                    case "area":
                        if (a.TryGetValue("href", out v))
                        {
                            string t; texts.TryGetValue(v, out t);
                            add(v, t ?? (a.ContainsKey("title") ? a["title"] : null), true, false);
                        }
                        break;
                    case "frame":
                    case "iframe":
                        if (a.TryGetValue("src", out v)) add(v, null, true, false);
                        break;
                    case "link":
                        if (a.TryGetValue("href", out v))
                        {
                            string rel = a.ContainsKey("rel") ? a["rel"].ToLowerInvariant() : "";
                            if (rel.Contains("stylesheet") || rel.Contains("icon") || rel.Contains("preload")) add(v, null, false, false);
                            else if (rel.Contains("alternate") || rel.Contains("next") || rel.Contains("prev")) add(v, null, true, false);
                        }
                        break;
                    case "meta":
                        if (a.TryGetValue("http-equiv", out v) && v.Equals("refresh", StringComparison.OrdinalIgnoreCase) && a.TryGetValue("content", out v))
                        {
                            Match u = Regex.Match(v, @"url\s*=\s*['""]?([^'""]+)", RegexOptions.IgnoreCase);
                            if (u.Success) add(u.Groups[1].Value.Trim(), null, true, false);
                        }
                        break;
                    default:
                        foreach (string at in new string[] { "src", "data-src", "poster", "background", "data" })
                            if (a.TryGetValue(at, out v)) add(v, a.ContainsKey("alt") ? a["alt"] : null, false, false);
                        if (a.TryGetValue("srcset", out v))
                            foreach (string part in v.Split(','))
                            {
                                string p = part.Trim(); int sp = p.IndexOf(' ');
                                add(sp > 0 ? p.Substring(0, sp) : p, null, false, false);
                            }
                        if (tag == "param" && a.TryGetValue("value", out v) && (a.ContainsKey("name") && Regex.IsMatch(a["name"], "^(movie|src|url|filename)$", RegexOptions.IgnoreCase)))
                            add(v, null, false, false);
                        break;
                }
            }
            foreach (Match s in StyleRe.Matches(html))
            {
                foreach (Match c in CssUrlRe.Matches(s.Groups[1].Value)) add(c.Groups[1].Value, null, false, false);
                foreach (Match c in ImportRe.Matches(s.Groups[1].Value)) add(c.Groups[1].Value, null, false, false);
            }
            // JavaScript: como IDM sin "Procesar JavaScript", se buscan en el texto los enlaces evidentes.
            HashSet<string> popups = new HashSet<string>();
            foreach (Match s in ScriptRe.Matches(html))
            {
                string js = s.Groups[1].Value;
                foreach (Match o in OpenRe.Matches(js)) { add(o.Groups[1].Value, null, true, true); popups.Add(o.Groups[1].Value); }
                foreach (Match c in JsStringRe.Matches(js))
                    if (!popups.Contains(c.Groups[1].Value)) add(c.Groups[1].Value.Replace("\\/", "/"), null, IsPageName(c.Groups[1].Value), false);
            }
            // Estilos en línea de cualquier etiqueta (style="background:url(...)").
            foreach (Match m in Regex.Matches(html, @"\sstyle\s*=\s*(""[^""]*""|'[^']*')", RegexOptions.IgnoreCase))
                foreach (Match c in CssUrlRe.Matches(WebUtility.HtmlDecode(m.Groups[1].Value))) add(c.Groups[1].Value, null, false, false);
            // Atributos onclick="..." con window.open o direcciones entre comillas.
            foreach (Match m in Regex.Matches(html, @"\son\w+\s*=\s*(""[^""]*""|'[^']*')", RegexOptions.IgnoreCase))
            {
                string js = WebUtility.HtmlDecode(m.Groups[1].Value);
                HashSet<string> opened = new HashSet<string>();
                foreach (Match o in OpenRe.Matches(js)) { add(o.Groups[1].Value, null, true, true); opened.Add(o.Groups[1].Value); }
                foreach (Match c in JsStringRe.Matches(js)) if (!opened.Contains(c.Groups[1].Value)) add(c.Groups[1].Value, null, IsPageName(c.Groups[1].Value), false);
            }
            return r;
        }

        public static List<string> ExtractCss(string css, string cssUrl)
        {
            List<string> r = new List<string>();
            foreach (Match c in CssUrlRe.Matches(css)) { string u = Resolve(cssUrl, c.Groups[1].Value); if (u != null && !r.Contains(u)) r.Add(u); }
            foreach (Match c in ImportRe.Matches(css)) { string u = Resolve(cssUrl, c.Groups[1].Value); if (u != null && !r.Contains(u)) r.Add(u); }
            return r;
        }

        static readonly string[] PageExts = { "", "htm", "html", "shtml", "xhtml", "php", "php3", "php4", "php5", "asp", "aspx", "jsp", "jspx", "cfm", "cgi", "pl", "do", "action" };

        public static bool IsPageName(string url)
        {
            string ext = ExtOf(url);
            return Array.IndexOf(PageExts, ext) >= 0;
        }

        public static string ExtOf(string url)
        {
            string p = url ?? "";
            int q = p.IndexOfAny(new char[] { '?', '#' }); if (q >= 0) p = p.Substring(0, q);
            int s = p.LastIndexOf('/'); if (s >= 0) p = p.Substring(s + 1);
            int d = p.LastIndexOf('.');
            return d > 0 ? p.Substring(d + 1).ToLowerInvariant() : "";
        }

        // Nombre del archivo en disco: el de la URL; las páginas sin nombre ("/", "/docs/") son index.html.
        public static string FileNameFor(string url, bool page)
        {
            string name = null;
            try
            {
                Uri u = new Uri(url);
                string last = Uri.UnescapeDataString(u.AbsolutePath);
                last = last.Substring(last.LastIndexOf('/') + 1);
                name = last;
            }
            catch { }
            if (string.IsNullOrEmpty(name)) name = page ? "index.html" : "archivo";
            // Páginas dinámicas: x.php?id=3 -> x_id_3.html (como el Grabber, que las guarda como .html).
            try
            {
                Uri u = new Uri(url);
                if (page && u.Query.Length > 1)
                {
                    string q = Regex.Replace(Uri.UnescapeDataString(u.Query.Substring(1)), @"[^\w\-.]+", "_");
                    name = Path.GetFileNameWithoutExtension(name) + "_" + q;
                    if (name.Length > 120) name = name.Substring(0, 120);
                    name += ".html";
                }
                else if (page && !Regex.IsMatch(Path.GetExtension(name), @"^\.(s?html?|xhtml)$", RegexOptions.IgnoreCase)) name = Path.GetFileNameWithoutExtension(name) + ".html";
            }
            catch { }
            return Util.SanitizeFileName(name) ?? "archivo";
        }

        public static string TypeLabel(string name)
        {
            string ext = Path.GetExtension(name ?? "").TrimStart('.').ToUpperInvariant();
            if (ext.Length == 0) return "Archivo";
            switch (ext)
            {
                case "JPG": case "JPEG": case "JPE": case "GIF": case "PNG": case "BMP": case "TIF": case "TIFF": case "WEBP": case "SVG": case "ICO":
                    return "Imagen " + ext;
                case "HTM": case "HTML": case "SHTML": case "XHTML": case "PHP": case "ASP": case "ASPX": case "JSP":
                    return "Página web";
                case "MP3": case "WAV": case "WMA": case "M4A": case "AAC": case "OGG": case "FLAC": case "OPUS":
                    return "Audio " + ext;
                case "MP4": case "AVI": case "MKV": case "MOV": case "WMV": case "FLV": case "WEBM": case "MPG": case "MPEG":
                    return "Vídeo " + ext;
                default: return "Archivo " + ext;
            }
        }
    }

    // Motor del Grabber: explorar (páginas) y descargar (archivos marcados).
    public class GrabberEngine
    {
        public readonly GrabberProject P;
        readonly AppConfig _cfg;
        readonly CookieContainer _jar = new CookieContainer();
        volatile bool _exploreStop, _downloadStop;
        int _exploring; volatile int _downloading;
        readonly object _lock = new object();
        readonly HashSet<string> _knownPages = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string, GrabberFile> _knownFiles = new Dictionary<string, GrabberFile>(StringComparer.Ordinal);
        readonly HashSet<string> _probing = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> _dupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public int Examined;          // archivos comprobados (tamaño/tipo)
        public string StatusText = "Detenido";
        DownloadManager _dl;          // descargas del propio Grabber (fuera de la lista principal)

        public event Action Changed;
        public event Action ExploreFinished, DownloadFinished;

        public GrabberEngine(GrabberProject p, AppConfig cfg)
        {
            P = p; _cfg = cfg;
            lock (_lock)
            {
                foreach (GrabberPage g in p.Pages) { _knownPages.Add(g.Url); if (g.FinalUrl != null) _knownPages.Add(g.FinalUrl); }
                foreach (GrabberFile f in p.Files)
                {
                    _knownFiles[f.Url] = f;
                    if (f.Size >= 0) _dupKeys.Add(f.Name + "|" + f.Size);
                }
            }
        }

        public bool Exploring { get { return Volatile.Read(ref _exploring) > 0; } }
        public bool Downloading { get { return _downloading > 0; } }

        void Raise() { Action h = Changed; if (h != null) h(); }

        // --- Sitio actual ---
        Uri StartUri { get { return new Uri(P.StartUrl); } }

        static string NoWww(string host) { host = (host ?? "").ToLowerInvariant(); return host.StartsWith("www.") ? host.Substring(4) : host; }

        // Dominio principal: ejemplo.com, ejemplo.co.uk...
        public static string MainDomainOf(string host)
        {
            string[] p = (host ?? "").ToLowerInvariant().Split('.');
            if (p.Length <= 2) return string.Join(".", p);
            int take = 2;
            string sld = p[p.Length - 2];
            if (p[p.Length - 1].Length == 2 && (sld == "co" || sld == "com" || sld == "org" || sld == "net" || sld == "gov" || sld == "edu" || sld == "ac" || sld == "gob")) take = 3;
            return string.Join(".", p, p.Length - take, take);
        }

        // ¿Se le pueden dar el usuario y la contraseña del proyecto? Solo a su servidor (con o sin "www.")
        // y nunca por http si la página inicial es https (irían sin cifrar).
        bool IsProjectHost(Uri u)
        {
            Uri s;
            if (u == null || !Uri.TryCreate(P.StartUrl, UriKind.Absolute, out s)) return false;
            if (s.Scheme == Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttps) return false;
            return NoWww(u.Host) == NoWww(s.Host);
        }

        public bool OnSite(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            string start = NoWww(StartUri.Host), host = NoWww(u.Host);
            if (host == start) return true;
            return P.MainDomain && MainDomainOf(host) == MainDomainOf(start);
        }

        static List<string> Patterns(string s)
        {
            List<string> r = new List<string>();
            foreach (string x in (s ?? "").Split(new char[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) if (x.Trim().Length > 0) r.Add(x.Trim());
            return r;
        }

        static bool AnyMatch(string url, List<string> patterns)
        {
            string bare = GrabberFilters.StripScheme(url);
            foreach (string p in patterns)
            {
                string pat = GrabberFilters.StripScheme(p);
                if (pat.IndexOf('*') < 0) pat = pat.TrimEnd('/') + "*";
                if (Util.WildcardMatch(bare, pat) || Util.WildcardMatch(bare, "*" + pat)) return true;
            }
            return false;
        }

        // ¿Se explora esta página? (paso 3)
        public bool ShouldExplore(string url, int level, int offLevel, bool popup)
        {
            if (popup && P.IgnorePopups) return false;
            if (AnyMatch(url, Patterns(P.LogoutPages))) return false;
            List<string> only = Patterns(P.ExploreOnly);
            if (only.Count > 0 && !AnyMatch(url, only)) return false;
            if (AnyMatch(url, Patterns(P.ExploreNot))) return false;
            if (offLevel == 0)
            {
                if (!P.WholeSite && level > P.LevelsSite) return false;
                if (P.NoParent)
                {
                    Uri s = StartUri, u = new Uri(url);
                    string dir = s.AbsolutePath.Substring(0, s.AbsolutePath.LastIndexOf('/') + 1);
                    if (NoWww(u.Host) == NoWww(s.Host) && !u.AbsolutePath.StartsWith(dir, StringComparison.Ordinal)) return false;
                }
                return true;
            }
            return offLevel <= P.LevelsOther;
        }

        // ¿Pasa los filtros de archivos? (paso 4; el tamaño se mira aparte)
        public bool FileMatches(string url, string name)
        {
            GrabberFilter inc = GrabberFilters.Find(P.IncludeFilter);
            if (inc == null || !GrabberFilters.MatchesMask(name, inc.Mask, url, P.StartUrl)) return false;
            GrabberFilter exc = GrabberFilters.Find(P.ExcludeFilter);
            if (exc != null && GrabberFilters.MatchesMask(name, exc.Mask, url, P.StartUrl)) return false;
            if (P.ThisSiteOnly && !OnSite(url)) return false;
            List<string> only = Patterns(P.DownloadOnly);
            if (only.Count > 0 && !AnyMatch(url, only)) return false;
            if (AnyMatch(url, Patterns(P.DownloadNot))) return false;
            return true;
        }

        bool SizeOk(long size)
        {
            if (size < 0) return true;
            if (P.MinKB > 0 && size < P.MinKB * 1024) return false;
            if (P.MaxKB > 0 && size > P.MaxKB * 1024) return false;
            return true;
        }

        // --- Red ---
        HttpWebRequest NewRequest(string url, string referer)
        {
            HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
            r.UserAgent = _cfg.UserAgent;
            r.AllowAutoRedirect = true; r.MaximumAutomaticRedirections = 10;
            r.Timeout = 30000; r.ReadWriteTimeout = 60000;
            r.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            r.CookieContainer = _jar;
            if (_cfg.ProxyMode != "system") r.Proxy = NetSettings.ProxyFor(_cfg, r.RequestUri);
            if (!HappyEyeballs.Global) HappyEyeballs.Use(r.ServicePoint);
            // Usuario y contraseña del proyecto: solo para su sitio, nunca para los enlaces a otros servidores.
            NetworkCredential cred = null;
            if (P.UseAuth && !string.IsNullOrEmpty(P.User) && IsProjectHost(r.RequestUri))
                cred = new NetworkCredential(P.User, P.Password);
            if (cred == null) cred = NetSettings.CredentialsFor(_cfg, r.RequestUri);
            if (cred != null) { r.Credentials = cred; r.PreAuthenticate = true; }
            if (!string.IsNullOrEmpty(referer)) { try { r.Referer = referer; } catch { } }
            return r;
        }

        // GetResponse siguiendo también las redirecciones 308.
        HttpWebResponse Fetch(ref string url, string referer, string method, bool firstByte, out HttpWebRequest req)
        {
            for (int hop = 0; ; hop++)
            {
                req = NewRequest(url, referer);
                if (method != null) req.Method = method;
                if (firstByte) req.AddRange(0L, 0L);
                try { return (HttpWebResponse)req.GetResponse(); }
                catch (WebException wex)
                {
                    string target = Util.RedirectTarget(wex);
                    if (target == null || hop >= 10) throw;
                    url = target;
                }
            }
        }

        public string CookiesFor(string url)
        {
            try { return _jar.GetCookieHeader(new Uri(url)); } catch { return null; }
        }

        // --- Inicio de sesión manual (paso 1 > Avanzado) ---
        // Las cookies de la sesión del navegador solo viven en el bote de este motor (_jar): no se guardan en el
        // proyecto y se vuelven a pedir en cada ejecución. El bote las manda solo a su servidor.

        // Para las pruebas: sustituye a CookieBridge.GetCookies(url, timeoutMs). null = CookieBridge.
        public static Func<string, int, string> CookieProvider;
        public string LoginDoneFor;   // página de inicio de sesión ya atendida en este motor (no se vuelve a pedir)
        public string LoginNote;      // qué pasó con la sesión (para el estado)

        public static bool BrowserCookiesAvailable
        {
            get
            {
                if (CookieProvider != null) return true;
                try { return CookieBridge.Available; } catch { return false; }
            }
        }

        static string AskBrowserCookies(string url, int timeoutMs)
        {
            Func<string, int, string> p = CookieProvider;
            try { return p != null ? p(url, timeoutMs) : CookieBridge.GetCookies(url, timeoutMs); }
            catch { return null; }
        }

        static bool IsWebUrl(string url, out Uri u)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
        }

        // Pide al navegador las cookies de la página de inicio y, si es de otro servidor, las de la página de inicio
        // de sesión. Devuelve cuántas se importaron, o -1 si el navegador no respondió.
        public int ImportBrowserCookies(string loginUrl, int timeoutMs)
        {
            if (!BrowserCookiesAvailable) return -1;
            List<string> urls = new List<string>();
            Uri s, l;
            if (IsWebUrl(P.StartUrl, out s)) urls.Add(P.StartUrl);
            if (IsWebUrl(loginUrl, out l) && (s == null || !string.Equals(NoWww(l.Host), NoWww(s.Host), StringComparison.Ordinal) || l.Scheme != s.Scheme))
                urls.Add(loginUrl);
            bool answered = false;
            int n = 0;
            foreach (string u in urls)
            {
                string header = AskBrowserCookies(u, timeoutMs);
                if (header == null) continue;
                answered = true;
                n += ImportCookieHeader(u, header);
            }
            return answered ? n : -1;
        }

        // Cookies en formato de cabecera ("a=1; b=2", con o sin "Cookie:") para el servidor de esa dirección (y su
        // gemelo con/sin "www.", que el Grabber trata como el mismo sitio). Solo para ese servidor: no se mandan a
        // otros dominios ni subdominios; si la dirección es https, tampoco por http. Devuelve cuántas se importaron.
        public int ImportCookieHeader(string url, string header)
        {
            Uri u;
            if (string.IsNullOrEmpty(header) || !IsWebUrl(url, out u)) return 0;
            string text = header.Trim();
            if (text.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) text = text.Substring(7);
            List<Uri> targets = new List<Uri>();
            targets.Add(u);
            string host = u.Host.ToLowerInvariant();
            IPAddress ip;
            if (host.IndexOf('.') > 0 && !IPAddress.TryParse(host, out ip))
            {
                UriBuilder b = new UriBuilder(u);
                b.Host = host.StartsWith("www.") ? host.Substring(4) : "www." + host;
                if (b.Host.IndexOf('.') > 0) targets.Add(b.Uri);
            }
            int n = 0;
            foreach (string part in text.Split(new char[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string kv = part.Trim();
                int eq = kv.IndexOf('=');
                if (eq <= 0) continue;
                string name = kv.Substring(0, eq).Trim(), value = kv.Substring(eq + 1).Trim();
                bool added = false;
                foreach (Uri t in targets)
                {
                    try
                    {
                        Cookie c = new Cookie(name, value, "/"); // sin dominio: solo para ese servidor exacto
                        c.Secure = u.Scheme == Uri.UriSchemeHttps;
                        _jar.Add(t, c);
                        added = true;
                    }
                    catch { } // nombre o valor que .NET no admite (comas, espacios...)
                }
                if (added) n++;
            }
            return n;
        }

        // --- Explorar ---
        public void StartExploring()
        {
            if (Exploring) return;
            _exploreStop = false;
            lock (_lock)
            {
                if (P.Pages.Count == 0)
                {
                    GrabberPage s = new GrabberPage(); s.Url = GrabberHtml.Resolve(P.StartUrl, P.StartUrl) ?? P.StartUrl; s.Level = 0;
                    P.Pages.Add(s); _knownPages.Add(s.Url);
                }
                foreach (GrabberPage g in P.Pages) if (g.Status == "Explorando") g.Status = null;
            }
            StatusText = "Explorando sitio";
            int n = Math.Max(1, Math.Min(10, P.ExploreAtOnce));
            _exploring = n;
            for (int i = 0; i < n; i++)
            {
                Thread t = new Thread(ExploreWorker); t.IsBackground = true; t.Name = "grabber-explorar-" + i; t.Start();
            }
            Raise();
        }

        public void StopExploring() { _exploreStop = true; }

        GrabberPage NextPage(out bool idle)
        {
            lock (_lock)
            {
                idle = true;
                foreach (GrabberPage g in P.Pages)
                {
                    if (g.Status == "Explorando") idle = false;
                    else if (g.Status == null) { g.Status = "Explorando"; idle = false; return g; }
                }
                return null;
            }
        }

        int _active;

        void ExploreWorker()
        {
            try
            {
                while (!_exploreStop)
                {
                    bool idle;
                    GrabberPage pg = NextPage(out idle);
                    if (pg == null)
                    {
                        // Otro hilo aún procesa páginas que pueden añadir más: esperar un poco.
                        if (Volatile.Read(ref _active) == 0 && idle) break;
                        Thread.Sleep(100);
                        continue;
                    }
                    Interlocked.Increment(ref _active);
                    try { ProcessPage(pg); }
                    catch (Exception) { lock (_lock) pg.Status = "Error"; }
                    finally { Interlocked.Decrement(ref _active); }
                    Raise();
                }
            }
            finally
            {
                if (Interlocked.Decrement(ref _exploring) == 0)
                {
                    lock (_lock) foreach (GrabberPage g in P.Pages) if (g.Status == "Explorando") g.Status = null;
                    // Esperar a las comprobaciones de tamaño pendientes.
                    for (int i = 0; i < 300 && ProbesRunning; i++) Thread.Sleep(100);
                    StatusText = _exploreStop ? "Detenido" : "Completo";
                    Raise();
                    Action h = ExploreFinished; if (h != null) h();
                }
            }
        }

        bool ProbesRunning { get { lock (_lock) return _probing.Count > 0; } }

        void ProcessPage(GrabberPage pg)
        {
            HttpWebRequest req;
            string html = null, finalUrl = pg.Url, type = "";
            long len = -1; string cd = null;
            try
            {
                string fetchUrl = pg.Url;
                using (HttpWebResponse resp = Fetch(ref fetchUrl, pg.Referer, null, false, out req))
                {
                    finalUrl = resp.ResponseUri.AbsoluteUri;
                    type = (resp.ContentType ?? "").ToLowerInvariant();
                    len = resp.ContentLength; cd = resp.Headers["Content-Disposition"];
                    bool isHtml = type.Contains("text/html") || type.Contains("xhtml");
                    bool isCss = type.Contains("text/css");
                    if ((isHtml || isCss) && string.IsNullOrEmpty(cd))
                    {
                        using (Stream s = resp.GetResponseStream())
                        using (MemoryStream m = new MemoryStream())
                        {
                            byte[] buf = new byte[65536]; int r; long total = 0;
                            while ((r = s.Read(buf, 0, buf.Length)) > 0 && total < 8 << 20) { m.Write(buf, 0, r); total += r; }
                            html = Decode(m.ToArray(), type);
                        }
                        if (isCss)
                        {
                            foreach (string u in GrabberHtml.ExtractCss(html, finalUrl)) Found(u, null, false, false, pg, finalUrl);
                            html = null;
                        }
                    }
                    else req.Abort();
                }
            }
            catch (WebException wex)
            {
                HttpWebResponse h = wex.Response as HttpWebResponse;
                lock (_lock) pg.Status = h != null && (int)h.StatusCode == 404 ? "No Encontrado" : "Error";
                return;
            }
            // No era una página (p. ej. descargar.php que da un .zip): es un archivo.
            if (html == null && !type.Contains("text/css"))
            {
                lock (_lock) pg.Status = "Procesada";
                string name = Util.FileNameFromContentDisposition(cd);
                if (string.IsNullOrEmpty(name)) name = GrabberHtml.FileNameFor(finalUrl, false);
                AddFile(pg.Url, name, len, "", pg.Referer, false, true);
                return;
            }
            lock (_lock)
            {
                pg.Status = "Procesada";
                // Redirigida a otra dirección (p. ej. /autor -> /autor/): es la misma página. Si esa ya
                // se conoce, no se procesa otra vez; si no, la dirección final queda como conocida.
                if (finalUrl != pg.Url)
                {
                    pg.FinalUrl = finalUrl;
                    if (_knownPages.Contains(finalUrl)) return;
                    _knownPages.Add(finalUrl);
                }
            }
            if (html == null) return;
            // La página misma, si el filtro incluye páginas web (sitio completo...), con su dirección final.
            AddFile(finalUrl, GrabberHtml.FileNameFor(finalUrl, true), -1, pg.Level == 0 ? "<página de inicio>" : null, pg.Referer, true, false);
            foreach (GrabberHtml.Link l in GrabberHtml.Extract(html, finalUrl, P.ProcessJs))
                Found(l.Url, l.Text, l.Page, l.Popup, pg, finalUrl);
        }

        static string Decode(byte[] b, string contentType)
        {
            Encoding enc = null;
            Match m = Regex.Match(contentType ?? "", @"charset=([\w-]+)", RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(Encoding.ASCII.GetString(b, 0, Math.Min(b.Length, 2048)), @"charset\s*=\s*[""']?([\w-]+)", RegexOptions.IgnoreCase);
            if (m.Success) { try { enc = Encoding.GetEncoding(m.Groups[1].Value); } catch { enc = null; } }
            if (enc == null)
            {
                try { return new UTF8Encoding(false, true).GetString(b); }
                catch (DecoderFallbackException) { enc = Encoding.GetEncoding(1252); }
            }
            return enc.GetString(b);
        }

        void Found(string url, string text, bool pageLink, bool popup, GrabberPage parent, string parentUrl)
        {
            if (url == null) return;
            bool onSite = OnSite(url);
            int level = parent.Level + 1;
            int off = onSite ? 0 : parent.OffLevel + 1;
            bool pageLike = pageLink && GrabberHtml.IsPageName(url);
            if (pageLike)
            {
                lock (_lock)
                {
                    if (_knownPages.Contains(url)) return;
                    if (!ShouldExplore(url, level, off, popup))
                    {
                        // No se explora, pero si el filtro incluye páginas web, se ofrece como archivo.
                        if (!popup && !P.NoLinkedPages && FileMatches(url, GrabberHtml.FileNameFor(url, true)) && !AnyMatch(url, Patterns(P.LogoutPages)))
                            AddFileLocked(url, GrabberHtml.FileNameFor(url, true), -1, text, parentUrl, true);
                        return;
                    }
                    _knownPages.Add(url);
                    GrabberPage g = new GrabberPage(); g.Url = url; g.Referer = parentUrl; g.Level = level; g.OffLevel = off;
                    P.Pages.Add(g);
                }
                return;
            }
            AddFile(url, GrabberHtml.FileNameFor(url, false), -1, text, parentUrl, false, false);
        }

        // Archivo encontrado: si pasa los filtros, se comprueban su tamaño y tipo (en segundo plano) y se añade.
        void AddFile(string url, string name, long size, string text, string referer, bool isPage, bool sizeKnown)
        {
            lock (_lock)
            {
                if (_knownFiles.ContainsKey(url) || _probing.Contains(url)) return;
                if (!FileMatches(url, name)) return;
                if (isPage || sizeKnown) { AddFileLocked(url, name, size, text, referer, isPage); return; }
                _probing.Add(url);
            }
            ThreadPool.QueueUserWorkItem(delegate { Probe(url, name, text, referer); });
        }

        GrabberFile AddFileLocked(string url, string name, long size, string text, string referer, bool isPage)
        {
            if (_knownFiles.ContainsKey(url)) return null;
            if (!SizeOk(size)) return null;
            if (P.HideDuplicates && !P.RelativeSubfolders && size >= 0 && !_dupKeys.Add(name + "|" + size)) return null;
            GrabberFile f = new GrabberFile();
            f.Url = url; f.Name = name; f.Size = size; f.LinkText = text; f.Referer = referer; f.IsPage = isPage;
            f.Type = GrabberHtml.TypeLabel(name); f.Checked = true;
            P.Files.Add(f); _knownFiles[url] = f;
            if (P.DownloadAll && !_downloadStop) ThreadPool.QueueUserWorkItem(delegate { try { StartDownloading(new List<GrabberFile> { f }); } catch { } });
            return f;
        }

        SemaphoreSlim _probeSlots;

        void Probe(string url, string name, string text, string referer)
        {
            long size = -1; string realName = name; bool notFound = false;
            lock (_lock) if (_probeSlots == null) _probeSlots = new SemaphoreSlim(Math.Max(1, Math.Min(10, P.ExploreAtOnce)));
            _probeSlots.Wait();
            try
            {
                HttpWebRequest r;
                string headUrl = url;
                using (HttpWebResponse resp = Fetch(ref headUrl, referer, "HEAD", false, out r))
                {
                    size = resp.ContentLength;
                    string cd = Util.FileNameFromContentDisposition(resp.Headers["Content-Disposition"]);
                    if (!string.IsNullOrEmpty(cd)) realName = Util.SanitizeFileName(cd);
                }
            }
            catch (WebException wex)
            {
                HttpWebResponse h = wex.Response as HttpWebResponse;
                if (h != null && ((int)h.StatusCode == 404 || (int)h.StatusCode == 410)) notFound = true;
                // HEAD no permitido: se intenta con el primer byte.
                else
                    try
                    {
                        HttpWebRequest r2;
                        string getUrl = url;
                        using (HttpWebResponse resp = Fetch(ref getUrl, referer, null, true, out r2))
                        {
                            long a, b, t;
                            if (DownloadEngine.ParseContentRange(resp.Headers["Content-Range"], out a, out b, out t)) size = t;
                        }
                    }
                    catch (Exception) { } // el catch de abajo no cubre lo que salga de aquí: sin esto se cerraría IDMClone
            }
            catch (Exception) { }
            finally { _probeSlots.Release(); }
            lock (_lock)
            {
                _probing.Remove(url);
                Examined++;
                if (realName != name && !FileMatches(url, realName)) return;
                GrabberFile f = AddFileLocked(url, realName, size, text, referer, false);
                if (f != null && notFound) { f.Status = "No Encontrado"; f.Checked = false; }
            }
            Raise();
        }

        // --- Descargar ---
        readonly Queue<GrabberFile> _queue = new Queue<GrabberFile>();
        readonly Dictionary<DownloadItem, GrabberFile> _running = new Dictionary<DownloadItem, GrabberFile>();

        DownloadManager Downloader
        {
            get
            {
                if (_dl == null)
                {
                    // Configuración propia: el Grabber hace 2 intentos y no deja rastro en la lista principal.
                    AppConfig c = AppConfig.NewSerializer().Deserialize<AppConfig>(AppConfig.NewSerializer().Serialize(_cfg));
                    c.RetryCount = 2; c.UseTempFolder = false; c.ShowFileInfoDialog = false; c.ShowCompleteDialog = false;
                    _dl = new DownloadManager(c);
                    _dl.Persist = false;
                    _dl.ItemFinished += OnDownloadFinished;
                }
                return _dl;
            }
        }

        // Descarga los archivos marcados (null = todos los marcados que faltan).
        public void StartDownloading(List<GrabberFile> files)
        {
            _downloadStop = false;
            lock (_lock)
            {
                if (files == null) files = P.Files.FindAll(delegate(GrabberFile f) { return f.Checked && f.Status != "Completo"; });
                foreach (GrabberFile f in files) EnqueueDownload(f);
            }
            Pump();
        }

        void EnqueueDownload(GrabberFile f)
        {
            if (f.Status == "Completo" || f.Status == "Descargando" || _queue.Contains(f)) return;
            if (f.Status == "No Encontrado") return;
            f.Status = "En cola";
            _queue.Enqueue(f);
        }

        public void StopDownloading()
        {
            _downloadStop = true;
            List<DownloadItem> items;
            lock (_lock)
            {
                while (_queue.Count > 0) { GrabberFile f = _queue.Dequeue(); if (f.Status == "En cola") f.Status = "Detenido"; }
                items = new List<DownloadItem>(_running.Keys);
            }
            foreach (DownloadItem it in items) Downloader.StopItem(it);
            Raise();
        }

        int _starting; // sacados de la cola y aún no en _running (otro Pump no debe dar por terminado)

        void Pump()
        {
            List<KeyValuePair<GrabberFile, AddOptions>> start = new List<KeyValuePair<GrabberFile, AddOptions>>();
            lock (_lock)
            {
                while (!_downloadStop && _running.Count + _starting + start.Count < Math.Max(1, P.DownloadAtOnce) && _queue.Count > 0)
                {
                    GrabberFile f = _queue.Dequeue();
                    if (f.Status != "En cola") continue;
                    f.Status = "Descargando";
                    start.Add(new KeyValuePair<GrabberFile, AddOptions>(f, OptionsFor(f)));
                }
                _starting += start.Count;
                _downloading = _running.Count + _starting + _queue.Count;
            }
            foreach (KeyValuePair<GrabberFile, AddOptions> kv in start)
            {
                try
                {
                    if (P.Overwrite) { string target = Path.Combine(kv.Value.Folder, kv.Value.FileName); if (File.Exists(target)) File.Delete(target); }
                    DownloadItem it = Downloader.Add(kv.Value);
                    lock (_lock) { _running[it] = kv.Key; kv.Key.SaveTo = it.FilePath; _starting--; }
                    Downloader.StartItem(it);
                }
                catch (Exception) { lock (_lock) { kv.Key.Status = "Error"; _starting--; } }
            }
            lock (_lock) _downloading = _running.Count + _starting + _queue.Count;
            Raise();
            if (_downloading == 0) FinishDownloads();
        }

        bool _finishing;

        void FinishDownloads()
        {
            lock (_lock) { if (_finishing) return; _finishing = true; }
            try
            {
                if (P.ConvertLinks) ConvertLinks();
                Action h = DownloadFinished; if (h != null) h();
            }
            finally { lock (_lock) _finishing = false; }
        }

        void OnDownloadFinished(DownloadItem it)
        {
            lock (_lock)
            {
                GrabberFile f;
                if (!_running.TryGetValue(it, out f)) return;
                _running.Remove(it);
                f.SaveTo = it.FilePath;
                if (it.Status == DownloadStatus.Complete) { f.Status = "Completo"; f.Size = it.Size; }
                else if (it.Status == DownloadStatus.Paused) f.Status = "Detenido";
                else f.Status = it.Error != null && (it.Error.Contains("404") || it.Error.Contains("410")) ? "No Encontrado" : "Error";
            }
            Pump();
        }

        // Carpeta de destino según el paso 2.
        public string FolderFor(GrabberFile f)
        {
            string baseDir;
            if (P.SaveMode == 0) baseDir = _cfg.FolderFor(f.Name);
            else if (P.SaveMode == 1) { Category c = _cfg.FindCategory(P.SaveCategory); baseDir = c != null ? c.Folder : _cfg.DefaultFolder; }
            else baseDir = string.IsNullOrEmpty(P.SaveFolder) ? _cfg.DefaultFolder : P.SaveFolder;
            if (P.SaveMode == 2 && P.RelativeSubfolders)
            {
                Uri u = new Uri(f.Url);
                string dir = Uri.UnescapeDataString(u.AbsolutePath);
                dir = dir.Substring(0, dir.LastIndexOf('/') + 1);
                // En el sitio: carpetas desde la raíz; de otro sitio: dentro de una carpeta con su nombre.
                string path = baseDir;
                if (!OnSite(f.Url)) path = Path.Combine(path, Util.SanitizeFileName(u.Host));
                foreach (string seg in dir.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string s = Util.SanitizeFileName(seg);
                    if (s != null && s.Length > 50) s = s.Substring(0, 50).TrimEnd(' ', '.'); // rutas de Windows de menos de 260
                    if (!string.IsNullOrEmpty(s) && s != "." && s != "..") path = Path.Combine(path, s);
                }
                return path;
            }
            return baseDir;
        }

        AddOptions OptionsFor(GrabberFile f)
        {
            AddOptions o = new AddOptions();
            o.Url = f.Url; o.Folder = FolderFor(f); o.FileName = f.Name; o.StartNow = false; o.Transient = true;
            o.Referrer = f.Referer; o.Cookies = CookiesFor(f.Url);
            o.Queue = DownloadItem.NoQueue;
            return o;
        }

        // Añade los archivos marcados a la lista principal de IDMClone y a la cola de descarga.
        public int AddCheckedTo(DownloadManager main)
        {
            int n = 0;
            List<GrabberFile> l;
            lock (_lock) l = P.Files.FindAll(delegate(GrabberFile f) { return f.Checked && f.Status != "Completo" && f.Status != "No Encontrado"; });
            main.BeginBatch();
            try
            {
                foreach (GrabberFile f in l)
                {
                    if (main.FindDuplicate(f.Url, null) != null) continue;
                    AddOptions o = OptionsFor(f);
                    o.Transient = false; o.Queue = DownloadItem.MainQueue;
                    if (P.LinkTextAsDescription && !string.IsNullOrEmpty(f.LinkText) && f.LinkText != "<página de inicio>") o.Description = f.LinkText;
                    main.Add(o);
                    n++;
                }
            }
            finally { main.EndBatch(); }
            return n;
        }

        // --- Convertir enlaces para navegar sin conexión ---
        static readonly Regex LinkAttrRe = new Regex(@"(\s(?:href|src|background|poster|data-src)\s*=\s*)(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex CssUrlAttrRe = new Regex(@"(url\(\s*)(['""]?)([^'"")]+?)(\2\s*\))", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public int ConvertLinks()
        {
            Dictionary<string, string> local = new Dictionary<string, string>(StringComparer.Ordinal);
            List<GrabberFile> pages = new List<GrabberFile>();
            lock (_lock)
                foreach (GrabberFile f in P.Files)
                    if (f.Status == "Completo" && !string.IsNullOrEmpty(f.SaveTo) && File.Exists(f.SaveTo))
                    {
                        local[f.Url] = f.SaveTo;
                        string ext = Path.GetExtension(f.SaveTo).ToLowerInvariant();
                        if (f.IsPage || ext == ".htm" || ext == ".html" || ext == ".css") pages.Add(f);
                    }
            // Las direcciones que redirigen a una página bajada apuntan al mismo archivo.
            lock (_lock)
                foreach (GrabberPage g in P.Pages)
                {
                    string target;
                    if (g.FinalUrl != null && !local.ContainsKey(g.Url) && local.TryGetValue(g.FinalUrl, out target)) local[g.Url] = target;
                }
            int converted = 0;
            foreach (GrabberFile pg in pages)
            {
                try
                {
                    byte[] raw = File.ReadAllBytes(pg.SaveTo);
                    Encoding enc;
                    try { new UTF8Encoding(false, true).GetString(raw); enc = new UTF8Encoding(false); }
                    catch (DecoderFallbackException) { enc = Encoding.GetEncoding(1252); }
                    string text = enc.GetString(raw);
                    string dir = Path.GetDirectoryName(pg.SaveTo);
                    string baseUrl = pg.Url;
                    Match b = Regex.Match(text, @"<base\b[^>]*href\s*=\s*[""']?([^""'\s>]+)", RegexOptions.IgnoreCase);
                    if (b.Success && GrabberHtml.Resolve(pg.Url, b.Groups[1].Value) != null) baseUrl = GrabberHtml.Resolve(pg.Url, b.Groups[1].Value);
                    text = LinkAttrRe.Replace(text, delegate(Match m)
                    {
                        string q = m.Groups[2].Value, inner = q, open = "", close = "";
                        if (q.StartsWith("\"") || q.StartsWith("'")) { open = q.Substring(0, 1); close = open; inner = q.Substring(1, q.Length - 2); }
                        string nv = Rewrite(inner, baseUrl, dir, local);
                        if (nv == null) return m.Value;
                        if (open.Length == 0) { open = "\""; close = "\""; }
                        return m.Groups[1].Value + open + WebUtility.HtmlEncode(nv).Replace("&#39;", "'") + close;
                    });
                    text = CssUrlAttrRe.Replace(text, delegate(Match m)
                    {
                        string nv = Rewrite(m.Groups[3].Value, baseUrl, dir, local);
                        return nv == null ? m.Value : m.Groups[1].Value + m.Groups[2].Value + nv + m.Groups[4].Value;
                    });
                    File.WriteAllBytes(pg.SaveTo, enc.GetBytes(text));
                    converted++;
                }
                catch (Exception) { }
            }
            return converted;
        }

        static string Rewrite(string value, string baseUrl, string dir, Dictionary<string, string> local)
        {
            string v = WebUtility.HtmlDecode(value).Trim();
            string frag = "";
            int h = v.IndexOf('#');
            if (h >= 0) { frag = v.Substring(h); v = v.Substring(0, h); }
            if (v.Length == 0) return null;
            string abs = GrabberHtml.Resolve(baseUrl, v);
            if (abs == null) return null;
            string target;
            if (local.TryGetValue(abs, out target)) return LocalHref(RelativePath(dir, target)) + frag;
            return abs + frag;
        }

        // Ruta relativa como enlace: "/" y cada trozo codificado (espacios, acentos...).
        static string LocalHref(string rel)
        {
            string[] parts = rel.Replace('\\', '/').Split('/');
            for (int i = 0; i < parts.Length; i++)
                if (parts[i] != ".." && parts[i] != ".") parts[i] = Uri.EscapeDataString(parts[i]);
            return string.Join("/", parts);
        }

        public static string RelativePath(string fromDir, string toFile)
        {
            Uri from = new Uri(Path.GetFullPath(fromDir).TrimEnd('\\') + "\\");
            Uri to = new Uri(Path.GetFullPath(toFile));
            return Uri.UnescapeDataString(from.MakeRelativeUri(to).ToString());
        }

        // --- Estadísticas ("Estadísticas de IDM Grabber") ---
        public void Counts(out int pagesDone, out int pagesTotal, out int files, out int matched, out int downloaded)
        {
            lock (_lock)
            {
                pagesTotal = P.Pages.Count; pagesDone = 0; files = P.Files.Count; matched = 0; downloaded = 0;
                foreach (GrabberPage g in P.Pages) if (g.Status != null && g.Status != "Explorando") pagesDone++;
                foreach (GrabberFile f in P.Files) { if (f.Checked) matched++; if (f.Status == "Completo") downloaded++; }
            }
        }

        public List<GrabberFile> FilesSnapshot() { lock (_lock) return new List<GrabberFile>(P.Files); }

        public void Close()
        {
            StopExploring();
            if (_dl != null) { StopDownloading(); _dl.StopAllAndWait(5000); }
        }
    }
}
