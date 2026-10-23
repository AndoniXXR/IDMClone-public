using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IDMClone
{
    // "Iniciar IDMClone con Windows": como IDM, un valor en HKCU\...\Run que arranca
    // el programa con /onboot (minimizado en la bandeja, sin ventana).
    public static class Startup
    {
        public const string ValueName = "IDMClone";
        // Los tests lo cambian por una clave propia para no tocar el arranque real.
        public static string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static string Command(string exePath) { return "\"" + exePath + "\" /onboot"; }

        public static bool IsEnabled(string exePath)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    string v = k != null ? k.GetValue(ValueName) as string : null;
                    return v != null && v.IndexOf(exePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        public static void Set(bool enable, string exePath)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (enable) k.SetValue(ValueName, Command(exePath));
                else
                {
                    // Solo si arranca este IDMClone.exe: el de otra copia (la instalada, si este es el de pruebas o
                    // de desarrollo) se respeta, igual que hace el desinstalador.
                    string v = k.GetValue(ValueName) as string;
                    if (v != null && v.IndexOf(exePath, StringComparison.OrdinalIgnoreCase) >= 0) k.DeleteValue(ValueName, false);
                }
            }
        }
    }

    // Icono de la aplicación. app.ico va incrustado dos veces: como icono Win32 del .exe
    // (/win32icon, el que ve el Explorador) y como recurso (/resource) para cargar cada tamaño nítido.
    public static class AppIcon
    {
        static Icon _main, _small;

        static Icon Load(Size size)
        {
            try
            {
                using (System.IO.Stream s = typeof(AppIcon).Assembly.GetManifestResourceStream("IDMClone.app.ico"))
                    if (s != null) return new Icon(s, size);
            }
            catch { }
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { return SystemIcons.Application; }
        }

        public static Icon Main { get { if (_main == null) _main = Load(SystemInformation.IconSize); return _main; } }
        public static Icon Small { get { if (_small == null) _small = Load(SystemInformation.SmallIconSize); return _small; } }
        // El tamaño más parecido de app.ico (para dibujarlo grande, p. ej. en el instalador).
        public static Icon Sized(int px) { return Load(new Size(px, px)); }
    }
}
