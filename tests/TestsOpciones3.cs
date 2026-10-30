using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace IDMClone.Tests
{
    // Opciones que antes solo se guardaban (área 3: integración con el navegador): ver scratchpad\opciones-adorno.md.
    // Las pruebas de la extensión están en browser\tests\extension.test.js (node --test).
    public static partial class TestMain
    {
        static void RunOpciones3()
        {
            Run("opción 3: host: getconfig manda las opciones 1.4 después de exts (#1 #2 #8 #9 #10 #11)", O3_ConfigReply);
            Run("opción 3: host: captura automática rechazada en un navegador desmarcado (#1)", O3_CaptureOffRejects);
            Run("opción 3: tipos del panel: intérprete común, ventana y protegidos desmarcado por defecto (#8 #10)", O3_MediaTypes);
            Run("opción 3: host: proxy del navegador, solo host:puerto válido (#17)", O3_BrowserProxy);
            Run("opción 3: cookies del navegador: GUI -> host -> extensión -> GUI (#18)", O3_CookieBridgeRoundTrip);
            Run("opción 3: cookies del navegador: varios navegadores, respuestas ajenas y tiempo agotado (#18)", O3_CookieBridgeLogic);
            Run("opción 3: cookies del navegador: solo habla con IDMClone (#18)", O3_CookieBridgeTrust);
            Run("panel IDM: lista de serie igual que la de IDM 6.43 (orden, MP3 50 KB, OGG 100 KB, todos marcados, + opus y flac)", PI_DefaultTable);
            Run("panel IDM: getconfig manda la lista de IDM y mínimos generales 0", PI_ConfigReply);
            Run("panel IDM: config antigua 1024/300 sin lista pasa a la de IDM; lista o mínimos propios se conservan", PI_Upgrade);
            Run("panel IDM: Opciones muestra MP3 50 KB, OGG 100 KB y los demás en blanco", PI_Dialog);
        }

        static Dictionary<string, object> O3_Json(string s) { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(s); }
        static List<object> O3_List(object o) { List<object> r = new List<object>(); foreach (object x in (System.Collections.IEnumerable)o) r.Add(x); return r; }

        static void O3_ConfigReply()
        {
            UseFreshHome("o3-config");
            string saved = NativeHost.Browser, savedExt = NativeHost.ExtensionFolder;
            try
            {
                AppConfig c = Cfg();
                c.CaptureBrowsersOff = "Opera;brave";
                c.BrowserMenuDownload = true; c.BrowserMenuDownloadAll = false; c.FirefoxMenuDownload = false; c.FirefoxMenuDownloadAll = true;
                c.MediaPanelTypes = "flv mp3:50 -mp4";
                c.MediaNoAutoCapture = true; c.MediaPanelProtected = false;
                c.SelectionPanelMode = 2; c.SelectionPanelMini = true; c.SelectionPanelSites = "mediafire.com *.mega.nz";
                c.Save();

                NativeHost.Browser = "Brave";
                string r = NativeHost.ConfigReply();
                Check(r.StartsWith("{\"ok\":true,\"exts\""), "Las claves nuevas deben ir después de exts: " + r);
                Dictionary<string, object> d = O3_Json(r);
                Check((string)d["browser"] == "Brave" && (bool)d["captureOff"], "Brave está desmarcado (sin distinguir mayúsculas): " + r);
                Dictionary<string, object> mc = (Dictionary<string, object>)d["menuChrome"], mf = (Dictionary<string, object>)d["menuFirefox"];
                Check((bool)mc["link"] && !(bool)mc["all"] && !(bool)mf["link"] && (bool)mf["all"], "Menú: " + r);
                List<object> types = O3_List(d["mediaTypes"]);
                Check(types.Count == 3, "Tipos: " + r);
                Dictionary<string, object> mp3 = (Dictionary<string, object>)types[1], mp4 = (Dictionary<string, object>)types[2];
                Check((string)mp3["ext"] == "mp3" && (int)mp3["minKB"] == 50 && (bool)mp3["on"], "mp3:50: " + r);
                Check((string)mp4["ext"] == "mp4" && !(bool)mp4["on"], "-mp4: " + r);
                Check((bool)d["mediaNoAutoCapture"] && !(bool)d["mediaProtected"], "#9/#10: " + r);
                Dictionary<string, object> sp = (Dictionary<string, object>)d["selectionPanel"];
                Check((int)sp["mode"] == 2 && (bool)sp["mini"] && O3_List(sp["sites"]).Count == 2, "Selección: " + r);
                // Siguen las claves de siempre (extensiones 1.3).
                Check(d.ContainsKey("exclude") && d.ContainsKey("mediaPanel") && d.ContainsKey("minVideoKB"), "Faltan claves antiguas: " + r);

                NativeHost.Browser = "Google Chrome";
                Check(!(bool)O3_Json(NativeHost.ConfigReply())["captureOff"], "Chrome sí captura");
                NativeHost.Browser = "?";
                Check(!(bool)O3_Json(NativeHost.ConfigReply())["captureOff"], "Navegador desconocido: se captura");
                NativeHost.Browser = null;
                Check((string)O3_Json(NativeHost.ConfigReply())["browser"] == "?", "Sin host: ?");

                // Con rid, y el registro sigue reconociéndola como configuración (no la vuelca entera).
                string h = NativeHost.HandleMessage("{\"type\":\"getconfig\",\"rid\":12,\"v\":\"1.4\",\"caps\":[\"getcookies\"]}");
                Check(h.StartsWith("{\"rid\":12,\"ok\":true,\"exts\""), "rid: " + h);
                string log = File.ReadAllText(Path.Combine(AppConfig.Dir, "host.log"), Encoding.UTF8);
                Check(log.Contains("-> getconfig: {ok:true, configuración}"), "Registro: " + log);

                // Configuración antigua sin las claves nuevas: valores por defecto (sin lista -> los de serie).
                File.WriteAllText(AppConfig.ConfigPath, "{\"MaxConnections\":8}");
                Dictionary<string, object> o = O3_Json(NativeHost.ConfigReply());
                Check(O3_List(o["mediaTypes"]).Count == AppConfig.MediaTypesOrder.Length, "Tipos de serie");
                Check(!(bool)o["mediaProtected"] && !(bool)o["mediaNoAutoCapture"] && (int)((Dictionary<string, object>)o["selectionPanel"])["mode"] == 0, "Valores de IDM");
                Dictionary<string, object> m2 = (Dictionary<string, object>)o["menuChrome"];
                Check((bool)m2["link"] && !(bool)m2["all"], "Menú por defecto como IDM: todos los enlaces desmarcado");

                // Versión de la extensión de la carpeta de IDMClone (para que la cargada a mano se recargue tras /update).
                string extDir = Path.Combine(Home, "o3-ext-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                Directory.CreateDirectory(extDir);
                NativeHost.ExtensionFolder = extDir;
                Check(!O3_Json(NativeHost.ConfigReply()).ContainsKey("extVersion"), "Sin manifest.json no se manda");
                File.WriteAllText(Path.Combine(extDir, "manifest.json"), "{\"manifest_version\":3,\"version\":\"1.7\"}");
                CheckEq("1.7", (string)O3_Json(NativeHost.ConfigReply())["extVersion"], "extVersion");
                File.WriteAllText(Path.Combine(extDir, "manifest.json"), "{roto");
                Check(!O3_Json(NativeHost.ConfigReply()).ContainsKey("extVersion"), "manifest.json roto: no se manda");
            }
            finally { NativeHost.Browser = saved; NativeHost.ExtensionFolder = savedExt; }
        }

        static void O3_CaptureOffRejects()
        {
            UseFreshHome("o3-off");
            string saved = NativeHost.Browser;
            try
            {
                AppConfig c = Cfg(); c.CaptureBrowsersOff = "Brave"; c.Save();
                NativeHost.Browser = "Brave";
                string r = NativeHost.HandleMessage("{\"type\":\"download\",\"auto\":true,\"url\":\"http://127.0.0.1:1/x.zip\",\"rid\":4}");
                Check(r.StartsWith("{\"rid\":4,\"ok\":false") && r.Contains("Brave"), "Debe rechazarse: " + r);
                Check(NativeHost.CaptureOff(c, "brave") && !NativeHost.CaptureOff(c, "Microsoft Edge") && !NativeHost.CaptureOff(c, "?") && !NativeHost.CaptureOff(null, "Brave"), "CaptureOff");
            }
            finally { NativeHost.Browser = saved; }
        }

        static void O3_MediaTypes()
        {
            UseFreshHome("o3-types");
            AppConfig d = AppConfig.CreateDefault();
            Check(!d.MediaPanelProtected, "Archivos protegidos: desmarcado por defecto, como IDM");
            List<AppConfig.MediaTypeRule> def = AppConfig.ParseMediaTypes(d);
            Check(def.Count == AppConfig.MediaTypesOrder.Length && def.TrueForAll(delegate(AppConfig.MediaTypeRule x) { return x.On; }), "De serie: todos marcados");
            Check(def[1].Ext == "mp3" && def[1].MinKB == 50 && def[2].Ext == "mp4" && def[2].MinKB == 0, "Mínimos de serie (los de IDM)");
            d.MediaPanelTypes = "flv mp3:50 -wmv .MKV mp3:9 -:4 bad:x";
            List<AppConfig.MediaTypeRule> l = AppConfig.ParseMediaTypes(d);
            Check(l.Count == 5, "Cuenta: " + l.Count);
            Check(l[0].Ext == "flv" && l[0].On && l[0].MinKB == 0, "flv");
            Check(l[1].Ext == "mp3" && l[1].MinKB == 50, "mp3 (el primero manda)");
            Check(l[2].Ext == "wmv" && !l[2].On, "-wmv");
            Check(l[3].Ext == "mkv", ".MKV");
            Check(l[4].Ext == "bad" && l[4].MinKB == 0, "Mínimo no numérico");

            // La ventana usa el mismo intérprete y guarda lo mismo.
            AppConfig c = Cfg();
            c.MediaPanelTypes = "flv mp3:50 -wmv";
            using (MediaPanelForm f = new MediaPanelForm(c)) { IntPtr h = f.Handle; f.Apply(); }
            Check(c.MediaPanelTypes == "flv mp3:50 -wmv", "Ventana: " + c.MediaPanelTypes);

            // Una configuración que ya tenía la casilla marcada la conserva.
            File.WriteAllText(AppConfig.ConfigPath, "{\"MediaPanelProtected\":true}");
            Check(AppConfig.Load().MediaPanelProtected, "Guardada marcada");
            File.WriteAllText(AppConfig.ConfigPath, "{\"MaxConnections\":8}");
            Check(!AppConfig.Load().MediaPanelProtected, "Antigua sin la clave: como IDM");
        }

        static void O3_BrowserProxy()
        {
            foreach (string ok in new string[] { "127.0.0.1:8080", "proxy.ejemplo.com:3128", "[::1]:8080", "p:1", "p:65535" })
                Check(NativeHost.ValidBrowserProxy(ok), "Rechazó '" + ok + "'");
            foreach (string bad in new string[] { null, "", "proxy", "proxy:", "proxy:0", "proxy:70000", "proxy:80x", "a b:80", "u:p@proxy:80", "proxy:80/ruta", ":80", "https://proxy:443", "socks5://u:p@proxy:1080", "::1:80", "pröxy:80", "proxy:+80" })
                Check(!NativeHost.ValidBrowserProxy(bad), "Aceptó '" + bad + "'");
        }

        // GUI (CookieBridge.GetCookies) y host (sondeo + HandleMessage "cookies") en este mismo proceso, por la tubería real.
        static void O3_CookieBridgeRoundTrip()
        {
            UseFreshHome("o3-cookies");
            int savedPoll = CookieBridge.LongPollMs, savedIdle = CookieBridge.PollIdleMs;
            CookieBridge.LongPollMs = 3000; CookieBridge.PollIdleMs = 200;
            List<string> toExt = new List<string>();
            try
            {
                CookieBridge.Start();
                // La "extensión": contesta a getcookies con una petición normal (con rid), como background.js.
                CookieBridge.StartHostPolling("Brave", delegate(string m)
                {
                    lock (toExt) toExt.Add(m);
                    Dictionary<string, object> q = O3_Json(m);
                    Thread t = new Thread(delegate()
                    {
                        string url = (string)q["url"];
                        string cookies = url == "https://x.com/privado/" ? "sid=ok; tema=oscuro" : "";
                        NativeHost.HandleMessage("{\"type\":\"cookies\",\"rid\":5,\"req\":" + q["req"] + ",\"url\":" + Ipc.JsonQuote(url) + ",\"ok\":true,\"cookies\":" + Ipc.JsonQuote(cookies) + ",\"userAgent\":\"UA-Prueba\"}");
                    });
                    t.IsBackground = true; t.Start();
                });
                Stopwatch sw = Stopwatch.StartNew();
                while (!CookieBridge.Available && sw.ElapsedMilliseconds < 5000) Thread.Sleep(50);
                Check(CookieBridge.Available, "El host no llegó a conectarse");

                string ua;
                string c = CookieBridge.GetCookies("https://x.com/privado/", 8000, out ua);
                Check(c == "sid=ok; tema=oscuro", "Cookies: " + (c ?? "null"));
                Check(ua == "UA-Prueba", "User-Agent: " + ua);
                lock (toExt)
                {
                    Check(toExt.Count == 1, "Mensajes a la extensión: " + toExt.Count);
                    Dictionary<string, object> m = O3_Json(toExt[0]);
                    Check((string)m["type"] == "getcookies" && !m.ContainsKey("rid") && (string)m["url"] == "https://x.com/privado/", "Mensaje: " + toExt[0]);
                }
                // El navegador contesta, pero sin cookies para esa dirección: "" (no null).
                Check(CookieBridge.GetCookies("https://x.com/otra/", 8000) == "", "Sin cookies debe dar cadena vacía");
                // Direcciones no http(s): ni se pregunta.
                int before; lock (toExt) before = toExt.Count;
                Check(CookieBridge.GetCookies("file:///C:/a.txt", 2000) == null && CookieBridge.GetCookies("javascript:alert(1)", 2000) == null, "No http(s)");
                lock (toExt) Check(toExt.Count == before, "Se preguntó por una dirección no válida");
                // Una respuesta de la extensión que nadie pidió no llega a la GUI.
                string r = NativeHost.HandleMessage("{\"type\":\"cookies\",\"rid\":6,\"req\":999,\"cookies\":\"a=1\"}");
                Check(r.Contains("\"ok\":false"), "Respuesta no pedida aceptada: " + r);
                // Nada en el registro del host con el valor de las cookies.
                string log = File.ReadAllText(Path.Combine(AppConfig.Dir, "host.log"), Encoding.UTF8);
                Check(!log.Contains("sid=ok") && !log.Contains("oscuro"), "Cookies en host.log");
            }
            finally
            {
                CookieBridge.StopHostPolling();
                CookieBridge.Stop();
                CookieBridge.LongPollMs = savedPoll; CookieBridge.PollIdleMs = savedIdle;
            }
        }

        // La lógica de la GUI sin tubería: dos "navegadores" (uno sin sesión), respuestas ajenas y sin navegadores.
        static void O3_CookieBridgeLogic()
        {
            UseFreshHome("o3-cookies2");
            int savedPoll = CookieBridge.LongPollMs;
            CookieBridge.LongPollMs = 3000;
            try
            {
                CookieBridge.Start();
                bool wrongUrlAccepted = false;
                string[] who = { "firefox", "chrome" };
                string[] give = { "", "sid=1" };
                for (int i = 0; i < 2; i++)
                {
                    string host = who[i], val = give[i];
                    Thread t = new Thread(delegate()
                    {
                        string cmd = CookieBridge.OnRequest("{\"type\":\"waitcmd\",\"host\":\"" + host + "\",\"browser\":\"" + host + "\"}");
                        Dictionary<string, object> q = O3_Json(cmd);
                        if ((string)q["type"] != "getcookies") return;
                        Thread.Sleep(host == "firefox" ? 50 : 300); // el que no tiene sesión contesta antes
                        if (host == "firefox")
                        {
                            // Una respuesta con otra dirección (p. ej. de una petición antigua) no vale.
                            string bad = CookieBridge.OnRequest("{\"type\":\"cookies\",\"host\":\"firefox\",\"req\":" + q["req"] + ",\"url\":\"https://otra.com/\",\"cookies\":\"x=1\"}");
                            if (!bad.Contains("\"ok\":false")) wrongUrlAccepted = true;
                        }
                        CookieBridge.OnRequest("{\"type\":\"cookies\",\"host\":\"" + host + "\",\"req\":" + q["req"] + ",\"url\":" + Ipc.JsonQuote((string)q["url"]) + ",\"cookies\":" + Ipc.JsonQuote(val) + "}");
                    });
                    t.IsBackground = true; t.Start();
                }
                Thread.Sleep(200);
                Stopwatch sw = Stopwatch.StartNew();
                Check(CookieBridge.GetCookies("https://x.com/", 5000) == "sid=1", "Debe ganar el navegador con sesión");
                Check(sw.ElapsedMilliseconds < 3000, "Tardó demasiado: " + sw.ElapsedMilliseconds);
                Check(!wrongUrlAccepted, "Aceptó cookies de otra dirección");
                // Respuesta de un host al que no se preguntó.
                Check(CookieBridge.OnRequest("{\"type\":\"cookies\",\"host\":\"intruso\",\"req\":1,\"cookies\":\"a=1\"}").Contains("\"ok\":false"), "Respuesta ajena aceptada");
                Check(CookieBridge.OnRequest("{\"type\":\"otra\",\"host\":\"x\"}").Contains("\"ok\":false"), "Orden desconocida");
                // Sin navegadores conectados: null al acabar el tiempo, sin colgarse.
                CookieBridge.Stop();
                CookieBridge.Start();
                Check(!CookieBridge.Available, "Sin hosts no debería estar disponible");
                sw = Stopwatch.StartNew();
                Check(CookieBridge.GetCookies("https://x.com/", 1200) == null, "Sin navegador: null");
                Check(sw.ElapsedMilliseconds >= 1100 && sw.ElapsedMilliseconds < 4000, "Tiempo: " + sw.ElapsedMilliseconds);
            }
            finally { CookieBridge.Stop(); CookieBridge.LongPollMs = savedPoll; }
        }

        static void O3_CookieBridgeTrust()
        {
            Check(CookieBridge.IsIdmCloneProcess((uint)Process.GetCurrentProcess().Id), "Este mismo proceso");
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c ping -n 4 127.0.0.1 >nul");
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi))
            {
                try { Check(!CookieBridge.IsIdmCloneProcess((uint)p.Id), "cmd.exe no es IDMClone"); }
                finally { try { p.Kill(); } catch { } }
            }
            Check(!CookieBridge.IsIdmCloneProcess(0) && !CookieBridge.IsIdmCloneProcess(999999), "pid inválido");
            Check(CookieBridge.PipeName.StartsWith(Ipc.PipeName) && CookieBridge.PipeName != Ipc.PipeName, "Tubería propia por usuario y sesión");
            Check(CookieBridge.ValidUrl("https://x.com/a?b=1") && !CookieBridge.ValidUrl("ftp://x.com/") && !CookieBridge.ValidUrl("https:///x") && !CookieBridge.ValidUrl(null), "ValidUrl");
        }

        // ---------------------------------------------------------------- panel IDM: lista "Para reproductores Web"

        // La de IDM 6.43 medida en Opciones > General > Personalizar panel > Para reproductores Web (+ opus y flac de IDMClone).
        static readonly string[] PI_Idm = { "flv", "mp3:50", "mp4", "m4a", "mpg", "mpeg", "avi", "wmv", "wma", "wav", "asf", "rm", "ogg:100", "mov", "ogv", "ts", "mkv", "aac",
                                            "vtt", "ttml", "ttml2", "dfxp", "srt", "3gp", "qt", "webm", "m4v", "f4v", "opus", "flac" };

        static void PI_CheckIdm(List<AppConfig.MediaTypeRule> l, string what)
        {
            CheckEq(string.Join(" ", PI_Idm), AppConfig.MediaTypesText(l), what);
            Check(l.TrueForAll(delegate(AppConfig.MediaTypeRule x) { return x.On; }), what + ": todos marcados");
        }

        static void PI_DefaultTable()
        {
            AppConfig d = AppConfig.CreateDefault();
            Check(d.MediaPanelTypes == null && d.MediaPanelMinVideoKB == 0 && d.MediaPanelMinAudioKB == 0, "Sin mínimos generales, como IDM");
            PI_CheckIdm(AppConfig.ParseMediaTypes(d), "Lista de serie");
            // Los mínimos de la lista no dependen de los generales.
            d.MediaPanelMinVideoKB = 4096; d.MediaPanelMinAudioKB = 999;
            PI_CheckIdm(AppConfig.ParseMediaTypes(d), "Con mínimos generales cambiados");
            // Todos los tipos de serie tienen una clase, y solo una (subtítulos aparte).
            foreach (string e in AppConfig.MediaTypesOrder)
            {
                int n = (AppConfig.IsMediaVideoType(e) ? 1 : 0) + (AppConfig.IsMediaAudioType(e) ? 1 : 0) + (AppConfig.IsMediaSubtitleType(e) ? 1 : 0);
                Check(n == 1, "Clase de " + e + ": " + n);
            }
            Check(AppConfig.IsMediaVideoType("RM") && AppConfig.IsMediaVideoType("ogv") && AppConfig.IsMediaVideoType("qt") && AppConfig.IsMediaVideoType("f4v"), "Vídeo nuevos");
            Check(AppConfig.IsMediaAudioType("wma") && AppConfig.IsMediaAudioType("ogg") && AppConfig.IsMediaAudioType("flac"), "Audio");
            Check(AppConfig.IsMediaSubtitleType("vtt") && AppConfig.IsMediaSubtitleType("TTML2") && AppConfig.IsMediaSubtitleType("srt") && !AppConfig.IsMediaSubtitleType("mp4"), "Subtítulos");
            // Un tipo agregado: el mínimo de IDM si es de serie; si no, el general de su clase.
            Check(AppConfig.NewMediaTypeMinKB(d, "mp3") == 50 && AppConfig.NewMediaTypeMinKB(d, "mp4") == 0 && AppConfig.NewMediaTypeMinKB(d, "xyz") == 4096, "Mínimo de un tipo agregado");
        }

        static void PI_ConfigReply()
        {
            UseFreshHome("pi-config");
            Cfg().Save();
            Dictionary<string, object> d = O3_Json(NativeHost.ConfigReply());
            Check((int)d["minVideoKB"] == 0 && (int)d["minAudioKB"] == 0, "Mínimos generales 0");
            List<string> w = new List<string>();
            foreach (object o in O3_List(d["mediaTypes"]))
            {
                Dictionary<string, object> t = (Dictionary<string, object>)o;
                Check((bool)t["on"], "Marcado: " + t["ext"]);
                w.Add((string)t["ext"] + ((int)t["minKB"] > 0 ? ":" + t["minKB"] : ""));
            }
            CheckEq(string.Join(" ", PI_Idm), string.Join(" ", w.ToArray()), "mediaTypes");
            // Una configuración guardada con los mínimos de antes llega ya convertida.
            File.WriteAllText(AppConfig.ConfigPath, "{\"MediaPanel\":true,\"MediaPanelMinVideoKB\":1024,\"MediaPanelMinAudioKB\":300,\"MediaPanelTypes\":null}");
            d = O3_Json(NativeHost.ConfigReply());
            Check((int)d["minVideoKB"] == 0 && (int)d["minAudioKB"] == 0 && O3_List(d["mediaTypes"]).Count == PI_Idm.Length, "Antigua por el host");
        }

        static void PI_Upgrade()
        {
            UseFreshHome("pi-upgrade");
            // Nunca tocó la lista y tiene los de antes (1024/300): pasa a los de IDM, y se queda así al guardar.
            File.WriteAllText(AppConfig.ConfigPath, "{\"MediaPanel\":true,\"MediaPanelMinVideoKB\":1024,\"MediaPanelMinAudioKB\":300,\"MediaPanelTypes\":null}");
            AppConfig a = AppConfig.Load();
            Check(a.MediaPanelTypes == null && a.MediaPanelMinVideoKB == 0 && a.MediaPanelMinAudioKB == 0, "1024/300 -> 0/0");
            PI_CheckIdm(AppConfig.ParseMediaTypes(a), "Antigua");
            a.Save();
            AppConfig a2 = AppConfig.Load();
            Check(a2.MediaPanelTypes == null && a2.MediaPanelMinVideoKB == 0 && a2.MediaPanelMinAudioKB == 0, "Guardada otra vez: igual");
            // Muy antigua, sin las claves del panel.
            File.WriteAllText(AppConfig.ConfigPath, "{\"MaxConnections\":8}");
            AppConfig v = AppConfig.Load();
            Check(v.MediaPanelTypes == null && v.MediaPanelMinVideoKB == 0 && v.MediaPanelMinAudioKB == 0, "Sin claves del panel");
            // Lista personalizada: se conserva tal cual (y sus mínimos generales).
            File.WriteAllText(AppConfig.ConfigPath, "{\"MediaPanel\":true,\"MediaPanelMinVideoKB\":1024,\"MediaPanelMinAudioKB\":300,\"MediaPanelTypes\":\"flv mp4:2048 -mp3\"}");
            AppConfig p = AppConfig.Load();
            Check(p.MediaPanelTypes == "flv mp4:2048 -mp3" && p.MediaPanelMinVideoKB == 1024 && p.MediaPanelMinAudioKB == 300, "Personalizada: " + p.MediaPanelTypes);
            // Sin lista pero con mínimos generales propios: se guardan en la lista (cada tipo con el de su clase) y se conservan.
            File.WriteAllText(AppConfig.ConfigPath, "{\"MediaPanel\":true,\"MediaPanelMinVideoKB\":2048,\"MediaPanelMinAudioKB\":300,\"MediaPanelTypes\":null}");
            AppConfig g = AppConfig.Load();
            Check(g.MediaPanelMinVideoKB == 2048 && g.MediaPanelMinAudioKB == 300 && g.MediaPanelTypes != null, "Mínimos propios");
            List<AppConfig.MediaTypeRule> l = AppConfig.ParseMediaTypes(g);
            Check(l.Count == PI_Idm.Length && l.TrueForAll(delegate(AppConfig.MediaTypeRule x) { return x.On; }), "Mínimos propios: lista de IDM");
            foreach (AppConfig.MediaTypeRule x in l)
            {
                int want = AppConfig.IsMediaAudioType(x.Ext) ? 300 : AppConfig.IsMediaVideoType(x.Ext) ? 2048 : 0;
                Check(x.MinKB == want, "Mínimo de " + x.Ext + ": " + x.MinKB);
            }
        }

        static void PI_Dialog()
        {
            UseFreshHome("pi-dialog");
            AppConfig c = Cfg();
            using (MediaPanelForm f = new MediaPanelForm(c))
            {
                IntPtr h = f.Handle;
                System.Windows.Forms.ListView lv = (System.Windows.Forms.ListView)typeof(MediaPanelForm)
                    .GetField("_types", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(f);
                Check(lv.Items.Count == PI_Idm.Length, "Filas: " + lv.Items.Count);
                for (int i = 0; i < lv.Items.Count; i++)
                {
                    System.Windows.Forms.ListViewItem it = lv.Items[i];
                    string ext = PI_Idm[i].Split(':')[0];
                    string size = ext == "mp3" ? "50.00 KB" : ext == "ogg" ? "100.00 KB" : "";
                    Check(it.Text == ext.ToUpperInvariant() && it.SubItems[1].Text == size && it.Checked, "Fila " + i + ": " + it.Text + " [" + it.SubItems[1].Text + "]");
                }
                f.Apply();
            }
            CheckEq(string.Join(" ", PI_Idm), c.MediaPanelTypes, "Aceptar guarda la lista de IDM");
            Check(c.MediaPanelMinVideoKB == 0 && c.MediaPanelMinAudioKB == 0, "Aceptar: mínimos generales 0");
        }
    }
}
