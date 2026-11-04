using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Jint; // solo extensiones (Options.TimeoutInterval): no carga Jint.dll por sí solo

namespace IDMClone
{
    public class YouTubeException : Exception { public YouTubeException(string m) : base(m) { } }

    // Descifrado de la firma ("s" de signatureCipher) y del parámetro "n" de las direcciones de YouTube, con las funciones
    // del reproductor de la página (base.js). Lo hace el descifrador de yt-dlp/ejs ejecutado con Jint:
    //  1) "preprocesar": ejs analiza el base.js (unos 3 MB) y deja un código con las dos funciones listas. Es lo lento
    //     (~30 s con Jint), así que se guarda en %APPDATA%\IDMClone\youtube\players\, una vez por versión del reproductor;
    //  2) con lo preprocesado, cada firma o "n" es una llamada de milisegundos (el motor queda en memoria).
    // Esta clase no nombra ningún tipo de Jint: así .NET no busca Jint.dll hasta que YouTubeLibs ha puesto su cargador.
    public static class YouTubeSolver
    {
        public static string CacheDir { get { return Path.Combine(Path.Combine(AppConfig.Dir, "youtube"), "players"); } }
        const int KeepPlayers = 4;              // reproductores preprocesados que se guardan
        public static int TimeoutSeconds = 180; // tope de cada ejecución de JavaScript

        static readonly object _lock = new object();
        static string _currentKey;              // reproductor del motor en memoria
        static object _current;                 // YtJint (como object: ver arriba)

        // Las pruebas: cuántas veces se preprocesó un reproductor (lo lento) en este proceso.
        public static int PreprocessCount;

        // Las pruebas: olvidar el motor en memoria (para comprobar que se lee la caché del disco).
        internal static void ForgetLoaded() { lock (_lock) { _current = null; _currentKey = null; } }

        // Número de versión del reproductor: "/s/player/<id>/..." -> "<id>".
        public static string PlayerId(string playerUrl)
        {
            Match m = Regex.Match(playerUrl ?? "", @"/s/player/([0-9A-Za-z_-]+)/");
            return m.Success ? m.Groups[1].Value : null;
        }

        // Dirección completa del reproductor ("/s/player/..." de la página -> https://www.youtube.com/s/player/...).
        public static string AbsolutePlayerUrl(string u)
        {
            if (string.IsNullOrEmpty(u)) return null;
            if (u.StartsWith("//")) return "https:" + u;
            if (u.StartsWith("/")) return "https://www.youtube.com" + u;
            return u;
        }

        // Resuelve las firmas y los "n" pedidos. "fetch" baja el base.js si no está ya preprocesado.
        // Excepción YouTubeException si no se pueden descifrar.
        public static void Solve(string playerUrl, Func<string, string> fetch, ICollection<string> n, ICollection<string> sig,
                                 IDictionary<string, string> nOut, IDictionary<string, string> sigOut)
        { Solve(playerUrl, fetch, n, sig, nOut, sigOut, null); }

        // "status" recibe PreparingText si hay que preparar el reproductor (o esperar a otro proceso que lo prepara).
        public static void Solve(string playerUrl, Func<string, string> fetch, ICollection<string> n, ICollection<string> sig,
                                 IDictionary<string, string> nOut, IDictionary<string, string> sigOut, Action<string> status)
        {
            playerUrl = AbsolutePlayerUrl(playerUrl);
            if (PlayerId(playerUrl) == null) throw new YouTubeException("No se conoce el reproductor de YouTube para descifrar el vídeo.");
            if ((n == null || n.Count == 0) && (sig == null || sig.Count == 0)) return;
            YouTubeLibs.Ensure();
            Exception error = null;
            RunWithBigStack(delegate
            {
                try { SolveCore(playerUrl, fetch, n, sig, nOut, sigOut, status); }
                catch (Exception ex) { error = ex; }
            });
            if (error is YouTubeException) throw error;
            if (error != null) throw new YouTubeException("No se pudo descifrar la dirección del vídeo de YouTube: " + FirstLine(error.Message));
        }

        static string FirstLine(string s)
        {
            s = s ?? "";
            int i = s.IndexOf('\n');
            s = i >= 0 ? s.Substring(0, i) : s;
            return s.Length > 300 ? s.Substring(0, 300) + "..." : s;
        }

