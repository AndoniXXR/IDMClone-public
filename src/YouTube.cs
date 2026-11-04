using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IDMClone
{
    // Motor de YouTube (como IDMan.exe de IDM, que lo hace por su cuenta): la web de YouTube ya no pide los vídeos con
    // direcciones que se puedan capturar (SABR), así que la extensión solo pasa los datos de la página (videoId,
    // visitorData, reproductor, STS, sesión) y IDMClone pide la lista de formatos a la API /youtubei/v1/player con los
    // clientes de youtube.json, en orden, hasta que uno dé direcciones https que se puedan bajar. La firma y el "n" de
    // esas direcciones se descifran con YouTubeSolver. Las direcciones caducan (unas 6 h): la descarga vuelve a pedirlas
    // cada vez que empieza o se reanuda (YouTubeDownload.cs).

    public class YtQuery
    {
        public string VideoId, VisitorData, PlayerJsUrl, Cookies, UserAgent, Referrer;
        public string PageTitle; // título de la página del vídeo, si se leyó (algunos clientes no lo devuelven)
        public Action<string> Status; // la descarga: qué se está haciendo (p. ej. YouTubeSolver.PreparingText)
        public string LastClient;     // la descarga: cliente cuyas direcciones dieron 403 (se prueba el último)
        public int Sts;          // signatureTimestamp del reproductor (0 = no se sabe)
        public bool LoggedIn;
    }

    public class YtFormat
    {
        public int Itag;
        public string Key;                  // lo que va en "video"/"audio" del protocolo: "<itag>" o "<itag>:<pista>"
        public string Url;
        public string S, Sp;                // firma por descifrar (signatureCipher) y el parámetro donde va
        public string MimeType, Container, Codecs;  // "mp4"/"webm"
        public bool HasVideo, HasAudio;
        public int Width, Height, Fps;
        public long Bitrate, Size = -1;
        public bool SizeExact;              // Size es el contentLength (si no, una estimación por bitrate)
        public string QualityLabel;
        public string AudioTrackId, Language, LanguageName;
        public bool DefaultAudio = true, Drc;

        public bool Muxed { get { return HasVideo && HasAudio; } }
        public string Ext { get { return HasVideo ? (Container == "webm" ? ".webm" : ".mp4") : (Container == "webm" ? ".webm" : ".m4a"); } }
    }

    public class YtVideo
    {
        public string Id, Title, Client, UserAgent, HlsUrl;
        public double Duration;
        public bool Live;
        public List<YtFormat> Formats = new List<YtFormat>();
        internal Action WarmUp; // Resolve sin descifrar: prepara el descifrado (caché en disco) para la descarga

        public YtFormat Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            foreach (YtFormat f in Formats) if (f.Key == key) return f;
            // "140" sin pista: la pista por defecto con ese itag; "140:es.3" que ya no existe: no.
            if (key.IndexOf(':') < 0)
                foreach (YtFormat f in Formats) if (f.Itag.ToString(CultureInfo.InvariantCulture) == key && f.DefaultAudio) return f;
            return null;
        }
    }

    // Transporte HTTP (las pruebas lo sustituyen por respuestas guardadas).
    public interface IYtHttp
    {
        string Post(string url, string body, IDictionary<string, string> headers);
        string Get(string url, IDictionary<string, string> headers);
    }

    public class YtHttp : IYtHttp
    {
        readonly AppConfig _cfg;
        public YtHttp(AppConfig cfg) { _cfg = cfg; DownloadEngine.ConfigureNetwork(); }

        public string Post(string url, string body, IDictionary<string, string> headers) { return Send("POST", url, body, headers); }
        public string Get(string url, IDictionary<string, string> headers) { return Send("GET", url, null, headers); }

        string Send(string method, string url, string body, IDictionary<string, string> headers)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = 30000; req.ReadWriteTimeout = 60000;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (_cfg != null && _cfg.ProxyMode != "system") req.Proxy = NetSettings.ProxyFor(_cfg, req.RequestUri);
            if (headers != null)
                foreach (KeyValuePair<string, string> h in headers)
                {
                    if (string.IsNullOrEmpty(h.Value)) continue;
                    if (h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) req.UserAgent = h.Value;
                    else if (h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) req.ContentType = h.Value;
                    else if (h.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase)) req.Referer = h.Value;
                    else req.Headers[h.Key] = h.Value;
                }
            if (body != null)
            {
                byte[] b = Encoding.UTF8.GetBytes(body);
                req.ContentLength = b.Length;
                using (Stream s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return r.ReadToEnd();
            }
            catch (WebException wex)
            {
                HttpWebResponse h = wex.Response as HttpWebResponse;
                if (h != null)
                {
                    // La API contesta los errores con un JSON {"error":{"message":...}}: se devuelve para leer el motivo.
                    string text = "";
                    try { using (StreamReader r = new StreamReader(h.GetResponseStream(), Encoding.UTF8)) text = r.ReadToEnd(); } catch { }
                    if (method == "POST" && text.TrimStart().StartsWith("{")) return text;
                    throw new YouTubeException("YouTube contestó " + (int)h.StatusCode + " (" + h.StatusDescription + ").");
                }
                throw new YouTubeException("No se pudo conectar con YouTube: " + wex.Message);
            }
        }
    }

    public static class YouTube
    {
        public const string Kind = "youtube";   // mediaKind de las descargas de YouTube
        public static Func<AppConfig, IYtHttp> HttpFactory = delegate(AppConfig c) { return new YtHttp(c); }; // las pruebas lo cambian
        public static Func<YtSettings> SettingsProvider = YtSettings.Load;                                   // las pruebas lo cambian
        internal static bool AllowPlainHttp = false; // las pruebas: direcciones http:// de su servidor local (YouTube solo da https)

        static readonly Regex IdRe = new Regex("^[0-9A-Za-z_-]{11}$");
        public static bool ValidId(string id) { return id != null && IdRe.IsMatch(id); }

        public static string WatchUrl(string id) { return "https://www.youtube.com/watch?v=" + id; }

        // Id del vídeo en una dirección de YouTube (watch?v=, youtu.be/, shorts/, embed/, live/).
        public static bool TryVideoId(string url, out string id)
        {
            id = null;
            Uri u;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) return false;
            string host = u.Host.ToLowerInvariant();
            if (host.StartsWith("www.")) host = host.Substring(4);
            else if (host.StartsWith("m.")) host = host.Substring(2);
            else if (host.StartsWith("music.")) host = host.Substring(6);
            string cand = null;
            if (host == "youtu.be") cand = u.AbsolutePath.Trim('/');
            else if (host == "youtube.com" || host == "youtube-nocookie.com")
            {
                if (u.AbsolutePath == "/watch") cand = QueryParam(u.Query, "v");
                else
                {
                    Match m = Regex.Match(u.AbsolutePath, "^/(?:shorts|embed|live|v)/([^/?#]+)");
                    if (m.Success) cand = m.Groups[1].Value;
                }
            }
            if (!ValidId(cand)) return false;
            id = cand;
            return true;
        }

        static string QueryParam(string query, string name)
        {
            foreach (string part in (query ?? "").TrimStart('?').Split('&'))
            {
                int eq = part.IndexOf('=');
                string k = eq >= 0 ? part.Substring(0, eq) : part;
                if (k == name) return Uri.UnescapeDataString((eq >= 0 ? part.Substring(eq + 1) : "").Replace('+', ' '));
            }
            return null;
        }

        // ¿Hay sesión de Google en estas cookies? (como yt-dlp: LOGIN_INFO y algún SAPISID)
        public static bool CookiesLoggedIn(string cookies)
        {
            Dictionary<string, string> c = ParseCookies(cookies);
            return c.ContainsKey("LOGIN_INFO") && (c.ContainsKey("SAPISID") || c.ContainsKey("__Secure-3PAPISID") || c.ContainsKey("__Secure-1PAPISID"));
        }

        static Dictionary<string, string> ParseCookies(string cookies)
        {
            Dictionary<string, string> r = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string part in (cookies ?? "").Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string k = part.Substring(0, eq).Trim(), v = part.Substring(eq + 1).Trim();
                if (k.Length > 0 && !r.ContainsKey(k)) r[k] = v;
            }
            return r;
        }

        // Cabecera Authorization de la sesión (SAPISIDHASH y compañía), como la web de YouTube.
        public static string SidAuthorization(string cookies, string origin, long unixTime)
        {
            Dictionary<string, string> c = ParseCookies(cookies);
            string sapisid, p1, p3;
            c.TryGetValue("SAPISID", out sapisid); c.TryGetValue("__Secure-1PAPISID", out p1); c.TryGetValue("__Secure-3PAPISID", out p3);
            if (string.IsNullOrEmpty(sapisid)) sapisid = p3;
            List<string> parts = new List<string>();
            string ts = unixTime.ToString(CultureInfo.InvariantCulture);
            foreach (KeyValuePair<string, string> kv in new KeyValuePair<string, string>[] {
                new KeyValuePair<string, string>("SAPISIDHASH", sapisid), new KeyValuePair<string, string>("SAPISID1PHASH", p1),
                new KeyValuePair<string, string>("SAPISID3PHASH", p3) })
            {
                if (string.IsNullOrEmpty(kv.Value)) continue;
                using (SHA1 h = SHA1.Create())
                {
                    byte[] d = h.ComputeHash(Encoding.UTF8.GetBytes(ts + " " + kv.Value + " " + origin));
                    parts.Add(kv.Key + " " + ts + "_" + BitConverter.ToString(d).Replace("-", "").ToLowerInvariant());
                }
            }
            return parts.Count == 0 ? null : string.Join(" ", parts.ToArray());
        }

        // --- Petición a la API ---

        public static string PlayerRequestBody(YtSettings s, YtClient c, YtQuery q, int sts)
        {
            Dictionary<string, object> client = new Dictionary<string, object>();
            client["clientName"] = c.ClientName;
            client["clientVersion"] = c.ClientVersion;
            foreach (KeyValuePair<string, object> kv in c.Context) client[kv.Key] = kv.Value;
            if (!string.IsNullOrEmpty(c.UserAgent)) client["userAgent"] = c.UserAgent;
            client["hl"] = s.Hl;
            client["timeZone"] = "UTC";
            client["utcOffsetMinutes"] = 0;
            if (!string.IsNullOrEmpty(q.VisitorData)) client["visitorData"] = q.VisitorData;
            Dictionary<string, object> ctx = new Dictionary<string, object>();
            ctx["client"] = client;
            Dictionary<string, object> cpc = new Dictionary<string, object>();
            cpc["html5Preference"] = "HTML5_PREF_WANTS";
            if (sts > 0) cpc["signatureTimestamp"] = sts;
            Dictionary<string, object> pc = new Dictionary<string, object>();
            pc["contentPlaybackContext"] = cpc;
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["context"] = ctx;
            body["videoId"] = q.VideoId;
            body["playbackContext"] = pc;
            body["contentCheckOk"] = true;
            body["racyCheckOk"] = true;
            return AppConfig.NewSerializer().Serialize(body);
        }

        public static Dictionary<string, string> PlayerRequestHeaders(YtClient c, YtQuery q, bool withCookies)
        {
            const string origin = "https://www.youtube.com";
            Dictionary<string, string> h = new Dictionary<string, string>();
            h["Content-Type"] = "application/json";
            h["User-Agent"] = c.UserAgent;
            h["X-YouTube-Client-Name"] = c.ClientNameId.ToString(CultureInfo.InvariantCulture);
            h["X-YouTube-Client-Version"] = c.ClientVersion;
            h["Origin"] = origin;
            if (!string.IsNullOrEmpty(q.VisitorData)) h["X-Goog-Visitor-Id"] = q.VisitorData;
            if (withCookies)
            {
                h["Cookie"] = q.Cookies;
                string auth = SidAuthorization(q.Cookies, origin, (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds);
                if (auth != null) { h["Authorization"] = auth; h["X-Origin"] = origin; }
                if (q.LoggedIn) h["X-Youtube-Bootstrap-Logged-In"] = "true";
            }
            return h;
        }

        // Pide el vídeo con los clientes configurados, en orden; devuelve el primero con formatos que se puedan bajar
        // (ya descifrados). YouTubeException con un mensaje para el usuario si ninguno vale.
        public static YtVideo Resolve(AppConfig cfg, YtQuery q) { return Resolve(cfg, q, true); }

        // decipher = false (ytinfo): la lista de calidades no necesita las direcciones (la descarga las vuelve a pedir),
        // así que no se descifra; v.WarmUp lo deja preparado en la caché para cuando se descargue. Así ytinfo contesta en
        // 1-3 s aunque el reproductor sea nuevo (descifrar en frío tarda ~30 s).
        public static YtVideo Resolve(AppConfig cfg, YtQuery q, bool decipher)
        {
            if (q == null || !ValidId(q.VideoId)) throw new YouTubeException("El identificador del vídeo de YouTube no es válido.");
            YtSettings s = SettingsProvider();
            IYtHttp http = HttpFactory(cfg);
            bool cookies = !string.IsNullOrEmpty(q.Cookies);
            bool authed = cookies && (q.LoggedIn || CookiesLoggedIn(q.Cookies));
            List<string> order = new List<string>(authed && s.AuthedClients.Count > 0 ? s.AuthedClients : s.Clients);
            // Tras un 403 con las direcciones de un cliente, ese va el último (YouTube a veces da direcciones que no sirven).
            if (q.LastClient != null && order.Remove(q.LastClient)) order.Add(q.LastClient);
            // Sin los datos de la página (la descarga no los trae): se lee la página del vídeo, como yt-dlp. Sin visitorData
            // YouTube contesta "Sign in to confirm you're not a bot".
            if (!string.IsNullOrEmpty(q.VisitorData) && !ValidVisitorData(q.VisitorData))
            {
                // Recortado (la extensión 2.0 lo cortaba a 200 caracteres) o dañado: uno así hace que YouTube pida
                // "confirmar que no eres un bot" y gane un cliente que necesita descifrar. Se toma el de la página.
                NativeHost.Log("youtube: visitorData no válido (" + q.VisitorData.Length + " caracteres); se lee la página del vídeo");
                string bad = q.VisitorData;
                q.VisitorData = null;
                FillFromWatchPage(q, http);
                if (string.IsNullOrEmpty(q.VisitorData)) q.VisitorData = bad;
            }
            else if (string.IsNullOrEmpty(q.VisitorData)) FillFromWatchPage(q, http);
            PlayerInfo player = new PlayerInfo(s, q, http);
            List<string> problems = new List<string>();
            string login = null, unplayable = null;
            foreach (string id in order)
            {
                YtClient c = s.Client(id);
                if (c == null) { problems.Add(id + ": cliente sin definir en youtube.json"); continue; }
                int sts = 0;
                if (c.RequireJsPlayer)
                {
                    try { sts = player.Sts(); }
                    catch (YouTubeException ex) { problems.Add(id + ": " + ex.Message); continue; }
                }
                string resp;
                try { resp = http.Post(s.ApiUrl, PlayerRequestBody(s, c, q, sts), PlayerRequestHeaders(c, q, cookies && c.SupportsCookies)); }
                catch (YouTubeException ex) { problems.Add(id + ": " + ex.Message); continue; }
                catch (Exception ex) { problems.Add(id + ": " + ex.Message); continue; }

                Dictionary<string, object> pr;
                try { pr = AppConfig.NewSerializer().Deserialize<Dictionary<string, object>>(resp); }
                catch { problems.Add(id + ": respuesta no válida"); continue; }
                if (pr == null) { problems.Add(id + ": respuesta vacía"); continue; }
                string apiError = Path_(pr, "error", "message") as string;
                if (apiError != null) { problems.Add(id + ": " + apiError); continue; }
                string status = Path_(pr, "playabilityStatus", "status") as string ?? "";
                string reason = Path_(pr, "playabilityStatus", "reason") as string;
                if (status != "OK")
                {
                    if (status == "LOGIN_REQUIRED" || status.StartsWith("AGE_", StringComparison.Ordinal) || Path_(pr, "playabilityStatus", "desktopLegacyAgeGateReason") != null)
                        login = login ?? reason ?? status;
                    else if (status == "LIVE_STREAM_OFFLINE")
                        throw new YouTubeException("La emisión en directo aún no ha empezado" + (reason != null ? " (" + reason + ")." : "."));
                    else unplayable = unplayable ?? reason ?? status;
                    problems.Add(id + ": " + (reason ?? status));
                    continue;
                }
                YtVideo v = ParseVideo(pr, q.VideoId);
                if (v.Live)
                {
                    if (v.HlsUrl != null) { v.Client = id; v.UserAgent = c.UserAgent; return v; }
                    problems.Add(id + ": emisión en directo sin lista HLS");
                    continue;
                }
                if (v.Formats.Count == 0) { problems.Add(id + ": sin formatos que se puedan bajar"); continue; }
                if (!decipher)
                {
                    if (NeedsDecipher(v))
                    {
                        YtVideo vv = v; PlayerInfo pl = player;
                        v.WarmUp = delegate
                        {
                            try { Decipher(vv, pl); }
                            catch (Exception ex) { NativeHost.Log("youtube: no se pudo preparar el descifrado: " + ex.Message); }
                        };
                    }
                }
                else
                {
                    try { Decipher(v, player); }
                    catch (YouTubeException ex) { problems.Add(id + ": " + ex.Message); continue; }
                }
                v.Client = id; v.UserAgent = c.UserAgent;
                if (string.IsNullOrEmpty(v.Title)) v.Title = q.PageTitle;
                if (problems.Count > 0) NativeHost.Log("youtube: " + q.VideoId + " con " + id + "; antes: " + string.Join(" | ", problems.ToArray()));
                return v;
            }
            NativeHost.Log("youtube: " + q.VideoId + " sin formatos: " + string.Join(" | ", problems.ToArray()));
            if (login != null)
                throw new YouTubeException(authed
                    ? "YouTube no deja ver este vídeo con la sesión del navegador: " + login
                    : "YouTube pide iniciar sesión para ver este vídeo (" + login + "). Inicia sesión en YouTube en el navegador, abre el vídeo y vuelve a intentarlo.");
            if (unplayable != null) throw new YouTubeException("YouTube no deja ver este vídeo: " + unplayable);
            throw new YouTubeException("No se pudieron obtener los formatos del vídeo de YouTube" + (problems.Count > 0 ? ": " + string.Join("; ", problems.ToArray()) : "."));
        }

        // VISITOR_DATA, PLAYER_JS_URL y STS de la página watch?v= (con las cookies del navegador, si las hay).
        // Lo que no se encuentre se deja como estaba; un error de red no impide seguir.
        public static void FillFromWatchPage(YtQuery q, IYtHttp http)
        {
            string html;
            try
            {
                Dictionary<string, string> h = new Dictionary<string, string>();
                h["User-Agent"] = string.IsNullOrEmpty(q.UserAgent) ? DefaultBrowserUa : q.UserAgent;
                h["Accept-Language"] = "en-US,en;q=0.9";
                if (!string.IsNullOrEmpty(q.Cookies)) h["Cookie"] = q.Cookies;
                html = http.Get(WatchUrl(q.VideoId) + "&hl=en&bpctr=9999999999&has_verified=1", h);
            }
            catch (Exception ex) { NativeHost.Log("youtube: no se pudo leer la página del vídeo: " + ex.Message); return; }
            if (string.IsNullOrEmpty(html)) return;
            string vd = JsonStringField(html, "VISITOR_DATA");
            if (!string.IsNullOrEmpty(vd) && string.IsNullOrEmpty(q.VisitorData)) q.VisitorData = vd.IndexOf('%') >= 0 ? Uri.UnescapeDataString(vd) : vd;
            string pj = JsonStringField(html, "PLAYER_JS_URL") ?? JsonStringField(html, "jsUrl");
            if (!string.IsNullOrEmpty(pj) && string.IsNullOrEmpty(q.PlayerJsUrl) && YouTubeSolver.PlayerId(pj) != null) q.PlayerJsUrl = pj;
            Match m = Regex.Match(html, @"""STS""\s*:\s*([0-9]{5})");
            if (m.Success && q.Sts <= 0) q.Sts = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            m = Regex.Match(html, @"<meta\s+name=""title""\s+content=""([^""]{1,500})""");
            if (m.Success) q.PageTitle = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
        }

        // ¿Es un visitorData entero? Es un mensaje protobuf en base64 (url-safe, a veces con %3D): se recorren sus campos
        // y tienen que acabar justo al final. Uno recortado casi siempre se queda a mitad de un campo.
        public static bool ValidVisitorData(string v)
        {
            if (string.IsNullOrEmpty(v) || v.Length > 4000) return false;
            byte[] b;
            try
            {
                string s = (v.IndexOf('%') >= 0 ? Uri.UnescapeDataString(v) : v).Replace('-', '+').Replace('_', '/');
                s = s.TrimEnd('=');
                if (s.Length % 4 == 1) return false;
                s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
                b = Convert.FromBase64String(s);
            }
            catch (FormatException) { return false; }
            int i = 0;
            Func<long> varint = delegate
            {
                long r = 0; int sh = 0;
                while (true)
                {
                    if (i >= b.Length || sh > 63) throw new FormatException();
                    byte c = b[i++];
                    r |= (long)(c & 0x7F) << sh; sh += 7;
                    if (c < 0x80) return r;
                }
            };
            try
            {
                if (b.Length == 0) return false;
                while (i < b.Length)
                {
                    long tag = varint();
                    if (tag >> 3 == 0) return false;
                    switch ((int)(tag & 7))
                    {
                        case 0: varint(); break;
                        case 1: i += 8; break;
                        case 2: long n = varint(); if (n < 0 || n > b.Length) return false; i += (int)n; break;
                        case 5: i += 4; break;
                        default: return false;
                    }
                    if (i > b.Length) return false;
                }
                return true;
            }
            catch (FormatException) { return false; }
        }

        const string DefaultBrowserUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

        // "NOMBRE":"valor" en el JavaScript de la página (con los escapes de JSON resueltos), o null.
        static string JsonStringField(string html, string name)
        {
            Match m = Regex.Match(html, "\"" + Regex.Escape(name) + @"""\s*:\s*""((?:[^""\\]|\\.){1,4000})""");
            if (!m.Success) return null;
            try { return new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<string>("\"" + m.Groups[1].Value + "\""); }
            catch { return null; }
        }

        // El reproductor (base.js) de la página, o el actual de YouTube si la extensión no lo dio. Solo se baja si hace falta.
        class PlayerInfo
        {
            readonly YtSettings _s; readonly YtQuery _q; readonly IYtHttp _http;
            string _url, _js; int _sts;
            public Action<string> Status { get { return _q.Status; } }
            public PlayerInfo(YtSettings s, YtQuery q, IYtHttp http)
            {
                _s = s; _q = q; _http = http;
                _url = YouTubeSolver.AbsolutePlayerUrl(q.PlayerJsUrl);
                if (YouTubeSolver.PlayerId(_url) == null) _url = null;
                _sts = q.Sts;
            }

            Dictionary<string, string> Headers()
            {
                Dictionary<string, string> h = new Dictionary<string, string>();
                h["User-Agent"] = string.IsNullOrEmpty(_q.UserAgent) ? DefaultBrowserUa : _q.UserAgent;
                return h;
            }

            public string Url()
            {
                if (_url != null) return _url;
                // iframe_api nombra el reproductor actual: https://www.youtube.com/s/player/<id>/www-widgetapi.vflset/...
                string api = _http.Get("https://www.youtube.com/iframe_api", Headers());
                Match m = Regex.Match(api ?? "", @"player\\?/([0-9A-Za-z_-]{6,})\\?/");
                if (!m.Success) throw new YouTubeException("No se encontró el reproductor actual de YouTube.");
                _url = "https://www.youtube.com/s/player/" + m.Groups[1].Value + "/" + _s.PlayerJsPath;
                return _url;
            }

            public string Js(string url)
            {
                if (_js == null) _js = _http.Get(url, Headers());
                return _js;
            }

            public int Sts()
            {
                if (_sts > 0) return _sts;
                Match m = Regex.Match(Js(Url()) ?? "", @"(?:signatureTimestamp|sts)\s*:\s*([0-9]{5})");
                if (!m.Success) throw new YouTubeException("No se encontró la versión de firma (signatureTimestamp) del reproductor de YouTube.");
                _sts = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                return _sts;
            }
        }

        static readonly Regex NParam = new Regex("([?&])n=([^&]*)");

        // Firma y "n" de cada dirección, con el reproductor de la página (todo en una sola pasada del descifrador).
        static bool NeedsDecipher(YtVideo v)
        {
            foreach (YtFormat f in v.Formats) if (f.S != null || NParam.IsMatch(f.Url)) return true;
            return false;
        }

        static void Decipher(YtVideo v, PlayerInfo player)
        {
            List<string> ns = new List<string>(), sigs = new List<string>();
            foreach (YtFormat f in v.Formats)
            {
                Match m = NParam.Match(f.Url);
                if (m.Success) { string n = Uri.UnescapeDataString(m.Groups[2].Value); if (n.Length > 0 && !ns.Contains(n)) ns.Add(n); }
                if (f.S != null && !sigs.Contains(f.S)) sigs.Add(f.S);
            }
            if (ns.Count == 0 && sigs.Count == 0) return;
            Dictionary<string, string> nOut = new Dictionary<string, string>(), sigOut = new Dictionary<string, string>();
            string url;
            try { url = player.Url(); }
            catch (YouTubeException) { throw; }
            catch (Exception ex) { throw new YouTubeException("No se pudo encontrar el reproductor de YouTube: " + ex.Message); }
            YouTubeSolver.Solve(url, player.Js, ns, sigs, nOut, sigOut, player.Status);
            foreach (YtFormat f in v.Formats)
            {
                string u = f.Url;
                Match m = NParam.Match(u);
                string nn;
                if (m.Success && nOut.TryGetValue(Uri.UnescapeDataString(m.Groups[2].Value), out nn))
                    u = u.Substring(0, m.Index) + m.Groups[1].Value + "n=" + Uri.EscapeDataString(nn) + u.Substring(m.Index + m.Length);
                string sg;
                if (f.S != null)
                {
                    if (!sigOut.TryGetValue(f.S, out sg)) throw new YouTubeException("Falta la firma de un formato.");
                    u += (u.IndexOf('?') >= 0 ? "&" : "?") + (f.Sp ?? "signature") + "=" + Uri.EscapeDataString(sg);
                    f.S = null;
                }
                f.Url = u;
            }
        }

        // --- Respuesta de la API -> formatos ---

        static object Path_(Dictionary<string, object> d, params string[] keys)
        {
            object cur = d;
            foreach (string k in keys)
            {
                Dictionary<string, object> m = cur as Dictionary<string, object>;
                if (m == null || !m.TryGetValue(k, out cur)) return null;
            }
            return cur;
        }

        static long Long(object o)
        {
            if (o == null) return -1;
            long l;
            return long.TryParse(o.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out l) ? l : -1;
        }

        public static YtVideo ParseVideo(Dictionary<string, object> pr, string videoId)
        {
            YtVideo v = new YtVideo();
            v.Id = videoId;
            v.Title = Path_(pr, "videoDetails", "title") as string
                      ?? Path_(pr, "microformat", "playerMicroformatRenderer", "title", "simpleText") as string;
            long secs = Long(Path_(pr, "videoDetails", "lengthSeconds"));
            v.Duration = secs > 0 ? secs : 0;
            object live = Path_(pr, "videoDetails", "isLive");
            v.Live = live is bool && (bool)live;
            Dictionary<string, object> sd = Path_(pr, "streamingData") as Dictionary<string, object>;
            if (sd == null) return v;
            v.HlsUrl = Path_(sd, "hlsManifestUrl") as string;
            List<YtFormat> all = new List<YtFormat>();
            foreach (string arr in new string[] { "formats", "adaptiveFormats" })
            {
                object o;
                System.Collections.IEnumerable list = sd.TryGetValue(arr, out o) ? o as System.Collections.IEnumerable : null;
                if (list == null || o is string) continue;
                foreach (object x in list)
                {
                    YtFormat f = ParseFormat(x as Dictionary<string, object>, arr == "formats");
                    if (f != null) all.Add(f);
                }
            }
            if (v.Duration <= 0)
                foreach (object x in (sd.ContainsKey("adaptiveFormats") ? sd["adaptiveFormats"] as System.Collections.IEnumerable : null) ?? new object[0])
                {
                    long ms = Long(Path_(x as Dictionary<string, object> ?? new Dictionary<string, object>(), "approxDurationMs"));
                    if (ms > 0) { v.Duration = ms / 1000.0; break; }
                }
            // Audio con "DRC" (volumen comprimido) repetido con el mismo itag: se queda el normal.
            all.RemoveAll(delegate(YtFormat f)
            {
                return f.Drc && all.Exists(delegate(YtFormat g) { return !g.Drc && g.Itag == f.Itag && g.AudioTrackId == f.AudioTrackId; });
            });
            // Claves: el itag; si varias pistas de audio (idiomas) comparten itag, las que no son la de por defecto llevan ":<pista>".
            foreach (YtFormat f in all)
            {
                int same = 0;
                foreach (YtFormat g in all) if (g.Itag == f.Itag) same++;
                f.Key = f.Itag.ToString(CultureInfo.InvariantCulture);
                if (same > 1 && !f.DefaultAudio && f.AudioTrackId != null) f.Key += ":" + f.AudioTrackId;
            }
            // Dos con la misma clave (no debería pasar): se queda el primero.
            HashSet<string> seen = new HashSet<string>();
            foreach (YtFormat f in all) if (seen.Add(f.Key)) v.Formats.Add(f);
            return v;
        }

        static YtFormat ParseFormat(Dictionary<string, object> d, bool muxedList)
        {
            if (d == null) return null;
            if (d.ContainsKey("drmFamilies")) return null;                                // protegido (DRM)
            string type = d.ContainsKey("type") && d["type"] != null ? d["type"].ToString() : "";
            if (type == "FORMAT_STREAM_TYPE_OTF") return null;                            // por trozos: no es un archivo
            if (d.ContainsKey("targetDurationSec")) return null;                          // trozos de directo
            YtFormat f = new YtFormat();
            f.Itag = (int)Long(d.ContainsKey("itag") ? d["itag"] : null);
            if (f.Itag <= 0) return null;
            f.Url = d.ContainsKey("url") ? d["url"] as string : null;
            string cipher = d.ContainsKey("signatureCipher") ? d["signatureCipher"] as string : (d.ContainsKey("cipher") ? d["cipher"] as string : null);
            if (f.Url == null && cipher != null)
            {
                f.Url = QueryParam("?" + cipher, "url");
                f.S = QueryParam("?" + cipher, "s");
                f.Sp = QueryParam("?" + cipher, "sp") ?? "signature";
                if (f.S == null) return null;
            }
            if (string.IsNullOrEmpty(f.Url) || !(f.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                AllowPlainHttp && f.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))) return null;
            f.MimeType = d.ContainsKey("mimeType") ? d["mimeType"] as string ?? "" : "";
            Match m = Regex.Match(f.MimeType, "^(video|audio)/([0-9A-Za-z-]+)(?:;\\s*codecs=\"([^\"]*)\")?");
            if (!m.Success) return null;
            f.Container = m.Groups[2].Value.ToLowerInvariant() == "webm" ? "webm" : "mp4";
            f.Codecs = m.Groups[3].Value.Trim();
            bool video = m.Groups[1].Value == "video";
            int codecCount = f.Codecs.Length == 0 ? 0 : f.Codecs.Split(',').Length;
            f.HasVideo = video;
            f.HasAudio = !video || muxedList || codecCount > 1; // los de "formats" llevan siempre el audio
            f.Width = (int)Math.Max(0, Long(d.ContainsKey("width") ? d["width"] : null));
            f.Height = (int)Math.Max(0, Long(d.ContainsKey("height") ? d["height"] : null));
            f.Fps = (int)Math.Max(0, Long(d.ContainsKey("fps") ? d["fps"] : null));
            f.Bitrate = Math.Max(0, Long(d.ContainsKey("bitrate") ? d["bitrate"] : null));
            f.Size = Long(d.ContainsKey("contentLength") ? d["contentLength"] : null);
            f.SizeExact = f.Size > 0;
            if (f.Size <= 0)
            {
                long ms = Long(d.ContainsKey("approxDurationMs") ? d["approxDurationMs"] : null);
                f.Size = ms > 0 && f.Bitrate > 0 ? (long)(f.Bitrate / 8.0 * ms / 1000.0) : -1;
            }
            f.QualityLabel = d.ContainsKey("qualityLabel") ? d["qualityLabel"] as string : null;
            object drc;
            f.Drc = d.TryGetValue("isDrc", out drc) && drc is bool && (bool)drc;
            Dictionary<string, object> at = d.ContainsKey("audioTrack") ? d["audioTrack"] as Dictionary<string, object> : null;
            if (at != null)
            {
                f.AudioTrackId = at.ContainsKey("id") ? at["id"] as string : null;
                f.LanguageName = at.ContainsKey("displayName") ? at["displayName"] as string : null;
                object def;
                f.DefaultAudio = !at.TryGetValue("audioIsDefault", out def) || !(def is bool) || (bool)def;
                if (f.AudioTrackId != null) { int dot = f.AudioTrackId.IndexOf('.'); f.Language = dot > 0 ? f.AudioTrackId.Substring(0, dot) : f.AudioTrackId; }
            }
            return f;
        }

        // --- Lista de calidades para el panel (como la respuesta de "probe") ---

        static int CodecRank(YtFormat f)
        {
            string c = (f.Codecs ?? "").ToLowerInvariant();
            if (f.Container == "mp4") return c.StartsWith("avc1") ? 3 : c.StartsWith("av01") ? 2 : 1;      // H.264 es lo más compatible
            return c.StartsWith("vp9") || c.StartsWith("vp09.00") ? 3 : c.StartsWith("vp09") ? 2 : 1;      // vp09.02 = HDR
        }

        static YtFormat BestAudio(YtVideo v, string container, string trackId, bool defaultTrack)
        {
            YtFormat best = null;
            foreach (YtFormat f in v.Formats)
            {
                if (f.HasVideo || !f.HasAudio) continue;
                if (container != null && f.Container != container) continue;
                if (defaultTrack ? !f.DefaultAudio : f.AudioTrackId != trackId) continue;
                if (best == null || f.Bitrate > best.Bitrate) best = f;
            }
            return best;
        }

        public static List<object> Items(YtVideo v)
        {
            List<object> items = new List<object>();
            // Vídeo: el mejor de cada altura y contenedor (mp4 / webm).
            Dictionary<string, YtFormat> best = new Dictionary<string, YtFormat>();
            List<YtFormat> muxed = new List<YtFormat>();
            foreach (YtFormat f in v.Formats)
            {
                if (!f.HasVideo) continue;
                if (f.Muxed) { muxed.Add(f); continue; }
                string k = f.Height + "/" + f.Container;
                YtFormat cur;
                if (!best.TryGetValue(k, out cur)) { best[k] = f; continue; }
                int r = CodecRank(f).CompareTo(CodecRank(cur));
                if (r == 0) r = f.Fps.CompareTo(cur.Fps);
                if (r == 0) r = f.Bitrate.CompareTo(cur.Bitrate);
                if (r > 0) best[k] = f;
            }
            List<YtFormat> videos = new List<YtFormat>(best.Values);
            videos.Sort(delegate(YtFormat a, YtFormat b)
            {
                int r = b.Height.CompareTo(a.Height);
                if (r == 0) r = (a.Container == "mp4" ? 0 : 1).CompareTo(b.Container == "mp4" ? 0 : 1);
                return r;
            });
            muxed.Sort(delegate(YtFormat a, YtFormat b) { return b.Height.CompareTo(a.Height); });
            videos.AddRange(muxed);
            foreach (YtFormat f in videos)
            {
                YtFormat a = f.Muxed ? null : (BestAudio(v, f.Container, null, true) ?? BestAudio(v, null, null, true));
                Dictionary<string, object> o = new Dictionary<string, object>();
                o["kind"] = "video"; o["itag"] = f.Itag; o["video"] = f.Key; o["audio"] = a != null ? a.Key : null;
                o["width"] = f.Width; o["height"] = f.Height; o["fps"] = f.Fps; o["label"] = f.QualityLabel;
                o["bandwidth"] = f.Bitrate + (a != null ? a.Bitrate : 0);
                o["size"] = f.Size > 0 && (a == null || a.Size > 0) ? f.Size + (a != null ? a.Size : 0) : -1;
                o["ext"] = f.Ext;
                o["codecs"] = f.Codecs + (a != null && !string.IsNullOrEmpty(a.Codecs) ? ", " + a.Codecs : "");
                o["muxed"] = f.Muxed;
                items.Add(o);
            }
            // Solo audio: por cada pista (idioma), el mejor m4a y el mejor webm; primero la pista por defecto.
            List<string> tracks = new List<string>();
            foreach (YtFormat f in v.Formats)
                if (!f.HasVideo && f.HasAudio)
                {
                    string t = f.DefaultAudio ? "" : f.AudioTrackId ?? "";
                    if (!tracks.Contains(t)) tracks.Add(t);
                }
            tracks.Sort(delegate(string a, string b) { return (a.Length == 0 ? 0 : 1).CompareTo(b.Length == 0 ? 0 : 1); });
            foreach (string t in tracks)
                foreach (string cont in new string[] { "mp4", "webm" })
                {
                    YtFormat a = BestAudio(v, cont, t.Length == 0 ? null : t, t.Length == 0);
                    if (a == null) continue;
                    Dictionary<string, object> o = new Dictionary<string, object>();
                    o["kind"] = "audio"; o["itag"] = a.Itag; o["video"] = null; o["audio"] = a.Key;
                    o["language"] = a.Language; o["languageName"] = a.LanguageName; o["default"] = a.DefaultAudio;
                    o["bandwidth"] = a.Bitrate; o["size"] = a.Size;
                    o["ext"] = a.Ext; o["codecs"] = a.Codecs;
                    items.Add(o);
                }
            return items;
        }

        // Mensaje "ytinfo" de la extensión -> respuesta con la forma de "probe".
        public static string InfoReply(AppConfig cfg, string msg)
        {
            Dictionary<string, object> d;
            try { d = AppConfig.NewSerializer().Deserialize<Dictionary<string, object>>(msg); }
            catch { return Ipc.ErrorJson("Mensaje no válido."); }
            if (d == null) return Ipc.ErrorJson("Mensaje vacío.");
            YtQuery q = QueryFromMessage(d);
            if (!ValidId(q.VideoId)) return Ipc.ErrorJson("El identificador del vídeo de YouTube no es válido.");
            YtVideo v;
            try { v = Resolve(cfg, q, false); }
            catch (YouTubeException ex) { return Ipc.ErrorJson(ex.Message); }
            catch (Exception ex) { return Ipc.ErrorJson("Error al leer el vídeo de YouTube: " + ex.Message); }
            if (v.Live)
            {
                // Emisión en directo: las calidades de la lista HLS de YouTube, con la sonda de siempre. Se descarga igual
                // ("mediaKind":"youtube", "mediaVideo" = la clave de aquí) y se graba como las demás emisiones.
                string probe;
                try { probe = DownloadEngine.ProbeMedia(cfg, v.HlsUrl, null, null, v.UserAgent); }
                catch (Exception ex) { return Ipc.ErrorJson("No se pudo leer la emisión en directo: " + ex.Message); }
                return LiveJson(v, AppConfig.NewSerializer().Deserialize<Dictionary<string, object>>(probe));
            }
            if (v.WarmUp != null)
            {
                System.Threading.Thread t = new System.Threading.Thread(delegate() { v.WarmUp(); });
                t.IsBackground = false; // si el navegador cierra el canal, el host acaba de prepararlo antes de salir (~2 s)
                t.Name = "YouTube: preparar descifrado";
                t.Start();
            }
            return InfoJson(v);
        }

        // Clave estable de una calidad de la lista HLS de un directo (sus direcciones cambian cada vez que se pide):
        // el itag de la dirección (".../itag/96/..."), o "hls-<n>" por su posición.
        public static string LiveKey(string variantUrl, int index)
        {
            Match m = Regex.Match(variantUrl ?? "", "/itag/([0-9]{1,4})/");
            return m.Success ? m.Groups[1].Value : "hls-" + index.ToString(CultureInfo.InvariantCulture);
        }

        // La respuesta de la sonda sobre la lista HLS, con la forma de "ytinfo" y las claves estables.
        internal static string LiveJson(YtVideo v, Dictionary<string, object> p)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["ok"] = true; r["kind"] = Kind; r["videoId"] = v.Id; r["url"] = WatchUrl(v.Id);
            r["title"] = v.Title; r["duration"] = 0; r["live"] = true; r["protected"] = p.ContainsKey("protected") && true.Equals(p["protected"]);
            r["client"] = v.Client;
            List<object> items = new List<object>();
            int i = 0;
            System.Collections.IEnumerable src = p.ContainsKey("items") ? p["items"] as System.Collections.IEnumerable : null;
            foreach (object o in src ?? new object[0])
            {
                Dictionary<string, object> it = o as Dictionary<string, object>;
                if (it == null || !"video".Equals(it["kind"])) continue; // los directos de YouTube llevan el audio dentro
                string key = LiveKey(it["video"] as string, i++);
                it["video"] = key; it["audio"] = null;
                int itag; if (int.TryParse(key, out itag)) it["itag"] = itag;
                items.Add(it);
            }
            r["items"] = items;
            return AppConfig.NewSerializer().Serialize(r);
        }

        public static string InfoJson(YtVideo v)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["ok"] = true; r["kind"] = Kind; r["videoId"] = v.Id; r["url"] = WatchUrl(v.Id);
            r["title"] = v.Title; r["duration"] = Math.Round(v.Duration, 2);
            r["live"] = false; r["protected"] = false;
            r["client"] = v.Client;
            r["items"] = Items(v);
            return AppConfig.NewSerializer().Serialize(r);
        }

        public static YtQuery QueryFromMessage(Dictionary<string, object> d)
        {
            Func<string, string> get = delegate(string k) { object o; return d.TryGetValue(k, out o) && o != null && !(o is Dictionary<string, object>) ? o.ToString() : null; };
            YtQuery q = new YtQuery();
            q.VideoId = get("videoId");
            if (!ValidId(q.VideoId)) { string id; if (TryVideoId(get("url"), out id)) q.VideoId = id; }
            q.VisitorData = Clip(get("visitorData"), 2000);
            q.PlayerJsUrl = Clip(get("playerJsUrl"), 500);
            int sts; q.Sts = int.TryParse(get("sts") ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out sts) && sts > 0 ? sts : 0;
            object li; q.LoggedIn = d.TryGetValue("loggedIn", out li) && (li is bool ? (bool)li : "true".Equals(li as string, StringComparison.OrdinalIgnoreCase));
            q.Cookies = get("cookies"); if (q.Cookies == "") q.Cookies = null;
            q.UserAgent = get("userAgent"); if (q.UserAgent == "") q.UserAgent = null;
            q.Referrer = get("referrer");
            // Un reproductor que no es de youtube.com no se baja.
            if (q.PlayerJsUrl != null)
            {
                string abs = YouTubeSolver.AbsolutePlayerUrl(q.PlayerJsUrl);
                Uri u;
                if (!Uri.TryCreate(abs, UriKind.Absolute, out u) || u.Scheme != Uri.UriSchemeHttps ||
                    !(u.Host.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase) || u.Host.Equals("m.youtube.com", StringComparison.OrdinalIgnoreCase)) ||
                    YouTubeSolver.PlayerId(abs) == null)
                    q.PlayerJsUrl = null;
            }
            return q;
        }

        static string Clip(string s, int max) { return s == null || s.Length == 0 ? null : s.Length > max ? null : s; }
    }
}
