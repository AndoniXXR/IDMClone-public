using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace IDMClone
{
    // "Agregando categoría" / "Editando propiedades de categoría" (clic derecho en el panel Categorías de IDM):
    // nombre, tipos de archivo, sitios, carpeta y "Recordar la última ruta de guardado".
    public class CategoryForm : DialogBase
    {
        readonly AppConfig _cfg;
        readonly Category _original;
        TextBox _name, _exts, _sites, _folder;
        CheckBox _remember, _sitesOn;

        // Para las pruebas: recibe los avisos en vez de mostrarlos.
        public static Action<string> MessageHook;
        public Category Result { get; private set; }

        public CategoryForm(AppConfig cfg, Category original)
        {
            _cfg = cfg;
            _original = original;
            bool general = original != null && original.IsGeneral;
            Text = original == null ? "Agregando categoría" : "Editando propiedades de categoría";
            ClientSize = new Size(493, 282);

            OptUi.Lbl(this, "Nombre de Categoría", 10, 8, 300);
            _name = OptUi.Box(this, "", 10, 26, 388);
            Button ok = OptUi.Btn(this, "Aceptar", 408, 24, 75);
            ok.Click += delegate { if (Accept()) { DialogResult = DialogResult.OK; Close(); } };
            Button cancel = OptUi.Btn(this, "Cancelar", 408, 60, 75); cancel.DialogResult = DialogResult.Cancel;
            AcceptButton = ok; CancelButton = cancel;

            OptUi.Lbl(this, "Poner los siguientes tipos de archivo en esta categoría automáticamente:", 10, 52, 394);
            _exts = OptUi.Box(this, "", 10, 70, 388);
            OptUi.Lbl(this, "Nota: escribe las extensiones separadas por un espacio (e.g. avi mpg mpeg)", 10, 92, 473);

            // Como IDM: los sitios solo cuentan si se marca la casilla.
            _sitesOn = OptUi.Chk(this, "Poner automáticamente a esta categoría los archivos solamente de los siguientes sitios:", 10, 118, 473);
            _sites = OptUi.Box(this, "", 10, 138, 473);
            OptUi.Note(this, "Separe los sitios con espacios. Puedes usar el asterisco (*) como un patrón de comodines.", 10, 160, 473, 16);
            _sitesOn.CheckedChanged += delegate { _sites.Enabled = _sitesOn.Checked; };

            Label folderL = OptUi.Lbl(this, "Guardar las futuras descargas de esta categoría en la siguiente carpeta:", 10, 186, 473);
            folderL.ForeColor = OptUi.BlueLabel;
            _folder = OptUi.Box(this, "", 10, 204, 473);
            _remember = OptUi.Chk(this, "Recordar la última ruta de guardado", 10, 232, 300);
            Button browse = OptUi.Btn(this, "Examinar...", 398, 230, 85);
            browse.Click += delegate { Browse(); };

            if (original != null)
            {
                _name.Text = original.Name;
                _exts.Text = original.Extensions ?? "";
                _sites.Text = original.Sites ?? "";
                _sitesOn.Checked = original.HasSites;
                _folder.Text = original.Folder ?? "";
                _remember.Checked = original.RememberLastPath;
            }
            else
            {
                // Una categoría nueva propone una subcarpeta de la carpeta por defecto (como "Video", "Music"...).
                _folder.Text = cfg.DefaultFolder ?? "";
                _name.TextChanged += delegate { if (!_folderEdited) SuggestFolder(); };
                _folder.TextChanged += delegate { if (!_suggesting) _folderEdited = true; };
            }
            _sites.Enabled = _sitesOn.Checked;
            if (general)
            {
                // General: recibe los tipos de archivo que no están en ninguna otra categoría.
                _name.ReadOnly = true;
                _exts.Text = "Los tipos de archivos que no están listados en cualquier otra categoría";
                _exts.Enabled = false; _sites.Enabled = false; _sitesOn.Enabled = false; _sitesOn.Checked = false;
            }
            ActiveControl = general ? (Control)_folder : _name;
            EndLayout();
        }

        bool _folderEdited, _suggesting;

        void SuggestFolder()
        {
            _suggesting = true;
            string n = Util.SanitizeFileName(_name.Text.Trim());
            _folder.Text = string.IsNullOrEmpty(n) ? (_cfg.DefaultFolder ?? "") : Path.Combine(_cfg.DefaultFolder ?? "", n);
            _suggesting = false;
        }

        void Browse()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "Elige un directorio";
                if (Directory.Exists(_folder.Text)) d.SelectedPath = _folder.Text;
                if (d.ShowDialog(this) == DialogResult.OK) _folder.Text = d.SelectedPath;
            }
        }

        // Para las pruebas (y el botón Aceptar).
        public void Set(string name, string exts, string sites, string folder, bool remember)
        {
            if (!_name.ReadOnly) _name.Text = name;
            if (_exts.Enabled) _exts.Text = exts;
            if (_sitesOn.Enabled) { _sites.Text = sites ?? ""; _sitesOn.Checked = (sites ?? "").Trim().Length > 0; }
            _folder.Text = folder;
            _remember.Checked = remember;
        }

        public bool Accept()
        {
            Category c = new Category();
            c.Name = _name.Text.Trim();
            c.Extensions = _exts.Enabled ? _exts.Text : "";
            c.Sites = _sitesOn.Enabled && _sitesOn.Checked ? _sites.Text.Trim() : null;
            c.Folder = _folder.Text.Trim();
            c.RememberLastPath = _remember.Checked;
            string err = _cfg.ValidateCategory(c, _original);
            if (err != null) { Say(err); return false; }
            try { c.Folder = Path.GetFullPath(c.Folder); }
            catch { Say("La carpeta \"" + c.Folder + "\" no es válida."); return false; }
            if (!Directory.Exists(c.Folder))
            {
                string q = "La carpeta \"" + c.Folder + "\" no existe.\r\n¿quieres crearla?";
                bool yes = MainForm.ConfirmHook != null ? MainForm.ConfirmHook(q)
                    : Msg.Show(this, q, "Categorías de IDMClone", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                if (!yes) return false;
                try { Directory.CreateDirectory(c.Folder); }
                catch (Exception ex) { Say("¡No se puede crear la carpeta!\r\n" + ex.Message); return false; }
            }
            Result = c;
            return true;
        }

        void Say(string text)
        {
            if (MessageHook != null) { MessageHook(text); return; }
            Msg.Show(this, text, "Categorías de IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