        // Jint es recursivo: el analizador de JavaScript sobre un base.js de 3 MB necesita más pila que el 1 MB de un hilo normal.
        static void RunWithBigStack(ThreadStart work)
        {
            Thread t = new Thread(work, 64 * 1024 * 1024);
            t.IsBackground = true;
            t.Name = "YouTube JS";
            t.Start();
            t.Join();
        }

        static void SolveCore(string playerUrl, Func<string, string> fetch, ICollection<string> n, ICollection<string> sig,
                              IDictionary<string, string> nOut, IDictionary<string, string> sigOut, Action<string> status)
        {
            lock (_lock)
            {
                string core = YouTubeLibs.ReadEjs("yt.solver.core.js");
                string key = CacheKey(playerUrl, core);
                if (_currentKey != key || _current == null)
                {
                    _current = null; _currentKey = null;
                    _current = Load(key, playerUrl, fetch, core, status);
                    _currentKey = key;
                }
                if (n != null) foreach (string c in n) if (!nOut.ContainsKey(c)) nOut[c] = Call(_current, "n", c);
                if (sig != null) foreach (string c in sig) if (!sigOut.ContainsKey(c)) sigOut[c] = Call(_current, "sig", c);
            }
        }

        static string CacheKey(string playerUrl, string core)
        {
            string path;
            try { path = new Uri(playerUrl).AbsolutePath; } catch { path = playerUrl; }
            using (SHA1 h = SHA1.Create())
            {
                byte[] d = h.ComputeHash(Encoding.UTF8.GetBytes(path + "\n" + core));
                return PlayerId(playerUrl) + "-" + BitConverter.ToString(d, 0, 5).Replace("-", "").ToLowerInvariant();
            }
        }

        // Texto de la ventana mientras se prepara (se traduce con Lang.T al mostrarlo; está en Languages\*.lng).
        public const string PreparingText = "Preparando YouTube (solo la primera vez)...";
        public static bool LastPreprocessFast; // las pruebas: el último se hizo por el camino rápido

        static object TryLoadFile(string file)
        {
            if (!File.Exists(file)) return null;
            try
            {
                object j = FromPreprocessed(File.ReadAllText(file, Encoding.UTF8));
                try { File.SetLastWriteTimeUtc(file, DateTime.UtcNow); } catch { }
                return j;
            }
            catch (Exception ex) { NativeHost.Log("youtube: caché del reproductor dañada (" + FirstLine(ex.Message) + "), se rehace"); return null; }
        }

        static object Load(string key, string playerUrl, Func<string, string> fetch, string core, Action<string> status)
        {
            string file = Path.Combine(CacheDir, key + ".js");
            object j = TryLoadFile(file);
            if (j != null) return j;
            if (status != null) status(PreparingText);
            // Uno solo por versión del reproductor en todo el equipo: el host (que lo prepara al contestar "ytinfo") y la
            // ventana (al descargar) son procesos distintos. El que llega segundo espera y lee la caché del primero.
            using (Mutex m = new Mutex(false, "IDMClone-YouTube-" + key))
            {
                bool own;
                try { own = m.WaitOne(TimeSpan.FromSeconds(TimeoutSeconds)); }
                catch (AbandonedMutexException) { own = true; } // el otro proceso se cerró a medias: se hace aquí
                try
                {
                    j = TryLoadFile(file);
                    if (j != null) return j;
                    return Prepare(file, playerUrl, fetch, core);
                }
                finally { if (own) m.ReleaseMutex(); }
            }
        }

