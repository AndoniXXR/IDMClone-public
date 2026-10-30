using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace IDMClone
{
    // El motor: descarga un DownloadItem con segmentación dinámica y robo de trabajo.
    // Reproduce la "división en mitades" de IDM: cuando una conexión queda libre,
    // parte por la mitad el mayor segmento que aún queda por descargar.
    // Protocolos: HTTP/HTTPS (Range + If-Range) y FTP (REST, modo pasivo o activo).
    public partial class DownloadEngine
    {
        const int BufferSize = 64 * 1024;
        const int MaxChangedRestarts = 2;

        static DownloadEngine() { ConfigureNetwork(); }

        // Ajustes globales de red. TLS 1.2/1.3 explícitos: sin ellos .NET Framework puede quedarse
        // en SSL3/TLS 1.0 y no conectar con ningún servidor HTTPS actual.
        public static void ConfigureNetwork()
        {
            ServicePointManager.DefaultConnectionLimit = 512;
            ServicePointManager.Expect100Continue = false;
            const SecurityProtocolType Tls13 = (SecurityProtocolType)12288;
            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | Tls13; }
            catch (NotSupportedException) { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
            DigestAuth.Register();
            HappyEyeballs.Install(); // IPv6 que no responde: no esperar 21 s como .NET (los navegadores e IDM no lo hacen)
        }

        readonly DownloadItem _item;
        readonly AppConfig _cfg;
        readonly bool _ftp, _ftps; // ftps:// = FTP con cifrado TLS explícito (AUTH TLS)
        // ftp:// por el proxy HTTP de las opciones (casilla ftp): la descarga sigue el camino HTTP ("GET ftp://..." al
        // proxy, con Range); _ftp queda en false. null = FTP directo con FtpWebRequest.
        readonly WebProxy _ftpProxy;
        volatile bool _stop;
        volatile bool _running;
        volatile bool _rangeBroken;          // el servidor dejó de respetar Range
        volatile bool _changed;              // el archivo cambió en el servidor: hay que empezar de nuevo
        volatile string _fatal;              // error definitivo: no tiene sentido reintentar
        string _lastError;                   // último error de una conexión
        string _probeContentType;
        readonly ManualResetEvent _exited = new ManualResetEvent(true);
        readonly List<Thread> _threads = new List<Thread>();
        readonly List<WebRequest> _active = new List<WebRequest>();

        // Tabla de conexiones de la ventana de progreso de IDM: "N.", "Descargado", "Info".
        readonly List<ConnectionInfo> _conns = new List<ConnectionInfo>();
        ConnectionInfo _firstConn;
        readonly TokenBucket _itemBucket = new TokenBucket(); // limitador de esta descarga

        public ConnectionInfo[] Connections
        {
            get
            {
                lock (_conns)
                {
                    ConnectionInfo[] r = new ConnectionInfo[_conns.Count];
                    for (int i = 0; i < r.Length; i++) r[i] = _conns[i].Clone();
                    return r;
                }
            }
        }

        ConnectionInfo NewConnection(string info)
        {
            lock (_conns)
            {
                ConnectionInfo c = new ConnectionInfo();
                c.Number = _conns.Count + 1;
                c.Info = info;
                _conns.Add(c);
                return c;
            }
        }

        static void SetInfo(ConnectionInfo c, string info) { if (c != null) c.Info = info; }

        public event Action<DownloadItem> Finished;   // completa, pausada o error
        public event Action<DownloadItem> PathChanged; // el servidor dio otro nombre de archivo

        // Reserva una ruta libre (la da el gestor, que conoce las rutas de las demás descargas).
        public Func<string, string> PathReserver;
        // Una ruta libre sin apartarla con un archivo vacío (para mover ahí el archivo terminado).
        public Func<string, string> FreePath;

        public DownloadEngine(DownloadItem item, AppConfig cfg)
        {
            _item = item; _cfg = cfg;
            _ftps = item.Url != null && item.Url.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase);
            bool ftpUrl = _ftps || item.Url != null && item.Url.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);
            // ftps:// nunca por proxy HTTP: el proxy no puede cifrar la conexión con el servidor FTP.
            if (ftpUrl && !_ftps) { try { _ftpProxy = NetSettings.FtpProxyFor(cfg, new Uri(item.Url)); } catch (UriFormatException) { } }
            _ftp = ftpUrl && _ftpProxy == null;
            if (!ftpUrl) SetupCookies();
        }

        CookieContainer _jar;
        string _rawCookies;

        // Las cookies del navegador ("a=1; b=2") van al contenedor, ligadas al servidor de la descarga.
        // Si alguna no es válida para .NET (comas, etc.), se mandan tal cual y solo a ese servidor.
        void SetupCookies()
        {
            CookieContainer jar = new CookieContainer();
            if (!string.IsNullOrEmpty(_item.Cookies))
            {
                try
                {
                    Uri u = new Uri(_item.Url);
                    foreach (string part in _item.Cookies.Split(';'))
                    {
                        int eq = part.IndexOf('=');
                        if (eq <= 0) continue;
                        jar.Add(u, new Cookie(part.Substring(0, eq).Trim(), part.Substring(eq + 1).Trim(), "/"));
                    }
                }
                catch (Exception)
                {
                    _rawCookies = _item.Cookies;
                    _jar = null;
                    return;
                }
            }
            _jar = jar;
        }

        public bool IsRunning { get { return _running; } }
        public bool StopRequested { get { return _stop; } }

        public void Start()
        {
            _stop = false;
            _running = true;
            _exited.Reset();
            Thread t = new Thread(Run);
            t.IsBackground = true;
            t.Name = "idm-" + _item.Id;
            t.Start();
        }

        // Pide parar y corta las conexiones abiertas para que la pausa sea inmediata.
        public void Stop()
        {
            _stop = true;
            AbortActive();
        }

        public bool WaitForExit(int milliseconds) { return _exited.WaitOne(milliseconds); }

        // Traza de depuración opcional (variable de entorno IDMCLONE_TRACE=1 -> stderr).
        static readonly bool TraceOn = Environment.GetEnvironmentVariable("IDMCLONE_TRACE") == "1";
        void Trace(string msg) { if (TraceOn) Console.Error.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + " " + Thread.CurrentThread.Name + "] " + msg); }

        bool Halted { get { return _stop || _fatal != null || _rangeBroken || _changed; } }

        void Run()
        {
            try
            {
                RunCore();
            }
            catch (Exception ex)
            {
                if (_stop) _item.Status = DownloadStatus.Paused;
                else { _item.Error = Describe(ex); _item.Status = DownloadStatus.Error; }
                _item.SaveState();
            }
            finally
            {
                _running = false;
                try { Fire(); }
                catch { } // los avisos (lista, cola, Grabber) no pueden tumbar el proceso desde este hilo
                finally { _exited.Set(); }
            }
        }

        void RunCore()
        {
            if (_item.IsMedia) { RunMedia(); return; }
            _item.Error = null;
            _item.Status = DownloadStatus.Connecting;
            int restarts = 0;
            Trace("inicio: " + _item.Url);

            while (true)
            {
                // Reanudación: si el archivo parcial ya no está o no cuadra, lo descargado no vale.
                if (_item.Segments.Count > 0 && !PartialFileMatches())
                    lock (_item.Sync) _item.Segments.Clear();

                // 1) Descarga nueva. Como IDM: la primera conexión pide el archivo entero y empieza a
                //    escribir datos en cuanto llegan; su respuesta da el tamaño, el nombre y si admite
                //    rangos. Las demás conexiones se abren a la vez y van partiendo por la mitad lo que queda.
                WebResponse first = null;
                WebRequest firstReq = null;
                if (_item.Segments.Count == 0)
                {
                    if (_ftp)
                    {
                        Probe();
                        if (_stop) { _item.Status = DownloadStatus.Paused; return; }
                        PrepareWorkFile();
                        InitSegments(true);
                    }
                    else
                    {
                        first = OpenFirst(out firstReq);
                        if (_stop || first == null)
                        {
                            if (first != null) { try { firstReq.Abort(); first.Close(); } catch { } }
                            _item.Status = DownloadStatus.Paused;
                            return;
                        }
                        try { PrepareWorkFile(); InitSegments(false); }
                        catch { try { firstReq.Abort(); first.Close(); } catch { } throw; }
                    }
                    _item.SaveState();
                }

                _item.Status = DownloadStatus.Downloading;

                // 2) Descargar con varias conexiones.
                RunWorkers(first, firstReq);

                // 2b) El servidor dejó de respetar Range a mitad: empezar de nuevo con un solo flujo
                //     (como IDM cuando el servidor no permite reanudar).
                if (_rangeBroken && !_stop && _fatal == null && !_changed)
                {
                    lock (_item.Sync)
                    {
                        _item.Segments.Clear();
                        _item.Resumable = false;
                        _item.Segments.Add(new Segment(0, 0, _item.Size > 0 ? _item.Size : long.MaxValue));
                    }
                    _rangeBroken = false;
                    _lastError = null;
                    _item.SaveState();
                    RunWorkers(null, null);
                }

                // 2c) El archivo cambió en el servidor (otro ETag/tamaño): lo bajado no vale.
                if (_changed && !_stop && _fatal == null)
                {
                    if (++restarts > MaxChangedRestarts)
                    {
                        _fatal = "El archivo cambia continuamente en el servidor; no se puede descargar de forma fiable.";
                        break;
                    }
                    lock (_item.Sync) _item.Segments.Clear();
                    _item.ETag = null; _item.LastModified = null;
                    _changed = false; _rangeBroken = false; _lastError = null;
                    continue;
                }
                break;
            }

            if (_stop)
            {
                _item.Status = DownloadStatus.Paused;
                _item.SaveState();
                return;
            }

            if (_fatal != null)
            {
                _item.Error = _fatal;
                _item.Status = DownloadStatus.Error;
                _item.SaveState();
                return;
            }

            // 3) ¿Completado? Lo decide lo que falta por bajar, no si alguna conexión falló.
            bool allDone = true;
            lock (_item.Sync)
                foreach (Segment s in _item.Segments)
                    if (s.End != long.MaxValue && s.Current < s.End) { allDone = false; break; }

            if (allDone)
            {
                // Ajustar longitud real si el tamaño era desconocido o cero (flujo único).
                if (_item.Size <= 0)
                {
                    long got = _item.Downloaded;
                    using (FileStream fs = new FileStream(WorkPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                        fs.SetLength(got);
                    _item.Size = got;
                }
                // Carpeta temporal: el archivo pasa a su destino ya completo (como el ensamblado de IDM). Con
                // "Información del archivo" aún abierto se queda en la temporal hasta que el usuario confirme.
                if (!string.IsNullOrEmpty(_item.TempPath))
                {
                    if (HoldIfPending()) return; // lo coloca Confirm (con la fecha del servidor)
                    MoveToDestination();
                }
                // "Establecer la fecha del archivo como la del servidor": ya en su sitio (copiar entre unidades
                // pone la fecha de creación a "ahora").
                if (_cfg.SetFileDateFromServer) ApplyServerDate(_item.FilePath, _item.LastModified);
                _item.Status = DownloadStatus.Complete;
                _item.DeleteState();
            }
            else
            {
                _item.Status = DownloadStatus.Error;
                _item.Error = string.IsNullOrEmpty(_lastError) ? "Descarga incompleta" : _lastError;
                _item.SaveState();
            }
        }

        void Fire() { Action<DownloadItem> h = Finished; if (h != null) h(_item); }

        // Límite de velocidad de esta descarga (pestaña "Límite de velocidad" de la ventana de progreso). 0 = sin límite.
        int ItemSpeedLimit { get { return _item.SpeedLimitOn ? Math.Max(1, _item.SpeedLimitKBps) : 0; } }

        // Archivo donde se escribe: el temporal (si se usa la carpeta temporal) o el de destino.
        string WorkPath { get { return _item.WorkingPath; } }

        bool PartialFileMatches()
        {
            try
            {
                if (!File.Exists(WorkPath)) return false;
                if (_item.Size > 0) return new FileInfo(WorkPath).Length == _item.Size;
                return true;
            }
            catch { return false; }
        }

        // Elige el archivo de trabajo. Con carpeta temporal: <temporal>\<id>\<nombre corto> (IDM: DwnlData, también con nombres cortos).
        void PrepareWorkFile()
        {
            if (_cfg.UseTempFolder)
            {
                if (string.IsNullOrEmpty(_item.TempPath) || !File.Exists(_item.TempPath))
                {
                    string dir = Path.Combine(_cfg.TempFolderOrDefault, _item.Id);
                    Directory.CreateDirectory(dir);
                    _item.TempPath = Path.Combine(dir, Util.WorkFileName(_item.FileName));
                }
                // Hueco vacío que pudo quedar con el nombre provisional en el destino.
                try { if (File.Exists(_item.FilePath) && new FileInfo(_item.FilePath).Length == 0) File.Delete(_item.FilePath); } catch { }
            }
            else
            {
                _item.TempPath = null;
                // "Sobrescribir" sin carpeta temporal: el archivo anterior se borra ahora que la descarga nueva
                // empieza de verdad (no antes: si se cancela, el usuario lo conserva).
                if (_item.Overwrite)
                {
                    if (File.Exists(_item.FilePath)) File.Delete(_item.FilePath);
                    _item.Overwrite = false;
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(WorkPath));
        }

        // Terminó mientras se muestra "Información del archivo" (la descarga empezó con el diálogo abierto): como IDM, el
        // archivo NO pasa a la carpeta de destino, que aún puede cambiar o no confirmarse; se queda en la temporal
        // (HeldInTemp) y DownloadManager.Confirm lo coloca donde diga el usuario. Cancelar lo borra de ahí. Bajo el
        // cerrojo de la descarga, con Confirm: o lo coloca este motor o lo coloca Confirm, nunca ninguno.
        bool HoldIfPending()
        {
            lock (_item.Sync)
            {
                if (!_item.Pending) return false;
                _item.HeldInTemp = true;
                _item.Status = DownloadStatus.Complete;
            }
            return true;
        }

        // Mueve el archivo terminado de la carpeta temporal a su destino. Si mientras tanto apareció otro
        // archivo con ese nombre, usa uno libre (nombre_1...).
        void MoveToDestination()
        {
            if (PlaceFinished(_item, _cfg, FreePath)) { Action<DownloadItem> h = PathChanged; if (h != null) h(_item); }
        }

        // El paso final, también para una descarga retenida en la temporal al confirmarla: la mueve a su FilePath (con
        // "Sobrescribir", sustituyendo al que hubiera). true = acabó con otro nombre porque ya había un archivo con ese.
        internal static bool PlaceFinished(DownloadItem it, AppConfig cfg, Func<string, string> reserver)
        {
            string temp = it.TempPath, tempDir = Path.GetDirectoryName(temp);
            string dest = it.FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            // "Redescargar" o "Sí, reemplazar": la versión nueva sustituye a la anterior (como IDM). La anterior se aparta y
            // solo se borra cuando la nueva ya está en su sitio: si no se puede mover (disco lleno...), vuelve a su nombre.
            string old = null;
            if (it.Overwrite && File.Exists(dest))
            {
                old = Path.Combine(Path.GetDirectoryName(dest), "~" + it.Id + ".idmclone-viejo"); // corto: dest + sufijo podía pasarse del límite de ruta
                try { if (File.Exists(old)) File.Delete(old); File.Move(dest, old); }
                catch (Exception) { old = null; try { File.Delete(dest); } catch { } } // no se deja apartar (ruta muy larga...)
            }
            try
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    if (File.Exists(dest)) dest = reserver != null ? reserver(dest) : Util.UniquePath(dest);
                    try { MoveFile(temp, dest); break; }
                    catch (IOException) { if (!File.Exists(temp)) throw; if (attempt == 4) throw; }
                    Thread.Sleep(200); // p. ej. el antivirus lo tiene abierto un momento
                }
            }
            catch
            {
                if (old != null && !File.Exists(it.FilePath)) { try { File.Move(old, it.FilePath); } catch { } }
                throw;
            }
            if (old != null) { try { File.Delete(old); } catch { } }
            bool renamed = !string.Equals(dest, it.FilePath, StringComparison.OrdinalIgnoreCase);
            it.FilePath = dest;
            it.TempPath = null;
            it.Overwrite = false;
            it.HeldInTemp = false;
            try { if (Directory.GetFileSystemEntries(tempDir).Length == 0) Directory.Delete(tempDir); } catch { }
            return renamed;
        }

        // En la misma unidad es un simple renombrado. Entre unidades se copia primero a un nombre provisional
        // y luego se renombra: si se corta a medias (cierre, disco lleno) no queda un archivo incompleto con
        // el nombre final, y el temporal sigue ahí para volver a intentarlo.
        static void MoveFile(string temp, string dest)
        {
            string r1 = Path.GetPathRoot(Path.GetFullPath(temp)), r2 = Path.GetPathRoot(Path.GetFullPath(dest));
            if (string.Equals(r1, r2, StringComparison.OrdinalIgnoreCase)) { File.Move(temp, dest); return; }
            string part = Path.Combine(Path.GetDirectoryName(dest), "~" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".idmclone-mover");
            try
            {
                File.Copy(temp, part, true);
                File.Move(part, dest);
            }
            catch
            {
                try { File.Delete(part); } catch { }
                throw;
            }
            try { File.Delete(temp); } catch { }
        }

        // Fecha de creación y de modificación del archivo = Last-Modified del servidor (en UTC). Fechas sin sentido
        // (antes de 1980 o en el futuro) se ignoran. Nunca hace fallar una descarga ya completa (antivirus que
        // bloquea el archivo, unidad de red sin permiso, sistema de archivos sin fechas...).
        static readonly DateTime MinServerDate = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        internal static bool ServerDateUsable(string lastModified, out DateTime utc)
        {
            if (!TryParseHttpDate(lastModified, out utc)) return false;
            return utc >= MinServerDate && utc <= DateTime.UtcNow.AddDays(1);
        }

        void ApplyServerDate(string path, string lastModified) { Trace(SetServerDate(path, lastModified)); }

        // Devuelve qué pasó (para el registro).
        internal static string SetServerDate(string path, string lastModified)
        {
            try
            {
                DateTime utc;
                if (!ServerDateUsable(lastModified, out utc)) return "fecha del servidor no válida o ausente: " + lastModified;
                File.SetCreationTimeUtc(path, utc);
                File.SetLastWriteTimeUtc(path, utc);
                return "fecha del archivo = la del servidor: " + utc.ToString("u");
            }
            catch (Exception ex) { return "no se pudo poner la fecha del servidor: " + ex.Message; }
        }

        // FTP: reparto inicial en N trozos (su tamaño se sabe por SIZE). HTTP: un solo trozo con la
        // primera conexión; las demás lo irán partiendo por la mitad (segmentación dinámica de IDM).
        void InitSegments(bool preSplit)
        {
            long total = _item.Size;
            Preallocate(total);

            lock (_item.Sync)
            {
                _item.Segments.Clear();
                if (preSplit && _item.Resumable && total > 0)
                {
                    // Pre-reparto inicial; nunca más trozos que los que da el tamaño mínimo de partición.
                    long n = Math.Min(_cfg.MaxConnections, Math.Max(1L, total / MinSplit));
                    long per = total / n;
                    long pos = 0;
                    for (long i = 0; i < n; i++)
                    {
                        long end = (i == n - 1) ? total : pos + per;
                        _item.Segments.Add(new Segment(pos, pos, end));
                        pos = end;
                    }
                }
                else
                {
                    // Un solo trozo: el de la primera conexión (o un flujo sin rangos).
                    _item.Segments.Add(new Segment(0, 0, total > 0 ? total : long.MaxValue));
                }
            }
        }

        long MinSplit { get { return Math.Max(_cfg.MinSplitSize, AppConfig.MinAllowedSplit); } }

        // --- Sondeo inicial (solo FTP: el tamaño se pide con SIZE) ---
        void Probe()
        {
            int attempts = 0;
            while (!_stop)
            {
                try
                {
                    ProbeFtp();
                    return;
                }
                catch (FatalDownloadException) { throw; }
                catch (Exception ex)
                {
                    if (_stop) return;
                    string tls = TlsError(ex);
                    if (tls != null) throw new FatalDownloadException(tls);
                    WebException wex = ex as WebException;
                    if (wex != null)
                    {
                        string fatal = PermanentError(wex);
                        if (fatal != null) throw new FatalDownloadException(fatal);
                    }
                    if (++attempts > _cfg.RetryCount) throw;
                    SleepRetry();
                }
            }
        }

        // --- Primera conexión (HTTP) ---
        // Pide "bytes=0-": si el servidor admite rangos responde 206 con el tamaño total y el archivo
        // entero; si no, 200. En los dos casos los datos se aprovechan: no hay petición de sondeo aparte.
        WebResponse OpenFirst(out WebRequest reqOut)
        {
            reqOut = null;
            int attempts = 0, redirects = 0, busy = 0;
            bool withRange = true;
            while (!_stop)
            {
                HttpWebRequest req = null;
                bool keep = false;
                try
                {
                    req = (HttpWebRequest)NewRequest(null);
                    if (withRange) req.AddRange(0L);
                    Register(req);
                    if (_firstConn == null) _firstConn = NewConnection("Conectando...");
                    SetInfo(_firstConn, _item.PostData != null ? "Enviar POST..." : "Enviar GET...");
                    SendBody(req);
                    long t0 = Stopwatch.GetTimestamp();
                    HttpWebResponse resp = (HttpWebResponse)req.GetResponse();
                    SetInfo(_firstConn, "Recibiendo datos...");
                    RecordLatency((Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency);
                    _probeContentType = resp.ContentType;
                    _item.ETag = NullIfEmpty(resp.Headers["ETag"]);
                    _item.LastModified = NullIfEmpty(resp.Headers["Last-Modified"]);
                    RememberFinalUrl(resp);
                    if (resp.StatusCode == HttpStatusCode.PartialContent)
                    {
                        long start, end, total;
                        bool okCr = ParseContentRange(resp.Headers["Content-Range"], out start, out end, out total);
                        if (okCr && start != 0)
                        {
                            // Servidor roto (206 que no empieza en 0): pedirlo entero sin rango.
                            Trace("206 inicial desde " + start + ": se repite sin rango");
                            req.Abort(); resp.Close();
                            withRange = false;
                            continue;
                        }
                        _item.Resumable = okCr && total >= 0;
                        _item.Size = _item.Resumable ? total : -1;
                    }
                    else
                    {
                        _item.Resumable = false;          // no admite rangos: una sola conexión
                        _item.Size = resp.ContentLength;  // puede ser -1
                    }
                    Trace("primera conexión: " + (int)resp.StatusCode + " tamaño=" + _item.Size + " reanudable=" + _item.Resumable);
                    SuggestName(resp.Headers["Content-Disposition"], resp.ResponseUri, resp.ContentType);
                    keep = true;
                    reqOut = req;
                    return resp;
                }
                catch (Exception ex)
                {
                    if (_stop) return null;
                    SetInfo(_firstConn, "La conexión ha fallado, reiniciando...");
                    WebException wex = ex as WebException;
                    if (wex != null)
                    {
                        // 416 ante "0-": archivo vacío o servidor que no lo acepta: pedirlo sin rango.
                        if (HttpCode(wex) == 416 && withRange) { withRange = false; continue; }
                        // 308: se sigue a mano (las demás redirecciones ya las sigue .NET).
                        string target = Util.RedirectTarget(wex);
                        if (target != null && ++redirects <= 10) { _redirected = target; Trace("redirección 308 a " + target); continue; }
                        if (TrySwitchToBrowserProxy(req, wex)) continue;
                        string fatal = PermanentError(wex);
                        if (fatal != null) throw new FatalDownloadException(fatal);
                    }
                    if (++attempts > _cfg.RetryCount) throw;
                    if (wex != null && IsBusy(wex)) { SetInfo(_firstConn, "Servidor ocupado, esperando..."); SleepMs(BusyWaitMs(wex, ++busy)); }
                    else SleepRetry();
                }
                finally { if (!keep) Unregister(req); }
            }
            return null;
        }

        void RememberFinalUrl(HttpWebResponse resp)
        {
            try
            {
                Uri final = resp.ResponseUri;
                _effectiveUrl = final != null && !string.Equals(final.AbsoluteUri, new Uri(_item.Url).AbsoluteUri, StringComparison.Ordinal)
                    ? final.AbsoluteUri : null;
                if (_effectiveUrl != null) Trace("URL final tras redirección: " + _effectiveUrl);
            }
            catch { _effectiveUrl = null; }
        }

        // FTP: SIZE para el tamaño. Si el servidor lo da, se asume REST (como IDM) y se usan varias conexiones.
        void ProbeFtp()
        {
            FtpWebRequest req = (FtpWebRequest)NewRequest();
            req.Method = WebRequestMethods.Ftp.GetFileSize;
            Register(req);
            try
            {
                using (FtpWebResponse resp = (FtpWebResponse)req.GetResponse())
                {
                    _item.Size = resp.ContentLength;
                    _item.Resumable = resp.ContentLength > 0;
                }
            }
            finally { Unregister(req); }
            _probeContentType = null;
            if (_cfg.SetFileDateFromServer) ProbeFtpDate();
        }

        // FTP: fecha del archivo con MDTM (solo si se va a usar: es una conexión más). Si el servidor no lo
        // admite, la descarga sigue igual, sin fecha.
        void ProbeFtpDate()
        {
            FtpWebRequest req = null;
            try
            {
                req = (FtpWebRequest)NewRequest();
                req.Method = WebRequestMethods.Ftp.GetDateTimestamp;
                Register(req);
                using (FtpWebResponse resp = (FtpWebResponse)req.GetResponse())
                {
                    DateTime lm = resp.LastModified; // .NET la da en hora local (MDTM viene en UTC)
                    if (lm > DateTime.MinValue && lm.Year > 1)
                        _item.LastModified = lm.ToUniversalTime().ToString("r", System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex) { Trace("MDTM no disponible: " + ex.Message); }
            finally { Unregister(req); }
        }

        // Nombre que da el servidor (Content-Disposition, o el de la URL final tras redirecciones),
        // salvo que el usuario o el navegador ya lo fijaran. Recalcula categoría y carpeta.
        void SuggestName(string contentDisposition, Uri finalUri, string contentType = null)
        {
            try
            {
                if (_item.NameFromUser) return;
                string name = Util.FileNameFromContentDisposition(contentDisposition);
                if (name == null && finalUri != null)
                {
                    string fromUri = Util.FileNameFromUrl(finalUri.AbsoluteUri);
                    if (!Util.IsGenericName(fromUri) && Util.IsGenericName(Path.GetFileName(_item.FilePath))) name = fromUri;
                }
                // Una página sin nombre en la dirección ("http://sitio/"): como IDM, "default.html".
                // Con el nombre de la dirección final (tras redirecciones), no con el provisional ya reservado ("download_1").
                if (name == null && IsHtml(contentType) && Path.GetExtension(_item.FilePath ?? "").Length == 0)
                {
                    string b = Util.FileNameFromUrl(finalUri != null ? finalUri.AbsoluteUri : _item.Url);
                    if (Path.GetExtension(b).Length > 0) b = Path.GetFileNameWithoutExtension(b);
                    name = (string.Equals(b, "download", StringComparison.OrdinalIgnoreCase) ? "default" : b) + ".html";
                }
                if (string.IsNullOrEmpty(name)) return;

                string oldPath = _item.FilePath;
                string dir = Path.GetDirectoryName(oldPath);
                if (!_item.FolderFromUser)
                {
                    dir = _cfg.FolderFor(name, _item.Url, _item.Referrer);
                    _item.Category = _cfg.CategoryNameFor(name, _item.Url, _item.Referrer);
                }
                string wanted = Path.Combine(dir, Util.FitFileName(dir, name));
                if (string.Equals(wanted, oldPath, StringComparison.OrdinalIgnoreCase)) return;
                string newPath = PathReserver != null ? PathReserver(wanted) : Util.UniquePath(wanted);
                _item.FilePath = newPath;
                // Liberar el hueco vacío que se reservó con el nombre provisional.
                try { if (File.Exists(oldPath) && new FileInfo(oldPath).Length == 0) File.Delete(oldPath); } catch { }
                _item.SaveState();
                Action<DownloadItem> h = PathChanged; if (h != null) h(_item);
            }
            catch { }
        }

        // --- Conexiones ---
        // Conexiones para este servidor: las de las opciones, sin pasar de las excepciones por servidor.
        int ConnectionLimit()
        {
            int n = _cfg.MaxConnections;
            try { n = Math.Min(n, _cfg.ConnectionsFor(new Uri(_item.Url).Host)); } catch { }
            string eff = _effectiveUrl;
            if (eff != null) { try { n = Math.Min(n, _cfg.ConnectionsFor(new Uri(eff).Host)); } catch { } }
            return Math.Max(1, n);
        }

        // initial: respuesta de la primera conexión (descarga nueva); la usa la conexión 0 para el primer trozo.
        volatile int _allowedConns = 1;      // conexiones que pueden partir trozos ahora mismo
        volatile bool _growthStopped;        // añadir más ya no aumenta la velocidad
        const int InitialConnections = 4;    // IDM abre 4 conexiones de entrada (medido con IDM 6.43)

        void RunWorkers(WebResponse initial, WebRequest initialReq)
        {
            int workers = _item.Resumable ? ConnectionLimit() : 1;
            // Al reanudar con varios trozos pendientes se usan todos desde el principio; en una descarga
            // nueva se empieza, como IDM, con varias conexiones a la vez y se añaden más mientras ayuden.
            int pending = 0;
            lock (_item.Sync) foreach (Segment s in _item.Segments) if (s.Remaining > 0) pending++;
            _allowedConns = initial != null ? Math.Min(workers, InitialConnections) : Math.Max(1, Math.Min(workers, pending));
            _growthStopped = false;
            Segment firstSeg = null;
            lock (_item.Sync)
            {
                foreach (Segment s in _item.Segments) { s.Active = false; s.Failed = false; }
                if (initial != null && _item.Segments.Count > 0) { firstSeg = _item.Segments[0]; firstSeg.Active = true; }
            }
            _threads.Clear();
            for (int i = 0; i < workers; i++)
            {
                Segment pre = i == 0 ? firstSeg : null;
                WebResponse r = i == 0 ? initial : null;
                WebRequest q = i == 0 ? initialReq : null;
                Thread w = new Thread(delegate() { Worker(pre, r, q); });
                w.IsBackground = true;
                w.Name = "idm-" + _item.Id + "-c" + i;
                _threads.Add(w);
                w.Start();
            }

            // Guardar estado periódicamente mientras se descarga, y decidir si añadir conexiones.
            int tick = 0;
            long lastBytes = _item.Downloaded;
            long winStart = Stopwatch.GetTimestamp(), winBytes = lastBytes;
            double lastSpeed = -1;
            while (true)
            {
                bool anyAlive = false;
                foreach (Thread w in _threads) if (w.IsAlive) { anyAlive = true; break; }
                if (!anyAlive) break;
                // Más conexiones solo mientras sumen velocidad: 4 -> 8 -> 16... Si al duplicarlas la
                // velocidad total no sube al menos un 15 %, la línea o el servidor ya no dan más: se para ahí.
                if (_item.Resumable && !_growthStopped && _allowedConns < workers)
                {
                    double el = (Stopwatch.GetTimestamp() - winStart) / (double)Stopwatch.Frequency;
                    double lat;
                    lock (_latLock) lat = Math.Max(0, _latencySec);
                    // La primera medida, más larga: al principio TCP aún está acelerando y cualquier conexión
                    // nueva "parecería" sumar velocidad.
                    double window = Math.Max(lastSpeed < 0 ? 1.2 : 0.6, Math.Min(lat * 2, 4.0));
                    if (el >= window)
                    {
                        long now = _item.Downloaded;
                        double speed = (now - winBytes) / el;
                        if (lastSpeed < 0 || speed > lastSpeed * 1.15)
                        {
                            // (Como IDM, la primera medida siempre dobla: 4 -> 8. Con un servidor que limita cada
                            // conexión, 8 bajan el doble; si no ayudan, la siguiente medida lo para.)
                            lock (_capLock) if (!_growthStopped) _allowedConns = Math.Min(workers, _allowedConns * 2);
                            Trace("conexiones permitidas: " + _allowedConns + " (" + (speed / 1048576).ToString("0.0") + " MB/s)");
                            lastSpeed = speed;
                        }
                        else
                        {
                            _growthStopped = true;
                            Trace("no se añaden más conexiones: " + (speed / 1048576).ToString("0.0") + " MB/s con " + _allowedConns + " frente a " + (lastSpeed / 1048576).ToString("0.0") + " antes");
                        }
                        winStart = Stopwatch.GetTimestamp(); winBytes = now;
                    }
                }
                if (++tick % 5 == 0)
                {
                    _item.SaveState();
                    long b = _item.Downloaded;
                    Trace("progreso: " + (b >> 20) + " MB, " + ((b - lastBytes) / 1048576.0).ToString("0.0") + " MB/s, conexiones activas " + _item.ActiveConnections);
                    lastBytes = b;
                }
                Thread.Sleep(200);
            }
            Trace("fin de las conexiones");
        }

        int _liveWorkers;
        int _receiving;                      // conexiones recibiendo datos ahora mismo (respuesta ya aceptada)
        long _lastConnEndTicks;              // cuándo dejó de recibir la última conexión que lo hizo (Stopwatch)

        void Worker(Segment pre, WebResponse initial, WebRequest initialReq)
        {
            Interlocked.Increment(ref _liveWorkers);
            bool retired = false;
            ConnectionInfo ci = initial != null ? _firstConn : null;
            try
            {
                while (!Halted)
                {
                    bool later = false;
                    Segment seg = pre ?? AcquireSegment(out later);
                    pre = null;
                    if (seg == null)
                    {
                        // Puede que haya que esperar a conocer la velocidad de las conexiones abiertas.
                        if (later) { Thread.Sleep(150); continue; }
                        return; // no queda trabajo
                    }
                    if (ci == null) ci = NewConnection("Conectando...");
                    try
                    {
                        WebResponse r = initial; WebRequest q = initialReq;
                        initial = null; initialReq = null;
                        DownloadSegment(seg, r, q, ci);
                        if (!Halted) SetInfo(ci, "Completo.");
                    }
                    catch (TooManyConnectionsException)
                    {
                        // El servidor no admite tantas conexiones: esta se retira y deja su trozo a las demás.
                        SetInfo(ci, "Desconectada: el servidor no admite más conexiones.");
                        retired = true;
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (!Halted)
                        {
                            _lastError = Describe(ex);
                            SetInfo(ci, "Error: " + _lastError);
                            seg.Failed = true; // que no lo vuelva a coger otra conexión en esta sesión
                        }
                    }
                    finally
                    {
                        seg.Active = false;
                    }
                }
            }
            finally
            {
                if (!retired) Interlocked.Decrement(ref _liveWorkers);
                // Si se paró antes de usar la respuesta inicial, cerrarla.
                if (initial != null) { try { if (initialReq != null) initialReq.Abort(); initial.Close(); } catch { } Unregister(initialReq); }
            }
        }

        // Al retirar una conexión por un 429/503: el máximo pasa a ser el de las que están recibiendo datos y no se
        // intenta crecer más en esta sesión. Con el mismo cerrojo que el crecimiento, para que este no deshaga el tope.
        readonly object _capLock = new object();
        volatile bool _serverLimited;        // el servidor ya respondió 429/503 por tener demasiadas conexiones

        void CapConnections(int remaining)
        {
            int cap = Math.Max(1, remaining);
            _serverLimited = true;
            lock (_capLock)
            {
                _growthStopped = true;
                if (cap < _allowedConns) { _allowedConns = cap; Trace("el servidor limita las conexiones: máximo " + cap); }
            }
        }

        // Quita una conexión si quedan otras vivas (el contador ya queda descontado). Nunca deja 0.
        bool TryRetireWorker()
        {
            while (true)
            {
                int n = _liveWorkers;
                if (n <= 1) return false;
                if (Interlocked.CompareExchange(ref _liveWorkers, n - 1, n) == n) return true;
            }
        }

        // Toma un segmento: primero uno sin empezar; si no, parte el mayor (segmentación dinámica de IDM).
        // 'later' = no hay nada que hacer AHORA, pero puede haberlo en breve (falta medir una velocidad).
        Segment AcquireSegment(out bool later)
        {
            later = false;
            lock (_item.Sync)
            {
                int active = 0;
                foreach (Segment s in _item.Segments) if (s.Active) active++;
                // a) ¿algún segmento inactivo con bytes pendientes? (el que soltó una conexión retirada por un 429/503
                //    lo coge la primera que quede libre: no se abre una conexión más de las que admite el servidor)
                if (active < _allowedConns || !_item.Resumable)
                    foreach (Segment s in _item.Segments)
                        if (!s.Active && !s.Failed && s.Remaining > 0) { s.Active = true; return s; }

                // b) robo de trabajo: partir el segmento activo con más bytes restantes.
                if (!_item.Resumable) return null;
                if (active >= _allowedConns)
                {
                    // De momento no se permiten más conexiones; puede que en breve sí.
                    later = !_growthStopped;
                    return null;
                }
                Segment victim = null;
                foreach (Segment s in _item.Segments)
                    if (s.Active && s.End != long.MaxValue)
                        if (victim == null || s.Remaining > victim.Remaining) victim = s;
                if (victim == null) return null;

                // Al empezar, como IDM: el archivo se reparte enseguida entre las primeras conexiones (cada nueva
                // parte por la mitad el trozo mayor), sin esperar a medir la velocidad de la primera.
                if (_item.Segments.Count < InitialConnections && victim.Remaining >= MinSplit * 4)
                {
                    long half = victim.Remaining / 2;
                    long oldEnd0 = victim.End, cut0 = oldEnd0 - half;
                    victim.End = cut0;
                    Segment first = new Segment(cut0, cut0, oldEnd0);
                    first.Active = true;
                    _item.Segments.Add(first);
                    return first;
                }

                long tail = StealTail(victim);
                if (tail < 0)
                {
                    // Aún no se sabe a qué velocidad baja la víctima: esperar un poco en vez de partir a ciegas.
                    later = victim.Remaining >= MinSplit * 2;
                    return null;
                }
                if (tail >= MinSplit)
                {
                    // La víctima se queda con lo que bajará mientras esta conexión conecta, más la mitad del resto;
                    // la nueva conexión se lleva la última parte.
                    long oldEnd = victim.End;
                    long cut = oldEnd - tail;
                    victim.End = cut;
                    Segment ns = new Segment(cut, cut, oldEnd);
                    ns.Active = true;
                    _item.Segments.Add(ns);
                    return ns;
                }
            }
            return null;
        }

        // --- Cuánto quitarle a una conexión ---
        // Abrir una conexión nueva cuesta 'latencia' (TCP + TLS + redirecciones + lo que tarde el servidor en
        // empezar a enviar; archive.org, ~3 s). Mientras tanto la víctima sigue bajando a su velocidad: ese
        // tramo se le deja; del resto, la mitad para cada una. Si el trozo resultante se bajaría en menos de lo
        // que cuesta conectar, no compensa partir. Devuelve -1 si aún no se conoce la velocidad de la víctima.
        double _latencySec = -1;             // media móvil del tiempo hasta la respuesta (-1 = aún sin medir)
        readonly object _latLock = new object();

        void RecordLatency(double sec)
        {
            lock (_latLock) _latencySec = _latencySec < 0 ? sec : _latencySec * 0.6 + sec * 0.4;
        }

        long StealTail(Segment victim)
        {
            long rem = victim.Remaining;
            if (victim.ConnStartTicks == 0) return -1; // la víctima aún está conectando
            double elapsed = (Stopwatch.GetTimestamp() - victim.ConnStartTicks) / (double)Stopwatch.Frequency;
            double lat;
            lock (_latLock) lat = Math.Max(0, _latencySec);
            // Medir la velocidad un tiempo representativo: los primeros instantes de una conexión TCP son
            // mucho más lentos que su velocidad real y harían partir demasiado pronto.
            if (elapsed < Math.Max(0.5, Math.Min(lat, 3.0)) || victim.ConnBytes <= 0) return -1;
            double speed = victim.ConnBytes / elapsed;   // bytes/s de esa conexión
            // Lo que bajará mientras la nueva conecta, con margen: al principio TCP aún está acelerando
            // (la velocidad medida se queda corta) y conectar con la línea ocupada tarda más.
            double during = speed * lat * 2;
            long tail = (long)((rem - during) / 2);
            if (tail < MinSplit || tail < during) return 0; // no compensa
            return tail;
        }

        // URL final tras las redirecciones (p. ej. el espejo de archive.org): las demás conexiones van
        // directas y se ahorran una redirección cada una. Si caduca, se vuelve a la original.
        volatile string _effectiveUrl;

        void DownloadSegment(Segment seg, WebResponse initial, WebRequest initialReq, ConnectionInfo ci)
        {
            int attempts = 0, busyHits = 0, busyWaits = 0;
            byte[] buf = new byte[BufferSize];
            while (!Halted)
            {
                long reqStart, endEx;
                lock (_item.Sync)
                {
                    // Sin rangos no se puede continuar a mitad: cada intento empieza desde el principio.
                    if (!_item.Resumable && initial == null) seg.Current = seg.Start;
                    if (seg.End != long.MaxValue && seg.Current >= seg.End) return;
                    reqStart = seg.Current; endEx = seg.End;
                }

                bool ranged = _item.Resumable;
                long gotThisAttempt = 0;
                bool receiving = false;
                WebRequest req = null;
                WebResponse respHolder = null;
                string viaUrl = _ftp ? null : _effectiveUrl;
                try
                {
                    WebResponse resp;
                    if (initial != null)
                    {
                        // Respuesta de la primera conexión: ya validada, los datos empiezan en el byte 0.
                        req = initialReq; resp = initial;
                        initial = null; initialReq = null;
                    }
                    else
                    {
                        // Servidor que limita las conexiones: si otra (o esta) acaba de terminar o cortar su trozo, se le
                        // da un momento para que la dé por cerrada; si no, contaría una de más y respondería 429.
                        if (_serverLimited) WaitAfterConnEnd();
                        req = NewRequest(viaUrl);
                        SetInfo(ci, _ftp ? "Enviando el comando RETR..." : "Enviar GET...");
                        long t0 = Stopwatch.GetTimestamp();
                        if (_ftp)
                        {
                            FtpWebRequest f = (FtpWebRequest)req;
                            f.Method = WebRequestMethods.Ftp.DownloadFile;
                            if (ranged && reqStart > 0) f.ContentOffset = reqStart; // REST
                        }
                        else if (ranged)
                        {
                            HttpWebRequest h = (HttpWebRequest)req;
                            h.AddRange(reqStart, endEx - 1);
                            // Si el archivo cambió desde que empezamos, el servidor responderá 200 entero.
                            string ifRange = IfRangeValue();
                            if (ifRange != null) h.Headers["If-Range"] = ifRange;
                        }
                        Register(req);
                        SendBody(req);
                        resp = req.GetResponse();
                        RecordLatency((Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency);
                        HttpWebResponse hr = resp as HttpWebResponse;
                        respHolder = resp;
                        if (hr != null) ValidateResponse(hr, ranged, reqStart);
                    }
                    respHolder = resp;
                    SetInfo(ci, "Recibiendo datos...");
                    Interlocked.Increment(ref _receiving); receiving = true;
                    lock (_item.Sync) { seg.ConnStartTicks = Stopwatch.GetTimestamp(); seg.ConnBytes = 0; }
                    {
                        long expected = (!ranged && resp.ContentLength >= 0) ? resp.ContentLength : -1;
                        using (Stream net = resp.GetResponseStream())
                        // Sin búfer de .NET (bufferSize 1): cada Write llega al sistema antes de sumarse a
                        // seg.Current, y SaveState vuelca el archivo a disco antes de guardar esas posiciones.
                        using (FileStream fs = new FileStream(WorkPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1))
                        {
                            fs.Seek(reqStart, SeekOrigin.Begin);
                            while (!Halted)
                            {
                                int want;
                                lock (_item.Sync)
                                {
                                    // Si nos robaron la cola, parar. (End solo baja a >= Current + MinSplit,
                                    // y MinSplit > BufferSize, así que 'want' nunca invade el tramo robado.)
                                    if (seg.End != long.MaxValue && seg.Current >= seg.End) break;
                                    want = buf.Length;
                                    if (seg.End != long.MaxValue)
                                    {
                                        long left = seg.End - seg.Current;
                                        if (left < want) want = (int)left;
                                    }
                                }
                                int globalLimit = _cfg.EffectiveSpeedLimitKBps, itemLimit = ItemSpeedLimit;
                                want = TokenBucket.MaxChunk(TokenBucket.MaxChunk(want, globalLimit), itemLimit);
                                if (want <= 0) break;

                                int read = net.Read(buf, 0, want);
                                if (read <= 0) break;

                                SpeedLimiter.Consume(read, globalLimit, delegate { return Halted; });
                                _itemBucket.Consume(read, itemLimit, delegate { return Halted; });
                                DownloadQuota.Consume(read, _cfg, delegate { return Halted; });
                                fs.Write(buf, 0, read);
                                lock (_item.Sync) { seg.Current += read; seg.ConnBytes += read; }
                                if (ci != null) Interlocked.Add(ref ci.Bytes, read);
                                gotThisAttempt += read;
                                if (_ftp && !_ftpDataOk) _ftpDataOk = true; // este modo FTP funciona: ya no se cambia
                            }
                            // Si paramos antes de lo pedido (nos robaron la cola o hay que pausar), cortar la
                            // conexión: si no, al cerrar la respuesta .NET descarga y tira el resto (el tramo
                            // cedido se bajaría dos veces) y FTP esperaría a terminar la transferencia.
                            long asked = ranged && endEx != long.MaxValue ? endEx - reqStart : resp.ContentLength;
                            if (_ftp || asked < 0 || gotThisAttempt < asked)
                            {
                                Trace("corte anticipado de la conexión tras " + gotThisAttempt + " de " + asked + " bytes");
                                try { req.Abort(); } catch { }
                            }
                        }

                        if (Halted) return;
                        bool done;
                        lock (_item.Sync) done = seg.End != long.MaxValue && seg.Current >= seg.End;
                        if (done) return;
                        if (seg.End == long.MaxValue)
                        {
                            // Flujo de tamaño desconocido: terminado, salvo que el servidor anunciara más.
                            // Se cierra el segmento para que ninguna conexión lo vuelva a coger.
                            if (expected < 0 || gotThisAttempt >= expected)
                            {
                                lock (_item.Sync) seg.End = seg.Current;
                                return;
                            }
                        }
                        throw new IOException("El servidor cerró la conexión antes de tiempo.");
                    }
                }
                catch (RangeNotHonoredException) { Trace("rango no respetado desde " + reqStart); _rangeBroken = true; return; }
                catch (FileChangedException fce) { Trace("archivo cambiado: " + fce.Message); _changed = true; return; }
                catch (FatalDownloadException fex) { Trace("fatal: " + fex.Message); _fatal = fex.Message; return; }
                catch (Exception ex)
                {
                    if (Halted) return;
                    Trace("error en segmento " + reqStart + "-" + endEx + ": " + ex.Message);
                    SetInfo(ci, ex is WebException && ((WebException)ex).Status == WebExceptionStatus.Timeout
                        ? "Se agotó el tiempo de espera de la conexión, reiniciando..." : "La conexión ha fallado, reiniciando...");
                    WebException wex = ex as WebException;
                    if (wex != null)
                    {
                        int code = HttpCode(wex);
                        if (viaUrl != null && (code == 401 || code == 403 || code == 404 || code == 410))
                        {
                            // La URL final (firmada, de un espejo...) caducó: volver a la original y reintentar.
                            Trace("la URL final dejó de valer (" + code + "); se vuelve a la original");
                            _effectiveUrl = null;
                            continue;
                        }
                        if (gotThisAttempt == 0 && TrySwitchFtpMode(req, wex)) continue;
                        if (gotThisAttempt == 0 && TrySwitchToBrowserProxy(req, wex)) continue;
                        if (code == 416) { Trace("416 -> archivo cambiado"); _changed = true; return; } // el rango ya no existe: archivo más corto
                        string fatal = PermanentError(wex);
                        if (fatal != null) { _fatal = fatal; return; }
                        if (IsBusy(wex))
                        {
                            // 429/503 mientras otras conexiones reciben datos: el servidor no admite más. Ya no se intenta
                            // crecer. Puede ser momentáneo (aún cuenta una conexión que acabamos de cortar al terminar un
                            // trozo): se reintenta enseguida un par de veces. Si sigue, esta conexión se retira, el máximo
                            // pasa a ser el de las que reciben y su trozo queda libre para la primera que acabe (antes lo
                            // retenía 2 s y se abrían conexiones de más que volvían a recibir 429).
                            int others = Thread.VolatileRead(ref _receiving);
                            // Justo después de terminar (o cortar) una conexión su trozo, el servidor puede seguir
                            // contándola unos milisegundos: también es momentáneo aunque ahora no reciba ninguna.
                            bool justEnded = Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastConnEndTicks) < Stopwatch.Frequency;
                            if (others > 0 || justEnded)
                            {
                                if (others > 0) lock (_capLock) _growthStopped = true;
                                if (++busyHits <= 2)
                                {
                                    SetInfo(ci, "Servidor ocupado, esperando...");
                                    SleepMs(BusyRetryMs);
                                    continue;
                                }
                                if (others > 0 && TryRetireWorker())
                                {
                                    CapConnections(others);
                                    Trace("servidor saturado (" + HttpCode(wex) + "): se retira una conexión; reciben " + others);
                                    throw new TooManyConnectionsException();
                                }
                            }
                            // Sola (o la última): espera lo que pida el servidor o, sin Retry-After, cada vez un poco más.
                            if (gotThisAttempt > 0) attempts = 0;
                            if (++attempts > _cfg.RetryCount) throw;
                            int waitMs;
                            SetInfo(ci, "Servidor ocupado, esperando...");
                            try { waitMs = BusyWaitMs(wex, ++busyWaits); }
                            catch (FatalDownloadException fex) { Trace("fatal: " + fex.Message); _fatal = fex.Message; return; }
                            SleepMs(waitMs);
                            continue;
                        }
                    }
                    // Hubo progreso y se cortó: el contador vuelve a empezar y se reconecta al momento, como IDM
                    // (esperar aquí solo alarga la descarga). Si el siguiente intento falla sin datos, ya espera.
                    // Solo con rangos: sin ellos cada intento empieza de cero y lo recibido no es avance real
                    // (un servidor que corta siempre provocaría un bucle de reconexiones). Si el avance es de
                    // muy pocos datos, una pausa corta para no martillear al servidor.
                    if (gotThisAttempt > 0 && _item.Resumable)
                    {
                        attempts = 0; busyHits = 0; busyWaits = 0;
                        SetInfo(ci, "Reconectando...");
                        if (gotThisAttempt < 64 * 1024) Thread.Sleep(250);
                        continue;
                    }
                    if (gotThisAttempt > 0) attempts = 0;
                    if (++attempts > _cfg.RetryCount) throw;
                    SleepRetry();
                }
                finally
                {
                    if (receiving) { Interlocked.Decrement(ref _receiving); Interlocked.Exchange(ref _lastConnEndTicks, Stopwatch.GetTimestamp()); }
                    if (respHolder != null) { try { respHolder.Close(); } catch { } }
                    Unregister(req);
                }
            }
        }

        // Validador para If-Range. Se prefiere la fecha: hay CDN (p. ej. arXiv) cuyos nodos dan ETags
        // distintos al mismo archivo, y un ETag "ajeno" haría que el servidor mandara el archivo entero.
        string IfRangeValue()
        {
            // "Ignorar los cambios de la hora del archivo al continuar": sin If-Range, porque si no el servidor que
            // cambia la fecha en cada petición respondería 200 entero. Tampoco el ETag: en Apache/nginx incluye la
            // fecha y cambiaría igual. El tamaño se sigue comprobando con Content-Range.
            if (IgnoreDateChanges) return null;
            if (!string.IsNullOrEmpty(_item.LastModified)) return _item.LastModified;
            string etag = _item.ETag;
            if (!string.IsNullOrEmpty(etag) && !etag.StartsWith("W/", StringComparison.Ordinal)) return etag;
            return null;
        }

        // Comprueba que la respuesta corresponde exactamente a lo pedido antes de escribir nada.
        void ValidateResponse(HttpWebResponse resp, bool ranged, long reqStart)
        {
            Trace("respuesta " + (int)resp.StatusCode + " pedido=" + reqStart + " CR=" + resp.Headers["Content-Range"] +
                  " ETag=" + resp.Headers["ETag"] + " (guardado " + _item.ETag + ") LM=" + resp.Headers["Last-Modified"] + " (guardado " + _item.LastModified + ")");
            if (ValidatorChanged(resp)) throw new FileChangedException();
            if (ranged)
            {
                if (resp.StatusCode != HttpStatusCode.PartialContent)
                    throw new RangeNotHonoredException();
                long start, end, total;
                if (!ParseContentRange(resp.Headers["Content-Range"], out start, out end, out total) || start != reqStart)
                    throw new RangeNotHonoredException();
                if (total >= 0 && _item.Size > 0 && total != _item.Size)
                    throw new FileChangedException();
            }
            else
            {
                if (resp.StatusCode != HttpStatusCode.OK && resp.StatusCode != HttpStatusCode.PartialContent)
                    throw new FatalDownloadException("Respuesta inesperada del servidor: " + (int)resp.StatusCode);
                if (_item.Size > 0 && resp.ContentLength >= 0 && resp.ContentLength != _item.Size)
                {
                    if (IsHtml(resp.ContentType) && !ExpectsHtml())
                        throw new FatalDownloadException("El servidor devolvió una página web en lugar del archivo (¿enlace caducado o hace falta iniciar sesión?).");
                    throw new FatalDownloadException("El servidor devolvió un contenido distinto del esperado (¿enlace caducado o página de error?).");
                }
            }
            if (IsHtml(resp.ContentType) && !ExpectsHtml())
                throw new FatalDownloadException("El servidor devolvió una página web en lugar del archivo (¿enlace caducado o hace falta iniciar sesión?).");
        }

        // ¿El servidor dice que el archivo ya no es el mismo? Manda la fecha de modificación; el ETag
        // solo cuenta si no hay fecha (algunos CDN reparten ETags distintos para los mismos bytes).
        // El cambio de tamaño se comprueba aparte con Content-Range.
        bool ValidatorChanged(HttpWebResponse resp)
        {
            if (IgnoreDateChanges) return false; // solo cuenta el tamaño (Content-Range), como IDM con la opción
            string lm = resp.Headers["Last-Modified"];
            if (!string.IsNullOrEmpty(_item.LastModified) && !string.IsNullOrEmpty(lm))
                return !SameHttpDate(lm, _item.LastModified);
            string etag = resp.Headers["ETag"];
            if (!string.IsNullOrEmpty(_item.ETag) && !string.IsNullOrEmpty(etag))
                return !string.Equals(StripWeak(etag), StripWeak(_item.ETag), StringComparison.Ordinal);
            return false;
        }

        // Opción "Ignorar los cambios de la hora del archivo cuando se continúe una descarga". Se lee en cada petición
        // (vale aunque se marque a mitad). Solo si hay fecha: con un servidor que solo da ETag se mantiene la
        // comprobación de siempre (la opción habla de la hora del archivo).
        bool IgnoreDateChanges
        {
            get { return _cfg.IgnoreDateChangesOnResume && !string.IsNullOrEmpty(_item.LastModified); }
        }

        static bool SameHttpDate(string a, string b)
        {
            DateTime da, db;
            if (TryParseHttpDate(a, out da) && TryParseHttpDate(b, out db)) return da == db;
            if (DateTime.TryParse(a, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out da) &&
                DateTime.TryParse(b, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out db))
                return da == db;
            return string.Equals(a.Trim(), b.Trim(), StringComparison.Ordinal);
        }

        // Fecha HTTP (RFC 7231 §7.1.1.1): RFC 1123 ("Sun, 06 Nov 1994 08:49:37 GMT"), RFC 850
        // ("Sunday, 06-Nov-94 08:49:37 GMT") y asctime ("Sun Nov  6 08:49:37 1994"). Siempre en GMT/UTC.
        static readonly string[] HttpDateFormats = {
            "ddd, dd MMM yyyy HH':'mm':'ss 'GMT'", "ddd, d MMM yyyy HH':'mm':'ss 'GMT'",
            "dddd, dd-MMM-yy HH':'mm':'ss 'GMT'", "dddd, d-MMM-yy HH':'mm':'ss 'GMT'",
            "ddd MMM d HH':'mm':'ss yyyy", "ddd MMM dd HH':'mm':'ss yyyy",
        };

        public static bool TryParseHttpDate(string s, out DateTime utc)
        {
            utc = DateTime.MinValue;
            if (string.IsNullOrEmpty(s)) return false;
            DateTime d;
            if (!DateTime.TryParseExact(s.Trim(), HttpDateFormats, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AllowWhiteSpaces | System.Globalization.DateTimeStyles.AssumeUniversal |
                    System.Globalization.DateTimeStyles.AdjustToUniversal, out d))
                return false;
            utc = DateTime.SpecifyKind(d, DateTimeKind.Utc);
            return true;
        }

        static string StripWeak(string etag) { etag = etag.Trim(); return etag.StartsWith("W/", StringComparison.Ordinal) ? etag.Substring(2) : etag; }

        static bool IsHtml(string contentType)
        {
            return !string.IsNullOrEmpty(contentType) && contentType.IndexOf("text/html", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Se descarga una página (la primera respuesta fue HTML, o es un .html/.htm, p. ej. al reanudar, cuando no se
        // repite la primera petición): entonces una respuesta HTML no es una página de error.
        bool ExpectsHtml()
        {
            if (IsHtml(_probeContentType)) return true;
            string ext = Path.GetExtension(_item.FilePath ?? "").ToLowerInvariant();
            return ext == ".html" || ext == ".htm" || ext == ".xhtml";
        }

        static int HttpCode(WebException wex)
        {
            HttpWebResponse r = wex.Response as HttpWebResponse;
            return r != null ? (int)r.StatusCode : 0;
        }

        // Errores que no se arreglan reintentando (como IDM: no insiste 20 veces ante un 404).
        // Mensaje de error para el usuario. Si no se llega al proxy, lo dice (el de .NET solo habla del "servidor remoto").
        string Describe(Exception ex)
        {
            WebException wex = ex as WebException;
            // Fallo del servidor SOCKS (lo explica el puente local con sus cabeceras): mensaje claro, no "(502)".
            string socks = wex != null ? SocksBridge.Explain(wex) : null;
            if (socks != null) return socks;
            if (wex != null && (wex.Status == WebExceptionStatus.ConnectFailure || wex.Status == WebExceptionStatus.ProxyNameResolutionFailure))
            {
                WebProxy bp = _useBrowserProxy ? _browserProxy : null;
                if (bp != null && !_ftp) return "No se puede conectar con el proxy del navegador " + BrowserProxyName(bp) + ".";
                try
                {
                    Uri u = new Uri(_effectiveUrl ?? _item.Url);
                    IWebProxy p = u.Scheme == Uri.UriSchemeFtp ? _ftpProxy : (_cfg.ProxyMode == "system" ? WebRequest.DefaultWebProxy : NetSettings.ProxyFor(_cfg, u));
                    if (p != null && !p.IsBypassed(u))
                    {
                        Uri pu = p.GetProxy(u);
                        if (pu != null && pu != u) return "No se puede conectar con el proxy " + pu.Authority + ". Revisa Opciones > Proxy.";
                    }
                }
                catch { }
            }
            return ex.Message;
        }

        // FTPS: certificado no válido, cifrado imposible o servidor sin AUTH TLS. Reintentar no lo arregla.
        string TlsError(Exception ex)
        {
            if (!_ftps) return null;
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if (e is System.Security.Authentication.AuthenticationException)
                    return "No se pudo establecer la conexión cifrada (FTPS): " + e.Message;
                WebException w = e as WebException;
                if (w == null) continue;
                if (w.Status == WebExceptionStatus.TrustFailure)
                    return "El certificado del servidor FTPS no es válido (caducado, autofirmado o de otro servidor).";
                if (w.Status == WebExceptionStatus.SecureChannelFailure)
                    return "No se pudo establecer la conexión cifrada con el servidor FTPS.";
                FtpWebResponse f = w.Response as FtpWebResponse;
                if (f != null)
                {
                    int c = (int)f.StatusCode;
                    if (c == 500 || c == 502 || c == 504 || c == 534 || c == 431)
                        return "El servidor FTP no admite conexiones cifradas (FTPS / AUTH TLS). Usa ftp:// si quieres bajarlo sin cifrar.";
                }
            }
            return null;
        }

        static string PermanentError(WebException wex)
        {
            if (SocksBridge.IsPermanent(wex)) return SocksBridge.Explain(wex); // contraseña o regla del SOCKS: reintentar no sirve
            HttpWebResponse h = wex.Response as HttpWebResponse;
            if (h != null)
            {
                int c = (int)h.StatusCode;
                if (c == 404 || c == 410) return "El archivo no existe en el servidor (" + c + ").";
                if (c == 401) return "El servidor pide usuario y contraseña, o no acepta los guardados (401). Revísalos en Opciones > Inicios de sesión.";
                if (c == 403) return "El servidor denegó el acceso (403). Puede que el enlace haya caducado o haga falta iniciar sesión.";
                if (c == 407) return "El proxy pide usuario y contraseña, o no acepta los configurados (407). Revísalos en Opciones > Proxy.";
                if (c == 400 || c == 405 || c == 501) return "El servidor rechazó la petición (" + c + ").";
                return null; // 408, 429, 5xx...: transitorios
            }
            FtpWebResponse f = wex.Response as FtpWebResponse;
            if (f != null)
            {
                int c = (int)f.StatusCode;
                if (c == 550) return "El archivo no existe en el servidor FTP (550).";
                if (c == 530) return "El servidor FTP rechazó el usuario o la contraseña (530).";
            }
            return null;
        }

        void Register(WebRequest req)
        {
            if (req == null) return;
            lock (_active) _active.Add(req);
            if (_stop) { try { req.Abort(); } catch { } }
        }

        void Unregister(WebRequest req)
        {
            if (req == null) return;
            lock (_active) _active.Remove(req);
        }

        void AbortActive()
        {
            WebRequest[] reqs;
            lock (_active) reqs = _active.ToArray();
            foreach (WebRequest r in reqs) { try { r.Abort(); } catch { } }
        }

        void Preallocate(long size)
        {
            if (size <= 0) { using (new FileStream(WorkPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { } return; }
            using (FileStream fs = new FileStream(WorkPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
            {
                if (fs.Length != size) fs.SetLength(size);
            }
        }

        // Cola de sincronización: ¿cambió el archivo en el servidor desde que se descargó?
        // Pide solo el primer byte y compara fecha, tamaño y ETag con los guardados.
        public static bool RemoteChanged(DownloadItem item, AppConfig cfg)
        {
            if (item.IsMedia) return false; // vídeo por streaming: no hay un archivo único que comparar
            DownloadEngine e = new DownloadEngine(item, cfg);
            if (e._ftp) return false; // FTP: sin forma fiable de saberlo
            HttpWebRequest req = (HttpWebRequest)e.NewRequest(null);
            req.AddRange(0L, 0L);
            e.SendBody(req);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                long total = resp.ContentLength;
                long s0, s1, t;
                if (resp.StatusCode == HttpStatusCode.PartialContent && ParseContentRange(resp.Headers["Content-Range"], out s0, out s1, out t)) total = t;
                string lm = NullIfEmpty(resp.Headers["Last-Modified"]), etag = NullIfEmpty(resp.Headers["ETag"]);
                if (lm != null && item.LastModified != null) return lm != item.LastModified;
                if (total >= 0 && item.Size >= 0 && total != item.Size) return true;
                if (lm == null && item.LastModified == null && etag != null && item.ETag != null) return etag != item.ETag;
                return false;
            }
        }

        // Descargas que salen de un formulario (POST): cada petición repite el envío con su cuerpo.
        // Se escribe al final, cuando ya están puestas todas las cabeceras (Range, If-Range...).
        void SendBody(WebRequest req)
        {
            HttpWebRequest h = req as HttpWebRequest;
            if (h == null || string.IsNullOrEmpty(_item.PostData)) return;
            byte[] body = Encoding.UTF8.GetBytes(_item.PostData);
            h.Method = "POST";
            h.ContentType = "application/x-www-form-urlencoded";
            h.ContentLength = body.Length;
            h.AllowAutoRedirect = true;
            using (Stream s = h.GetRequestStream()) s.Write(body, 0, body.Length);
        }

        // --- FTP: modo pasivo / activo ---
        // Se empieza con el de las opciones. Si la conexión de datos falla en ese modo antes de haber recibido nada,
        // se cambia UNA vez al otro para el resto de esta descarga (detrás de NAT el activo casi nunca funciona; hay
        // servidores con el pasivo cortado por el cortafuegos).
        // Si el otro modo tampoco va, se vuelve al de las opciones (un fallo de la conexión de control, p. ej. el
        // servidor caído un momento, no se distingue de uno de la de datos) y siguen los reintentos normales.
        int _ftpMode;                        // 0 = el de las opciones; 1 = pasivo; 2 = activo
        int _ftpModeSwitches;
        volatile bool _ftpDataOk;

        bool FtpPassiveNow
        {
            get { int m = _ftpMode; return m == 0 ? _cfg.FtpPassive : m == 1; }
        }

        // ¿Error típico de la conexión de datos (o de un modo que el servidor no admite)?
        internal static bool IsFtpDataModeError(WebException wex)
        {
            if (wex == null) return false;
            FtpWebResponse f = wex.Response as FtpWebResponse;
            if (f != null)
            {
                int c = (int)f.StatusCode;
                // 227 = el servidor dio su puerto pasivo pero no se pudo conectar a él (.NET informa con la última
                // respuesta); 425 = el servidor no pudo abrir la conexión de datos (modo activo detrás de NAT).
                if (c == 227 || c == 229 || c == 425 || c == 500 || c == 501 || c == 502 || c == 504) return true;
                for (Exception e = wex.InnerException; e != null; e = e.InnerException)
                    if (e is System.Net.Sockets.SocketException) return true;
                return false;
            }
            return wex.Status == WebExceptionStatus.ConnectFailure || wex.Status == WebExceptionStatus.Timeout;
        }

        // Decide si el fallo de una petición FTP se arregla con el otro modo. true = repetir ya (sin contar intento).
        bool TrySwitchFtpMode(WebRequest req, Exception ex)
        {
            FtpWebRequest f = req as FtpWebRequest;
            if (f == null || !IsFtpDataModeError(ex as WebException)) return false;
            bool usedPassive = f.UsePassive;
            lock (_active)
            {
                if (usedPassive != FtpPassiveNow) return true;        // otra conexión ya cambió de modo
                if (_ftpModeSwitches >= 2 || _ftpDataOk) return false; // ya se probaron los dos, o este modo ya funcionó
                _ftpModeSwitches++;
                _ftpMode = usedPassive ? 2 : 1;
            }
            Trace("FTP: el modo " + (usedPassive ? "pasivo" : "activo") + " falló (" + ex.Message + "); se reintenta en modo " +
                  (usedPassive ? "activo" : "pasivo"));
            return true;
        }

        // --- Proxy del navegador (Opciones > Proxy: "usar el proxy de la petición del navegador si hay errores") ---
        // Si la conexión directa (o por el proxy de las opciones) falla de una forma que un proxy puede arreglar, el
        // resto de esta descarga va por el proxy que usó el navegador (DownloadItem.BrowserProxy): HTTP ("host:puerto")
        // o SOCKS ("socks5://host:puerto", "socks4://", "socks4a://"), este por el puente SOCKS local (src\Socks.cs).
        volatile bool _useBrowserProxy;

        // "host:puerto" (o "[ipv6]:puerto", o con "http://") -> proxy HTTP; "socks5://..." / "socks4://..." /
        // "socks4a://..." -> el puente SOCKS local hacia ese servidor. null si no es válido.
        internal static WebProxy ParseBrowserProxy(string hostPort)
        {
            if (string.IsNullOrEmpty(hostPort)) return null;
            string s = hostPort.Trim();
            if (s.StartsWith("socks", StringComparison.OrdinalIgnoreCase))
            {
                SocksSettings socks = SocksSettings.FromBrowserProxy(s);
                if (socks == null) return null;
                WebProxy bridge = new WebProxy(SocksBridge.Address);
                bridge.Credentials = SocksBridge.CredentialFor(socks);
                bridge.BypassProxyOnLocal = false;
                return bridge;
            }
            if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s = s.Substring(7);
            s = s.TrimEnd('/');
            if (s.Length == 0 || s.IndexOfAny(new char[] { '/', '@', ' ', '?', '#', '\\' }) >= 0) return null;
            Uri u;
            if (!Uri.TryCreate("http://" + s, UriKind.Absolute, out u) || string.IsNullOrEmpty(u.Host)) return null;
            // Puerto obligatorio y explícito.
            int colon = s.LastIndexOf(':');
            if (colon <= 0 || colon < s.LastIndexOf(']')) return null;
            int port;
            if (!int.TryParse(s.Substring(colon + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port) ||
                port < 1 || port > 65535 || u.Port != port) return null;
            WebProxy p = new WebProxy(u);
            p.BypassProxyOnLocal = false;
            return p;
        }

        // Errores que un proxy puede arreglar: no se llega al servidor (o al proxy de las opciones), se agota el
        // tiempo o el proxy de las opciones pide credenciales (407). Una respuesta del propio servidor (403, 404,
        // 401, 5xx...) no: un 403 lo puede mandar cualquier servidor para desviar la descarga al proxy del navegador.
        internal static bool IsBrowserProxyWorthy(WebException wex)
        {
            if (wex == null) return false;
            // El SOCKS de las opciones caído, que no responde o que no nos acepta (lo cuenta el puente con un 502 marcado):
            // igual que un proxy HTTP caído o con 407. Los fallos del destino (dns, refused, rule) no.
            string socks = SocksBridge.FailureCode(wex);
            if (socks != null) return socks == "connect" || socks == "timeout" || socks == "fail" || socks == "proto" || socks == "auth";
            HttpWebResponse h = wex.Response as HttpWebResponse;
            if (h != null) return (int)h.StatusCode == 407;
            switch (wex.Status)
            {
                case WebExceptionStatus.ConnectFailure:
                case WebExceptionStatus.NameResolutionFailure:
                case WebExceptionStatus.ProxyNameResolutionFailure:
                case WebExceptionStatus.Timeout:
                case WebExceptionStatus.ConnectionClosed:
                case WebExceptionStatus.SecureChannelFailure:
                case WebExceptionStatus.RequestProhibitedByProxy:
                    return true;
            }
            return false;
        }

        // true = repetir ya por el proxy del navegador (sin contar intento).
        bool TrySwitchToBrowserProxy(WebRequest req, Exception ex)
        {
            if (!_cfg.UseBrowserProxyOnError) return false; // Opciones > Proxy/Socks: "Usar proxy/socks desde la petición de un navegador..."
            HttpWebRequest h = req as HttpWebRequest;
            if (_ftp || _ftpProxy != null || h == null) return false;   // ftp:// (directo o por su proxy): nunca por el del navegador
            if (h.Proxy != null && h.Proxy == _browserProxy) return false; // ya iba por él: error normal
            if (!IsBrowserProxyWorthy(ex as WebException)) return false;
            lock (_active)
            {
                if (_browserProxy == null)
                {
                    WebProxy p = ParseBrowserProxy(_item.BrowserProxy);
                    if (p == null) return false;
                    _browserProxy = p;
                    Trace("la conexión falló (" + ex.Message + "); se usa el proxy del navegador " + BrowserProxyName(p) + " para el resto de la descarga");
                }
                _useBrowserProxy = true;
            }
            return true;
        }

        WebProxy _browserProxy;

        // Nombre del proxy del navegador para los mensajes: el SOCKS por su dirección (no la del puente local).
        string BrowserProxyName(WebProxy p)
        {
            string s = _item.BrowserProxy;
            return !string.IsNullOrEmpty(s) && s.StartsWith("socks", StringComparison.OrdinalIgnoreCase) ? s : p.Address.Authority;
        }

        WebRequest NewRequest() { return NewRequest(null); }

        volatile string _redirected; // destino de una redirección 308 de la URL de la descarga

        WebRequest NewRequest(string url)
        {
            string target = url ?? _redirected ?? _item.Url;
            // .NET no conoce "ftps://": es la misma dirección ftp:// pidiendo cifrado (AUTH TLS, PROT P).
            if (target.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase)) target = "ftp://" + target.Substring(7);
            // FTP por el proxy HTTP: una petición HTTP con la dirección ftp:// (el proxy hace de cliente FTP).
            WebRequest wr = _ftpProxy != null && target.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)
                ? NetSettings.FtpViaHttpProxy(new Uri(target), _ftpProxy) : WebRequest.Create(target);
            FtpWebRequest ftp = wr as FtpWebRequest;
            if (ftp != null)
            {
                ftp.EnableSsl = _ftps;
                ftp.UseBinary = true;
                ftp.UsePassive = FtpPassiveNow; // Opciones > Proxy: "usar FTP en modo PASV" (o el otro modo si ese falló)
                ftp.KeepAlive = false;
                ftp.Timeout = 30000;
                ftp.ReadWriteTimeout = 60000;
                ftp.Proxy = null;           // FtpWebRequest no admite proxys HTTP
                // Usuario y contraseña: "Inicios de sesión", los de la URL (ftp://usuario:clave@host/...) o anónimo.
                NetworkCredential fc = !string.IsNullOrEmpty(_item.LoginUser) ? new NetworkCredential(_item.LoginUser, _item.LoginPassword)
                                                                                : NetSettings.CredentialsFor(_cfg, new Uri(_item.Url));
                if (fc != null) ftp.Credentials = fc;
                return ftp;
            }

            HttpWebRequest req = (HttpWebRequest)wr;
            req.UserAgent = string.IsNullOrEmpty(_item.UserAgentOverride) ? _cfg.UserAgent : _item.UserAgentOverride;
            req.AllowAutoRedirect = true;
            req.KeepAlive = true;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            // Proxy de las opciones. "system" deja WebRequest.DefaultWebProxy (con su caché): crear uno
            // nuevo en cada petición repetía la detección automática (WPAD) en cada conexión.
            WebProxy browserProxy = _useBrowserProxy ? _browserProxy : null;
            if (_ftpProxy != null && req.RequestUri.Scheme == Uri.UriSchemeFtp) req.Proxy = _ftpProxy;   // FTP: siempre por su proxy
            else if (browserProxy != null) req.Proxy = browserProxy;
            else if (_cfg.ProxyMode != "system") req.Proxy = NetSettings.ProxyFor(_cfg, req.RequestUri);
            if (!HappyEyeballs.Global) HappyEyeballs.Use(req.ServicePoint);
            // Usuario y contraseña del sitio (Basic/Digest/NTLM): .NET responde al 401 del servidor.
            NetworkCredential cred = null;
            if (!string.IsNullOrEmpty(_item.LoginUser) && SameHost(req.RequestUri, _item.Url))
                cred = new NetworkCredential(_item.LoginUser, _item.LoginPassword);
            if (cred == null) cred = NetSettings.CredentialsFor(_cfg, req.RequestUri);
            if (cred == null && SameHost(req.RequestUri, _item.Url)) cred = NetSettings.CredentialsFor(_cfg, new Uri(_item.Url));
            if (cred != null) { req.Credentials = cred; req.PreAuthenticate = true; }
            if (!string.IsNullOrEmpty(_item.Referrer)) { try { req.Referer = _item.Referrer; } catch { } }
            // Cookies: contenedor por dominio, como un navegador. Envía las del sitio solo a ese sitio
            // (nunca a otro dominio tras una redirección) y guarda las que el servidor fije por el camino.
            if (_jar != null) req.CookieContainer = _jar;
            else if (_rawCookies != null && SameHost(req.RequestUri, _item.Url))
            { try { req.Headers[HttpRequestHeader.Cookie] = _rawCookies; } catch { } }
            return req;
        }

        void SleepRetry() { SleepSeconds(Math.Max(1, _cfg.RetryDelaySec)); }

        void SleepSeconds(int seconds) { SleepMs(Math.Max(1, seconds) * 1000); }

        void SleepMs(int ms)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (!Halted)
            {
                long left = ms - sw.ElapsedMilliseconds;
                if (left <= 0) break;
                Thread.Sleep((int)Math.Min(100, left));
            }
        }

        // Reintento rápido ante un 429/503 que parece momentáneo (otras conexiones reciben datos, o se acaba de cortar una).
        const int BusyRetryMs = 200;
        const int AfterConnEndMs = 60;

        void WaitAfterConnEnd()
        {
            long since = (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastConnEndTicks)) * 1000 / Stopwatch.Frequency;
            if (since >= 0 && since < AfterConnEndMs) Thread.Sleep((int)(AfterConnEndMs - since));
        }

        // Espera ante un 429/503 de la única conexión: la que pida el servidor (Retry-After) o, sin ella, progresiva
        // (0,5 s, 1 s, 2 s, 4 s... hasta "Reintentar cada" de Opciones): un 503 pasajero se supera enseguida en vez de
        // esperar siempre esos 5 s (dos 503 seguidos costaban 10 s). n = número de esperas seguidas (1, 2...).
        int BusyWaitMs(WebException wex, int n)
        {
            if (RetryAfterHeader(wex) != null) return RetryAfterSeconds(wex) * 1000;
            return BusyBackoffMs(n, _cfg.RetryDelaySec);
        }

        internal static int BusyBackoffMs(int n, int retryDelaySec)
        {
            int cap = Math.Max(1, retryDelaySec) * 1000;
            int ms = 500;
            for (int i = 1; i < n && ms < cap; i++) ms *= 2;
            return Math.Min(ms, cap);
        }

        static string RetryAfterHeader(WebException wex)
        {
            HttpWebResponse r = wex != null ? wex.Response as HttpWebResponse : null;
            string v = null;
            try { v = r != null ? r.Headers["Retry-After"] : null; } catch (ObjectDisposedException) { }
            return string.IsNullOrEmpty(v) ? null : v;
        }

        static bool IsBusy(WebException wex) { int c = HttpCode(wex); return c == 429 || c == 503; }

        // Más espera que esto no se hace dentro de la descarga: se da error y la cola reintenta luego.
        const int MaxInlineRetryAfterSec = 120;

        // Retry-After en segundos o como fecha HTTP. Si el servidor pide esperar demasiado
        // (p. ej. Wikimedia bloquea 600 s), no tiene sentido insistir: error con el motivo.
        int RetryAfterSeconds(WebException wex)
        {
            int def = Math.Max(1, _cfg.RetryDelaySec);
            HttpWebResponse r = wex.Response as HttpWebResponse;
            string v = r != null ? r.Headers["Retry-After"] : null;
            if (string.IsNullOrEmpty(v)) return def;
            int s = -1;
            DateTime when;
            if (!int.TryParse(v.Trim(), out s))
            {
                if (DateTime.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out when))
                    s = (int)Math.Ceiling((when - DateTime.UtcNow).TotalSeconds);
                else return def;
            }
            if (s > MaxInlineRetryAfterSec)
                throw new FatalDownloadException("El servidor está limitando las descargas (" + HttpCode(wex) + ") y pide esperar " +
                                                 Math.Ceiling(s / 60.0) + " min. Se podrá reanudar más tarde.");
            return Math.Max(1, s);
        }

        static string NullIfEmpty(string s) { return string.IsNullOrEmpty(s) ? null : s; }

        internal static bool SameHost(Uri a, string b)
        {
            try { return string.Equals(a.Host, new Uri(b).Host, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        // "bytes 100-199/1000", "bytes 100-199/*" o "bytes */1000".
        public static bool ParseContentRange(string cr, out long start, out long end, out long total)
        {
            start = end = total = -1;
            if (string.IsNullOrEmpty(cr)) return false;
            string s = cr.Trim();
            if (s.StartsWith("bytes", StringComparison.OrdinalIgnoreCase)) s = s.Substring(5).Trim();
            int slash = s.LastIndexOf('/');
            if (slash < 0) return false;
            string range = s.Substring(0, slash).Trim();
            string tail = s.Substring(slash + 1).Trim();
            if (tail != "*" && !long.TryParse(tail, out total)) total = -1;
            if (range == "*") return false;
            int dash = range.IndexOf('-');
            if (dash <= 0) return false;
            return long.TryParse(range.Substring(0, dash), out start) && long.TryParse(range.Substring(dash + 1), out end);
        }
    }

    // El servidor no devolvió el rango pedido (200, otro inicio o sin Content-Range).
    public class RangeNotHonoredException : Exception
    {
        public RangeNotHonoredException() : base("El servidor no respetó el rango pedido.") { }
    }

    // El servidor rechaza más conexiones (429/503): la conexión se retira.
    public class TooManyConnectionsException : Exception
    {
        public TooManyConnectionsException() : base("El servidor no admite tantas conexiones.") { }
    }

    // El archivo del servidor ya no es el mismo que se empezó a descargar.
    public class FileChangedException : Exception
    {
        public FileChangedException() : base("El archivo cambió en el servidor.") { }
    }

    // Error que no se arregla reintentando (archivo inexistente, página de error, etc.).
    public class FatalDownloadException : Exception
    {
        public FatalDownloadException(string msg) : base(msg) { }
    }

    // Fila de la tabla de conexiones.
    public class ConnectionInfo
    {
        public int Number;
        public long Bytes;
        public volatile string Info;
        public ConnectionInfo Clone() { ConnectionInfo c = new ConnectionInfo(); c.Number = Number; c.Bytes = Interlocked.Read(ref Bytes); c.Info = Info; return c; }
    }

    // Cubo de fichas que puede quedar en negativo: así nunca se bloquea aunque una lectura
    // sea mayor que el límite por segundo.
    public class TokenBucket
    {
        readonly object _lock = new object();
        readonly Stopwatch _clock = Stopwatch.StartNew();
        double _tokens;
        long _last;

        public static int MaxChunk(int want, int limitKBps)
        {
            if (limitKBps <= 0) return want;
            int cap = Math.Max(1024, limitKBps * 1024 / 8); // trozos de ~1/8 s: velocidad más estable
            return Math.Min(want, cap);
        }

        public void Consume(int bytes, int limitKBps, Func<bool> halted)
        {
            if (limitKBps <= 0 || bytes <= 0) return;
            double rate = limitKBps * 1024.0;
            double waitSec;
            lock (_lock)
            {
                long now = _clock.ElapsedTicks;
                double dt = (now - _last) / (double)Stopwatch.Frequency;
                _last = now;
                _tokens = Math.Min(rate, _tokens + dt * rate); // como mucho 1 s de ráfaga
                _tokens -= bytes;
                waitSec = _tokens < 0 ? -_tokens / rate : 0;
            }
            int waitMs = (int)(waitSec * 1000);
            while (waitMs > 0 && (halted == null || !halted()))
            {
                int step = Math.Min(waitMs, 50);
                Thread.Sleep(step);
                waitMs -= step;
            }
        }
    }

    // Limitador de velocidad global (como IDM: el límite es para todas las descargas juntas).
    public static class SpeedLimiter
    {
        static readonly TokenBucket _bucket = new TokenBucket();
        public static int MaxChunk(int want, int limitKBps) { return TokenBucket.MaxChunk(want, limitKBps); }
        public static void Consume(int bytes, int limitKBps, Func<bool> halted) { _bucket.Consume(bytes, limitKBps, halted); }
    }
}
