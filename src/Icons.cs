using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace IDMClone
{
    // Escala de la pantalla (100 % = 1, 125 % = 1.25...). WinForms ya agranda solo los controles y las
    // letras; esto es para lo que no escala solo: anchos de columnas, iconos y alturas fijas.
    public static class Dpi
    {
        public static float Override;   // para simular otra escala en las pruebas
        static float _real;

        public static float Factor
        {
            get
            {
                if (Override > 0) return Override;
                if (_real <= 0)
                {
                    try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) _real = g.DpiX / 96f; }
                    catch { _real = 1f; }
                }
                return _real;
            }
        }

        public static int Px(int logical) { return (int)Math.Round(logical * Factor); }

        // Casillas de varias líneas: con la pantalla al 150 % el alto escalado se queda a 1-2 px de lo que piden sus
        // líneas y GDI no dibuja la última (se veía "...si hay errores de" sin el resto). Al abrir la ventana (ya
        // escalada y traducida) se les da el alto que piden, sin pasar del control que tengan debajo.
        public static void FitWrappedChecks(Control root)
        {
            foreach (Control c in root.Controls)
            {
                CheckBox b = c as CheckBox;
                if (b != null && !b.AutoSize && b.Height > Px(20) && b.Text.Length > 0)
                {
                    int textW = Math.Max(10, b.Width - Px(17));   // la casilla y el hueco hasta el texto
                    Size need = TextRenderer.MeasureText(b.Text, b.Font, new Size(textW, int.MaxValue), TextFormatFlags.WordBreak);
                    int want = need.Height + Px(4);
                    if (want > b.Height)
                    {
                        int limit = root.ClientSize.Height - b.Top;
                        foreach (Control s in root.Controls)
                            if (s != b && s.Top >= b.Top + 2 && s.Left < b.Right && s.Right > b.Left) limit = Math.Min(limit, s.Top - b.Top);
                        b.Height = Math.Max(b.Height, Math.Min(want, limit));
                    }
                }
                if (c.HasChildren) FitWrappedChecks(c);
            }
        }
    }

    // Iconos de la barra de herramientas, del árbol y del Grabber, dibujados en código (no se copian los de
    // IDM; se imita su estilo). Dos juegos para la barra, como IDM:
    //  - "neon" (el de IDM por defecto): trazo fino de 1-2 px en colores pastel, sin relleno (IconsNeon.cs).
    //  - "3d" ("3D Style"): dibujos clásicos con relleno y sombra (carpeta amarilla, engranajes, despertador...).
    // Los iconos del árbol (16 px) son de estilo XP: carpetas amarillas, nota musical azul, etc.
    public static partial class Glyphs
    {
        public const string StyleNeon = "neon", Style3D = "3d";

        static readonly Dictionary<string, Bitmap> _cache = new Dictionary<string, Bitmap>();

        // Estilo guardado en la configuración -> "neon" o "3d". Vacío = el de IDM por defecto (neón); los nombres
        // del estilo clásico ("3d", "3D Style", "classic"...) son 3D; cualquier otro valor desconocido, neón.
        public static string NormalizeStyle(string style)
        {
            string s = (style ?? "").Trim().ToLowerInvariant();
            if (s.IndexOf("3d", StringComparison.Ordinal) >= 0 || s == "classic" || s == "clasico" || s == "clásico") return Style3D;
            return StyleNeon;
        }

        // Icono clásico (relleno): árbol, Grabber y demás sitios que no siguen el estilo de la barra.
        public static Bitmap Get(string name, int size) { return Get(name, size, Style3D); }

        // Icono de la barra en el estilo elegido. Los nombres sin versión de neón (los del árbol, el Grabber...)
        // salen en su dibujo clásico.
        public static Bitmap Get(string name, int size, string style)
        {
            if (size < 1) size = 1;
            string st = NormalizeStyle(style);
            if (st == StyleNeon && !HasNeon(name)) st = Style3D;
            string key = name + "@" + size + "#" + st;
            lock (_cache)
            {
                Bitmap b;
                if (_cache.TryGetValue(key, out b)) return b;
                b = Render(name ?? "", size, st);
                _cache[key] = b;
                return b;
            }
        }

        // Versión deshabilitada (gris claro, como IDM) del icono de la barra.
        public static Bitmap GetDisabled(string name, int size, string style)
        {
            return (Bitmap)Disabled(Get(name, size, style));
        }

        static readonly ConditionalWeakTable<Image, Image> _disabled = new ConditionalWeakTable<Image, Image>();

        // Cualquier imagen en gris claro (los botones deshabilitados de IDM). Se guarda una por imagen.
        public static Image Disabled(Image src)
        {
            if (src == null) return null;
            lock (_disabled)
            {
                Image d;
                if (_disabled.TryGetValue(src, out d)) return d;
                d = MakeDisabled(src);
                _disabled.Add(src, d);
                return d;
            }
        }

        static Bitmap MakeDisabled(Image src)
        {
            int w = Math.Max(1, src.Width), h = Math.Max(1, src.Height);
            int[] px = Pixels(src, w, h);
            for (int i = 0; i < px.Length; i++)
            {
                int c = px[i], a = (c >> 24) & 255;
                if (a == 0) continue;
                int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
                double lum = 0.30 * r + 0.59 * g + 0.11 * b;
                int v = (int)Math.Round(255 - (255 - lum) * 0.42);  // gris claro (~#C0C0C0 para un trazo medio)
                if (v > 236) v = 236;                               // que el blanco no desaparezca del todo
                px[i] = ((int)(a * 0.9) << 24) | (v << 16) | (v << 8) | v;
            }
            return FromPixels(px, w, h);
        }

        static int[] Pixels(Image src, int w, int h)
        {
            using (Bitmap c = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(c)) g.DrawImage(src, 0, 0, w, h);
                BitmapData d = c.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                int[] px = new int[w * h];
                Marshal.Copy(d.Scan0, px, 0, px.Length);
                c.UnlockBits(d);
                return px;
            }
        }

        static Bitmap FromPixels(int[] px, int w, int h)
        {
            Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            BitmapData d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(px, 0, d.Scan0, px.Length);
            b.UnlockBits(d);
            return b;
        }

        static Bitmap Render(string name, int size, string style)
        {
            Bitmap b = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                if (style == StyleNeon)
                {
                    g.ScaleTransform(size / 32f, size / 32f);     // rejilla de 32x32
                    DrawNeon(g, name, size);
                }
                else if (IsTreeGlyph(name))
                {
                    g.ScaleTransform(size / 16f, size / 16f);     // rejilla de 16x16, líneas de 1 px nítidas a 100 %
                    DrawTree(g, name);
                }
                else
                {
                    g.ScaleTransform(size / 32f, size / 32f);
                    Draw3D(g, name);
                }
            }
            return b;
        }

        // ---------------------------------------------------------------------------------------------
        // Árbol de categorías y colas (16 px, estilo XP)
        // ---------------------------------------------------------------------------------------------

        static readonly string[] TreeGlyphs = { "folder", "folder-open", "folder-unfinished", "folder-finished", "queues", "queue", "queue-sync",
                                                "cat-general", "cat-compressed", "cat-documents", "cat-music", "cat-programs", "cat-video" };

        public static bool IsTreeGlyph(string name) { return Array.IndexOf(TreeGlyphs, name) >= 0; }

        // Icono de una categoría del árbol (como IDM: Comprimido, Documentos, Música, Programas, Video), según sus
        // extensiones o, si no se reconocen, su nombre. null = carpeta normal (o el icono del tipo de archivo).
        public static string CategoryGlyph(string name, string extensions)
        {
            string[] exts = (extensions ?? "").ToLowerInvariant().Split(new char[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in exts)
            {
                string e = raw.TrimStart('*', '.');
                if (In(e, "zip", "rar", "7z", "gz", "gzip", "tar", "bz2", "arj", "ace", "lzh", "sit", "sitx", "sea", "r0*", "r1*", "z", "cab", "xz")) return "cat-compressed";
                if (In(e, "mp3", "wav", "wma", "mpa", "ram", "ra", "aac", "aif", "m4a", "flac", "ogg", "opus", "tsa", "mid")) return "cat-music";
                if (In(e, "avi", "mpg", "mpe", "mpeg", "asf", "wmv", "mov", "qt", "rm", "mp4", "flv", "m4v", "webm", "ogv", "ogm", "mkv", "3gp", "rmvb", "ts")) return "cat-video";
                if (In(e, "exe", "msi", "msu", "apk", "dmg", "bin", "iso", "img")) return "cat-programs";
                if (In(e, "doc", "docx", "pdf", "ppt", "pptx", "pps", "xls", "xlsx", "txt", "rtf", "odt", "ods", "plj", "csv", "epub")) return "cat-documents";
            }
            string n = (name ?? "").Trim().ToLowerInvariant();
            if (n.StartsWith("compr")) return "cat-compressed";
            if (n.StartsWith("músic") || n.StartsWith("music") || n.StartsWith("audio")) return "cat-music";
            if (n.StartsWith("vídeo") || n.StartsWith("video")) return "cat-video";
            if (n.StartsWith("program")) return "cat-programs";
            if (n.StartsWith("document")) return "cat-documents";
            return null;
        }

        static bool In(string s, params string[] list) { return Array.IndexOf(list, s) >= 0; }

        static readonly Color FolderYellow = Color.FromArgb(250, 212, 96), FolderGreen = Color.FromArgb(128, 206, 96);

        static void DrawTree(Graphics g, string name)
        {
            switch (name)
            {
                case "folder":
                case "queues":
                case "cat-general":
                    XpFolder(g, FolderYellow, false);
                    break;
                case "folder-open":
                    XpFolder(g, FolderYellow, true);
                    break;
                case "folder-unfinished":
                    XpFolder(g, FolderYellow, false);
                    // flecha verde hacia abajo (aún descargando)
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(52, 176, 66)))
                    using (Pen p = new Pen(Color.FromArgb(24, 112, 34), 1f))
                    {
                        PointF[] arrow = { new PointF(9.5f, 6.5f), new PointF(13.5f, 6.5f), new PointF(13.5f, 10.5f), new PointF(15.5f, 10.5f),
                                           new PointF(11.5f, 15.5f), new PointF(7.5f, 10.5f), new PointF(9.5f, 10.5f) };
                        g.FillPolygon(b, arrow);
                        g.DrawPolygon(p, arrow);
                    }
                    break;
                case "folder-finished":
                    XpFolder(g, FolderYellow, false);
                    using (Pen w = new Pen(Color.White, 4f)) { w.StartCap = w.EndCap = LineCap.Round; w.LineJoin = LineJoin.Round; g.DrawLines(w, CheckPts()); }
                    using (Pen p = new Pen(Color.FromArgb(36, 96, 216), 2.2f)) { p.StartCap = p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round; g.DrawLines(p, CheckPts()); }
                    break;
                case "queue":
                    XpFolder(g, FolderYellow, false, true);
                    break;
                case "queue-sync":
                    XpFolder(g, FolderGreen, false, true);
                    break;
                case "cat-documents":
                    XpPage(g);
                    break;
                case "cat-compressed":
                    {
                        RectangleF r = new RectangleF(2.5f, 1.5f, 11, 13);
                        using (LinearGradientBrush b = new LinearGradientBrush(r, Color.FromArgb(255, 232, 140), Color.FromArgb(234, 182, 60), 0f)) g.FillRectangle(b, r);
                        using (Pen p = new Pen(Color.FromArgb(166, 118, 30), 1f)) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(128, 84, 36))) g.FillRectangle(b, 7, 2, 2, 12);
                        using (Pen p = new Pen(Color.FromArgb(250, 238, 200), 1f))
                            for (int y = 3; y <= 9; y += 2) g.DrawLine(p, y % 4 == 1 ? 7f : 8f, y + 0.5f, y % 4 == 1 ? 8f : 9f, y + 0.5f);
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(200, 200, 205))) g.FillRectangle(b, 6, 10, 4, 3);
                        using (Pen p = new Pen(Color.FromArgb(110, 110, 120), 1f)) g.DrawRectangle(p, 6.5f, 10.5f, 3, 2);
                    }
                    break;
                case "cat-music":
                    {
                        Color blue = Color.FromArgb(32, 92, 214), dark = Color.FromArgb(18, 52, 150);
                        using (SolidBrush b = new SolidBrush(blue))
                        {
                            g.FillEllipse(b, 1.5f, 10.5f, 5f, 4f);
                            g.FillEllipse(b, 9f, 9f, 5f, 4f);
                            g.FillPolygon(b, new PointF[] { new PointF(5.6f, 3.4f), new PointF(13.2f, 1.2f), new PointF(13.2f, 3.8f), new PointF(5.6f, 6f) });
                        }
                        using (Pen p = new Pen(blue, 1.3f)) { g.DrawLine(p, 5.9f, 12.4f, 5.9f, 3.6f); g.DrawLine(p, 13.4f, 10.9f, 13.4f, 1.6f); }
                        using (Pen p = new Pen(dark, 0.8f)) { g.DrawEllipse(p, 1.5f, 10.5f, 5f, 4f); g.DrawEllipse(p, 9f, 9f, 5f, 4f); }
                        using (SolidBrush hl = new SolidBrush(Color.FromArgb(150, 255, 255, 255))) { g.FillEllipse(hl, 2.6f, 11.2f, 2f, 1.3f); g.FillEllipse(hl, 10.1f, 9.7f, 2f, 1.3f); }
                    }
                    break;
                case "cat-programs":
                    {
                        using (SolidBrush b = new SolidBrush(Color.White)) g.FillRectangle(b, 1, 3, 14, 11);
                        using (LinearGradientBrush b = new LinearGradientBrush(new RectangleF(1, 3, 14, 3), Color.FromArgb(84, 150, 240), Color.FromArgb(28, 80, 200), 90f)) g.FillRectangle(b, 1, 3, 14, 3);
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(222, 232, 248))) g.FillRectangle(b, 2, 7, 3, 6);
                        using (Pen p = new Pen(Color.FromArgb(44, 74, 150), 1f)) g.DrawRectangle(p, 0.5f, 2.5f, 15, 12);
                        using (SolidBrush b = new SolidBrush(Color.White)) g.FillRectangle(b, 12, 4, 2, 1);
                    }
                    break;
                case "cat-video":
                    {
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(40, 64, 150))) g.FillRectangle(b, 0, 2, 16, 12);
                        using (LinearGradientBrush b = new LinearGradientBrush(new RectangleF(2, 5, 12, 6), Color.FromArgb(170, 214, 255), Color.FromArgb(70, 136, 230), 90f)) g.FillRectangle(b, 2, 5, 12, 6);
                        using (SolidBrush b = new SolidBrush(Color.White))
                            for (int x = 1; x < 16; x += 3) { g.FillRectangle(b, x, 3, 1.5f, 1); g.FillRectangle(b, x, 12, 1.5f, 1); }
                        using (Pen p = new Pen(Color.FromArgb(24, 40, 110), 1f)) g.DrawRectangle(p, 0.5f, 2.5f, 15, 11);
                    }
                    break;
            }
        }

        static PointF[] CheckPts() { return new PointF[] { new PointF(6.5f, 10f), new PointF(9.3f, 13f), new PointF(14.5f, 6.5f) }; }

        static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(a.A, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
        static Color Light(Color c, float t) { return Mix(c, Color.White, t); }
        static Color Dark(Color c, float t) { return Mix(c, Color.Black, t); }

        // Carpeta XP de 16 px: trasera con pestaña y delantera con degradado (abierta: delantera inclinada).
        static void XpFolder(Graphics g, Color c, bool open, bool page = false)
        {
            Color line = Dark(c, 0.42f), back = Dark(c, 0.1f);
            PointF[] rear = { new PointF(0.5f, 2.5f), new PointF(5.5f, 2.5f), new PointF(7f, 4f), new PointF(14.5f, 4f), new PointF(14.5f, 13.5f), new PointF(0.5f, 13.5f) };
            using (SolidBrush b = new SolidBrush(back)) g.FillPolygon(b, rear);
            using (Pen p = new Pen(line, 1f)) g.DrawPolygon(p, rear);
            if (page || open)
            {
                float top = open ? 4.5f : 1.5f;
                using (SolidBrush b = new SolidBrush(Color.White)) g.FillRectangle(b, 3.5f, top, 9, 7);
                using (Pen p = new Pen(Color.FromArgb(150, 150, 150), 1f)) g.DrawRectangle(p, 3.5f, top, 9, 7);
                using (Pen p = new Pen(Color.FromArgb(170, 185, 210), 1f)) { g.DrawLine(p, 5, top + 2, 11, top + 2); g.DrawLine(p, 5, top + 4, 11, top + 4); }
            }
            if (!open)
            {
                RectangleF front = new RectangleF(0.5f, 6.5f, 14, 7);
                using (LinearGradientBrush b = new LinearGradientBrush(new RectangleF(0, 6, 15, 8), Light(c, 0.55f), c, 90f)) g.FillRectangle(b, front);
                using (Pen p = new Pen(line, 1f)) g.DrawRectangle(p, front.X, front.Y, front.Width, front.Height);
                using (Pen p = new Pen(Color.FromArgb(160, 255, 255, 255), 1f)) g.DrawLine(p, 1.5f, 7.5f, 13.5f, 7.5f);
            }
            else
            {
                PointF[] front = { new PointF(3f, 7.5f), new PointF(15.5f, 7.5f), new PointF(13f, 13.5f), new PointF(0.5f, 13.5f) };
                using (LinearGradientBrush b = new LinearGradientBrush(new RectangleF(0, 7, 16, 7), Light(c, 0.55f), c, 90f)) g.FillPolygon(b, front);
                using (Pen p = new Pen(line, 1f)) g.DrawPolygon(p, front);
            }
        }

        static void XpPage(Graphics g)
        {
            PointF[] pg = { new PointF(2.5f, 0.5f), new PointF(10f, 0.5f), new PointF(13.5f, 4f), new PointF(13.5f, 15.5f), new PointF(2.5f, 15.5f) };
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillPolygon(b, pg);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(222, 222, 222))) g.FillPolygon(b, new PointF[] { new PointF(10f, 0.5f), new PointF(10f, 4f), new PointF(13.5f, 4f) });
            using (Pen p = new Pen(Color.FromArgb(128, 128, 128), 1f)) { g.DrawPolygon(p, pg); g.DrawLines(p, new PointF[] { new PointF(10f, 0.5f), new PointF(10f, 4f), new PointF(13.5f, 4f) }); }
            using (Pen p = new Pen(Color.FromArgb(150, 170, 210), 1f))
                for (int y = 6; y <= 12; y += 2) g.DrawLine(p, 4.5f, y + 0.5f, 11.5f, y + 0.5f);
        }

        // ---------------------------------------------------------------------------------------------
        // "3D Style": iconos clásicos con relleno, degradado y brillo (rejilla de 32x32)
        // ---------------------------------------------------------------------------------------------

        static readonly Color Blue = Color.FromArgb(33, 118, 214), BlueDark = Color.FromArgb(18, 74, 150);
        static readonly Color Green = Color.FromArgb(46, 160, 67), GreenDark = Color.FromArgb(26, 110, 42);
        static readonly Color Red = Color.FromArgb(214, 55, 45), RedDark = Color.FromArgb(150, 30, 25);
        static readonly Color Orange = Color.FromArgb(240, 150, 30);
        static readonly Color Yellow3D = Color.FromArgb(252, 204, 60);

        static void Draw3D(Graphics g, string name)
        {
            switch (name)
            {
                case "add":
                    FolderBack(g, new RectangleF(1, 5, 26, 21), Yellow3D);
                    Doc(g, 6, 3, 17, 17);
                    FolderFront(g, new RectangleF(1, 5, 26, 21), Yellow3D);
                    Ball(g, new RectangleF(16, 15, 16, 16), Blue, BlueDark);
                    using (Pen p = new Pen(Color.White, 2.8f)) { p.StartCap = p.EndCap = LineCap.Round; g.DrawLine(p, 24, 19, 24, 27); g.DrawLine(p, 20, 23, 28, 23); }
                    break;
                case "resume":
                    Doc(g, 4, 1, 19, 25);
                    Ball(g, new RectangleF(13, 13, 18, 18), Green, GreenDark);
                    Arrow(g, 22, 22, 5.2f, Color.White);
                    break;
                case "stop":
                    Doc(g, 4, 1, 19, 25);
                    Octagon(g, 22, 22, 9);
                    break;
                case "stopall":
                    Doc(g, 7, 1, 18, 22);
                    Doc(g, 2, 5, 18, 22);
                    Octagon(g, 22, 22, 9);
                    break;
                case "delete":
                    Bin(g, Color.FromArgb(222, 52, 40), false);
                    break;
                case "deletecompleted":
                    Bin(g, Color.FromArgb(52, 96, 210), true);
                    break;
                case "options":
                    Gear(g, 12.5f, 12.5f, 8.2f, 11.6f, 9, Color.FromArgb(150, 158, 172));
                    Gear(g, 23f, 23f, 5.4f, 8f, 8, Color.FromArgb(244, 150, 40));
                    break;
                case "scheduler":
                    AlarmClock(g);
                    break;
                case "startqueue":
                    FolderBack(g, new RectangleF(1, 4, 26, 22), Yellow3D);
                    Doc(g, 5, 2, 16, 16);
                    FolderFront(g, new RectangleF(1, 4, 26, 22), Yellow3D);
                    Ball(g, new RectangleF(15, 14, 17, 17), Green, GreenDark);
                    Play(g, 24.2f, 22.5f, 4.6f, Color.White);
                    break;
                case "stopqueue":
                    FolderBack(g, new RectangleF(1, 4, 26, 22), Yellow3D);
                    Doc(g, 5, 2, 16, 16);
                    FolderFront(g, new RectangleF(1, 4, 26, 22), Yellow3D);
                    Barrier(g);
                    break;
                case "grabber":
                    Globe(g, new RectangleF(1, 1, 24, 24));
                    FolderBack(g, new RectangleF(14, 16, 17, 14), Yellow3D);
                    FolderFront(g, new RectangleF(14, 16, 17, 14), Yellow3D);
                    break;
                // --- Grabber (barra de su ventana) ---
                case "project":
                    Globe(g, new RectangleF(3, 3, 26, 26));
                    break;
                case "explore":
                    Globe(g, new RectangleF(1, 1, 24, 24));
                    Ball(g, new RectangleF(15, 15, 16, 16), Green, GreenDark);
                    Play(g, 23.6f, 23, 4.6f, Color.White);
                    break;
                case "explore-stop":
                    Globe(g, new RectangleF(1, 1, 24, 24));
                    Ball(g, new RectangleF(15, 15, 16, 16), Red, RedDark);
                    Square(g, 23, 23, 3.4f, Color.White);
                    break;
                case "checkall":
                    for (int i = 0; i < 3; i++)
                    {
                        float y = 3 + i * 9;
                        using (SolidBrush b = new SolidBrush(Color.White)) g.FillRectangle(b, 2, y, 8, 8);
                        using (Pen p = new Pen(Color.FromArgb(90, 100, 115), 1.2f)) g.DrawRectangle(p, 2, y, 8, 8);
                        Check(g, 3.2f, y + 1.8f, 5.6f, Green, 2f);
                        using (Pen p = new Pen(Color.FromArgb(90, 100, 115), 2.2f)) g.DrawLine(p, 14, y + 4, 30, y + 4);
                    }
                    break;
                case "uncheckall":
                    for (int i = 0; i < 3; i++)
                    {
                        float y = 3 + i * 9;
                        using (SolidBrush b = new SolidBrush(Color.White)) g.FillRectangle(b, 2, y, 8, 8);
                        using (Pen p = new Pen(Color.FromArgb(90, 100, 115), 1.2f)) g.DrawRectangle(p, 2, y, 8, 8);
                        using (Pen p = new Pen(Color.FromArgb(90, 100, 115), 2.2f)) g.DrawLine(p, 14, y + 4, 30, y + 4);
                    }
                    break;
                case "download":
                    Ball(g, new RectangleF(2, 2, 28, 28), Green, GreenDark);
                    Arrow(g, 16, 16, 8, Color.White);
                    break;
                case "download-stop":
                    Ball(g, new RectangleF(2, 2, 28, 28), Red, RedDark);
                    Square(g, 16, 16, 6, Color.White);
                    break;
                case "addlist":
                    ListLines(g, 1, 6, 20);
                    Ball(g, new RectangleF(15, 15, 16, 16), Blue, BlueDark);
                    using (Pen p = new Pen(Color.White, 2.6f)) { p.StartCap = p.EndCap = LineCap.Round; g.DrawLine(p, 23, 19, 23, 27); g.DrawLine(p, 19, 23, 27, 23); }
                    break;
                case "stats":
                    using (Pen p = new Pen(Color.FromArgb(90, 100, 115), 1.6f)) { g.DrawLine(p, 3, 3, 3, 29); g.DrawLine(p, 3, 29, 30, 29); }
                    float[] hs = { 10, 18, 13, 23 };
                    Color[] cs = { Blue, Green, Orange, Red };
                    for (int i = 0; i < 4; i++)
                        using (LinearGradientBrush b = new LinearGradientBrush(new RectangleF(6 + i * 6, 28 - hs[i], 5, hs[i]), ControlPaint.Light(cs[i], 0.5f), cs[i], 0f))
                            g.FillRectangle(b, 6 + i * 6, 28 - hs[i], 5, hs[i]);
                    break;
                default:
                    // Nombre desconocido: un documento en blanco (mejor que un hueco en la barra).
                    Doc(g, 6, 2, 20, 27);
                    break;
            }
        }

        static void Ball(Graphics g, RectangleF r, Color light, Color dark)
        {
            using (LinearGradientBrush br = new LinearGradientBrush(r, ControlPaint.Light(light, 0.4f), dark, 60f))
                g.FillEllipse(br, r);
            using (Pen p = new Pen(dark, 1.2f)) g.DrawEllipse(p, r);
            RectangleF hl = new RectangleF(r.X + r.Width * 0.18f, r.Y + r.Height * 0.08f, r.Width * 0.64f, r.Height * 0.38f);
            using (LinearGradientBrush br = new LinearGradientBrush(hl, Color.FromArgb(150, Color.White), Color.FromArgb(0, Color.White), 90f))
                g.FillEllipse(br, hl);
        }

        static void Doc(Graphics g, float x, float y, float w, float h)
        {
            float f = Math.Min(7, w * 0.35f);
            PointF[] pts = { new PointF(x, y), new PointF(x + w - f, y), new PointF(x + w, y + f), new PointF(x + w, y + h), new PointF(x, y + h) };
            // sombra
            using (SolidBrush sh = new SolidBrush(Color.FromArgb(50, 0, 0, 0)))
                g.FillPolygon(sh, new PointF[] { new PointF(x + 1.5f, y + 1.5f), new PointF(x + w + 1.5f - f, y + 1.5f), new PointF(x + w + 1.5f, y + f + 1.5f), new PointF(x + w + 1.5f, y + h + 1.5f), new PointF(x + 1.5f, y + h + 1.5f) });
            using (LinearGradientBrush br = new LinearGradientBrush(new RectangleF(x, y, w, h), Color.White, Color.FromArgb(218, 226, 238), 70f))
                g.FillPolygon(br, pts);
            using (Pen p = new Pen(Color.FromArgb(110, 120, 135), 1.1f)) g.DrawPolygon(p, pts);
            using (SolidBrush fold = new SolidBrush(Color.FromArgb(200, 208, 222)))
                g.FillPolygon(fold, new PointF[] { new PointF(x + w - f, y), new PointF(x + w - f, y + f), new PointF(x + w, y + f) });
            using (Pen p = new Pen(Color.FromArgb(110, 120, 135), 1f)) g.DrawLines(p, new PointF[] { new PointF(x + w - f, y), new PointF(x + w - f, y + f), new PointF(x + w, y + f) });
            using (Pen p = new Pen(Color.FromArgb(160, 175, 200), 1.3f))
                for (float ly = y + f + 3; ly < y + h - 3; ly += 3.6f) g.DrawLine(p, x + 3, ly, x + w - 4, ly);
        }

        // Carpeta amarilla en dos partes (para meter algo dentro): trasera con pestaña y delantera en perspectiva.
        static void FolderBack(Graphics g, RectangleF r, Color c)
        {
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            PointF[] back = { new PointF(x, y + h * 0.05f), new PointF(x + w * 0.36f, y + h * 0.05f), new PointF(x + w * 0.46f, y + h * 0.2f),
                              new PointF(x + w, y + h * 0.2f), new PointF(x + w, y + h), new PointF(x, y + h) };
            using (LinearGradientBrush b = new LinearGradientBrush(new RectangleF(x, y, w, h + 1), Dark(c, 0.05f), Dark(c, 0.3f), 90f)) g.FillPolygon(b, back);
            using (Pen p = new Pen(Dark(c, 0.45f), 1f)) { p.LineJoin = LineJoin.Round; g.DrawPolygon(p, back); }
        }

        static void FolderFront(Graphics g, RectangleF r, Color c)
        {
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            PointF[] front = { new PointF(x + w * 0.14f, y + h * 0.4f), new PointF(x + w + 2f, y + h * 0.4f), new PointF(x + w, y + h), new PointF(x, y + h) };
            using (LinearGradientBrush b = new LinearGradientBrush(new RectangleF(x, y + h * 0.4f - 1, w, h * 0.6f + 2), Light(c, 0.6f), c, 90f)) g.FillPolygon(b, front);
            using (Pen p = new Pen(Dark(c, 0.45f), 1f)) { p.LineJoin = LineJoin.Round; g.DrawPolygon(p, front); }
            using (Pen p = new Pen(Color.FromArgb(170, 255, 255, 255), 1f)) g.DrawLine(p, x + w * 0.14f + 1.5f, y + h * 0.4f + 1.2f, x + w + 0.5f, y + h * 0.4f + 1.2f);
        }

        // Señal de stop roja (octógono con borde blanco y una barra blanca).
        static void Octagon(Graphics g, float cx, float cy, float r)
        {
            PointF[] o = new PointF[8], i2 = new PointF[8];
            for (int i = 0; i < 8; i++)
            {
                double a = Math.PI / 8 + i * Math.PI / 4;
                o[i] = new PointF(cx + (float)(r * Math.Cos(a)), cy + (float)(r * Math.Sin(a)));
                i2[i] = new PointF(cx + (float)(r * 0.8 * Math.Cos(a)), cy + (float)(r * 0.8 * Math.Sin(a)));
            }
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillPolygon(b, o);
            using (LinearGradientBrush b = new LinearGradientBrush(new RectangleF(cx - r, cy - r, 2 * r, 2 * r), Color.FromArgb(250, 96, 80), Color.FromArgb(170, 20, 18), 70f)) g.FillPolygon(b, i2);
            using (Pen p = new Pen(Color.FromArgb(130, 20, 20), 1f)) g.DrawPolygon(p, o);
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillRectangle(b, cx - r * 0.5f, cy - r * 0.14f, r, r * 0.28f);
        }

        // Papelera abierta con un papel amarillo dentro (Eliminar); en azul y con una marca verde, Eliminar completadas.
        static void Bin(Graphics g, Color c, bool check)
        {
            // boca (interior oscuro)
            using (SolidBrush b = new SolidBrush(Dark(c, 0.55f))) g.FillEllipse(b, 5, 8, 22, 6);
            // papel
            PointF[] paper = { new PointF(10, 11), new PointF(12, 1.5f), new PointF(22.5f, 3.5f), new PointF(20.5f, 12) };
            using (LinearGradientBrush b = new LinearGradientBrush(new RectangleF(10, 1, 13, 12), Color.FromArgb(255, 244, 160), Color.FromArgb(240, 196, 50), 90f)) g.FillPolygon(b, paper);
            using (Pen p = new Pen(Color.FromArgb(176, 136, 30), 1f)) g.DrawPolygon(p, paper);
            using (Pen p = new Pen(Color.FromArgb(200, 160, 60), 1f)) { g.DrawLine(p, 13.5f, 5, 20.5f, 6.4f); g.DrawLine(p, 13f, 7.5f, 20f, 8.9f); }
            // cuerpo
            PointF[] body = { new PointF(5, 11), new PointF(27, 11), new PointF(24.5f, 29), new PointF(7.5f, 29) };
            using (LinearGradientBrush b = new LinearGradientBrush(new RectangleF(5, 10, 22, 20), Dark(c, 0.2f), Dark(c, 0.35f), 0f))
            {
                ColorBlend cb = new ColorBlend(3);
                cb.Colors = new Color[] { Dark(c, 0.25f), Light(c, 0.35f), Dark(c, 0.35f) };
                cb.Positions = new float[] { 0f, 0.38f, 1f };
                b.InterpolationColors = cb;
                g.FillPolygon(b, body);
            }
            using (Pen p = new Pen(Dark(c, 0.5f), 1f)) { p.LineJoin = LineJoin.Round; g.DrawPolygon(p, body); }
            using (Pen p = new Pen(Color.FromArgb(90, 0, 0, 0), 1.2f)) { g.DrawLine(p, 11.5f, 14, 12.3f, 26.5f); g.DrawLine(p, 16, 14, 16, 26.5f); g.DrawLine(p, 20.5f, 14, 19.7f, 26.5f); }
            // borde de la boca
            using (Pen p = new Pen(Light(c, 0.45f), 1.6f)) g.DrawArc(p, 5, 8, 22, 6, 0, 180);
            using (Pen p = new Pen(Dark(c, 0.5f), 1f)) g.DrawEllipse(p, 5, 8, 22, 6);
            if (check)
            {
                Ball(g, new RectangleF(17, 17, 15, 15), Green, GreenDark);
                Check(g, 20.5f, 21.2f, 8, Color.White, 2.4f);
            }
        }

        // Puntos del contorno de un engranaje (también lo usa el estilo neón).
        static PointF[] GearPoints(float cx, float cy, float rIn, float rOut, int teeth)
        {
            List<PointF> pts = new List<PointF>();
            double step = 2 * Math.PI / teeth, half = step / 2;
            for (int i = 0; i < teeth; i++)
            {
                double a = i * step - Math.PI / 2;
                double[] ang = { a - half * 0.92, a - half * 0.5, a + half * 0.5, a + half * 0.92 };
                // base del diente, punta, punta, base
                pts.Add(new PointF(cx + (float)(rIn * Math.Cos(ang[0])), cy + (float)(rIn * Math.Sin(ang[0]))));
                pts.Add(new PointF(cx + (float)(rOut * Math.Cos(ang[1])), cy + (float)(rOut * Math.Sin(ang[1]))));
                pts.Add(new PointF(cx + (float)(rOut * Math.Cos(ang[2])), cy + (float)(rOut * Math.Sin(ang[2]))));
                pts.Add(new PointF(cx + (float)(rIn * Math.Cos(ang[3])), cy + (float)(rIn * Math.Sin(ang[3]))));
            }
            return pts.ToArray();
        }

        static void Gear(Graphics g, float cx, float cy, float rIn, float rOut, int teeth, Color c)
        {
            PointF[] pts = GearPoints(cx, cy, rIn, rOut, teeth);
            using (LinearGradientBrush b = new LinearGradientBrush(new RectangleF(cx - rOut, cy - rOut, 2 * rOut, 2 * rOut), Light(c, 0.5f), Dark(c, 0.35f), 55f)) g.FillPolygon(b, pts);
            using (Pen p = new Pen(Dark(c, 0.5f), 1f)) { p.LineJoin = LineJoin.Round; g.DrawPolygon(p, pts); }
            float h = rIn * 0.45f;
            using (LinearGradientBrush b = new LinearGradientBrush(new RectangleF(cx - h, cy - h, 2 * h, 2 * h), Dark(c, 0.55f), Light(c, 0.2f), 55f)) g.FillEllipse(b, cx - h, cy - h, 2 * h, 2 * h);
            using (Pen p = new Pen(Dark(c, 0.5f), 1f)) g.DrawEllipse(p, cx - h, cy - h, 2 * h, 2 * h);
        }

        // Despertador naranja con esfera blanca.
        static void AlarmClock(Graphics g)
        {
            Color o = Color.FromArgb(250, 160, 30), od = Color.FromArgb(190, 90, 10);
            using (Pen p = new Pen(Color.FromArgb(90, 70, 50), 2.2f)) { p.StartCap = p.EndCap = LineCap.Round; g.DrawLine(p, 9, 26, 6, 30); g.DrawLine(p, 23, 26, 26, 30); }
            Ball(g, new RectangleF(2, 2, 10, 10), o, od);
            Ball(g, new RectangleF(20, 2, 10, 10), o, od);
            using (Pen p = new Pen(Color.FromArgb(90, 70, 50), 1.8f)) { p.StartCap = p.EndCap = LineCap.Round; g.DrawLine(p, 16, 3.5f, 16, 6); }
            RectangleF body = new RectangleF(4, 6, 24, 24);
            using (LinearGradientBrush b = new LinearGradientBrush(body, Color.FromArgb(255, 205, 90), od, 60f)) g.FillEllipse(b, body);
            using (Pen p = new Pen(od, 1.1f)) g.DrawEllipse(p, body);
            RectangleF face = new RectangleF(7.5f, 9.5f, 17, 17);
            using (LinearGradientBrush b = new LinearGradientBrush(face, Color.FromArgb(232, 232, 232), Color.White, 60f)) g.FillEllipse(b, face);
            using (Pen p = new Pen(Dark(od, 0.2f), 1f)) g.DrawEllipse(p, face);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(90, 90, 90)))
                for (int i = 0; i < 12; i += 3)
                {
                    double a = i * Math.PI / 6;
                    g.FillEllipse(b, 16 + (float)(6.6 * Math.Cos(a)) - 0.9f, 18 + (float)(6.6 * Math.Sin(a)) - 0.9f, 1.8f, 1.8f);
                }
            using (Pen p = new Pen(Color.FromArgb(40, 40, 40), 1.8f)) { p.StartCap = p.EndCap = LineCap.Round; g.DrawLine(p, 16, 18, 16, 12.5f); g.DrawLine(p, 16, 18, 20, 20); }
            using (SolidBrush b = new SolidBrush(Red)) g.FillEllipse(b, 14.8f, 16.8f, 2.4f, 2.4f);
        }

        // Barrera rayada roja y blanca cruzando la carpeta (Detener cola).
        static void Barrier(Graphics g)
        {
            GraphicsState st = g.Save();
            g.TranslateTransform(17, 21);
            g.RotateTransform(-28);
            RectangleF bar = new RectangleF(-15, -3.2f, 30, 6.4f);
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillRectangle(b, bar);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(222, 36, 30)))
                for (float x = -15; x < 15; x += 7.5f)
                    g.FillPolygon(b, new PointF[] { new PointF(x, 3.2f), new PointF(x + 3.2f, -3.2f), new PointF(Math.Min(15, x + 6.6f), -3.2f), new PointF(Math.Min(15, x + 3.4f), 3.2f) });
            using (Pen p = new Pen(Color.FromArgb(120, 20, 20), 1f)) g.DrawRectangle(p, bar.X, bar.Y, bar.Width, bar.Height);
            using (Pen p = new Pen(Color.FromArgb(120, 255, 255, 255), 1f)) g.DrawLine(p, bar.X + 1, bar.Y + 1, bar.Right - 1, bar.Y + 1);
            g.Restore(st);
        }

        static void Play(Graphics g, float cx, float cy, float r, Color c)
        {
            using (SolidBrush b = new SolidBrush(c))
                g.FillPolygon(b, new PointF[] { new PointF(cx - r * 0.55f, cy - r), new PointF(cx + r, cy), new PointF(cx - r * 0.55f, cy + r) });
        }

        static void Square(Graphics g, float cx, float cy, float r, Color c)
        {
            using (SolidBrush b = new SolidBrush(c)) g.FillRectangle(b, cx - r, cy - r, r * 2, r * 2);
        }

        static void Check(Graphics g, float x, float y, float s, Color c, float width)
        {
            using (Pen p = new Pen(c, width)) { p.StartCap = p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round; g.DrawLines(p, new PointF[] { new PointF(x, y + s * 0.5f), new PointF(x + s * 0.38f, y + s * 0.88f), new PointF(x + s, y) }); }
        }

        static void ListLines(Graphics g, float x, float y, float w)
        {
            using (SolidBrush dot = new SolidBrush(BlueDark))
            using (Pen p = new Pen(Color.FromArgb(90, 100, 115), 2.2f))
                for (int i = 0; i < 4; i++)
                {
                    float ly = y + i * 6;
                    g.FillEllipse(dot, x, ly - 1.6f, 3.2f, 3.2f);
                    g.DrawLine(p, x + 6, ly, x + w, ly);
                }
        }

        // Globo terráqueo (meridianos y paralelos).
        static void Globe(Graphics g, RectangleF r)
        {
            using (LinearGradientBrush b = new LinearGradientBrush(r, Color.FromArgb(120, 190, 250), BlueDark, 60f)) g.FillEllipse(b, r);
            using (Pen p = new Pen(Color.FromArgb(210, 235, 255), Math.Max(1f, r.Width / 22f)))
            {
                float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                g.DrawEllipse(p, r.X + r.Width * 0.28f, r.Y, r.Width * 0.44f, r.Height);
                g.DrawLine(p, cx, r.Y, cx, r.Bottom);
                g.DrawLine(p, r.X, cy, r.Right, cy);
                g.DrawLine(p, r.X + r.Width * 0.08f, r.Y + r.Height * 0.27f, r.Right - r.Width * 0.08f, r.Y + r.Height * 0.27f);
                g.DrawLine(p, r.X + r.Width * 0.08f, r.Y + r.Height * 0.73f, r.Right - r.Width * 0.08f, r.Y + r.Height * 0.73f);
            }
            using (Pen p = new Pen(BlueDark, 1.2f)) g.DrawEllipse(p, r);
        }

        static void Arrow(Graphics g, float cx, float cy, float r, Color c)
        {
            using (Pen p = new Pen(c, r * 0.5f)) { p.StartCap = p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                g.DrawLine(p, cx, cy - r, cx, cy + r * 0.7f);
                g.DrawLines(p, new PointF[] { new PointF(cx - r * 0.8f, cy), new PointF(cx, cy + r * 0.8f), new PointF(cx + r * 0.8f, cy) }); }
        }

        // Imagen lateral del asistente del Grabber (dibujada: degradado, globo y enlaces).
        public static Bitmap GrabberArt(int w, int h)
        {
            Bitmap bmp = new Bitmap(Math.Max(1, w), Math.Max(1, h));
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (LinearGradientBrush b = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(18, 40, 92), Color.FromArgb(70, 150, 225), 70f))
                    g.FillRectangle(b, 0, 0, w, h);
                Random rnd = new Random(7);
                using (SolidBrush star = new SolidBrush(Color.FromArgb(160, Color.White)))
                    for (int i = 0; i < 40; i++) { float x = rnd.Next(w), y = rnd.Next(h); g.FillEllipse(star, x, y, 1.6f, 1.6f); }
                float gw = w * 0.86f;
                RectangleF globe = new RectangleF(w * 0.07f, h * 0.30f, gw, gw);
                using (Pen link = new Pen(Color.FromArgb(150, 255, 255, 255), 1.4f))
                {
                    PointF c = new PointF(globe.X + gw / 2, globe.Y + gw / 2);
                    for (int i = 0; i < 6; i++)
                    {
                        double a = i * Math.PI / 3 + 0.3;
                        PointF q = new PointF(c.X + (float)Math.Cos(a) * gw * 0.75f, c.Y + (float)Math.Sin(a) * gw * 0.75f);
                        g.DrawLine(link, c, q);
                        using (SolidBrush node = new SolidBrush(i % 2 == 0 ? Color.FromArgb(255, 210, 90) : Color.FromArgb(140, 230, 140)))
                            g.FillRectangle(node, q.X - 4, q.Y - 3, 8, 6);
                    }
                }
                Globe(g, globe);
            }
            return bmp;
        }
    }

    // Iconos de Windows por tipo de archivo (los mismos que muestra el Explorador), sin tocar el disco.
    public static class ShellIcons
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr h);

        const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_SMALLICON = 0x1, SHGFI_USEFILEATTRIBUTES = 0x10, SHGFI_TYPENAME = 0x400;
        const uint FILE_ATTRIBUTE_NORMAL = 0x80, FILE_ATTRIBUTE_DIRECTORY = 0x10;

        static readonly Dictionary<string, Bitmap> _cache = new Dictionary<string, Bitmap>();
        static readonly Dictionary<string, string> _types = new Dictionary<string, string>();

        public static string ExtensionOf(string fileName)
        {
            string e = "";
            try { e = Path.GetExtension(fileName ?? "") ?? ""; } catch { }
            return e.ToLowerInvariant();
        }

        // Icono (16 o 32 px) para un nombre de archivo según su extensión.
        public static Bitmap For(string fileName, bool large)
        {
            string ext = ExtensionOf(fileName);
            string key = ext + (large ? "@L" : "@S");
            lock (_cache)
            {
                Bitmap b;
                if (_cache.TryGetValue(key, out b)) return b;
                b = Load(string.IsNullOrEmpty(ext) ? "archivo" : "archivo" + ext, FILE_ATTRIBUTE_NORMAL, large);
                _cache[key] = b;
                return b;
            }
        }

        // "Documento PDF", "Aplicación"... como en la columna Tipo del Explorador.
        public static string TypeName(string fileName)
        {
            string ext = ExtensionOf(fileName);
            lock (_types)
            {
                string t;
                if (_types.TryGetValue(ext, out t)) return t;
                SHFILEINFO info = new SHFILEINFO();
                try { SHGetFileInfo(string.IsNullOrEmpty(ext) ? "archivo" : "archivo" + ext, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf(info), SHGFI_USEFILEATTRIBUTES | SHGFI_TYPENAME); }
                catch { }
                t = string.IsNullOrEmpty(info.szTypeName) ? (ext.Length > 1 ? "Archivo " + ext.Substring(1).ToUpperInvariant() : "Archivo") : info.szTypeName;
                _types[ext] = t;
                return t;
            }
        }

        static Bitmap Load(string path, uint attrs, bool large)
        {
            int size = large ? 32 : 16;
            SHFILEINFO info = new SHFILEINFO();
            try
            {
                SHGetFileInfo(path, attrs, ref info, (uint)Marshal.SizeOf(info), SHGFI_ICON | SHGFI_USEFILEATTRIBUTES | (large ? SHGFI_LARGEICON : SHGFI_SMALLICON));
                if (info.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        using (Icon ic = Icon.FromHandle(info.hIcon))
                        {
                            Bitmap b = new Bitmap(size, size);
                            using (Graphics g = Graphics.FromImage(b)) g.DrawIcon(ic, new Rectangle(0, 0, size, size));
                            return b;
                        }
                    }
                    finally { DestroyIcon(info.hIcon); }
                }
            }
            catch { }
            return new Bitmap(Glyphs.Get("folder", size)); // no debería pasar
        }
    }
}
