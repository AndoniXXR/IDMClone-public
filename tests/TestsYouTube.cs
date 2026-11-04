using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace IDMClone.Tests
{
    // Motor de YouTube (src\YouTube*.cs), sin red: la API de YouTube se sustituye por respuestas guardadas (FakeYt) y el
    // reproductor por uno de muestra, pequeño, con la misma forma que el base.js real (una función que construye la
    // dirección y llama a X.Y("alr","yes"), y un método que transforma la firma "s" y el "n"). Así se prueba el camino
    // entero: Jint + el descifrador ejs de verdad (dentro del exe), la caché en disco, el orden de los clientes,
    // la respuesta de "ytinfo" y la descarga con "mediaKind":"youtube".
    public static partial class TestMain
    {
        static void RunYouTube()
        {
            Run("youtube: id del vídeo en watch?v=, youtu.be, shorts, embed y live; lo demás no vale", YT_VideoId);
            Run("youtube: youtube.json de serie, editado (orden y un cliente nuevo) y dañado -> los de serie", YT_Settings);
            Run("youtube: descifrado de la firma y de n con un reproductor de muestra (Jint + ejs) y caché en disco", YT_Decipher);
            Run("youtube: orden de los clientes con respuestas guardadas (bot -> el siguiente, firma y n descifradas)", YT_ClientFallback);
            Run("youtube: con sesión (LOGIN_INFO + SAPISID) primero authedClients, con cookies y SAPISIDHASH", YT_AuthedOrder);
            Run("youtube: sin reproductor ni STS de la página -> iframe_api y signatureTimestamp del base.js", YT_PlayerDiscovery);
            Run("youtube: todos piden iniciar sesión -> mensaje para iniciar sesión en el navegador", YT_LoginRequired);
            Run("youtube: respuesta de ytinfo con la forma de probe (calidades, audio por idioma, sin DRM ni DRC)", YT_InfoShape);
            Run("youtube: ytinfo por el host con rid, sin abrir la ventana; errores con ok:false", YT_HostMessage);
            Run("youtube: mensaje download con mediaKind youtube -> descarga con los itag elegidos; lo no válido se rechaza", YT_DownloadCommand);
            Run("youtube: descarga vídeo + audio con direcciones nuevas; un 403 a mitad -> se vuelven a pedir", YT_DownloadReResolve);
            Run("youtube: getconfig anuncia features [\"ytinfo\"] detrás de los campos de siempre", YT_GetConfigFeatures);
            Run("youtube: ytinfo sin url ni reproductor no descifra (contesta enseguida) y deja el descifrado en la caché", YT_InfoNoDecipher);
            Run("youtube: directo -> calidades de la lista HLS con claves estables (itag de la dirección) y audio dentro", YT_LiveShape);
            Run("youtube: preparado rápido (Acornima + ejs sobre las candidatas) = ejs entero, y es el que se usa", YT_FastPrep);
            Run("youtube: visitorData recortado -> no se usa; se toma el de la página del vídeo", YT_VisitorData);
            Run("youtube: otro proceso prepara el reproductor -> la descarga espera (\"Preparando YouTube\") y no lo repite", YT_PrepMutex);
            Run("youtube: 403 con las direcciones de un cliente -> al volver a pedirlas, ese cliente va el último", YT_RetryOtherClient);
        }

        // --- Reproductor de muestra y API simulada ---

        const string YtPlayerUrl = "https://www.youtube.com/s/player/test0001/player_ias.vflset/en_US/base.js";

        const string YtSamplePlayer =
            "var _yt_player={};(function(g){var window=this;\n" +
            "var cfg={signatureTimestamp:20001};\n" +
            "var rev=function(s){return s.split(\"\").reverse().join(\"\")};\n" +
            "function U(u){this.p={};this.u=u}\n" +
            "U.prototype.set=function(k,v){this.p[k]=v};\n" +
            "U.prototype.get=function(k){return this.p[k]};\n" +
            "U.prototype.clone=function(){return this};\n" +
            "U.prototype.apply=function(){var n=this.p.n;if(n)this.p.n=\"N\"+rev(n);var s=this.p.s;if(s)this.p.s=encodeURIComponent(rev(decodeURIComponent(s)))};\n" +
            "g.cfg=function(a,b){return a+b};\n" +
            "g.mk=function(u,name,sig){var x=new U(u);g.cfg(\"alr\",\"yes\");if(sig)x.set(name,sig);return x};\n" +
            "})(_yt_player);\n";

        static string YtRev(string s) { char[] c = s.ToCharArray(); Array.Reverse(c); return new string(c); }

        class FakeYt : IYtHttp
        {
            public readonly List<string> Order = new List<string>();      // clientName (+deviceMake) de cada petición
            public readonly List<Dictionary<string, object>> Bodies = new List<Dictionary<string, object>>();
            public readonly List<IDictionary<string, string>> Headers = new List<IDictionary<string, string>>();
            public readonly List<string> Gets = new List<string>();
            public readonly List<IDictionary<string, string>> GetHeaders = new List<IDictionary<string, string>>();
            public Func<string, int, string> Respond;                      // (cliente, nº de petición de ese cliente) -> JSON
            public Dictionary<string, string> Pages = new Dictionary<string, string>();
            readonly Dictionary<string, int> _count = new Dictionary<string, int>();

            public string Post(string url, string body, IDictionary<string, string> headers)
            {
                Dictionary<string, object> b = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                Dictionary<string, object> c = (Dictionary<string, object>)((Dictionary<string, object>)b["context"])["client"];
                string id = c["clientName"] + (c.ContainsKey("deviceMake") ? "/" + c["deviceMake"] : "");
                lock (this)
                {
                    Order.Add(id); Bodies.Add(b); Headers.Add(new Dictionary<string, string>(headers));
                    int n; _count.TryGetValue(id, out n); _count[id] = ++n;
                    return Respond(id, n);
                }
            }

            public string Get(string url, IDictionary<string, string> headers)
            {
                lock (this) { Gets.Add(url); GetHeaders.Add(new Dictionary<string, string>(headers)); }
                string t;
                if (Pages.TryGetValue(url, out t)) return t;
                throw new YouTubeException("YouTube contestó 404 (Not Found).");
            }
        }

        const string YtVisionos = "VISIONOS/Apple", YtTvSamsung = "TVHTML5/Samsung";

        static string YtPr(string status, string reason, string formats, string adaptive)
        {
            return "{\"playabilityStatus\":{\"status\":\"" + status + "\"" + (reason != null ? ",\"reason\":\"" + reason + "\"" : "") + "}" +
                   (status == "OK" ? ",\"videoDetails\":{\"videoId\":\"abcdefghijk\",\"title\":\"Vídeo de prueba\",\"lengthSeconds\":\"212\",\"isLive\":false}" +
                                     ",\"streamingData\":{\"expiresInSeconds\":\"21540\",\"formats\":[" + formats + "],\"adaptiveFormats\":[" + adaptive + "]}" : "") + "}";
        }

        static string YtF(int itag, string mime, int w, int h, long bitrate, long len, string urlOrCipher, string extra)
        {
            return "{\"itag\":" + itag + "," + urlOrCipher + ",\"mimeType\":\"" + mime.Replace("\"", "\\\"") + "\"" +
                   (w > 0 ? ",\"width\":" + w + ",\"height\":" + h + ",\"fps\":30,\"qualityLabel\":\"" + h + "p\"" : "") +
                   ",\"bitrate\":" + bitrate + (len > 0 ? ",\"contentLength\":\"" + len + "\"" : "") + ",\"approxDurationMs\":\"212000\"" + (extra ?? "") + "}";
        }

        static string YtUrl(string u) { return "\"url\":\"" + u + "\""; }
        static string YtCipher(string s, string url) { return "\"signatureCipher\":\"s=" + Uri.EscapeDataString(s) + "&sp=sig&url=" + Uri.EscapeDataString(url) + "\""; }

        static void WithYt(FakeYt fake, Action body)
        {
            Func<AppConfig, IYtHttp> savedHttp = YouTube.HttpFactory;
            Func<YtSettings> savedSettings = YouTube.SettingsProvider;
            bool savedHttpOk = YouTube.AllowPlainHttp;
            string savedHome = AppConfig.HomeOverride;
            try
            {
                UseFreshHome("yt-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                YouTube.HttpFactory = delegate(AppConfig c) { return fake; };
                YouTube.SettingsProvider = YtSettings.Defaults;
                YouTubeSolver.ForgetLoaded();
                body();
            }
            finally
            {
                YouTube.HttpFactory = savedHttp; YouTube.SettingsProvider = savedSettings; YouTube.AllowPlainHttp = savedHttpOk;
                AppConfig.HomeOverride = savedHome;
            }
        }

        static YtQuery YtQ()
        {
            YtQuery q = new YtQuery();
            q.VideoId = "abcdefghijk"; q.VisitorData = "CgtWaXNpdG9yMTIzNA=="; q.PlayerJsUrl = "/s/player/test0001/player_ias.vflset/en_US/base.js"; q.Sts = 20001;
            return q;
        }

        // --- Pruebas ---

        static void YT_VideoId()
        {
            string id;
            foreach (string u in new string[] { "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "https://youtube.com/watch?feature=x&v=dQw4w9WgXcQ&t=10",
                                                "https://m.youtube.com/watch?v=dQw4w9WgXcQ", "https://youtu.be/dQw4w9WgXcQ?si=abc", "https://www.youtube.com/shorts/dQw4w9WgXcQ",
                                                "https://www.youtube.com/embed/dQw4w9WgXcQ", "https://www.youtube.com/live/dQw4w9WgXcQ?feature=share", "https://music.youtube.com/watch?v=dQw4w9WgXcQ" })
                Check(YouTube.TryVideoId(u, out id) && id == "dQw4w9WgXcQ", "No reconoce " + u);
            foreach (string u in new string[] { "https://www.youtube.com/watch?v=short", "https://example.com/watch?v=dQw4w9WgXcQ", "https://www.youtube.com/channel/UC123",
                                                "ftp://youtube.com/watch?v=dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXc$", "no es una dirección" })
                Check(!YouTube.TryVideoId(u, out id), "Aceptó " + u);
        }

        static void YT_Settings()
        {
            YtSettings d = YtSettings.Defaults();
            Check(string.Join(",", d.Clients.ToArray()) == "visionos,tv_samsung", "Orden de serie: " + string.Join(",", d.Clients.ToArray()));
            Check(d.Client("visionos").ClientNameId == 101 && !d.Client("visionos").RequireJsPlayer && !d.Client("visionos").SupportsCookies, "visionos de serie");
            Check(d.Client("tv_samsung").RequireJsPlayer && d.Client("tv_samsung").SupportsCookies && (string)d.Client("tv_samsung").Context["deviceMake"] == "Samsung", "tv_samsung de serie");
            string savedHome = AppConfig.HomeOverride;
            try
            {
                UseFreshHome("ytcfg-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                YtSettings s = YtSettings.Load();
                Check(File.Exists(YtSettings.FilePath), "No se creó youtube.json para poder editarlo");
                Check(s.Clients.Count == 2, "Load sin archivo");
                // Editado: otro orden y un cliente nuevo; los que no se nombran siguen con los de serie.
                File.WriteAllText(YtSettings.FilePath, "{\"clients\":[\"nuevo\",\"visionos\"],\"definitions\":{\"nuevo\":{\"clientName\":\"NUEVO\",\"clientVersion\":\"9.9\",\"clientNameId\":99,\"userAgent\":\"UA nuevo\",\"requireJsPlayer\":true,\"context\":{\"osName\":\"X\"}}}}");
                s = YtSettings.Load();
                Check(string.Join(",", s.Clients.ToArray()) == "nuevo,visionos", "Orden editado: " + string.Join(",", s.Clients.ToArray()));
                Check(s.Client("nuevo") != null && s.Client("nuevo").ClientNameId == 99 && s.Client("nuevo").RequireJsPlayer && !s.Client("nuevo").SupportsCookies, "Cliente nuevo");
                Check(s.Client("visionos") != null && s.Client("visionos").ClientVersion == "1.02", "Los de serie deben seguir");
                Check(string.Join(",", s.AuthedClients.ToArray()) == "tv_samsung,visionos", "authedClients de serie si no se dan");
                File.WriteAllText(YtSettings.FilePath, "{esto no es json");
                s = YtSettings.Load();
                Check(s.Clients.Count == 2 && s.Client("tv_samsung") != null, "Dañado -> los de serie");
            }
            finally { AppConfig.HomeOverride = savedHome; }
        }

        static void YT_Decipher()
        {
            WithYt(new FakeYt(), delegate
            {
                int fetches = 0;
                Func<string, string> fetch = delegate(string u) { fetches++; Check(u == YtPlayerUrl, "Pidió otro reproductor: " + u); return YtSamplePlayer; };
                Dictionary<string, string> nOut = new Dictionary<string, string>(), sOut = new Dictionary<string, string>();
                int pre = YouTubeSolver.PreprocessCount;
                string sig = "AOq0QJ8wRAIgXy%Z=/+zA";
                YouTubeSolver.Solve(YtPlayerUrl, fetch, new string[] { "abcDEF123", "zz" }, new string[] { sig }, nOut, sOut);
                Check(nOut["abcDEF123"] == "N" + YtRev("abcDEF123") && nOut["zz"] == "Nzz", "n: " + string.Join(",", new List<string>(nOut.Values).ToArray()));
                Check(sOut[sig] == YtRev(sig), "firma: " + sOut[sig]);
                Check(fetches == 1 && YouTubeSolver.PreprocessCount == pre + 1, "Debe bajar y preprocesar una vez");
                string[] cache = Directory.GetFiles(YouTubeSolver.CacheDir, "test0001-*.js");
                Check(cache.Length == 1, "No quedó el reproductor preprocesado en la caché");
                // Otra vez en el mismo proceso: el motor en memoria.
                nOut.Clear();
                YouTubeSolver.Solve(YtPlayerUrl, fetch, new string[] { "q1" }, null, nOut, sOut);
                Check(nOut["q1"] == "N1q" && fetches == 1, "Segunda vez en memoria");
                // Proceso nuevo (motor olvidado): sale de la caché del disco, sin bajar ni preprocesar.
                YouTubeSolver.ForgetLoaded();
                nOut.Clear();
                Func<string, string> noFetch = delegate(string u) { throw new Exception("no debe bajar el reproductor"); };
                YouTubeSolver.Solve(YtPlayerUrl, noFetch, new string[] { "xy" }, null, nOut, sOut);
                Check(nOut["xy"] == "Nyx" && YouTubeSolver.PreprocessCount == pre + 1, "Desde la caché del disco");
                // Un reproductor que no es un base.js: error claro (YouTubeException), no una excepción de Jint.
                YouTubeSolver.ForgetLoaded();
                bool failed = false;
                try { YouTubeSolver.Solve("https://www.youtube.com/s/player/bad00001/player_ias.vflset/en_US/base.js", delegate(string u) { return "var x = 1;"; }, new string[] { "a" }, null, nOut, sOut); }
                catch (YouTubeException ex) { failed = ex.Message.Length > 0; }
                Check(failed, "Un reproductor sin las funciones debe dar YouTubeException");
            });
        }

        static void YT_ClientFallback()
        {
            FakeYt fake = new FakeYt();
            string vUrl = "https://rr1.googlevideo.com/videoplayback?itag=137&n=nnVID&sig=ok1&id=x";
            string aUrl = "https://rr1.googlevideo.com/videoplayback?itag=140&n=nnAUD&id=x";
            fake.Respond = delegate(string client, int n)
            {
                if (client == YtVisionos) return YtPr("LOGIN_REQUIRED", "Sign in to confirm you’re not a bot", "", "");
                return YtPr("OK", null, "", YtF(137, "video/mp4; codecs=\"avc1.640028\"", 1920, 1080, 4000000, 50000000, YtUrl(vUrl), null) + "," +
                                         YtF(140, "audio/mp4; codecs=\"mp4a.40.2\"", 0, 0, 130000, 3000000, YtCipher("SIGabc=/1", aUrl), null));
            };
            fake.Pages[YtPlayerUrl] = YtSamplePlayer;
            WithYt(fake, delegate
            {
                YtQuery q = YtQ(); q.Cookies = "PREF=f1"; // sin sesión: orden normal; solo tv_samsung recibe cookies
                YtVideo v = YouTube.Resolve(null, q);
                Check(string.Join(",", fake.Order.ToArray()) == YtVisionos + "," + YtTvSamsung, "Orden: " + string.Join(",", fake.Order.ToArray()));
                Check(v.Client == "tv_samsung" && v.Title == "Vídeo de prueba" && v.Duration == 212, "Vídeo: " + v.Client + " " + v.Title + " " + v.Duration);
                Dictionary<string, object> cpc0 = (Dictionary<string, object>)((Dictionary<string, object>)fake.Bodies[0]["playbackContext"])["contentPlaybackContext"];
                Dictionary<string, object> cpc1 = (Dictionary<string, object>)((Dictionary<string, object>)fake.Bodies[1]["playbackContext"])["contentPlaybackContext"];
                Check(!cpc0.ContainsKey("signatureTimestamp") && Convert.ToInt32(cpc1["signatureTimestamp"]) == 20001, "signatureTimestamp solo para tv_samsung");
                Check(fake.Headers[0]["X-Goog-Visitor-Id"] == "CgtWaXNpdG9yMTIzNA==" && (string)((Dictionary<string, object>)((Dictionary<string, object>)fake.Bodies[0]["context"])["client"])["visitorData"] == "CgtWaXNpdG9yMTIzNA==", "visitorData");
                Check(fake.Headers[0]["X-YouTube-Client-Name"] == "101" && fake.Headers[1]["X-YouTube-Client-Name"] == "7", "X-YouTube-Client-Name");
                Check(!fake.Headers[0].ContainsKey("Cookie") && fake.Headers[1]["Cookie"] == "PREF=f1", "Cookies solo al cliente que las admite");
                Check(!fake.Headers[1].ContainsKey("Authorization"), "Sin SAPISID no hay Authorization");
                YtFormat fv = v.Find("137"), fa = v.Find("140");
                Check(fv.Url == "https://rr1.googlevideo.com/videoplayback?itag=137&n=NDIVnn&sig=ok1&id=x", "n del vídeo: " + fv.Url);
                Check(fa.Url == aUrl.Replace("n=nnAUD", "n=NDUAnn") + "&sig=" + Uri.EscapeDataString(YtRev("SIGabc=/1")), "firma y n del audio: " + fa.Url);
                Check(fake.Gets.Count == 1 && fake.Gets[0] == YtPlayerUrl, "Debe bajar el reproductor de la página una vez: " + string.Join(",", fake.Gets.ToArray()));
            });
        }

        static void YT_AuthedOrder()
        {
            FakeYt fake = new FakeYt();
            fake.Respond = delegate(string client, int n)
            {
                return YtPr("OK", null, "", YtF(140, "audio/mp4; codecs=\"mp4a.40.2\"", 0, 0, 130000, 3000000, YtUrl("https://rr1.googlevideo.com/videoplayback?itag=140&id=x"), null));
            };
            WithYt(fake, delegate
            {
                YtQuery q = YtQ(); q.Cookies = "LOGIN_INFO=AFmmF2s; SAPISID=abc/def; __Secure-3PAPISID=p3"; q.LoggedIn = true;
                YtVideo v = YouTube.Resolve(null, q);
                Check(fake.Order[0] == YtTvSamsung && v.Client == "tv_samsung", "Con sesión primero tv_samsung: " + string.Join(",", fake.Order.ToArray()));
                string auth = fake.Headers[0]["Authorization"];
                Check(auth.StartsWith("SAPISIDHASH ") && auth.Contains(" SAPISID3PHASH "), "Authorization: " + auth);
                Check(fake.Headers[0]["Cookie"] == q.Cookies && fake.Headers[0]["X-Origin"] == "https://www.youtube.com" && fake.Headers[0]["X-Youtube-Bootstrap-Logged-In"] == "true", "Cabeceras de sesión");
                // SAPISIDHASH = <ts>_sha1("<ts> <SAPISID> <origen>")
                string a = YouTube.SidAuthorization("SAPISID=abc", "https://www.youtube.com", 1700000000);
                Check(a == "SAPISIDHASH 1700000000_" + Sha1Hex("1700000000 abc https://www.youtube.com"), "SAPISIDHASH: " + a);
                Check(YouTube.CookiesLoggedIn(q.Cookies) && !YouTube.CookiesLoggedIn("SAPISID=abc") && !YouTube.CookiesLoggedIn(null), "CookiesLoggedIn");
            });
        }

        static string Sha1Hex(string s)
        {
            using (System.Security.Cryptography.SHA1 h = System.Security.Cryptography.SHA1.Create())
                return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", "").ToLowerInvariant();
        }

        static void YT_PlayerDiscovery()
        {
            FakeYt fake = new FakeYt();
            string base2 = "https://www.youtube.com/s/player/ffff0000/player_ias.vflset/en_US/base.js";
            fake.Pages["https://www.youtube.com/iframe_api"] = "var scriptUrl = 'https:\\/\\/www.youtube.com\\/s\\/player\\/ffff0000\\/www-widgetapi.vflset\\/www-widgetapi.js';";
            fake.Pages[base2] = YtSamplePlayer;
            fake.Respond = delegate(string client, int n)
            {
                if (client == YtVisionos) return YtPr("UNPLAYABLE", "Video unavailable for this client", "", "");
                return YtPr("OK", null, "", YtF(140, "audio/mp4; codecs=\"mp4a.40.2\"", 0, 0, 130000, 3000000, YtUrl("https://rr1.googlevideo.com/videoplayback?itag=140&n=abc&id=x"), null));
            };
            WithYt(fake, delegate
            {
                YtQuery q = YtQ(); q.PlayerJsUrl = null; q.Sts = 0;
                YtVideo v = YouTube.Resolve(null, q);
                Dictionary<string, object> cpc = (Dictionary<string, object>)((Dictionary<string, object>)fake.Bodies[1]["playbackContext"])["contentPlaybackContext"];
                Check(Convert.ToInt32(cpc["signatureTimestamp"]) == 20001, "signatureTimestamp del base.js");
                Check(v.Find("140").Url.Contains("n=Ncba"), "n con el reproductor encontrado: " + v.Find("140").Url);
                Check(fake.Gets.Count == 2 && fake.Gets[0].EndsWith("/iframe_api") && fake.Gets[1] == base2, "Peticiones: " + string.Join(",", fake.Gets.ToArray()));
                // Un reproductor que no es de youtube.com en el mensaje no se usa.
                Dictionary<string, object> m = new Dictionary<string, object>();
                m["videoId"] = "abcdefghijk"; m["playerJsUrl"] = "https://evil.example/s/player/x/base.js"; m["sts"] = "20001"; m["loggedIn"] = true;
                YtQuery mq = YouTube.QueryFromMessage(m);
                Check(mq.PlayerJsUrl == null && mq.Sts == 20001 && mq.LoggedIn, "QueryFromMessage");
            });
        }

        static void YT_LoginRequired()
        {
            FakeYt fake = new FakeYt();
            fake.Respond = delegate(string client, int n) { return YtPr("LOGIN_REQUIRED", "Sign in to confirm your age", "", ""); };
            fake.Pages[YtPlayerUrl] = YtSamplePlayer;
            WithYt(fake, delegate
            {
                string msg = null;
                try { YouTube.Resolve(null, YtQ()); } catch (YouTubeException ex) { msg = ex.Message; }
                Check(msg != null && msg.Contains("iniciar sesión") && msg.Contains("Sign in to confirm your age"), "Mensaje: " + msg);
                Check(fake.Order.Count == 2, "Debe probar los dos clientes");
                // Emisión que aún no empieza: se dice tal cual, sin probar más.
                fake.Respond = delegate(string client, int n) { return YtPr("LIVE_STREAM_OFFLINE", "Premieres in 2 hours", "", ""); };
                msg = null;
                try { YouTube.Resolve(null, YtQ()); } catch (YouTubeException ex) { msg = ex.Message; }
                Check(msg != null && msg.Contains("aún no ha empezado"), "Directo sin empezar: " + msg);
            });
        }

        static string YtRichResponse(string vUrl, string aUrl)
        {
            string fmt = YtF(18, "video/mp4; codecs=\"avc1.42001E, mp4a.40.2\"", 640, 360, 500000, 0, YtUrl(vUrl + "&i=18"), null);
            string ad = string.Join(",", new string[] {
                YtF(137, "video/mp4; codecs=\"avc1.640028\"", 1920, 1080, 4000000, 50000000, YtUrl(vUrl + "&i=137"), null),
                YtF(399, "video/mp4; codecs=\"av01.0.08M.08\"", 1920, 1080, 2000000, 25000000, YtUrl(vUrl + "&i=399"), null),
                YtF(248, "video/webm; codecs=\"vp9\"", 1920, 1080, 3000000, 40000000, YtUrl(vUrl + "&i=248"), null),
                YtF(136, "video/mp4; codecs=\"avc1.4d401f\"", 1280, 720, 2000000, 20000000, YtUrl(vUrl + "&i=136"), null),
                YtF(999, "video/mp4; codecs=\"avc1.640028\"", 3840, 2160, 9000000, 90000000, YtUrl(vUrl + "&i=999"), ",\"drmFamilies\":[\"WIDEVINE\"]"),
                YtF(998, "video/mp4; codecs=\"avc1.640028\"", 2560, 1440, 9000000, 90000000, YtUrl(vUrl + "&i=998"), ",\"type\":\"FORMAT_STREAM_TYPE_OTF\""),
                YtF(140, "audio/mp4; codecs=\"mp4a.40.2\"", 0, 0, 130000, 3000000, YtUrl(aUrl + "&i=140en"), ",\"audioTrack\":{\"id\":\"en.4\",\"displayName\":\"English original\",\"audioIsDefault\":true}"),
                YtF(140, "audio/mp4; codecs=\"mp4a.40.2\"", 0, 0, 130500, 3000500, YtUrl(aUrl + "&i=140drc"), ",\"isDrc\":true,\"audioTrack\":{\"id\":\"en.4\",\"displayName\":\"English original\",\"audioIsDefault\":true}"),
                YtF(140, "audio/mp4; codecs=\"mp4a.40.2\"", 0, 0, 129000, 2900000, YtUrl(aUrl + "&i=140es"), ",\"audioTrack\":{\"id\":\"es.3\",\"displayName\":\"Spanish\",\"audioIsDefault\":false}"),
                YtF(251, "audio/webm; codecs=\"opus\"", 0, 0, 160000, 3500000, YtUrl(aUrl + "&i=251en"), ",\"audioTrack\":{\"id\":\"en.4\",\"displayName\":\"English original\",\"audioIsDefault\":true}"),
                YtF(249, "audio/webm; codecs=\"opus\"", 0, 0, 50000, 1000000, YtUrl(aUrl + "&i=249en"), ",\"audioTrack\":{\"id\":\"en.4\",\"displayName\":\"English original\",\"audioIsDefault\":true}")
            });
            return YtPr("OK", null, fmt, ad);
        }

        static void YT_InfoShape()
        {
            FakeYt fake = new FakeYt();
            fake.Respond = delegate(string client, int n) { return YtRichResponse("https://rr1.googlevideo.com/videoplayback?v=1", "https://rr1.googlevideo.com/videoplayback?a=1"); };
            WithYt(fake, delegate
            {
                string reply = YouTube.InfoReply(null, "{\"type\":\"ytinfo\",\"rid\":3,\"videoId\":\"abcdefghijk\",\"visitorData\":\"CgtWaXNpdG9yMTIzNA==\"}");
                Dictionary<string, object> r = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reply);
                Check(true.Equals(r["ok"]) && (string)r["kind"] == "youtube" && (string)r["title"] == "Vídeo de prueba" && Convert.ToDouble(r["duration"]) == 212, "Cabecera: " + reply);
                Check(false.Equals(r["live"]) && false.Equals(r["protected"]) && (string)r["videoId"] == "abcdefghijk" && (string)r["url"] == "https://www.youtube.com/watch?v=abcdefghijk", "live/protected/url");
                List<string> got = new List<string>();
                foreach (object o in (System.Collections.ArrayList)r["items"])
                {
                    Dictionary<string, object> it = (Dictionary<string, object>)o;
                    got.Add(it["kind"] + ":" + it["video"] + "+" + (it["audio"] ?? "null") + it["ext"]);
                    foreach (string k in new string[] { "kind", "itag", "audio", "bandwidth", "size", "ext" }) Check(it.ContainsKey(k), "Falta " + k + " en " + new JavaScriptSerializer().Serialize(it));
                    if ((string)it["kind"] == "video")
                    {
                        foreach (string k in new string[] { "video", "width", "height", "codecs" }) Check(it.ContainsKey(k), "Falta " + k);
                        Check(it["video"].ToString() == it["itag"].ToString(), "video = itag");
                    }
                    else Check(it.ContainsKey("language"), "Falta language");
                    if (it["video"] as string == "137")
                    {
                        Check(Convert.ToInt64(it["size"]) == 53000000 && Convert.ToInt64(it["bandwidth"]) == 4130000 && Convert.ToInt32(it["height"]) == 1080 && Convert.ToInt32(it["width"]) == 1920, "Tamaño del 1080p mp4: " + new JavaScriptSerializer().Serialize(it));
                        Check((string)it["codecs"] == "avc1.640028, mp4a.40.2", "codecs: " + it["codecs"]);
                    }
                    if (it["video"] as string == "18") Check(Convert.ToInt64(it["size"]) == 500000L / 8 * 212 && true.Equals(it["muxed"]), "Tamaño del muxed por bitrate: " + it["size"]);
                    if (it["audio"] as string == "140:es.3") Check((string)it["language"] == "es" && (string)it["languageName"] == "Spanish", "Idioma");
                }
                string expect = "video:137+140.mp4|video:248+251.webm|video:136+140.mp4|video:18+null.mp4|audio:+140.m4a|audio:+251.webm|audio:+140:es.3.m4a";
                Check(string.Join("|", got.ToArray()) == expect, "Calidades:\n  " + string.Join("|", got.ToArray()) + "\nesperado\n  " + expect);
                // El audio DRC repetido se descarta: el 140 es el normal.
                YtVideo v = YouTube.Resolve(null, YtQ());
                Check(v.Find("140").Url.EndsWith("&i=140en") && v.Find("140:es.3").Url.EndsWith("&i=140es") && v.Find("999") == null && v.Find("998") == null, "Find");
                Check(v.Find("140:fr.1") == null && v.Find("") == null, "Find de lo que no hay");
            });
        }

        static void YT_HostMessage()
        {
            FakeYt fake = new FakeYt();
            fake.Respond = delegate(string client, int n) { return YtRichResponse("https://rr1.googlevideo.com/videoplayback?v=1", "https://rr1.googlevideo.com/videoplayback?a=1"); };
            WithYt(fake, delegate
            {
                string reply = NativeHost.HandleMessage("{\"type\":\"ytinfo\",\"rid\":41,\"videoId\":\"abcdefghijk\",\"visitorData\":\"V\",\"playerJsUrl\":\"/s/player/test0001/player_ias.vflset/en_US/base.js\",\"sts\":20001,\"loggedIn\":false,\"userAgent\":\"Mozilla/5.0\"}");
                Check(reply.StartsWith("{\"rid\":41,\"ok\":true,\"kind\":\"youtube\""), "Respuesta del host: " + reply.Substring(0, Math.Min(200, reply.Length)));
                reply = NativeHost.HandleMessage("{\"type\":\"ytinfo\",\"rid\":42,\"videoId\":\"corto\"}");
                Check(reply.StartsWith("{\"rid\":42,\"ok\":false,\"error\":"), "Id no válido: " + reply);
                fake.Respond = delegate(string client, int n) { return "{\"error\":{\"code\":400,\"message\":\"Precondition check failed.\"}}"; };
                reply = NativeHost.HandleMessage("{\"type\":\"ytinfo\",\"rid\":\"x\",\"videoId\":\"abcdefghijk\"}");
                Check(reply.StartsWith("{\"rid\":\"x\",\"ok\":false,\"error\":") && reply.Contains("Precondition check failed"), "Error de la API: " + reply);
                // Los mensajes de siempre no cambian (probe sin URL válida sigue contestando como antes).
                reply = NativeHost.HandleMessage("{\"type\":\"probe\",\"rid\":43,\"url\":\"no\"}");
                Check(reply.StartsWith("{\"rid\":43,\"ok\":false,\"error\":") && reply.Contains("URL no v"), "probe: " + reply);
            });
        }

        static void YT_GetConfigFeatures()
        {
            string r = NativeHost.ConfigReply();
            Check(r.StartsWith("{\"ok\":true,\"exts\""), "El registro del host depende de este comienzo: " + r.Substring(0, Math.Min(40, r.Length)));
            Dictionary<string, object> d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(r);
            System.Collections.ArrayList f = d["features"] as System.Collections.ArrayList;
            Check(f != null && f.Contains("ytinfo"), "features: " + r);
            List<string> keys = new List<string>(d.Keys);
            Check(keys.IndexOf("features") > keys.IndexOf("selectionPanel") && keys.IndexOf("features") > keys.IndexOf("mediaTypes"), "features debe ir detrás de los campos de la 1.4");
        }

        static void YT_InfoNoDecipher()
        {
            FakeYt fake = new FakeYt();
            string watch = "https://www.youtube.com/watch?v=abcdefghijk&hl=en&bpctr=9999999999&has_verified=1";
            fake.Pages[watch] = "<script>ytcfg.set({\"STS\":20001,\"VISITOR_DATA\":\"Vk\",\"PLAYER_JS_URL\":\"\\/s\\/player\\/test0001\\/player_ias.vflset\\/en_US\\/base.js\"});</script>";
            fake.Pages[YtPlayerUrl] = YtSamplePlayer;
            fake.Respond = delegate(string client, int n)
            {
                if (client == YtVisionos) return YtPr("LOGIN_REQUIRED", "Sign in to confirm you’re not a bot", "", "");
                return YtPr("OK", null, "", YtF(140, "audio/mp4; codecs=\"mp4a.40.2\"", 0, 0, 130000, 3000000, YtCipher("SIGabc", "https://rr1.googlevideo.com/videoplayback?itag=140&n=nn1&id=x"), null));
            };
            WithYt(fake, delegate
            {
                int pre = YouTubeSolver.PreprocessCount;
                // Como lo manda la extensión 2.0: sin "url" y con playerJsUrl vacío.
                string reply = NativeHost.HandleMessage("{\"type\":\"ytinfo\",\"rid\":5,\"videoId\":\"abcdefghijk\",\"visitorData\":\"\",\"playerJsUrl\":\"\",\"sts\":0,\"loggedIn\":false,\"cookies\":\"\",\"userAgent\":\"Mozilla/5.0\",\"referrer\":\"https://www.youtube.com/watch?v=abcdefghijk\"}");
                Check(reply.StartsWith("{\"rid\":5,\"ok\":true,\"kind\":\"youtube\"") && reply.Contains("\"audio\":\"140\""), "Respuesta: " + reply);
                Check(string.Join(",", fake.Order.ToArray()) == YtVisionos + "," + YtTvSamsung, "Orden: " + string.Join(",", fake.Order.ToArray()));
                // El descifrado se prepara aparte y queda en la caché del disco para la descarga.
                DateTime limit = DateTime.Now.AddSeconds(30);
                while (DateTime.Now < limit && (!Directory.Exists(YouTubeSolver.CacheDir) || Directory.GetFiles(YouTubeSolver.CacheDir, "test0001-*.js").Length == 0)) System.Threading.Thread.Sleep(50);
                Check(Directory.Exists(YouTubeSolver.CacheDir) && Directory.GetFiles(YouTubeSolver.CacheDir, "test0001-*.js").Length == 1 && YouTubeSolver.PreprocessCount == pre + 1, "No dejó el reproductor preparado en la caché");
            });
        }

        static void YT_LiveShape()
        {
            Check(YouTube.LiveKey("https://manifest.googlevideo.com/api/manifest/hls_playlist/expire/1/ei/x/itag/96/source/yt_live_broadcast/file/index.m3u8", 0) == "96", "itag de la dirección");
            Check(YouTube.LiveKey("https://example.com/v1.m3u8", 3) == "hls-3", "sin itag: por posición");
            YtVideo v = new YtVideo(); v.Id = "abcdefghijk"; v.Title = "Directo"; v.Live = true; v.Client = "visionos";
            string probe = "{\"ok\":true,\"kind\":\"hls\",\"url\":\"https://m/x.m3u8\",\"duration\":0,\"live\":true,\"protected\":false,\"items\":[" +
                "{\"kind\":\"video\",\"video\":\"https://m/itag/96/a.m3u8\",\"audio\":null,\"width\":1920,\"height\":1080,\"bandwidth\":5000000,\"size\":-1,\"ext\":\".mp4\",\"codecs\":\"avc1.640028,mp4a.40.2\"}," +
                "{\"kind\":\"video\",\"video\":\"https://m/itag/94/a.m3u8\",\"audio\":null,\"width\":854,\"height\":480,\"bandwidth\":1000000,\"size\":-1,\"ext\":\".mp4\",\"codecs\":\"avc1.4d401f,mp4a.40.2\"}],\"children\":[]}";
            string r = YouTube.LiveJson(v, new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(probe));
            Dictionary<string, object> d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(r);
            Check((string)d["kind"] == "youtube" && true.Equals(d["live"]) && (string)d["url"] == "https://www.youtube.com/watch?v=abcdefghijk" && !d.ContainsKey("children"), "Cabecera: " + r);
            System.Collections.ArrayList items = (System.Collections.ArrayList)d["items"];
            Dictionary<string, object> first = (Dictionary<string, object>)items[0];
            Check(items.Count == 2 && (string)first["video"] == "96" && Convert.ToInt32(first["itag"]) == 96 && first["audio"] == null && Convert.ToInt32(first["height"]) == 1080, "Calidades: " + r);
            AddOptions o = new AddOptions(); o.Url = "https://www.youtube.com/watch?v=abcdefghijk"; o.MediaVideo = "hls-2"; o.MediaAudio = "-";
            Check(MainForm.YouTubeDownloadOptions(o) == null, "La clave hls-N de un directo debe aceptarse");
        }

        static void YT_FastPrep()
        {
            WithYt(new FakeYt(), delegate
            {
                YouTubeLibs.Ensure();
                string lib = YouTubeLibs.ReadEjs("yt.solver.lib.js"), core = YouTubeLibs.ReadEjs("yt.solver.core.js");
                Type yj = typeof(YouTube).Assembly.GetType("IDMClone.YtJint");
                string pp = null; object fast = null, full = null; Exception err = null;
                // Jint necesita una pila grande (como en YouTubeSolver).
                System.Threading.Thread t = new System.Threading.Thread(delegate()
                {
                    try
                    {
                        pp = (string)yj.GetMethod("FastPreprocess").Invoke(null, new object[] { YtSamplePlayer, lib, core, 60 });
                        fast = yj.GetMethod("FromPreprocessed").Invoke(null, new object[] { pp, 60 });
                        object[] a = { YtSamplePlayer, lib, core, 60, null };
                        full = yj.GetMethod("Preprocess").Invoke(null, a);
                        MethodInfo call = yj.GetMethod("Call");
                        foreach (string kind in new string[] { "n", "sig" })
                            foreach (string c in new string[] { "abc123XYZ", "Zz-_09" })
                                Check((string)call.Invoke(fast, new object[] { kind, c }) == (string)call.Invoke(full, new object[] { kind, c }), kind + " distinto para " + c);
                    }
                    catch (Exception ex) { err = ex is TargetInvocationException ? ex.InnerException : ex; }
                }, 64 * 1024 * 1024);
                t.Start(); t.Join();
                if (err != null) throw err;
                Check(pp.Contains("_result.n") && pp.Contains("U.prototype.apply=function()"), "El preparado debe llevar el texto original y las funciones de ejs");
                // El que usa la descarga.
                Dictionary<string, string> nOut = new Dictionary<string, string>(), sOut = new Dictionary<string, string>();
                YouTubeSolver.Solve(YtPlayerUrl, delegate(string u) { return YtSamplePlayer; }, new string[] { "q1" }, null, nOut, sOut);
                Check(nOut["q1"] == "N1q" && YouTubeSolver.LastPreprocessFast, "La descarga debe usar el camino rápido");
                // Un reproductor con otra forma: el camino rápido no vale (null) y se usa el de siempre.
                string other = "(function(){var g=this;" + YtSamplePlayer.Substring(YtSamplePlayer.IndexOf("var cfg")).Replace("})(_yt_player);", "}).call(this);").Replace("(function(g){var window=this;", "");
                YouTubeSolver.ForgetLoaded();
                nOut.Clear();
                YouTubeSolver.Solve("https://www.youtube.com/s/player/other001/player_ias.vflset/en_US/base.js", delegate(string u) { return other; }, new string[] { "q2" }, null, nOut, sOut);
                Check(nOut["q2"] == "N2q", "Primera forma de ejs (.call(this)): " + nOut["q2"]);
            });
        }

        static void YT_VisitorData()
        {
            Check(YouTube.ValidVisitorData("CgtWaXNpdG9yMTIzNA==") && YouTube.ValidVisitorData("CgtWaXNpdG9yMTIzNA%3D%3D") && YouTube.ValidVisitorData("CgNhYmMolgEyAggB"), "Enteros");
            Check(!YouTube.ValidVisitorData("CgtWaXNpdG9y") && !YouTube.ValidVisitorData("CgNhYmMolgEyAg") && !YouTube.ValidVisitorData("no*es*base64") && !YouTube.ValidVisitorData(""), "Recortados o no válidos");
            FakeYt fake = new FakeYt();
            string watch = "https://www.youtube.com/watch?v=abcdefghijk&hl=en&bpctr=9999999999&has_verified=1";
            fake.Pages[watch] = "<script>ytcfg.set({\"VISITOR_DATA\":\"CgNhYmMolgEyAggB\"});</script>";
            fake.Respond = delegate(string client, int n) { return YtPr("OK", null, "", YtF(140, "audio/mp4; codecs=\"mp4a.40.2\"", 0, 0, 130000, 3000000, YtUrl("https://rr1.googlevideo.com/videoplayback?itag=140&id=x"), null)); };
            WithYt(fake, delegate
            {
                YtQuery q = YtQ(); q.VisitorData = "CgtWaXNpdG9yMTIzNA==".Substring(0, 12); // recortado
                YouTube.Resolve(null, q);
                Check(fake.Gets.Count == 1 && fake.Gets[0] == watch && fake.Headers[0]["X-Goog-Visitor-Id"] == "CgNhYmMolgEyAggB", "Debe usar el de la página: " + fake.Headers[0]["X-Goog-Visitor-Id"]);
            });
        }

        static void YT_PrepMutex()
        {
            FakeYt fake = new FakeYt();
            string watch = "https://www.youtube.com/watch?v=abcdefghijk&hl=en&bpctr=9999999999&has_verified=1";
            fake.Pages[watch] = "<script>ytcfg.set({\"STS\":20001,\"VISITOR_DATA\":\"CgNhYmMolgEyAggB\",\"PLAYER_JS_URL\":\"\\/s\\/player\\/test0001\\/player_ias.vflset\\/en_US\\/base.js\"});</script>";
            fake.Pages[YtPlayerUrl] = YtSamplePlayer;
            string idA = NewId("ytm");
            long aSize = 100 * 1024 + 3;
            fake.Respond = delegate(string client, int n)
            {
                return YtPr("OK", null, "", YtF(140, "audio/mp4; codecs=\"mp4a.40.2\"", 0, 0, 130000, aSize, YtUrl(S.Url("range", "a.bin", aSize, idA) + "&n=abc"), null));
            };
            WithYt(fake, delegate
            {
                YouTube.AllowPlainHttp = true;
                YouTube.SettingsProvider = delegate { YtSettings s = YtSettings.Defaults(); s.Clients = new List<string> { "tv_samsung" }; return s; };
                // El reproductor preparado, como lo dejaría el otro proceso.
                Dictionary<string, string> nOut = new Dictionary<string, string>(), sOut = new Dictionary<string, string>();
                YouTubeSolver.Solve(YtPlayerUrl, delegate(string u) { return YtSamplePlayer; }, new string[] { "x" }, null, nOut, sOut);
                string file = Directory.GetFiles(YouTubeSolver.CacheDir, "test0001-*.js")[0];
                string pp = File.ReadAllText(file);
                File.Delete(file);
                YouTubeSolver.ForgetLoaded();
                int pre = YouTubeSolver.PreprocessCount;
                string key = Path.GetFileNameWithoutExtension(file);
                // "Otro proceso" (un hilo con el mutex del reproductor) lo está preparando.
                System.Threading.ManualResetEvent held = new System.Threading.ManualResetEvent(false), release = new System.Threading.ManualResetEvent(false);
                System.Threading.Thread other = new System.Threading.Thread(delegate()
                {
                    using (System.Threading.Mutex m = new System.Threading.Mutex(false, "IDMClone-YouTube-" + key))
                    {
                        m.WaitOne(); held.Set();
                        release.WaitOne(30000);
                        File.WriteAllText(file, pp);
                        m.ReleaseMutex();
                    }
                });
                other.IsBackground = true; other.Start();
                Check(held.WaitOne(10000), "El hilo no tomó el mutex");
                DownloadManager mg = NewMgr();
                DownloadItem it = AddMedia(mg, "https://www.youtube.com/watch?v=abcdefghijk", YouTube.Kind, "Audio.m4a", null, "140", true);
                string seen = null;
                DateTime limit = DateTime.Now.AddSeconds(20);
                while (DateTime.Now < limit && it.Status != DownloadStatus.Error && it.Status != DownloadStatus.Complete)
                {
                    string st = ListColumns.StatusText(it);
                    if (st.Contains("Preparando YouTube")) { seen = st; break; }
                    System.Threading.Thread.Sleep(20);
                }
                release.Set();
                Check(seen == "Preparando YouTube (solo la primera vez)...", "La lista debe decir que prepara YouTube; estado " + it.Status + " " + it.Error + " / " + ListColumns.StatusText(it));
                WaitEnd(it, 60);
                Check(it.Status == DownloadStatus.Complete, "Estado " + it.Status + ": " + it.Error);
                Check(YouTubeSolver.PreprocessCount == pre, "No debe preparar el reproductor otra vez: lo hizo el otro proceso");
            });
        }

        static void YT_RetryOtherClient()
        {
            FakeYt fake = new FakeYt();
            fake.Pages[YtPlayerUrl] = YtSamplePlayer;
            string watch = "https://www.youtube.com/watch?v=abcdefghijk&hl=en&bpctr=9999999999&has_verified=1";
            fake.Pages[watch] = "<script>ytcfg.set({\"STS\":20001,\"VISITOR_DATA\":\"CgNhYmMolgEyAggB\",\"PLAYER_JS_URL\":\"\\/s\\/player\\/test0001\\/player_ias.vflset\\/en_US\\/base.js\"});</script>";
            long aSize = 150 * 1024 + 9;
            string idA = NewId("ytr");
            fake.Respond = delegate(string client, int n)
            {
                // Las de visionos dan 403; las de tv_samsung (con n que descifrar) sirven.
                string u = client == YtVisionos ? S.Url("forbidden", "a.bin", aSize, idA) : S.Url("range", "a.bin", aSize, idA) + "&n=abc";
                return YtPr("OK", null, "", YtF(140, "audio/mp4; codecs=\"mp4a.40.2\"", 0, 0, 130000, aSize, YtUrl(u), null));
            };
            WithYt(fake, delegate
            {
                YouTube.AllowPlainHttp = true;
                DownloadItem it = AddMedia(NewMgr(), "https://www.youtube.com/watch?v=abcdefghijk", YouTube.Kind, "Audio.m4a", null, "140", true);
                WaitEnd(it, 60);
                Check(it.Status == DownloadStatus.Complete, "Estado " + it.Status + ": " + it.Error);
                Check(string.Join(",", fake.Order.ToArray()) == YtVisionos + "," + YtTvSamsung, "Orden: " + string.Join(",", fake.Order.ToArray()));
            });
        }

        static void YT_DownloadCommand()
        {
            // Normalización y validación (sin ventana).
            AddOptions o = new AddOptions(); o.Url = "https://youtu.be/abcdefghijk?t=3"; o.MediaKind = YouTube.Kind; o.MediaVideo = "137"; o.MediaAudio = "140:es.3";
            Check(MainForm.YouTubeDownloadOptions(o) == null && o.Url == "https://www.youtube.com/watch?v=abcdefghijk" && o.FileName == "YouTube abcdefghijk.mp4", "Normalizada: " + o.Url + " " + o.FileName);
            o = new AddOptions(); o.Url = "https://www.youtube.com/watch?v=abcdefghijk"; o.MediaAudio = "251";
            Check(MainForm.YouTubeDownloadOptions(o) == null && o.FileName == "YouTube abcdefghijk.m4a", "Solo audio: " + o.FileName);
            foreach (string[] bad in new string[][] { new string[] { "https://example.com/watch?v=abcdefghijk", "137", "140" }, new string[] { "https://www.youtube.com/watch?v=abcdefghijk", "13x", "140" },
                                                      new string[] { "https://www.youtube.com/watch?v=abcdefghijk", "137", "../x" }, new string[] { "https://www.youtube.com/watch?v=abcdefghijk", null, "-" } })
            {
                o = new AddOptions(); o.Url = bad[0]; o.MediaVideo = bad[1]; o.MediaAudio = bad[2];
                Check(MainForm.YouTubeDownloadOptions(o) != null, "Aceptó " + string.Join(" ", bad));
            }
            // Por HandleJsonCommand: la descarga queda con mediaKind youtube y los itag; YouTube (simulado) pide sesión -> error claro.
            FakeYt fake = new FakeYt();
            fake.Respond = delegate(string client, int n) { return YtPr("LOGIN_REQUIRED", "Sign in to confirm your age", "", ""); };
            // La descarga no trae los datos de la página: IDMClone lee la página del vídeo (VISITOR_DATA, PLAYER_JS_URL, STS).
            string watch = "https://www.youtube.com/watch?v=abcdefghijk&hl=en&bpctr=9999999999&has_verified=1";
            fake.Pages[watch] = "<meta name=\"title\" content=\"Ana &amp; Luis\"><script>ytcfg.set({\"STS\":20001,\"VISITOR_DATA\":\"Q2d0VmlzaXRvcg%3D%3D\",\"PLAYER_JS_URL\":\"\\/s\\/player\\/test0001\\/player_ias.vflset\\/en_US\\/base.js\"});</script>";
            fake.Pages[YtPlayerUrl] = YtSamplePlayer;
            WithYt(fake, delegate
            {
                MainForm f = NewMainForm();
                Action<string> savedNotice = MainForm.NoticeHook;
                try
                {
                    MainForm.NoticeHook = delegate(string t) { };
                    DownloadManager m = Field<DownloadManager>(f, "_mgr");
                    MethodInfo h = typeof(MainForm).GetMethod("HandleJsonCommand", BindingFlags.NonPublic | BindingFlags.Instance);
                    string reply = (string)h.Invoke(f, new object[] { "{\"type\":\"download\",\"url\":\"https://www.youtube.com/watch?v=abcdefghijk&list=PL1\",\"filename\":\"Mi vídeo.mp4\",\"mediaKind\":\"youtube\",\"mediaVideo\":\"137\",\"mediaAudio\":\"140\",\"cookies\":\"PREF=1\",\"userAgent\":\"Mozilla/5.0 Prueba\"}" });
                    Check(reply.Contains("\"ok\":true"), "Respuesta: " + reply);
                    DownloadItem it = null;
                    foreach (DownloadItem x in m.Snapshot()) if (x.MediaKind == YouTube.Kind) it = x;
                    Check(it != null && it.Url == "https://www.youtube.com/watch?v=abcdefghijk" && it.MediaVideo == "137" && it.MediaAudio == "140" && it.FileName == "Mi vídeo.mp4", "Descarga: " + (it == null ? "no está" : it.Url + " " + it.MediaVideo + "/" + it.MediaAudio + " " + it.FileName));
                    WaitEnd(it, 30);
                    Check(it.Status == DownloadStatus.Error && (it.Error ?? "").Contains("iniciar sesión"), "Estado " + it.Status + ": " + it.Error);
                    Check(fake.Order.Count == 2 && fake.Headers[1]["Cookie"] == "PREF=1", "Debe pedir las direcciones al descargar, con las cookies del navegador: " + string.Join(",", fake.Order.ToArray()));
                    Check(fake.Gets.Count >= 1 && fake.Gets[0] == watch && fake.GetHeaders[0]["Cookie"] == "PREF=1" && fake.GetHeaders[0]["User-Agent"] == "Mozilla/5.0 Prueba", "Página del vídeo: " + string.Join(",", fake.Gets.ToArray()));
                    Check(fake.Headers[0]["X-Goog-Visitor-Id"] == "Q2d0VmlzaXRvcg==", "visitorData de la página (sin %3D): " + fake.Headers[0]["X-Goog-Visitor-Id"]);
                    Dictionary<string, object> cpc = (Dictionary<string, object>)((Dictionary<string, object>)fake.Bodies[1]["playbackContext"])["contentPlaybackContext"];
                    Check(Convert.ToInt32(cpc["signatureTimestamp"]) == 20001, "STS de la página");
                    YtQuery pq = new YtQuery(); pq.VideoId = "abcdefghijk";
                    YouTube.FillFromWatchPage(pq, fake);
                    Check(pq.PageTitle == "Ana & Luis" && pq.Sts == 20001 && pq.PlayerJsUrl == "/s/player/test0001/player_ias.vflset/en_US/base.js", "Datos de la página: " + pq.PageTitle + " " + pq.PlayerJsUrl);
                    reply = (string)h.Invoke(f, new object[] { "{\"type\":\"download\",\"url\":\"https://www.youtube.com/watch?v=abcdefghijk\",\"mediaKind\":\"youtube\",\"mediaVideo\":\"<b>\"}" });
                    Check(reply.Contains("\"ok\":false"), "Formato no válido aceptado: " + reply);
                }
                finally { MainForm.NoticeHook = savedNotice; CloseOpenForms(typeof(ProgressForm)); DisposeMainForm(f); }
            });
        }

        static void YT_DownloadReResolve()
        {
            FakeYt fake = new FakeYt();
            long vSize = 3 * 1024 * 1024 + 77, aSize = 200 * 1024 + 5;
            string idV = NewId("ytv"), idA = NewId("yta");
            fake.Respond = delegate(string client, int n)
            {
                // La primera vez el vídeo da 403 (dirección caducada); la segunda, una dirección buena.
                string v = S.Url(n == 1 ? "forbidden" : "range", "v.bin", vSize, idV);
                string a = S.Url("range", "a.bin", aSize, idA);
                return YtPr("OK", null, "", YtF(137, "video/mp4; codecs=\"avc1.640028\"", 1920, 1080, 4000000, vSize, YtUrl(v), null) + "," +
                                         YtF(140, "audio/mp4; codecs=\"mp4a.40.2\"", 0, 0, 130000, aSize, YtUrl(a), null));
            };
            WithYt(fake, delegate
            {
                YouTube.AllowPlainHttp = true;
                DownloadManager m = NewMgr();
                DownloadItem it = AddMedia(m, "https://www.youtube.com/watch?v=abcdefghijk", YouTube.Kind, "Clip.mp4", "137", "140", true);
                WaitEnd(it, 60);
                Check(it.Status == DownloadStatus.Complete, "Estado " + it.Status + ": " + it.Error);
                Check(fake.Order.Count == 2 && fake.Order[0] == YtVisionos && fake.Order[1] == YtVisionos, "Debe volver a pedir las direcciones tras el 403: " + string.Join(",", fake.Order.ToArray()));
                // Datos de prueba (no son MP4): vídeo y audio quedan por separado, cada uno entero.
                Check(File.Exists(it.FilePath) && new FileInfo(it.FilePath).Length == vSize, "Vídeo: " + it.FilePath + " " + (File.Exists(it.FilePath) ? new FileInfo(it.FilePath).Length : -1));
                string audio = Path.Combine(Path.GetDirectoryName(it.FilePath), Path.GetFileNameWithoutExtension(it.FilePath) + " (audio).bin");
                Check(File.Exists(audio) && new FileInfo(audio).Length == aSize, "Audio aparte: " + audio);
            });
        }
    }
}
