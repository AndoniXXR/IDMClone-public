using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace IDMClone.Tests
{
    // Tests de la gestión de categorías (Agregar categoría / Propiedades / Eliminar, como IDM).
    public static partial class TestMain
    {
        static void RunCategories()
        {
            Run("categorías: tipos, sitios y prioridad de las limitadas a sitios", TC_Matching);
            Run("categorías: validar, aplicar (un tipo en una sola categoría) y General fija", TC_ApplyValidate);
            Run("categorías: se guardan y se cargan (sitios, recordar ruta, General)", TC_Persist);
            Run("categorías: una nueva crea su carpeta y recibe las descargas", TC_NewCategoryDownload);
            Run("categorías: ventana Agregar/Propiedades (avisos, crear carpeta)", TC_Form);
            Run("categorías: renombrar, asociar descargas antiguas y eliminar", TC_RenameAssignDelete);
            Run("categorías: Opciones > Guardar en por categoría y «recordar la última ruta»", TC_OptionsAndRemember);
        }

        static void TC_Matching()
        {
            AppConfig c = Cfg();
            Category v = new Category("Vídeos de ejemplo", Path.Combine(Dl, "ej"), "mp4 webm"); v.Sites = "ejemplo.com *.cdn-ej.net";
            c.Categories.Add(v);
            Check(c.CategoryNameFor("a.mp4", "https://ejemplo.com/v/a.mp4", null) == "Vídeos de ejemplo", "mp4 de ejemplo.com no fue a su categoría");
            Check(c.CategoryNameFor("a.mp4", "https://www.ejemplo.com/a.mp4", null) == "Vídeos de ejemplo", "subdominio no coincide");
            Check(c.CategoryNameFor("a.mp4", "https://x1.cdn-ej.net/a.mp4", null) == "Vídeos de ejemplo", "comodín de sitio no coincide");
            Check(c.CategoryNameFor("a.mp4", "https://cdn.otro.com/a.mp4", "https://ejemplo.com/pagina") == "Vídeos de ejemplo", "no tuvo en cuenta la página de origen");
            Check(c.CategoryNameFor("a.mp4", "https://otro.com/a.mp4", null) == "Vídeo", "mp4 de otro sitio debería ir a Vídeo");
            Check(c.CategoryNameFor("a.mp4") == "Vídeo", "sin URL debería ir a Vídeo");
            Check(c.CategoryNameFor("a.zip", "https://ejemplo.com/a.zip", null) == "Comprimidos", "zip de ejemplo.com debería ir a Comprimidos");
            Check(c.CategoryNameFor("foto.JPG") == "General", "jpg sin categoría debería ir a General");
            Check(c.FolderFor("foto.jpg") == c.DefaultFolder, "General debería usar la carpeta por defecto");
            Category img = new Category("Imágenes", Path.Combine(Dl, "img"), "jpg jpeg png gif web*");
            c.Categories.Add(img);
            Check(c.CategoryNameFor("foto.JPG") == "Imágenes" && c.CategoryNameFor("x.webp") == "Imágenes", "Imágenes no recoge jpg/webp (comodín)");
            Check(c.FolderFor("foto.png") == img.Folder, "Imágenes no da su carpeta");
        }

        static void TC_ApplyValidate()
        {
            AppConfig c = Cfg();
            Category general = c.FindCategory("General");
            Category docs = c.FindCategory("Documentos");
            Category n = new Category("", Dl, "pdf");
            Check(c.ValidateCategory(n, null).Contains("Nombre de categoría"), "Nombre vacío aceptado");
            n.Name = "documentos";
            Check(c.ValidateCategory(n, null).Contains("Ya existe"), "Nombre repetido aceptado");
            n.Name = "a/b";
            Check(c.ValidateCategory(n, null) != null, "Nombre con / aceptado");
            n.Name = "PDF"; n.Folder = "";
            Check(c.ValidateCategory(n, null) != null, "Carpeta vacía aceptada");
            n.Folder = Path.Combine(Dl, "pdf");
            Check(c.ValidateCategory(n, null) == null, "Categoría válida rechazada");
            Check(c.ValidateCategory(docs.Clone(), docs) == null, "Editar sin renombrar no debería chocar consigo misma");
            Category g2 = general.Clone(); g2.Name = "Otra";
            Check(c.ValidateCategory(g2, general) != null, "General se pudo renombrar");

            n.Extensions = ".PDF, epub";
            Category added = c.ApplyCategory(null, n);
            Check(c.Categories.Contains(added) && added.Extensions == "pdf epub", "Extensiones mal normalizadas: " + added.Extensions);
            Check(!docs.ExtensionList().Contains("pdf"), "pdf siguió en Documentos");
            Check(c.CategoryNameFor("libro.pdf") == "PDF", "pdf no va a la categoría nueva");
            // Una limitada a sitios no quita el tipo a las demás.
            Category s = new Category("Docs del trabajo", Path.Combine(Dl, "t"), "docx"); s.Sites = "intranet.local";
            c.ApplyCategory(null, s);
            Check(docs.ExtensionList().Contains("docx"), "Una categoría con sitios quitó docx a Documentos");

            Category ge = general.Clone(); ge.Folder = Path.Combine(Dl, "general2"); ge.Extensions = "zip";
            c.ApplyCategory(general, ge);
            Check(c.DefaultFolder == ge.Folder && general.Extensions == "", "General: carpeta no sincronizada o aceptó tipos");
            Check(c.FindCategory("Comprimidos").ExtensionList().Contains("zip"), "General quitó tipos a otra categoría");
            Check(!c.RemoveCategory(general) && c.FindCategory("General") != null, "Se pudo eliminar General");
            Check(c.RemoveCategory(added) && c.CategoryNameFor("libro.pdf") == "General", "Eliminar no funcionó");
        }

        static void TC_Persist()
        {
            UseFreshHome("cat-persist");
            AppConfig c = Cfg();
            Category s = new Category("Vídeos de ejemplo", Path.Combine(Dl, "ej"), "mp4"); s.Sites = "ejemplo.com"; s.RememberLastPath = true;
            c.Categories.Add(s);
            c.Categories.RemoveAll(delegate(Category x) { return x.IsGeneral; }); // config antigua o editada sin General
            c.Save();
            AppConfig r = AppConfig.Load();
            Category rs = r.FindCategory("Vídeos de ejemplo");
            Check(rs != null && rs.Sites == "ejemplo.com" && rs.RememberLastPath, "Sitios/recordar no se guardaron");
            Category g = r.FindCategory("General");
            Check(g != null && r.Categories[0] == g && g.Folder == r.DefaultFolder, "General no se repuso al cargar");
        }

        static void TC_NewCategoryDownload()
        {
            DownloadManager m = NewMgr();
            string host = new Uri(S.Url("range", "x.bin", 1, "x")).Host;
            string imgDir = Path.Combine(Dl, "Images-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            string siteDir = Path.Combine(Dl, "Sitio-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            m.Config.ApplyCategory(null, new Category("Imágenes", imgDir, "jpg png"));
            Category site = new Category("Del servidor de pruebas", siteDir, "mp4"); site.Sites = host;
            m.Config.ApplyCategory(null, site);
            Check(!Directory.Exists(imgDir), "La carpeta se creó antes de tiempo");

            DownloadItem a = m.Add(S.Url("range", "foto.jpg", 48 * 1024, NewId("img")), null, null, true);
            DownloadItem b = m.Add(S.Url("range", "clip.mp4", 48 * 1024, NewId("vid")), null, null, true);
            WaitEnd(a, 20); WaitEnd(b, 20);
            Check(a.Status == DownloadStatus.Complete && a.Category == "Imágenes", "jpg: " + a.Status + " / " + a.Category);
            Check(File.Exists(Path.Combine(imgDir, "foto.jpg")), "foto.jpg no está en la carpeta de Imágenes: " + a.FilePath);
            Check(b.Category == "Del servidor de pruebas" && File.Exists(Path.Combine(siteDir, "clip.mp4")), "mp4 del sitio: " + b.Category + " " + b.FilePath);

            // El servidor da otro nombre (Content-Disposition, un .pdf): se recoloca en la categoría de ese sitio.
            string repDir = Path.Combine(Dl, "Informes-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Category rep = new Category("Informes", repDir, "pdf"); rep.Sites = host;
            m.Config.ApplyCategory(null, rep);
            DownloadItem c = m.Add(S.Url("range,cd", "descargar.php", 32 * 1024, NewId("cd")), null, null, true);
            WaitEnd(c, 20);
            Check(c.Status == DownloadStatus.Complete && c.Category == "Informes", "cd: " + c.Status + " " + c.Category);
            Check(Path.GetDirectoryName(c.FilePath) == repDir && File.Exists(c.FilePath), "cd no fue a la carpeta de Informes: " + c.FilePath);
        }

        static void TC_Form()
        {
            AppConfig c = Cfg();
            List<string> said = new List<string>();
            Func<string, bool> savedConfirm = MainForm.ConfirmHook;
            CategoryForm.MessageHook = delegate(string t) { said.Add(t); };
            try
            {
                using (CategoryForm f = new CategoryForm(c, null))
                {
                    IntPtr h = f.Handle;
                    Check(f.Text == "Agregando categoría", "Título: " + f.Text);
                    f.Set("", "jpg", "", Dl, false);
                    Check(!f.Accept() && said.Count == 1 && said[0].Contains("Nombre de categoría"), "Sin nombre: " + string.Join("|", said.ToArray()));
                    // Al escribir el nombre propone una subcarpeta de la carpeta por defecto.
                    Field<TextBox>(f, "_name").Text = "Imágenes";
                    Check(Field<TextBox>(f, "_folder").Text == Path.Combine(c.DefaultFolder, "Imágenes"), "Carpeta propuesta: " + Field<TextBox>(f, "_folder").Text);
                    string dir = Path.Combine(Dl, "nueva-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                    f.Set("Imágenes", "jpg png", "", dir, true);
                    MainForm.ConfirmHook = delegate(string q) { return false; };
                    Check(!f.Accept() && !Directory.Exists(dir), "Creó la carpeta sin permiso");
                    string asked = null;
                    MainForm.ConfirmHook = delegate(string q) { asked = q; return true; };
                    Check(f.Accept() && Directory.Exists(dir), "No creó la carpeta");
                    Check(asked != null && asked.Contains("no existe"), "No preguntó si crear la carpeta");
                    Check(f.Result.Name == "Imágenes" && f.Result.RememberLastPath && f.Result.Extensions == "jpg png", "Resultado incorrecto");
                }
                using (CategoryForm f = new CategoryForm(c, c.FindCategory("General")))
                {
                    IntPtr h = f.Handle;
                    Check(f.Text == "Editando propiedades de categoría", "Título: " + f.Text);
                    Check(Field<TextBox>(f, "_name").ReadOnly && !Field<TextBox>(f, "_exts").Enabled, "General permite cambiar nombre o tipos");
                    f.Set("X", "zip", "", Dl, false);
                    Check(f.Accept() && f.Result.Name == "General" && f.Result.Extensions == "", "General editada mal");
                }
            }
            finally { CategoryForm.MessageHook = null; MainForm.ConfirmHook = savedConfirm; }
        }

        static void TC_RenameAssignDelete()
        {
            MainForm f = NewMainForm();
            Func<string, bool> savedConfirm = MainForm.ConfirmHook;
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                AppConfig c = m.Config;
                DownloadItem jpg = m.Add(S.Url("range", "vieja.jpg", 1024, NewId("o1")), null, null, false);
                DownloadItem pdf = m.Add(S.Url("range", "viejo.pdf", 1024, NewId("o2")), null, null, false);
                Check(jpg.Category == "General" && pdf.Category == "Documentos", "Categorías iniciales");

                // Nueva categoría con jpg: pregunta si asociar las descargas antiguas.
                string asked = null;
                MainForm.ConfirmHook = delegate(string q) { asked = q; return true; };
                Category img = f.ApplyCategoryEdit(null, new Category("Imágenes", Path.Combine(Dl, "img"), "jpg"));
                Check(asked != null && asked.Contains("jpg"), "No ofreció asociar las descargas antiguas");
                Check(jpg.Category == "Imágenes" && pdf.Category == "Documentos", "Asociar antiguas: " + jpg.Category + " / " + pdf.Category);
                TreeView tree = Field<TreeView>(f, "_tree");
                Check(tree.SelectedNode != null && (string)tree.SelectedNode.Tag == "cat:Imágenes", "No se seleccionó la categoría nueva en el árbol");

                // Renombrar: las descargas la siguen.
                Category edit = img.Clone(); edit.Name = "Fotos";
                asked = null;
                f.ApplyCategoryEdit(img, edit);
                Check(jpg.Category == "Fotos" && c.FindCategory("Imágenes") == null, "Renombrar no movió las descargas");
                Check(asked == null, "Volvió a preguntar sin cambiar los tipos");
                Check((string)tree.SelectedNode.Tag == "cat:Fotos", "El filtro no siguió el nombre nuevo");

                // Eliminar: pregunta y las descargas vuelven a la que les toque.
                MainForm.ConfirmHook = delegate(string q) { asked = q; return false; };
                Check(!f.DeleteCategory(c.FindCategory("Fotos")) && c.FindCategory("Fotos") != null, "Eliminó sin confirmar");
                MainForm.ConfirmHook = delegate(string q) { asked = q; return true; };
                Check(f.DeleteCategory(c.FindCategory("Fotos")), "No eliminó");
                Check(asked.Contains("Realmente quieres eliminar"), "Pregunta: " + asked);
                Check(jpg.Category == "General" && c.FindCategory("Fotos") == null, "Tras eliminar: " + jpg.Category);
                Check((string)tree.SelectedNode.Tag == "__all__", "El árbol sigue en la categoría eliminada");
                Check(!f.DeleteCategory(c.FindCategory("General")), "Se pudo eliminar General");

                // Menú del árbol: en una categoría se puede eliminar; en General no.
                ContextMenuStrip menu = tree.ContextMenuStrip;
                bool hasAdd = false; if (menu != null) foreach (ToolStripItem mi in menu.Items) if (mi.Text.StartsWith("Agregar categoría")) hasAdd = true; // IDM: Examinar, Propiedades | Agregar categoría | Eliminar
                Check(hasAdd, "Falta el menú del panel");
            }
            finally { MainForm.ConfirmHook = savedConfirm; DisposeMainForm(f); }
        }

        static void TC_OptionsAndRemember()
        {
            UseFreshHome("cat-opts");
            AppConfig c = Cfg();
            string vdir = Path.Combine(Dl, "videos-opc"), gdir = Path.Combine(Dl, "general-opc");
            using (OptionsForm o = new OptionsForm(c))
            {
                IntPtr h = o.Handle;
                o.SetCategoryFolder("Vídeo", vdir, true);
                o.SetCategoryFolder("General", gdir, false);
                o.SetCategoryFolder("Música", c.FindCategory("Música").Folder, false); // cambiar de categoría guarda lo anterior
                typeof(OptionsForm).GetMethod("Apply", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(o, null);
            }
            Check(c.FindCategory("Vídeo").Folder == vdir && c.FindCategory("Vídeo").RememberLastPath, "Vídeo no guardó su carpeta/recordar");
            Check(c.DefaultFolder == gdir && c.FindCategory("General").Folder == gdir, "General no cambió la carpeta por defecto");
            Check(c.FolderFor("x.jpg") == gdir, "Lo de General no va a la carpeta nueva");

            // "Recordar la última ruta": la casilla de Información del archivo sale marcada.
            DownloadManager m = new DownloadManager(c);
            string other = Path.Combine(Dl, "otra-carpeta");
            DownloadItem it = m.Add(S.Url("range", "peli.mp4", 1024, NewId("rem")), null, null, false);
            it.Pending = true;
            using (FileInfoForm fi = new FileInfoForm(c, m, it))
            {
                IntPtr h = fi.Handle;
                Check(Field<CheckBox>(fi, "_remember").Checked, "No vino marcada «Recordar esta ruta» con la opción de la categoría");
                ComboBox cat = Field<ComboBox>(fi, "_cat");
                cat.SelectedItem = "Música";
                Check(!Field<CheckBox>(fi, "_remember").Checked, "Música no tiene la opción y siguió marcada");
                cat.SelectedItem = "Vídeo";
                Directory.CreateDirectory(other);
                Field<TextBox>(fi, "_path").Text = Path.Combine(other, "peli.mp4");
                fi.Later();
            }
            Check(c.FindCategory("Vídeo").Folder == other, "La categoría no recordó la última carpeta: " + c.FindCategory("Vídeo").Folder);
        }
    }
}
