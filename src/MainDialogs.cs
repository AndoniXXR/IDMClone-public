using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace IDMClone
{
    // Lo que se busca con Descargas > Buscar (Ctrl+F) y Buscar Siguiente (F3).
    public class FindQuery
    {
        public string Text = "";
        public bool InName = true, InDescription, InSite, MatchCase, WholeWord;

        public bool Matches(DownloadItem it)
        {
            if (it == null || string.IsNullOrEmpty(Text)) return false;
            if (InName && Has(it.FileName)) return true;
            if (InDescription && Has(it.Description)) return true;
            if (InSite)
            {
                string host = "";
                try { host = new Uri(it.Url).Host; } catch { }
                if (Has(host) || Has(it.Url) || Has(it.Referrer)) return true;
            }
            return false;
        }

        bool Has(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (WholeWord)
            {
                RegexOptions o = MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
                return Regex.IsMatch(s, @"(?<![\p{L}\p{N}_])" + Regex.Escape(Text) + @"(?![\p{L}\p{N}_])", o);
            }
            return s.IndexOf(Text, MatchCase ? StringComparison.Ordinal : StringComparison.CurrentCultureIgnoreCase) >= 0;
        }
    }

    // "Buscar Archivo" (IDM: Descargas > Buscar).
    public class FindForm : DialogBase
    {
        TextBox _text;
        CheckBox _name, _desc, _site, _case, _words;
        Button _find;

        public FindForm(FindQuery last)
        {
            Text = "Buscar Archivo";
            ShowIcon = false; // como IDM
            ClientSize = new Size(336, 232);
            AddLabel("Buscar:", 12, 6, 200).Height = 18;
            _text = new TextBox(); _text.SetBounds(12, 26, 312, 21); Controls.Add(_text);
            _name = Check("Nombre del archivo o parte del nombre", 12, 56, 312, 20);
            _desc = Check("Descripción o parte de la descripción", 12, 78, 312, 20);
            _site = Check("Nombre del sitio/Enlace de descarga/Página web parental/Referente", 12, 100, 312, 32);
            _site.TextAlign = ContentAlignment.TopLeft; _site.CheckAlign = ContentAlignment.TopLeft;
            Label line = new Label(); line.BorderStyle = BorderStyle.Fixed3D; line.SetBounds(12, 140, 312, 2); Controls.Add(line);
            _case = Check("Coincidir mayúsculas y minúsculas", 24, 150, 300, 20);
            _words = Check("Coincidir sólo palabras completas", 24, 172, 300, 20);
            _find = AddButton("Buscar", 72, 200, 75, DialogResult.OK); _find.Height = 23;
            Button cancel = AddButton("Cancelar", 188, 200, 75, DialogResult.Cancel); cancel.Height = 23;
            AcceptButton = _find; CancelButton = cancel;
            FindQuery q = last ?? new FindQuery();
            _text.Text = q.Text ?? "";
            _name.Checked = q.InName; _desc.Checked = q.InDescription; _site.Checked = q.InSite;
            _case.Checked = q.MatchCase; _words.Checked = q.WholeWord;
            _text.TextChanged += delegate { UpdateButton(); };
            _name.CheckedChanged += delegate { UpdateButton(); };
            _desc.CheckedChanged += delegate { UpdateButton(); };
            _site.CheckedChanged += delegate { UpdateButton(); };
            UpdateButton();
            ActiveControl = _text;
            EndLayout();
        }

        CheckBox Check(string text, int x, int y, int w, int h)
        {
            CheckBox c = new CheckBox(); c.Text = text; c.SetBounds(x, y, w, h); Controls.Add(c); return c;
        }

        // Como IDM, Buscar siempre se puede pulsar (sin texto no hace nada).
        void UpdateButton() { }

        // Para las pruebas (y el botón Buscar).
        public void Set(string text, bool name, bool desc, bool site, bool matchCase, bool words)
        {
            _text.Text = text; _name.Checked = name; _desc.Checked = desc; _site.Checked = site; _case.Checked = matchCase; _words.Checked = words;
        }

        public FindQuery Query
        {
            get
            {
                FindQuery q = new FindQuery();
                q.Text = _text.Text; q.InName = _name.Checked; q.InDescription = _desc.Checked; q.InSite = _site.Checked;
                q.MatchCase = _case.Checked; q.WholeWord = _words.Checked;
                return q;
            }
        }
    }

    // "Confirmar eliminación de descargas" (como IDM): por defecto solo quita de la lista.
    public class DeleteDownloadsForm : DialogBase
    {
        CheckBox _files, _dontAsk;
        public bool DeleteFiles { get { return _files.Checked; } }
        public bool DontAskAgain { get { return _dontAsk.Checked; } }

        public DeleteDownloadsForm()
        {
            Text = "Confirmar eliminación de descargas";
            ShowIcon = false;
            ClientSize = new Size(424, 136);
            Label q = AddLabel("¿Seguro que deseas eliminar las descargas de la lista de descargas de IDMClone?", 8, 6, 410);
            q.Height = 30; q.TextAlign = ContentAlignment.TopLeft;
            _files = new CheckBox(); _files.Text = "Eliminar también los archivos descargados del disco duro. ¡Cuidado!, esta opción no es reversible.";
            _files.SetBounds(8, 40, 410, 30); _files.CheckAlign = ContentAlignment.TopLeft; _files.TextAlign = ContentAlignment.TopLeft; Controls.Add(_files);
            _dontAsk = new CheckBox(); _dontAsk.Text = "No mostrar este diálogo otra vez"; _dontAsk.SetBounds(8, 74, 410, 20); Controls.Add(_dontAsk);
            Button yes = AddButton("Sí", 118, 104, 75, DialogResult.OK); yes.Height = 23;
            Button no = AddButton("No", 230, 104, 75, DialogResult.Cancel); no.Height = 23;
            AcceptButton = yes; CancelButton = no;
            ActiveControl = yes;
            EndLayout();
        }
    }

    // "Introducir nombre de la cola" (Crear nueva cola).
    public class QueueNameForm : DialogBase
    {
        TextBox _name;
        public string QueueName { get { return _name.Text.Trim(); } }

        public QueueNameForm(string suggested)
        {
            Text = "Introducir nombre de la cola";
            ShowIcon = false;
            ClientSize = new Size(290, 110);
            Label l = AddLabel("Introducir un nombre para la nueva cola que se mostrará en la lista de colas", 8, 6, 274);
            l.Height = 30; l.TextAlign = ContentAlignment.TopLeft;
            _name = new TextBox(); _name.SetBounds(8, 40, 274, 21); _name.Text = suggested ?? ""; Controls.Add(_name);
            Button ok = AddButton("Aceptar", 60, 76, 75, DialogResult.OK); ok.Height = 23;
            Button cancel = AddButton("Cancelar", 150, 76, 75, DialogResult.Cancel); cancel.Height = 23;
            AcceptButton = ok; CancelButton = cancel;
            _name.TextChanged += delegate { ok.Enabled = _name.Text.Trim().Length > 0; };
            ActiveControl = _name;
            _name.SelectAll();
            EndLayout();
        }
    }

    // "Acerca de IDMClone": ventana pequeña como la de IDM (logo, nombre en grande, versión y Cerrar).
    public class AboutForm : DialogBase
    {
        public AboutForm()
        {
            Text = "Acerca de IDMClone";
            ClientSize = new Size(392, 300);
            PictureBox logo = new PictureBox();
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            try { using (Icon ic = AppIcon.Sized(48)) logo.Image = ic.ToBitmap(); logo.Disposed += delegate { if (logo.Image != null) logo.Image.Dispose(); }; } catch { }
            logo.SetBounds(14, 12, 40, 40); Controls.Add(logo);
            Label name = new Label(); name.Text = "IDMClone"; name.Font = new Font("Tahoma", 16f, FontStyle.Bold, GraphicsUnit.Point);
            name.Tag = Lang.Keep; name.SetBounds(64, 10, 316, 30); name.TextAlign = ContentAlignment.MiddleCenter; Controls.Add(name);
            Label ver = AddLabel("Versión: " + Application.ProductVersion, 64, 40, 316); ver.Height = 18; ver.TextAlign = ContentAlignment.MiddleCenter;

            GroupBox box = new GroupBox(); box.Text = "Este programa"; box.SetBounds(10, 66, 372, 70); Controls.Add(box);
            Label about = new Label();
            about.Text = "Gestor de descargas para Windows: descargas segmentadas que se reanudan, colas, categorías, programador, Grabber y línea de comandos.";
            about.SetBounds(10, 18, 352, 46); box.Controls.Add(about);

            TextBox notice = new TextBox(); notice.Multiline = true; notice.ReadOnly = true; notice.ScrollBars = ScrollBars.Vertical;
            notice.Text = Lang.T("IDMClone es un programa independiente: no es Internet Download Manager ni tiene relación con Tonec FZE. "
                + "Imita la forma de trabajar de IDM, pero su código, sus iconos y sus textos son propios."); // las cajas de texto no las traduce Lang.Apply
            notice.SetBounds(10, 146, 372, 58); notice.TabStop = false; Controls.Add(notice);

            Label dataL = AddLabel("Carpeta de datos:", 10, 212, 110); dataL.Height = 18;
            LinkLabel data = new LinkLabel(); data.Text = AppConfig.Dir; data.Tag = Lang.Keep; data.AutoEllipsis = true;
            data.SetBounds(120, 212, 262, 18); data.TextAlign = ContentAlignment.MiddleLeft; Controls.Add(data);
            data.LinkClicked += delegate { try { if (Directory.Exists(AppConfig.Dir)) System.Diagnostics.Process.Start(AppConfig.Dir); } catch { } };
            Label copy = AddLabel("Copyright © 2026 IDMClone.", 10, 234, 372); copy.Height = 18;

            Button close = AddButton("Cerrar", 158, 264, 75, DialogResult.Cancel); close.Height = 23;
            AcceptButton = close; CancelButton = close;
            ActiveControl = close;
            EndLayout();
        }
    }

    // Menús como los nativos de Windows 10 que usa IDM: fondo gris claro, resaltado azul claro, separadores finos y
    // viñeta "•" en las opciones excluyentes (los ToolStripMenuItem cuyo Name empieza por "radio").
    public class IdmMenuColors : ProfessionalColorTable
    {
        static readonly Color Back = Color.FromArgb(0xF2, 0xF2, 0xF2), Hot = Color.FromArgb(0x91, 0xC9, 0xF7),
            BarHot = Color.FromArgb(0xCC, 0xE8, 0xFF), Sep = Color.FromArgb(0xD7, 0xD7, 0xD7), Border = Color.FromArgb(0xCC, 0xCC, 0xCC);
        public override Color MenuStripGradientBegin { get { return Color.White; } }
        public override Color MenuStripGradientEnd { get { return Color.White; } }
        public override Color MenuItemSelected { get { return Hot; } }
        public override Color MenuItemSelectedGradientBegin { get { return BarHot; } }
        public override Color MenuItemSelectedGradientEnd { get { return BarHot; } }
        public override Color MenuItemPressedGradientBegin { get { return BarHot; } }
        public override Color MenuItemPressedGradientMiddle { get { return BarHot; } }
        public override Color MenuItemPressedGradientEnd { get { return BarHot; } }
        public override Color MenuItemBorder { get { return Color.Transparent; } }
        public override Color MenuBorder { get { return Border; } }
        public override Color ToolStripDropDownBackground { get { return Back; } }
        public override Color ImageMarginGradientBegin { get { return Back; } }
        public override Color ImageMarginGradientMiddle { get { return Back; } }
        public override Color ImageMarginGradientEnd { get { return Back; } }
        public override Color SeparatorDark { get { return Sep; } }
        public override Color SeparatorLight { get { return Back; } }
        public override Color CheckBackground { get { return Hot; } }
        public override Color CheckSelectedBackground { get { return Hot; } }
        public override Color CheckPressedBackground { get { return Hot; } }
        // Barra de herramientas: lisa #F0F0F0 (sin degradado) y botones como los de Windows 10.
        public override Color ToolStripGradientBegin { get { return Theme.DialogBack; } }
        public override Color ToolStripGradientMiddle { get { return Theme.DialogBack; } }
        public override Color ToolStripGradientEnd { get { return Theme.DialogBack; } }
        public override Color ToolStripBorder { get { return Theme.DialogBack; } }
        public override Color ButtonSelectedGradientBegin { get { return Color.FromArgb(0xE5, 0xF3, 0xFF); } }
        public override Color ButtonSelectedGradientMiddle { get { return Color.FromArgb(0xE5, 0xF3, 0xFF); } }
        public override Color ButtonSelectedGradientEnd { get { return Color.FromArgb(0xE5, 0xF3, 0xFF); } }
        public override Color ButtonSelectedHighlight { get { return Color.FromArgb(0xE5, 0xF3, 0xFF); } }
        public override Color ButtonSelectedBorder { get { return BarHot; } }
        public override Color ButtonPressedGradientBegin { get { return BarHot; } }
        public override Color ButtonPressedGradientMiddle { get { return BarHot; } }
        public override Color ButtonPressedGradientEnd { get { return BarHot; } }
        public override Color ButtonPressedHighlight { get { return BarHot; } }
        public override Color ButtonPressedBorder { get { return Color.FromArgb(0x99, 0xD1, 0xFF); } }
        public override Color ButtonCheckedGradientBegin { get { return BarHot; } }
        public override Color ButtonCheckedGradientMiddle { get { return BarHot; } }
        public override Color ButtonCheckedGradientEnd { get { return BarHot; } }
        public override Color OverflowButtonGradientBegin { get { return Theme.DialogBack; } }
        public override Color OverflowButtonGradientMiddle { get { return Theme.DialogBack; } }
        public override Color OverflowButtonGradientEnd { get { return Theme.DialogBack; } }
        public override Color GripDark { get { return Theme.DialogBack; } }
        public override Color GripLight { get { return Theme.DialogBack; } }
    }

    public class IdmMenuRenderer : ToolStripProfessionalRenderer
    {
        public static readonly IdmMenuRenderer Instance = new IdmMenuRenderer();

        public IdmMenuRenderer() : base(new IdmMenuColors()) { RoundedEdges = false; }

        public static bool IsRadio(ToolStripItem i) { return i != null && i.Name != null && i.Name.StartsWith("radio", StringComparison.Ordinal); }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            if (!IsRadio(e.Item)) { base.OnRenderItemCheck(e); return; }
            Rectangle r = e.ImageRectangle;
            using (SolidBrush b = new SolidBrush(ColorTable.CheckBackground)) e.Graphics.FillRectangle(b, Rectangle.Inflate(r, 2, 2));
            int d = Math.Max(4, r.Height / 3);
            SmoothingMode old = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush dot = new SolidBrush(e.Item.Enabled ? Color.Black : SystemColors.GrayText))
                e.Graphics.FillEllipse(dot, r.X + (r.Width - d) / 2f, r.Y + (r.Height - d) / 2f, d, d);
            e.Graphics.SmoothingMode = old;
        }

        // La barra de herramientas de IDM no tiene línea inferior.
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is ToolStripDropDown) base.OnRenderToolStripBorder(e);
        }
    }

    // Icono de la bandeja en "Estilo Clásico" (Ver > Bandeja de iconos): un dibujo plano propio.
    public static class TrayIcons
    {
        static Icon _classic;

        public static Icon Classic
        {
            get
            {
                if (_classic != null) return _classic;
                int s = SystemInformation.SmallIconSize.Width;
                using (Bitmap b = new Bitmap(s, s))
                {
                    using (Graphics g = Graphics.FromImage(b))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.ScaleTransform(s / 16f, s / 16f);
                        using (SolidBrush globe = new SolidBrush(Color.FromArgb(0x2B, 0x6C, 0xC4))) g.FillEllipse(globe, 1, 1, 14, 14);
                        using (Pen p = new Pen(Color.FromArgb(0xA8, 0xCF, 0xF5), 1f)) { g.DrawEllipse(p, 4, 1, 8, 14); g.DrawLine(p, 1, 8, 15, 8); }
                        PointF[] arrow = { new PointF(6, 3), new PointF(10, 3), new PointF(10, 8), new PointF(13, 8), new PointF(8, 14), new PointF(3, 8), new PointF(6, 8) };
                        using (SolidBrush a = new SolidBrush(Color.FromArgb(0x2E, 0xB8, 0x2E))) g.FillPolygon(a, arrow);
                        using (Pen o = new Pen(Color.FromArgb(0x10, 0x60, 0x10), 0.8f)) g.DrawPolygon(o, arrow);
                    }
                    _classic = Icon.FromHandle(b.GetHicon());
                }
                return _classic;
            }
        }
    }
}
