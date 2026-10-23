using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;

namespace IDMClone
{
    // Columnas de la lista de descargas (como IDM: Ver > Personalizar lista de URL... y Ver > Organizar archivos).
    public class ColumnDef
    {
        public string Id, Title, SortTitle;
        public int Width;
        public HorizontalAlignment Align;
        public Func<DownloadItem, string> Text;
        public Comparison<DownloadItem> Compare;
    }

    public static class ListColumns
    {
        static readonly CultureInfo Es = new CultureInfo("es-ES");

        // Columnas visibles de IDM 6.43 y sus anchos (píxeles a escala 100 %), medidos en sus capturas.
        public const string IdmDefault = "name:150,q:22,size:75,status:65,eta:75,speed:95,lasttry:65,desc:200";

        // Mismo orden que la ventana "Columnas" de IDM (las ocultas al final); después, las propias de IDMClone.
        // Todo alineado a la izquierda, como en IDM.
        public static readonly ColumnDef[] All = new ColumnDef[]
        {
            Col("name", "Nombre de Archivo", "Por nombre de archivo", 150,
                delegate(DownloadItem it) { return it.FileName; },
                delegate(DownloadItem a, DownloadItem b) { return string.Compare(a.FileName, b.FileName, true, Es); }),
            // "Q": en la lista se ve el icono de la cola (lo pone la ventana principal), sin texto.
            Col("q", "Q", null, 22,
                delegate(DownloadItem it) { return ""; },
                delegate(DownloadItem a, DownloadItem b) { return QueueKey(a).CompareTo(QueueKey(b)); }),
            Col("size", "Tamaño", "Por tamaño", 75,
                delegate(DownloadItem it) { return it.Size >= 0 ? Size(it.Size) : ""; },
                delegate(DownloadItem a, DownloadItem b) { return a.Size.CompareTo(b.Size); }),
            Col("status", "Estado", "Por estado", 65,
                StatusText,
                delegate(DownloadItem a, DownloadItem b) { return Progress(a).CompareTo(Progress(b)); }),
            Col("eta", "Tiempo restante", "Por tiempo restante", 75,
                delegate(DownloadItem it) { return Running(it) && it.SpeedBps > 0 ? Fmt.Eta(it.Remaining, it.SpeedBps) : ""; },
                delegate(DownloadItem a, DownloadItem b) { return EtaSeconds(a).CompareTo(EtaSeconds(b)); }),
            Col("speed", "Velocidad de transferencia", "Por velocidad de transferencia", 95,
                delegate(DownloadItem it) { return Running(it) ? Speed(it.SpeedBps) : ""; },
                delegate(DownloadItem a, DownloadItem b) { return (Running(a) ? a.SpeedBps : -1).CompareTo(Running(b) ? b.SpeedBps : -1); }),
            Col("lasttry", "Última fecha de intento", "Por fecha de último intento", 65,
                delegate(DownloadItem it) { return ListDate(it.LastTry); },
                delegate(DownloadItem a, DownloadItem b) { return string.CompareOrdinal(a.LastTry ?? "", b.LastTry ?? ""); }),
            Col("desc", "Descripción", "Por descripción", 200,
                delegate(DownloadItem it) { return it.Description ?? ""; },
                delegate(DownloadItem a, DownloadItem b) { return string.Compare(a.Description ?? "", b.Description ?? "", true, Es); }),
            Col("added", "Fecha de adición", "Por orden de agregado", 120,
                delegate(DownloadItem it) { return ListDate(it.Added); },
                delegate(DownloadItem a, DownloadItem b) { int c = string.CompareOrdinal(a.Added ?? "", b.Added ?? ""); return c != 0 ? c : a.QueueOrder.CompareTo(b.QueueOrder); }),
            Col("saveto", "Guardar en", "Por ruta de guardado", 180,
                delegate(DownloadItem it) { return SafeDir(it.FilePath); },
                delegate(DownloadItem a, DownloadItem b) { return string.Compare(a.FilePath, b.FilePath, true, Es); }),
            Col("referer", "Referido", "Por referido", 160,
                delegate(DownloadItem it) { return it.Referrer ?? ""; },
                delegate(DownloadItem a, DownloadItem b) { return string.CompareOrdinal(a.Referrer ?? "", b.Referrer ?? ""); }),
            // La página de la que salió el enlace: IDMClone la conoce por el Referer que manda el navegador.
            Col("parent", "Página Web Parental", "Por Página Web Parental", 160,
                delegate(DownloadItem it) { return ParentPage(it); },
                delegate(DownloadItem a, DownloadItem b) { return string.CompareOrdinal(ParentPage(a), ParentPage(b)); }),
            Col("url", "Dirección", null, 220,
                delegate(DownloadItem it) { return it.Url ?? ""; },
                delegate(DownloadItem a, DownloadItem b) { return string.CompareOrdinal(a.Url ?? "", b.Url ?? ""); }),
            Col("category", "Categoría", null, 90,
                delegate(DownloadItem it) { return it.Category ?? ""; },
                delegate(DownloadItem a, DownloadItem b) { return string.Compare(a.Category ?? "", b.Category ?? "", true, Es); }),
            Col("done", "Descargado", null, 75,
                delegate(DownloadItem it) { return Size(it.Downloaded); },
                delegate(DownloadItem a, DownloadItem b) { return a.Downloaded.CompareTo(b.Downloaded); }),
        };

        // Orden de Ver > Organizar archivos en IDM.
        public static readonly string[] SortOrder = { "added", "name", "size", "status", "eta", "speed", "lasttry", "desc", "saveto", "referer", "parent" };

