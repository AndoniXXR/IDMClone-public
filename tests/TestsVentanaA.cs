using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace IDMClone.Tests
{
    // Ventana principal como la de IDM 6.43 (capturas\idm 01, 02, 03, 12a, 35, 60-65, 76, 77).
    public static partial class TestMain
    {
        static void RunVentanaA()
        {
            Run("ventana principal IDM: árbol (nombres, orden, sin General, colas)", TVA_Tree);
            Run("ventana principal IDM: menús Tareas, Archivo, Descargas, Ver y Ayuda", TVA_Menus);
            Run("ventana principal IDM: lista con Tahoma 8, cuadrícula, tamaños y fechas de IDM", TVA_List);
            Run("ventana principal IDM: menús de clic derecho (lista, cabecera, árbol, cola) y bandeja", TVA_ContextMenus);
            Run("ventana principal IDM: Buscar y Buscar Siguiente", TVA_Find);
        }

        static List<string> MenuTexts(ToolStripItemCollection items)
        {
            List<string> r = new List<string>();
            foreach (ToolStripItem i in items) r.Add(i is ToolStripSeparator ? "-" : i.Text);
            return r;
        }

        static string Joined(ToolStripItemCollection items) { return string.Join("|", MenuTexts(items).ToArray()); }

        static void TVA_Tree()
        {
            MainForm f = NewMainForm();
            try
            {
                TreeView tree = Field<TreeView>(f, "_tree");
                List<string> top = new List<string>();
                foreach (TreeNode n in tree.Nodes) top.Add(n.Text);
                Check(string.Join("|", top.ToArray()) == "Todas las descargas|No finalizadas|Finalizadas|Proyectos Grabber|Colas", "Raíz: " + string.Join("|", top.ToArray()));
                List<string> cats = new List<string>();
                foreach (TreeNode n in tree.Nodes[0].Nodes) cats.Add(n.Text);
                Check(string.Join("|", cats.ToArray()) == "Comprimido|Documentos|Música|Programas|Video", "Categorías: " + string.Join("|", cats.ToArray()));
                Check(tree.Nodes[1].Nodes.Count == 5 && !tree.Nodes[1].IsExpanded, "No finalizadas debería tener sus categorías, plegadas");
                Check(tree.Nodes[4].Nodes[0].Text == "Descarga principal de la cola" && tree.Nodes[4].Nodes[1].Text == "Sincronización de la cola", "Colas: " + tree.Nodes[4].Nodes[0].Text);
                // Los nombres guardados no cambian (solo lo que se ve).
                AppConfig c = Field<AppConfig>(f, "_cfg");
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                Check(c.FindCategory("Comprimidos") != null && c.FindCategory("Vídeo") != null && m.Queues.Main.Name == "Cola principal", "Se cambiaron los nombres guardados");
                Check((string)tree.Nodes[0].Nodes[4].Tag == "cat:Vídeo", "Etiqueta del nodo Video: " + tree.Nodes[0].Nodes[4].Tag);
                // Filtros "No finalizadas > categoría".
                DownloadItem zip = m.Add(S.Url("range", "arbol.zip", 1024, NewId("tz")), Dl, null, false);
                ListView list = Field<ListView>(f, "_list");
                IntPtr th = tree.Handle, lh = list.Handle; // ventana oculta: el árbol avisa del cambio solo con su control nativo
                tree.SelectedNode = tree.Nodes[1].Nodes[0];
                Check(list.Items.Count == 1 && list.Items[0].Tag == zip, "No finalizadas > Comprimido: " + list.Items.Count);
                tree.SelectedNode = tree.Nodes[2].Nodes[0];
                Check(list.Items.Count == 0, "Finalizadas > Comprimido: " + list.Items.Count);
                // Crear nueva cola (desde el árbol o "Añadir a la cola").
                MainForm.QueueNameHook = delegate(string s) { Check(s == "Cola # 3", "Nombre propuesto: " + s); return "Nocturna"; };
                QueueDef q = f.CreateQueue();
                Check(q != null && q.Name == "Nocturna" && tree.Nodes[4].Nodes.Count == 3 && tree.Nodes[4].Nodes[2].Text == "Nocturna", "Crear nueva cola");
                Func<string, bool> saved = MainForm.ConfirmHook;
                MainForm.ConfirmHook = delegate(string s) { return true; };
                try { Check(f.DeleteQueue(q) && tree.Nodes[4].Nodes.Count == 2, "Eliminar cola"); Check(!f.DeleteQueue(m.Queues.Main), "Se pudo eliminar la cola principal"); }
                finally { MainForm.ConfirmHook = saved; }
            }
            finally { MainForm.QueueNameHook = null; DisposeMainForm(f); }
        }

        static void TVA_Menus()
        {
            MainForm f = NewMainForm();
            try
            {
                MenuStrip menu = Field<MenuStrip>(f, "_menu");
                Check(Joined(menu.Items) == "&Tareas|&Archivo|&Descargas|&Ver|A&yuda", "Menús: " + Joined(menu.Items));
                ToolStripMenuItem tasks = (ToolStripMenuItem)menu.Items[0], file = (ToolStripMenuItem)menu.Items[1], dl = (ToolStripMenuItem)menu.Items[2],
                    view = (ToolStripMenuItem)menu.Items[3], help = (ToolStripMenuItem)menu.Items[4];
                Check(Joined(tasks.DropDownItems) == "Agregar nueva descarga|Añadir descarga por lotes|Añadir descarga por lotes desde portapapeles|Iniciar captura del sitio|-|Mostrar arrastrar destino|-|Exportar|Importar|-|Salir", "Tareas: " + Joined(tasks.DropDownItems));
                Check(Joined(((ToolStripMenuItem)tasks.DropDownItems[7]).DropDownItems) == "A un archivo de exportación de IDM|A un archivo de texto", "Exportar");
                Check(Joined(((ToolStripMenuItem)tasks.DropDownItems[8]).DropDownItems) == "Desde un archivo de exportación de IDM|Desde un archivo de texto", "Importar");
                Check(Joined(file.DropDownItems) == "Detener descarga|Quitar|Descargar ahora|Redescargar", "Archivo: " + Joined(file.DropDownItems));
                Check(Joined(dl.DropDownItems) == "Pausar todas|Detener todas|-|Eliminar todas las completadas|-|Buscar (Ctrl+F)|Buscar Siguiente (F3)|-|Planificador|Iniciar cola|detener cola|-|Limitador de Velocidad|-|Opciones", "Descargas: " + Joined(dl.DropDownItems));
                Check(((ToolStripMenuItem)dl.DropDownItems[5]).ShortcutKeys == (Keys.Control | Keys.F) && ((ToolStripMenuItem)dl.DropDownItems[6]).ShortcutKeys == Keys.F3, "Atajos de Buscar");
                ToolStripMenuItem start = (ToolStripMenuItem)dl.DropDownItems[9];
                start.ShowDropDown(); start.HideDropDown();
                Check(Joined(start.DropDownItems) == "Iniciar Descarga principal de la cola|Iniciar Sincronización de la cola", "Iniciar cola: " + Joined(start.DropDownItems));
                ToolStripMenuItem lim = (ToolStripMenuItem)dl.DropDownItems[12];
                Check(Joined(lim.DropDownItems) == "Activar|Desactivar|-|Configurar...", "Limitador: " + Joined(lim.DropDownItems));
                Check(Joined(view.DropDownItems) == "Ocultar categorías|Organizar archivos|Barra de herramientas|IDMClone bandeja de iconos|Personalizar lista de URL...|Fuente|-|Idioma", "Ver: " + Joined(view.DropDownItems));
                Check(Joined(((ToolStripMenuItem)view.DropDownItems[1]).DropDownItems) == "Por orden de agregado|Por nombre de archivo|Por tamaño|Por estado|Por tiempo restante|Por velocidad de transferencia|Por fecha de último intento|Por descripción|Por ruta de guardado|Por referido|Por Página Web Parental", "Organizar: " + Joined(((ToolStripMenuItem)view.DropDownItems[1]).DropDownItems));
                ToolStripMenuItem bar = (ToolStripMenuItem)view.DropDownItems[2];
                bar.ShowDropDown(); bar.HideDropDown();
                Check(Joined(bar.DropDownItems) == "Personalizar barra de herramientas...|-|Botones grandes|Botones pequeños|-|Neon|3D Style", "Barra: " + Joined(bar.DropDownItems));
                Check(Joined(((ToolStripMenuItem)view.DropDownItems[3]).DropDownItems) == "Estilo 3D|Estilo Clásico|No mostrar", "Bandeja");
                Check(Joined(((ToolStripMenuItem)view.DropDownItems[5]).DropDownItems) == "Elegir Fuente...|Restablecer fuente por defecto", "Fuente");
                Check(Joined(help.DropDownItems) == "Contenidos de ayuda|Programador y colas|Ayuda de Grabber|-|Comprobar actualizaciones...|-|Acerca de IDMClone", "Ayuda: " + Joined(help.DropDownItems));
                // Bandeja de iconos: "No mostrar" la oculta y se recuerda.
                f.SetTrayStyle("none");
                Check(!Field<NotifyIcon>(f, "_tray").Visible && AppConfig.Load().TrayIconStyle == "none", "No mostrar");
                f.SetTrayStyle("3d");
                Check(Field<NotifyIcon>(f, "_tray").Visible && AppConfig.Load().TrayIconStyle == null, "Estilo 3D");
                // Ver > Fuente: se aplica a la lista y se recuerda; "Restablecer" vuelve a Tahoma 8.
                ListView list = Field<ListView>(f, "_list");
                f.SetListFont(new Font("Verdana", 10f));
                Check(list.Font.Name == "Verdana" && AppConfig.Load().ListFont == "Verdana;10;0", "Fuente elegida: " + AppConfig.Load().ListFont);
                f.SetListFont(null);
                Check(list.Font.Name == "Tahoma" && AppConfig.Load().ListFont == null, "Restablecer fuente: " + list.Font.Name);
            }
            finally { DisposeMainForm(f); }
        }

        static void TVA_List()
        {
            MainForm f = NewMainForm();
            try
            {
                ListView list = Field<ListView>(f, "_list");
                Check(list.Font.Name == "Tahoma" && Math.Abs(list.Font.SizeInPoints - 8.25f) < 0.01f, "Letra de la lista: " + list.Font);
                Check(list.GridLines && list.FullRowSelect, "Sin cuadrícula o sin selección de fila completa");
                List<string> cols = new List<string>();
                foreach (ColumnHeader h in list.Columns) { cols.Add(h.Text); Check(h.TextAlign == HorizontalAlignment.Left, "Columna no alineada a la izquierda: " + h.Text); }
                Check(string.Join("|", cols.ToArray()) == "Nombre de Archivo|Q|Tamaño|Estado|Tiempo restante|Velocidad de transferencia|Última fecha de intento|Descripción", "Columnas: " + string.Join("|", cols.ToArray()));
                Check(list.Columns[0].Width == Dpi.Px(150) && list.Columns[1].Width == Dpi.Px(22), "Anchos: " + list.Columns[0].Width + " " + list.Columns[1].Width);
                Check(ListColumns.Size(32L * 1024 * 1024) == "32.00  MB" && ListColumns.Size(512) == "512  Bytes" && ListColumns.Size(1536) == "1.50  KB", "Tamaños: " + ListColumns.Size(32L * 1024 * 1024));
                Check(ListColumns.ListDate("2026-10-22 17:05:09") == "Oct 22 17:05:09 2026", "Fecha: " + ListColumns.ListDate("2026-10-22 17:05:09"));
                // Sin barra de estado; la lista llega hasta abajo.
                Check(f.GetType().GetField("_status", BindingFlags.NonPublic | BindingFlags.Instance) == null, "Sigue habiendo barra de estado");
                // "Q": icono de la cola, sin número.
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                IntPtr lh = list.Handle; // ventana oculta: el control nativo guarda los iconos
                DownloadItem it = m.Add(S.Url("range", "en-cola.bin", 1024, NewId("vq")), Dl, null, false);
                f.GetType().GetMethod("RefreshList", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(f, null);
                Check(list.Items.Count == 1 && list.Items[0].SubItems[1].Text == "", "Columna Q con texto: " + (list.Items.Count > 0 ? list.Items[0].SubItems[1].Text : "-"));
                Dictionary<ListViewItem, int> qi = Field<Dictionary<ListViewItem, int>>(f, "_qImage");
                Check(it.Queue != DownloadItem.NoQueue && qi.ContainsKey(list.Items[0]) && qi[list.Items[0]] == 1, "Sin icono de cola en Q");
            }
            finally { DisposeMainForm(f); }
        }

        static void TVA_ContextMenus()
        {
            MainForm f = NewMainForm();
            try
            {
                ListView list = Field<ListView>(f, "_list");
                Check(Joined(list.ContextMenuStrip.Items) == "Abrir|Abrir con...|Abrir carpeta|-|Mover/Renombrar (Ctrl-M)|-|Redescargar|-|Reanudar descarga|Detener descarga|-|Actualizar dirección de descarga|-|Quitar|-|Añadir a la cola|Borrar desde cola de descarga|-|Al hacer doble clic|-|propiedades",
                    "Menú de la lista: " + Joined(list.ContextMenuStrip.Items));
                ToolStripMenuItem dbl = (ToolStripMenuItem)list.ContextMenuStrip.Items[18];
                Check(Joined(dbl.DropDownItems) == "Abrir|propiedades", "Al hacer doble clic");
                Check(Joined(Field<ContextMenuStrip>(f, "_headerMenu").Items) == "Columnas...", "Menú de la cabecera");
                TreeView tree = Field<TreeView>(f, "_tree");
                MethodInfo fill = typeof(MainForm).GetMethod("FillTreeMenu", BindingFlags.NonPublic | BindingFlags.Instance);
                ToolStrip tmp = new ToolStrip();
                fill.Invoke(f, new object[] { tmp.Items, "cat:Vídeo" });
                Check(Joined(tmp.Items) == "Examinar|Propiedades|-|Agregar categoría|-|Eliminar" && tmp.Items[5].Enabled, "Menú de categoría: " + Joined(tmp.Items));
                tmp.Items.Clear(); fill.Invoke(f, new object[] { tmp.Items, "__all__" });
                Check(!tmp.Items[5].Enabled && tmp.Items[1].Enabled, "En Todas las descargas (General) no se puede eliminar");
                tmp.Items.Clear(); fill.Invoke(f, new object[] { tmp.Items, "q:0" });
                Check(Joined(tmp.Items) == "Iniciar ahora|detener|-|Editar cola|Programar|-|Eliminar|-|Crear nueva cola" && !tmp.Items[1].Enabled && !tmp.Items[6].Enabled, "Menú de cola: " + Joined(tmp.Items));
                tmp.Dispose();
                NotifyIcon tray = Field<NotifyIcon>(f, "_tray");
                Check(Joined(tray.ContextMenuStrip.Items) == "Ayuda|Acerca de IDMClone|-|Limitador de Velocidad|-|Mostrar ventana de enlaces|Restaurar|-|Salir", "Bandeja: " + Joined(tray.ContextMenuStrip.Items));
                ToolStripItem[] tb = (ToolStripItem[])typeof(MainForm).GetMethod("ToolbarMenuItems", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(f, null);
                List<string> t = new List<string>(); foreach (ToolStripItem i in tb) t.Add(i is ToolStripSeparator ? "-" : i.Text);
                Check(string.Join("|", t.ToArray()) == "Personalizar...|-|Botones pequeños|Botones grandes|-|Neon|3D Style", "Barra: " + string.Join("|", t.ToArray()));
                Check(IdmMenuRenderer.IsRadio(tb[2]) && IdmMenuRenderer.IsRadio(tb[5]), "Las opciones excluyentes deberían llevar viñeta");
            }
            finally { DisposeMainForm(f); }
        }

        static void TVA_Find()
        {
            MainForm f = NewMainForm();
            Action<string> savedHook = MainForm.FindNotFoundHook;
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                DownloadItem a = m.Add(S.Url("range", "informe-anual.pdf", 1024, NewId("fa")), Dl, null, false);
                DownloadItem b = m.Add(S.Url("range", "fotos.zip", 1024, NewId("fb")), Dl, null, false);
                DownloadItem c = m.Add(S.Url("range", "Informe-final.pdf", 1024, NewId("fc")), Dl, null, false);
                b.Description = "informe de fotos";
                IntPtr lh = Field<ListView>(f, "_list").Handle;
                f.GetType().GetMethod("RefreshList", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(f, null);
                string missing = null;
                MainForm.FindNotFoundHook = delegate(string s) { missing = s; };
                FindQuery q = new FindQuery(); q.Text = "informe";
                f.SetFind(q);
                DownloadItem x1 = f.FindNext(), x2 = f.FindNext(), x3 = f.FindNext();
                Check(x1 != null && x2 != null && x1 != x2 && x3 == x1 && x1 != b && x2 != b, "Buscar por nombre (sin mayúsculas, vuelve al principio)");
                q.MatchCase = true;
                Check(f.FindNext() == a && f.FindNext() == a, "Coincidir mayúsculas");
                q.MatchCase = false; q.InName = false; q.InDescription = true;
                Check(f.FindNext() == b, "Buscar en la descripción");
                q.Text = "inform"; q.WholeWord = true;
                Check(f.FindNext() == null && missing == "inform", "Palabras completas / aviso de no encontrado");
                q.InName = true; q.InDescription = false; q.WholeWord = false; q.InSite = true; q.Text = "127.0.0.1";
                Check(f.FindNext() != null, "Buscar por sitio");
                using (FindForm ff = new FindForm(null))
                {
                    IntPtr h = ff.Handle;
                    ff.Set("abc", true, false, true, true, false);
                    FindQuery r = ff.Query;
                    Check(r.Text == "abc" && r.InName && !r.InDescription && r.InSite && r.MatchCase && !r.WholeWord, "Ventana Buscar Archivo");
                }
            }
            finally { MainForm.FindNotFoundHook = savedHook; DisposeMainForm(f); }
        }
    }
}
