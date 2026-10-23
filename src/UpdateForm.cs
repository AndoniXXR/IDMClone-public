using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace IDMClone
{
    // "Actualización de IDMClone": la ventana de Ayuda > Comprobar actualizaciones... y la que sale sola cuando la
    // comprobación semanal encuentra una versión nueva ("Hay una nueva versión..."), con las novedades y los botones
    // "Actualizar ahora" / "Más tarde". "Actualizar ahora" descarga el instalador aquí mismo (barra de progreso),
    // lo comprueba y cierra la ventana con OK: quien la abrió lanza el instalador y cierra IDMClone.
    public class UpdateForm : DialogBase
    {
        readonly AppConfig _cfg;
        readonly string _exe;
        Label _title, _sub, _notesLabel, _status;
        TextBox _notes;
        ProgressBar _bar;
        CheckBox _auto;
        Button _update, _later;
        volatile bool _cancel;

        public UpdateInfo Info { get; private set; }
        public string Error { get; private set; }          // último error (comprobar o descargar)
        public string InstallerPath { get; private set; }  // instalador descargado y comprobado
        public bool Busy { get; private set; }
        public bool HasUpdate { get { return Updater.IsNewer(Info); } }

        // info == null: comprueba al abrirse (Ayuda > Comprobar actualizaciones...).
        public UpdateForm(AppConfig cfg, UpdateInfo info) : this(cfg, info, Application.ExecutablePath) { }

        public UpdateForm(AppConfig cfg, UpdateInfo info, string currentExe)
        {
            _cfg = cfg; _exe = currentExe; Info = info;
            Text = "Actualización de IDMClone";
            ShowInTaskbar = true;
            ClientSize = new Size(440, 300);
            PictureBox icon = new PictureBox(); icon.SizeMode = PictureBoxSizeMode.Zoom; icon.SetBounds(12, 12, 32, 32);
            try { using (Icon ic = AppIcon.Sized(32)) icon.Image = ic.ToBitmap(); icon.Disposed += delegate { if (icon.Image != null) icon.Image.Dispose(); }; } catch { }
            Controls.Add(icon);
            _title = AddLabel("", 56, 10, 372); _title.Height = 22; _title.Font = new Font(Font, FontStyle.Bold);
            _sub = AddLabel("Versión instalada: " + Updater.Display(Updater.CurrentVersion), 56, 32, 372); _sub.Height = 18;
            _notesLabel = AddLabel("Novedades:", 12, 58, 200); _notesLabel.Height = 18;
            _notes = new TextBox(); _notes.Multiline = true; _notes.ReadOnly = true; _notes.ScrollBars = ScrollBars.Vertical;
            _notes.SetBounds(12, 78, 416, 96); _notes.BackColor = SystemColors.Window; _notes.TabStop = false; Controls.Add(_notes);
            // Dos líneas: los errores ("...no coincide con el publicado (SHA-256 distinto): se ha descartado.") no caben en una.
            _status = AddLabel("", 12, 179, 416); _status.Height = 34; _status.AutoEllipsis = true; _status.TextAlign = ContentAlignment.TopLeft;
            _bar = new ProgressBar(); _bar.SetBounds(12, 216, 416, 16); _bar.Visible = false; Controls.Add(_bar);
            _auto = new CheckBox(); _auto.Text = "Comprobar actualizaciones automáticamente cada semana";
            _auto.SetBounds(12, 240, 416, 20); _auto.Checked = cfg.AutoCheckUpdates; Controls.Add(_auto);
            _auto.CheckedChanged += delegate { _cfg.AutoCheckUpdates = _auto.Checked; _cfg.Save(); };
            _update = AddButton("Actualizar ahora", 226, 266, 100, DialogResult.None); _update.Height = 25;
            _later = AddButton("Más tarde", 332, 266, 96, DialogResult.Cancel); _later.Height = 25;
            _update.Click += delegate { BeginDownload(); };
            AcceptButton = _update; CancelButton = _later;
            ShowState();
            EndLayout();
        }

        // Texto traducido (lo que cambia después de abrirse no lo traduce Lang.Apply).
        static void Set(Control c, string es) { c.Text = Lang.T(es); }

        void ShowState()
        {
            _bar.Visible = Busy && HasUpdate;
            _notesLabel.Visible = Info != null; // sin versión que mostrar, el cuadro lleva el error, no novedades
            if (Busy && Info == null)
            {
                Set(_title, "Comprobando si hay una versión nueva...");
                _notes.Text = ""; Set(_status, ""); _update.Visible = false; Set(_later, "Cancelar");
                return;
            }
            if (Info == null)
            {
                Set(_title, "No se pudo comprobar si hay actualizaciones.");
                _notes.Text = Lang.T(Error ?? ""); Set(_status, ""); _update.Visible = false; Set(_later, "Cerrar");
                return;
            }
            if (!HasUpdate)
            {
                Set(_title, "Tienes la última versión de IDMClone.");
                _notes.Text = Info.Notes ?? ""; Set(_status, "Versión publicada: " + Updater.Display(Info.Version));
                _update.Visible = false; Set(_later, "Cerrar");
                return;
            }
            Set(_title, "Hay una nueva versión de IDMClone: " + Updater.Display(Info.Version));
            _notes.Text = Info.Notes ?? "";
            _update.Visible = true; _update.Enabled = !Busy;
            Set(_later, Busy ? "Cancelar" : "Más tarde");
            if (!Busy) Set(_status, Error != null ? Lang.T("No se pudo actualizar: ") + Lang.T(Error) : "");
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (Owner == null) { TopMost = true; Activate(); TopMost = false; } // desde la bandeja: delante de todo
            ActiveControl = _update.Visible ? (Control)_update : _later;
            _notes.Select(0, 0);
            if (Info == null && !Busy && Error == null) BeginCheck();
        }

        // Comprueba en segundo plano (Ayuda > Comprobar actualizaciones...). El resultado se anota como la semanal.
        public void BeginCheck()
        {
            if (Busy) return;
            Busy = true; Error = null; ShowState();
            AppConfig cfg = _cfg;
            Thread th = new Thread(delegate()
            {
                UpdateInfo info = null; string err = null;
                try { info = Updater.Fetch(cfg); }
                catch (UpdateException ex) { err = ex.Message; }
                catch (Exception ex) { err = ex.Message; }
                Post(delegate
                {
                    Busy = false; Info = info; Error = err;
                    Updater.RecordCheck(_cfg, DateTime.UtcNow, err == null); _cfg.Save();
                    ShowState();
                });
            });
            th.IsBackground = true; th.Name = "Comprobar actualizaciones";
            th.Start();
        }

        // "Actualizar ahora": descarga y comprueba el instalador; si todo va bien cierra la ventana con OK.
        public void BeginDownload()
        {
            if (Busy || !HasUpdate) return;
            Busy = true; Error = null; _cancel = false;
            _bar.Value = 0; _bar.Style = ProgressBarStyle.Marquee;
            Set(_status, "Descargando el instalador...");
            ShowState();
            AppConfig cfg = _cfg; UpdateInfo info = Info; string exe = _exe;
            Thread th = new Thread(delegate()
            {
                string path = null, err = null;
                int lastPct = -1;
                try
                {
                    path = Updater.Download(cfg, info, Updater.DownloadFolder, exe, delegate(long done, long total)
                    {
                        int pct = total > 0 ? (int)(done * 100 / total) : -1;
                        if (pct == lastPct) return;
                        lastPct = pct;
                        Post(delegate
                        {
                            if (pct < 0) return;
                            _bar.Style = ProgressBarStyle.Continuous; _bar.Value = Math.Min(100, pct);
                        });
                    }, delegate { return _cancel; });
                }
                catch (UpdateException ex) { err = ex.Message; }
                catch (Exception ex) { err = ex.Message; }
                Post(delegate
                {
                    Busy = false;
                    if (_cancel) { ShowState(); return; }
                    if (err != null) { Error = err; ShowState(); return; }
                    InstallerPath = path;
                    Set(_status, "Instalando la actualización...");
                    DialogResult = DialogResult.OK;   // cierra la ventana (también si la abrió una prueba con Show)
                    if (!Modal) Close();
                });
            });
            th.IsBackground = true; th.Name = "Descargar actualización";
            th.Start();
        }

        void Post(MethodInvoker a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // "Cancelar" mientras descarga: se detiene la descarga (el hilo ve _cancel y borra lo descargado).
            if (Busy) _cancel = true;
            base.OnFormClosing(e);
        }
    }
}
