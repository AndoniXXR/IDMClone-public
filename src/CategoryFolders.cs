using System;
using System.Collections.Generic;
using System.IO;

namespace IDMClone
{
    // Carpetas de las categorías de siempre con el nombre del idioma de IDMClone (IDM las deja siempre en inglés):
    // "Comprimidos, Documentos, Música, Vídeos, Programas" en español, "Compressed, Documents, Music, Video, Programs" en
    // inglés, y la traducción de "Carpeta: <categoría>" en los demás. Al cambiar de idioma (y en cada arranque, para las
    // instalaciones de antes) las que siguen en su sitio de siempre se renombran con lo que tengan dentro.
    public static class CategoryFolders
    {
        // Nombre interno de la categoría (no cambia) -> carpeta en español y en inglés (el de IDM, también para los idiomas
        // cuyo archivo no la traduce).
        static readonly string[,] Names = {
            { "Comprimidos", "Comprimidos", "Compressed" },
            { "Documentos", "Documentos", "Documents" },
            { "Música", "Música", "Music" },
            { "Vídeo", "Vídeos", "Video" },
            { "Programas", "Programas", "Programs" } };
        public const string KeyPrefix = "Carpeta: ";

        public static List<string> Categories()
        {
            List<string> r = new List<string>();
            for (int i = 0; i < Names.GetLength(0); i++) r.Add(Names[i, 0]);
            return r;
        }

        static int IndexOf(string catName)
        {
            for (int i = 0; i < Names.GetLength(0); i++) if (Names[i, 0] == catName) return i;
            return -1;
        }

        static bool Valid(string name)
        {
            return !string.IsNullOrEmpty(name) && name.Trim() == name && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        // La carpeta de una categoría de siempre en el idioma actual (null si no es una de ellas).
        public static string NameFor(string catName)
        {
            int i = IndexOf(catName);
            if (i < 0) return null;
            if (Lang.Current == Lang.BuiltIn) return Names[i, 1];
            string key = KeyPrefix + catName, t = Lang.T(key);
            return t != key && Valid(t) ? t : Names[i, 2];
        }

        // Los nombres que esa carpeta tiene en todos los idiomas conocidos: así se sabe que sigue "en su sitio de siempre"
        // aunque se creara con otro idioma (o con la versión de antes, en inglés).
        public static HashSet<string> KnownNames(string catName)
        {
            HashSet<string> r = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int i = IndexOf(catName);
            if (i < 0) return r;
            r.Add(Names[i, 1]); r.Add(Names[i, 2]);
            try
            {
                foreach (Lang.LangFile l in Lang.Available())
                {
                    string t = Lang.Lookup(l.Path, KeyPrefix + catName);
                    if (Valid(t)) r.Add(t);
                }
            }
            catch (Exception) { }
            string cur = NameFor(catName);
            if (cur != null) r.Add(cur);
            return r;
        }

        // Pasa al idioma actual las carpetas de las categorías de siempre que siguen en su sitio (Descargas\<nombre de algún
        // idioma>). Las que el usuario cambió de sitio no se tocan. Si una no se puede renombrar (en uso, o ya hay otra con
        // ese nombre y con archivos), se deja como estaba y se dice por qué. Devuelve esos avisos; 'changed' = hay que guardar.
        // openGrabber: el proyecto de la ventana del Grabber si está abierta (se guarda al cerrarla con lo que tenga en memoria).
        public static List<string> Sync(AppConfig cfg, DownloadManager mgr, GrabberProject openGrabber, out bool changed)
        {
            changed = false;
            List<string> problems = new List<string>();
            if (cfg == null || cfg.Categories == null || string.IsNullOrEmpty(cfg.DefaultFolder)) return problems;
            string root = cfg.DefaultFolder.TrimEnd('\\', '/');
            foreach (Category cat in cfg.Categories)
            {
                string want = NameFor(cat.Name);
                if (want == null || string.IsNullOrEmpty(cat.Folder)) continue;
                string cur = cat.Folder.TrimEnd('\\', '/');
                string parent, curName;
                try { parent = Path.GetDirectoryName(cur); curName = Path.GetFileName(cur); }
                catch (Exception) { continue; }
                if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase) || !KnownNames(cat.Name).Contains(curName)) continue;
                if (string.Equals(curName, want, StringComparison.OrdinalIgnoreCase)) continue;
                string target = Path.Combine(root, want);
                if (mgr != null && Busy(mgr, cur))
                {
                    problems.Add("\"" + cur + "\": hay una descarga en curso guardándose en ella.");
                    continue;
                }
                try
                {
                    if (Directory.Exists(cur))
                    {
                        if (!Directory.Exists(target)) Directory.Move(cur, target);
                        else if (Directory.GetFileSystemEntries(cur).Length == 0) Directory.Delete(cur);
                        else
                        {
                            problems.Add("\"" + cur + "\": ya existe \"" + target + "\" con archivos.");
                            continue;
                        }
                    }
                }
                catch (Exception ex)
                {
                    problems.Add("\"" + cur + "\": " + ex.Message);
                    continue;
                }
                cat.Folder = target;
                changed = true;
                ReplaceEverywhere(cfg, mgr, openGrabber, cur, target);
            }
            return problems;
        }

