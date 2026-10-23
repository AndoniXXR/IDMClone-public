using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace IDMClone
{
    // Ajustes de red de las peticiones: proxy (IDM: Opciones -> Proxy) e inicios de sesión por sitio.
    public static class NetSettings
    {
        // Proxy para una URL según la configuración. null = conexión directa.
        public static IWebProxy ProxyFor(AppConfig cfg, Uri uri)
        {
            switch (cfg.ProxyMode)
            {
                case "none":
                    return null;
                case "manual":
                    if (uri != null && uri.Scheme == Uri.UriSchemeFtp) return FtpProxyFor(cfg, uri);
                    // Como IDM: cada protocolo marcado ("Usar este proxy para..." / "Usar Proxy HTTP/HTTPS") va por su
                    // proxy HTTP; los demás, por SOCKS si está activo ("IDM sólo usará socks para los protocolos que no
                    // estén verificados arriba") y, si no, directos. Un mismo objeto para los dos esquemas: una
                    // redirección de http a https (o al revés) usa el proxy que toca.
                    IWebProxy ph = ProtocolProxy(cfg, Uri.UriSchemeHttp), ps = ProtocolProxy(cfg, Uri.UriSchemeHttps);
                    if (ph == null && ps == null) return null;
                    if (ph == ps) return ph;
                    return new SchemeProxy(ph, ps);
                case "pac":
                    string p = Pac.ProxyForUrl(cfg.PacUrl, uri.AbsoluteUri);
                    if (p == null) return null; // DIRECT
                    return new WebProxy(p);
                default:
                    return WebRequest.DefaultWebProxy; // el de Windows
            }
        }

        // Proxy HTTP manual de un protocolo ("http", "https" o "ftp"): el suyo de "Avanzado..." o, si no tiene
        // servidor propio, el de la página Proxy/Socks. null si ese protocolo no usa proxy HTTP.
        internal static WebProxy ManualProxy(AppConfig cfg, string scheme)
        {
            bool on = scheme == Uri.UriSchemeHttps ? cfg.ProxyHttps : scheme == Uri.UriSchemeFtp ? cfg.ProxyFtp : cfg.ProxyHttp;
            if (!on) return null;
            string host = cfg.ProxyHost, user = cfg.ProxyUser, passEnc = cfg.ProxyPasswordEnc;
            int port = cfg.ProxyPort;
            if (scheme == Uri.UriSchemeHttps && cfg.ProxyHttpsHost.Length > 0)
            { host = cfg.ProxyHttpsHost; port = cfg.ProxyHttpsPort; user = cfg.ProxyHttpsUser; passEnc = cfg.ProxyHttpsPasswordEnc; }
            else if (scheme == Uri.UriSchemeFtp && cfg.ProxyFtpHost.Length > 0)
            { host = cfg.ProxyFtpHost; port = cfg.ProxyFtpPort; user = cfg.ProxyFtpUser; passEnc = cfg.ProxyFtpPasswordEnc; }
            host = (host ?? "").Trim();
            if (host.Length == 0) return null;
            NetworkCredential cred = string.IsNullOrEmpty(user) ? null : new NetworkCredential(user, Util.Unprotect(passEnc) ?? "");
            return BuildProxy(cfg, new WebProxy(host, port), cred);
        }

        // Proxy de un protocolo web en modo manual: su proxy HTTP; si no, SOCKS (puente local); si no, directo (null).
        // Casilla marcada sin ningún servidor ni SOCKS: el proxy de Windows, como antes.
        static IWebProxy ProtocolProxy(AppConfig cfg, string scheme)
        {
            WebProxy wp = ManualProxy(cfg, scheme);
            if (wp != null) return wp;
            SocksSettings socks = SocksSettings.From(cfg);
            if (socks != null) return BuildProxy(cfg, new WebProxy(SocksBridge.Address), SocksBridge.CredentialFor(socks));   // SOCKS: por el puente local
            bool on = scheme == Uri.UriSchemeHttps ? cfg.ProxyHttps : cfg.ProxyHttp;
            return on ? WebRequest.DefaultWebProxy : null;
        }

        // FTP por proxy HTTP (modo manual, casilla ftp marcada y con servidor): el proxy, o null si va directo
        // (también ftps://, que un proxy HTTP no puede cifrar, y las direcciones de la lista de excepciones).
        public static WebProxy FtpProxyFor(AppConfig cfg, Uri uri)
        {
            if (cfg == null || cfg.ProxyMode != "manual" || uri == null) return null;
            WebProxy wp = ManualProxy(cfg, Uri.UriSchemeFtp);
            if (wp == null) return null;
            try { if (wp.IsBypassed(uri)) return null; } catch { }
            return wp;
        }

        // Proxy con la lista de direcciones que no lo usan (Avanzado... / Socks...).
        static WebProxy BuildProxy(AppConfig cfg, WebProxy wp, NetworkCredential cred)
        {
            List<string> bypass = new List<string>();
            foreach (string b in (cfg.ProxyBypass ?? "").Split(';', ','))
            {
                string t = b.Trim();
                if (t.Length == 0) continue;
                if (t == "<local>") { wp.BypassProxyOnLocal = true; continue; }
                // WebProxy.BypassList son expresiones regulares que .NET compara con "esquema://servidor[:puerto]":
                // el esquema y el puerto son opcionales para que "ejemplo.com" o "*.ejemplo.com" funcionen.
                bypass.Add("^(?:[a-z][a-z0-9+.-]*://)?" + BypassBody(t) + "(?::[0-9]+)?/?$");
            }
            wp.BypassList = bypass.ToArray();
            if (cred != null) wp.Credentials = cred;
            return wp;
        }

        // Patrón de la lista de excepciones como expresión regular. En los que parecen una IPv4 (solo cifras,
        // puntos, * y ?), los comodines solo valen por cifras: "10.0.0.*" coincide con 10.0.0.5 pero no con
        // 10.0.0.5.evil.io. Un * final con menos de cuatro números ("10.*") cubre los números que faltan.
        internal static string BypassBody(string pattern)
        {
            // "10.0.0.*:8080": el puerto va aparte; si no, los ':' sacarían el patrón de la rama IPv4 y su * valdría ".*".
            System.Text.RegularExpressions.Match pm = System.Text.RegularExpressions.Regex.Match(pattern, "^(.+?)(:[0-9*]+)$");
            if (pm.Success)
                return BypassBody(pm.Groups[1].Value) + System.Text.RegularExpressions.Regex.Escape(pm.Groups[2].Value).Replace("\\*", "[0-9]+");
            string esc = System.Text.RegularExpressions.Regex.Escape(pattern);
            if (!System.Text.RegularExpressions.Regex.IsMatch(pattern, "^[0-9.*?]+$") || pattern.IndexOf('.') < 0 ||
                !System.Text.RegularExpressions.Regex.IsMatch(pattern, "[0-9]"))
                return esc.Replace("\\*", ".*").Replace("\\?", ".");
            int missing = 4 - pattern.Split('.').Length;
            string tail = null;
            if (missing > 0 && esc.EndsWith("\\*"))
            {
                esc = esc.Substring(0, esc.Length - 2);
                tail = "[0-9]+(?:\\.[0-9]+){0," + missing + "}";
            }
            return esc.Replace("\\*", "[0-9]+").Replace("\\?", "[0-9]") + (tail ?? "");
        }

        // FTP por un proxy HTTP ("CERN", como Squid): una petición HttpWebRequest con la dirección ftp://. .NET manda al
        // proxy "GET ftp://[usuario:clave@]servidor/ruta HTTP/1.1" (el usuario y la contraseña salen de Credentials si
        // la dirección no los lleva). Es lo mismo que hace FtpWebRequest por dentro con un proxy HTTP, pero sin su
        // límite: FtpWebRequest no deja pedir un trozo (ContentOffset) por proxy; así van Range, varias conexiones y
        // reanudar, si el proxy los admite (si responde 200 entero, una sola conexión, como un servidor sin rangos).
        static System.Reflection.ConstructorInfo _ftpHttpCtor;

        public static HttpWebRequest FtpViaHttpProxy(Uri ftpUri, IWebProxy proxy)
        {
            if (ftpUri == null || ftpUri.Scheme != Uri.UriSchemeFtp) throw new ArgumentException("Se esperaba una dirección ftp://", "ftpUri");
            System.Reflection.ConstructorInfo c = _ftpHttpCtor;
            if (c == null)
            {
                c = typeof(HttpWebRequest).GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
                                                          new Type[] { typeof(Uri), typeof(ServicePoint) }, null);
                if (c == null) throw new NotSupportedException("Esta versión de .NET no permite descargar FTP a través de un proxy HTTP.");
                _ftpHttpCtor = c;
            }
            HttpWebRequest r = (HttpWebRequest)c.Invoke(new object[] { ftpUri, ServicePointManager.FindServicePoint(ftpUri, proxy) });
            r.Proxy = proxy;
            // .NET lee Credentials al escribir la línea de la petición (sin ellas falla por dentro y la petición se
            // queda colgada): sin usuario, unas que no dan ninguno -> el proxy entra como anónimo.
            r.Credentials = NoCredentials.Instance;
            return r;
        }

        sealed class NoCredentials : ICredentials
        {
            public static readonly NoCredentials Instance = new NoCredentials();
            public NetworkCredential GetCredential(Uri uri, string authType) { return null; }
        }

        // Un proxy distinto para http y para https (null = directo), también tras una redirección a otro esquema.
        sealed class SchemeProxy : IWebProxy
        {
            readonly IWebProxy _http, _https;
            ICredentials _creds;
            public SchemeProxy(IWebProxy http, IWebProxy https) { _http = http; _https = https; _creds = new SchemeCredentials(this); }
            IWebProxy For(Uri u) { return u.Scheme == Uri.UriSchemeHttps ? _https : u.Scheme == Uri.UriSchemeHttp ? _http : null; }
            public Uri GetProxy(Uri destination) { IWebProxy p = For(destination); return p == null ? destination : p.GetProxy(destination); }
            public bool IsBypassed(Uri host) { IWebProxy p = For(host); return p == null || p.IsBypassed(host); }
            public ICredentials Credentials { get { return _creds; } set { _creds = value; } }

            // .NET pide las credenciales del proxy con su dirección: las del proxy (http o https) que tenga esa dirección.
            sealed class SchemeCredentials : ICredentials
            {
                readonly SchemeProxy _o;
                public SchemeCredentials(SchemeProxy o) { _o = o; }
                public NetworkCredential GetCredential(Uri uri, string authType)
                {
                    foreach (IWebProxy p in new IWebProxy[] { _o._http, _o._https })
                    {
                        WebProxy wp = p as WebProxy;
                        if (wp == null || wp.Address == null || uri == null || !string.Equals(wp.Address.Authority, uri.Authority, StringComparison.OrdinalIgnoreCase)) continue;
                        return wp.Credentials != null ? wp.Credentials.GetCredential(uri, authType) : null;
                    }
                    foreach (IWebProxy p in new IWebProxy[] { _o._http, _o._https })
                    {
                        NetworkCredential nc = p != null && p.Credentials != null ? p.Credentials.GetCredential(uri, authType) : null;
                        if (nc != null) return nc;
                    }
                    return null;
                }
            }
        }

        // Usuario y contraseña para una URL: los de "Inicios de sesión" o los de la propia URL (usuario:clave@).
        public static NetworkCredential CredentialsFor(AppConfig cfg, Uri uri)
        {
            SiteLogin s = cfg.LoginFor(uri);
            if (s != null) return new NetworkCredential(s.User, s.Password);
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                string[] up = uri.UserInfo.Split(new char[] { ':' }, 2);
                return new NetworkCredential(Uri.UnescapeDataString(up[0]), up.Length > 1 ? Uri.UnescapeDataString(up[1]) : "");
            }
            return null;
        }
    }

    // Digest con SHA-256 (RFC 7616) y qop=auth-int, que el Digest de .NET Framework no admite (solo MD5 con qop=auth).
    // El módulo de .NET va primero; este solo actúa cuando aquel no sabe responder al desafío.
    public class DigestAuth : IAuthenticationModule
    {
        static int _registered;
        public static void Register()
        {
            if (Interlocked.Exchange(ref _registered, 1) == 0) AuthenticationManager.Register(new DigestAuth());
        }

        public string AuthenticationType { get { return "DigestIDMClone"; } }
        public bool CanPreAuthenticate { get { return false; } }
        public Authorization PreAuthenticate(WebRequest request, ICredentials credentials) { return null; }

        public Authorization Authenticate(string challenge, WebRequest request, ICredentials credentials)
        {
            if (challenge == null || request == null || credentials == null) return null;
            NetworkCredential nc = credentials.GetCredential(request.RequestUri, "Digest");
            if (nc == null || string.IsNullOrEmpty(nc.UserName)) return null;
            foreach (Dictionary<string, string> ch in ParseDigestChallenges(challenge))
            {
                string algo; ch.TryGetValue("algorithm", out algo);
                algo = string.IsNullOrEmpty(algo) ? "MD5" : algo.ToUpperInvariant();
                bool sess = algo.EndsWith("-SESS");
                string baseAlgo = sess ? algo.Substring(0, algo.Length - 5) : algo;
                if (baseAlgo != "MD5" && baseAlgo != "SHA-256") continue;
                string realm, nonce, qopList, opaque;
                ch.TryGetValue("realm", out realm); ch.TryGetValue("nonce", out nonce);
                ch.TryGetValue("qop", out qopList); ch.TryGetValue("opaque", out opaque);
                if (nonce == null) continue;
                string qop = null;
                if (qopList != null)
                {
                    foreach (string q in qopList.Split(','))
                    {
                        string t = q.Trim().ToLowerInvariant();
                        if (t == "auth") { qop = "auth"; break; }
                        if (t == "auth-int") qop = "auth-int";
                    }
                    if (qop == null) continue;
                }
                return new Authorization(Build(nc, realm ?? "", nonce, opaque, algo, baseAlgo, sess, qop, request), true);
            }
            return null;
        }

        static string Build(NetworkCredential nc, string realm, string nonce, string opaque, string algo, string baseAlgo, bool sess, string qop, WebRequest request)
        {
            string uri = request.RequestUri.PathAndQuery;
            string cnonce = Guid.NewGuid().ToString("N").Substring(0, 16);
            const string count = "00000001";
            string ha1 = H(baseAlgo, nc.UserName + ":" + realm + ":" + nc.Password);
            if (sess) ha1 = H(baseAlgo, ha1 + ":" + nonce + ":" + cnonce);
            // auth-int: hash del cuerpo de la petición (vacío en un GET).
            string ha2 = qop == "auth-int" ? H(baseAlgo, request.Method + ":" + uri + ":" + H(baseAlgo, "")) : H(baseAlgo, request.Method + ":" + uri);
            string response = qop == null ? H(baseAlgo, ha1 + ":" + nonce + ":" + ha2)
                                          : H(baseAlgo, ha1 + ":" + nonce + ":" + count + ":" + cnonce + ":" + qop + ":" + ha2);
            StringBuilder sb = new StringBuilder("Digest ");
            sb.Append("username=\"").Append(Esc(nc.UserName)).Append("\", realm=\"").Append(Esc(realm))
              .Append("\", nonce=\"").Append(Esc(nonce)).Append("\", uri=\"").Append(Esc(uri))
              .Append("\", algorithm=").Append(algo).Append(", response=\"").Append(response).Append('"');
            if (qop != null) sb.Append(", qop=").Append(qop).Append(", nc=").Append(count).Append(", cnonce=\"").Append(cnonce).Append('"');
            if (opaque != null) sb.Append(", opaque=\"").Append(Esc(opaque)).Append('"');
            return sb.ToString();
        }

        static string Esc(string v) { return v.Replace("\\", "\\\\").Replace("\"", "\\\""); }

        public static string H(string algo, string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            using (HashAlgorithm h = algo == "SHA-256" ? (HashAlgorithm)SHA256.Create() : MD5.Create())
                return BitConverter.ToString(h.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        // Desafíos "Digest" de una o varias cabeceras WWW-Authenticate (unidas por comas).
        public static List<Dictionary<string, string>> ParseDigestChallenges(string s)
        {
            List<Dictionary<string, string>> all = new List<Dictionary<string, string>>();
            Dictionary<string, string> cur = null;
            int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && (s[i] == ' ' || s[i] == ',' || s[i] == '\t')) i++;
                int start = i;
                while (i < s.Length && s[i] != '=' && s[i] != ' ' && s[i] != ',') i++;
                string token = s.Substring(start, i - start);
                if (token.Length == 0) { i++; continue; }
                int j = i;
                while (j < s.Length && s[j] == ' ') j++;
                if (j < s.Length && s[j] == '=')
                {
                    i = j + 1;
                    while (i < s.Length && s[i] == ' ') i++;
                    string val;
                    if (i < s.Length && s[i] == '"')
                    {
                        StringBuilder v = new StringBuilder();
                        i++;
                        while (i < s.Length && s[i] != '"') { if (s[i] == '\\' && i + 1 < s.Length) i++; v.Append(s[i]); i++; }
                        i++;
                        val = v.ToString();
                    }
                    else
                    {
                        int vs = i;
                        while (i < s.Length && s[i] != ',') i++;
                        val = s.Substring(vs, i - vs).Trim();
                    }
                    if (cur != null) cur[token] = val;
                }
                else
                {
                    // Empieza otro esquema (Digest, Basic, NTLM...).
                    cur = null;
                    if (token.Equals("Digest", StringComparison.OrdinalIgnoreCase))
                    {
                        cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        all.Add(cur);
                    }
                }
            }
            return all;
        }
    }

    // Evaluación de scripts PAC con WinHTTP (la misma que usa Windows).
    public static class Pac
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct AutoProxyOptions
        {
            public int dwFlags;
            public int dwAutoDetectFlags;
            public string lpszAutoConfigUrl;
            public IntPtr lpvReserved;
            public int dwReserved;
            public bool fAutoLogonIfChallenged;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ProxyInfo
        {
            public int dwAccessType;
            public IntPtr lpszProxy;
            public IntPtr lpszProxyBypass;
        }

        [DllImport("winhttp.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr WinHttpOpen(string agent, int accessType, string proxy, string bypass, int flags);
        [DllImport("winhttp.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool WinHttpGetProxyForUrl(IntPtr session, string url, ref AutoProxyOptions options, out ProxyInfo info);
        [DllImport("kernel32.dll")]
        static extern IntPtr GlobalFree(IntPtr mem);

        const int WINHTTP_ACCESS_TYPE_NO_PROXY = 1, WINHTTP_ACCESS_TYPE_NAMED_PROXY = 3, WINHTTP_AUTOPROXY_CONFIG_URL = 2;
        static IntPtr _session;
        static readonly object _lock = new object();

        // "servidor:puerto" del primer proxy que dé el script, o null si dice DIRECT o falla.
        public static string ProxyForUrl(string pacUrl, string url)
        {
            if (string.IsNullOrEmpty(pacUrl)) return null;
            lock (_lock)
            {
                if (_session == IntPtr.Zero) _session = WinHttpOpen("IDMClone", WINHTTP_ACCESS_TYPE_NO_PROXY, null, null, 0);
                if (_session == IntPtr.Zero) return null;
                AutoProxyOptions o = new AutoProxyOptions();
                o.dwFlags = WINHTTP_AUTOPROXY_CONFIG_URL;
                o.lpszAutoConfigUrl = pacUrl;
                ProxyInfo info;
                if (!WinHttpGetProxyForUrl(_session, url, ref o, out info)) return null;
                try
                {
                    if (info.dwAccessType != WINHTTP_ACCESS_TYPE_NAMED_PROXY || info.lpszProxy == IntPtr.Zero) return null;
                    string list = Marshal.PtrToStringUni(info.lpszProxy);
                    foreach (string p in list.Split(';', ' '))
                        if (p.Trim().Length > 0) return p.Trim();
                    return null;
                }
                finally
                {
                    if (info.lpszProxy != IntPtr.Zero) GlobalFree(info.lpszProxy);
                    if (info.lpszProxyBypass != IntPtr.Zero) GlobalFree(info.lpszProxyBypass);
                }
            }
        }
    }

    // Cuota de descarga (IDM: Programador -> Límites de descarga): "no más de N MB cada H horas".
    // Al llegar al límite las descargas esperan y siguen solas cuando empieza el siguiente periodo.
    public static class DownloadQuota
    {
        static readonly object _lock = new object();
        static DateTime _windowStart = DateTime.MinValue;
        static long _used;
        public static int WindowSecondsOverride;       // solo para los tests
        public static DateTime BlockedUntilUtc { get; private set; }
        // Se dispara (desde un hilo de descarga) una vez por periodo, al alcanzar el límite: la interfaz avisa.
        public static event Action<DateTime> LimitReached;
        static void RaiseLimitReached(DateTime untilUtc)
        {
            Action<DateTime> h = LimitReached;
            if (h == null) return;
            // Cada suscriptor por separado: si uno falla, los demás reciben el aviso igual.
            foreach (Delegate d in h.GetInvocationList()) { try { ((Action<DateTime>)d)(untilUtc); } catch { } }
        }

        static DateTime _warnedWindow = DateTime.MinValue;   // periodo del que ya se avisó

        public static void Reset() { lock (_lock) { _windowStart = DateTime.MinValue; _used = 0; BlockedUntilUtc = DateTime.MinValue; _warnedWindow = DateTime.MinValue; } }

        public static void Consume(int bytes, AppConfig cfg, Func<bool> halted)
        {
            if (!cfg.QuotaEnabled || bytes <= 0) return;
            long limit = (long)Math.Max(1, cfg.QuotaMB) * 1024 * 1024;
            TimeSpan window = WindowSecondsOverride > 0 ? TimeSpan.FromSeconds(WindowSecondsOverride) : TimeSpan.FromHours(Math.Max(1, cfg.QuotaHours));
            DateTime until;
            bool warn = false;
            lock (_lock)
            {
                DateTime now = DateTime.UtcNow;
                if (now >= _windowStart + window) { _windowStart = now; _used = 0; }
                _used += bytes;
                if (_used < limit) return;
                until = _windowStart + window;
                BlockedUntilUtc = until;
                // Una sola vez por periodo, aunque lleguen al límite varias conexiones a la vez.
                if (_warnedWindow != _windowStart) { _warnedWindow = _windowStart; warn = true; }
            }
            if (warn) RaiseLimitReached(until);   // fuera del lock: el aviso no frena a las demás conexiones
            // También se libera si el usuario desactiva el límite mientras se espera.
            while (DateTime.UtcNow < until && cfg.QuotaEnabled && (halted == null || !halted())) Thread.Sleep(100);
            lock (_lock) { if (BlockedUntilUtc == until) BlockedUntilUtc = DateTime.MinValue; }
        }
    }
}
