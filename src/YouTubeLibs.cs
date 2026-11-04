using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace IDMClone
{
    // Bibliotecas del motor de YouTube que van DENTRO de IDMClone.exe (para que siga siendo un solo archivo):
    //  - Jint (motor de JavaScript en C#) y lo que pide en .NET 4.8: Acornima, System.Memory, System.Buffers,
    //    System.Numerics.Vectors y System.Runtime.CompilerServices.Unsafe. build.ps1 las mete comprimidas como
    //    recursos "IDMClone.lib.<nombre>.dll.gz" (copias en lib\) y se cargan aquí al pedirlas .NET (AssemblyResolve).
    //  - El descifrador de yt-dlp/ejs (Unlicense): recursos "IDMClone.ejs.<archivo>.gz" (copias en lib\ejs\).
    //    Si en %APPDATA%\IDMClone\youtube\ejs\ hay una versión de esos archivos, se usa esa: así se puede poner al día
    //    el descifrador sin recompilar.
    // El manejador se instala la primera vez que hace falta Jint (YouTubeSolver), no al arrancar IDMClone.
    public static class YouTubeLibs
    {
        static readonly string[] Names = {
            "Jint", "Acornima", "System.Memory", "System.Buffers", "System.Numerics.Vectors", "System.Runtime.CompilerServices.Unsafe"
        };
        static readonly object _lock = new object();
        static bool _installed;
        static readonly Dictionary<string, Assembly> _loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        public static void Ensure()
        {
            lock (_lock)
            {
                if (_installed) return;
                _installed = true;
                AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            }
        }

        static Assembly Resolve(object sender, ResolveEventArgs e)
        {
            string name;
            try { name = new AssemblyName(e.Name).Name; } catch { return null; }
            bool ours = false;
            foreach (string n in Names) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) { ours = true; break; }
            if (!ours) return null;
            lock (_loaded)
            {
                // Una sola copia de cada una aunque se pida con otra versión (Jint pide Acornima 1.7 y va la 1.9;
                // System.Memory pide Unsafe 4.0.4.1 y va la 6.0): sin esto habría dos tipos distintos con el mismo nombre.
                Assembly a;
                if (_loaded.TryGetValue(name, out a)) return a;
                byte[] raw = ReadResource("IDMClone.lib." + name + ".dll.gz");
                if (raw == null)
                {
                    string p = Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib"), name + ".dll");
                    if (File.Exists(p)) raw = File.ReadAllBytes(p);
                }
                if (raw == null) return null;
                a = Assembly.Load(raw);
                _loaded[name] = a;
                return a;
            }
        }

        // Recurso comprimido (gzip) de este ensamblado, o null si no está.
        static byte[] ReadResource(string name)
        {
            Stream s = typeof(YouTubeLibs).Assembly.GetManifestResourceStream(name);
            if (s == null) return null;
            using (s)
            using (GZipStream gz = new GZipStream(s, CompressionMode.Decompress))
            using (MemoryStream ms = new MemoryStream())
            {
                gz.CopyTo(ms);
                return ms.ToArray();
            }
        }

        public static string EjsOverrideDir { get { return Path.Combine(Path.Combine(AppConfig.Dir, "youtube"), "ejs"); } }

        // "yt.solver.lib.js" o "yt.solver.core.js": la copia de %APPDATA%\IDMClone\youtube\ejs\, la de dentro del exe
        // o la de lib\ejs\ junto al exe (en ese orden). Excepción si no hay ninguna.
        public static string ReadEjs(string file)
        {
            try
            {
                string p = Path.Combine(EjsOverrideDir, file);
                if (File.Exists(p)) return File.ReadAllText(p, Encoding.UTF8);
            }
            catch { }
            byte[] raw = ReadResource("IDMClone.ejs." + file + ".gz");
            if (raw != null) return Encoding.UTF8.GetString(raw);
            string local = Path.Combine(Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib"), "ejs"), file);
            if (File.Exists(local)) return File.ReadAllText(local, Encoding.UTF8);
            throw new FileNotFoundException("Falta el descifrador de YouTube (" + file + ").");
        }
    }
}
