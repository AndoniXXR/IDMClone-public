using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace IDMClone
{
    // Ventanitas que se abren desde Opciones (IDM 6.43, capturas 67 a 71) y la de "Nueva cola" del Programador.

    // Pide un texto: "Introducir nombre de la cola" (Programador > Nueva cola) y "Agregar dirección".
    public class TextInputForm : DialogBase
    {
        TextBox _text;
        public string Value { get { return _text.Text.Trim(); } }

        public TextInputForm(string title, string prompt, string value)
        {
            Text = title;
            ClientSize = new Size(286, 106);
            OptUi.Lbl(this, prompt, 10, 8, 266, 28);
            _text = OptUi.Box(this, value, 10, 42, 266);
            Button ok = OptUi.Btn(this, "Aceptar", 62, 74); ok.DialogResult = DialogResult.OK;
            Button cancel = OptUi.Btn(this, "Cancelar", 148, 74); cancel.DialogResult = DialogResult.Cancel;
            ok.Click += delegate { if (Value.Length == 0) DialogResult = DialogResult.None; };
            AcceptButton = ok; CancelButton = cancel;
            ActiveControl = _text;
            _text.SelectAll();
            EndLayout();
        }

        public void SetValue(string v) { _text.Text = v; }
    }

    // "Usando teclas especiales" (Opciones > General > Teclas...): teclas que se mantienen pulsadas al hacer clic
    // en un enlace del navegador para que IDMClone no capture la descarga o para forzarla.
    public class SpecialKeysForm : DialogBase
    {
        CheckBox _usePrevent, _useForce;
        CheckBox[] _prevent, _force;
        static readonly string[] PreventIds = { "alt", "shift", "ctrl", "del" };
        static readonly string[] ForceIds = { "alt", "shift", "ctrl", "ins" };

        public bool UsePrevent { get; private set; }
        public bool UseForce { get; private set; }
        public string PreventKeys { get; private set; }
        public string ForceKeys { get; private set; }

        public SpecialKeysForm(bool usePrevent, string preventKeys, bool useForce, string forceKeys)
        {
            Text = "Usando teclas especiales";
            ClientSize = new Size(524, 350);
            PreventKeys = string.IsNullOrEmpty(preventKeys) ? "del" : preventKeys;
            ForceKeys = string.IsNullOrEmpty(forceKeys) ? "ins" : forceKeys;
            _usePrevent = OptUi.Chk(this, "Usar la(s) siguiente(s) tecla(s) para prevenir descargas con IDMClone para cualquier enlace:", 12, 10, 500);
            _usePrevent.Checked = usePrevent;
            _prevent = KeyRow(new string[] { "Alt", "Shift", "Ctrl", "Del" }, PreventIds, PreventKeys, 30);
            OptUi.Line(this, 12, 56, 500);
            _useForce = OptUi.Chk(this, "Usar la(s) siguiente(s) tecla(s) para forzar descargas con IDMClone para cualquier enlace:", 12, 64, 500);
            _useForce.Checked = useForce;
            _force = KeyRow(new string[] { "Alt", "Shift", "Ctrl", "Ins" }, ForceIds, ForceKeys, 84);
            OptUi.Lbl(this, "Nota: A veces, después de hacer clic en un enlace de descarga, se carga una página que dice que la descarga empezará en unos segundos. En ese caso, desmarca la siguiente opción para forzar la descarga con IDMClone.", 12, 108, 500, 44);
            CheckBox left = OptUi.Chk(this, "comprobar que se hizo clic izquierdo en un enlace junto con la(s) tecla(s) especial(es) presionadas para forzar la descarga del mismo", 40, 154, 472, 32);
            // La extensión aplica las teclas a cualquier descarga que empiece mientras están pulsadas (enlace, formulario,
            // descarga lanzada por la página...), no solo a los clics en enlaces: como IDM con esta casilla desmarcada.
            left.Checked = false; left.Enabled = false;
            OptUi.Line(this, 12, 188, 500);
            OptUi.Lbl(this, "Nota 2: Algunas veces, al hacer clic en un enlace, el navegador necesita otros archivos al mismo tiempo (páginas web, imágenes, scripts...). Con la tecla para forzar, IDMClone toma también lo que el navegador mostraría (PDF, imágenes, texto), pero nunca las páginas web salvo el enlace pulsado, así que la página sigue funcionando.", 12, 194, 500, 58);
            CheckBox only = OptUi.Chk(this, "Mientras se mantiene presionada una tecla especial, IDMClone NO tomará las descargas como páginas web, imágenes, scripts, entre otros.", 40, 256, 472, 32);
            only.Checked = true; only.Enabled = false;   // siempre es así en IDMClone
            Button ok = OptUi.Btn(this, "Aceptar", 178, 316); ok.DialogResult = DialogResult.OK;
            Button cancel = OptUi.Btn(this, "Cancelar", 270, 316); cancel.DialogResult = DialogResult.Cancel;
            ok.Click += delegate { Save(); };
            AcceptButton = ok; CancelButton = cancel;
            EventHandler upd = delegate
            {
                foreach (CheckBox c in _prevent) c.Enabled = _usePrevent.Checked;
                foreach (CheckBox c in _force) c.Enabled = _useForce.Checked;
            };
            _usePrevent.CheckedChanged += upd; _useForce.CheckedChanged += upd; upd(null, null);
            EndLayout();
        }

        CheckBox[] KeyRow(string[] titles, string[] ids, string current, int y)
        {
            List<string> on = new List<string>(current.ToLowerInvariant().Split('+'));
            CheckBox[] r = new CheckBox[4];
            int[] xs = { 176, 232, 294, 350 };
            for (int i = 0; i < 4; i++)
            {
                r[i] = OptUi.Chk(this, titles[i], xs[i], y, 54);
                r[i].Checked = on.Contains(ids[i]);
                r[i].Tag = Lang.Keep;
            }
            return r;
        }

        static string Combo(CheckBox[] boxes, string[] ids)
        {
            List<string> r = new List<string>();
            for (int i = 0; i < boxes.Length; i++) if (boxes[i].Checked) r.Add(ids[i]);
            return string.Join("+", r.ToArray());
        }

        void Save()
        {
            string p = Combo(_prevent, PreventIds), f = Combo(_force, ForceIds);
            // Sin ninguna tecla marcada la opción queda desactivada (y se conservan las teclas de antes).
            UsePrevent = _usePrevent.Checked && p.Length > 0; if (p.Length > 0) PreventKeys = p;
            UseForce = _useForce.Checked && f.Length > 0; if (f.Length > 0) ForceKeys = f;
        }
    }

    // "Personalizar opciones de IDMClone para menú contextual" (Opciones > General).
    public class BrowserMenuForm : DialogBase
    {
        CheckBox _cLink, _cAll, _fLink, _fAll;
        public bool ChromeLink { get { return _cLink.Checked; } }
        public bool ChromeAll { get { return _cAll.Checked; } }
        public bool FirefoxLink { get { return _fLink.Checked; } }
        public bool FirefoxAll { get { return _fAll.Checked; } }

        public BrowserMenuForm(bool chromeLink, bool chromeAll, bool firefoxLink, bool firefoxAll)
        {
            Text = "Personalizar opciones de IDMClone para menú contextual";
            ClientSize = new Size(420, 182);
            TabControl tabs = OptUi.Add(this, new TabControl(), 8, 8, 404, 132);
            _cLink = Page(tabs, "Chrome, Edge y otros basados en Chromium", out _cAll);
            _fLink = Page(tabs, "Firefox y otros basados en Mozilla", out _fAll);
            _cLink.Checked = chromeLink; _cAll.Checked = chromeAll; _fLink.Checked = firefoxLink; _fAll.Checked = firefoxAll;
            Button ok = OptUi.Btn(this, "Aceptar", 256, 150); ok.DialogResult = DialogResult.OK;
            Button cancel = OptUi.Btn(this, "Cancelar", 337, 150); cancel.DialogResult = DialogResult.Cancel;
            AcceptButton = ok; CancelButton = cancel;
            EndLayout();
        }

        static CheckBox Page(TabControl tabs, string title, out CheckBox all)
        {
            TabPage p = new TabPage(title); p.UseVisualStyleBackColor = true; tabs.TabPages.Add(p);
            OptUi.Lbl(p, "Seleccionar opciones que aparecerán en menú contextual del navegador:", 10, 14, 376);
            CheckBox link = OptUi.Chk(p, "Descargar con IDMClone", 10, 40, 376);
            all = OptUi.Chk(p, "Descargar con IDMClone todos los enlaces", 10, 60, 376);
            return link;
        }
    }

    // "Personalizar ventana del progreso de descarga" (Opciones > Descargas > Editar...).
    public class ProgressWindowForm : DialogBase
    {
        ComboBox _mode;
        CheckBox _limit, _finish, _hide;
        static readonly string[] Modes = { "normal", "minimized", "none" };
        public string Mode { get { return Modes[Math.Max(0, _mode.SelectedIndex)]; } }
        public bool LimitTab { get { return _limit.Checked; } }
        public bool FinishTab { get { return _finish.Checked; } }
        public bool HideButton { get { return _hide.Checked; } }

        public ProgressWindowForm(string mode, bool limitTab, bool finishTab, bool hideButton)
        {
            Text = "Personalizar ventana del progreso de descarga";
            ClientSize = new Size(400, 208);
            OptUi.Lbl(this, "Vista inicial:", 12, 10, 200);
            _mode = OptUi.Combo(this, 12, 28, 170, true, "Mostrar en tamaño normal", "Mostrar minimizado", "No mostrar");
            _mode.SelectedIndex = Math.Max(0, Array.IndexOf(Modes, mode));
            OptUi.Lbl(this, "Nota: La opción \"Vista inicial\" no se aplica a las colas: las descargas que inicia una cola no abren su ventana \"Progreso de descarga\".", 12, 56, 376, 44);
            _limit = OptUi.Chk(this, "Mostrar pestaña \"Límite de velocidad\"", 12, 106, 376); _limit.Checked = limitTab;
            _finish = OptUi.Chk(this, "Mostrar pestaña \"Opciones de finalización\"", 12, 126, 376); _finish.Checked = finishTab;
            _hide = OptUi.Chk(this, "Mostrar botón \"Ocultar pestaña\"", 12, 146, 376); _hide.Checked = hideButton;
            Button ok = OptUi.Btn(this, "Aceptar", 118, 176); ok.DialogResult = DialogResult.OK;
            Button cancel = OptUi.Btn(this, "Cancelar", 208, 176); cancel.DialogResult = DialogResult.Cancel;
            AcceptButton = ok; CancelButton = cancel;
            EndLayout();
        }
    }

    // "La lista de excepciones de dirección" (Opciones > Tipos de archivos > Editar lista...): direcciones
    // (con ruta y comodines) desde las que la extensión no captura descargas.
    public class AddressExceptionsForm : DialogBase
    {
        ListBox _list;
        CheckBox _ask;
        public List<string> Result { get; private set; }
        // "Cancelando una descarga iniciada automáticamente" (AppConfig.AskOnAutoCaptureCancel).
        public bool AskOnCancel { get { return _ask.Checked; } }

        public AddressExceptionsForm(List<string> addresses) : this(addresses, true) { }

        public AddressExceptionsForm(List<string> addresses, bool askOnCancel)
        {
            Text = "La lista de excepciones de dirección";
            ClientSize = new Size(410, 272);
            _ask = OptUi.Chk(this, "Mostrar el cuadro de diálogo para añadir una dirección a la lista de excepciones de una descarga cancelada dos veces", 12, 6, 386, 32);
            _ask.Checked = askOnCancel;
            OptUi.Lbl(this, "No iniciar la descarga automáticamente desde las siguientes direcciones:", 12, 40, 386);
            _list = OptUi.Add(this, new ListBox(), 12, 58, 386, 150);
            _list.IntegralHeight = false; _list.HorizontalScrollbar = true;
            foreach (string a in addresses) _list.Items.Add(a);
            OptUi.Lbl(this, "Puedes usar el asterisco de comodín como patrón de dirección.", 12, 212, 386);
            Button add = OptUi.Btn(this, "Agregar", 20, 240, 80);
            Button del = OptUi.Btn(this, "Eliminar", 130, 240, 80);
            Button ok = OptUi.Btn(this, "Aceptar", 318, 240, 80); ok.DialogResult = DialogResult.OK;
            ok.Click += delegate { Result = new List<string>(); foreach (object o in _list.Items) Result.Add((string)o); };
            AcceptButton = ok;
            add.Click += delegate
            {
                using (TextInputForm f = new TextInputForm("Agregar dirección", "Dirección desde la que no se capturan descargas (con *):", "http://"))
                    if (f.ShowDialog(this) == DialogResult.OK && f.Value.IndexOf(' ') < 0) { _list.Items.Add(f.Value); _list.SelectedIndex = _list.Items.Count - 1; }
            };
            del.Click += delegate { int i = _list.SelectedIndex; if (i >= 0) _list.Items.RemoveAt(i); };
            EventHandler sel = delegate { del.Enabled = _list.SelectedIndex >= 0; };
            _list.SelectedIndexChanged += sel; sel(null, null);
            EndLayout();
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            return base.ProcessDialogKey(keyData);
        }
    }

    // Excepción de Opciones > Conexión: un servidor que admite menos conexiones.
    public class ServerLimitForm : DialogBase
    {
        TextBox _host;
        NumericUpDown _n;
        public string Host { get { return _host.Text.Trim(); } }
        public int Connections { get { return (int)_n.Value; } }

        public ServerLimitForm(string host, int connections)
        {
            Text = "Excepción de conexiones";
            ClientSize = new Size(340, 126);
            OptUi.Lbl(this, "Servidor (p. ej. ejemplo.com o *.cdn.net):", 12, 10, 316);
            _host = OptUi.Box(this, host, 12, 28, 316);
            OptUi.Lbl(this, "Número máximo de conexiones:", 12, 60, 200);
            _n = OptUi.Num(this, 1, 32, connections, 218, 58, 60);
            Button ok = OptUi.Btn(this, "Aceptar", 172, 94); ok.DialogResult = DialogResult.OK;
            Button cancel = OptUi.Btn(this, "Cancelar", 253, 94); cancel.DialogResult = DialogResult.Cancel;
            ok.Click += delegate { if (Host.Length == 0 || Host.IndexOf(' ') >= 0) DialogResult = DialogResult.None; };
            AcceptButton = ok; CancelButton = cancel;
            EndLayout();
        }
    }

    // "Sesión de sitio" (Opciones > Sesiones de sitios > Nuevo / Editar).
    public class SiteLoginForm : DialogBase
    {
        ComboBox _scheme;
        TextBox _site, _user, _pass;
        static readonly string[] Schemes = { "https://", "http://", "ftp://", "ftps://" };

        public string SitePath { get { string s = _site.Text.Trim(); return s.Length == 0 ? "" : (string)_scheme.SelectedItem + s; } }
        public string User { get { return _user.Text.Trim(); } }
        public string Password { get { return _pass.Text; } }

        public SiteLoginForm(string site, string user, string password)
        {
            Text = "Sesión de sitio";
            ClientSize = new Size(445, 142);
            OptUi.Lbl(this, "Servidor/ruta", 88, 8, 200);
            _scheme = OptUi.Combo(this, 8, 26, 74, false, Schemes);
            _scheme.Tag = Lang.Keep;
            _site = OptUi.Box(this, "", 88, 26, 348);
            string s = site ?? "";
            int idx = 0;
            for (int i = 0; i < Schemes.Length; i++)
                if (s.StartsWith(Schemes[i], StringComparison.OrdinalIgnoreCase)) { idx = i; s = s.Substring(Schemes[i].Length); break; }
            _scheme.SelectedIndex = idx; _site.Text = s;
            OptUi.Lbl(this, "nota: escribe la ruta sólo si tienes diferentes nombres de usuario para diferentes directorios del servidor.", 88, 50, 348, 30);
            OptUi.LblRight(this, "Nombre de usuario", 30, 88, 150);
            _user = OptUi.Box(this, user, 186, 86, 140);
            OptUi.LblRight(this, "Contraseña", 30, 114, 150);
            _pass = OptUi.Box(this, password, 186, 112, 140); _pass.UseSystemPasswordChar = true;
            Button ok = OptUi.Btn(this, "Aceptar", 360, 84); ok.DialogResult = DialogResult.OK;
            Button cancel = OptUi.Btn(this, "Cancelar", 360, 112); cancel.DialogResult = DialogResult.Cancel;
            ok.Click += delegate { if (_site.Text.Trim().Length == 0 || User.Length == 0) DialogResult = DialogResult.None; };
            AcceptButton = ok; CancelButton = cancel;
            ActiveControl = _site;
            EndLayout();
        }
    }

    // Un proxy de "Avanzado... / Socks...": casilla "Usar Proxy X", servidor, puerto, usuario y contraseña.
    public class ProxyServerFields
    {
        public bool On;
        public string Host = "", User = "", Password = "";
        public int Port = 8080;
        public ProxyServerFields() { }
        public ProxyServerFields(bool on, string host, int port, string user, string password)
        {
            On = on; Host = (host ?? "").Trim(); Port = port >= 1 && port <= 65535 ? port : 8080; User = user ?? ""; Password = password ?? "";
        }
        public bool SameServer(ProxyServerFields o)
        {
            return o != null && string.Equals(Host, o.Host, StringComparison.OrdinalIgnoreCase) && Port == o.Port && User == o.User && Password == o.Password;
        }
    }

    // Opciones > Proxy/Socks > "Avanzado... / Socks..." ("Configuración avanzada del Proxy" de IDM 6.43, captura 91):
    // un proxy HTTP para cada protocolo (http, https, ftp), el servidor SOCKS (Socks 5 / Socks 4 y "Proxy DNS"),
    // "Obtener del sistema" y, además (IDM no la enseña aquí), la lista de direcciones que no usan el proxy.
    public class ProxyAdvancedForm : DialogBase
    {
        TextBox _bypass, _sHost, _sPort, _sUser, _sPass;
        CheckBox _sOn, _sDns;
        RadioButton _s5, _s4;
        readonly CheckBox[] _pOn = new CheckBox[3];
        readonly TextBox[] _pHost = new TextBox[3], _pPort = new TextBox[3], _pUser = new TextBox[3], _pPass = new TextBox[3];

        public string Bypass { get { return _bypass.Text.Trim(); } }
        public bool SocksEnabled { get { return _sOn.Checked && SocksHost.Length > 0; } }
        // "5", "4" o "4a" (= Socks 4 con Proxy DNS).
        public string SocksType { get { return _s4.Checked ? (_sDns.Checked ? "4a" : "4") : "5"; } }
        public bool SocksProxyDns { get { return _sDns.Checked; } }
        public string SocksHost { get { return _sHost.Text.Trim(); } }
        public int SocksPort { get { return PortOf(_sPort, 1080); } }
        public string SocksUser { get { return _sUser.Text.Trim(); } }
        public string SocksPassword { get { return _sPass.Text; } }
        public ProxyServerFields Http { get { return Fields(0); } }
        public ProxyServerFields Https { get { return Fields(1); } }
        public ProxyServerFields Ftp { get { return Fields(2); } }

        static int PortOf(TextBox t, int def) { int p; return int.TryParse(t.Text.Trim(), out p) && p > 0 && p <= 65535 ? p : def; }

        ProxyServerFields Fields(int i)
        {
            return new ProxyServerFields(_pOn[i].Checked, _pHost[i].Text, PortOf(_pPort[i], 8080), _pUser[i].Text.Trim(), _pPass[i].Text);
        }

        public ProxyAdvancedForm(string bypass) : this(bypass, false, "5", "", 1080, "", "") { }

        public ProxyAdvancedForm(string bypass, bool socks, string type, string host, int port, string user, string password)
            : this(bypass, new ProxyServerFields(true, "", 8080, "", ""), new ProxyServerFields(true, "", 8080, "", ""), new ProxyServerFields(true, "", 8080, "", ""),
                   socks, type, false, host, port, user, password) { }

        public ProxyAdvancedForm(string bypass, ProxyServerFields http, ProxyServerFields https, ProxyServerFields ftp,
                                 bool socks, string type, bool proxyDns, string host, int port, string user, string password)
        {
            Text = "Configuración avanzada del Proxy";
            ClientSize = new Size(384, 470);
            string[] titles = { "Usar Proxy HTTP", "Usar Proxy HTTPS", "Usar Proxy ftp" };
            ProxyServerFields[] values = { http ?? new ProxyServerFields(), https ?? new ProxyServerFields(), ftp ?? new ProxyServerFields() };
            for (int i = 0; i < 3; i++)
            {
                CheckBox on;
                GroupBox g = OptUi.CheckGroup(this, out on, titles[i], 10, 6 + i * 68, 364, 62);
                _pOn[i] = on;
                Label[] ls = ServerLabels(g, "Proxy");
                _pHost[i] = OptUi.Box(g, values[i].Host, 10, 34, 130);
                _pPort[i] = OptUi.Box(g, values[i].Port.ToString(), 146, 34, 50);
                _pUser[i] = OptUi.Box(g, values[i].User, 202, 34, 74);
                _pPass[i] = OptUi.Box(g, values[i].Password, 282, 34, 72); _pPass[i].UseSystemPasswordChar = true;
                on.Checked = values[i].On;
                int k = i;
                EventHandler upd = delegate { OptUi.Enable(_pOn[k].Checked, ls[0], ls[1], ls[2], ls[3], _pHost[k], _pPort[k], _pUser[k], _pPass[k]); };
                on.CheckedChanged += upd; upd(null, null);
            }

            GroupBox sg = OptUi.CheckGroup(this, out _sOn, "Usar socks", 10, 212, 364, 106);
            Label[] sl = ServerLabels(sg, "Dirección del servidor");
            _sHost = OptUi.Box(sg, host ?? "", 10, 34, 130);
            _sPort = OptUi.Box(sg, (port > 0 ? port : 1080).ToString(), 146, 34, 50);
            _sUser = OptUi.Box(sg, user ?? "", 202, 34, 74);
            _sPass = OptUi.Box(sg, password ?? "", 282, 34, 72); _sPass.UseSystemPasswordChar = true;
            _s5 = OptUi.Radio(sg, "Socks 5", 10, 62, 90); _s5.Tag = Lang.Keep;
            _s4 = OptUi.Radio(sg, "Socks 4", 10, 82, 90); _s4.Tag = Lang.Keep;
            _sDns = OptUi.Chk(sg, "Proxy DNS", 110, 62, 120); _sDns.Tag = Lang.Keep;
            string t = (type ?? "").Trim().ToLowerInvariant();
            if (t.StartsWith("socks")) t = t.Substring(5);
            if (t == "4" || t == "4a") _s4.Checked = true; else _s5.Checked = true;   // desconocido -> Socks 5
            _sDns.Checked = proxyDns || t == "4a";
            _sOn.Checked = socks;
            OptUi.Note(this, "Nota: IDMClone sólo usará socks para los protocolos que no estén marcados arriba. El FTP no pasa por socks.", 10, 322, 364, 28);

            OptUi.Lbl(this, "No usar el proxy para las siguientes direcciones (separadas por ';'):", 10, 354, 364);
            _bypass = OptUi.Box(this, bypass, 10, 372, 364);
            Button fromSys = OptUi.Btn(this, "Obtener del sistema", 10, 404, 130);
            fromSys.Click += delegate { FromSystem(); };

            Button ok = OptUi.Btn(this, "Aceptar", 218, 438); ok.DialogResult = DialogResult.OK;
            Button cancel = OptUi.Btn(this, "Cancelar", 299, 438); cancel.DialogResult = DialogResult.Cancel;
            AcceptButton = ok; CancelButton = cancel;
            ok.Click += delegate
            {
                for (int i = 0; i < 3; i++)
                {
                    if (!_pOn[i].Checked || _pHost[i].Text.Trim().Length == 0) continue;
                    if (!ValidPort(_pPort[i], "El puerto del proxy debe ser un número entre 1 y 65535.")) return;
                }
                if (!_sOn.Checked) return;
                if (SocksHost.Length == 0)
                {
                    Msg.Show(this, "Escribe el servidor SOCKS o desmarca \"Usar socks\".", "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    DialogResult = DialogResult.None;
                }
                else ValidPort(_sPort, "El puerto SOCKS debe ser un número entre 1 y 65535.");
            };
            EventHandler supd = delegate { OptUi.Enable(_sOn.Checked, sl[0], sl[1], sl[2], sl[3], _sHost, _sPort, _sUser, _sPass, _s5, _s4, _sDns); };
            _sOn.CheckedChanged += supd; supd(null, null);
            EndLayout();
        }

        // Cabecera de las columnas de un recuadro: servidor, "Puerto", "Usuario", "Contraseña".
        static Label[] ServerLabels(GroupBox g, string first)
        {
            return new Label[] { OptUi.Lbl(g, first, 10, 17, 130), OptUi.Lbl(g, "Puerto", 146, 17, 50), OptUi.Lbl(g, "Usuario", 202, 17, 74), OptUi.Lbl(g, "Contraseña", 282, 17, 72) };
        }

        bool ValidPort(TextBox box, string message)
        {
            int p;
            if (int.TryParse(box.Text.Trim(), out p) && p >= 1 && p <= 65535) return true;
            Msg.Show(this, message, "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.None;
            box.Focus(); box.SelectAll();
            return false;
        }

        // "Obtener del sistema": el proxy de Windows para cada protocolo (y su SOCKS) en los recuadros.
        void FromSystem()
        {
            string server, bypass;
            OptionsForm.ReadWindowsProxy(out server, out bypass);
            string[] per = new string[3];
            string socks;
            OptionsForm.ParseWindowsProxy(server, out per[0], out per[1], out per[2], out socks);
            if (per[0] == null && per[1] == null && per[2] == null && socks == null)
            {
                Msg.Show(this, "Windows no tiene configurado ningún proxy.", "IDMClone", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            for (int i = 0; i < 3; i++)
            {
                string h = null; int p = 0;
                bool has = per[i] != null && OptionsForm.SplitHostPort(per[i], out h, out p);
                _pOn[i].Checked = has;
                if (!has) continue;
                _pHost[i].Text = h; if (p > 0) _pPort[i].Text = p.ToString();
            }
            string sh; int sp;
            // "socks=" de Windows: WinINet lo usa como SOCKS4 (los servidores SOCKS suelen admitir 4, 4a y 5).
            if (socks != null && OptionsForm.SplitHostPort(socks, out sh, out sp)) { _sOn.Checked = true; _s4.Checked = true; _sHost.Text = sh; _sPort.Text = (sp > 0 ? sp : 1080).ToString(); }
            else _sOn.Checked = false;
            if (!string.IsNullOrEmpty(bypass)) _bypass.Text = bypass.Trim();
        }
    }
}