        static object Prepare(string file, string playerUrl, Func<string, string> fetch, string core)
        {
            string js;
            try { js = fetch(playerUrl); }
            catch (Exception ex) { throw new YouTubeException("No se pudo bajar el reproductor de YouTube: " + FirstLine(ex.Message)); }
            if (string.IsNullOrEmpty(js)) throw new YouTubeException("El reproductor de YouTube está vacío.");
            Interlocked.Increment(ref PreprocessCount);
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            string lib = YouTubeLibs.ReadEjs("yt.solver.lib.js");
            // Camino rápido (~3 s): Acornima (C#) analiza el base.js y ejs solo mira las sentencias candidatas. Si algo no
            // cuadra, o el resultado no pasa la prueba, el camino de siempre: ejs analiza el base.js entero (~30 s con Jint).
            object jint = null; string pp = null;
            try
            {
                pp = FastPreprocess(js, lib, core);
                if (pp != null) { jint = FromPreprocessed(pp); SelfTest(jint); }
                else NativeHost.Log("youtube: el reproductor no tiene la forma esperada; se prepara entero");
            }
            catch (Exception ex) { jint = null; NativeHost.Log("youtube: camino rápido descartado (" + FirstLine(ex.Message) + "); se prepara entero"); }
            LastPreprocessFast = jint != null;
            if (jint == null) jint = Preprocess(js, core, out pp);
            NativeHost.Log("youtube: reproductor " + Path.GetFileNameWithoutExtension(file) + " preparado en " + sw.ElapsedMilliseconds + " ms" + (LastPreprocessFast ? " (rápido)" : " (completo)"));
            try
            {
                Directory.CreateDirectory(CacheDir);
                string tmp = file + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
                File.WriteAllText(tmp, pp, new UTF8Encoding(false));
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
                Prune(file);
            }
            catch (Exception ex) { NativeHost.Log("youtube: no se pudo guardar el reproductor preprocesado: " + ex.Message); }
            return jint;
        }

        // Una firma y un "n" de muestra: el reproductor preparado por el camino rápido tiene que poder con ellos.
        static void SelfTest(object jint)
        {
            Call(jint, "n", "ZdZIqFPQK-Ty8wId");
            Call(jint, "sig", "AAJAJfQdSswRQIhAMG5SN7-cAFChdrE7tLA6grI0rTMICA1mmDc0HoXgW3CAiAQQ4=CspfaF_vHTpP9bP8Y7=7Qm1R2N5W8vXq");
        }

