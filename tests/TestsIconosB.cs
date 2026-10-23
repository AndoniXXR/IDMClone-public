using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace IDMClone.Tests
{
    // Iconos de la barra (estilos Neon y 3D de IDM), iconos del árbol y diálogo Personalizar.
    public static partial class TestMain
    {
        static void RunIconsB()
        {
            Run("barra: iconos neón de trazo fino y 3D con relleno para todos los botones (grandes, pequeños, 150 %)", TB_IconStyles);
            Run("barra: iconos deshabilitados en gris claro y estilos guardados desconocidos", TB_IconDisabledAndStyles);
            Run("barra: aspecto de IDM (fondo liso, icono deshabilitado gris) con ToolbarRenderer", TB_Renderer);
            Run("iconos: árbol de categorías al estilo XP (carpetas amarillas, nota azul...)", TB_TreeIcons);
            Run("barra: Personalizar... como IDM (Cerrar, Restablecer, Subir, Bajar y hueco del final)", TB_CustomizeLikeIdm);
            Run("escala 100/125/150 %: Personalizar la barra de herramientas sin textos cortados (dpi, iconos de 32 px escalados)", TB_CustomizeDpi);
        }

        static int Saturated(Bitmap b)
        {
            int n = 0;
            for (int y = 0; y < b.Height; y++)
                for (int x = 0; x < b.Width; x++)
                {
                    Color c = b.GetPixel(x, y);
                    if (c.A > 60 && Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) > 40) n++;
                }
            return n;
        }

        static void TB_IconStyles()
        {
            foreach (ToolbarCatalog.Button b in ToolbarCatalog.All)
                foreach (int sz in new int[] { 32, 20, 26, 48 })
                {
                    Bitmap n = Glyphs.Get(b.Id, sz, "neon"), d = Glyphs.Get(b.Id, sz, "3d");
                    Check(n.Width == sz && n.Height == sz && d.Width == sz, b.Id + ": tamaño " + n.Width + "/" + d.Width + " en vez de " + sz);
                    int on = Opaque(n), od = Opaque(d);
                    Check(on > sz * sz / 25, b.Id + " neón casi vacío a " + sz + " px: " + on);
                    Check(on < sz * sz * (sz < 32 ? 62 : 50) / 100, b.Id + " neón con relleno a " + sz + " px: " + on);
                    Check(sz < 32 || od > on * 3 / 2, b.Id + " 3D sin relleno a " + sz + " px: " + od + " frente a " + on);
                    Check(Saturated(n) > on / 3, b.Id + " neón sin color a " + sz + " px");
                }
            Check(ToolbarCatalog.IconSize(true) == Dpi.Px(32) && ToolbarCatalog.IconSize(false) == Dpi.Px(ToolbarCatalog.SmallIcon) && ToolbarCatalog.SmallIcon == 26, "Tamaños: " + ToolbarCatalog.IconSize(true) + "/" + ToolbarCatalog.IconSize(false));
            // Por defecto, como IDM: sin Grabber (se añade con Personalizar...) y sin separadores.
            List<string> def = ToolbarCatalog.Parse(null);
            Check(string.Join(",", def.ToArray()) == "add,resume,stop,stopall,delete,deletecompleted,options,scheduler,startqueue,stopqueue", "Por defecto: " + string.Join(",", def.ToArray()));
            // Las listas guardadas con la barra antigua siguen igual.
            string old = "add,resume,stop,stopall,|,delete,deletecompleted,|,options,scheduler,startqueue,stopqueue,|,grabber";
            Check(string.Join(",", ToolbarCatalog.Parse(old).ToArray()) == old, "Lista antigua: " + string.Join(",", ToolbarCatalog.Parse(old).ToArray()));
        }

        static void TB_IconDisabledAndStyles()
        {
            foreach (string st in new string[] { "neon", "3d" })
                foreach (ToolbarCatalog.Button b in ToolbarCatalog.All)
                {
                    Bitmap dis = Glyphs.GetDisabled(b.Id, 32, st);
                    Check(Opaque(dis) > 20, b.Id + " deshabilitado vacío");
                    for (int y = 0; y < dis.Height; y++)
                        for (int x = 0; x < dis.Width; x++)
                        {
                            Color c = dis.GetPixel(x, y);
                            if (c.A < 60) continue;
                            Check(Math.Abs(c.R - c.G) < 4 && Math.Abs(c.G - c.B) < 4 && c.R >= 140, b.Id + " (" + st + ") deshabilitado no es gris claro: " + c);
                        }
                    Check(object.ReferenceEquals(dis, Glyphs.GetDisabled(b.Id, 32, st)), "El gris no se reutiliza");
                }
            Check(Glyphs.NormalizeStyle(null) == "neon" && Glyphs.NormalizeStyle("") == "neon", "Vacío: neón (el de IDM)");
            Check(Glyphs.NormalizeStyle("3d") == "3d" && Glyphs.NormalizeStyle("3D Style") == "3d" && Glyphs.NormalizeStyle("classic") == "3d", "3D");
            Check(Glyphs.NormalizeStyle("NEON") == "neon" && Glyphs.NormalizeStyle("glossy") == "neon", "Otros: neón");
            Check(object.ReferenceEquals(Glyphs.Get("add", 32, "xyz"), Glyphs.Get("add", 32, "neon")), "Estilo desconocido = neón");
            Check(object.ReferenceEquals(Glyphs.Get("add", 32), Glyphs.Get("add", 32, "3d")), "Sin estilo = 3D (clásico)");
            // Lo que no tiene versión neón (árbol, Grabber) sale igual en los dos estilos.
            Check(object.ReferenceEquals(Glyphs.Get("explore", 32, "neon"), Glyphs.Get("explore", 32)), "explore");
            Check(Opaque(Glyphs.Get("no-existe", 32)) > 0, "Un nombre desconocido no deja un hueco");
        }

        static void TB_Renderer()
        {
            using (Form f = new Form())
            {
                f.ClientSize = new Size(240, 80);
                ToolStrip ts = new ToolStrip();
                ts.GripStyle = ToolStripGripStyle.Hidden;
                ts.Renderer = new ToolbarRenderer();
                ts.ImageScalingSize = new Size(32, 32);
                ToolStripButton on = new ToolStripButton("", Glyphs.Get("add", 32, "neon"));
                ToolStripButton off = new ToolStripButton("", Glyphs.Get("options", 32, "neon"));
                foreach (ToolStripButton b in new ToolStripButton[] { on, off }) { b.DisplayStyle = ToolStripItemDisplayStyle.Image; b.ImageScaling = ToolStripItemImageScaling.None; ts.Items.Add(b); }
                off.Enabled = false;
                f.Controls.Add(ts);
                IntPtr h = f.Handle;
                f.PerformLayout();
                using (Bitmap bmp = new Bitmap(ts.Width, ts.Height))
                {
                    ts.DrawToBitmap(bmp, new Rectangle(0, 0, ts.Width, ts.Height));
                    Color back = bmp.GetPixel(ts.Width - 5, ts.Height / 2);
                    Check(back.R == 0xF0 && back.G == 0xF0 && back.B == 0xF0, "Fondo de la barra: " + back);
                    Color bottom = bmp.GetPixel(ts.Width - 5, ts.Height - 1);
                    Check(bottom.R == 0xF0 && bottom.G == 0xF0 && bottom.B == 0xF0, "La barra no debía tener borde: " + bottom);
                    Rectangle ro = on.Bounds, rf = off.Bounds;
                    using (Bitmap a = bmp.Clone(ro, bmp.PixelFormat)) Check(Saturated(a) > 30, "El icono habilitado debía verse en color");
                    using (Bitmap a = bmp.Clone(rf, bmp.PixelFormat)) Check(Saturated(a) == 0, "El icono deshabilitado debía ser gris");
                }
            }
        }

        static void TB_TreeIcons()
        {
            string[] ids = { "folder", "folder-open", "folder-unfinished", "folder-finished", "queues", "queue", "queue-sync",
                             "cat-general", "cat-compressed", "cat-documents", "cat-music", "cat-programs", "cat-video", "grabber", "project" };
            foreach (string id in ids)
                foreach (int sz in new int[] { 16, 24 })
                {
                    Bitmap b = Glyphs.Get(id, sz);
                    Check(b.Width == sz && Opaque(b) > sz * sz / 5, id + " a " + sz + " px vacío");
                }
            Func<Bitmap, Func<Color, bool>, int> count = delegate(Bitmap b, Func<Color, bool> f)
            {
                int n = 0;
                for (int y = 0; y < b.Height; y++) for (int x = 0; x < b.Width; x++) { Color c = b.GetPixel(x, y); if (c.A > 200 && f(c)) n++; }
                return n;
            };
            Check(count(Glyphs.Get("folder", 16), delegate(Color c) { return c.R > 200 && c.G > 150 && c.B < 150; }) > 60, "La carpeta debía ser amarilla");
            Check(count(Glyphs.Get("cat-music", 16), delegate(Color c) { return c.B > 150 && c.R < 100; }) > 15, "La nota debía ser azul");
            Check(count(Glyphs.Get("queue-sync", 16), delegate(Color c) { return c.G > 150 && c.R < 180 && c.B < 140; }) > 40, "La cola de sincronización debía ser verde");
            Check(Glyphs.CategoryGlyph("Música", "mp3 wav") == "cat-music", "Música");
            Check(Glyphs.CategoryGlyph("Comprimido", "zip rar r0* r1* arj gz") == "cat-compressed", "Comprimido");
            Check(Glyphs.CategoryGlyph("Compressed", "") == "cat-compressed", "Compressed por nombre");
            Check(Glyphs.CategoryGlyph("Video", "avi mpg") == "cat-video" && Glyphs.CategoryGlyph("Vídeo", null) == "cat-video", "Video");
            Check(Glyphs.CategoryGlyph("Programas", "exe msi") == "cat-programs", "Programas");
            Check(Glyphs.CategoryGlyph("Documentos", "doc pdf xls") == "cat-documents", "Documentos");
            Check(Glyphs.CategoryGlyph("Mis cosas", "mp4") == "cat-video", "Por extensión");
            Check(Glyphs.CategoryGlyph("General", "") == null && Glyphs.CategoryGlyph("Otra", "xyz") == null, "Sin icono propio");
        }

        static void TB_CustomizeDpi()
        {
            try
            {
                foreach (float k in Scales)
                {
                    CheckForm(delegate { return new ToolbarCustomizeForm(ToolbarCatalog.Parse(null), "neon"); }, "Personalizar la barra", k);
                    using (Form f = Scaled(delegate { return new ToolbarCustomizeForm(ToolbarCatalog.Parse(null), "3d"); }, k))
                    {
                        ToolbarCustomizeForm d = (ToolbarCustomizeForm)f;
                        Check(d.Current.ItemHeight >= (int)(32 * k) + 6, "Filas de " + d.Current.ItemHeight + " px al " + (int)(k * 100) + " %");
                        Check(d.Current.Right < d.ClientSize.Width && d.Current.Bottom <= d.ClientSize.Height, "La lista se sale de la ventana al " + (int)(k * 100) + " %");
                    }
                }
            }
            finally { Dpi.Override = 0; }
        }

        static void TB_CustomizeLikeIdm()
        {
            using (ToolbarCustomizeForm d = new ToolbarCustomizeForm(ToolbarCatalog.Parse(null), "neon"))
            {
                IntPtr h = d.Handle;
                List<string> texts = new List<string>();
                foreach (Control c in d.Controls) if (c is Button) texts.Add(c.Text);
                string all = string.Join("|", texts.ToArray());
                foreach (string t in new string[] { "Agregar ->", "<- Quitar", "Cerrar", "Restablecer", "Subir", "Bajar" })
                    Check(texts.Contains(t), "Falta el botón " + t + ": " + all);
                Check(!texts.Contains("Aceptar") && !texts.Contains("Cancelar"), "IDM solo tiene Cerrar: " + all);
                Check(d.Text == "Personalizar la barra de herramientas", "Título: " + d.Text);
                Check(d.Current.ItemHeight >= Dpi.Px(32) + 6, "Filas de " + d.Current.ItemHeight + " px para iconos de 32");
                int end = d.Current.Items.Count - 1;
                Check((string)d.Current.Items[end] == ToolbarCustomizeForm.EndSlot && d.Current.SelectedIndex == end, "Hueco del final");
                Button remove = null, up = null, down = null;
                foreach (Control c in d.Controls) { if (c.Text == "<- Quitar") remove = (Button)c; if (c.Text == "Subir") up = (Button)c; if (c.Text == "Bajar") down = (Button)c; }
                Check(!remove.Enabled && !up.Enabled && !down.Enabled, "Con el hueco elegido: Quitar, Subir y Bajar en gris");
                d.Current.SelectedIndex = 0;
                Check(remove.Enabled && !up.Enabled && down.Enabled, "El primero: no se puede subir");
                d.Current.SelectedIndex = end - 1;
                Check(up.Enabled && !down.Enabled, "El último: no se puede bajar");
                // Separador al final (detrás de Detener cola): se añade delante del hueco.
                d.Available.SelectedItem = "grabber"; d.Current.SelectedIndex = end; d.AddSelected();
                Check((string)d.Current.Items[end] == "grabber" && d.Current.SelectedIndex == end + 1, "Agregar al final");
                d.Available.SelectedItem = ToolbarCatalog.Separator; d.Current.SelectedIndex = end; d.AddSelected();
                string r = string.Join(",", d.Collect().ToArray());
                Check(r == ToolbarCatalog.Default + ",|,grabber", "Resultado: " + r);
                d.Show(); d.Close();
                Check(d.DialogResult == DialogResult.OK && d.Result != null && string.Join(",", d.Result.ToArray()) == r, "Cerrar conserva los cambios");
            }
        }
    }
}
