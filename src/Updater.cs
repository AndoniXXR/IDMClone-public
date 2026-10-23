using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace IDMClone
{
    // Una versión publicada de IDMClone (lo que dice el manifiesto o la versión de GitHub).
    public class UpdateInfo
    {
        public Version Version;        // con sus cuatro partes (1.3 -> 1.3.0.0)
        public string VersionText;     // tal como viene ("v1.3.0")
        public string InstallerUrl;    // IDMClone-Setup.exe de esa versión
        public string Sha256;          // SHA-256 del instalador en hexadecimal (minúsculas), o null
        public string Sha256Url;       // GitHub sin "digest": archivo "<instalador>.sha256" publicado al lado
        public string Notes;           // novedades (texto)
        public string PageUrl;         // página de la versión, si la hay
    }

    public class UpdateException : Exception
    {
        public UpdateException(string message) : base(message) { }
    }

    // Actualizaciones de IDMClone, como las de IDM: Ayuda > "Comprobar actualizaciones..." y una comprobación automática
    // cada semana (en segundo plano y en silencio si falla). El instalador se descarga a %TEMP%, se comprueba su SHA-256 y,
    // si este IDMClone.exe está firmado (Authenticode), que el instalador lo esté por el mismo editor; luego se ejecuta en
    // modo actualización ("/update /D=<carpeta>": reinstala en la misma carpeta sin preguntar y vuelve a abrir IDMClone).
    public static class Updater
    {
        // Origen de las versiones (config.json: "UpdateFeedUrl"; vacío = este). Admite dos formatos:
        //  1) Manifiesto JSON propio, p. ej. publicado en cualquier servidor HTTPS:
        //       { "version": "1.3.0", "url": "https://.../IDMClone-Setup.exe", "sha256": "<64 cifras hex>", "notes": "Novedades..." }
        //     ("url" puede ser relativa al manifiesto; "page" opcional con la página de la versión).
        //  2) La API de GitHub Releases (".../releases/latest"): versión = "tag_name" ("v1.3.0"), notas = "body", instalador =
        //     el asset "IDMClone-Setup*.exe". Su SHA-256 sale del campo "digest" ("sha256:...") que GitHub calcula para cada
        //     asset, o de un asset "<instalador>.sha256", o de una línea "SHA-256: <hex>" en las notas.
        // Por defecto, las versiones del repositorio público AndoniXXR/IDMClone-public (el privado no sirve: leerlo pediría
        // un token, y dentro del .exe lo vería cualquiera). Mientras no tenga versiones publicadas, la API contesta 404 y la
        // comprobación falla (en silencio si es la automática). Cómo publicar una: README, "Firmar y publicar una versión".
        public const string DefaultFeedUrl = "https://api.github.com/repos/AndoniXXR/IDMClone-public/releases/latest";
        public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(7);     // "cada semana", como IDM
        public static readonly TimeSpan RetryAfterError = TimeSpan.FromDays(1);   // tras un fallo, no antes de un día
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\IDMClone"; // el del instalador

        // Para las pruebas: versión "instalada", carpeta instalada y lanzamiento del instalador.
        public static Version CurrentVersionOverride;
        public static Func<string> InstalledDirHook;
        public static Func<ProcessStartInfo, bool> LaunchHook;

        public static Version CurrentVersion
        {
            get { return Normalize(CurrentVersionOverride ?? typeof(Updater).Assembly.GetName().Version); }
        }

        public static string FeedUrl(AppConfig c)
        {
            return c == null || string.IsNullOrEmpty((c.UpdateFeedUrl ?? "").Trim()) ? DefaultFeedUrl : c.UpdateFeedUrl.Trim();
        }

        // "1.3.0" (la revisión solo si no es 0).
        public static string Display(Version v)
        {
            if (v == null) return "?";
            v = Normalize(v);
            return v.Major + "." + v.Minor + "." + v.Build + (v.Revision > 0 ? "." + v.Revision : "");
        }

        // 1.3 -> 1.3.0.0: Version compara las partes que faltan como -1 (1.3 < 1.3.0).
        public static Version Normalize(Version v)
        {
            if (v == null) return null;
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        }

        // "v1.3.0", "1.3", "IDMClone 1.3.0-beta" -> 1.3.0.0. null si no hay versión.
        public static Version ParseVersion(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            Match m = Regex.Match(s, @"([0-9]+)\.([0-9]+)(?:\.([0-9]+))?(?:\.([0-9]+))?");
            if (!m.Success) return null;
            try
            {
                int[] p = new int[4];
                for (int i = 0; i < 4; i++) p[i] = m.Groups[i + 1].Success ? int.Parse(m.Groups[i + 1].Value, CultureInfo.InvariantCulture) : 0;
                return new Version(p[0], p[1], p[2], p[3]);
            }
            catch (OverflowException) { return null; }
        }

        public static bool IsNewer(UpdateInfo u) { return IsNewer(u, CurrentVersion); }

        public static bool IsNewer(UpdateInfo u, Version current)
        {
            return u != null && u.Version != null && Normalize(u.Version).CompareTo(Normalize(current)) > 0;
        }

        // ------------------------------------------------------------------ comprobación semanal

        // ¿Toca comprobar? Activada, sin comprobación correcta en los últimos 7 días y sin un fallo en el último día.
        // Una fecha "del futuro" (el reloj se atrasó) no bloquea: se comprueba.
        public static bool IsCheckDue(AppConfig c, DateTime nowUtc)
        {
            if (c == null || !c.AutoCheckUpdates) return false;
            DateTime? last = ParseUtc(c.LastUpdateCheck), fail = ParseUtc(c.LastUpdateFailure);
            if (fail.HasValue && fail.Value <= nowUtc && nowUtc - fail.Value < RetryAfterError) return false;
            if (!last.HasValue || last.Value > nowUtc) return true;
            return nowUtc - last.Value >= CheckInterval;
        }

        // Anota el resultado de una comprobación (correcta o fallida). Hay que guardar la configuración después.
        public static void RecordCheck(AppConfig c, DateTime nowUtc, bool ok)
        {
            string t = nowUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            if (ok) { c.LastUpdateCheck = t; c.LastUpdateFailure = null; }
            else c.LastUpdateFailure = t;
        }

        static DateTime? ParseUtc(string s)
        {
            DateTime d;
            if (string.IsNullOrEmpty(s) || !DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d)) return null;
            return d;
        }

        // ------------------------------------------------------------------ manifiesto

        // Lee la versión publicada (bloquea: llamar desde un hilo de fondo). Lanza UpdateException con un texto legible.
        public static UpdateInfo Fetch(AppConfig c)
        {
            Uri feed;
            if (!Uri.TryCreate(FeedUrl(c), UriKind.Absolute, out feed)) throw new UpdateException("La dirección de actualizaciones no es válida: " + FeedUrl(c));
            CheckScheme(feed);
            string json = Encoding.UTF8.GetString(Get(c, feed, 1024 * 1024, "application/vnd.github+json, application/json"));
            UpdateInfo u = Parse(json, feed);
            // GitHub sin "digest": el SHA-256 del archivo .sha256 publicado junto al instalador.
            if (u.Sha256 == null && u.Sha256Url != null)
            {
                Uri su;
                if (Uri.TryCreate(u.Sha256Url, UriKind.Absolute, out su) && IsAllowed(su))
                {
                    try { u.Sha256 = FindSha256(Encoding.UTF8.GetString(Get(c, su, 64 * 1024, null)), true); }
                    catch (UpdateException) { }
                }
            }
            return u;
        }

        public static UpdateInfo Parse(string json, Uri feed)
        {
            Dictionary<string, object> d;
            try { d = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>; }
            catch (ArgumentException) { d = null; }
            catch (InvalidOperationException) { d = null; }
            if (d == null) throw new UpdateException("La respuesta del servidor de actualizaciones no es válida.");
            UpdateInfo u = new UpdateInfo();
            if (d.ContainsKey("tag_name") || d.ContainsKey("assets"))
            {
                // Respuesta de la API de GitHub Releases.
                u.VersionText = Str(d, "tag_name") ?? Str(d, "name");
                u.Notes = Str(d, "body");
                u.PageUrl = Str(d, "html_url");
                Dictionary<string, object> setup = null;
                object[] assets = d.ContainsKey("assets") ? d["assets"] as object[] : null;
                if (assets != null)
                {
                    foreach (object o in assets)
                    {
                        Dictionary<string, object> a = o as Dictionary<string, object>;
                        string n = a != null ? Str(a, "name") ?? "" : "";
                        if (Regex.IsMatch(n, @"^IDMClone-Setup.*\.exe$", RegexOptions.IgnoreCase)) { setup = a; break; }
                    }
                    if (setup != null)
                    {
                        u.InstallerUrl = Str(setup, "browser_download_url");
                        u.Sha256 = FindSha256(Str(setup, "digest"), true);
                        string shaName = Str(setup, "name") + ".sha256";
                        foreach (object o in assets)
                        {
                            Dictionary<string, object> a = o as Dictionary<string, object>;
                            if (a != null && string.Equals(Str(a, "name"), shaName, StringComparison.OrdinalIgnoreCase)) u.Sha256Url = Str(a, "browser_download_url");
                        }
                    }
                }
                if (u.Sha256 == null) u.Sha256 = FindSha256(u.Notes, false);
            }
            else
            {
                // Manifiesto propio.
                u.VersionText = Str(d, "version");
                u.InstallerUrl = Str(d, "url") ?? Str(d, "installer");
                u.Sha256 = FindSha256(Str(d, "sha256"), true);
                u.Notes = Str(d, "notes");
                u.PageUrl = Str(d, "page");
            }
            u.Version = ParseVersion(u.VersionText);
            if (u.Version == null) throw new UpdateException("El servidor de actualizaciones no indica la versión publicada.");
            if (!string.IsNullOrEmpty(u.InstallerUrl) && feed != null)
            {
                Uri abs;
                if (Uri.TryCreate(feed, u.InstallerUrl, out abs)) u.InstallerUrl = abs.AbsoluteUri;
            }
            if (u.Notes != null) u.Notes = u.Notes.Replace("\r\n", "\n").Replace("\n", "\r\n").Trim();
            return u;
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return null;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim();
            return s.Length == 0 ? null : s;
        }

        // Un SHA-256 en hexadecimal: el valor entero ("sha256:<hex>" o "<hex>  archivo") o, en un texto, tras "SHA-256".
        static string FindSha256(string s, bool whole)
        {
            if (string.IsNullOrEmpty(s)) return null;
            Match m = whole ? Regex.Match(s.Trim(), @"^(?:sha-?256:)?\s*([0-9a-fA-F]{64})\b", RegexOptions.IgnoreCase)
                            : Regex.Match(s, @"SHA-?256\W{0,20}([0-9a-fA-F]{64})\b", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }

        // ------------------------------------------------------------------ red

        // Solo HTTPS (salvo el propio equipo, para las pruebas y un servidor local).
        public static bool IsAllowed(Uri u)
        {
            return u != null && (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback));
        }

        static void CheckScheme(Uri u)
        {
            if (!IsAllowed(u)) throw new UpdateException("Las actualizaciones solo se descargan por HTTPS: " + u.AbsoluteUri);
        }

        static HttpWebRequest NewRequest(AppConfig c, Uri u, string accept)
        {
            HttpWebRequest r = (HttpWebRequest)WebRequest.Create(u);
            r.AllowAutoRedirect = false;   // las redirecciones las sigue Open, comprobando cada destino antes de conectar
            r.UserAgent = "IDMClone/" + Display(CurrentVersion);   // la API de GitHub exige User-Agent
            if (accept != null) r.Accept = accept;
            if (c != null) { try { r.Proxy = NetSettings.ProxyFor(c, u); } catch { } }   // el proxy de Opciones > Proxy/Socks
            return r;
        }

        // Abre la URL siguiendo hasta 5 redirecciones; cada destino tiene que ser HTTPS (o local) ANTES de conectar con él,
        // para que no salga ni una petición en claro. decompress: el manifiesto puede venir comprimido; el instalador no
        // (su Content-Length sería el comprimido y el tamaño no cuadraría).
        static HttpWebResponse Open(AppConfig c, Uri u, string accept, int timeoutMs, bool decompress)
        {
            for (int hop = 0; ; hop++)
            {
                CheckScheme(u);
                HttpWebRequest r = NewRequest(c, u, accept);
                r.Timeout = timeoutMs; r.ReadWriteTimeout = timeoutMs;
                r.AutomaticDecompression = decompress ? DecompressionMethods.GZip | DecompressionMethods.Deflate : DecompressionMethods.None;
                HttpWebResponse resp = (HttpWebResponse)r.GetResponse();
                int code = (int)resp.StatusCode;
                if (code < 300 || code > 399 || code == 304) return resp;
                string loc = resp.Headers[HttpResponseHeader.Location];
                resp.Close();
                Uri next;
                if (hop >= 5 || string.IsNullOrEmpty(loc) || !Uri.TryCreate(u, loc, out next))
                    throw new UpdateException("El servidor de actualizaciones respondió con un error (" + code + ").");
                u = next;
            }
        }

        static byte[] Get(AppConfig c, Uri u, int max, string accept)
        {
            try
            {
                using (HttpWebResponse resp = Open(c, u, accept, 30000, true))
                {
                    using (Stream s = resp.GetResponseStream())
                    using (MemoryStream ms = new MemoryStream())
                    {
                        byte[] buf = new byte[16384];
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            ms.Write(buf, 0, n);
                            if (ms.Length > max) throw new UpdateException("La respuesta del servidor de actualizaciones es demasiado grande.");
                        }
                        return ms.ToArray();
                    }
                }
            }
            catch (WebException ex) { throw new UpdateException(NetError(ex)); }
            catch (IOException ex) { throw new UpdateException("Error de red: " + ex.Message); }
        }

        static string NetError(WebException ex)
        {
            HttpWebResponse r = ex.Response as HttpWebResponse;
            if (r != null)
            {
                int code = (int)r.StatusCode;
                r.Close();
                if (code == 404) return "No hay ninguna versión publicada en el servidor de actualizaciones (404).";
                return "El servidor de actualizaciones respondió con un error (" + code + ").";
            }
            return "No se pudo conectar con el servidor de actualizaciones: " + ex.Message;
        }

        // ------------------------------------------------------------------ descarga y comprobación

        public static string DownloadFolderOverride;   // pruebas
        public static string DownloadFolder { get { return DownloadFolderOverride ?? Path.Combine(Path.GetTempPath(), "IDMClone-update"); } }

        // Descarga el instalador a "folder", comprueba el SHA-256 y la firma, y devuelve su ruta. Si algo falla lanza
        // UpdateException y no deja nada. progress(bytes, total o -1); cancelled() detiene la descarga.
        public static string Download(AppConfig c, UpdateInfo u, string folder, string currentExe, Action<long, long> progress, Func<bool> cancelled)
        {
            if (u == null || string.IsNullOrEmpty(u.InstallerUrl)) throw new UpdateException("La versión publicada no incluye el instalador.");
            if (u.Sha256 == null) throw new UpdateException("La versión publicada no indica el SHA-256 del instalador: no se instala sin poder comprobarlo.");
            Uri url;
            if (!Uri.TryCreate(u.InstallerUrl, UriKind.Absolute, out url)) throw new UpdateException("La dirección del instalador no es válida.");
            CheckScheme(url);
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, "IDMClone-Setup-" + Display(u.Version) + ".exe");
            string part = target + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".part"; // otra descarga a la vez no choca
            CleanFolder(folder);
            try
            {
                using (HttpWebResponse resp = Open(c, url, "application/octet-stream", 60000, false))
                {
                    long total = resp.ContentLength;
                    using (Stream s = resp.GetResponseStream())
                    using (FileStream fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        byte[] buf = new byte[65536];
                        long done = 0;
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (cancelled != null && cancelled()) throw new UpdateException("Descarga cancelada.");
                            fs.Write(buf, 0, n);
                            done += n;
                            if (done > 512L * 1024 * 1024) throw new UpdateException("El instalador es demasiado grande.");
                            if (progress != null) progress(done, total);
                        }
                        if (total >= 0 && done != total) throw new UpdateException("La descarga del instalador se cortó.");
                    }
                }
                if (cancelled != null && cancelled()) throw new UpdateException("Descarga cancelada.");
                string err = Verify(part, u, currentExe);
                if (err == null && cancelled != null && cancelled()) err = "Descarga cancelada.";
                if (err != null) throw new UpdateException(err);
                if (File.Exists(target)) File.Delete(target);
                File.Move(part, target);
                return target;
            }
            catch (WebException ex) { TryDelete(part); throw new UpdateException(NetError(ex)); }
            catch (IOException ex) { TryDelete(part); throw new UpdateException("No se pudo guardar el instalador: " + ex.Message); }
            catch (UnauthorizedAccessException ex) { TryDelete(part); throw new UpdateException("No se pudo guardar el instalador: " + ex.Message); }
            catch { TryDelete(part); throw; }
        }

        static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

        // Instaladores de actualizaciones anteriores (de más de una hora: el de una actualización en curso puede seguir
        // abierto). Se llama al arrancar IDMClone y antes de cada descarga.
        public static void CleanFolder(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (string f in Directory.GetFiles(folder))
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromHours(1)) TryDelete(f);
            }
            catch { }
        }

        // null si el instalador es bueno; si no, el motivo.
        public static string Verify(string file, UpdateInfo u, string currentExe)
        {
            string sha = Sha256Of(file);
            if (u.Sha256 == null || !string.Equals(sha, u.Sha256, StringComparison.OrdinalIgnoreCase))
                return "El instalador descargado no coincide con el publicado (SHA-256 distinto): se ha descartado.";
            string sig = CheckSignature(file, currentExe);
            if (sig != null) return sig;
            // Con IDMClone firmado, un instalador firmado pero de una versión anterior (otra publicación del mismo editor
            // servida por un manifiesto manipulado) no vale: la versión del propio archivo tiene que ser más nueva.
            if (SignerOf(currentExe) != null)
            {
                Version fv = FileVersionOf(file);
                if (fv == null || fv.CompareTo(CurrentVersion) <= 0)
                    return "El instalador descargado no es de una versión más nueva que la instalada: se ha descartado.";
            }
            return null;
        }

        static Version FileVersionOf(string file)
        {
            try
            {
                FileVersionInfo v = FileVersionInfo.GetVersionInfo(file);
                return Normalize(new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart));
            }
            catch { return null; }
        }

        public static string Sha256Of(string file)
        {
            using (SHA256 h = SHA256.Create())
            using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                return BitConverter.ToString(h.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }

        // Firma Authenticode. Si este IDMClone.exe no está firmado (compilado a mano) basta el SHA-256. Si lo está, el
        // instalador tiene que estar firmado, con la firma intacta y por el mismo editor (mismo "Subject" del certificado,
        // que se mantiene al renovarlo). Con un certificado propio o de pruebas (raíz que Windows no reconoce) solo vale
        // exactamente el mismo certificado que firma este IDMClone.exe.
        public static string CheckSignature(string installer, string currentExe)
        {
            X509Certificate2 mine = SignerOf(currentExe);
            if (mine == null) return null;
            X509Certificate2 theirs = SignerOf(installer);
            if (theirs == null) return "El instalador descargado no tiene firma digital y este IDMClone sí: se ha descartado.";
            uint trust = (uint)VerifyTrust(installer);
            if (trust == 0)
            {
                // Mismo editor = mismo "Subject" si este IDMClone tiene una firma de confianza; si no, el mismo certificado.
                bool mineTrusted = VerifyTrust(currentExe) == 0;
                if (mineTrusted ? string.Equals(mine.Subject, theirs.Subject, StringComparison.Ordinal)
                                : string.Equals(mine.Thumbprint, theirs.Thumbprint, StringComparison.OrdinalIgnoreCase)) return null;
                return "El instalador está firmado por otro editor (" + theirs.GetNameInfo(X509NameType.SimpleName, false) + "): se ha descartado.";
            }
            if (trust == CERT_E_UNTRUSTEDROOT || trust == CERT_E_CHAINING || trust == CERT_E_UNTRUSTEDTESTROOT)
            {
                if (string.Equals(mine.Thumbprint, theirs.Thumbprint, StringComparison.OrdinalIgnoreCase)) return null;
                return "El instalador está firmado con un certificado que no es de confianza: se ha descartado.";
            }
            return "La firma digital del instalador no es válida (0x" + trust.ToString("X8") + "): se ha descartado.";
        }

        // Certificado del firmante (sin comprobar nada), o null si el archivo no está firmado.
        public static X509Certificate2 SignerOf(string file)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
            try { return new X509Certificate2(X509Certificate.CreateFromSignedFile(file)); }
            catch (CryptographicException) { return null; }
        }

        const uint CERT_E_UNTRUSTEDROOT = 0x800B0109, CERT_E_CHAINING = 0x800B010A, CERT_E_UNTRUSTEDTESTROOT = 0x800B010D;

        // WinVerifyTrust (lo que usa el Explorador): 0 = firma intacta y de confianza; si no, el código de error
        // (p. ej. 0x80096010 si el archivo se modificó tras firmarlo, 0x800B0100 sin firma). Sin interfaz;
        // comprueba la revocación de la cadena (salvo la raíz), por red si hace falta: se llama desde un hilo de fondo.
        public static int VerifyTrust(string file)
        {
            WINTRUST_FILE_INFO fi = new WINTRUST_FILE_INFO();
            fi.cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO));
            fi.pcwszFilePath = file;
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
            try
            {
                Marshal.StructureToPtr(fi, pFile, false);
                WINTRUST_DATA wd = new WINTRUST_DATA();
                wd.cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA));
                wd.dwUIChoice = 2;            // WTD_UI_NONE
                wd.fdwRevocationChecks = 1;   // WTD_REVOKE_WHOLECHAIN: un certificado robado y revocado no vale
                wd.dwUnionChoice = 1;         // WTD_CHOICE_FILE
                wd.pFile = pFile;
                wd.dwStateAction = 1;         // WTD_STATEACTION_VERIFY
                wd.dwProvFlags = 0x80;        // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
                Guid action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
                int r = WinVerifyTrust(new IntPtr(-1), ref action, ref wd);
                wd.dwStateAction = 2;         // WTD_STATEACTION_CLOSE
                WinVerifyTrust(new IntPtr(-1), ref action, ref wd);
                return r;
            }
            finally
            {
                Marshal.DestroyStructure(pFile, typeof(WINTRUST_FILE_INFO));
                Marshal.FreeHGlobal(pFile);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

        // ------------------------------------------------------------------ instalar

        // Carpeta donde está instalado IDMClone (la que anotó el instalador en "Aplicaciones instaladas"), o null.
        public static string InstalledDir()
        {
            if (InstalledDirHook != null) return InstalledDirHook();
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                {
                    string d = k != null ? k.GetValue("InstallLocation") as string : null;
                    return string.IsNullOrEmpty(d) ? null : d;
                }
            }
            catch { return null; }
        }

        // Cómo se lanza el instalador: si este IDMClone es el instalado, en modo actualización en su carpeta
        // ("/update /D=<carpeta>", sin preguntas, y vuelve a abrir IDMClone); si es otra copia (portátil o de desarrollo),
        // el asistente normal, para que el usuario elija dónde instalar.
        public static ProcessStartInfo InstallerStart(string installer, string currentExe)
        {
            string mine = Path.GetDirectoryName(Path.GetFullPath(currentExe)).TrimEnd('\\');
            string inst = InstalledDir();
            bool installed = false;
            try { installed = inst != null && string.Equals(Path.GetFullPath(inst).TrimEnd('\\'), mine, StringComparison.OrdinalIgnoreCase); }
            catch { }
            ProcessStartInfo psi = new ProcessStartInfo(installer);
            psi.UseShellExecute = false;
            psi.WorkingDirectory = Path.GetDirectoryName(installer);
            if (installed) psi.Arguments = "/update \"/D=" + (mine.EndsWith(":") ? mine + "\\." : mine) + "\""; // "C:\" + comilla se leería como \"
            return psi;
        }

        // Lanza el instalador; true si arrancó (entonces IDMClone se cierra para que lo pueda sustituir).
        public static bool Launch(string installer, string currentExe)
        {
            ProcessStartInfo psi = InstallerStart(installer, currentExe);
            if (LaunchHook != null) return LaunchHook(psi);
            try { Process.Start(psi); return true; }
            catch { return false; }
        }
    }
}