        // Deja solo los reproductores usados más recientemente.
        static void Prune(string keep)
        {
            List<FileInfo> files = new List<FileInfo>(new DirectoryInfo(CacheDir).GetFiles("*.js"));
            files.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });
            for (int i = KeepPlayers; i < files.Count; i++)
                if (!string.Equals(files[i].FullName, keep, StringComparison.OrdinalIgnoreCase)) { try { files[i].Delete(); } catch { } }
        }

        // Los métodos que tocan Jint, sin poder integrarse en los de arriba (si no, .NET buscaría Jint.dll al compilarlos).
        [MethodImpl(MethodImplOptions.NoInlining)]
        static object Preprocess(string js, string core, out string pp) { return YtJint.Preprocess(js, YouTubeLibs.ReadEjs("yt.solver.lib.js"), core, TimeoutSeconds, out pp); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string FastPreprocess(string js, string lib, string core) { return YtJint.FastPreprocess(js, lib, core, TimeoutSeconds); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static object FromPreprocessed(string pp) { return YtJint.FromPreprocessed(pp, TimeoutSeconds); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string Call(object j, string kind, string challenge) { return ((YtJint)j).Call(kind, challenge); }
    }

    // La única clase que usa Jint.
    sealed class YtJint
    {
        Jint.Engine _e;

        static Jint.Engine NewEngine(int timeoutSeconds)
        {
            return new Jint.Engine(delegate(Jint.Options o) { o.TimeoutInterval(TimeSpan.FromSeconds(timeoutSeconds)); });
        }

        // Analiza el base.js con ejs; devuelve el motor listo y el código preprocesado (para la caché).
        public static YtJint Preprocess(string player, string lib, string core, int timeoutSeconds, out string pp)
        {
            Jint.Engine e = NewEngine(timeoutSeconds);
            e.Execute(lib);
            e.Execute("var meriyah = lib.meriyah, astring = lib.astring;");
            e.Execute(core);
            e.SetValue("__player", player);
            pp = e.Evaluate("(function(){ var o = jsc({ type: 'player', player: __player, output_preprocessed: true, requests: [] }); __player = null; return o.preprocessed_player; })()").ToString();
            YtJint j = new YtJint();
            j._e = e;
            j.Setup(pp);
            return j;
        }

        public static YtJint FromPreprocessed(string pp, int timeoutSeconds)
        {
            YtJint j = new YtJint();
            j._e = NewEngine(timeoutSeconds);
            j.Setup(pp);
            return j;
        }

        // --- Camino rápido ---
        // Hace lo mismo que preprocessPlayer de ejs, pero el base.js entero lo analiza Acornima (el analizador en C# que ya
        // usa Jint, 0,5 s) en vez de meriyah dentro de Jint (25 s):
        //  - el bloque de la función principal del reproductor (las dos formas que acepta modifyPlayer de ejs);
        //  - fuera, como ejs, las sentencias de expresión que no son asignaciones ni literales (y, en la segunda forma, la primera);
        //  - ejs (el de verdad) se ejecuta sobre un reproductor con solo las sentencias candidatas (las que nombran "alr" y
        //    "yes"): así sigue siendo ejs quien reconoce las funciones y escribe las de descifrado (_result.n/_result.sig);
        //  - el resultado se monta con el TEXTO original del base.js (no se regenera con astring) más esas funciones.
        // null si el reproductor no tiene esa forma.
        public static string FastPreprocess(string player, string lib, string core, int timeoutSeconds)
        {
            Acornima.Parser parser = new Acornima.Parser();
            List<Acornima.Ast.Node> top = Kids(parser.ParseScript(player));
            Acornima.Ast.Node block; bool second;
            if (!FindBlock(top, out block, out second)) return null;
            List<Acornima.Ast.Node> st = Kids(block);
            if (st.Count < 2) return null;
            bool[] keep = new bool[st.Count];
            StringBuilder cands = new StringBuilder();
            for (int i = 0; i < st.Count; i++)
            {
                keep[i] = !(second && i == 0) && Keep(st[i]);
                if (!keep[i]) continue;
                string t = player.Substring(st[i].Start, st[i].End - st[i].Start);
                if (t.IndexOf("alr", StringComparison.Ordinal) >= 0 && t.IndexOf("yes", StringComparison.Ordinal) >= 0) cands.Append(t).Append('\n');
            }
            if (cands.Length == 0) return null;
            int last = st[st.Count - 1].End;
            string tiny = player.Substring(0, st[0].Start) + (second ? player.Substring(st[0].Start, st[0].End - st[0].Start) + "\n" : "") +
                          cands.ToString() + player.Substring(last);

            Jint.Engine e = NewEngine(timeoutSeconds);
            e.Execute(lib);
            e.Execute("var meriyah = lib.meriyah, astring = lib.astring;");
            e.Execute(core);
            e.SetValue("__player", tiny);
            // ejs ejecuta lo que prepara (getFromPrepared: Function('_result', código)) y con solo las candidatas eso falla
            // (una clase ES6 cuya base no está, en player_es6). Aquí solo hace falta el texto: esa llamada no se ejecuta.
            string outp = e.Evaluate(
                "(function(){ var F = Function, code = null;" +
                " Function = function(a, b) { if (a === '_result' && arguments.length === 2) { code = b; return function(){}; } return F.apply(this, arguments); };" +
                " try { jsc({ type: 'player', player: __player, output_preprocessed: true, requests: [] }); } finally { Function = F; }" +
                " return code; })()").ToString();
            if (string.IsNullOrEmpty(outp) || outp == "null") return null;

            List<Acornima.Ast.Node> otop = Kids(parser.ParseScript(outp));
            int playerTop = top.Count;
            if (otop.Count < playerTop) return null;
            List<Acornima.Ast.Node> ptop = otop.GetRange(otop.Count - playerTop, playerTop);
            Acornima.Ast.Node oblock; bool osecond;
            if (!FindBlock(ptop, out oblock, out osecond)) return null;
            StringBuilder sol = new StringBuilder();
            foreach (Acornima.Ast.Node x in Kids(oblock))
                if (IsResultAssignment(x)) sol.Append(outp, x.Start, x.End - x.Start).Append('\n');
            if (sol.Length == 0) return null;

            StringBuilder pp = new StringBuilder(player.Length + sol.Length + 4096);
            pp.Append(outp, 0, ptop[0].Start).Append('\n');          // lo que ejs pone delante (location, document, window...)
            pp.Append(player, 0, st[0].Start);
            for (int i = 0; i < st.Count; i++)
                if (keep[i]) pp.Append(player, st[i].Start, st[i].End - st[i].Start).Append('\n');
            pp.Append(sol);
            pp.Append(player, last, player.Length - last);
            return pp.ToString();
        }

        // Los hijos de un nodo (las listas de Acornima son "ref readonly": C# 5 no las puede leer directamente).
        static List<Acornima.Ast.Node> Kids(Acornima.Ast.Node n)
        {
            List<Acornima.Ast.Node> r = new List<Acornima.Ast.Node>();
            foreach (Acornima.Ast.Node c in n.ChildNodes) r.Add(c);
            return r;
        }

        // Las dos formas de modifyPlayer de ejs: "(function(){...}).call(this)" o "var x={};(function(g){...})(x)".
        static bool FindBlock(List<Acornima.Ast.Node> top, out Acornima.Ast.Node block, out bool second)
        {
            block = null; second = false;
            if (top.Count == 1)
            {
                Acornima.Ast.ExpressionStatement es = top[0] as Acornima.Ast.ExpressionStatement;
                Acornima.Ast.CallExpression call = es != null ? es.Expression as Acornima.Ast.CallExpression : null;
                Acornima.Ast.MemberExpression m = call != null ? call.Callee as Acornima.Ast.MemberExpression : null;
                Acornima.Ast.FunctionExpression f = m != null ? m.Object as Acornima.Ast.FunctionExpression : null;
                if (f == null) return false;
                block = f.Body;
            }
            else if (top.Count == 2)
            {
                Acornima.Ast.ExpressionStatement es = top[1] as Acornima.Ast.ExpressionStatement;
                Acornima.Ast.CallExpression call = es != null ? es.Expression as Acornima.Ast.CallExpression : null;
                Acornima.Ast.FunctionExpression f = call != null ? call.Callee as Acornima.Ast.FunctionExpression : null;
                if (f == null) return false;
                block = f.Body; second = true;
            }
            return block != null;
        }

        static bool Keep(Acornima.Ast.Node st)
        {
            Acornima.Ast.ExpressionStatement es = st as Acornima.Ast.ExpressionStatement;
            return es == null || es.Expression is Acornima.Ast.AssignmentExpression || es.Expression is Acornima.Ast.Literal;
        }

        static bool IsResultAssignment(Acornima.Ast.Node st)
        {
            Acornima.Ast.ExpressionStatement es = st as Acornima.Ast.ExpressionStatement;
            Acornima.Ast.AssignmentExpression a = es != null ? es.Expression as Acornima.Ast.AssignmentExpression : null;
            Acornima.Ast.MemberExpression m = a != null ? a.Left as Acornima.Ast.MemberExpression : null;
            Acornima.Ast.Identifier id = m != null ? m.Object as Acornima.Ast.Identifier : null;
            return id != null && id.Name == "_result";
        }

        // Lo mismo que getFromPrepared de ejs: el código preprocesado deja en _result las funciones n y sig.
        void Setup(string pp)
        {
            _e.SetValue("__pp", pp);
            _e.Execute("var __r = { n: null, sig: null }; Function('_result', __pp)(__r); __pp = null;");
        }

        public string Call(string kind, string challenge)
        {
            if (_e.Evaluate("typeof __r." + kind).ToString() != "function")
                throw new YouTubeException("El descifrador no encontró la función \"" + kind + "\" en el reproductor de YouTube.");
            _e.SetValue("__c", challenge);
            string r;
            try
            {
                r = _e.Evaluate("(function(){ var x = __r." + kind + "(__c); return x == null ? '' : String(x); })()").ToString();
            }
            catch (Exception ex)
            {
                string m = ex.Message ?? "";
                throw new YouTubeException("El descifrador de YouTube falló (" + kind + "): " + (m.Length > 200 ? m.Substring(0, 200) + "..." : m));
            }
            if (string.IsNullOrEmpty(r) || r.StartsWith("enhanced_except", StringComparison.Ordinal))
                throw new YouTubeException("El descifrador de YouTube no devolvió un valor válido (" + kind + ").");
            return r;
        }
    }
}
