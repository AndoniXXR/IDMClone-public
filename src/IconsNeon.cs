using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace IDMClone
{
    // Estilo "Neon" de la barra (el de IDM por defecto): dibujos de trazo fino (1-2 px) con un degradado de
    // colores pastel y sin relleno. Rejilla de 32x32; el grosor del trazo se ajusta para que a 20 px
    // (botones pequeños) siga viéndose de algo más de 1 px.
    public static partial class Glyphs
    {
        static readonly string[] NeonGlyphs = { "add", "resume", "stop", "stopall", "delete", "deletecompleted", "options",
                                                "scheduler", "startqueue", "stopqueue", "grabber" };

        public static bool HasNeon(string name) { return Array.IndexOf(NeonGlyphs, name) >= 0; }

        // Colores de cada dibujo (inicio y fin del degradado, de arriba a la izquierda a abajo a la derecha).
        static readonly Color NGreenA = Color.FromArgb(92, 196, 92), NGreenB = Color.FromArgb(44, 170, 150);
        static readonly Color NBlueA = Color.FromArgb(76, 150, 236), NBlueB = Color.FromArgb(52, 190, 222);
        static readonly Color NCoralA = Color.FromArgb(240, 104, 96), NCoralB = Color.FromArgb(240, 150, 84);
        static readonly Color NOrangeA = Color.FromArgb(246, 140, 60), NOrangeB = Color.FromArgb(232, 70, 118);
        static readonly Color NPinkA = Color.FromArgb(226, 84, 160), NPinkB = Color.FromArgb(160, 96, 226);
        static readonly Color NVioletA = Color.FromArgb(150, 104, 236), NVioletB = Color.FromArgb(70, 128, 240);
        static readonly Color NRoseA = Color.FromArgb(236, 102, 150), NRoseB = Color.FromArgb(246, 166, 76);

        // 1,7 px a 32 px y algo más de 1,5 px a 20 px (botones pequeños), que IDM los dibuja algo más gruesos.
        static float NeonWidth(int size) { return Math.Max(1.7f, 1.55f * 32f / Math.Max(1, size)); }

        static Pen NeonPen(Color a, Color b, float w)
        {
            LinearGradientBrush br = new LinearGradientBrush(new PointF(2, 2), new PointF(30, 30), a, b);
            Pen p = new Pen(br, w);
            br.Dispose();
            p.StartCap = p.EndCap = LineCap.Round;
            p.LineJoin = LineJoin.Round;
            return p;
        }

        static void DrawNeon(Graphics g, string name, int size)
        {
            float w = NeonWidth(size);
            switch (name)
            {
                case "add":
                    using (Pen p = NeonPen(NGreenA, NGreenB, w))
                    {
                        float a = 11.5f, b = 20.5f, lo = 3.5f, hi = 28.5f;
                        g.DrawPolygon(p, new PointF[] { new PointF(a, lo), new PointF(b, lo), new PointF(b, a), new PointF(hi, a), new PointF(hi, b), new PointF(b, b),
                                                        new PointF(b, hi), new PointF(a, hi), new PointF(a, b), new PointF(lo, b), new PointF(lo, a), new PointF(a, a) });
                    }
                    break;
                case "resume":
                    using (Pen p = NeonPen(NBlueA, NBlueB, w))
                    {
                        g.DrawEllipse(p, 4, 4, 24, 24);
                        g.DrawLine(p, 16, 9.5f, 16, 22);
                        g.DrawLines(p, new PointF[] { new PointF(11, 17), new PointF(16, 22), new PointF(21, 17) });
                    }
                    break;
                case "stop":
                    using (Pen p = NeonPen(NCoralA, NCoralB, w))
                    {
                        g.DrawEllipse(p, 4, 4, 24, 24);
                        using (GraphicsPath r = RoundRect(11, 11, 10, 10, 2.2f)) g.DrawPath(p, r);
                    }
                    break;
                case "stopall":
                    using (Pen p = NeonPen(NCoralA, NCoralB, w))
                        StrokeUnion(g, size, p, Hand());
                    break;
                case "delete":
                    using (Pen p = NeonPen(NOrangeA, NOrangeB, w))
                    {
                        TrashCan(g, p);
                        g.DrawLine(p, 13, 15, 13, 24);
                        g.DrawLine(p, 19, 15, 19, 24);
                    }
                    break;
                case "deletecompleted":
                    using (Pen p = NeonPen(NPinkA, NPinkB, w))
                    {
                        TrashCan(g, p);
                        g.DrawLines(p, new PointF[] { new PointF(11.5f, 19), new PointF(14.8f, 22.3f), new PointF(20.8f, 15.3f) });
                    }
                    break;
                case "options":
                    using (Pen p = NeonPen(NVioletA, NVioletB, w))
                    {
                        g.DrawPolygon(p, GearPoints(16, 16, 9.6f, 13f, 8));
                        g.DrawEllipse(p, 11.5f, 11.5f, 9, 9);
                    }
                    break;
                case "scheduler":
                    using (Pen p = NeonPen(NRoseA, NRoseB, w))
                    {
                        g.DrawEllipse(p, 5.5f, 7, 21, 21);
                        using (GraphicsPath bell = new GraphicsPath())
                        {
                            bell.AddArc(2.5f, 2.5f, 9, 9, 150, 150); bell.CloseFigure();
                            bell.AddArc(20.5f, 2.5f, 9, 9, 240, 150); bell.CloseFigure();
                            g.DrawPath(p, bell);
                        }
                        g.DrawLine(p, 9.5f, 26, 7.5f, 29);
                        g.DrawLine(p, 22.5f, 26, 24.5f, 29);
                        g.DrawLines(p, new PointF[] { new PointF(16, 11.5f), new PointF(16, 17.5f), new PointF(20, 20) });
                    }
                    break;
                case "startqueue":
                case "stopqueue":
                    {
                        bool start = name == "startqueue";
                        using (Pen p = NeonPen(NBlueA, NBlueB, w))
                        {
                            GraphicsState st = g.Save();
                            g.SetClip(new RectangleF(10.5f - w, 9.5f - w, 18 + 2 * w, 20 + 2 * w), CombineMode.Exclude);
                            using (GraphicsPath back = RoundRect(4.5f, 3.5f, 17, 20, 2.5f)) g.DrawPath(p, back);
                            g.Restore(st);
                            using (GraphicsPath front = RoundRect(10.5f, 9.5f, 18, 20, 2.5f)) g.DrawPath(p, front);
                        }
                        if (start)
                            using (Pen p = NeonPen(NGreenA, NGreenB, w))
                            {
                                g.DrawLine(p, 19.5f, 14, 19.5f, 24.5f);
                                g.DrawLines(p, new PointF[] { new PointF(15.5f, 20.5f), new PointF(19.5f, 24.5f), new PointF(23.5f, 20.5f) });
                            }
                        else
                            using (Pen p = NeonPen(NPinkA, NOrangeB, w))
                            using (GraphicsPath r = RoundRect(15, 15, 9, 9, 1.8f)) g.DrawPath(p, r);
                    }
                    break;
                case "grabber":
                    using (Pen p = NeonPen(NVioletA, NVioletB, w))
                    {
                        g.DrawEllipse(p, 2.5f, 2.5f, 21, 21);
                        g.DrawEllipse(p, 8.5f, 2.5f, 9, 21);
                        g.DrawLine(p, 3, 13, 23, 13);
                        g.DrawLine(p, 4.5f, 8, 21.5f, 8);
                        g.DrawLine(p, 4.5f, 18, 21.5f, 18);
                    }
                    using (Pen p = NeonPen(NOrangeA, NRoseB, w))
                    {
                        // flecha naranja hacia abajo: descargar el sitio
                        g.DrawLine(p, 26, 16.5f, 26, 28.5f);
                        g.DrawLines(p, new PointF[] { new PointF(22, 24.5f), new PointF(26, 28.5f), new PointF(30, 24.5f) });
                    }
                    break;
            }
        }

        // Papelera de trazo: tapa, asa y cuerpo con las esquinas redondeadas.
        static void TrashCan(Graphics g, Pen p)
        {
            g.DrawLine(p, 5.5f, 9, 26.5f, 9);
            g.DrawLines(p, new PointF[] { new PointF(12.5f, 9), new PointF(12.5f, 5), new PointF(19.5f, 5), new PointF(19.5f, 9) });
            using (GraphicsPath body = new GraphicsPath())
            {
                body.AddLine(8.5f, 12, 9.6f, 26);
                body.AddArc(9.6f, 24, 4, 4, 180, -90);
                body.AddLine(11.6f, 28, 20.4f, 28);
                body.AddArc(18.4f, 24, 4, 4, 90, -90);
                body.AddLine(22.4f, 26, 23.5f, 12);
                g.DrawPath(p, body);
            }
        }

        // Mano abierta (Detener todas): palma, cuatro dedos y pulgar, como una sola silueta.
        static GraphicsPath[] Hand()
        {
            GraphicsPath palm = RoundRect(8.5f, 14, 16, 15, 5.5f);
            GraphicsPath f1 = Capsule(10.6f, 8.5f, 3.6f, 10);
            GraphicsPath f2 = Capsule(14.4f, 4.5f, 3.6f, 14);
            GraphicsPath f3 = Capsule(18.2f, 5.5f, 3.6f, 13);
            GraphicsPath f4 = Capsule(22f, 9f, 3.4f, 10);
            GraphicsPath thumb = Capsule(-1.8f, -6.5f, 3.6f, 10.5f);
            using (Matrix m = new Matrix()) { m.Translate(8.6f, 22.5f); m.Rotate(-38); thumb.Transform(m); }
            return new GraphicsPath[] { palm, f1, f2, f3, f4, thumb };
        }

        static GraphicsPath Capsule(float x, float y, float w, float h)
        {
            return RoundRect(x, y, w, h, w / 2f);
        }

        static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            GraphicsPath gp = new GraphicsPath();
            float d = Math.Min(2 * r, Math.Min(w, h));
            gp.AddArc(x, y, d, d, 180, 90);
            gp.AddArc(x + w - d, y, d, d, 270, 90);
            gp.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            gp.AddArc(x, y + h - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        // Contorno de la unión de varias figuras: se dibuja cada contorno con el doble de grosor y se borra
        // lo que cae dentro de cualquiera de ellas, así solo queda la silueta exterior.
        static void StrokeUnion(Graphics g, int size, Pen pen, GraphicsPath[] shapes)
        {
            float k = size / 32f;
            using (Bitmap stroke = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            using (Bitmap mask = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics sg = Graphics.FromImage(stroke))
                using (Graphics mg = Graphics.FromImage(mask))
                using (Pen p2 = (Pen)pen.Clone())
                {
                    p2.Width = pen.Width * 2;
                    foreach (Graphics x in new Graphics[] { sg, mg })
                    {
                        x.SmoothingMode = SmoothingMode.AntiAlias;
                        x.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        x.ScaleTransform(k, k);
                    }
                    foreach (GraphicsPath s in shapes) sg.DrawPath(p2, s);
                    foreach (GraphicsPath s in shapes) mg.FillPath(Brushes.Black, s);
                }
                int[] sp = Pixels(stroke, size, size), mp = Pixels(mask, size, size);
                for (int i = 0; i < sp.Length; i++)
                {
                    int a = (sp[i] >> 24) & 255, ma = (mp[i] >> 24) & 255;
                    a = a * (255 - ma) / 255;
                    sp[i] = (a << 24) | (sp[i] & 0xFFFFFF);
                }
                using (Bitmap outline = FromPixels(sp, size, size))
                {
                    GraphicsState st = g.Save();
                    g.ResetTransform();
                    g.DrawImage(outline, 0, 0, size, size);
                    g.Restore(st);
                }
            }
            foreach (GraphicsPath s in shapes) s.Dispose();
        }
    }
}
