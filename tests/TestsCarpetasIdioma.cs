using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace IDMClone.Tests
{
    // Carpetas de las categorías con el nombre del idioma de IDMClone (IDM las deja siempre en inglés): Comprimidos,
    // Documentos, Música, Vídeos y Programas en español; Compressed... en inglés. Al cambiar de idioma, y al arrancar
    // (instalaciones de antes, en inglés), las que siguen en su sitio de siempre se renombran con lo que tengan dentro.
    public static partial class TestMain
    {
        static void RunCarpetasIdioma()
        {
            Run("carpetas idioma: nombres en español, inglés, japonés y chino (y en inglés si el idioma no los trae)", TCI_Names);
            Run("carpetas idioma: de inglés a español se renombran con sus archivos y las descargas apuntan a la nueva", TCI_Rename);
            Run("carpetas idioma: si ya existe la nueva con archivos no se toca nada; si la vieja está vacía, se quita", TCI_Conflict);
            Run("carpetas idioma: una carpeta elegida por el usuario no se toca", TCI_Custom);
            Run("carpetas idioma: con una descarga en curso guardándose dentro no se renombra", TCI_Busy);
            Run("carpetas idioma: Ver > Idioma renombra las carpetas al momento (y al volver)", TCI_SetLanguage);
            Run("carpetas idioma: el instalador crea la configuración en su idioma y las carpetas", TCI_Installer);
        }

        // Una configuración de antes: carpetas en inglés dentro de 'root'.
        static AppConfig OldFoldersConfig(string root)
        {
            AppConfig c = Cfg();
            c.DefaultFolder = root;
            c.Categories = new List<Category>();
            c.Categories.Add(new Category("General", root, ""));
            c.Categories.Add(new Category("Comprimidos", Path.Combine(root, "Compressed"), "zip"));
            c.Categories.Add(new Category("Documentos", Path.Combine(root, "Documents"), "pdf"));
            c.Categories.Add(new Category("Música", Path.Combine(root, "Music"), "mp3"));
            c.Categories.Add(new Category("Vídeo", Path.Combine(root, "Video"), "mp4"));
            c.Categories.Add(new Category("Programas", Path.Combine(root, "Programs"), "exe"));
            return c;
        }

        static string FolderOf(AppConfig c, string cat)
        {
            foreach (Category x in c.Categories) if (x.Name == cat) return x.Folder;
            return null;
        }

        static string FreshRoot(string tag)
        {
            string r = Path.Combine(Home, "cfi-" + tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 6), "Descargas");
            Directory.CreateDirectory(r);
            return r;
        }

        static void TCI_Names()
        {
            try
            {
                Lang.Reset();
                string es = string.Join("|", CategoryFolders.Categories().ConvertAll(CategoryFolders.NameFor).ToArray());
                Check(es == "Comprimidos|Documentos|Música|Vídeos|Programas", "Español: " + es);
                Lang.LoadFile(EnglishFile());
                string en = string.Join("|", CategoryFolders.Categories().ConvertAll(CategoryFolders.NameFor).ToArray());
                Check(en == "Compressed|Documents|Music|Video|Programs", "Inglés: " + en);
                Lang.LoadFile(LangFile("ja"));
                Check(CategoryFolders.NameFor("Música") == "音楽" && CategoryFolders.NameFor("Vídeo") == "ビデオ", "Japonés: " + CategoryFolders.NameFor("Música"));
                Lang.LoadFile(LangFile("zh"));
                Check(CategoryFolders.NameFor("Comprimidos") == "压缩文件" && CategoryFolders.NameFor("Programas") == "程序", "Chino: " + CategoryFolders.NameFor("Comprimidos"));
                string other = Path.Combine(Home, "idmclone_xx.lng");
                File.WriteAllText(other, "[IDMClone]\r\nLANGUAGE=Otro\r\nCODE=xx\r\n[Textos]\r\nAbrir=Ouvrir\r\n");
                Lang.LoadFile(other);
                Check(CategoryFolders.NameFor("Documentos") == "Documents", "Sin la clave: " + CategoryFolders.NameFor("Documentos"));
                Check(CategoryFolders.NameFor("General") == null, "General no es una carpeta de categoría");
                // La configuración por defecto ya sale con los nombres del idioma.
                Lang.Reset();
                Check(FolderOf(AppConfig.CreateDefault(), "Vídeo").EndsWith("\\Vídeos"), "CreateDefault en español");
            }
            finally { Lang.Reset(); }
        }

        static void TCI_Rename()
        {
            UseFreshHome("cfi-rename");
            string root = FreshRoot("rename");
            AppConfig c = OldFoldersConfig(root);
            Directory.CreateDirectory(Path.Combine(root, "Compressed")); File.WriteAllText(Path.Combine(root, @"Compressed\a.zip"), "zip");
            Directory.CreateDirectory(Path.Combine(root, "Documents")); File.WriteAllText(Path.Combine(root, @"Documents\b.pdf"), "pdf");
            Directory.CreateDirectory(Path.Combine(root, "Music")); // vacía
            // Video y Programs no existen: solo cambia la ruta (la carpeta se crea con la próxima descarga, como siempre).
            c.SaveAsHistory = new List<string> { Path.Combine(root, @"Compressed\sub"), Path.Combine(root, @"Comprimidos\sub"), @"C:\otra" };
            DownloadManager m = new DownloadManager(c);
            DownloadItem it = new DownloadItem(); it.Id = "cfi1"; it.Url = "http://x/a.zip"; it.FilePath = Path.Combine(root, @"Compressed\a.zip"); it.Status = DownloadStatus.Complete;
            m.Items.Add(it); m.SaveList(); it.SaveState();
            Lang.Reset();
            bool changed;
            List<string> problems = CategoryFolders.Sync(c, m, null, out changed);
            Check(changed && problems.Count == 0, "Avisos: " + string.Join(" / ", problems.ToArray()));
            Check(File.Exists(Path.Combine(root, @"Comprimidos\a.zip")) && !Directory.Exists(Path.Combine(root, "Compressed")), "Comprimidos no se renombró con su archivo");
            Check(File.Exists(Path.Combine(root, @"Documentos\b.pdf")) && Directory.Exists(Path.Combine(root, "Música")), "Documentos / Música");
            Check(!Directory.Exists(Path.Combine(root, "Vídeos")) && FolderOf(c, "Vídeo") == Path.Combine(root, "Vídeos"), "Vídeo: " + FolderOf(c, "Vídeo"));
            Check(FolderOf(c, "Programas") == Path.Combine(root, "Programas") && FolderOf(c, "General") == root, "Programas / General");
            Check(it.FilePath == Path.Combine(root, @"Comprimidos\a.zip"), "La descarga: " + it.FilePath);
            Check(string.Join("|", c.SaveAsHistory.ToArray()) == Path.Combine(root, @"Comprimidos\sub") + @"|C:\otra", "Historial: " + string.Join("|", c.SaveAsHistory.ToArray()));
            // Al recargar la lista (el estado de cada descarga manda sobre la lista), la ruta nueva.
            DownloadManager m2 = new DownloadManager(c); m2.LoadList();
            DownloadItem again = m2.Items.Find(delegate(DownloadItem x) { return x.Id == "cfi1"; });
            Check(again != null && again.FilePath == Path.Combine(root, @"Comprimidos\a.zip"), "Tras recargar: " + (again != null ? again.FilePath : "no está"));
            // Otra vez: ya está en su idioma, no hace nada.
            CategoryFolders.Sync(c, m, null, out changed);
            Check(!changed, "La segunda vez no debería cambiar nada");
        }

        static void TCI_Conflict()
        {
            UseFreshHome("cfi-conflict");
            string root = FreshRoot("conflict");
            AppConfig c = OldFoldersConfig(root);
            foreach (string d in new string[] { "Compressed", "Comprimidos", "Music", "Música" }) Directory.CreateDirectory(Path.Combine(root, d));
            File.WriteAllText(Path.Combine(root, @"Compressed\viejo.zip"), "1");
            File.WriteAllText(Path.Combine(root, @"Comprimidos\nuevo.zip"), "2");
            File.WriteAllText(Path.Combine(root, @"Música\c.mp3"), "3"); // Music está vacía
            Lang.Reset();
            bool changed;
            List<string> problems = CategoryFolders.Sync(c, new DownloadManager(c), null, out changed);
            Check(problems.Count == 1 && problems[0].Contains("Compressed") && problems[0].Contains("con archivos"), "Avisos: " + string.Join(" / ", problems.ToArray()));
            Check(FolderOf(c, "Comprimidos") == Path.Combine(root, "Compressed") && File.Exists(Path.Combine(root, @"Compressed\viejo.zip")) && File.Exists(Path.Combine(root, @"Comprimidos\nuevo.zip")), "Comprimidos no debía tocarse");
            Check(!Directory.Exists(Path.Combine(root, "Music")) && FolderOf(c, "Música") == Path.Combine(root, "Música") && File.Exists(Path.Combine(root, @"Música\c.mp3")), "Music vacía: se quita y se usa Música");
        }

        static void TCI_Custom()
        {
            UseFreshHome("cfi-custom");
            string root = FreshRoot("custom");
            AppConfig c = OldFoldersConfig(root);
            string mine = Path.Combine(root, "MisZips"), outside = Path.Combine(Home, "fuera-" + Guid.NewGuid().ToString("N").Substring(0, 6), "Video");
            foreach (Category x in c.Categories) { if (x.Name == "Comprimidos") x.Folder = mine; if (x.Name == "Vídeo") x.Folder = outside; }
            Directory.CreateDirectory(mine); Directory.CreateDirectory(outside);
            Lang.Reset();
            bool changed;
            CategoryFolders.Sync(c, new DownloadManager(c), null, out changed);
            Check(FolderOf(c, "Comprimidos") == mine && Directory.Exists(mine), "MisZips se tocó");
            Check(FolderOf(c, "Vídeo") == outside && Directory.Exists(outside), "Una carpeta Video fuera de Descargas se tocó");
            Check(FolderOf(c, "Documentos") == Path.Combine(root, "Documentos"), "Las demás sí");
        }

        static void TCI_Busy()
        {
            UseFreshHome("cfi-busy");
            string root = FreshRoot("busy");
            AppConfig c = OldFoldersConfig(root); c.UseTempFolder = false; // se escribe directamente en la carpeta
            DownloadManager m = new DownloadManager(c);
            try
            {
                DownloadItem it = m.Add(S.Url("range,veryslow", "grande.zip", 8L * 1024 * 1024, NewId("cfibusy")), Path.Combine(root, "Compressed"), null, true);
                WaitProgress(it, 200 * 1024, 30);
                Lang.Reset();
                bool changed;
                List<string> problems = CategoryFolders.Sync(c, m, null, out changed);
                Check(problems.Exists(delegate(string p) { return p.Contains("Compressed") && p.Contains("descarga en curso"); }), "Avisos: " + string.Join(" / ", problems.ToArray()));
                Check(FolderOf(c, "Comprimidos") == Path.Combine(root, "Compressed") && Directory.Exists(Path.Combine(root, "Compressed")), "Se renombró con la descarga en marcha");
            }
            finally { m.StopAllAndWait(10000); }
        }

        static void TCI_SetLanguage()
        {
            MainForm f = NewMainForm();
            Action<string> savedNotice = MainForm.FolderNoticeHook;
            List<string> notices = new List<string>();
            MainForm.FolderNoticeHook = delegate(string t) { notices.Add(t); };
            try
            {
                string langDir = Path.Combine(AppConfig.Dir, "Languages");
                Directory.CreateDirectory(langDir);
                File.Copy(EnglishFile(), Path.Combine(langDir, "idmclone_en.lng"), true);
                AppConfig c = Field<AppConfig>(f, "_cfg");
                string root = FreshRoot("setlang");
                c.DefaultFolder = root;
                foreach (Category x in c.Categories)
                {
                    if (x.Name == Category.GeneralName) x.Folder = root;
                    else if (CategoryFolders.NameFor(x.Name) != null) x.Folder = Path.Combine(root, CategoryFolders.NameFor(x.Name));
                }
                Directory.CreateDirectory(Path.Combine(root, "Comprimidos"));
                File.WriteAllText(Path.Combine(root, @"Comprimidos\x.zip"), "x");
                f.SetLanguage("English");
                Check(File.Exists(Path.Combine(root, @"Compressed\x.zip")) && !Directory.Exists(Path.Combine(root, "Comprimidos")), "No pasó a Compressed");
                Check(FolderOf(c, "Comprimidos") == Path.Combine(root, "Compressed") && FolderOf(c, "Vídeo") == Path.Combine(root, "Video"), "Rutas en inglés");
                Check(FolderOf(AppConfig.Load(), "Comprimidos") == Path.Combine(root, "Compressed"), "No se guardó en la configuración");
                f.SetLanguage(Lang.BuiltIn);
                Check(File.Exists(Path.Combine(root, @"Comprimidos\x.zip")) && FolderOf(c, "Vídeo") == Path.Combine(root, "Vídeos"), "No volvió a español");
                Check(notices.Count == 0, "Avisos: " + string.Join(" / ", notices.ToArray()));
            }
            finally { MainForm.FolderNoticeHook = savedNotice; Lang.Reset(); DisposeMainForm(f); }
        }

        static void TCI_Installer()
        {
            string savedDl = AppConfig.DownloadsDirOverride;
            try
            {
                // Instalador en inglés (Windows en inglés): IDMClone empezará en inglés, con Compressed...
                UseFreshHome("cfi-setup-en");
                string root = FreshRoot("setup-en");
                AppConfig.DownloadsDirOverride = root;
                Lang.LoadFile(EnglishFile());
                IDMClone.Setup.Installer.FirstConfig();
                AppConfig c = AppConfig.Load();
                Check(c.Language == "English", "Idioma: " + c.Language);
                foreach (string d in new string[] { "Compressed", "Documents", "Music", "Video", "Programs" })
                    Check(Directory.Exists(Path.Combine(root, d)), "Falta la carpeta " + d);
                // En español: Comprimidos... y el idioma del programa (null).
                UseFreshHome("cfi-setup-es");
                root = FreshRoot("setup-es");
                AppConfig.DownloadsDirOverride = root;
                Lang.Reset();
                IDMClone.Setup.Installer.FirstConfig();
                c = AppConfig.Load();
                Check(c.Language == null && FolderOf(c, "Vídeo") == Path.Combine(root, "Vídeos"), "Español: " + c.Language + " " + FolderOf(c, "Vídeo"));
                foreach (string d in new string[] { "Comprimidos", "Documentos", "Música", "Vídeos", "Programas" })
                    Check(Directory.Exists(Path.Combine(root, d)), "Falta la carpeta " + d);
                // Reinstalar con configuración: ni el idioma ni las carpetas cambian.
                c.Language = "日本語"; c.Save();
                Directory.Delete(Path.Combine(root, "Programas"));
                Lang.LoadFile(EnglishFile());
                IDMClone.Setup.Installer.FirstConfig();
                Check(AppConfig.Load().Language == "日本語" && !Directory.Exists(Path.Combine(root, "Programas")), "Con configuración no debía tocar nada");
            }
            finally { AppConfig.DownloadsDirOverride = savedDl; Lang.Reset(); }
        }
    }
}
