using System;
using System.Collections.Generic;
using System.Drawing;

namespace IDMClone
{
    // Aspecto común, medido sobre IDM 6.43 (capturas\idm\INDICE.md): fuente de la lista y los diálogos,
    // y los colores que IDM no toma del sistema. Todas las ventanas lo usan desde aquí.
    public static class Theme
    {
        // IDM: Tahoma 8 pt en la lista y en los diálogos (Ver > Fuente lo confirma).
        // Tahoma no tiene ideogramas: Windows los toma de otras fuentes ("font linking", FontLink\SystemLink).
        // Con el japonés eso da MS UI Gothic, igual que en un Windows japonés, y se deja así. Con el chino, en
        // un Windows que no es chino, los ideogramas salen mezclados (los que tiene MS UI Gothic con forma
        // japonesa, el resto de SimSun): se usa Microsoft YaHei UI, con latín y chino y las medidas de Tahoma 8.
        // Las ventanas toman la letra al crearse; la principal la cambia al cambiar de idioma.
        public static Font UiFont { get { Pick(); return _ui; } }
        public static Font UiFontBold { get { Pick(); return _uiBold; } }
        public static string UiFamily { get { Pick(); return _family; } }

        static Font _ui, _uiBold;
        static string _family;
        static readonly Dictionary<string, bool> _installed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        static void Pick()
        {
            string family = FamilyFor(Lang.CurrentCode);
            if (_ui != null && family == _family) return;
            // Las letras anteriores no se liberan: las siguen usando las ventanas que ya estaban abiertas.
            _family = family;
            _ui = new Font(family, 8.25f, FontStyle.Regular, GraphicsUnit.Point);
            _uiBold = new Font(family, 8.25f, FontStyle.Bold, GraphicsUnit.Point);
        }

        public static string FamilyFor(string langCode)
        {
            if (langCode == "zh" || langCode == "zh-cn" || langCode == "zh-hans") // chino simplificado
                foreach (string f in new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimSun" })
                    if (Installed(f)) return f;
            return "Tahoma";
        }

        static bool Installed(string family)
        {
            bool ok;
            if (_installed.TryGetValue(family, out ok)) return ok;
            try { using (FontFamily f = new FontFamily(family)) ok = true; }
            catch (ArgumentException) { ok = false; } // no está instalada
            _installed[family] = ok;
            return ok;
        }

        public static readonly Color DialogBack = Color.FromArgb(0xF0, 0xF0, 0xF0);
        public static readonly Color Selection = Color.FromArgb(0x00, 0x78, 0xD7);       // fila seleccionada (texto blanco)
        public static readonly Color GridLine = Color.FromArgb(0xF0, 0xF0, 0xF0);        // cuadrícula de la lista
        public static readonly Color StatusBlue = Color.FromArgb(0x1E, 0x50, 0xC8);      // "Recibiendo datos...", "Pausar"
        public static readonly Color HelpBlue = Color.FromArgb(0x00, 0x00, 0xFF);        // textos de ayuda en azul

        // Ventana de progreso.
        public static readonly Color ProgressGreen = Color.FromArgb(0x06, 0xB0, 0x25);   // barra de progreso total
        public static readonly Color SegmentDone = Color.FromArgb(0x16, 0x4E, 0xBB);     // bloques descargados
        public static readonly Color SegmentBack = Color.FromArgb(0xE6, 0xEE, 0xFB);     // fondo azul muy claro
        public static readonly Color SegmentMark = Color.FromArgb(0xE0, 0x20, 0x20);     // marcas rojas (inicio de conexión)
        public static readonly Color ConnRow = Color.FromArgb(0xBF, 0xB3, 0xBD);         // filas de la tabla de conexiones
    }
}
