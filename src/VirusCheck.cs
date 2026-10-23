using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace IDMClone
{
    // Comprobación de virus al terminar una descarga (IDM: Opciones > Descargas > "Configuración de la
    // comprobación de virus"). Se ejecuta el antivirus elegido con sus parámetros; "[File]" se sustituye
    // por la ruta del archivo descargado y, si no hay parámetros, la ruta se añade al final.
    public static class VirusCheck
    {
        // Parámetros que se ponen solos al elegir un antivirus conocido (como hace IDM).
        static readonly string[,] Known =
        {
            { "mpcmdrun.exe", "-Scan -ScanType 3 -File \"[File]\"" },     // Windows Defender / Microsoft Defender
            { "avp.com",      "SCAN \"[File]\"" },                         // Kaspersky
            { "avp.exe",      "SCAN \"[File]\"" },
            { "ecls.exe",     "\"[File]\"" },                              // ESET
            { "avgscanx.exe", "/SCAN=\"[File]\"" },                        // AVG
            { "avgscana.exe", "/SCAN=\"[File]\"" },
            { "ashcmd.exe",   "\"[File]\"" },                              // avast!
            { "clamscan.exe", "\"[File]\"" },                              // ClamAV
            { "sav32cli.exe", "\"[File]\"" },                              // Sophos
        };

        public const int DefenderThreatFound = 2;  // código de salida de MpCmdRun.exe si encuentra una amenaza

        public static string KnownArgs(string exePath)
        {
            string n = Path.GetFileName(exePath ?? "").ToLowerInvariant();
            for (int i = 0; i < Known.GetLength(0); i++) if (Known[i, 0] == n) return Known[i, 1];
            return null;
        }

        public static bool IsDefender(string exePath) { return string.Equals(Path.GetFileName(exePath ?? ""), "MpCmdRun.exe", StringComparison.OrdinalIgnoreCase); }

        // Ruta del analizador de línea de comandos de Microsoft Defender, si está.
        public static string DefenderPath()
        {
            try
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string p = Path.Combine(pf, @"Windows Defender\MpCmdRun.exe");
                if (File.Exists(p)) return p;
                // En equipos de 64 bits un proceso de 32 bits ve "Program Files (x86)".
                string w = Environment.GetEnvironmentVariable("ProgramW6432");
                if (!string.IsNullOrEmpty(w)) { p = Path.Combine(w, @"Windows Defender\MpCmdRun.exe"); if (File.Exists(p)) return p; }
            }
            catch { }
            return null;
        }

        // Línea de parámetros para un archivo concreto.
        public static string BuildArgs(string args, string file)
        {
            if (string.IsNullOrEmpty(args) || args.Trim().Length == 0) return "\"" + file + "\"";
            // "[File]" ya entre comillas: solo la ruta; sin comillas: se añaden (las rutas llevan espacios).
            // Una sola pasada sobre los parámetros: la ruta puede contener "[File]" y no se vuelve a sustituir.
            const string quoted = "\"[File]\"", bare = "[File]";
            string path = "\"" + file + "\"";
            StringBuilder sb = new StringBuilder();
            int i = 0;
            while (i < args.Length)
            {
                if (string.Compare(args, i, quoted, 0, quoted.Length, StringComparison.OrdinalIgnoreCase) == 0) { sb.Append(path); i += quoted.Length; }
                else if (string.Compare(args, i, bare, 0, bare.Length, StringComparison.OrdinalIgnoreCase) == 0) { sb.Append(path); i += bare.Length; }
                else sb.Append(args[i++]);
            }
            return sb.ToString();
        }

        // Para las pruebas: sustituye el arranque del proceso.
        public static Func<ProcessStartInfo, Process> Starter = delegate(ProcessStartInfo psi) { return Process.Start(psi); };

        // Analiza el archivo en segundo plano. done(código de salida) al terminar (-1 si no se pudo ejecutar).
        public static bool Scan(AppConfig cfg, string file, Action<int> done)
        {
            string exe = cfg.AntivirusPath;
            if (string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(file) || !File.Exists(file)) return false;
            ProcessStartInfo psi = new ProcessStartInfo(exe, BuildArgs(cfg.AntivirusArgs, file));
            psi.UseShellExecute = false;
            psi.WorkingDirectory = Path.GetDirectoryName(file);
            // Los analizadores de consola (MpCmdRun, ecls...) sin ventana negra; los de ventana, normales.
            psi.CreateNoWindow = IsConsoleApp(exe);
            ThreadPool.QueueUserWorkItem(delegate
            {
                int code = -1;
                try
                {
                    using (Process p = Starter(psi))
                    {
                        if (p != null && p.WaitForExit(30 * 60 * 1000)) code = p.ExitCode;
                    }
                }
                catch { }
                try { if (done != null) done(code); } catch { }
            });
            return true;
        }

        // ¿Programa de consola? (subsistema del encabezado PE = 3).
        public static bool IsConsoleApp(string exe)
        {
            try
            {
                using (FileStream fs = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (BinaryReader r = new BinaryReader(fs))
                {
                    if (fs.Length < 0x40 || r.ReadUInt16() != 0x5A4D) return false;   // "MZ"
                    fs.Position = 0x3C;
                    int pe = r.ReadInt32();
                    if (pe <= 0 || pe + 0x5E > fs.Length) return false;
                    fs.Position = pe;
                    if (r.ReadUInt32() != 0x00004550) return false;                   // "PE\0\0"
                    fs.Position = pe + 24 + 68;                                        // OptionalHeader.Subsystem (igual en PE32 y PE32+)
                    return r.ReadUInt16() == 3;
                }
            }
            catch { return false; }
        }
    }
}