        static ColumnDef Col(string id, string title, string sortTitle, int width, Func<DownloadItem, string> text, Comparison<DownloadItem> cmp)
        {
            ColumnDef c = new ColumnDef();
            c.Id = id; c.Title = title; c.SortTitle = sortTitle; c.Width = width; c.Align = HorizontalAlignment.Left; c.Text = text; c.Compare = cmp;
            return c;
        }

        public static ColumnDef ById(string id)
        {
            foreach (ColumnDef c in All) if (c.Id == id) return c;
            return null;
        }

        // "name:150,q:22,..." -> columnas visibles en orden, con su ancho. "Nombre" siempre va.
        // Sin configuración (o con la de antes, que nadie tocó) se ven las de IDM.
        public static List<KeyValuePair<ColumnDef, int>> Parse(string layout)
        {
            if (layout == null || layout == AppConfig.DefaultColumns || layout == OldDefault) layout = IdmDefault;
            List<KeyValuePair<ColumnDef, int>> r = new List<KeyValuePair<ColumnDef, int>>();
            HashSet<string> seen = new HashSet<string>();
            foreach (string part in layout.Split(','))
            {
                string[] kv = part.Trim().Split(':');
                ColumnDef c = ById(kv[0].Trim());
                if (c == null || !seen.Add(c.Id)) continue;
                int w;
                if (kv.Length < 2 || !int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out w) || w < 16 || w > 2000) w = c.Width;
                r.Add(new KeyValuePair<ColumnDef, int>(c, w));
            }
            if (!seen.Contains("name")) r.Insert(0, new KeyValuePair<ColumnDef, int>(ById("name"), ById("name").Width));
            return r;
        }

        // Columnas por defecto de versiones anteriores de IDMClone.
        const string OldDefault = "name:250,q:28,size:80,status:90,eta:95,speed:150,lasttry:150,desc:150";

        public static string Format(List<KeyValuePair<ColumnDef, int>> cols)
        {
            List<string> parts = new List<string>();
            foreach (KeyValuePair<ColumnDef, int> kv in cols) parts.Add(kv.Key.Id + ":" + kv.Value.ToString(CultureInfo.InvariantCulture));
            return string.Join(",", parts.ToArray());
        }

        static bool Running(DownloadItem it) { return it.Status == DownloadStatus.Downloading; }

        // Columna "Estado" como en IDM: "Completo", el porcentaje mientras no termina, "Error"...
        public static string StatusText(DownloadItem it)
        {
            switch (it.Status)
            {
                case DownloadStatus.Complete: return Lang.T("Completo");
                case DownloadStatus.Error: return string.IsNullOrEmpty(it.Error) ? Lang.T("Error") : Lang.T("Error: " + it.Error);
                case DownloadStatus.Connecting: return Lang.T("Conectando...");
            }
            long done = it.Downloaded;
            if (it.Size > 0 && (done > 0 || it.Status == DownloadStatus.Downloading))
                return string.Format(CultureInfo.InvariantCulture, "{0:0.00}%", Math.Min(100.0, 100.0 * done / it.Size));
            if (done > 0) return Size(done);
            return it.Status == DownloadStatus.Downloading ? Lang.T("Recibiendo datos...") : "";
        }

        // Tamaños como en la lista de IDM: dos decimales y dos espacios antes de la unidad ("32.00  MB").
        public static string Size(long bytes)
        {
            if (bytes < 0) return "";
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + "  Bytes";
            double b = bytes;
            string[] u = { "KB", "MB", "GB", "TB" };
            int i = -1;
            while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
            return string.Format(CultureInfo.InvariantCulture, "{0:0.00}  {1}", b, u[i]);
        }

        // Velocidad como en IDM ("1.10 MB/sec"); el mismo formato que la ventana de progreso y la bandeja.
        public static string Speed(long bps) { return Fmt.IdmSpeed(bps, 2); }

        static double Progress(DownloadItem it)
        {
            if (it.Status == DownloadStatus.Complete) return 2;
            if (it.Status == DownloadStatus.Error) return -1;
            return it.Size > 0 ? (double)it.Downloaded / it.Size : 0;
        }

        static long EtaSeconds(DownloadItem it)
        {
            if (!Running(it) || it.SpeedBps <= 0 || it.Remaining < 0) return long.MaxValue;
            return it.Remaining / it.SpeedBps;
        }

        // ¿Se ve el icono de la cola en la columna "Q"? (las completadas salen de la cola, salvo en la de sincronización)
        public static bool ShowsQueue(DownloadItem it)
        {
            if (it.Queue == DownloadItem.NoQueue) return false;
            return it.Status != DownloadStatus.Complete || it.Queue == QueueDef.SyncId;
        }

        static int QueueKey(DownloadItem it) { return ShowsQueue(it) ? it.Queue + 1 : 0; }

        static string ParentPage(DownloadItem it)
        {
            string r = it.Referrer ?? "";
            return Util.LooksLikeUrl(r) ? r : "";
        }

        // Fecha de otras ventanas (Propiedades): "22 oct 2026 16:58".
        public static string Date(string iso)
        {
            DateTime d;
            if (!TryDate(iso, out d)) return "";
            return d.ToString("dd MMM yyyy HH:mm", Es);
        }

        // Fecha de la lista, como IDM: "Oct 22 16:58:12 2026".
        public static string ListDate(string iso)
        {
            DateTime d;
            if (!TryDate(iso, out d)) return "";
            return d.ToString("MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture);
        }

        static bool TryDate(string iso, out DateTime d)
        {
            d = DateTime.MinValue;
            return !string.IsNullOrEmpty(iso) && DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
        }

        static string SafeDir(string path)
        {
            try { return System.IO.Path.GetDirectoryName(path) ?? ""; } catch { return ""; }
        }
    }
}
