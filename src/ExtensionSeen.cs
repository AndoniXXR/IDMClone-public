using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace IDMClone
{
    // En qué navegadores se ha conectado la extensión (cada vez que el navegador abre el host). El
    // instalador lo usa para marcar "✓ conectada en Brave" en cuanto se carga la extensión.
    // Archivo: %APPDATA%\IDMClone\extension-connected.txt, una línea por navegador: "navegador<TAB>fecha UTC<TAB>origen".
    public static class ExtensionSeen
    {
        public const string FileName = "extension-connected.txt";
        static string PathOf { get { return Path.Combine(AppConfig.Dir, FileName); } }

        public class Entry { public string Browser, Origin; public DateTime WhenUtc; }

        // Devuelve el navegador que lanzó este proceso ("?" si no se sabe), para el host nativo (#1: captura por navegador).
        public static string Record(string origin)
        {
            string browser = "?";
            try { browser = BrowserOfThisProcess(); } catch { }
            try
            {
                Dictionary<string, Entry> all = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                foreach (Entry e in Read()) all[e.Browser] = e;
                Entry n = new Entry(); n.Browser = browser; n.Origin = origin ?? ""; n.WhenUtc = DateTime.UtcNow;
                all[browser] = n;
                StringBuilder sb = new StringBuilder();
                foreach (Entry e in all.Values) sb.Append(e.Browser).Append('\t').Append(e.WhenUtc.ToString("o")).Append('\t').Append(e.Origin).Append("\r\n");
                Directory.CreateDirectory(AppConfig.Dir);
                File.WriteAllText(PathOf, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
            return browser;
        }

        public static List<Entry> Read()
        {
            List<Entry> r = new List<Entry>();
            try
            {
                if (!File.Exists(PathOf)) return r;
                foreach (string line in File.ReadAllLines(PathOf, Encoding.UTF8))
                {
                    string[] p = line.Split('\t');
                    if (p.Length < 2) continue;
                    DateTime t;
                    if (!DateTime.TryParse(p[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out t)) continue;
                    Entry e = new Entry(); e.Browser = p[0]; e.WhenUtc = t.ToUniversalTime(); e.Origin = p.Length > 2 ? p[2] : "";
                    r.Add(e);
                }
            }
            catch { }
            return r;
        }

        // Navegadores donde se conectó después de cierta hora (p. ej. desde que terminó la instalación).
        public static List<string> Since(DateTime utc)
        {
            List<string> r = new List<string>();
            foreach (Entry e in Read()) if (e.WhenUtc >= utc) r.Add(e.Browser);
            return r;
        }

        // El navegador lanza "cmd.exe /c IDMClone.exe ..." (Chromium) o el host directamente (Firefox):
        // se sube por los procesos padre hasta encontrar uno que no sea cmd/conhost.
        public static string BrowserOfThisProcess()
        {
            Dictionary<int, KeyValuePair<int, string>> procs = Snapshot();
            int pid = Process.GetCurrentProcess().Id;
            for (int i = 0; i < 4; i++)
            {
                KeyValuePair<int, string> me;
                if (!procs.TryGetValue(pid, out me)) break;
                KeyValuePair<int, string> parent;
                if (!procs.TryGetValue(me.Key, out parent)) break;
                string exe = parent.Value.ToLowerInvariant();
                if (exe != "cmd.exe" && exe != "conhost.exe") return DisplayName(exe);
                pid = me.Key;
            }
            return "?";
        }

        public static string DisplayName(string exe)
        {
            switch ((exe ?? "").ToLowerInvariant())
            {
                case "brave.exe": return "Brave";
                case "msedge.exe": return "Microsoft Edge";
                case "chrome.exe": return "Google Chrome";
                case "opera.exe": return "Opera";
                case "vivaldi.exe": return "Vivaldi";
                case "firefox.exe": return "Mozilla Firefox";
                case "browser.exe": return "Yandex";
                default: return exe;
            }
        }

        // pid -> (pid del padre, ejecutable), de una foto de los procesos (Toolhelp).
        static Dictionary<int, KeyValuePair<int, string>> Snapshot()
        {
            Dictionary<int, KeyValuePair<int, string>> r = new Dictionary<int, KeyValuePair<int, string>>();
            IntPtr snap = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return r;
            try
            {
                PROCESSENTRY32 pe = new PROCESSENTRY32();
                pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                if (!Process32First(snap, ref pe)) return r;
                do { r[(int)pe.th32ProcessID] = new KeyValuePair<int, string>((int)pe.th32ParentProcessID, pe.szExeFile); }
                while (Process32Next(snap, ref pe));
            }
            finally { CloseHandle(snap); }
            return r;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 pe);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 pe);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    }
}