        static bool Busy(DownloadManager mgr, string dir)
        {
            foreach (DownloadItem it in mgr.Snapshot())
            {
                DownloadEngine e = it.Engine;
                if (e != null && e.IsRunning && Moved(it.WorkingPath, dir, "x") != it.WorkingPath) return true;
            }
            return false;
        }

        // La ruta con la carpeta 'from' cambiada por 'to' (si está dentro de ella; si no, igual).
        public static string Moved(string path, string from, string to)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string p = path.TrimEnd('\\', '/');
            if (string.Equals(p, from, StringComparison.OrdinalIgnoreCase)) return to;
            if (p.Length > from.Length && p.StartsWith(from, StringComparison.OrdinalIgnoreCase) && (p[from.Length] == '\\' || p[from.Length] == '/'))
                return to + path.Substring(from.Length);
            return path;
        }

        // Todas las rutas guardadas que apuntaban dentro de la carpeta renombrada.
        static void ReplaceEverywhere(AppConfig cfg, DownloadManager mgr, GrabberProject openGrabber, string from, string to)
        {
            if (mgr != null)
            {
                bool any = false;
                foreach (DownloadItem it in mgr.Snapshot())
                {
                    string fp = Moved(it.FilePath, from, to), tp = Moved(it.TempPath, from, to);
                    if (fp == it.FilePath && tp == it.TempPath) continue;
                    it.FilePath = fp; it.TempPath = tp; any = true;
                    // El estado de cada descarga (state\<id>.json) manda sobre la lista al cargar: también.
                    try { if (File.Exists(it.StatePath)) it.SaveState(); } catch (Exception) { }
                }
                if (any) mgr.SaveList();
                bool q = false;
                if (mgr.Queues != null && mgr.Queues.Queues != null)
                    foreach (QueueDef d in mgr.Queues.Queues) { string r = Moved(d.RunFile, from, to); if (r != d.RunFile) { d.RunFile = r; q = true; } }
                if (q) mgr.SaveQueues();
            }
            if (cfg.SaveAsHistory != null)
            {
                List<string> h = new List<string>();
                foreach (string s in cfg.SaveAsHistory)
                {
                    string r = Moved(s, from, to);
                    if (!h.Exists(delegate(string x) { return string.Equals(x, r, StringComparison.OrdinalIgnoreCase); })) h.Add(r);
                }
                cfg.SaveAsHistory = h;
            }
            if (cfg.Sounds != null) foreach (SoundSetting s in cfg.Sounds) s.File = Moved(s.File, from, to);
            cfg.TempFolder = Moved(cfg.TempFolder, from, to);
            cfg.AntivirusPath = Moved(cfg.AntivirusPath, from, to);
            if (mgr == null || mgr.Persist) ReplaceInGrabber(openGrabber, from, to);
        }

        static bool Patch(GrabberProject p, string from, string to)
        {
            bool c = false;
            string sf = Moved(p.SaveFolder, from, to);
            if (sf != p.SaveFolder) { p.SaveFolder = sf; c = true; }
            if (p.Files != null)
                foreach (GrabberFile f in p.Files) { string s = Moved(f.SaveTo, from, to); if (s != f.SaveTo) { f.SaveTo = s; c = true; } }
            return c;
        }

        static void ReplaceInGrabber(GrabberProject open, string from, string to)
        {
            if (open != null) lock (open) Patch(open, from, to);
            foreach (string name in GrabberProject.SavedNames())
            {
                if (open != null && string.Equals(open.Name, name, StringComparison.CurrentCultureIgnoreCase)) continue;
                try { GrabberProject p = GrabberProject.Load(name); if (Patch(p, from, to)) p.Save(); }
                catch (Exception) { }
            }
            try
            {
                if (!Directory.Exists(GrabberProject.TemplatesFolder)) return;
                foreach (string f in Directory.GetFiles(GrabberProject.TemplatesFolder, "*.json"))
                {
                    try
                    {
                        GrabberProject t = AppConfig.NewSerializer().Deserialize<GrabberProject>(File.ReadAllText(f));
                        if (t != null && Patch(t, from, to)) Util.WriteAllTextAtomic(f, AppConfig.NewSerializer().Serialize(t));
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }
    }
}
