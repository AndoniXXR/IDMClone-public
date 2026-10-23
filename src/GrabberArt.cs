using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace IDMClone
{
    // Dibujos propios del Grabber (no se copian los de IDM): la ilustración lateral del asistente (globo de alambre
    // con cubos de colores que vuelan hacia él, en el espíritu de la de IDM), los iconos pequeños "3D" de su barra y
    // los del árbol de la ventana principal del proyecto.
    public static class GrabberArt
    {
        static readonly Dictionary<string, Bitmap> _cache = new Dictionary<string, Bitmap>();

        // Ilustración del asistente (IDM: ~130x262 a la izquierda de cada paso).
        public static Bitmap Wizard(int w, int h)
        {
            w = Math.Max(8, w); h = Math.Max(8, h);
            string key = "art@" + w + "x" + h;
            lock (_cache)
            {
                Bitmap b;
                if (_cache.TryGetValue(key, out b)) return b;
                b = new Bitmap(w, h);
                using (Graphics g = Graphics.FromImage(b)) DrawWizard(g, w, h);
                _cache[key] = b;
                return b;
            }
        }

        static void DrawWizard(Graphics g, int w, int h)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Rectangle all = new Rectangle(0, 0, w, h);
            // Fondo: azul noche arriba a la izquierda que se aclara hacia abajo a la derecha.
            using (LinearGradientBrush bg = new LinearGradientBrush(all, Color.FromArgb(8, 22, 70), Color.FromArgb(40, 110, 205), 65f))
                g.FillRectangle(bg, all);
            // Resplandor blanco detrás del globo.
            PointF gc = new PointF(w * 0.80f, h * 0.40f);
            float gr = w * 0.62f;
            using (GraphicsPath glow = new GraphicsPath())
            {
                glow.AddEllipse(gc.X - gr * 1.45f, gc.Y - gr * 1.45f, gr * 2.9f, gr * 2.9f);
                using (PathGradientBrush pb = new PathGradientBrush(glow))
                {
                    pb.CenterPoint = gc;
                    pb.CenterColor = Color.FromArgb(235, 245, 252, 255);
                    pb.SurroundColors = new Color[] { Color.FromArgb(0, 60, 140, 230) };
                    g.FillPath(pb, glow);
                }
            }
            Random rnd = new Random(11);
            // Estelas de luz que barren de abajo a la izquierda hacia el globo.
            for (int i = 0; i < 7; i++)
            {
                float y0 = h * (0.62f + i * 0.05f);
                using (Pen p = new Pen(Color.FromArgb(70 + i * 15, 220, 238, 255), 1f + (i % 3)))
                    g.DrawBezier(p, -4, y0, w * 0.25f, y0 - h * 0.10f, w * 0.45f, gc.Y + gr * 0.9f - i * 3, gc.X - gr * 0.3f, gc.Y + gr * 0.15f - i * 4);
            }
            // Globo de alambre: meridianos y paralelos.
            using (Pen wire = new Pen(Color.FromArgb(200, 120, 200, 255), 1.1f))
            using (Pen wire2 = new Pen(Color.FromArgb(150, 255, 255, 255), 0.8f))
            {
                g.DrawEllipse(wire, gc.X - gr, gc.Y - gr, gr * 2, gr * 2);
                for (int i = 1; i < 6; i++)
                {
                    float rx = gr * (float)Math.Abs(Math.Cos(i * Math.PI / 6));
                    g.DrawEllipse(i % 2 == 0 ? wire2 : wire, gc.X - rx, gc.Y - gr, rx * 2, gr * 2);
                }
                for (int i = -2; i <= 2; i++)
                {
                    float y = gc.Y + i * gr / 3f;
                    float rx = (float)Math.Sqrt(Math.Max(0, gr * gr - (y - gc.Y) * (y - gc.Y)));
                    g.DrawEllipse(i == 0 ? wire : wire2, gc.X - rx, y - rx * 0.16f, rx * 2, rx * 0.32f);
                }
            }
            // Puntos de luz (estrellas) y nodos brillantes en el globo.
            using (SolidBrush star = new SolidBrush(Color.FromArgb(190, 255, 255, 255)))
                for (int i = 0; i < 45; i++) { float x = rnd.Next(w), y = rnd.Next(h); float s = 0.8f + (float)rnd.NextDouble() * 1.6f; g.FillEllipse(star, x, y, s, s); }
            // Cubos de colores que vuelan hacia el globo (en una espiral, más pequeños cuanto más cerca).
            Color[] cols = { Color.FromArgb(250, 205, 40), Color.FromArgb(225, 50, 45), Color.FromArgb(60, 185, 75), Color.FromArgb(245, 245, 245),
                             Color.FromArgb(50, 120, 230), Color.FromArgb(250, 140, 30), Color.FromArgb(150, 90, 210) };
            // Más lejos del globo (abajo a la izquierda) son más grandes; se dibujan de lejos a cerca.
            for (int i = 0; i < 34; i++)
            {
                double t = Math.Pow((34 - i) / 34.0, 1.2);               // 1 = lejos del globo, 0 = junto a él
                double a = rnd.NextDouble() * 1.4 + 1.7;                  // dirección: hacia abajo a la izquierda
                double dist = (0.25 + 0.95 * t) * w;
                float x = gc.X + (float)(Math.Cos(a) * dist * 1.05) + (float)(rnd.NextDouble() - 0.5) * w * 0.25f;
                float y = gc.Y + (float)(Math.Sin(a) * dist * 1.7) * 0.6f + (float)(t * h * 0.38) + (float)(rnd.NextDouble() - 0.5) * h * 0.08f;
                if (x < 2 || y > h - 2 || y < 4) continue;
                float s = (float)(w * (0.045 + 0.12 * t)) * (0.75f + (float)rnd.NextDouble() * 0.5f);
                Cube(g, x, y, s, cols[i % cols.Length], (float)(rnd.NextDouble() * 50 - 25));
            }
            // Flechas pequeñas que apuntan al globo.
            for (int i = 0; i < 5; i++)
            {
                float x = w * (0.12f + i * 0.13f), y = h * (0.20f + (i % 3) * 0.17f);
                Arrow(g, x, y, gc, w * 0.09f, i % 2 == 0 ? Color.FromArgb(255, 225, 90) : Color.FromArgb(210, 240, 255));
            }
            // Línea fina de borde, como el marco de la imagen de IDM.
            using (Pen edge = new Pen(Color.FromArgb(90, 0, 0, 40))) g.DrawRectangle(edge, 0, 0, w - 1, h - 1);
        }

        // Cubo isométrico: cara de arriba clara, izquierda media, derecha oscura.
        static void Cube(Graphics g, float x, float y, float s, Color c, float angle)
        {
            if (s < 2) return;
            GraphicsState st = g.Save();
            g.TranslateTransform(x, y);
            g.RotateTransform(angle);
            float hx = s * 0.5f, hy = s * 0.28f;
            PointF top = new PointF(0, -hy * 2), left = new PointF(-hx, -hy), right = new PointF(hx, -hy), mid = new PointF(0, 0);
            PointF bl = new PointF(-hx, s * 0.55f - hy), br = new PointF(hx, s * 0.55f - hy), bm = new PointF(0, s * 0.55f);
            using (SolidBrush b = new SolidBrush(Light(c, 0.45f))) g.FillPolygon(b, new PointF[] { top, right, mid, left });
            using (SolidBrush b = new SolidBrush(c)) g.FillPolygon(b, new PointF[] { left, mid, bm, bl });
            using (SolidBrush b = new SolidBrush(Dark(c, 0.35f))) g.FillPolygon(b, new PointF[] { mid, right, br, bm });
            using (Pen p = new Pen(Color.FromArgb(120, 20, 20, 30), 0.6f))
            {
                g.DrawPolygon(p, new PointF[] { top, right, br, bm, bl, left });
                g.DrawLine(p, mid, bm); g.DrawLine(p, left, mid); g.DrawLine(p, mid, right);
            }
            g.Restore(st);
        }

        static void Arrow(Graphics g, float x, float y, PointF target, float len, Color c)
        {
            double a = Math.Atan2(target.Y - y, target.X - x);
            PointF tip = new PointF(x + (float)Math.Cos(a) * len, y + (float)Math.Sin(a) * len);
            using (Pen p = new Pen(Color.FromArgb(220, c), Math.Max(1f, len / 9f)))
            {
                p.CustomEndCap = new AdjustableArrowCap(3, 3);
                g.DrawLine(p, x, y, tip.X, tip.Y);
            }
        }

        static Color Light(Color c, float k) { return Color.FromArgb(c.A, c.R + (int)((255 - c.R) * k), c.G + (int)((255 - c.G) * k), c.B + (int)((255 - c.B) * k)); }
        static Color Dark(Color c, float k) { return Color.FromArgb(c.A, (int)(c.R * (1 - k)), (int)(c.G * (1 - k)), (int)(c.B * (1 - k))); }

        // ------------------------------------------------------------------ iconos (rejilla de 20x20)

        // Iconos pequeños de estilo 3D (relleno, degradado y sombra), como la barra del Grabber de IDM.
        public static Bitmap Icon(string name, int size)
        {
            size = Math.Max(8, size);
            string key = name + "@" + size;
            lock (_cache)
            {
                Bitmap b;
                if (_cache.TryGetValue(key, out b)) return b;
                b = new Bitmap(size, size);
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.ScaleTransform(size / 20f, size / 20f);
                    DrawIcon(g, name);
                }
                _cache[key] = b;
                return b;
            }
        }

        static void DrawIcon(Graphics g, string name)
        {
            switch (name)
            {
                case "explore": Globe(g, 1, 2, 14, false); PlayArrow(g, 11, 9, 8, Color.FromArgb(60, 180, 60)); break;
                case "explore-stop": Globe(g, 1, 2, 14, true); StopBox(g, 11, 10, 7); break;
                case "checkall": Folder(g, 1, 4, 17, 13, false); Check(g, 7, 8, Color.FromArgb(30, 150, 40)); break;
                case "uncheckall": Folder(g, 1, 4, 17, 13, false); Box(g, 8, 8, 8); break;
                case "download": PlayArrow(g, 3, 2, 16, Color.FromArgb(40, 175, 50)); break;
                case "download-stop": StopBox(g, 4, 4, 12); break;
                case "addlist":
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(70, 120, 210))) g.FillRectangle(b, 2, 12, 16, 6);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(250, 205, 50))) g.FillRectangle(b, 3, 8, 14, 5);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(240, 240, 240))) g.FillRectangle(b, 4, 4, 12, 5);
                    using (Pen p = new Pen(Color.FromArgb(90, 70, 40), 0.8f)) { g.DrawRectangle(p, 2, 12, 16, 6); g.DrawRectangle(p, 3, 8, 14, 5); g.DrawRectangle(p, 4, 4, 12, 5); }
                    DownArrow(g, 13, 0, Color.FromArgb(40, 160, 50));
                    break;
                case "refresh":
                    using (Pen p = new Pen(Color.FromArgb(40, 110, 215), 2.6f))
                    {
                        p.CustomEndCap = new AdjustableArrowCap(2.2f, 2.2f);
                        g.DrawArc(p, 3, 3, 14, 14, 200, 140);
                        g.DrawArc(p, 3, 3, 14, 14, 20, 140);
                    }
                    break;
                case "schedule":
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(240, 130, 40))) { g.FillEllipse(b, 1, 1, 6, 6); g.FillEllipse(b, 13, 1, 6, 6); }
                    using (LinearGradientBrush b = new LinearGradientBrush(new RectangleF(2, 3, 16, 16), Color.FromArgb(255, 180, 80), Color.FromArgb(220, 100, 20), 90f))
                        g.FillEllipse(b, 2, 3, 16, 16);
                    using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, 4.5f, 5.5f, 11, 11);
                    using (Pen p = new Pen(Color.FromArgb(60, 40, 20), 1.3f)) { g.DrawLine(p, 10, 11, 10, 7); g.DrawLine(p, 10, 11, 13, 12); }
                    break;
                case "stats":
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(60, 120, 220))) g.FillRectangle(b, 3, 9, 4, 9);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(60, 170, 70))) g.FillRectangle(b, 8, 4, 4, 14);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(225, 70, 50))) g.FillRectangle(b, 13, 11, 4, 7);
                    using (Pen p = new Pen(Color.FromArgb(80, 80, 80), 1f)) { g.DrawLine(p, 1.5f, 18.5f, 18.5f, 18.5f); g.DrawLine(p, 1.5f, 2, 1.5f, 18.5f); }
                    break;
                // Árbol de la ventana principal del proyecto (16 px).
                case "tree-thumbs":
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(120, 180, 240))) g.FillRectangle(b, 2, 3, 16, 14);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(70, 160, 60))) g.FillPolygon(b, new PointF[] { new PointF(2, 17), new PointF(8, 9), new PointF(12, 13), new PointF(14, 11), new PointF(18, 17) });
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 220, 60))) g.FillEllipse(b, 12, 5, 4, 4);
                    using (Pen p = new Pen(Color.FromArgb(90, 90, 110), 1f)) g.DrawRectangle(p, 2, 3, 16, 14);
                    break;
                case "tree-folder": Folder(g, 1, 3, 18, 14, false); break;
                case "tree-folder-open": Folder(g, 1, 3, 18, 14, true); break;
                case "tree-links": Page(g); Globe(g, 7, 7, 12, false); break;
                case "tree-page": Page(g); Globe(g, 9, 9, 10, false); break;
            }
        }

        static void Globe(Graphics g, float x, float y, float d, bool gray)
        {
            RectangleF r = new RectangleF(x, y, d, d);
            Color a = gray ? Color.FromArgb(215, 215, 215) : Color.FromArgb(150, 210, 255), b = gray ? Color.FromArgb(130, 130, 130) : Color.FromArgb(20, 90, 200);
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddEllipse(r);
                using (PathGradientBrush pb = new PathGradientBrush(p))
                {
                    pb.CenterPoint = new PointF(x + d * 0.35f, y + d * 0.3f);
                    pb.CenterColor = a; pb.SurroundColors = new Color[] { b };
                    g.FillPath(pb, p);
                }
            }
            using (SolidBrush land = new SolidBrush(gray ? Color.FromArgb(160, 160, 160) : Color.FromArgb(70, 170, 70)))
            {
                g.FillEllipse(land, x + d * 0.18f, y + d * 0.22f, d * 0.32f, d * 0.26f);
                g.FillEllipse(land, x + d * 0.50f, y + d * 0.52f, d * 0.30f, d * 0.24f);
            }
            using (Pen p = new Pen(Color.FromArgb(gray ? 110 : 30, gray ? 110 : 60, gray ? 110 : 140), 0.8f)) g.DrawEllipse(p, r);
        }

        static void PlayArrow(Graphics g, float x, float y, float s, Color c)
        {
            PointF[] tri = { new PointF(x, y), new PointF(x + s * 0.9f, y + s / 2), new PointF(x, y + s) };
            using (LinearGradientBrush b = new LinearGradientBrush(new RectangleF(x, y, s, s), Light(c, 0.45f), Dark(c, 0.25f), 90f)) g.FillPolygon(b, tri);
            using (Pen p = new Pen(Dark(c, 0.45f), 0.8f)) g.DrawPolygon(p, tri);
        }

        static void StopBox(Graphics g, float x, float y, float s)
        {
            using (LinearGradientBrush b = new LinearGradientBrush(new RectangleF(x, y, s, s), Color.FromArgb(250, 120, 100), Color.FromArgb(190, 30, 25), 90f)) g.FillRectangle(b, x, y, s, s);
            using (Pen p = new Pen(Color.FromArgb(120, 20, 15), 0.8f)) g.DrawRectangle(p, x, y, s, s);
        }

        static void Folder(Graphics g, float x, float y, float w, float h, bool open)
        {
            using (SolidBrush back = new SolidBrush(Color.FromArgb(225, 175, 40)))
            {
                g.FillRectangle(back, x, y + 1.5f, w * 0.42f, 2.5f);
                g.FillRectangle(back, x, y + 3, w, h - 3);
            }
            PointF[] front = open
                ? new PointF[] { new PointF(x + 2.5f, y + 6), new PointF(x + w + 0.5f, y + 6), new PointF(x + w - 2, y + h), new PointF(x, y + h) }
                : new PointF[] { new PointF(x, y + 5), new PointF(x + w, y + 5), new PointF(x + w, y + h), new PointF(x, y + h) };
            using (LinearGradientBrush b = new LinearGradientBrush(new RectangleF(x, y + 4, w, h - 3), Color.FromArgb(255, 232, 130), Color.FromArgb(240, 190, 50), 90f)) g.FillPolygon(b, front);
            using (Pen p = new Pen(Color.FromArgb(170, 125, 20), 0.8f)) { g.DrawPolygon(p, front); g.DrawLine(p, x, y + 1.5f, x + w * 0.42f, y + 1.5f); }
        }

        static void Check(Graphics g, float x, float y, Color c)
        {
            using (Pen p = new Pen(c, 2.4f)) { p.StartCap = p.EndCap = LineCap.Round; g.DrawLines(p, new PointF[] { new PointF(x, y + 4), new PointF(x + 3, y + 7), new PointF(x + 9, y) }); }
        }

        static void Box(Graphics g, float x, float y, float s)
        {
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillRectangle(b, x, y, s, s);
            using (Pen p = new Pen(Color.FromArgb(90, 90, 90), 1f)) g.DrawRectangle(p, x, y, s, s);
        }

        static void DownArrow(Graphics g, float x, float y, Color c)
        {
            PointF[] a = { new PointF(x + 1.5f, y), new PointF(x + 4.5f, y), new PointF(x + 4.5f, y + 4), new PointF(x + 6.5f, y + 4), new PointF(x + 3, y + 8), new PointF(x - 0.5f, y + 4), new PointF(x + 1.5f, y + 4) };
            using (SolidBrush b = new SolidBrush(c)) g.FillPolygon(b, a);
            using (Pen p = new Pen(Dark(c, 0.4f), 0.6f)) g.DrawPolygon(p, a);
        }

        static void Page(Graphics g)
        {
            PointF[] pg = { new PointF(3, 1), new PointF(12, 1), new PointF(16, 5), new PointF(16, 19), new PointF(3, 19) };
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillPolygon(b, pg);
            using (Pen p = new Pen(Color.FromArgb(110, 120, 140), 0.9f)) { g.DrawPolygon(p, pg); g.DrawLines(p, new PointF[] { new PointF(12, 1), new PointF(12, 5), new PointF(16, 5) }); }
        }

        // ------------------------------------------------------------------ vista previa del panel de los navegadores

        // El panel "Descargar este video" de la extensión (modo completo o mini), para la ventana de personalizarlo.
        public static Bitmap PanelPreview(string text, bool mini, int scalePx)
        {
            float k = Math.Max(1f, scalePx / 20f);
            Font f = new Font(Theme.UiFamily, 12f * k, FontStyle.Bold, GraphicsUnit.Pixel);
            int h = (int)(22 * k);
            int textW;
            using (Bitmap tmp = new Bitmap(1, 1)) using (Graphics tg = Graphics.FromImage(tmp)) textW = mini ? 0 : (int)Math.Ceiling(tg.MeasureString(text, f).Width);
            int mainW = (int)(22 * k) + (mini ? 0 : textW + (int)(6 * k));
            int w = mainW + (int)((mini ? 20 : 40) * k);
            Bitmap b = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                Rectangle r = new Rectangle(0, 0, mainW, h - 1);
                using (LinearGradientBrush bg = new LinearGradientBrush(r, Color.FromArgb(246, 250, 255), Color.FromArgb(190, 216, 243), 90f)) g.FillRectangle(bg, r);
                using (Pen p = new Pen(Color.FromArgb(120, 160, 210))) g.DrawRectangle(p, r);
                float ts = 12 * k;
                PointF[] tri = { new PointF(6 * k, (h - ts) / 2), new PointF(6 * k + ts * 0.9f, h / 2f), new PointF(6 * k, (h + ts) / 2) };
                using (LinearGradientBrush gb = new LinearGradientBrush(new RectangleF(0, 0, 20 * k, h), Color.FromArgb(110, 215, 110), Color.FromArgb(25, 150, 40), 90f)) g.FillPolygon(gb, tri);
                if (!mini) using (SolidBrush tb = new SolidBrush(Color.FromArgb(30, 60, 120))) g.DrawString(text, f, tb, 22 * k, (h - f.Height) / 2f);
                int x = mainW + (int)(2 * k);
                string[] small = mini ? new string[] { "X" } : new string[] { "?", "X" };
                foreach (string s in small)
                {
                    Rectangle q = new Rectangle(x, (int)(3 * k), (int)(16 * k), h - (int)(7 * k));
                    using (LinearGradientBrush bg = new LinearGradientBrush(q, Color.FromArgb(240, 246, 252), Color.FromArgb(175, 200, 228), 90f)) g.FillRectangle(bg, q);
                    using (Pen p = new Pen(Color.FromArgb(120, 150, 190))) g.DrawRectangle(p, q);
                    using (Font sf = new Font("Tahoma", 9f * k, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (SolidBrush tb = new SolidBrush(Color.FromArgb(40, 70, 130)))
                    using (StringFormat c = new StringFormat()) { c.Alignment = c.LineAlignment = StringAlignment.Center; g.DrawString(s, sf, tb, q, c); }
                    x += (int)(18 * k);
                }
            }
            f.Dispose();
            return b;
        }
    }
}
