using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace IDMClone.Tests
{
    // "Abrir carpeta" como IDM 6.43 (probado en un Sandbox): el archivo seleccionado en su carpeta, reutilizando la ventana
    // del Explorador que ya la muestre; sin archivo, su carpeta (nunca la de arriba). Nada se abre de verdad: Shell.OpenFolderHook.
    public static partial class TestMain
    {
        static void RunCarpeta()
        {
            Run("carpeta: qué se abre (archivo, archivo que aún no existe, carpeta, rutas vacías o no válidas, nombres con #,)", TC_Target);
            Run("carpeta: Shell.OpenFolder y OpenFolderWindow pasan por el hook con la ruta y el modo", TC_Hook);
            Run("carpeta: menú de la lista con una descarga sin archivo final -> su carpeta, no la de arriba", TC_ListMenu);
            Run("carpeta: Examinar de una categoría crea la carpeta y la abre", TC_BrowseCategory);
        }

        // Guarda el hook global de las pruebas, recoge lo que se abriría y lo deja como estaba.
        static List<string> CaptureFolders(Action body)
        {
            List<string> got = new List<string>();
            Action<string, bool> saved = Shell.OpenFolderHook;
            Shell.OpenFolderHook = delegate(string p, bool select) { got.Add((select ? "sel:" : "dir:") + p); };
            try { body(); }
            finally { Shell.OpenFolderHook = saved; }
            return got;
        }

        static void TC_Target()
        {
            string dir = Path.Combine(Dl, "carpeta-destino");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "informe #1, año.pdf");
            File.WriteAllText(file, "x");
            bool sel;
            Check(Shell.FolderTarget(file, out sel) == file && sel, "Archivo que existe: se selecciona");
            Check(Shell.FolderTarget(Path.Combine(dir, "aun-descargando.zip"), out sel) == dir && !sel, "Archivo que no existe: su carpeta, no la de arriba");
            Check(Shell.FolderTarget(dir, out sel) == dir && !sel, "Una carpeta: ella misma");
            Check(Shell.FolderTarget(dir + "\\", out sel) != null && !sel, "Carpeta con \\ final");
            foreach (string bad in new string[] { null, "", "   ", "C:\\no\\existe\\nada.zip", "C:\\a<b>|c?.zip", "x" })
                Check(Shell.FolderTarget(bad, out sel) == null && !sel, "Nada que abrir: " + (bad ?? "null"));
        }

        static void TC_Hook()
        {
            string dir = Path.Combine(Dl, "carpeta-hook");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "vídeo final.mp4");
            File.WriteAllText(file, "x");
            List<string> got = CaptureFolders(delegate
            {
                Shell.OpenFolder(file);
                Shell.OpenFolder(Path.Combine(dir, "falta.mp4"));
                Shell.OpenFolder("");
                Shell.OpenFolder(null);
                Shell.OpenFolder("C:\\no\\existe\\nada.zip");
                Shell.OpenFolderWindow(dir);
                Shell.OpenFolderWindow("");
            });
            Check(string.Join("|", got.ToArray()) == "sel:" + file + "|dir:" + dir + "|dir:" + dir, "Lo que se abriría: " + string.Join("|", got.ToArray()));
        }

        // Antes: Path.Combine(carpeta, "") llegaba a Shell como si fuera un archivo y se abría la carpeta de arriba
        // (con la descarga aún en DwnlData); con la ruta vacía, una excepción en el menú.
        static void TC_ListMenu()
        {
            MainForm f = NewMainForm();
            try
            {
                string dir = Path.Combine(Dl, "carpeta-lista");
                Directory.CreateDirectory(dir);
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                DownloadItem pending = new DownloadItem(); pending.Id = "pend1"; pending.Url = "http://x/aun-descargando.zip";
                pending.FilePath = Path.Combine(dir, "aun-descargando.zip"); pending.Status = DownloadStatus.Paused;
                DownloadItem done = new DownloadItem(); done.Id = "done1"; done.Url = "http://x/listo.zip";
                done.FilePath = Path.Combine(dir, "listo.zip"); done.Status = DownloadStatus.Complete;
                File.WriteAllText(done.FilePath, "x");
                DownloadItem empty = new DownloadItem(); empty.Id = "empty1"; empty.Url = "http://x/sin-ruta"; empty.FilePath = "";
                m.Items.Add(pending); m.Items.Add(done); m.Items.Add(empty);
                typeof(MainForm).GetMethod("RefreshList", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(f, null);
                ListView list = Field<ListView>(f, "_list");
                Check(list.Items.Count == 3, "Filas en la lista: " + list.Items.Count);
                IntPtr handle = list.Handle; // sin ventana creada, ListView no guarda la selección (no se muestra nada)
                ToolStripItem folder = null;
                foreach (ToolStripItem i in list.ContextMenuStrip.Items) if (i.Text == "Abrir carpeta") folder = i;
                Check(folder != null, "Falta 'Abrir carpeta' en el menú de la lista");
                List<string> got = CaptureFolders(delegate
                {
                    foreach (string id in new string[] { "pend1", "done1", "empty1" })
                    {
                        foreach (ListViewItem lvi in list.Items) lvi.Selected = ((DownloadItem)lvi.Tag).Id == id;
                        Check(list.SelectedItems.Count == 1, "Seleccionadas: " + list.SelectedItems.Count);
                        folder.PerformClick();
                    }
                });
                Check(string.Join("|", got.ToArray()) == "dir:" + dir + "|sel:" + done.FilePath, "Lo que se abriría: " + string.Join("|", got.ToArray()));
            }
            finally { DisposeMainForm(f); }
        }

        static void TC_BrowseCategory()
        {
            MainForm f = NewMainForm();
            try
            {
                Category c = new Category(); c.Name = "Prueba"; c.Folder = Path.Combine(Dl, "categoria-nueva");
                MethodInfo browse = typeof(MainForm).GetMethod("BrowseCategory", BindingFlags.NonPublic | BindingFlags.Instance);
                List<string> got = CaptureFolders(delegate { browse.Invoke(f, new object[] { c }); });
                Check(Directory.Exists(c.Folder), "La carpeta de la categoría no se creó");
                Check(string.Join("|", got.ToArray()) == "dir:" + c.Folder, "Lo que se abriría: " + string.Join("|", got.ToArray()));
            }
            finally { DisposeMainForm(f); }
        }
    }
}
