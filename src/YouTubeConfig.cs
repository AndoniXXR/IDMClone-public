using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace IDMClone
{
    // Un cliente de la API de YouTube (Innertube): con qué nombre, versión y navegador se presenta IDMClone al pedir
    // /youtubei/v1/player. YouTube los cambia a menudo; por eso están en youtube.json (junto a config.json) y se pueden
    // corregir sin recompilar. Si el archivo falta o no se puede leer, valen los de serie (DefaultJson).
    public class YtClient
    {
        public string Id;
        public string ClientName, ClientVersion, UserAgent;
        public int ClientNameId;                    // cabecera X-YouTube-Client-Name
        public Dictionary<string, object> Context = new Dictionary<string, object>(); // más campos de context.client
        public bool RequireJsPlayer;                // sus direcciones llevan firma o "n" que descifrar (y pide signatureTimestamp)
        public bool SupportsCookies;                // se le mandan las cookies de la sesión del navegador
    }

    public class YtSettings
    {
        public List<string> Clients = new List<string>();        // orden sin sesión iniciada
        public List<string> AuthedClients = new List<string>();  // orden con la sesión del navegador (cookies)
        public Dictionary<string, YtClient> Definitions = new Dictionary<string, YtClient>(StringComparer.OrdinalIgnoreCase);
        public string ApiUrl = "https://www.youtube.com/youtubei/v1/player?prettyPrint=false";
        public string PlayerJsPath = "player_ias.vflset/en_US/base.js"; // variante del reproductor si la página no dice cuál
        public string Hl = "en";

        public static string FilePath { get { return Path.Combine(AppConfig.Dir, "youtube.json"); } }

        // Valores de serie (clientes de yt-dlp, finales de 2026): visionos no necesita descifrar nada ni token PO,
        // pero no admite cookies ni vídeos "para niños"; tv_samsung necesita descifrar la firma y "n" y admite cookies.
        public const string DefaultJson =
@"{
  ""_info"": ""Clientes de la API de YouTube que usa IDMClone, en orden. Si YouTube cambia algo se pueden corregir aquí sin recompilar; borra este archivo para volver a los valores de serie."",
  ""version"": 1,
  ""clients"": [""visionos"", ""tv_samsung""],
  ""authedClients"": [""tv_samsung"", ""visionos""],
  ""apiUrl"": ""https://www.youtube.com/youtubei/v1/player?prettyPrint=false"",
  ""playerJsPath"": ""player_ias.vflset/en_US/base.js"",
  ""hl"": ""en"",
  ""definitions"": {
    ""visionos"": {
      ""clientName"": ""VISIONOS"",
      ""clientVersion"": ""1.02"",
      ""clientNameId"": 101,
      ""userAgent"": ""Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15"",
      ""requireJsPlayer"": false,
      ""supportsCookies"": false,
      ""context"": { ""deviceMake"": ""Apple"", ""deviceModel"": ""RealityDevice17,1"", ""osName"": ""visionOS"", ""osVersion"": ""26.5.23O471"" }
    },
    ""tv_samsung"": {
      ""clientName"": ""TVHTML5"",
      ""clientVersion"": ""5.20260707"",
      ""clientNameId"": 7,
      ""userAgent"": ""Mozilla/5.0 (SMART-TV; Linux; Tizen 2.4.0) AppleWebKit/538.1 (KHTML, like Gecko) Version/2.4.0 TV Safari/538.1"",
      ""requireJsPlayer"": true,
      ""supportsCookies"": true,
      ""context"": { ""deviceMake"": ""Samsung"", ""deviceModel"": ""UKS9800"", ""osName"": ""Tizen"", ""osVersion"": ""2.4.0"" }
    },
    ""tv"": {
      ""clientName"": ""TVHTML5"",
      ""clientVersion"": ""7.20260707.07.00"",
      ""clientNameId"": 7,
      ""userAgent"": ""Mozilla/5.0 (ChromiumStylePlatform) Cobalt/25.lts.30.1034943-gold (unlike Gecko), Unknown_TV_Unknown_0/Unknown (Unknown, Unknown)"",
      ""requireJsPlayer"": true,
      ""supportsCookies"": true,
      ""context"": {}
    }
  }
}
";

        public static YtSettings Defaults() { return Parse(DefaultJson, null); }

        // youtube.json (y si no existe, se crea con los de serie para poder editarlo).
        public static YtSettings Load()
        {
            YtSettings def = Defaults();
            string p = FilePath;
            try
            {
                if (!File.Exists(p))
                {
                    try { Directory.CreateDirectory(AppConfig.Dir); File.WriteAllText(p, DefaultJson, new UTF8Encoding(false)); } catch { }
                    return def;
                }
                return Parse(File.ReadAllText(p, Encoding.UTF8), def);
            }
            catch (Exception ex)
            {
                NativeHost.Log("youtube: youtube.json no válido (" + ex.Message + "): se usan los clientes de serie");
                return def;
            }
        }

        // Lee el JSON; lo que falte se toma de "fallback" (los de serie). Excepción si no es JSON.
        public static YtSettings Parse(string json, YtSettings fallback)
        {
            Dictionary<string, object> d = AppConfig.NewSerializer().Deserialize<Dictionary<string, object>>(json);
            if (d == null) throw new FormatException("vacío");
            YtSettings s = new YtSettings();
            if (fallback != null)
            {
                s.ApiUrl = fallback.ApiUrl; s.PlayerJsPath = fallback.PlayerJsPath; s.Hl = fallback.Hl;
                foreach (KeyValuePair<string, YtClient> kv in fallback.Definitions) s.Definitions[kv.Key] = kv.Value;
            }
            string str;
            if ((str = Str(d, "apiUrl")) != null && str.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s.ApiUrl = str;
            if ((str = Str(d, "playerJsPath")) != null) s.PlayerJsPath = str.TrimStart('/');
            if ((str = Str(d, "hl")) != null) s.Hl = str;
            object o;
            Dictionary<string, object> defs = d.TryGetValue("definitions", out o) ? o as Dictionary<string, object> : null;
            if (defs != null)
                foreach (KeyValuePair<string, object> kv in defs)
                {
                    Dictionary<string, object> c = kv.Value as Dictionary<string, object>;
                    if (c == null) continue;
                    YtClient cl = new YtClient();
                    cl.Id = kv.Key;
                    cl.ClientName = Str(c, "clientName");
                    cl.ClientVersion = Str(c, "clientVersion");
                    cl.UserAgent = Str(c, "userAgent");
                    int n; object v;
                    if (c.TryGetValue("clientNameId", out v) && v != null && int.TryParse(v.ToString(), out n)) cl.ClientNameId = n;
                    cl.RequireJsPlayer = c.TryGetValue("requireJsPlayer", out v) && v is bool && (bool)v;
                    cl.SupportsCookies = c.TryGetValue("supportsCookies", out v) && v is bool && (bool)v;
                    Dictionary<string, object> ctx = c.TryGetValue("context", out v) ? v as Dictionary<string, object> : null;
                    if (ctx != null) foreach (KeyValuePair<string, object> f in ctx) cl.Context[f.Key] = f.Value;
                    if (string.IsNullOrEmpty(cl.ClientName) || string.IsNullOrEmpty(cl.ClientVersion)) continue;
                    s.Definitions[cl.Id] = cl;
                }
            s.Clients = List(d, "clients", fallback != null ? fallback.Clients : null);
            s.AuthedClients = List(d, "authedClients", fallback != null ? fallback.AuthedClients : null);
            return s;
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v is string && ((string)v).Length > 0 ? (string)v : null;
        }

        static List<string> List(Dictionary<string, object> d, string k, List<string> fallback)
        {
            List<string> r = new List<string>();
            object v;
            System.Collections.IEnumerable arr = d.TryGetValue(k, out v) && !(v is string) ? v as System.Collections.IEnumerable : null;
            if (arr != null) foreach (object x in arr) if (x is string && ((string)x).Length > 0) r.Add((string)x);
            if (r.Count == 0 && fallback != null) r.AddRange(fallback);
            return r;
        }

        public YtClient Client(string id)
        {
            YtClient c;
            return id != null && Definitions.TryGetValue(id, out c) ? c : null;
        }
    }
}
