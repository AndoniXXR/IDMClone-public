using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace IDMClone.Tests
{
    // Fase 6: comprobación de virus al terminar y estilos/personalización de la barra de herramientas.
    public static partial class TestMain
    {
        static void RunPhase6()
        {
            Run("antivirus: parámetros de los antivirus conocidos y [File] con la ruta entre comillas", T6_AvArgs);
            Run("antivirus: se detecta si el analizador es de consola (sin ventana negra)", T6_AvConsole);
            Run("antivirus: al terminar una descarga se analiza el archivo con el programa elegido", T6_AvOnComplete);
            Run("antivirus: Opciones > Descargas > Editar... guarda el programa y sus parámetros", T6_AvOptions);
            Run("barra: lista de botones guardada (orden, separadores, repetidos y desconocidos)", T6_ToolbarParse);
            Run("barra: los cuatro estilos de IDM (3D y neón, grandes y pequeños) desde el menú", T6_ToolbarStyles);
            Run("barra: Personalizar... quita, añade y ordena botones", T6_ToolbarCustomize);
            RunIconsB(); // iconos Neon/3D, árbol y Personalizar como IDM (TestsIconosB.cs)
        }

        static void T6_AvArgs()
        {
            Check(VirusCheck.KnownArgs(@"C:\Program Files\Windows Defender\MpCmdRun.exe") == "-Scan -ScanType 3 -File \"[File]\"", "Defender");
            Check(VirusCheck.KnownArgs(@"C:\ESET\ecls.exe") == "\"[File]\"", "ESET");
            Check(VirusCheck.KnownArgs(@"C:\x\desconocido.exe") == null, "Desconocido");
            string f = @"C:\Mis descargas\setup 1.exe";
            Check(VirusCheck.BuildArgs("", f) == "\"" + f + "\"", "Sin parámetros: la ruta al final");
            Check(VirusCheck.BuildArgs("-Scan -File \"[File]\"", f) == "-Scan -File \"" + f + "\"", "Con comillas: " + VirusCheck.BuildArgs("-Scan -File \"[File]\"", f));
            Check(VirusCheck.BuildArgs("/SCAN=[file] /q", f) == "/SCAN=\"" + f + "\" /q", "Sin comillas y en minúsculas: " + VirusCheck.BuildArgs("/SCAN=[file] /q", f));
        }

        static void T6_AvConsole()
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            Check(VirusCheck.IsConsoleApp(Path.Combine(sys, "cmd.exe")), "cmd.exe es de consola");
            Check(!VirusCheck.IsConsoleApp(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")), "explorer.exe no es de consola");
            Check(!VirusCheck.IsConsoleApp(Path.Combine(Dl, "no-existe.exe")), "Un archivo que no existe no es de consola");
        }

        static void T6_AvOnComplete()
        {
            Func<ProcessStartInfo, Process> saved = VirusCheck.Starter;
            ProcessStartInfo got = null;
            VirusCheck.Starter = delegate(ProcessStartInfo psi) { got = psi; return null; };
            MainForm f = NewMainForm();
            try
            {
                DownloadManager m = Field<DownloadManager>(f, "_mgr");
                m.Config.ShowCompleteDialog = false;
                m.Config.AntivirusPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                m.Config.AntivirusArgs = "/c rem [File]";
                DownloadItem it = m.Add(S.Url("range", "analizar.exe", 300 * 1024, NewId("av")), Dl, null, true);
                Check(PumpUntil(delegate { return got != null; }, 30), "No se lanzó el antivirus (estado " + it.Status + ")");
                Check(got.FileName == m.Config.AntivirusPath, "Programa: " + got.FileName);
                Check(got.Arguments == "/c rem \"" + it.FilePath + "\"", "Parámetros: " + got.Arguments);
                Check(got.CreateNoWindow, "cmd.exe es de consola: debía ir sin ventana");
            }
            finally { VirusCheck.Starter = saved; DisposeMainForm(f); }
        }

        static void T6_AvOptions()
        {
            UseFreshHome("av-opts");
            AppConfig c = Cfg();
            using (VirusCheckForm v = new VirusCheckForm(null, null))
            {
                IntPtr h = v.Handle;
                v.SetExe(@"C:\Program Files\Windows Defender\MpCmdRun.exe");
                Check(v.Args == "-Scan -ScanType 3 -File \"[File]\"", "Defender no rellenó sus parámetros: " + v.Args);
            }
            using (OptionsForm o = new OptionsForm(c))
            {
                IntPtr h = o.Handle;
                typeof(OptionsForm).GetField("_avPath", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(o, @"C:\av\ecls.exe");
                typeof(OptionsForm).GetField("_avArgs", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(o, "\"[File]\"");
                typeof(OptionsForm).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(o, null);
            }
            Check(c.AntivirusPath == @"C:\av\ecls.exe" && c.AntivirusArgs == "\"[File]\"", "No se guardó: " + c.AntivirusPath + " " + c.AntivirusArgs);
        }

        static void T6_ToolbarParse()
        {
            Check(string.Join(",", ToolbarCatalog.Parse(null).ToArray()) == ToolbarCatalog.Default, "Por defecto");
            string p = string.Join(",", ToolbarCatalog.Parse("|,grabber,|,|,add,add,zzz,|").ToArray());
            Check(p == "grabber,|,add", "Limpieza: " + p);
            Check(string.Join(",", ToolbarCatalog.Parse("|,|").ToArray()) == ToolbarCatalog.Default, "Vacía -> la de siempre");
        }

        static void T6_ToolbarStyles()
        {
            MainForm f = NewMainForm();
            try
            {
                ToolStrip tb = Field<ToolStrip>(f, "_toolbar");
                ToolStripItem[] items = (ToolStripItem[])typeof(MainForm).GetMethod("ToolbarMenuItems", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(f, null);
                List<string> texts = new List<string>();
                foreach (ToolStripItem i in items) texts.Add(i is ToolStripSeparator ? "-" : i.Text);
                Check(string.Join("|", texts.ToArray()) == "Personalizar...|-|Botones pequeños|Botones grandes|-|Neon|3D Style", "Menú: " + string.Join("|", texts.ToArray())); // como IDM (30a)
                Check(((ToolStripMenuItem)items[3]).Checked && !((ToolStripMenuItem)items[2]).Checked, "Por defecto: botones grandes");
                Check(tb.ContextMenuStrip != null, "La barra no tiene menú con clic derecho");

                Bitmap big3d = (Bitmap)tb.Items["tb_add"].Image;
                ((ToolStripMenuItem)items[5]).PerformClick(); ((ToolStripMenuItem)items[2]).PerformClick(); // Neon y Botones pequeños
                AppConfig c = Field<AppConfig>(f, "_cfg");
                Check(c.ToolbarStyle == "neon" && !c.ToolbarLarge, "Config: " + c.ToolbarStyle + " " + c.ToolbarLarge);
                Bitmap neon = (Bitmap)tb.Items["tb_add"].Image;
                Check(neon.Width == Dpi.Px(ToolbarCatalog.SmallIcon) && neon.Width < big3d.Width, "Tamaño del icono: " + neon.Width); // IDM: pequeños de ~22-24 px de dibujo
                Check(Opaque(Glyphs.Get("add", 32, "neon")) * 2 < Opaque(Glyphs.Get("add", 32, "3d")), "El neón debía ser de trazo fino, sin relleno");
                Check(AppConfig.Load().ToolbarStyle == "neon", "No se guardó el estilo");
            }
            finally { DisposeMainForm(f); }
        }

        static int Opaque(Bitmap b)
        {
            int n = 0;
            for (int y = 0; y < b.Height; y++) for (int x = 0; x < b.Width; x++) if (b.GetPixel(x, y).A > 20) n++;
            return n;
        }

        static void T6_ToolbarCustomize()
        {
            MainForm f = NewMainForm();
            try
            {
                List<string> cur = ToolbarCatalog.Parse(null);
                List<string> result;
                using (ToolbarCustomizeForm d = new ToolbarCustomizeForm(cur, "3d"))
                {
                    IntPtr h = d.Handle;
                    // Como IDM: Grabber no está en la barra por defecto, pero se puede añadir.
                    Check(d.Available.Items.Contains("grabber") && d.Available.Items.Contains("|") && !d.Current.Items.Contains("grabber"), "Disponibles al abrir");
                    d.Current.SelectedItem = "scheduler"; d.RemoveSelected();
                    Check(d.Available.Items.Contains("scheduler"), "Planificador no volvió a disponibles");
                    d.Current.SelectedIndex = 0; d.MoveSelected(1);                  // Reanudar antes que Agregar URL
                    d.Available.SelectedItem = "grabber"; d.Current.SelectedIndex = 1; d.AddSelected(); // Grabber delante del segundo
                    d.Current.SelectedIndex = d.Current.Items.Count - 1; d.RemoveSelected(); d.MoveSelected(-1); // el hueco del final no se mueve
                    result = d.Collect();
                }
                string r = string.Join(",", result.ToArray());
                Check(r.StartsWith("resume,grabber,add,stop,") && r.IndexOf("scheduler") < 0 && r.EndsWith(",stopqueue"), "Resultado: " + r);
                AppConfig c = Field<AppConfig>(f, "_cfg");
                c.ToolbarButtons = r;
                typeof(MainForm).GetMethod("FillToolbar", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(f, null);
                ToolStrip tb = Field<ToolStrip>(f, "_toolbar");
                Check(tb.Items[0].Name == "tb_resume" && tb.Items[1].Name == "tb_grabber" && tb.Items[2].Name == "tb_add", "Barra: " + tb.Items[0].Name + " " + tb.Items[1].Name + " " + tb.Items[2].Name);
            }
            finally { DisposeMainForm(f); }
        }
    }
}
