using System;
using System.Drawing;
using System.Globalization;
using System.Web.Script.Serialization;

namespace IDMClone
{
    // Opciones de la ventana principal que IDM guarda aparte (Ver > Fuente, Ver > Bandeja de iconos,
    // "No mostrar este diálogo otra vez" al eliminar descargas). Las configuraciones antiguas cargan igual:
    // si faltan, valen lo de IDM.
    public partial class AppConfig
    {
        // Ver > Fuente > Elegir Fuente...: "Tahoma;8.25;0" (nombre;puntos;estilo). null = la de IDM (Tahoma 8).
        public string ListFont { get; set; }
        // Ver > Bandeja de iconos: null o "3d" (Estilo 3D), "classic" (Estilo Clásico), "none" (No mostrar).
        public string TrayIconStyle { get; set; }
        // "Confirmar eliminación de descargas" > "No mostrar este diálogo otra vez".
        public bool SkipDeleteConfirm { get; set; }

        [ScriptIgnore] public string TrayIconStyleOrDefault
        {
            get { return TrayIconStyle == "classic" || TrayIconStyle == "none" ? TrayIconStyle : "3d"; }
        }

        // La fuente elegida, o null si es la de siempre (o no se puede crear).
        public Font CreateListFont()
        {
            if (string.IsNullOrEmpty(ListFont)) return null;
            try
            {
                string[] p = ListFont.Split(';');
                float size;
                int style = 0;
                if (p.Length < 2 || p[0].Trim().Length == 0 || !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out size)) return null;
                if (p.Length > 2) int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out style);
                if (size < 6 || size > 36) return null;
                return new Font(p[0].Trim(), size, (FontStyle)(style & 0xF), GraphicsUnit.Point);
            }
            catch { return null; }
        }

        public static string FormatFont(Font f)
        {
            if (f == null) return null;
            return f.Name + ";" + f.SizeInPoints.ToString("0.##", CultureInfo.InvariantCulture) + ";" + ((int)f.Style).ToString(CultureInfo.InvariantCulture);
        }
    }
}
