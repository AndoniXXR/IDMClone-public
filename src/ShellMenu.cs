using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace IDMClone
{
    // Menú contextual del Explorador de Windows (IDM lo hace con su extensión del shell, PDF §4.2).
    // Aquí son verbos estáticos por usuario en HKCU\Software\Classes, sin DLL ni administrador:
    //   - accesos directos de Internet (.url): "Descargar con IDMClone"      -> IDMClone.exe /urlfile "archivo"
    //   - .txt, .htm y .html: "Descargar los enlaces con IDMClone"          -> IDMClone.exe /linksfile "archivo"
    // Lo registran el instalador y Opciones > General; al quitarlo solo se borra lo que apunta a nuestro IDMClone.exe.
    public static class ShellMenu
    {
        public const string UrlVerb = "IDMClone.Descargar";
        public const string LinksVerb = "IDMClone.Enlaces";
        public const string UrlSwitch = "/urlfile";
        public const string LinksSwitch = "/linksfile";
        public const string UrlText = "Descargar con IDMClone";
        public const string LinksText = "Descargar los enlaces con IDMClone";

        // Raíz de las clases del usuario. Las pruebas (y el instalador con su RegRoot) la cambian por una clave propia.
        public static string ClassesKey = @"Software\Classes";

        // "InternetShortcut" es la clase de los .url; los de texto van por extensión (SystemFileAssociations)
        // para que salga con cualquier programa predeterminado (Bloc de notas, Chrome, Edge...).
        public static readonly string[] LinkExtensions = { ".txt", ".htm", ".html" };
        // Windows 10 asocia los .url a "IE.AssocFile.URL" (UserChoice): el menú sale de esa clase, no de InternetShortcut
        // ni de SystemFileAssociations (comprobado en el Explorador de verdad). Se registra en las dos.
        static readonly string[] UrlClasses = { "InternetShortcut", "IE.AssocFile.URL" };

        static List<string> VerbKeys(string classes, bool links)
        {
            List<string> r = new List<string>();
            if (!links) foreach (string c in UrlClasses) r.Add(classes + @"\" + c + @"\shell\" + UrlVerb);
            else foreach (string ext in LinkExtensions) r.Add(classes + @"\SystemFileAssociations\" + ext + @"\shell\" + LinksVerb);
            return r;
        }

        static List<string> AllVerbKeys(string classes)
        {
            List<string> r = VerbKeys(classes, false);
            r.AddRange(VerbKeys(classes, true));
            return r;
        }

        // Lo que tenían ya las versiones anteriores (sin IE.AssocFile.URL): basta para darlo por puesto, y Relabel (al
        // arrancar) añade lo que falte.
        static List<string> CoreVerbKeys(string classes)
        {
            List<string> r = new List<string>();
            r.Add(classes + @"\" + UrlClasses[0] + @"\shell\" + UrlVerb);
            r.AddRange(VerbKeys(classes, true));
            return r;
        }

        public static string Command(string exe, bool links)
        {
            return "\"" + exe + "\" " + (links ? LinksSwitch : UrlSwitch) + " \"%1\"";
        }

        public static bool IsRegistered(string exe) { return IsRegistered(ClassesKey, exe); }
        // El texto del menú se escribe en el idioma de la interfaz al registrarlo: al cambiar de idioma (y al arrancar,
        // por si el instalador lo dejó en el de Windows) se vuelve a escribir si está puesto.
        public static void Relabel(string exe)
        {
            try { if (IsRegistered(exe)) Register(ClassesKey, exe); } catch { }
        }

        public static void Set(bool enable, string exe)
        {
            if (enable) Register(ClassesKey, exe); else Unregister(ClassesKey, exe);
            MarkOff(UninstallKey, !enable);
        }

        // "Quitado por el usuario" (Opciones o /noexplorermenu), en la clave de desinstalación de IDMClone: la actualización
        // (/update) no lo vuelve a poner; quien viene de una versión sin este menú (sin la marca) sí lo recibe.
        public static string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\IDMClone";
        public const string OffValue = "ExplorerMenuOff";

        public static void MarkOff(string uninstallKey, bool off)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(uninstallKey, true))
                {
                    if (k == null) return; // sin instalar (copia suelta): nada que recordar
                    if (off) k.SetValue(OffValue, 1, RegistryValueKind.DWord); else k.DeleteValue(OffValue, false);
                }
            }
            catch { }
        }

        public static bool IsMarkedOff(string uninstallKey)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(uninstallKey))
                {
                    object v = k != null ? k.GetValue(OffValue) : null;
                    return v is int && (int)v != 0;
                }
            }
            catch { return false; }
        }

        // ¿Están todos los verbos y apuntan a este IDMClone.exe?
        public static bool IsRegistered(string classes, string exe)
        {
            foreach (string k in CoreVerbKeys(classes))
                if (!SameExe(CommandExe(ReadCommand(k)), exe)) return false;
            return true;
        }

        // Textos en el idioma de la interfaz (el instalador los deja en español).
        public static void Register(string classes, string exe)
        {
            Register(classes, exe, Lang.T(UrlText), Lang.T(LinksText));
        }

        public static void Register(string classes, string exe, string urlText, string linksText)
        {
            foreach (string k in VerbKeys(classes, false)) Write(k, urlText, exe, Command(exe, false));
            foreach (string k in VerbKeys(classes, true)) Write(k, linksText, exe, Command(exe, true));
        }

        static void Write(string key, string text, string exe, string command)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(key))
            {
                k.SetValue("", text);
                k.SetValue("Icon", exe + ",0");
            }
            using (RegistryKey c = Registry.CurrentUser.CreateSubKey(key + @"\command")) c.SetValue("", command);
        }

        // Quita los verbos que lanzan este IDMClone.exe (los de otra copia se respetan).
        public static void Unregister(string classes, string exe)
        {
            foreach (string k in AllVerbKeys(classes))
                if (SameExe(CommandExe(ReadCommand(k)), exe)) DeleteVerb(k);
        }

        // Desinstalar: quita los verbos que lanzan un programa de esa carpeta.
        public static void UnregisterFolder(string classes, string dir)
        {
            string root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            foreach (string k in AllVerbKeys(classes))
            {
                string exe = CommandExe(ReadCommand(k));
                if (exe != null && exe.StartsWith(root, StringComparison.OrdinalIgnoreCase)) DeleteVerb(k);
            }
        }

        static void DeleteVerb(string key)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(key, false); } catch { }
            // Las claves que se crearon para el verbo ("shell" y ".txt", "InternetShortcut" o "IE.AssocFile.URL") se quitan si
            // se han quedado vacías; nunca SystemFileAssociations ni Classes.
            for (int i = 0; i < 2; i++)
            {
                int cut = key.LastIndexOf('\\');
                if (cut <= 0) return;
                key = key.Substring(0, cut);
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(key))
                    {
                        if (k == null || k.SubKeyCount > 0 || k.ValueCount > 0) return;
                    }
                    Registry.CurrentUser.DeleteSubKey(key, false);
                }
                catch { return; }
            }
        }

        static string ReadCommand(string key)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(key + @"\command"))
                    return k != null ? k.GetValue("") as string : null;
            }
            catch { return null; }
        }

        // Programa de la orden: "C:\x\IDMClone.exe" /urlfile "%1" -> C:\x\IDMClone.exe
        public static string CommandExe(string command)
        {
            if (string.IsNullOrEmpty(command)) return null;
            string c = command.Trim();
            if (c.StartsWith("\""))
            {
                int end = c.IndexOf('"', 1);
                return end > 1 ? c.Substring(1, end - 1) : null;
            }
            int sp = c.IndexOf(' ');
            return sp > 0 ? c.Substring(0, sp) : c;
        }

        static bool SameExe(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        // ------------------------------------------------------------------ archivos

        // Dirección de un acceso directo de Internet (.url): la línea URL= de la sección [InternetShortcut].
        // Windows escribe el archivo en ANSI y, si la dirección lleva caracteres que no caben, la repite en
        // [InternetShortcut.W] en UTF-7: esa manda. Solo vale una dirección web (http, https, ftp); si no, null.
        public static string ShortcutUrl(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string section = null, inSection = null, wide = null, anywhere = null;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2).Trim(); continue; }
                if (!line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) continue;
                string v = line.Substring(4).Trim();
                if (section != null && section.Equals("InternetShortcut", StringComparison.OrdinalIgnoreCase)) { if (inSection == null) inSection = v; }
                else if (section != null && section.Equals("InternetShortcut.W", StringComparison.OrdinalIgnoreCase)) { if (wide == null) wide = FromUtf7(v); }
                else if (anywhere == null) anywhere = v;
            }
            if (Util.LooksLikeUrl(wide)) return wide;
            string url = inSection ?? anywhere;
            return Util.LooksLikeUrl(url) ? url : null;
        }

        static string FromUtf7(string v)
        {
            try { return Encoding.UTF7.GetString(Encoding.ASCII.GetBytes(v)); } catch { return null; }
        }

        public static string ReadShortcutUrl(string file)
        {
            return ShortcutUrl(ReadText(file));
        }

        // Texto de un archivo: con BOM, el que diga; sin BOM, UTF-8 si lo es y si no ANSI (como los escribe Windows).
        public static string ReadText(string file)
        {
            byte[] b = File.ReadAllBytes(file);
            bool bom = (b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF)))
                    || (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF);
            if (bom) using (StreamReader r = new StreamReader(new MemoryStream(b), true)) return r.ReadToEnd();
            try { return new UTF8Encoding(false, true).GetString(b); }
            catch (DecoderFallbackException) { return Encoding.Default.GetString(b); }
        }

        static readonly Regex HrefRegex = new Regex(@"\b(?:href|src)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>""']+))", RegexOptions.IgnoreCase);
        static readonly Regex BaseRegex = new Regex(@"<base\b[^>]*\bhref\s*=\s*[""']?([^""'\s>]+)", RegexOptions.IgnoreCase);
        static readonly Regex SavedFromRegex = new Regex(@"saved from url=\(\d+\)(\S+?)\s*-->", RegexOptions.IgnoreCase);
        static readonly Regex BaseTagRegex = new Regex(@"<base\b[^>]*>", RegexOptions.IgnoreCase);
        static readonly Regex SchemeRegex = new Regex(@"^[a-zA-Z][a-zA-Z0-9+.\-]*:");

        // Enlaces web de un archivo de texto o de una página guardada, sin repetir y en orden.
        // En .htm/.html también los relativos, si la página dice de dónde es (<base href> o "saved from url").
        public static List<string> LinksInText(string text, bool html)
        {
            if (string.IsNullOrEmpty(text)) return new List<string>();
            if (!html) return Util.ExtractUrls(text);
            List<string> r = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Uri baseUri = null;
            Match b = BaseRegex.Match(text);
            if (!b.Success) b = SavedFromRegex.Match(text);
            if (b.Success) Uri.TryCreate(WebUtility.HtmlDecode(b.Groups[1].Value), UriKind.Absolute, out baseUri);
            if (baseUri != null && !Util.LooksLikeUrl(baseUri.AbsoluteUri)) baseUri = null;
            // La dirección base y la de la propia página no son enlaces a descargar.
            text = SavedFromRegex.Replace(BaseTagRegex.Replace(text, " "), " ");
            foreach (Match m in HrefRegex.Matches(text))
            {
                string v = WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).Trim();
                if (v.Length == 0 || v.StartsWith("#")) continue;
                string abs = null;
                if (Util.LooksLikeUrl(v)) abs = v;
                else if (baseUri != null && !SchemeRegex.IsMatch(v)) // mailto:, javascript:, data:... no
                {
                    Uri u;
                    if (Uri.TryCreate(baseUri, v, out u)) abs = u.AbsoluteUri;
                }
                if (abs == null) continue;
                abs = NoFragment(abs);
                if (seen.Add(abs)) r.Add(abs);
            }
            // Y las direcciones escritas como texto en la página (fuera de las etiquetas, que ya se han mirado:
            // así un href con espacios no da además un enlace cortado).
            foreach (string u in Util.ExtractUrls(WebUtility.HtmlDecode(TagRegex.Replace(text, " "))))
            {
                string v = NoFragment(u);
                if (seen.Add(v)) r.Add(v);
            }
            return r;
        }

        static readonly Regex TagRegex = new Regex(@"<[^>]*>");

        // El "#ancla" no cambia lo que se descarga.
        static string NoFragment(string url)
        {
            int hash = url.IndexOf('#');
            return hash > 0 ? url.Substring(0, hash) : url;
        }

        public static List<string> LinksInFile(string file)
        {
            string ext = (Path.GetExtension(file) ?? "").ToLowerInvariant();
            return LinksInText(ReadText(file), ext == ".htm" || ext == ".html");
        }
    }
}
