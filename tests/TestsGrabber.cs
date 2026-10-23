using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace IDMClone.Tests
{
    // Tests del Grabber (fase 5b): enlaces de las páginas, niveles, sitios, filtros, duplicados, tamaño,
    // descarga con subcarpetas, conversión de enlaces para navegar sin conexión, plantillas y proyectos.
    public static partial class TestMain
    {
        static void RunGrabber()
        {
            Run("grabber: enlaces de una página (base, srcset, CSS, JavaScript, ventanas emergentes)", TG_Extract);
            Run("grabber: plantillas y filtros de IDM", TG_TemplatesFilters);
            Run("grabber: todas las imágenes del sitio (desconexión, emergentes, otros sitios fuera)", TG_ImagesWholeSite);
            Run("grabber: niveles de enlace, directorios padre y otros sitios", TG_Levels);
            Run("grabber: duplicados, tamaño mínimo y archivo con nombre del servidor", TG_DupsSizeCd);
            Run("grabber: sitio completo con subcarpetas y enlaces convertidos para navegar sin conexión", TG_CompleteSite);
            Run("grabber: añadir los marcados a la lista principal y a la cola", TG_AddToMain);
            Run("grabber: guardar y cargar proyectos y plantillas propias", TG_SaveLoad);
            Run("redirección 308: descarga normal, vídeo HLS y páginas del Grabber", TG_Redirect308);
            Run("grabber: asistente de 4 pasos y ventana principal (explorar, descargar, añadir, guardar)", TG_Ui);
            Run("grabber: «Proyectos Grabber» en la ventana principal (abrir y eliminar) y filtros", TG_MainTree);
        }

        static void TG_Redirect308()
        {
            // Descarga normal detrás de una 308: llega entera (SHA-256) y con varias conexiones.
            DownloadManager m = NewMgr();
            long size = 3L * 1024 * 1024;
            string id = NewId("r308");
            DownloadItem it = m.Add(S.Base + "/r308/range/permanente.bin?size=" + size + "&id=" + id, Dl, null, true);
            WaitEnd(it, 30);
            Check(it.Status == DownloadStatus.Complete, "Estado: " + it.Status + " " + it.Error);
            Check(HashFile(it.FilePath, "SHA256").Equals(TestServer.Sha256Of(size), StringComparison.OrdinalIgnoreCase), "Contenido distinto");
            // Lista HLS detrás de una 308.
            for (int i = 0; i < 2; i++) S.Put("hls-308/seg" + i + ".ts", MakeTsSegment(i), "video/mp2t");
            S.Put("hls-308/v.m3u8", HlsMedia(2, "seg{0}.ts", null), "application/vnd.apple.mpegurl");
            DownloadItem v = AddMedia(m, S.Base + "/r308/s/hls-308/v.m3u8", "hls", "redirigido.mp4", null, null, true);
            WaitEnd(v, 30);
            Check(v.Status == DownloadStatus.Complete && Tracks(v.FilePath)[0].Samples.Count == 2 * FramesPerSeg, "HLS tras 308: " + v.Status + " " + v.Error);
            // Página del Grabber detrás de una 308 (como los autores de quotes.toscrape.com).
            S.Put("site308/index.html", "<html><body><a href=\"/r308/s/site308/autor/index.html\">Autor</a></body></html>", "text/html");
            S.Put("site308/autor/index.html", "<html><body><img src=\"foto.png\"></body></html>", "text/html");
            S.Put("site308/autor/foto.png", Img(2000, 3), "image/png");
            GrabberProject p = GrabberProject.CreateDefault();
            p.StartUrl = S.Base + "/s/site308/index.html";
            p.ApplyTemplate(1, Cfg());
            Explore(p, null);
            Check(p.Files.Exists(delegate(GrabberFile f) { return f.Name == "foto.png"; }), "El Grabber no siguió la 308: " + Show(Names(p)));
            // La página redirigida y su destino son la misma: se explora una vez y se guarda con su dirección final.
            S.Put("site308/index.html", "<html><body><a href=\"/r308/s/site308/autor/index.html\">Autor</a> <a href=\"/s/site308/autor/index.html\">Otra vez</a></body></html>", "text/html");
            string dir = Path.Combine(Dl, "g308-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            p = GrabberProject.CreateDefault();
            p.StartUrl = S.Base + "/s/site308/index.html";
            p.ApplyTemplate(5, Cfg()); p.SaveFolder = dir;
            GrabberEngine e = Explore(p, null);
            Check(p.Files.FindAll(delegate(GrabberFile f) { return f.IsPage; }).Count == 2, "Páginas: " + Show(Names(p)));
            ManualResetEvent done = new ManualResetEvent(false);
            e.DownloadFinished += delegate { done.Set(); };
            e.StartDownloading(null);
            Check(done.WaitOne(30000), "No terminó de descargar");
            string index = File.ReadAllText(Path.Combine(dir, "s", "site308", "index.html"));
            Check(index.Contains("href=\"autor/index.html\">Autor") && index.Contains("href=\"autor/index.html\">Otra vez"), "Las dos direcciones al mismo archivo local: " + index);
        }

        static void TG_Ui()
        {
            UseFreshHome("grabber-ui");
            GrabberFilters.Reload();
            List<string> said = new List<string>();
            GrabberForm.MessageHook = delegate(string t) { said.Add(t); };
            GrabberForm.AskHook = delegate(string t) { said.Add(t); return System.Windows.Forms.DialogResult.Yes; };
            MainForm mf = NewMainForm();
            try
            {
                DownloadManager m = Field<DownloadManager>(mf, "_mgr");
                AppConfig cfg = m.Config;
                GrabberForm g = mf.OpenGrabber(null, null);
                Check(g != null && g.Step == 1 && g.Text.Contains("Paso 1 --- Asignar página de inicio"), "Paso 1: " + (g != null ? g.Text : "null"));
                Check(!g.GoNext() && said.Count > 0 && said[said.Count - 1].Contains("página/dirección de inicio"), "Sin página de inicio debería avisar");
                g.SetStart(PutSite(), 1); // "Todas las Imágenes del sitio web"
                Check(g.GoNext() && g.Step == 2 && g.Text.Contains("Guardar archivos"), "Paso 2");
                string dir = Path.Combine(Dl, "grb-ui");
                g.SetSaveFolder(dir, false);
                Check(g.GoNext() && g.Step == 3 && g.Text.Contains("explorador de filtros"), "Paso 3");
                g.SetLevels(true, 0, 0);
                Check(g.GoNext() && g.Step == 4 && g.Text.Contains("filtros de archivos"), "Paso 4");
                Check(g.GoNext() && g.Step == 5 && g.Engine != null, "Ventana principal");
                Check(PumpUntil(delegate { return !g.Engine.Exploring; }, 30), "No terminó de explorar");
                PumpUntil(delegate { return g.ListCount >= 6; }, 3);
                Check(g.ListCount >= 6 && g.Project.IncludeFilter == GrabberFilters.Images && g.Project.SaveMode == 2 && g.Project.SaveFolder == dir, "Lista: " + g.ListCount);
                Check(g.StatusText.Contains("Archivos encontrados: " + g.Project.Files.Count), "Estado: " + g.StatusText);
                g.SelectView("page:" + PutSite());
                Check(g.ListCount > 0 && g.ListCount < g.Project.Files.Count, "Vista por enlace: " + g.ListCount);
                g.SelectView("all");
                // Desmarcar todos, marcar uno y descargarlo.
                g.ClickTool("uncheckall");
                g.Project.Files.Find(delegate(GrabberFile f) { return f.Name == "logo.png"; }).Checked = true;
                g.DownloadChecked();
                Check(PumpUntil(delegate { return g.Project.Files.Find(delegate(GrabberFile f) { return f.Name == "logo.png"; }).Status == "Completo"; }, 20), "No se descargó el marcado");
                Check(File.Exists(Path.Combine(dir, "logo.png")) && !File.Exists(Path.Combine(dir, "a.jpg")), "Solo el marcado");
                // Añadir los marcados a la lista principal.
                g.Project.Files.Find(delegate(GrabberFile f) { return f.Name == "a.jpg"; }).Checked = true;
                int before = m.Snapshot().Count;
                Check(g.AddToMain() == 1 && m.Snapshot().Count == before + 1, "Añadir a IDMClone");
                Check(said[said.Count - 1].Contains("1 archivos fueron añadidos"), "Aviso: " + said[said.Count - 1]);
                // Atrás al paso 4 y otra vez adelante: sigue con lo explorado.
                int files = g.Project.Files.Count;
                g.ShowStep(4);
                Check(g.GoNext() && g.Project.Files.Count == files, "Volver a la ventana principal conserva lo explorado");
                // Al cerrar pregunta si guardar (con nombre puesto no pide otro).
                g.SetName("Fotos del sitio");
                g.ShowStep(4); g.GoNext();
                PumpUntil(delegate { return !g.Engine.Exploring; }, 10);
                said.Clear();
                g.Close();
                PumpUntil(delegate { return g.IsDisposed; }, 5);
                Check(said.Exists(delegate(string x) { return x.Contains("¿Guardar el proyecto de Grabber?"); }), "No preguntó si guardar");
                Check(File.Exists(GrabberProject.PathFor("Fotos del sitio")), "No se guardó el proyecto");
                Check(GrabberProject.Load("Fotos del sitio").Files.Count == files, "Proyecto guardado sin archivos");
            }
            finally { GrabberForm.MessageHook = null; GrabberForm.AskHook = null; DisposeMainForm(mf); }
        }

        static void TG_MainTree()
        {
            Func<string, bool> savedConfirm = MainForm.ConfirmHook;
            GrabberForm.AskHook = delegate(string t) { return System.Windows.Forms.DialogResult.No; };
            MainForm mf = NewMainForm(); // usa su propia carpeta de datos: el proyecto se guarda después
            try
            {
                GrabberFilters.Reload();
                GrabberProject p = NewProject(1);
                p.Name = "Proyecto árbol";
                Explore(p, null);
                p.Save();
                System.Windows.Forms.TreeView tree = Field<System.Windows.Forms.TreeView>(mf, "_tree");
                mf.GetType().GetMethod("RefreshTree", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(mf, null);
                System.Windows.Forms.TreeNode root = null;
                foreach (System.Windows.Forms.TreeNode n in tree.Nodes) if ((string)n.Tag == "__grabber__") root = n;
                Check(root != null && root.Text == "Proyectos Grabber" && root.Nodes.Count == 1 && root.Nodes[0].Text == "Proyecto árbol", "Nodo Proyectos Grabber");
                GrabberForm g = mf.OpenGrabber("Proyecto árbol", null);
                Check(g.Step == 5 && g.Project.Files.Count == p.Files.Count, "Abrir proyecto: paso " + g.Step);
                PumpUntil(delegate { return g.ListCount == p.Files.Count; }, 3);
                Check(g.ListCount == p.Files.Count, "Lista del proyecto abierto: " + g.ListCount);
                Check(mf.OpenGrabber(null, null) == g, "Solo un Grabber a la vez");
                g.Close();
                PumpUntil(delegate { return g.IsDisposed; }, 5);
                MainForm.ConfirmHook = delegate(string q) { return q.Contains("Proyecto árbol"); };
                Check(mf.DeleteGrabberProject("Proyecto árbol") && !File.Exists(GrabberProject.PathFor("Proyecto árbol")), "Eliminar proyecto");
                root = null;
                foreach (System.Windows.Forms.TreeNode n in tree.Nodes) if ((string)n.Tag == "__grabber__") root = n;
                Check(root.Nodes.Count == 0, "El árbol no se actualizó");
                // Filtros: validaciones del diálogo "Filtro".
                List<string> said = new List<string>();
                GrabberForm.MessageHook = delegate(string t) { said.Add(t); };
                using (GrabberFilterEditForm f = new GrabberFilterEditForm(null))
                {
                    IntPtr h = f.Handle;
                    f.Set("", "*.x"); Check(!f.Accept() && said[0].Contains("nombre del filtro"), "Sin nombre");
                    f.Set(GrabberFilters.Images, "*.x"); Check(!f.Accept() && said[1].Contains("ya existe"), "Repetido");
                    f.Set("Mis fotos", "foto*.jpg, *.png"); Check(f.Accept() && f.Result.Mask == "foto*.jpg,*.png", "Filtro nuevo: " + (f.Result != null ? f.Result.Mask : ""));
                }
            }
            finally { MainForm.ConfirmHook = savedConfirm; GrabberForm.AskHook = null; GrabberForm.MessageHook = null; DisposeMainForm(mf); }
        }

        static byte[] Img(int size, byte seed)
        {
            byte[] b = new byte[size];
            for (int i = 0; i < size; i++) b[i] = (byte)(seed + i * 7);
            return b;
        }

        static string _siteRoot;

        // Sitio de prueba en /s/site/ (y una página en "otro sitio": localhost en vez de 127.0.0.1).
        static string PutSite()
        {
            if (_siteRoot != null) return _siteRoot;
            string other = "http://localhost:" + S.Port + "/s/ext/";
            string cd = S.Url("range,cd", "descargar.php", 4096, "grb-cd");
            Func<string, string> html = delegate(string body) { return "<!doctype html><html><head><meta charset=\"utf-8\"><title>t</title></head><body>" + body + "</body></html>"; };
            S.Put("site/index.html", html(
                "<a href=\"pag2.html\">Página&nbsp;2</a> <a href='sub/pag3.html'>Tres</a>" +
                "<img src=\"img/logo.png\" alt=\"Logo\"> <img src=\"" + other + "otro.png\">" +
                "<a href=\"files/doc.pdf\">Manual <b>PDF</b></a> <a href=files/pack.zip>Paquete</a>" +
                "<a href=\"logout.html\">Salir</a> <a href=\"../fuera/padre.html\">Arriba</a>" +
                "<a href=\"" + cd + "\">Informe</a> <a href=\"" + other + "e1.html\">Fuera</a>" +
                "<a href=\"mailto:a@b.c\">correo</a> <a href=\"javascript:void(0)\">js</a> <a href=\"#arriba\">ancla</a>" +
                "<div style=\"background:url('img/fondo.jpg')\"></div>" +
                "<script>var v = \"media/clip.mp4\"; function p(){ window.open('popup.html'); }</script>" +
                "<img src=\"img/tiny.gif\">"), "text/html; charset=utf-8");
            S.Put("site/pag2.html", html("<img src=\"img/a.jpg\"><a href=\"pag4.html\">Cuatro</a>"), "text/html");
            S.Put("site/pag4.html", html("<img src=\"img/deep.gif\"><a href=\"pag5.html\">Cinco</a>"), "text/html");
            S.Put("site/pag5.html", html("<img src=\"img/deeper.gif\">"), "text/html");
            S.Put("site/sub/pag3.html", html("<a href=\"../index.html\">Inicio</a><img src=\"../img/logo.png\"><img src=\"a.jpg\">"), "text/html");
            S.Put("site/popup.html", html("<img src=\"img/popup.gif\">"), "text/html");
            S.Put("site/logout.html", html("<img src=\"img/logout.gif\">"), "text/html");
            S.Put("fuera/padre.html", html("<img src=\"padre.png\">"), "text/html");
            S.Put("ext/e1.html", html("<img src=\"ext.png\">"), "text/html");
            foreach (string n in new string[] { "site/img/logo.png", "site/img/fondo.jpg", "site/img/deep.gif", "site/img/deeper.gif", "site/img/popup.gif", "site/img/logout.gif",
                                                "fuera/padre.png", "ext/ext.png", "ext/otro.png" })
                S.Put(n, Img(3000 + n.Length, (byte)n.Length), "image/png");
            S.Put("site/img/a.jpg", Img(5000, 9), "image/jpeg");
            S.Put("site/sub/a.jpg", Img(5000, 9), "image/jpeg"); // mismo nombre y tamaño: duplicado
            S.Put("site/img/tiny.gif", Img(100, 1), "image/gif");
            S.Put("site/files/doc.pdf", Img(20000, 3), "application/pdf");
            S.Put("site/files/pack.zip", Img(30000, 4), "application/zip");
            S.Put("site/media/clip.mp4", Img(40000, 5), "video/mp4");
            _siteRoot = S.Base + "/s/site/index.html";
            return _siteRoot;
        }

        static GrabberProject NewProject(int template)
        {
            GrabberProject p = GrabberProject.CreateDefault();
            p.StartUrl = PutSite();
            p.LogoutPages = "*logout*";
            p.ApplyTemplate(template, Cfg());
            return p;
        }

        static GrabberEngine Explore(GrabberProject p, AppConfig cfg)
        {
            GrabberEngine e = new GrabberEngine(p, cfg ?? Cfg());
            ManualResetEvent done = new ManualResetEvent(false);
            e.ExploreFinished += delegate { done.Set(); };
            e.StartExploring();
            Check(done.WaitOne(30000), "La exploración no terminó");
            return e;
        }

        static List<string> Names(GrabberProject p)
        {
            List<string> r = new List<string>();
            foreach (GrabberFile f in p.Files) r.Add(f.Name);
            r.Sort(StringComparer.Ordinal);
            return r;
        }

        static string Show(List<string> l) { return string.Join(", ", l.ToArray()); }

        static void TG_Extract()
        {
            string html = "<html><head><base href=\"http://ej.com/b/\"><link rel=stylesheet href=\"e.css\"><meta http-equiv=\"refresh\" content=\"5; URL='nueva.html'\"></head>" +
                "<body><!-- <a href=\"comentado.html\">no</a> --><a href=\"p.html?x=1&amp;y=2\">Ir  a <i>P</i></a>" +
                "<img srcset=\"s1.jpg 1x, s2.jpg 2x\" src=\"s.jpg\"><a href=\"#top\">top</a><a href=\"tel:123\">t</a>" +
                "<td background=\"fondo.gif\"><video poster=\"cartel.jpg\"><source src=\"v.webm\"></video>" +
                "<style>.x{background:url(\"css-img.png\")} @import 'otro.css';</style>" +
                "<script>var a='/js/f.zip'; window.open(\"emerge.html\");</script><span onclick=\"location='doc.pdf'\">x</span></body></html>";
            List<GrabberHtml.Link> l = GrabberHtml.Extract(html, "http://ej.com/a/index.html", false);
            Dictionary<string, GrabberHtml.Link> d = new Dictionary<string, GrabberHtml.Link>();
            foreach (GrabberHtml.Link x in l) d[x.Url] = x;
            foreach (string u in new string[] { "http://ej.com/b/e.css", "http://ej.com/b/nueva.html", "http://ej.com/b/p.html?x=1&y=2", "http://ej.com/b/s1.jpg", "http://ej.com/b/s2.jpg",
                                                "http://ej.com/b/s.jpg", "http://ej.com/b/fondo.gif", "http://ej.com/b/cartel.jpg", "http://ej.com/b/v.webm", "http://ej.com/b/css-img.png",
                                                "http://ej.com/b/otro.css", "http://ej.com/js/f.zip", "http://ej.com/b/emerge.html", "http://ej.com/b/doc.pdf" })
                Check(d.ContainsKey(u), "Falta " + u + " en: " + string.Join(" ", new List<string>(d.Keys).ToArray()));
            Check(!d.ContainsKey("http://ej.com/b/comentado.html") && !d.ContainsKey("http://ej.com/b/"), "Enlaces que no lo son");
            Check(d["http://ej.com/b/p.html?x=1&y=2"].Page && d["http://ej.com/b/p.html?x=1&y=2"].Text == "Ir a P", "Texto del enlace: " + d["http://ej.com/b/p.html?x=1&y=2"].Text);
            Check(d["http://ej.com/b/emerge.html"].Popup && !d["http://ej.com/b/s.jpg"].Page, "Emergente / recurso");
            Check(GrabberHtml.FileNameFor("http://ej.com/docs/", true) == "index.html" && GrabberHtml.FileNameFor("http://ej.com/ver.php?id=3", true) == "ver_id_3.html"
                  && GrabberHtml.FileNameFor("http://ej.com/a/mi%20foto.jpg", false) == "mi foto.jpg", "Nombres de archivo");
            Check(GrabberEngine.MainDomainOf("a.b.ejemplo.com") == "ejemplo.com" && GrabberEngine.MainDomainOf("www.bbc.co.uk") == "bbc.co.uk", "Dominio principal");
            Check(GrabberEngine.RelativePath(@"C:\a\b", @"C:\a\c\d.html") == @"..\c\d.html" || GrabberEngine.RelativePath(@"C:\a\b", @"C:\a\c\d.html") == "../c/d.html", "Ruta relativa: " + GrabberEngine.RelativePath(@"C:\a\b", @"C:\a\c\d.html"));
        }

        static void TG_TemplatesFilters()
        {
            Check(GrabberProject.Templates.Length == 8 && GrabberProject.Templates[5] == "El sitio web completo", "Plantillas de IDM");
            GrabberProject p = GrabberProject.CreateDefault();
            p.StartUrl = "https://www.ejemplo.com/inicio.html";
            p.ApplyTemplate(2, Cfg());
            Check(p.IncludeFilter == GrabberFilters.Audio && !p.WholeSite && p.LevelsSite == 1, "Plantilla de audio");
            p.ApplyTemplate(5, Cfg());
            Check(p.IncludeFilter == GrabberFilters.All && p.WholeSite && p.ConvertLinks && p.RelativeSubfolders && p.SaveMode == 2 && p.SaveFolder.EndsWith("www.ejemplo.com"), "Sitio completo: " + p.SaveFolder);
            p.ApplyTemplate(6, Cfg());
            Check(!p.WholeSite && p.LevelsSite == 0 && p.PagesAreDownloaded, "Página de inicio completa");
            p.ApplyTemplate(1, Cfg());
            Check(!p.PagesAreDownloaded, "Las imágenes no bajan páginas");
            Check(GrabberFilters.MatchesMask("Foto.JPG", GrabberFilters.Find(GrabberFilters.Images).Mask, "", ""), "Mayúsculas");
            Check(GrabberFilters.MatchesMask("image7.jpg", "image*.jpg", "", "") && !GrabberFilters.MatchesMask("foto.jpg", "image*.jpg", "", ""), "Comodín en el nombre");
            Check(GrabberFilters.MatchesMask("x.r05", GrabberFilters.Find(GrabberFilters.Zip).Mask, "", ""), "r0*");
            Check(GrabberFilters.MatchesMask("a.zip", "<start page>*", "http://ej.com/d/a.zip", "http://ej.com/d/"), "<start page>");
            Check(GrabberFilters.MatchesMask("sin-extension", "*.*", "", ""), "*.* vale para todo");
        }

        static void TG_ImagesWholeSite()
        {
            GrabberProject p = NewProject(1);
            p.HideDuplicates = false;
            Explore(p, null);
            List<string> n = Names(p);
            foreach (string want in new string[] { "logo.png", "a.jpg", "deep.gif", "deeper.gif", "fondo.jpg", "tiny.gif", "padre.png" })
                Check(n.Contains(want), "Falta " + want + ": " + Show(n));
            foreach (string no in new string[] { "logout.gif", "popup.gif", "otro.png", "ext.png", "doc.pdf", "index.html" })
                Check(!n.Contains(no), "No debería estar " + no + ": " + Show(n));
            Check(n.FindAll(delegate(string x) { return x == "a.jpg"; }).Count == 2, "Las dos a.jpg (sin ocultar duplicados)");
            GrabberFile logo = p.Files.Find(delegate(GrabberFile f) { return f.Name == "logo.png"; });
            Check(logo.Size == 3000 + "site/img/logo.png".Length && logo.Type == "Imagen PNG" && logo.Checked && logo.LinkText == "Logo", "Datos del archivo: " + logo.Size + " " + logo.Type + " " + logo.LinkText);
            Check(p.Pages.Exists(delegate(GrabberPage g) { return g.Url.EndsWith("/pag5.html") && g.Status == "Procesada"; }), "No exploró todo el sitio");
            Check(!p.Pages.Exists(delegate(GrabberPage g) { return g.Url.Contains("logout") || g.Url.Contains("popup"); }), "Abrió la desconexión o la emergente");
        }

        static void TG_Levels()
        {
            GrabberProject p = NewProject(1);
            p.WholeSite = false; p.LevelsSite = 1; p.NoParent = true;
            Explore(p, null);
            List<string> n = Names(p);
            Check(n.Contains("a.jpg") && n.Contains("logo.png"), "Nivel 1: " + Show(n));
            Check(!n.Contains("deep.gif") && !n.Contains("deeper.gif"), "Exploró por debajo del nivel 1: " + Show(n));
            Check(!n.Contains("padre.png"), "Exploró un directorio padre");

            p = NewProject(1);
            p.ThisSiteOnly = false; p.LevelsOther = 1;
            Explore(p, null);
            n = Names(p);
            Check(n.Contains("ext.png") && n.Contains("otro.png"), "Otros sitios (nivel 1): " + Show(n));
            p = NewProject(1);
            p.ThisSiteOnly = false; p.LevelsOther = 0;
            Explore(p, null);
            n = Names(p);
            Check(n.Contains("otro.png") && !n.Contains("ext.png"), "Archivos de otros sitios sí; sus páginas no: " + Show(n));
        }

        static void TG_DupsSizeCd()
        {
            GrabberProject p = NewProject(1);
            p.HideDuplicates = true; p.MinKB = 1;
            Explore(p, null);
            List<string> n = Names(p);
            Check(n.FindAll(delegate(string x) { return x == "a.jpg"; }).Count == 1, "Duplicado no oculto: " + Show(n));
            Check(!n.Contains("tiny.gif"), "Pasó el tamaño mínimo: " + Show(n));

            p = NewProject(0);
            p.IncludeFilter = GrabberFilters.All; p.ExcludeFilter = GrabberFilters.WebAndImages; p.ThisSiteOnly = true;
            p.WholeSite = false; p.LevelsSite = 1;
            Explore(p, null);
            n = Names(p);
            Check(n.Contains("doc.pdf") && n.Contains("pack.zip") && n.Contains("clip.mp4"), "Archivos (también del JavaScript): " + Show(n));
            Check(n.Contains("informe final €.pdf"), "Nombre del servidor (Content-Disposition): " + Show(n));
            Check(!n.Contains("logo.png") && !n.Contains("index.html"), "El filtro de exclusión no funcionó: " + Show(n));
            GrabberFile pdf = p.Files.Find(delegate(GrabberFile f) { return f.Name == "doc.pdf"; });
            Check(pdf.LinkText == "Manual PDF" && pdf.Size == 20000 && pdf.Referer == p.StartUrl, "Texto/tamaño/página: " + pdf.LinkText + " " + pdf.Size);
        }

        static void TG_CompleteSite()
        {
            string dir = Path.Combine(Dl, "grabber-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            GrabberProject p = NewProject(5);
            p.SaveFolder = dir;
            GrabberEngine e = Explore(p, null);
            List<string> n = Names(p);
            Check(n.Contains("index.html") && n.Contains("pag2.html") && n.Contains("pag3.html") && n.Contains("logo.png"), "Sitio completo: " + Show(n));
            ManualResetEvent done = new ManualResetEvent(false);
            e.DownloadFinished += delegate { done.Set(); };
            e.StartDownloading(null);
            Check(done.WaitOne(60000), "Las descargas no terminaron");
            foreach (GrabberFile f in p.Files) Check(f.Status == "Completo", f.Name + ": " + f.Status);
            string site = Path.Combine(dir, "s", "site");
            Check(File.Exists(Path.Combine(site, "index.html")) && File.Exists(Path.Combine(site, "img", "logo.png")) && File.Exists(Path.Combine(site, "sub", "pag3.html")), "Subcarpetas originales");
            Check(File.Exists(Path.Combine(dir, "s", "fuera", "padre.html")), "Directorio padre del sitio");
            string index = File.ReadAllText(Path.Combine(site, "index.html"));
            Check(index.Contains("href=\"pag2.html\"") && index.Contains("href='sub/pag3.html'") && index.Contains("src=\"img/logo.png\""), "Enlaces locales: " + index);
            Check(index.Contains("href=\"../fuera/padre.html\""), "Enlace al directorio padre");
            Check(index.Contains("href=\"http://localhost:" + S.Port + "/s/ext/e1.html\""), "Enlace externo absoluto");
            Check(index.Contains("href=\"http://127.0.0.1:" + S.Port + "/s/site/logout.html\""), "La página no bajada queda con enlace a internet");
            Check(index.Contains("url('img/fondo.jpg')"), "url() del estilo");
            Check(index.Contains("href=\"../../f/range%2Ccd/informe%20final%20%E2%82%AC.pdf\""), "Nombre con espacios y acentos codificado");
            string pag3 = File.ReadAllText(Path.Combine(site, "sub", "pag3.html"));
            Check(pag3.Contains("href=\"../index.html\"") && pag3.Contains("src=\"../img/logo.png\"") && pag3.Contains("src=\"a.jpg\""), "Enlaces de la subcarpeta: " + pag3);
            byte[] logo = File.ReadAllBytes(Path.Combine(site, "img", "logo.png"));
            Check(logo.Length == 3000 + "site/img/logo.png".Length, "Imagen descargada");
            int pd, pt, fl, mt, dn; e.Counts(out pd, out pt, out fl, out mt, out dn);
            Check(dn == fl && pd == pt && pt >= 7, "Estadísticas: " + pd + "/" + pt + " " + dn + "/" + fl);
        }

        static void TG_AddToMain()
        {
            GrabberProject p = NewProject(0);
            p.IncludeFilter = GrabberFilters.Pdf; p.WholeSite = false; p.LevelsSite = 0;
            p.SaveMode = 2; p.SaveFolder = Path.Combine(Dl, "grb-main");
            GrabberEngine e = Explore(p, null);
            // Nivel 0: solo los PDF enlazados desde la página de inicio (descargar.php no se explora).
            Check(p.Files.Count == 1 && p.Files[0].Name == "doc.pdf", "PDF: " + Show(Names(p)));
            DownloadManager m = NewMgr();
            p.Files[0].Checked = false;
            Check(e.AddCheckedTo(m) == 0, "Añadió uno sin marcar");
            p.Files[0].Checked = true;
            Check(e.AddCheckedTo(m) == 1, "Añadidos");
            DownloadItem it = m.Snapshot()[0];
            Check(it.Queue == DownloadItem.MainQueue && it.Description == "Manual PDF" && it.Referrer == p.StartUrl, "Cola/descripción/referer: " + it.Queue + " " + it.Description);
            Check(Path.GetDirectoryName(it.FilePath) == p.SaveFolder && it.FileName == "doc.pdf" && it.Status == DownloadStatus.Queued, "Carpeta/nombre: " + it.FilePath);
            Check(e.AddCheckedTo(m) == 0, "Se añadió dos veces");
        }

        static void TG_SaveLoad()
        {
            UseFreshHome("grabber-save");
            GrabberFilters.Reload();
            GrabberProject p = NewProject(1);
            p.Name = "Mi sitio: fotos"; p.UseAuth = true; p.User = "ana"; p.Password = "secreta";
            Explore(p, null);
            int files = p.Files.Count;
            p.Files[0].Status = "Descargando";
            p.Save();
            Check(GrabberProject.SavedNames().Contains("Mi sitio fotos") || GrabberProject.SavedNames().Count == 1, "Lista de proyectos: " + string.Join("|", GrabberProject.SavedNames().ToArray()));
            string raw = File.ReadAllText(GrabberProject.PathFor(p.Name));
            Check(!raw.Contains("secreta"), "La contraseña se guardó sin cifrar");
            GrabberProject q = GrabberProject.Load(p.Name);
            Check(q.Files.Count == files && q.Pages.Count == p.Pages.Count && q.Password == "secreta" && q.IncludeFilter == GrabberFilters.Images, "Proyecto cargado");
            Check(q.Files[0].Status == "Detenido", "Lo que se descargaba queda detenido");
            File.WriteAllText(GrabberProject.PathFor("roto"), "{no es json");
            try { GrabberProject.Load("roto"); Check(false, "Cargó un proyecto dañado"); }
            catch (InvalidDataException ex) { Check(ex.Message.Contains("dañado"), ex.Message); }

            p.WholeSite = false; p.LevelsSite = 3; p.MinKB = 7; p.ExploreNot = "*/privado/*";
            p.SaveAsTemplate("Fotos sin privado");
            Check(GrabberProject.UserTemplates().Contains("Fotos sin privado"), "Plantilla propia");
            GrabberProject r = GrabberProject.CreateDefault();
            r.ApplyUserTemplate("Fotos sin privado");
            Check(!r.WholeSite && r.LevelsSite == 3 && r.MinKB == 7 && r.ExploreNot == "*/privado/*" && r.Files.Count == 0, "Aplicar plantilla propia");
            try { p.SaveAsTemplate("El sitio web completo"); Check(false, "Aceptó un nombre reservado"); }
            catch (ArgumentException ex) { Check(ex.Message.Contains("reservado"), ex.Message); }

            GrabberFilters.List.Add(new GrabberFilter("Mis fotos", "foto*.jpg"));
            GrabberFilters.Save(); GrabberFilters.Reload();
            Check(GrabberFilters.Find("Mis fotos") != null && GrabberFilters.Find(GrabberFilters.Images) != null, "Filtros guardados");
            GrabberProject.Delete(p.Name);
            Check(!File.Exists(GrabberProject.PathFor(p.Name)), "Eliminar proyecto");
            GrabberFilters.Reload();
        }
    }
}
