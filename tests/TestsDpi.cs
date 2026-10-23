using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace IDMClone.Tests
{
    // Escala de pantalla al 125 % y 150 %. No se cambia la configuración de Windows: se hace lo mismo
    // que hace WinForms en un equipo así (agranda controles y letras en la misma proporción) y se
    // comprueba que nada se corta ni se pisa.
    public static partial class TestMain
    {
        static readonly float[] Scales = { 1f, 1.25f, 1.5f };

        static void RunDpi()
        {
            Run("escala 100/125/150 %: diálogos sin textos cortados ni controles superpuestos", T_DpiDialogs);
            Run("escala 150 %: ventana de progreso al ocultar y mostrar detalles", T_DpiProgress);
            Run("escala 150 %: ventana principal (columnas, iconos, árbol)", T_DpiMain);
        }

        // Lo que hace Windows a esa escala y la ventana no hace sola: letras más grandes (las propias de cada control
        // también). Los controles ya los escaló la propia ventana al construirse (Dpi.ScaleForm, con Dpi.Override = k):
        // la simulación y la pantalla de verdad pasan por el mismo código.
        public static void SimulateDpi(Form f, float k)
        {
            if (k == 1f) return;
            List<KeyValuePair<Control, Font>> own = new List<KeyValuePair<Control, Font>>();
            CollectFonts(f, own);
            foreach (KeyValuePair<Control, Font> kv in own)
                kv.Key.Font = new Font(kv.Value.FontFamily, kv.Value.Size * k, kv.Value.Style, kv.Value.Unit);
            // Con una pantalla de verdad la ventana ya está escalada al abrirse (OnShown); aquí se escala después.
            Dpi.FitWrappedChecks(f);
        }

        static void CollectFonts(Control c, List<KeyValuePair<Control, Font>> own)
        {
            if (c.Parent == null || !c.Font.Equals(c.Parent.Font)) own.Add(new KeyValuePair<Control, Font>(c, c.Font));
            foreach (Control x in c.Controls) CollectFonts(x, own);
        }

        static Form Scaled(Func<Form> make, float k)
        {
            Dpi.Override = k;
            Form f = make();
            SimulateDpi(f, k);
            IntPtr h = f.Handle;
            f.PerformLayout();
            return f;
        }

        // ¿Cabe el texto de etiquetas, casillas y botones en su control?
        static void CheckTextFits(Control parent, string name)
        {
            foreach (Control c in parent.Controls)
            {
                CheckTextFits(c, name);
                if (string.IsNullOrEmpty(c.Text) || c is TextBoxBase || c is ComboBox || c is NumericUpDown || c is ListControl) continue;
                if (c is TabPage || c is TabControl || c is Panel || c is ToolStrip || c is ListView || c is TreeView || c is DataGridView) continue;
                Label l = c as Label;
                if (l != null && (l.AutoSize || l.AutoEllipsis)) continue;
                int avail = c.ClientSize.Width;
                TextFormatFlags fl = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl;
                if (c is CheckBox || c is RadioButton) avail -= (int)(20 * c.Font.Size / 9f);
                if (c is Button) { avail -= 8; fl = TextFormatFlags.SingleLine; }
                Size need = TextRenderer.MeasureText(c.Text, c.Font, new Size(Math.Max(1, avail), int.MaxValue), fl);
                Check(need.Width <= avail + 1 && need.Height <= c.ClientSize.Height + 1,
                    name + ": «" + c.Text.Replace("\r\n", " ") + "» no cabe (" + c.GetType().Name + " " + c.ClientSize.Width + "x" + c.ClientSize.Height + ", necesita " + need.Width + "x" + need.Height + ")");
            }
        }

        static void CheckForm(Func<Form> make, string name, float k)
        {
            using (Form f = Scaled(make, k))
            {
                string n = name + " al " + (int)(k * 100) + " %";
                CheckNoOverlap(f, n);
                CheckTextFits(f, n);
                foreach (Control c in f.Controls)
                {
                    TabControl tc = c as TabControl;
                    if (tc == null) continue;
                    foreach (TabPage p in tc.TabPages) CheckTextFits(p, n + " / " + p.Text);
                }
                FileInfoForm fi = f as FileInfoForm; if (fi != null) fi.Cancel();
            }
        }

        static void T_DpiDialogs()
        {
            try
            {
                foreach (float k in Scales)
                {
                    AppConfig c = Cfg();
                    DownloadManager m = new DownloadManager(c);
                    DownloadItem done = new DownloadItem(); done.FilePath = Path.Combine(Dl, "informe anual.pdf"); done.Url = "http://x/informe.pdf"; done.Size = 123456; done.Status = DownloadStatus.Complete;
                    CheckForm(delegate { return new AddUrlForm("http://x/y.zip"); }, "Agregar URL", k);
                    CheckForm(delegate { AddOptions o = new AddOptions(); o.Url = "http://x/y.zip"; o.Pending = true; return new FileInfoForm(c, m, m.Add(o)); }, "Información del archivo", k);
                    CheckForm(delegate { return new CompleteForm(done); }, "Descarga completada", k);
                    CheckForm(delegate { return new PropertiesForm(c, m, done); }, "Propiedades", k);
                    CheckForm(delegate { return new ColumnsForm(c.Columns); }, "Columnas", k);
                    CheckForm(delegate { return new DuplicateForm(done); }, "Enlace duplicado", k);
                    CheckForm(delegate { return new QueuePickForm(); }, "Descargar más tarde", k);
                    CheckForm(delegate { return new RefreshWaitForm(done); }, "Actualizar dirección", k);
                    CheckForm(delegate { return new ShutdownForm("IDMClone ha terminado la descarga. El equipo se apagará.", 30); }, "Apagar", k);
                    CheckForm(delegate { return new SpeedLimiterForm(c); }, "Limitador", k);
                    CheckForm(delegate { return new DeleteConfirmForm(3); }, "Eliminar", k);
                    CheckForm(delegate { return new SchedulerForm(new Schedule(), null); }, "Planificador", k);
                    CheckForm(delegate { return new OptionsForm(c); }, "Opciones", k);
                    CheckForm(delegate { return new ProgressForm(done, m); }, "Progreso", k);
                    CheckForm(delegate { return new SchedulerForm(m, QueueDef.MainId); }, "Planificador con colas", k);
                    CheckForm(delegate { return new BatchForm(null); }, "Descarga por lotes", k);
                    CheckForm(delegate { List<LinkEntry> l = new List<LinkEntry>(); l.Add(new LinkEntry("http://x/a.zip", "A")); return new LinkListForm(c, m, l, null); }, "Lista de enlaces", k);
                    CheckForm(delegate { return new QueuePickForm(m.Queues); }, "Elegir cola", k);
                }
            }
            finally { Dpi.Override = 0; }
        }

        static void T_DpiProgress()
        {
            try
            {
                DownloadItem it = new DownloadItem(); it.FilePath = Path.Combine(Dl, "x.bin"); it.Url = "http://x/x.bin"; it.Size = 1000;
                using (ProgressForm f = (ProgressForm)Scaled(delegate { return new ProgressForm(it, NewMgr()); }, 1.5f))
                {
                    Control details = Field<Control>(f, "_details"), panel = Field<Control>(f, "_detailPanel");
                    f.SetExpanded(false);
                    Check(details.Bottom <= f.ClientSize.Height && f.ClientSize.Height < panel.Top + 20, "Sin detalles: alto " + f.ClientSize.Height + ", botones hasta " + details.Bottom);
                    f.SetExpanded(true);
                    Check(panel.Bottom <= f.ClientSize.Height + 1, "Con detalles la tabla queda cortada: alto " + f.ClientSize.Height + ", tabla hasta " + panel.Bottom);
                    ListView conns = Field<ListView>(f, "_conns");
                    foreach (ColumnHeader h in conns.Columns)
                        Check(TextRenderer.MeasureText(h.Text, conns.Font).Width + 8 <= h.Width, "Columna «" + h.Text + "» cortada (" + h.Width + " px)");
                }
            }
            finally { Dpi.Override = 0; }
        }

        static void T_DpiMain()
        {
            UseFreshHome("dpi-main");
            DownloadManager m = NewMgr();
            m.Schedule = new Schedule();
            Dpi.Override = 1.5f;
            MainForm f = null;
            try
            {
                f = new MainForm(m, m.Config, m.Schedule);
                SimulateDpi(f, 1.5f);
                f.StartHidden = true;
                IntPtr h = f.Handle;
                f.PerformLayout();
                ListView list = Field<ListView>(f, "_list");
                // Anchos de IDM (algunos títulos salen cortados, como en IDM), escalados con la pantalla.
                foreach (ColumnHeader c in list.Columns)
                {
                    int want = Dpi.Px(ListColumns.Parse(m.Config.Columns).Find(delegate(KeyValuePair<ColumnDef, int> kv) { return kv.Key == c.Tag; }).Value);
                    Check(Math.Abs(c.Width - want) <= 1, "Columna «" + c.Text + "»: " + c.Width + " px (deberían ser " + want + ")");
                }
                Check(TextRenderer.MeasureText(list.Columns[0].Text, list.Font).Width + 10 <= list.Columns[0].Width, "Columna «" + list.Columns[0].Text + "» cortada");
                ToolStrip bar = Field<ToolStrip>(f, "_toolbar");
                Check(bar.ImageScalingSize.Width >= 48, "Iconos de la barra: " + bar.ImageScalingSize.Width + " px (deberían ser 48 al 150 %)");
                TreeView tree = Field<TreeView>(f, "_tree");
                Check(tree.ItemHeight >= tree.Font.Height + 4, "Filas del árbol demasiado bajas: " + tree.ItemHeight + " para letra de " + tree.Font.Height);
                Check(tree.ImageList.ImageSize.Width >= 24, "Iconos del árbol: " + tree.ImageList.ImageSize.Width + " px");
                Check(list.SmallImageList.ImageSize.Width >= 24, "Iconos de la lista: " + list.SmallImageList.ImageSize.Width + " px");
                // Lo guardado no depende de la escala: al 150 % un ancho de 300 px se guarda como 200.
                list.Columns[0].Width = 300;
                typeof(MainForm).GetMethod("SaveLayout", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(f, null);
                Check(m.Config.Columns.StartsWith("name:200,"), "Anchos guardados: " + m.Config.Columns);
            }
            finally
            {
                Dpi.Override = 0;
                if (f != null) DisposeMainForm(f);
            }
        }
    }
}
