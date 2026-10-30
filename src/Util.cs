using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace IDMClone
{
    public static class Util
    {
        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }

        // Límites de IDM 6.43 (medidos en un Sandbox con nombres de 206, 244 y 304 caracteres): el nombre se acorta, sin tocar
        // la extensión, a 211 caracteres como mucho y a lo justo para que la ruta completa no pase de 254.
        public const int MaxFileNameLength = 211, MaxPathLength = 254;

        // El nombre que cabe en esa carpeta. Si ni acortándolo cabe (carpeta larguísima), se deja como está.
        public static string FitFileName(string dir, string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int dirLen = string.IsNullOrEmpty(dir) ? 0 : dir.TrimEnd('\\', '/').Length + 1;
            int limit = Math.Min(MaxFileNameLength, MaxPathLength - dirLen);
            if (name.Length <= limit) return name;
            string ext = "";
            int dot = name.LastIndexOf('.');
            if (dot > 0 && name.Length - dot <= 16) ext = name.Substring(dot); // ".zip", ".tar.gz" -> ".gz"
            string stem = Shorten(name.Substring(0, name.Length - ext.Length), limit - ext.Length);
            return stem.Length == 0 ? name : stem + ext;
        }

        // La ruta con el nombre acortado como IDM (ver FitFileName).
        public static string FitPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try
            {
                string dir = Path.GetDirectoryName(path), name = Path.GetFileName(path);
                string fit = FitFileName(dir, name);
                return fit == name ? path : Path.Combine(dir, fit);
            }
            catch (Exception) { return path; }
        }

        // Los primeros 'max' caracteres, sin partir un carácter doble (emoji...) ni acabar en punto o espacio (Windows los quita).
        public static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
            if (max <= 0) return "";
            int n = max;
            if (char.IsHighSurrogate(s[n - 1])) n--;
            return s.Substring(0, n).TrimEnd(' ', '.');
        }

        // Nombre corto para el archivo de trabajo en la carpeta temporal (IDM tampoco usa ahí el nombre entero): así una
        // descarga con un nombre muy largo no se pasa del límite de ruta de Windows antes de llegar a su destino.
        public static string WorkFileName(string name)
        {
            name = SanitizeFileName(name);
            if (string.IsNullOrEmpty(name)) return "descarga";
            string ext = Path.GetExtension(name);
            if (ext.Length > 16) ext = "";
            string stem = Shorten(name.Substring(0, name.Length - ext.Length), 40);
            return (stem.Length == 0 ? "descarga" : stem) + ext;
        }

        // Deduce un nombre de archivo a partir de la URL.
        public static string FileNameFromUrl(string url)
        {
            try
            {
                Uri u = new Uri(url);
                string name = Path.GetFileName(Uri.UnescapeDataString(u.AbsolutePath));
                name = SanitizeFileName(name);
                if (string.IsNullOrEmpty(name)) name = "download";
                return name;
            }
            catch { return "download"; }
        }

        // Devuelve una ruta que no existe (añade _1, _2, ...).
        public static string UniquePath(string path)
        {
            return UniquePath(path, null);
        }

        // Igual, pero además evita las rutas que 'taken' diga que ya están reservadas
        // (por ejemplo, las de otras descargas de la lista que aún no han creado su archivo).
        public static string UniquePath(string path, Func<string, bool> taken)
        {
            if (!File.Exists(path) && (taken == null || !taken(path))) return path;
            string dir = Path.GetDirectoryName(path);
            string baseName = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            // Subtítulos "Vídeo.es.vtt": el número va antes del idioma ("Vídeo_1.es.vtt"), como el del vídeo ("Vídeo_1.mp4"),
            // para que el reproductor los siga emparejando.
            System.Text.RegularExpressions.Match lang = System.Text.RegularExpressions.Regex.Match(baseName, @"^(.+)(\.[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})?)$");
            if (lang.Success && (ext.Equals(".vtt", StringComparison.OrdinalIgnoreCase) || ext.Equals(".srt", StringComparison.OrdinalIgnoreCase) ||
                                 ext.Equals(".ttml", StringComparison.OrdinalIgnoreCase)))
            {
                baseName = lang.Groups[1].Value;
                ext = lang.Groups[2].Value + ext;
            }
            for (int i = 1; i < 100000; i++)
            {
                string cand = Path.Combine(dir, string.Format("{0}_{1}{2}", baseName, i, ext));
                if (!File.Exists(cand) && (taken == null || !taken(cand))) return cand;
            }
            return path;
        }

        // Nombre de archivo de una cabecera Content-Disposition (RFC 6266 + RFC 5987).
        // Prefiere filename*=charset'lang'valor-con-%XX; si no, filename= (entre comillas o no).
        public static string FileNameFromContentDisposition(string cd)
        {
            if (string.IsNullOrEmpty(cd)) return null;
            // Algunos servidores omiten el tipo ("filename=a.zip" a secas).
            int firstSemi = IndexOfUnquoted(cd, ';', 0), firstEq = cd.IndexOf('=');
            if (firstEq >= 0 && (firstSemi < 0 || firstEq < firstSemi)) cd = "attachment; " + cd;
            string plain = null, ext = null;
            int i = 0;
            while (i < cd.Length)
            {
                // Saltar hasta el siguiente parámetro.
                int semi = IndexOfUnquoted(cd, ';', i);
                if (semi < 0) break;
                i = semi + 1;
                int eq = cd.IndexOf('=', i);
                if (eq < 0) break;
                string key = cd.Substring(i, eq - i).Trim().ToLowerInvariant();
                int valStart = eq + 1;
                while (valStart < cd.Length && cd[valStart] == ' ') valStart++;
                string val;
                int next;
                if (valStart < cd.Length && cd[valStart] == '"')
                {
                    StringBuilder sb = new StringBuilder();
                    int k = valStart + 1;
                    for (; k < cd.Length; k++)
                    {
                        char c = cd[k];
                        if (c == '\\' && k + 1 < cd.Length) { sb.Append(cd[++k]); continue; }
                        if (c == '"') break;
                        sb.Append(c);
                    }
                    val = sb.ToString();
                    next = k + 1;
                }
                else
                {
                    int end = cd.IndexOf(';', valStart);
                    if (end < 0) end = cd.Length;
                    val = cd.Substring(valStart, end - valStart).Trim();
                    next = end;
                }
                if (key == "filename*") ext = DecodeExtValue(val);
                else if (key == "filename") plain = DecodeMaybePercent(val);
                i = next;
            }
            string name = !string.IsNullOrEmpty(ext) ? ext : plain;
            if (string.IsNullOrEmpty(name)) return null;
            // Quitar rutas que algunos servidores incluyen ("C:\x\a.zip", "../a.zip").
            int slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
            if (slash >= 0) name = name.Substring(slash + 1);
            name = SanitizeFileName(name);
            return string.IsNullOrEmpty(name) ? null : name;
        }

        static int IndexOfUnquoted(string s, char ch, int from)
        {
            bool q = false;
            for (int k = from; k < s.Length; k++)
            {
                if (s[k] == '"' && (k == 0 || s[k - 1] != '\\')) q = !q;
                else if (s[k] == ch && !q) return k;
            }
            return -1;
        }

        // RFC 5987: charset'idioma'valor%XX
        static string DecodeExtValue(string v)
        {
            int a = v.IndexOf('\'');
            int b = a >= 0 ? v.IndexOf('\'', a + 1) : -1;
            if (a < 0 || b < 0) return DecodeMaybePercent(v);
            string charset = v.Substring(0, a).Trim();
            string data = v.Substring(b + 1);
            Encoding enc;
            try { enc = string.IsNullOrEmpty(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
            catch { enc = Encoding.UTF8; }
            return PercentDecode(data, enc);
        }

        static string DecodeMaybePercent(string v)
        {
            if (v.IndexOf('%') >= 0)
            {
                string d = PercentDecode(v, Encoding.UTF8);
                if (d.IndexOf('\uFFFD') < 0) return d;
            }
            // Algunos servidores mandan UTF-8 en bruto que .NET lee como Latin-1.
            try
            {
                bool high = false;
                foreach (char c in v) if (c > 127 && c < 256) { high = true; break; }
                if (high)
                {
                    string u = Encoding.UTF8.GetString(Encoding.GetEncoding(28591).GetBytes(v));
                    if (u.IndexOf('\uFFFD') < 0) return u;
                }
            }
            catch { }
            return v;
        }

        static string PercentDecode(string s, Encoding enc)
        {
            List<byte> bytes = new List<byte>();
            for (int k = 0; k < s.Length; k++)
            {
                char c = s[k];
                if (c == '%' && k + 2 < s.Length && IsHex(s[k + 1]) && IsHex(s[k + 2]))
                {
                    bytes.Add(Convert.ToByte(s.Substring(k + 1, 2), 16));
                    k += 2;
                }
                else bytes.AddRange(enc.GetBytes(c.ToString()));
            }
            return enc.GetString(bytes.ToArray());
        }

        static bool IsHex(char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'); }

        // Nombres "genéricos" que da una URL dinámica (descarga.php?id=5): mejor el del servidor.
        public static bool IsGenericName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return true;
            string ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
            if (ext.Length == 0) return true;
            if (Path.GetFileNameWithoutExtension(fileName).Equals("download", StringComparison.OrdinalIgnoreCase)) return true;
            switch (ext)
            {
                case "php": case "asp": case "aspx": case "jsp": case "cgi": case "pl": case "do": case "action": case "ashx":
                    return true;
            }
            return false;
        }

        static readonly System.Text.RegularExpressions.Regex UrlRegex =
            new System.Text.RegularExpressions.Regex(@"(?:https?|ftps?)://[^\s""'<>]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Todas las URL de un texto, sin repetir (Archivo -> Importar, como IDM).
        // "Añadir descarga por lotes" de IDM: sustituye el * de la dirección por números (con ceros a la
        // izquierda hasta 'width' cifras) o por letras, desde 'from' hasta 'to' (ambos incluidos).
        public static List<string> ExpandBatch(string pattern, string from, string to, int width)
        {
            List<string> r = new List<string>();
            if (string.IsNullOrEmpty(pattern) || pattern.IndexOf('*') < 0) return r;
            int a, b;
            if (int.TryParse(from, out a) && int.TryParse(to, out b))
            {
                if (b < a) { int t = a; a = b; b = t; }
                // En long: con int, "b - a" se desborda (-2000000000..2000000000) y el bucle no acaba si b es int.MaxValue.
                long last = Math.Min((long)b, (long)a + 100000);
                for (long i = a; i <= last; i++) r.Add(pattern.Replace("*", i.ToString(new string('0', Math.Max(1, width)))));
                return r;
            }
            if (from != null && to != null && from.Length == 1 && to.Length == 1 && char.IsLetter(from[0]) && char.IsLetter(to[0]))
            {
                char x = from[0], y = to[0];
                if (y < x) { char t = x; x = y; y = t; }
                for (char c = x; c <= y; c++) r.Add(pattern.Replace("*", c.ToString()));
            }
            return r;
        }

        public static List<string> ExtractUrls(string text)
        {
            List<string> r = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return r;
            foreach (System.Text.RegularExpressions.Match m in UrlRegex.Matches(text))
            {
                string u = m.Value.TrimEnd('.', ',', ';', ')', ']', '>');
                if (seen.Add(u)) r.Add(u);
            }
            return r;
        }

        // ¿El servidor coincide con el patrón? "ejemplo.com" vale también para sus subdominios;
        // admite comodines ("*.cdn.net", "files?.ejemplo.com").
        public static bool HostMatches(string host, string pattern)
        {
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(pattern)) return false;
            string p = pattern.Trim().ToLowerInvariant();
            int scheme = p.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) p = p.Substring(scheme + 3);
            int slash = p.IndexOf('/'); if (slash >= 0) p = p.Substring(0, slash);
            int colon = p.IndexOf(':'); if (colon >= 0) p = p.Substring(0, colon);
            host = host.ToLowerInvariant();
            if (p.IndexOf('*') < 0 && p.IndexOf('?') < 0) return host == p || host.EndsWith("." + p, StringComparison.Ordinal);
            return WildcardMatch(host, p);
        }

        // Patrón "servidor/ruta*" (sin esquema). Sin "/", solo el servidor.
        public static bool UrlMatchesPattern(Uri uri, string pattern)
        {
            if (uri == null || string.IsNullOrEmpty(pattern)) return false;
            string p = pattern.Trim().ToLowerInvariant();
            int scheme = p.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) p = p.Substring(scheme + 3);
            int slash = p.IndexOf('/');
            if (slash < 0) return HostMatches(uri.Host, p);
            if (!HostMatches(uri.Host, p.Substring(0, slash))) return false;
            return WildcardMatch(uri.AbsolutePath.ToLowerInvariant(), p.Substring(slash));
        }

        public static bool WildcardMatch(string text, string pattern)
        {
            string re = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(text, re, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        public static string NewId()
        {
            return DateTime.UtcNow.Ticks.ToString("x") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        // .NET Framework no sigue solo las redirecciones 308 (sí 301, 302, 303 y 307): destino de una 308, o null.
        public static string RedirectTarget(System.Net.WebException wex)
        {
            System.Net.HttpWebResponse r = wex != null ? wex.Response as System.Net.HttpWebResponse : null;
            if (r == null || (int)r.StatusCode != 308) return null;
            string loc = r.Headers["Location"];
            if (string.IsNullOrEmpty(loc)) return null;
            try
            {
                Uri u = new Uri(r.ResponseUri, loc);
                return u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps ? u.AbsoluteUri : null;
            }
            catch { return null; }
        }

        public static bool LooksLikeUrl(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase);
        }

        // --- Escritura y lectura seguras de los JSON de datos ---

        static readonly Dictionary<string, object> _fileLocks = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        static object LockFor(string path)
        {
            lock (_fileLocks)
            {
                object o;
                if (!_fileLocks.TryGetValue(path, out o)) { o = new object(); _fileLocks[path] = o; }
                return o;
            }
        }

        // Escribe en <ruta>.tmp, lo vuelca a disco y lo cambia por el original dejando
        // la versión anterior en <ruta>.bak. Si se corta a mitad, el original queda intacto.
        public static void WriteAllTextAtomic(string path, string text)
        {
            lock (LockFor(path))
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = path + ".tmp";
                string bak = path + ".bak";
                byte[] data = new UTF8Encoding(false).GetBytes(text);
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(data, 0, data.Length);
                    fs.Flush(true);
                }
                if (File.Exists(path))
                {
                    try { File.Replace(tmp, path, bak, true); return; }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    // Respaldo por si File.Replace no está disponible en ese volumen.
                    File.Copy(path, bak, true);
                    File.Delete(path);
                }
                File.Move(tmp, path);
            }
        }

        // Lee un JSON con 'parse'. Si el principal está dañado, prueba con <ruta>.bak.
        // Si los dos fallan, aparta el dañado como <ruta>.corrupt-<fecha> (nunca se sobrescribe)
        // y devuelve false con un mensaje en 'warning'.
        public static bool TryLoadJson<T>(string path, Func<string, T> parse, out T result, out string warning) where T : class
        {
            result = null; warning = null;
            lock (LockFor(path))
            {
                bool mainExists = File.Exists(path);
                if (mainExists)
                {
                    T v = TryParseFile(path, parse);
                    if (v != null) { result = v; return true; }
                }
                string bak = path + ".bak";
                if (File.Exists(bak))
                {
                    T v = TryParseFile(bak, parse);
                    if (v != null)
                    {
                        result = v;
                        if (mainExists) warning = Path.GetFileName(path) + " estaba dañado; se recuperó la copia anterior.";
                        return true;
                    }
                }
                if (!mainExists) return false;
                string corrupt = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Move(path, corrupt); } catch { }
                warning = Path.GetFileName(path) + " estaba dañado y no había copia válida. Se apartó como " + Path.GetFileName(corrupt) + ".";
                return false;
            }
        }

        static T TryParseFile<T>(string file, Func<string, T> parse) where T : class
        {
            try
            {
                string json = File.ReadAllText(file, Encoding.UTF8);
                if (string.IsNullOrEmpty(json) || json.Trim().Length == 0) return null;
                return parse(json);
            }
            catch { return null; }
        }

        public static void DeleteWithBackups(string path)
        {
            lock (LockFor(path))
            {
                foreach (string p in new string[] { path, path + ".bak", path + ".tmp" })
                    try { if (File.Exists(p)) File.Delete(p); } catch { }
            }
        }

        // --- Cifrado de datos sensibles (cookies) con DPAPI: solo el usuario actual los descifra ---
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(enc);
        }

        public static string Unprotect(string b64)
        {
            if (string.IsNullOrEmpty(b64)) return null;
            try
            {
                byte[] dec = ProtectedData.Unprotect(Convert.FromBase64String(b64), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }
            catch { return null; }
        }
    }
}
