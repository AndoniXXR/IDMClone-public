using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace IDMClone
{
    // Idiomas de la interfaz (como los archivos .lng de IDM). El programa está escrito en español; un archivo
    // de idioma dice cómo se traduce cada texto:
    //
    //   [IDMClone]
    //   LANGUAGE=English
    //   CODE=en
    //   [Textos]
    //   Agregar URL=Add URL
    //   Recordar esta ruta para la categoría "%s"=Remember this path for the "%s" category
    //
    // %s es cualquier texto y %d un número (en el mismo orden en la traducción). \n es un salto de línea.
    // Lo que no está en el archivo se queda en español. Los archivos van en la carpeta "Languages" junto
    // a IDMClone.exe o en %APPDATA%\IDMClone\Languages (idmclone_<código>.lng).
    public static class Lang
    {
        public const string BuiltIn = "Español";

        public class LangFile
        {
            public string Name, Code, Path, Author;
            public override string ToString() { return Name; }
        }

        static Dictionary<string, string> _exact = new Dictionary<string, string>(StringComparer.Ordinal);
        static List<KeyValuePair<Regex, string>> _patterns = new List<KeyValuePair<Regex, string>>();
        static string _current = BuiltIn;
        static string _code; // CODE= de la cabecera ("en", "zh"...); null con el español del programa

        public static string Current { get { return _current; } }
        public static string CurrentCode { get { return _code; } }
        public static bool Active { get { return _exact.Count > 0 || _patterns.Count > 0; } }
        public static int Count { get { return _exact.Count + _patterns.Count; } }

        public static List<string> Folders()
        {
            List<string> r = new List<string>();
            try { r.Add(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.ExecutablePath), "Languages")); } catch { }
            try { r.Add(System.IO.Path.Combine(AppConfig.Dir, "Languages")); } catch { }
            return r;
        }

        public static List<LangFile> Available()
        {
            List<LangFile> r = new List<LangFile>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string d in Folders())
            {
                if (!Directory.Exists(d)) continue;
                foreach (string f in Directory.GetFiles(d, "*.lng"))
                {
                    LangFile l = ReadHeader(f);
                    if (l != null && seen.Add(l.Name)) r.Add(l);
                }
            }
            r.Sort(delegate(LangFile a, LangFile b) { return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); });
            return r;
        }

        static LangFile ReadHeader(string path)
        {
            try
            {
                LangFile l = new LangFile(); l.Path = path;
                foreach (string raw in File.ReadLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("[Textos]", StringComparison.OrdinalIgnoreCase)) break;
                    if (line.StartsWith("LANGUAGE=", StringComparison.OrdinalIgnoreCase)) l.Name = line.Substring(9).Trim();
                    else if (line.StartsWith("CODE=", StringComparison.OrdinalIgnoreCase)) l.Code = line.Substring(5).Trim();
                    else if (line.StartsWith("AUTHOR=", StringComparison.OrdinalIgnoreCase)) l.Author = line.Substring(7).Trim();
                }
                if (string.IsNullOrEmpty(l.Name)) l.Name = System.IO.Path.GetFileNameWithoutExtension(path);
                return l;
            }
            catch { return null; }
        }

        // Carga un idioma por su nombre (o el español del programa). false si no se encuentra.
        public static bool Load(string name)
        {
            if (string.IsNullOrEmpty(name) || name == BuiltIn) { Reset(); return true; }
            foreach (LangFile l in Available())
                if (string.Equals(l.Name, name, StringComparison.CurrentCultureIgnoreCase) || string.Equals(l.Code, name, StringComparison.OrdinalIgnoreCase))
                {
                    LoadFile(l.Path);
                    return true;
                }
            Reset();
            return false;
        }

        public static void Reset()
        {
            _exact = new Dictionary<string, string>(StringComparer.Ordinal);
            _patterns = new List<KeyValuePair<Regex, string>>();
            _current = BuiltIn;
            _code = null;
        }

        public static void LoadFile(string path)
        {
            Dictionary<string, string> exact = new Dictionary<string, string>(StringComparer.Ordinal);
            List<KeyValuePair<Regex, string>> patterns = new List<KeyValuePair<Regex, string>>();
            Dictionary<string, string> withParts = new Dictionary<string, string>(StringComparer.Ordinal); // textos con %s / %d
            bool texts = false;
            string name = null, code = null;
            foreach (string raw in File.ReadLines(path, Encoding.UTF8))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith(";")) continue;
                if (line.StartsWith("[")) { texts = line.StartsWith("[Textos]", StringComparison.OrdinalIgnoreCase); continue; }
                if (!texts)
                {
                    if (line.StartsWith("LANGUAGE=", StringComparison.OrdinalIgnoreCase)) name = line.Substring(9).Trim();
                    else if (line.StartsWith("CODE=", StringComparison.OrdinalIgnoreCase)) code = line.Substring(5).Trim().ToLowerInvariant();
                    continue;
                }
                int eq = SplitAt(line);
                if (eq <= 0) continue;
                string key = Unescape(line.Substring(0, eq)), value = Unescape(line.Substring(eq + 1));
                if (value.Length == 0) continue;
                // Un texto repetido en el archivo no es un error: vale la última traducción.
                if (key.Contains("%s") || key.Contains("%d")) withParts[key] = value;
                else exact[key] = value;
            }
            foreach (KeyValuePair<string, string> p in withParts)
            {
                string re = "^" + Regex.Escape(p.Key).Replace("%s", "(.*?)").Replace("%d", @"(-?\d+)") + "$";
                patterns.Add(new KeyValuePair<Regex, string>(new Regex(re, RegexOptions.Singleline | RegexOptions.CultureInvariant), p.Value));
            }
            // Los patrones más largos primero (más concretos).
            patterns.Sort(delegate(KeyValuePair<Regex, string> a, KeyValuePair<Regex, string> b) { return b.Key.ToString().Length.CompareTo(a.Key.ToString().Length); });
            _exact = exact; _patterns = patterns;
            _current = name ?? System.IO.Path.GetFileNameWithoutExtension(path);
            _code = string.IsNullOrEmpty(code) ? null : code;
        }

        // Primer "=" sin escapar (\= dentro de un texto).
        static int SplitAt(string line)
        {
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '\\') { i++; continue; }
                if (line[i] == '=') return i;
            }
            return -1;
        }

        static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            StringBuilder b = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    b.Append(n == 'n' ? '\n' : n == 'r' ? '\r' : n == 't' ? '\t' : n);
                }
                else b.Append(s[i]);
            }
            return b.ToString();
        }

        public static string Escape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("=", "\\=").Replace("\r", "").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        // Traducción de un texto (o el mismo si no se conoce).
        public static string T(string s)
        {
            if (string.IsNullOrEmpty(s) || !Active) return s;
            string r;
            if (_exact.TryGetValue(s, out r)) return r;
            foreach (KeyValuePair<Regex, string> p in _patterns)
            {
                Match m = p.Key.Match(s);
                if (!m.Success) continue;
                int g = 1;
                string outText = Regex.Replace(p.Value, "%[sd]", delegate(Match x) { return g < m.Groups.Count ? T(m.Groups[g++].Value) : x.Value; });
                return outText;
            }
            // Mensajes de varias líneas: cada línea por separado.
            if (s.IndexOf('\n') >= 0)
            {
                string[] lines = s.Replace("\r\n", "\n").Split('\n');
                bool any = false;
                for (int i = 0; i < lines.Length; i++) { string t = T(lines[i]); if (t != lines[i]) { any = true; lines[i] = t; } }
                if (any) return string.Join(s.Contains("\r\n") ? "\r\n" : "\n", lines);
            }
            return s;
        }

        // --- Traducir ventanas ---
        // Se guarda el texto original de cada elemento: así se puede cambiar de idioma (o volver al español)
        // con las ventanas abiertas. Si el programa cambia el texto después, el nuevo es el original.
        class Orig { public string Source, Shown, Tip, TipShown; }
        static readonly ConditionalWeakTable<object, Orig> _orig = new ConditionalWeakTable<object, Orig>();
        static readonly ConditionalWeakTable<object, object> _hooked = new ConditionalWeakTable<object, object>();

        static string Tr(object owner, string current, bool tip)
        {
            Orig o = _orig.GetOrCreateValue(owner);
            if (tip)
            {
                if (o.Tip == null || current != o.TipShown) o.Tip = current;
                o.TipShown = T(o.Tip);
                return o.TipShown;
            }
            if (o.Source == null || current != o.Shown) o.Source = current;
            o.Shown = T(o.Source);
            return o.Shown;
        }

        // Sin idioma cargado también se recorre: devuelve los textos originales si antes había otro idioma.
        public static void Apply(Control root)
        {
            if (root != null) Walk(root);
        }

        // Textos de una ventana (para crear archivos de idioma y comprobar que están todos).
        public static void Collect(Control c, ICollection<string> into)
        {
            if (Keep.Equals(c.Tag)) return;
            if (Translatable(c)) Add(into, Source(c, c.Text));
            string[] orig;
            if (Mark.Equals(c.Tag))
            {
                if (_items.TryGetValue(c, out orig)) { foreach (string s in orig) Add(into, s); }
                else if (c is ComboBox) { foreach (object o in ((ComboBox)c).Items) Add(into, o as string); }
                else if (c is ListView) { foreach (ListViewItem i in ((ListView)c).Items) Add(into, i.Text); }
            }
            ListView lv = c as ListView;
            if (lv != null) foreach (ColumnHeader h in lv.Columns) Add(into, Source(h, h.Text));
            TreeView tv = c as TreeView;
            if (tv != null) CollectNodes(tv.Nodes, into);
            ToolStrip ts = c as ToolStrip;
            if (ts != null) CollectItems(ts.Items, into);
            if (c.ContextMenuStrip != null) CollectItems(c.ContextMenuStrip.Items, into);
            foreach (Control ch in c.Controls) Collect(ch, into);
        }

        static string Source(object owner, string current)
        {
            Orig o;
            return _orig.TryGetValue(owner, out o) && current == o.Shown && o.Source != null ? o.Source : current;
        }

        static void Add(ICollection<string> into, string s)
        {
            if (string.IsNullOrEmpty(s) || s.Trim().Length == 0 || into.Contains(s)) return;
            into.Add(s);
        }

        static void CollectNodes(TreeNodeCollection nodes, ICollection<string> into)
        {
            foreach (TreeNode n in nodes) { Add(into, Source(n, n.Text)); CollectNodes(n.Nodes, into); }
        }

        public static void CollectItems(ToolStripItemCollection items, ICollection<string> into)
        {
            foreach (ToolStripItem i in items)
            {
                if (!(i is ToolStripTextBox) && !(i is ToolStripComboBox)) Add(into, Source(i, i.Text));
                Orig o;
                Add(into, _orig.TryGetValue(i, out o) && o.Tip != null && i.ToolTipText == o.TipShown ? o.Tip : i.ToolTipText);
                ToolStripDropDownItem dd = i as ToolStripDropDownItem;
                if (dd != null) CollectItems(dd.DropDownItems, into);
            }
        }

        static bool Translatable(Control c)
        {
            return !(c is TextBoxBase) && !(c is ComboBox) && !(c is NumericUpDown) && !(c is ListBox) && !(c is DateTimePicker) && !(c is WebBrowser);
        }

        // Listas con textos fijos (marcadas con Tag = Lang.Mark): se traducen sus elementos.
        public const string Mark = "traducir";
        public const string Keep = "sin-traducir"; // datos (nombres de Windows, rutas...): no se tocan
        static readonly ConditionalWeakTable<object, string[]> _items = new ConditionalWeakTable<object, string[]>();

        static void ListItems(object owner, int count, Func<int, string> get, Action<int, string> set)
        {
            string[] orig;
            if (!_items.TryGetValue(owner, out orig) || orig.Length != count)
            {
                if (orig != null) _items.Remove(owner);
                orig = new string[count];
                for (int i = 0; i < count; i++) orig[i] = get(i);
                _items.Add(owner, orig);
            }
            for (int i = 0; i < count; i++)
                if (orig[i] != null) { string t = T(orig[i]); if (t != get(i)) set(i, t); }
        }

        static void Walk(Control c)
        {
            if (Keep.Equals(c.Tag)) return;
            if (Translatable(c) && !string.IsNullOrEmpty(c.Text)) { string t = Tr(c, c.Text, false); if (t != c.Text) c.Text = t; }
            if (Mark.Equals(c.Tag))
            {
                ComboBox cb = c as ComboBox;
                if (cb != null)
                {
                    int sel = cb.SelectedIndex;
                    ListItems(cb, cb.Items.Count, delegate(int i) { return cb.Items[i] as string; }, delegate(int i, string t) { cb.Items[i] = t; });
                    if (cb.SelectedIndex != sel) cb.SelectedIndex = sel;
                }
                ListView items = c as ListView;
                if (items != null) ListItems(items, items.Items.Count, delegate(int i) { return items.Items[i].Text; }, delegate(int i, string t) { items.Items[i].Text = t; });
            }
            ListView lv = c as ListView;
            if (lv != null) foreach (ColumnHeader h in lv.Columns) { string t = Tr(h, h.Text, false); if (t != h.Text) h.Text = t; }
            TreeView tv = c as TreeView;
            if (tv != null) Nodes(tv.Nodes);
            ToolStrip ts = c as ToolStrip;
            if (ts != null) Items(ts.Items);
            if (c.ContextMenuStrip != null) { Items(c.ContextMenuStrip.Items); Hook(c.ContextMenuStrip); }
            foreach (Control ch in c.Controls) Walk(ch);
        }

        static void Nodes(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes)
            {
                string t = Tr(n, n.Text, false); if (t != n.Text) n.Text = t;
                Nodes(n.Nodes);
            }
        }

        public static void Items(ToolStripItemCollection items)
        {
            foreach (ToolStripItem i in items)
            {
                if (!string.IsNullOrEmpty(i.Text) && !(i is ToolStripTextBox) && !(i is ToolStripComboBox)) { string t = Tr(i, i.Text, false); if (t != i.Text) i.Text = t; }
                if (!string.IsNullOrEmpty(i.ToolTipText)) { string t = Tr(i, i.ToolTipText, true); if (t != i.ToolTipText) i.ToolTipText = t; }
                ToolStripDropDownItem dd = i as ToolStripDropDownItem;
                if (dd != null)
                {
                    Items(dd.DropDownItems);
                    Hook(dd.DropDown);
                }
            }
        }

        // Menús que se rellenan al abrirse (colas, idiomas...): se traducen justo antes de mostrarse.
        static void Hook(ToolStripDropDown dd)
        {
            object x;
            if (dd == null || _hooked.TryGetValue(dd, out x)) return;
            _hooked.Add(dd, true);
            dd.Opened += delegate { Items(dd.Items); };
        }

        public static void ApplyAll()
        {
            foreach (Form f in Application.OpenForms) Walk(f);
        }
    }

    // MessageBox con el texto traducido.
    public static class Msg
    {
        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons b, MessageBoxIcon i)
        { return MessageBox.Show(owner, Lang.T(text), Lang.T(caption), b, i); }
        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons b, MessageBoxIcon i, MessageBoxDefaultButton d)
        { return MessageBox.Show(owner, Lang.T(text), Lang.T(caption), b, i, d); }
        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons b)
        { return MessageBox.Show(owner, Lang.T(text), Lang.T(caption), b); }
        public static DialogResult Show(IWin32Window owner, string text, string caption)
        { return MessageBox.Show(owner, Lang.T(text), Lang.T(caption)); }
        public static DialogResult Show(IWin32Window owner, string text)
        { return MessageBox.Show(owner, Lang.T(text)); }
        public static DialogResult Show(string text, string caption, MessageBoxButtons b, MessageBoxIcon i)
        { return MessageBox.Show(Lang.T(text), Lang.T(caption), b, i); }
        public static DialogResult Show(string text, string caption)
        { return MessageBox.Show(Lang.T(text), Lang.T(caption)); }
        public static DialogResult Show(string text)
        { return MessageBox.Show(Lang.T(text)); }
    }
}
