using System;
using System.Collections.Generic;
using System.IO;

namespace IDMClone
{
    // Opciones de las ventanas de descarga (Agregar URL, Información del archivo, Limitador de velocidad).
    // Las configuraciones antiguas no las tienen: quedan a null/false y todo funciona igual.
    public partial class AppConfig
    {
        public const int HistoryMax = 20;

        // "Ingresar nueva dirección a descargar": últimas direcciones (el combo de IDM).
        public List<string> AddUrlHistory { get; set; }

        // "Información del archivo a descargar": últimas carpetas de "Guardar como" (el combo de IDM).
        public List<string> SaveAsHistory { get; set; }

        // "Configurar Limitador de Velocidad": "Activar siempre al iniciar el Limitador de Velocidad".
        public bool SpeedLimiterOnAtStart { get; set; }

        public void RememberUrl(string url)
        {
            AddUrlHistory = Push(AddUrlHistory, url, StringComparison.Ordinal);
        }

        public void RememberSaveFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            SaveAsHistory = Push(SaveAsHistory, folder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }

        // El más reciente primero, sin repetidos ni vacíos, como mucho HistoryMax.
        static List<string> Push(List<string> list, string value, StringComparison cmp)
        {
            List<string> r = new List<string>();
            if (!string.IsNullOrEmpty(value)) r.Add(value.Trim());
            if (list != null)
                foreach (string s in list)
                {
                    if (string.IsNullOrEmpty(s) || r.Count >= HistoryMax) continue;
                    bool dup = false;
                    foreach (string x in r) if (string.Equals(x, s, cmp)) { dup = true; break; }
                    if (!dup) r.Add(s);
                }
            return r;
        }
    }
}