namespace IDMClone
{
    using System.Drawing;
    using System.Windows.Forms;

    // Ventana "Comprobación de virus" (IDM: Opciones > Descargas > Editar...).
    public class VirusCheckForm : DialogBase
    {
        TextBox _exe, _args;
        public string ExePath { get { return _exe.Text.Trim(); } }
        public string Args { get { return _args.Text.Trim(); } }

        // Como IDM ("Análisis de virus", captura 34): programa y parámetros arriba, la explicación debajo y
        // Aceptar/Cancelar al pie. Además, un botón para elegir Microsoft Defender si está instalado.
        public VirusCheckForm(string exe, string args)
        {
            Text = "Análisis de virus";
            ClientSize = new Size(556, 388);
            OptUi.Lbl(this, "Programa de escaneo de virus", 12, 10, 440);
            _exe = OptUi.Box(this, exe, 12, 28, 446);
            Button browse = OptUi.Btn(this, "Examinar", 470, 27);
            OptUi.Lbl(this, "Parámetros de la línea de comandos", 12, 56, 440);
            _args = OptUi.Box(this, args, 12, 74, 446);
            OptUi.Lbl(this, "Si quieres ejecutar el antivirus al completar las descargas, escribe o busca la ruta completa de su ejecutable. Si IDMClone conoce los parámetros de ese programa (Microsoft Defender, Kaspersky, ESET, AVG, avast!, ClamAV, Sophos), los pone automáticamente; si no, escríbelos según la documentación del antivirus. El archivo que se analiza se escribe como\r\n[File]\r\n(con los corchetes). Si no pones ningún parámetro, IDMClone ejecuta el antivirus con la ruta completa del archivo descargado al final.", 12, 104, 532, 120);
            OptUi.Lbl(this, "Ten en cuenta que casi todos los antivirus actuales tienen \"protección en tiempo real\", que ya comprueba todos los archivos que se escriben en el equipo, incluidos los descargados con IDMClone. Por eso, solo hace falta configurar aquí el análisis si la protección en tiempo real está desactivada. También puede ocurrir que la protección en tiempo real haga más lento el final de las descargas grandes; en ese caso, añade IDMClone.exe a las excepciones del antivirus y configura el análisis en este diálogo.", 12, 230, 532, 100);
            Button defender = OptUi.Btn(this, "Microsoft Defender", 12, 350, 130);
            defender.Enabled = VirusCheck.DefenderPath() != null;
            Button ok = OptUi.Btn(this, "Aceptar", 170, 350); ok.DialogResult = DialogResult.OK;
            Button cancel = OptUi.Btn(this, "Cancelar", 334, 350); cancel.DialogResult = DialogResult.Cancel;
            AcceptButton = ok; CancelButton = cancel;

            browse.Click += delegate
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Filter = "Programas (*.exe;*.com)|*.exe;*.com|Todos los archivos (*.*)|*.*";
                    try
                    {
                        string start = File.Exists(_exe.Text) ? Path.GetDirectoryName(_exe.Text) : Path.GetDirectoryName(VirusCheck.DefenderPath() ?? "");
                        if (!string.IsNullOrEmpty(start)) d.InitialDirectory = start;
                    }
                    catch { }
                    if (d.ShowDialog(this) == DialogResult.OK) SetExe(d.FileName);
                }
            };
            defender.Click += delegate { SetExe(VirusCheck.DefenderPath()); };
            EndLayout();
        }

        // Como IDM: al elegir un antivirus que conoce, pone sus parámetros.
        public void SetExe(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            _exe.Text = path;
            string k = VirusCheck.KnownArgs(path);
            if (k != null) _args.Text = k;
        }
    }
}
