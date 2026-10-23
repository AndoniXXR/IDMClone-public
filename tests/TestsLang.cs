using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace IDMClone.Tests
{
    // Idiomas (archivos .lng como los de IDM): traductor, cambio en caliente y cobertura del inglés.
    public static partial class TestMain
    {
        static void RunLang()
        {
            Run("idiomas: textos exactos, con partes variables, varias líneas y escapes", TL_Translate);
            Run("idiomas: traducir una ventana, volver al español y cambiar otra vez", TL_ApplyAndRevert);
            Run("idiomas: Ver > Idioma en la ventana principal (al momento y se recuerda)", TL_MainMenu);
            Run("idiomas: el inglés traduce todos los textos de las ventanas", TL_Coverage);
            Run("idiomas: textos repetidos en un archivo (vale el último) y el inglés sin repetidos", TL_Duplicates);
            Run("idiomas: chino y japonés tienen todos los textos del inglés, con sus %s, \\n y teclas", TL_CjkFiles);
            Run("idiomas: el chino traduce todos los textos de las ventanas", delegate { TL_CjkCoverage("zh"); });
            Run("idiomas: el japonés traduce todos los textos de las ventanas", delegate { TL_CjkCoverage("ja"); });
            Run("idiomas: letra de la interfaz en chino (YaHei) y Tahoma en los demás, también al cambiar; idioma del instalador", TL_CjkFont);
        }

        static string EnglishFile() { return LangFile("en"); }

        static string LangFile(string code)
        {
            string d = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 4 && d != null; i++)
            {
                string f = Path.Combine(d, "Languages", "idmclone_" + code + ".lng");
                if (File.Exists(f)) return f;
                d = Path.GetDirectoryName(d.TrimEnd('\\'));
            }
            throw new FileNotFoundException("No se encuentra Languages\\idmclone_" + code + ".lng");
        }

        static void TL_Translate()
        {
            string f = Path.Combine(Dl, "prueba-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".lng");
            File.WriteAllText(f, "; comentario\n[IDMClone]\nLANGUAGE=Prueba\nCODE=xx\n[Textos]\nHola=Hello\nUno\\=dos=One\\=two\n" +
                "Línea uno\\nLínea dos=Line one\\nLine two\nTiene %d archivos en %s=Has %d files in %s\nCarpeta=Folder\nVacío=\n", new UTF8Encoding(false));
            try
            {
                Lang.LoadFile(f);
                Check(Lang.Current == "Prueba" && Lang.Active, "Cabecera: " + Lang.Current);
                Check(Lang.T("Hola") == "Hello" && Lang.T("Adiós") == "Adiós", "Exacto / desconocido");
                Check(Lang.T("Uno=dos") == "One=two", "Escape \\=: " + Lang.T("Uno=dos"));
                Check(Lang.T("Línea uno\nLínea dos") == "Line one\nLine two", "Escape \\n");
                Check(Lang.T("Tiene 12 archivos en Carpeta") == "Has 12 files in Folder", "Patrón (y traduce lo variable): " + Lang.T("Tiene 12 archivos en Carpeta"));
                Check(Lang.T("Hola\r\nAdiós\r\nHola") == "Hello\r\nAdiós\r\nHello", "Varias líneas: " + Lang.T("Hola\r\nAdiós\r\nHola"));
                Check(Lang.T("Vacío") == "Vacío", "Una traducción vacía no cuenta");
                Lang.LoadFile(EnglishFile());
                Check(Lang.Current == "English" && Lang.Count > 400, "Inglés: " + Lang.Count);
                Check(Lang.T("Error: El archivo no existe en el servidor (404).") == "Error: The file does not exist on the server (404).", Lang.T("Error: El archivo no existe en el servidor (404)."));
                Check(Lang.T("Recordar esta ruta para la categoría \"Vídeo\"") == "Remember this path for \"Video\" category", "Categoría dentro de un patrón");
                Check(Lang.T("IDMClone Site Grabber. Paso 3 --- Asignar explorador de filtros") == "IDMClone Site Grabber. Step 3 --- Set explorer filters", "Título del Grabber");
                Check(Lang.T("Páginas web procesadas: 3 de 7") == "Web pages processed: 3 of 7", "Patrón anidado");
            }
            finally { Lang.Reset(); }
        }

        static void TL_ApplyAndRevert()
        {
            try
            {
                using (OptionsForm o = new OptionsForm(Cfg()))
                {
                    IntPtr h = o.Handle;
                    Lang.LoadFile(EnglishFile());
                    Lang.Apply(o);
                    TabControl tabs = Field<TabControl>(o, "_tabs");
                    Check(o.Text == "IDMClone Configuration" && tabs.TabPages[2].Text == "Save" && tabs.TabPages[6].Text == "Site Logins", "Inglés: " + o.Text + " / " + tabs.TabPages[2].Text);
                    ComboBox mode = Field<ComboBox>(o, "_duplicate");
                    int sel = mode.SelectedIndex;
                    Check((string)mode.Items[1] == "Add the duplicate with a numbered file name" && mode.SelectedIndex == sel, "Lista desplegable traducida sin perder la selección");
                    // El programa cambia un texto después: al volver a traducir se usa el nuevo.
                    Label folderLabel = Field<Label>(o, "_folderLabel");
                    o.SetCategoryFolder("Vídeo", Dl, false);
                    Check(folderLabel.Text.StartsWith("Directorio predeterminado"), "Texto puesto por el programa: " + folderLabel.Text);
                    Lang.Apply(o);
                    Check(folderLabel.Text == "Default download directory for \"Video\" category", "Texto nuevo traducido: " + folderLabel.Text);
                    Lang.Reset();
                    Lang.Apply(o);
                    Check(o.Text == "Configuración de IDMClone" && tabs.TabPages[2].Text == "Guardar" && (string)mode.Items[1] == "Añadir el duplicado con un nombre de archivo numerado", "No volvió al español: " + o.Text);
                    // La categoría se muestra con la etiqueta de IDM ("Video"), no con el nombre guardado ("Vídeo").
                    Check(folderLabel.Text == "Directorio predeterminado de descarga para la categoría \"Video\"", "Etiqueta en español: " + folderLabel.Text);
                    Lang.LoadFile(EnglishFile());
                    Lang.Apply(o);
                    Check(o.Text == "IDMClone Configuration", "Segundo cambio");
                }
            }
            finally { Lang.Reset(); }
        }

        static void TL_MainMenu()
        {
            MainForm f = NewMainForm(); // carpeta de datos propia: el idioma se instala en su Languages
            try
            {
                string langDir = Path.Combine(AppConfig.Dir, "Languages");
                Directory.CreateDirectory(langDir);
                File.Copy(EnglishFile(), Path.Combine(langDir, "idmclone_en.lng"), true);
                Check(Lang.Available().Exists(delegate(Lang.LangFile l) { return l.Name == "English" && l.Code == "en"; }), "No aparece el inglés");
                MenuStrip menu = Field<MenuStrip>(f, "_menu");
                ToolStripMenuItem view = (ToolStripMenuItem)menu.Items[3];
                ToolStripMenuItem lang = null;
                foreach (ToolStripItem i in view.DropDownItems) if (i.Text == "Idioma") lang = (ToolStripMenuItem)i;
                Check(lang != null, "Falta Ver > Idioma");
                f.SetLanguage("English");
                Check(menu.Items[0].Text == "&Tasks" && menu.Items[3].Text == "&View", "Menús: " + menu.Items[0].Text);
                ToolStrip tb = Field<ToolStrip>(f, "_toolbar");
                Check(tb.Items["tb_add"].Text == "Add URL" && tb.Items["tb_stopall"].Text == "Stop All", "Barra: " + tb.Items["tb_add"].Text);
                TreeView tree = Field<TreeView>(f, "_tree");
                Check(tree.Nodes[0].Text == "All Downloads" && tree.Nodes[0].Nodes[0].Text == "Compressed", "Árbol: " + tree.Nodes[0].Text + " / " + tree.Nodes[0].Nodes[0].Text);
                ListView list = Field<ListView>(f, "_list");
                Check(list.Columns[0].Text == "File Name", "Columnas: " + list.Columns[0].Text);
                Check(Field<AppConfig>(f, "_cfg").Language == "English" && AppConfig.Load().Language == "English", "No se guardó el idioma");
                // Las ventanas que se abren después ya salen en inglés.
                using (CategoryForm c = new CategoryForm(Field<AppConfig>(f, "_cfg"), null)) { c.CreateControl(); typeof(Form).GetMethod("OnLoad", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(c, new object[] { EventArgs.Empty }); Check(c.Text == "Adding category", "Ventana nueva: " + c.Text); }
                lang.ShowDropDown(); lang.HideDropDown();
                // Español + los archivos .lng (junto al programa: inglés, chino y japonés).
                List<string> marked = new List<string>();
                foreach (ToolStripItem i in lang.DropDownItems) if (((ToolStripMenuItem)i).Checked) marked.Add(i.Text);
                Check(lang.DropDownItems.Count == 1 + Lang.Available().Count && lang.DropDownItems.Count >= 2 && marked.Count == 1 && marked[0] == "English", "Lista de idiomas: " + lang.DropDownItems.Count + " / " + string.Join(", ", marked.ToArray()));
                f.SetLanguage(Lang.BuiltIn);
                Check(menu.Items[0].Text == "&Tareas" && tree.Nodes[0].Text == "Todas las descargas" && tb.Items["tb_add"].Text == "Agregar URL", "No volvió al español");
                Check(AppConfig.Load().Language == null, "Español no se guardó");
            }
            finally { Lang.Reset(); DisposeMainForm(f); }
        }

        // Textos que no se traducen: datos de prueba, números, direcciones y palabras iguales en inglés.
        static readonly HashSet<string> SameInEnglish = new HashSet<string> {
            "IDMClone", "URL", "General", "Info", "N.", "Q", "Total", "Proxy", "Grabber", "Insert", "Ctrl", "Alt", "Error", "Audio", "No",
            "×", "...", "Sa", "Proxy/Socks", "MBytes", "IDMClone Site Grabber", "Media", "User-Agent:", "KB/s", "MB", "KB", "Español", "English", "(colas)", "(idiomas)", "(estilos)", "Microsoft Defender", "Video", "Simple", "Socks" };

        static bool Ignorable(string t)
        {
            string s = t.Trim();
            if (s.Length == 0 || !Regex.IsMatch(s, @"\p{L}")) return true;
            if (SameInEnglish.Contains(s)) return true;
            if (Regex.IsMatch(s, @"^(https?|ftps?)://|^[A-Za-z]:\\|^/|^\d|\.\w{2,4}$|^[\w\-. ]+\.\w{2,4}\b|MB/s|^127\.")) return true;
            return false;
        }

        static void TL_Coverage()
        {
            List<string> texts = CoverageTexts();
            Lang.LoadFile(EnglishFile());
            try
            {
                List<string> missing = new List<string>();
                foreach (string t in texts)
                {
                    if (Ignorable(t)) continue;
                    if (Lang.T(t) == t && !missing.Contains(t)) missing.Add(t.Replace("\r\n", "\\n"));
                }
                Check(missing.Count == 0, missing.Count + " textos sin traducir: " + string.Join(" | ", missing.ToArray()));
                Check(texts.Count > 300, "Pocos textos recogidos: " + texts.Count);
            }
            finally { Lang.Reset(); }
        }

        // Textos (en español) de todas las ventanas: se recogen una vez para el inglés, el chino y el japonés.
        static List<string> _coverageTexts;

        static List<string> CoverageTexts()
        {
            if (_coverageTexts != null) return _coverageTexts;
            Lang.Reset();
            List<string> texts = new List<string>();
            MainForm mf = NewMainForm();
            try
            {
                DownloadManager m = Field<DownloadManager>(mf, "_mgr");
                AppConfig c = m.Config;
                DownloadItem it = m.Add(S.Url("range", "cobertura.zip", 1024, NewId("cov")), Dl, null, false);
                Lang.Collect(mf, texts); Lang.CollectItems(Field<MenuStrip>(mf, "_menu").Items, texts);
                List<Form> forms = new List<Form>();
                forms.Add(new OptionsForm(c)); forms.Add(new SchedulerForm(m, QueueDef.MainId)); forms.Add(new CategoryForm(c, null));
                forms.Add(new CategoryForm(c, c.FindCategory("General"))); forms.Add(new MediaPanelForm(c)); forms.Add(new AddUrlForm(""));
                forms.Add(new FileInfoForm(c, m, it)); forms.Add(new QueuePickForm(m.Queues)); forms.Add(new CompleteForm(it));
                forms.Add(new PropertiesForm(c, m, it)); forms.Add(new ColumnsForm(c.Columns)); forms.Add(new DuplicateForm(it));
                forms.Add(new RefreshWaitForm(it)); forms.Add(new SpeedLimiterForm(c)); forms.Add(new DeleteConfirmForm(1)); forms.Add(new DeleteConfirmForm(3));
                forms.Add(new LinkListForm(c, m, new List<LinkEntry> { new LinkEntry("https://ej.com/a.zip", "a") }, null)); forms.Add(new BatchForm(null));
                forms.Add(new GrabberStatsForm()); forms.Add(new GrabberSettingsForm(GrabberProject.CreateDefault())); forms.Add(new GrabberFiltersForm(false));
                forms.Add(new GrabberFilterEditForm(null)); forms.Add(new VirusCheckForm(null, null)); forms.Add(new IDMClone.Setup.SetupForm() { Quiet = true }); forms.Add(new IDMClone.Setup.UninstallForm()); forms.Add(new ToolbarCustomizeForm(ToolbarCatalog.Parse(null), "3d")); forms.Add(new GrabberNameForm("Guardando el proyecto de IDMClone Grabber", "Guardar este proyecto con el siguiente nombre:", ""));
                forms.Add(new GrabberPickForm(new List<string>())); forms.Add(new ProgressForm(it, m));
                GrabberForm g = new GrabberForm(c, m, null, "http://ej.com/");
                forms.Add(g);
                foreach (Form f in forms) { IntPtr h = f.Handle; Lang.Collect(f, texts); if (f.MainMenuStrip != null) Lang.CollectItems(f.MainMenuStrip.Items, texts); }
                for (int step = 2; step <= 4; step++) { g.ShowStep(step); texts.Add(g.Text); }
                foreach (Form f in forms) f.Dispose();
            }
            finally { DisposeMainForm(mf); }
            _coverageTexts = texts;
            return texts;
        }

        static void TL_Duplicates()
        {
            string f = Path.Combine(Dl, "repetidos-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".lng");
            File.WriteAllText(f, "[IDMClone]\nLANGUAGE=Repetidos\nCODE=xx\n[Textos]\nSubir=Up\nBajar=Down\nSubir=Move Up\nCola %d=Q %d\nCola %d=Queue %d\n", new UTF8Encoding(false));
            try
            {
                Lang.LoadFile(f);
                Check(Lang.Count == 3, "Entradas: " + Lang.Count);
                Check(Lang.T("Subir") == "Move Up" && Lang.T("Bajar") == "Down", "Exacto repetido: " + Lang.T("Subir"));
                Check(Lang.T("Cola 4") == "Queue 4", "Patrón repetido: " + Lang.T("Cola 4"));
                Check(Lang.CurrentCode == "xx", "Código: " + Lang.CurrentCode);
            }
            finally { Lang.Reset(); }
            Check(Lang.CurrentCode == null, "El español no tiene código");
            List<string> dup = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in LngEntries(EnglishFile())) if (!seen.Add(kv.Key)) dup.Add(kv.Key);
            Check(dup.Count == 0, "Repetidos en idmclone_en.lng: " + string.Join(" | ", dup.ToArray()));
        }

        // Entradas de un archivo .lng tal cual están escritas (clave sin desescapar, para comparar archivos).
        static List<KeyValuePair<string, string>> LngEntries(string path)
        {
            List<KeyValuePair<string, string>> r = new List<KeyValuePair<string, string>>();
            bool texts = false;
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith(";")) continue;
                if (line.StartsWith("[")) { texts = line.StartsWith("[Textos]"); continue; }
                if (!texts) continue;
                int eq = -1;
                for (int i = 0; i < line.Length; i++) { if (line[i] == '\\') { i++; continue; } if (line[i] == '=') { eq = i; break; } }
                if (eq > 0) r.Add(new KeyValuePair<string, string>(line.Substring(0, eq), line.Substring(eq + 1)));
            }
            return r;
        }

        static string Tokens(string s)
        {
            StringBuilder b = new StringBuilder();
            foreach (Match m in Regex.Matches(s, @"%[sd]|\\n|\\=")) b.Append(m.Value);
            return b.ToString();
        }

        // Textos que en chino y japonés se quedan igual: marcas, teclas, unidades y datos.
        static readonly HashSet<string> SameInCjk = new HashSet<string> {
            "IDMClone", "URL", "Ctrl", "Alt", "Insert", "Del", "Shift", "×", "...", "KB/s", "MB", "KB", "Español", "English",
            "Microsoft Defender", "Socks", "User-Agent:", "Q", "%d / %d", "(colas)", "(idiomas)", "(estilos)", "MBytes",
            "OK", "No." }; // "OK" y "No." como en un Windows japonés

        static void TL_CjkFiles()
        {
            Dictionary<string, string> en = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in LngEntries(EnglishFile())) en[kv.Key] = kv.Value;
            Check(en.Count > 1000, "Inglés: " + en.Count);
            foreach (string code in new string[] { "zh", "ja" })
            {
                List<string> bad = new List<string>();
                Dictionary<string, string> tr = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, string> kv in LngEntries(LangFile(code)))
                {
                    if (tr.ContainsKey(kv.Key)) bad.Add("repetido: " + kv.Key);
                    tr[kv.Key] = kv.Value;
                    string v = kv.Value;
                    if (v.Trim().Length == 0) bad.Add("vacío: " + kv.Key);
                    if (Tokens(kv.Key) != Tokens(v)) bad.Add("%s/\\n: " + kv.Key + " => " + v);
                    if (kv.Key.Contains("&") && !Regex.IsMatch(v, @"\(&[A-Z]\)$")) bad.Add("tecla (&X): " + v);
                    if (kv.Key.EndsWith("...") != v.EndsWith("...")) bad.Add("puntos: " + v);
                    // Traducido de verdad: con caracteres chinos/japoneses, o igual que en inglés solo si es una marca o una tecla.
                    if (!Regex.IsMatch(v, @"[\u3040-\u30FF\u4E00-\u9FFF]") && !SameInCjk.Contains(v) && Regex.IsMatch(v, "[A-Za-z]{2,}"))
                        bad.Add("sin traducir: " + v);
                }
                foreach (string k in en.Keys) if (!tr.ContainsKey(k)) bad.Add("falta: " + k);
                Check(bad.Count == 0, code + ": " + bad.Count + " problemas: " + string.Join(" | ", bad.ToArray()));
                Lang.LoadFile(LangFile(code));
                try
                {
                    Check(Lang.CurrentCode == code && Lang.Current == (code == "zh" ? "简体中文" : "日本語") && Lang.Count >= en.Count, code + ": " + Lang.Current + " / " + Lang.Count);
                    // Patrones (el valor de dentro también se traduce) y varias líneas.
                    string q = Lang.T("Cola 3"), cat = Lang.T("Recordar esta ruta para la categoría \"Vídeo\""), err = Lang.T("Error: El archivo no existe en el servidor (404).");
                    Check(q.Contains("3") && q != "Cola 3" && !cat.Contains("Vídeo") && !cat.Contains("Video") && !err.Contains("servidor"), code + ": " + q + " / " + cat + " / " + err);
                    string two = Lang.T("Recordar mi selección y no mostrar este diálogo la próxima vez.\nPuedes cambiar más tarde esto en Opciones -> Descargas.");
                    Check(two.Split('\n').Length == 2 && !two.Contains("Opciones"), code + ": " + two);
                }
                finally { Lang.Reset(); }
            }
        }

        static void TL_CjkCoverage(string code)
        {
            List<string> texts = CoverageTexts();
            Lang.LoadFile(LangFile(code));
            try
            {
                List<string> missing = new List<string>();
                foreach (string t in texts)
                {
                    string s = t.Trim();
                    if (s.Length == 0 || !Regex.IsMatch(s, @"\p{L}") || SameInCjk.Contains(s)) continue;
                    if (Regex.IsMatch(s, @"^(https?|ftps?)://|^[A-Za-z]:\\|^/|^\d|\.\w{2,4}$|^[\w\-. ]+\.\w{2,4}\b|MB/s|^127\.")) continue;
                    string r = Lang.T(t);
                    // Traducido: cambia y lleva caracteres chinos/japoneses (o es una marca/tecla que se queda igual).
                    if ((r == t || !Regex.IsMatch(r, @"[\u3040-\u30FF\u4E00-\u9FFF]")) && !SameInCjk.Contains(r.Trim()) && !missing.Contains(t))
                        missing.Add(t.Replace("\r\n", "\\n") + " => " + r.Replace("\r\n", "\\n"));
                }
                Check(missing.Count == 0, code + ": " + missing.Count + " textos sin traducir: " + string.Join(" | ", missing.ToArray()));
                Check(texts.Count > 300, "Pocos textos recogidos: " + texts.Count);
            }
            finally { Lang.Reset(); }
        }

        static void TL_CjkFont()
        {
            Lang.Reset();
            Check(Theme.UiFont.Name == "Tahoma" && Math.Abs(Theme.UiFont.SizeInPoints - 8.25f) < 0.01f && Theme.UiFontBold.Bold, "Español: " + Theme.UiFont);
            Lang.LoadFile(EnglishFile());
            Check(Theme.UiFamily == "Tahoma", "Inglés: " + Theme.UiFamily);
            Lang.LoadFile(LangFile("ja"));
            Check(Theme.UiFamily == "Tahoma", "Japonés (MS UI Gothic por el enlace de Tahoma): " + Theme.UiFamily);
            Lang.LoadFile(LangFile("zh"));
            string zh = Theme.UiFamily;
            Check(zh == "Microsoft YaHei UI" || zh == "Microsoft YaHei" || zh == "SimSun", "Chino: " + zh);
            Check(Theme.UiFont.Name == zh && Math.Abs(Theme.UiFont.SizeInPoints - 8.25f) < 0.01f && OptUi.TitleFont.Name == zh, "Letras del chino: " + Theme.UiFont + " / " + OptUi.TitleFont);
            using (OptionsForm o = new OptionsForm(Cfg())) Check(o.Font.Name == zh, "Ventana nueva en chino: " + o.Font.Name);
            Lang.Reset();
            Check(Theme.UiFont.Name == "Tahoma" && OptUi.TitleFont.Name == "Tahoma", "Vuelta al español: " + Theme.UiFont);

            // Al cambiar de idioma en Ver > Idioma, la ventana principal cambia de letra (y vuelve a Tahoma).
            MainForm f = NewMainForm();
            try
            {
                string langDir = Path.Combine(AppConfig.Dir, "Languages");
                Directory.CreateDirectory(langDir);
                File.Copy(LangFile("zh"), Path.Combine(langDir, "idmclone_zh.lng"), true);
                File.Copy(LangFile("ja"), Path.Combine(langDir, "idmclone_ja.lng"), true);
                Check(Lang.Available().Exists(delegate(Lang.LangFile l) { return l.Name == "简体中文" && l.Code == "zh"; })
                    && Lang.Available().Exists(delegate(Lang.LangFile l) { return l.Name == "日本語" && l.Code == "ja"; }), "No aparecen el chino y el japonés");
                ListView list = Field<ListView>(f, "_list");
                ToolStrip tb = Field<ToolStrip>(f, "_toolbar");
                f.SetLanguage("简体中文");
                Check(f.Font.Name == zh && tb.Font.Name == zh && list.Font.Name == zh, "Principal en chino: " + f.Font.Name + " / " + tb.Font.Name + " / " + list.Font.Name);
                Check(tb.Items["tb_add"].Text == "添加 URL" && list.Columns[0].Text == "文件名", "Textos en chino: " + tb.Items["tb_add"].Text);
                f.SetLanguage("日本語");
                Check(f.Font.Name == "Tahoma" && tb.Font.Name == "Tahoma" && tb.Items["tb_add"].Text == "URL の追加", "Principal en japonés: " + f.Font.Name + " / " + tb.Items["tb_add"].Text);
                f.SetLanguage(Lang.BuiltIn);
                Check(f.Font.Name == "Tahoma" && list.Font.Name == "Tahoma" && tb.Items["tb_add"].Text == "Agregar URL", "Vuelta al español: " + f.Font.Name);
            }
            finally { Lang.Reset(); DisposeMainForm(f); }

            // El instalador sale en el idioma de Windows (el chino tradicional y los demás, en inglés).
            Func<string, string> setup = delegate(string c) { return IDMClone.Setup.SetupProgram.SetupLanguage(new System.Globalization.CultureInfo(c)); };
            Check(setup("es-MX") == null && setup("es-ES") == null && setup("en-US") == "en" && setup("fr-FR") == "en" && setup("ja-JP") == "ja"
                && setup("zh-CN") == "zh" && setup("zh-SG") == "zh" && setup("zh-TW") == "en" && setup("zh-HK") == "en",
                "Idioma del instalador: " + setup("zh-CN") + " / " + setup("zh-TW") + " / " + setup("ja-JP"));
        }
    }
}
