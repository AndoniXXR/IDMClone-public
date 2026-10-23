namespace IDMClone
{
    // Opciones nuevas de las ventanas "Configuración de IDMClone" y "Programador" (como las de IDM 6.43).
    // Los valores por defecto que no son 0/false/null van en el campo de respaldo: así las configuraciones
    // antiguas (sin estas claves) cargan con el valor de siempre.
    public partial class AppConfig
    {
        // General > "Capturar descargas para los siguientes navegadores": nombres (como los de
        // ExtensionSeen.DisplayName) separados por ';' en los que NO se capturan descargas.
        string _captureBrowsersOff = "";
        public string CaptureBrowsersOff { get { return _captureBrowsersOff; } set { _captureBrowsersOff = value ?? ""; } }
        // Navegadores añadidos a la lista con "Añadir navegador..." (separados por ';').
        string _captureBrowsersExtra = "";
        public string CaptureBrowsersExtra { get { return _captureBrowsersExtra; } set { _captureBrowsersExtra = value ?? ""; } }

        // General > "Personalizar opciones de IDMClone para menú contextual" (Chromium y Firefox por separado).
        bool _menuChromeLink = true, _menuChromeAll = false, _menuFirefoxLink = true, _menuFirefoxAll = false; // como IDM: "todos los enlaces" desmarcado
        public bool BrowserMenuDownload { get { return _menuChromeLink; } set { _menuChromeLink = value; } }
        public bool BrowserMenuDownloadAll { get { return _menuChromeAll; } set { _menuChromeAll = value; } }
        public bool FirefoxMenuDownload { get { return _menuFirefoxLink; } set { _menuFirefoxLink = value; } }
        public bool FirefoxMenuDownloadAll { get { return _menuFirefoxAll; } set { _menuFirefoxAll = value; } }

        // Descargas > "Personalizar ventana del progreso de descarga".
        bool _progressLimitTab = true, _progressFinishTab = true, _progressHideTabs = true;
        public bool ProgressShowLimitTab { get { return _progressLimitTab; } set { _progressLimitTab = value; } }
        public bool ProgressShowFinishTab { get { return _progressFinishTab; } set { _progressFinishTab = value; } }
        public bool ProgressShowHideTabsButton { get { return _progressHideTabs; } set { _progressHideTabs = value; } }

        // Proxy/Socks > "usar FTP en modo PASV". IDM viene sin marcar (modo activo); IDMClone, marcado: el modo activo
        // falla detrás de casi cualquier router. Si un modo falla, el motor reintenta una vez con el otro.
        bool _ftpPassive = true;
        public bool FtpPassive { get { return _ftpPassive; } set { _ftpPassive = value; } }

        // Proxy/Socks > "Usar este proxy para los siguientes protocolos: http https ftp" (modo manual) y las casillas
        // "Usar Proxy HTTP / HTTPS / ftp" de "Avanzado... / Socks..." (son las mismas). Desmarcado = ese protocolo va
        // por SOCKS si está activo y, si no, directo.
        bool _proxyHttp = true, _proxyHttps = true, _proxyFtp = true;
        public bool ProxyHttp { get { return _proxyHttp; } set { _proxyHttp = value; } }
        public bool ProxyHttps { get { return _proxyHttps; } set { _proxyHttps = value; } }
        public bool ProxyFtp { get { return _proxyFtp; } set { _proxyFtp = value; } }

        // "Avanzado... / Socks...": servidor propio para https y para ftp (servidor, puerto, usuario, contraseña),
        // como en IDM. Servidor vacío = el mismo de la página Proxy/Socks (ProxyHost, que es el de http).
        // El FTP va por el proxy HTTP con "GET ftp://..." (proxy "CERN", como Squid); ftps:// va siempre directo.
        string _proxyHttpsHost = "", _proxyFtpHost = "";
        public string ProxyHttpsHost { get { return _proxyHttpsHost; } set { _proxyHttpsHost = (value ?? "").Trim(); } }
        int _proxyHttpsPort = 8080;
        public int ProxyHttpsPort { get { return _proxyHttpsPort; } set { _proxyHttpsPort = value >= 1 && value <= 65535 ? value : 8080; } }
        public string ProxyHttpsUser { get; set; }
        public string ProxyHttpsPasswordEnc { get; set; }   // cifrada con Util.Protect, como ProxyPasswordEnc
        public string ProxyFtpHost { get { return _proxyFtpHost; } set { _proxyFtpHost = (value ?? "").Trim(); } }
        int _proxyFtpPort = 8080;
        public int ProxyFtpPort { get { return _proxyFtpPort; } set { _proxyFtpPort = value >= 1 && value <= 65535 ? value : 8080; } }
        public string ProxyFtpUser { get; set; }
        public string ProxyFtpPasswordEnc { get; set; }

        // Proxy/Socks > "Avanzado... / Socks...": servidor SOCKS (modo manual). Como en IDM, solo se usa para los
        // protocolos que no van por un proxy HTTP (casilla desmarcada o sin servidor); ver Socks.cs. El FTP no pasa
        // por SOCKS (FtpWebRequest no puede): si no va por el proxy HTTP, va directo.
        public bool SocksEnabled { get; set; }
        // "Proxy DNS": los nombres los resuelve el servidor SOCKS (SOCKS5 con nombre, SOCKS4a). Desmarcado (como en
        // IDM), este equipo resuelve el nombre y manda la IP. El tipo "4a" antiguo implica Proxy DNS.
        public bool SocksProxyDns { get; set; }
        string _socksType = "5";
        // "4", "4a" o "5" (admite también "SOCKS5", "socks4a"...).
        public string SocksType
        {
            get { return _socksType; }
            set
            {
                string v = (value ?? "").Trim().ToLowerInvariant();
                if (v.StartsWith("socks")) v = v.Substring(5);
                _socksType = v == "4" || v == "4a" ? v : "5";
            }
        }
        string _socksHost = "";
        public string SocksHost { get { return _socksHost; } set { _socksHost = (value ?? "").Trim(); } }
        int _socksPort = 1080;
        public int SocksPort { get { return _socksPort; } set { _socksPort = value >= 1 && value <= 65535 ? value : 1080; } }
        public string SocksUser { get; set; }
        public string SocksPasswordEnc { get; set; }   // cifrada con Util.Protect, como ProxyPasswordEnc

        // Proxy/Socks > "Usar proxy/socks desde la petición de un navegador si hay errores de descarga para las
        // descargas interceptadas desde el navegador": si una descarga del navegador falla, se reintenta con el proxy
        // que usó el navegador (DownloadItem.BrowserProxy). Marcada por defecto, como en IDM.
        bool _useBrowserProxy = true;
        public bool UseBrowserProxyOnError { get { return _useBrowserProxy; } set { _useBrowserProxy = value; } }

        // Descargas: "No iniciar la descarga, sólo añadir archivos a la cola" (sin el diálogo de inicio).
        public bool AddToQueueWithoutDialog { get; set; }
        // Descargas: "Mostrar panel de selección de colas al añadir descargas por lotes".
        bool _queuePickerBatch = true;
        public bool ShowQueuePickerBatch { get { return _queuePickerBatch; } set { _queuePickerBatch = value; } }
        // Descargas: "Ignorar los cambios de la hora del archivo cuando se continúe una descarga".
        public bool IgnoreDateChangesOnResume { get; set; }
        // Guardar: "Establecer la fecha del archivo como la proporcionada por el servidor".
        public bool SetFileDateFromServer { get; set; }

        // Conexión / Programador > Límites de descarga: "Mostrar advertencia antes de detener descargas".
        bool _quotaWarning = true;
        public bool QuotaWarning { get { return _quotaWarning; } set { _quotaWarning = value; } }

        // Ventana "Cancelando una descarga iniciada automáticamente" (al cancelar otra vez una captura del navegador del
        // mismo sitio, ofrece añadirlo a las excepciones). Su casilla "No mostrar este diálogo otra vez" la desmarca.
        bool _askOnAutoCancel = true;
        public bool AskOnAutoCaptureCancel { get { return _askOnAutoCancel; } set { _askOnAutoCancel = value; } }

        // Añade un sitio o un patrón de direcciones a las excepciones de captura (Opciones > Tipos de archivo) si no está.
        public bool AddCaptureExclusion(string entry)
        {
            entry = (entry ?? "").Trim();
            if (entry.Length == 0 || entry.IndexOf(' ') >= 0) return false;
            string cur = CaptureExcludeSites ?? "";
            foreach (string s in cur.Split(new char[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries))
                if (string.Equals(s, entry, System.StringComparison.OrdinalIgnoreCase)) return false;
            CaptureExcludeSites = (cur.Trim() + " " + entry).Trim();
            return true;
        }
    }
}
