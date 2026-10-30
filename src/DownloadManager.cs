using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace IDMClone
{
    // Opciones de alta de una descarga.
    public class AddOptions
    {
        public string Url;
        public string Folder;      // null = la de la categoría
        public string FileName;    // null = el de la URL (o el que diga el servidor)
        public string Category;    // null = según la extensión
        public bool StartNow;
        public string Cookies, Referrer, UserAgent;
        public int Queue = DownloadItem.MainQueue; // IDM añade las descargas nuevas a la cola principal
        public bool Transient;     // no dejar rastro en disco (descargas de navegación privada)
        public string Description;
        public string LoginUser, LoginPassword; // "usar autorización" del diálogo Agregar URL
        public bool Pending;       // se muestra el diálogo "Información del archivo": aún fuera de la lista
        public string PostData;    // formulario enviado por POST (lo da la extensión)
        // Vídeo por streaming (panel "Descargar este video"): Url es la lista .m3u8/.mpd.
        public string MediaKind;   // "hls", "dash", "pair" o "subs" (solo unos subtítulos: MediaVideo = la pista)
        public string MediaVideo;  // calidad elegida (null = la mejor)
        public string MediaAudio;  // pista de audio (null = la que va con esa calidad; "-" = sin audio)
        public string OverwritePath; // "Sobrescribir" un duplicado: el archivo que sustituirá (no se reserva otra ruta)
        public string BrowserProxy;  // proxy que usó el navegador ("host:puerto" o "socks5://host:puerto"...), para reintentar con él si falla
        public bool AutoCaptured;    // la capturó la extensión sola (no un clic en su menú o su panel)
    }

    // Gestiona todas las descargas, la cola y la persistencia de la lista.
    public class DownloadManager
    {
        public AppConfig Config;
        // Colas de descarga (principal, sincronización y las del usuario), como en el Planificador de IDM.
        public QueueSet Queues = QueueSet.CreateDefault(null);
        // Programación de la cola principal (atajo; lo usan las pruebas y la línea de comandos).
        public Schedule Schedule { get { return Queues.Main.Schedule; } set { Queues.Main.Schedule = value; } }
        public bool PersistQueues; // guarda queues.json (la GUI); las pruebas trabajan en memoria
        public void SaveQueues() { if (Persist && PersistQueues) Queues.Save(); }
        public readonly List<DownloadItem> Items = new List<DownloadItem>();
        readonly object _sync = new object();
        readonly object _saveLock = new object();
        readonly HashSet<int> _running = new HashSet<int>();          // colas en marcha
        readonly Dictionary<int, HashSet<string>> _syncChecked = new Dictionary<int, HashSet<string>>(); // ya comprobadas en esta pasada
        readonly HashSet<string> _syncChecking = new HashSet<string>(); // comprobándose ahora
        Timer _queueTimer;
        int _batchDepth;
        bool _batchDirty;
        long _queueOrderSeq;

        // Pausa entre reintentos de un archivo fallido en la cola (IDM: 30 s).
        public int QueueRetryDelaySec = 30;

        // false = modo consola (/n): no lee ni escribe downloads.json ni estados.
        public bool Persist = true;

        // Aviso de la última carga (lista dañada y recuperada o apartada).
        public string LoadWarning;

        public event Action Changed;                 // la lista cambió (para refrescar GUI)
        public event Action<DownloadItem> ItemFinished;
        public event Action<QueueDef> QueueFinished;   // una cola terminó sola (no al pararla a mano)
        public event Action<QueueDef> QueueStarted, QueueStopped; // para los sonidos y las acciones al terminar

        public DownloadManager(AppConfig cfg) { Config = cfg; }

        public bool QueueRunning { get { return IsQueueRunning(QueueDef.MainId); } }
        public bool IsQueueRunning(int id) { lock (_running) return _running.Contains(id); }
        bool AnyQueueRunning { get { lock (_running) return _running.Count > 0; } }

        // Copia de la lista para recorrerla sin bloquear (la GUI la usa en cada refresco).
        public List<DownloadItem> Snapshot()
        {
            lock (_sync) return new List<DownloadItem>(Items);
        }

        // --- Alta de descargas ---
        public DownloadItem Add(string url, string folder, string fileName, bool startNow)
        {
            return Add(url, folder, fileName, startNow, null, null, null);
        }

        public DownloadItem Add(string url, string folder, string fileName, bool startNow,
                                string cookies, string referrer, string userAgent)
        {
            AddOptions o = new AddOptions();
            o.Url = url; o.Folder = folder; o.FileName = fileName; o.StartNow = startNow;
            o.Cookies = cookies; o.Referrer = referrer; o.UserAgent = userAgent;
            return Add(o);
        }

        public DownloadItem Add(AddOptions o)
        {
            string url = (o.Url ?? "").Trim();
            if (!Util.LooksLikeUrl(url)) throw new ArgumentException("URL no válida: " + url);

            string fileName = o.FileName;
            bool nameFromUser = !string.IsNullOrEmpty(fileName);
            if (!nameFromUser) fileName = Util.FileNameFromUrl(url);
            fileName = Util.SanitizeFileName(fileName);
            if (string.IsNullOrEmpty(fileName)) { fileName = "download"; nameFromUser = false; }

            Category cat = null;
            if (!string.IsNullOrEmpty(o.Category))
                foreach (Category c in Config.Categories) if (c.Name == o.Category) { cat = c; break; }
            string folder = o.Folder;
            bool folderFromUser = !string.IsNullOrEmpty(folder);
            // Subtítulos: a la carpeta (y categoría) del vídeo al que acompañan, no a la de los .vtt/.srt (como IDM).
            string catName = o.MediaKind == DownloadEngine.SubtitleKind ? Path.GetFileNameWithoutExtension(fileName) + ".mp4" : fileName;
            if (!folderFromUser) folder = cat != null ? cat.Folder : Config.FolderFor(catName, url, o.Referrer);

            DownloadItem it = new DownloadItem();
            it.Id = Util.NewId();
            it.Url = url;
            it.Category = cat != null ? cat.Name : Config.CategoryNameFor(catName, url, o.Referrer);
            it.NameFromUser = nameFromUser;
            it.FolderFromUser = folderFromUser || cat != null;
            it.Cookies = o.Cookies;
            it.Referrer = o.Referrer;
            it.UserAgentOverride = o.UserAgent;
            it.Added = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            it.Status = DownloadStatus.Queued;
            it.Queue = o.Queue;
            it.Transient = !Persist || o.Transient;
            it.Description = string.IsNullOrEmpty(o.Description) ? null : o.Description;
            it.MediaKind = o.MediaKind; it.MediaVideo = o.MediaVideo; it.MediaAudio = o.MediaAudio;
            it.BrowserProxy = string.IsNullOrEmpty(o.BrowserProxy) ? null : o.BrowserProxy;
            if (!string.IsNullOrEmpty(o.LoginUser)) { it.LoginUser = o.LoginUser; it.LoginPassword = o.LoginPassword; }
            it.Pending = o.Pending;
            it.PostData = string.IsNullOrEmpty(o.PostData) ? null : o.PostData;

            // Reservar la ruta: única frente al disco y frente a las demás descargas de la lista.
            lock (_sync)
            {
                if (!string.IsNullOrEmpty(o.OverwritePath)) { it.FilePath = o.OverwritePath; it.Overwrite = true; }
                else it.FilePath = ReservePathLocked(Path.Combine(folder, Util.FitFileName(folder, fileName)), it);
                it.QueueOrder = ++_queueOrderSeq;
                Items.Add(it);
            }
            SaveList();
            if (o.StartNow) StartItem(it);
            else if (IsQueueRunning(it.Queue)) PumpQueue();
            RaiseChanged();
            return it;
        }

        // Agrupa muchas altas (importar) en un solo guardado.
        public void BeginBatch() { lock (_saveLock) _batchDepth++; }
        public void EndBatch()
        {
            bool save;
            lock (_saveLock) { _batchDepth--; save = _batchDepth == 0 && _batchDirty; if (save) _batchDirty = false; }
            if (save) SaveList();
        }

        // Busca una ruta libre frente al disco y frente a las demás descargas. Sin carpeta temporal crea
        // además el archivo vacío para apartarla; con ella, el archivo no aparece en el destino hasta el
        // final (como IDM) y la reserva es solo en la lista. Llamar con _sync tomado.
        string ReservePathLocked(string wanted, DownloadItem owner)
        {
            string dir = Path.GetDirectoryName(wanted);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (Config.UseTempFolder)
                return Util.UniquePath(wanted, delegate(string p) { return IsPathTakenLocked(p, owner); });
            for (int tries = 0; tries < 50; tries++)
            {
                string path = Util.UniquePath(wanted, delegate(string p) { return IsPathTakenLocked(p, owner); });
                try
                {
                    using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite)) { }
                    return path;
                }
                catch (DirectoryNotFoundException) { break; } // ruta imposible (demasiado larga...): reintentar no sirve
                catch (PathTooLongException) { break; }
                catch (IOException) { /* alguien lo creó justo ahora: buscar otra */ }
            }
            return Util.UniquePath(wanted, delegate(string p) { return IsPathTakenLocked(p, owner); });
        }

        bool IsPathTakenLocked(string path, DownloadItem except)
        {
            foreach (DownloadItem o in Items)
                if (o != except && string.Equals(o.FilePath, path, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public string ReservePath(string wanted, DownloadItem owner)
        {
            lock (_sync) return ReservePathLocked(wanted, owner);
        }

        // Una ruta libre en el disco y en la lista, sin apartarla con un archivo vacío: para mover ahí enseguida un
        // archivo ya terminado (el vacío lo impediría).
        public string FreePath(string wanted, DownloadItem owner)
        {
            lock (_sync) return Util.UniquePath(wanted, delegate(string p) { return IsPathTakenLocked(p, owner); });
        }

        // --- Iniciar / detener ---

        // El usuario inicia la descarga a mano.
        public void StartItem(DownloadItem it) { StartItem(it, false); }

        void StartItem(DownloadItem it, bool byQueue)
        {
            // Si hay un motor parándose (pausa recién pedida), esperar a que suelte el archivo.
            DownloadEngine old = it.Engine;
            if (old != null && old.IsRunning)
            {
                if (!old.StopRequested) return;     // ya está descargando
                old.WaitForExit(10000);
            }

            DownloadEngine eng;
            lock (it.Sync)
            {
                if (it.Removed) return;
                if (it.Engine != null && it.Engine.IsRunning) return;  // otro StartItem ganó la carrera
                if (it.Status == DownloadStatus.Complete) return;
                it.Error = null;
                it.StartedByQueue = byQueue;
                it.StartedByQueueId = byQueue ? it.Queue : -1;
                if (!byQueue) it.UserStopped = false;
                it._lastDone = it.Downloaded;
                it._lastTick = 0;
                it.SpeedBps = 0;
                eng = new DownloadEngine(it, Config);
                eng.PathReserver = delegate(string wanted) { return ReservePath(wanted, it); };
                eng.FreePath = delegate(string wanted) { return FreePath(wanted, it); };
                eng.Finished += OnEngineFinished;
                eng.PathChanged += delegate(DownloadItem x) { SaveList(); RaiseChanged(); };
                it.Engine = eng;
                it.LastTry = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                it.Status = DownloadStatus.Connecting;
                eng.Start();
            }
            RaiseChanged();
        }

        // Retiene la descarga: la para, espera a que suelte el archivo y evita que la cola la relance
        // (UserStopped). Devuelve si estaba en marcha; Release la deja como estaba.
        public bool Hold(DownloadItem it)
        {
            it.UserStopped = true;
            DownloadEngine eng = it.Engine;
            if (eng == null || !eng.IsRunning) return false;
            if (eng.StopRequested) { eng.WaitForExit(10000); return false; } // ya se estaba parando (pausa del usuario)
            eng.Stop();
            eng.WaitForExit(10000);
            return true;
        }

        public void Release(DownloadItem it, bool wasUserStopped, bool wasRunning, bool byQueue)
        {
            it.UserStopped = wasUserStopped;
            if (!wasRunning) return;
            if (byQueue) { if (AnyQueueRunning) PumpQueue(); } // la cola la vuelve a lanzar
            else StartItem(it);
        }

        // El usuario detiene la descarga: la cola no la volverá a lanzar mientras siga en marcha.
        public void StopItem(DownloadItem it)
        {
            it.UserStopped = true;
            if (!it.RememberSpeedLimit) it.SpeedLimitOn = false;
            DownloadEngine eng = it.Engine;
            if (eng != null) eng.Stop();
            RaiseChanged();
        }

        // "Detener todo": para todas las descargas y el procesamiento de la cola.
        public void StopAll()
        {
            StopQueueTimer();
            lock (_running) _running.Clear();
            foreach (DownloadItem it in Snapshot())
                if (it.Engine != null) it.Engine.Stop();
            RaiseChanged();
        }

        // Para todos los motores y espera (con límite) a que suelten los archivos.
        public void StopAllAndWait(int milliseconds)
        {
            StopQueueTimer();
            lock (_running) _running.Clear();
            List<DownloadEngine> engines = new List<DownloadEngine>();
            foreach (DownloadItem it in Snapshot())
                if (it.Engine != null) { it.Engine.Stop(); engines.Add(it.Engine); }
            DateTime limit = DateTime.UtcNow.AddMilliseconds(milliseconds);
            foreach (DownloadEngine e in engines)
            {
                int left = (int)(limit - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0) break;
                e.WaitForExit(left);
            }
        }

        public void Remove(DownloadItem it, bool deleteFile)
        {
            // Marcarla antes de parar: al terminar, el motor avisa a la cola, y StartItem no debe relanzarla.
            DownloadEngine eng;
            lock (it.Sync) { it.Removed = true; eng = it.Engine; }
            // Parar y esperar a que el motor suelte el archivo antes de borrar nada.
            if (eng != null) { eng.Stop(); eng.WaitForExit(10000); }
            it.DeleteState();
            bool unfinishedEmpty = false;
            try { unfinishedEmpty = it.Status != DownloadStatus.Complete && File.Exists(it.FilePath) && new FileInfo(it.FilePath).Length == 0; }
            catch { }
            // El archivo de destino no es de esta descarga si es el que iba a sustituir y se cancela en
            // "Información del archivo", o si otra descarga de la lista usa la misma ruta (el duplicado).
            // Si terminó retenida en la temporal (HeldInTemp), lo que haya en el destino tampoco es suyo: su archivo es el temporal.
            // Igual con una pendiente que baja a la temporal: en el destino aún no ha puesto nada.
            bool notOurs = (it.Overwrite && it.Pending) || it.HeldInTemp || (it.Pending && !string.IsNullOrEmpty(it.TempPath));
            lock (_sync) notOurs |= IsPathTakenLocked(it.FilePath, it);
            if (!notOurs && (deleteFile || unfinishedEmpty))
                try { if (File.Exists(it.FilePath)) File.Delete(it.FilePath); } catch { }
            // Vídeo por streaming: su carpeta de trabajo (los trozos) entera.
            if (it.IsMedia) DownloadEngine.DeleteMediaWork(it, Config);
            // El temporal de una descarga sin terminar no sirve para nada fuera de la lista.
            if (!string.IsNullOrEmpty(it.TempPath))
            {
                try { if (File.Exists(it.TempPath)) File.Delete(it.TempPath); } catch { }
                try { string d = Path.GetDirectoryName(it.TempPath); if (Directory.Exists(d) && Directory.GetFileSystemEntries(d).Length == 0) Directory.Delete(d); } catch { }
            }
            lock (_sync) Items.Remove(it);
            SaveList();
            if (AnyQueueRunning) PumpQueue();
            RaiseChanged();
        }

        void OnEngineFinished(DownloadItem it)
        {
            if (it.Removed) return;
            bool wasQueue = it.StartedByQueue;
            it.StartedByQueue = false;
            it.LastRunByQueue = wasQueue;
            if (it.Status == DownloadStatus.Complete)
            {
                // IDM quita de la cola de descarga los archivos completados, salvo de la de sincronización:
                // esos se quedan para comprobar si cambian en el servidor.
                if (it.Queue != QueueDef.SyncId) it.Queue = DownloadItem.NoQueue;
                else lock (_sync) MarkSyncChecked(it);
                it.QueueFailures = 0;
            }
            else if (it.Status == DownloadStatus.Error && wasQueue)
            {
                // IDM: el archivo fallido pasa al final de la cola y se reintenta cada 30 s.
                it.QueueFailures++;
                it.QueueRetryAfter = DateTime.UtcNow.AddSeconds(QueueRetryDelaySec);
                lock (_sync) it.QueueOrder = ++_queueOrderSeq;
            }
            SaveList();
            RaiseChanged();
            Action<DownloadItem> h = ItemFinished;
            if (h != null) { try { h(it); } catch { } } // un aviso que falla no deja la cola sin continuar

            if (AnyQueueRunning) PumpQueue();
        }

        // --- Diálogo "Información del archivo a descargar" ---
        // La descarga se crea fuera de la lista (Pending) y puede ir bajando mientras se muestra el diálogo.
        // "Iniciar descarga" o "Descargar más tarde" la confirman; "Cancelar" la descarta sin dejar rastro.
        // Si terminó con el diálogo abierto, el archivo sigue en la carpeta temporal (HeldInTemp) y pasa ahora a la ruta
        // que eligió el usuario, como IDM. Si no se puede mover (disco lleno, archivo bloqueado...), lanza la excepción y
        // la descarga sigue pendiente: el diálogo lo dice y el usuario puede elegir otra ruta o cancelar.
        public void Confirm(DownloadItem it)
        {
            bool held;
            // Bajo el cerrojo de la descarga, con el motor (HoldIfPending): si aún no está retenida, deja de estar
            // pendiente y el motor la colocará él al terminar; si ya lo está, el motor ya acabó y la coloca esto.
            lock (it.Sync)
            {
                if (!it.Pending) return;
                held = it.HeldInTemp;
                // Su estado se guarda bajo el cerrojo: si no, el motor podría terminar y borrarlo antes de guardarlo.
                if (!held) { it.Pending = false; it.SaveState(); }
            }
            if (held)
            {
                // Si otro archivo ocupó el nombre entretanto, usa uno libre (queda en it.FilePath).
                Func<string, string> free = delegate(string p) { return FreePath(p, it); };
                DownloadEngine.PlaceFinished(it, Config, free);
                DownloadEngine.PlaceSeparateAudio(it, Config, free);
                if (Config.SetFileDateFromServer) DownloadEngine.SetServerDate(it.FilePath, it.LastModified);
                if (it.IsMedia && it.SeparateAudio == null) DownloadEngine.DeleteMediaWork(it, Config); // (si el audio no se pudo mover, sigue ahí)
                it.DeleteState();
                it.Pending = false;
            }
            SaveList();
            if (IsQueueRunning(it.Queue)) PumpQueue();
            RaiseChanged();
        }

        public void Discard(DownloadItem it) { Remove(it, true); } // la ruta la reservó esta descarga: el parcial es suyo

        // Otra descarga de la lista con la misma dirección ("Enlace de descarga duplicado" de IDM).
        public DownloadItem FindDuplicate(string url, DownloadItem except)
        {
            if (string.IsNullOrEmpty(url)) return null;
            foreach (DownloadItem it in Snapshot())
                if (it != except && !it.Pending && !it.Removed && string.Equals(it.Url, url.Trim(), StringComparison.Ordinal)) return it;
            return null;
        }

        // Igual, sabiendo qué se pide: los subtítulos de una lista HLS/DASH tienen la misma dirección que su vídeo
        // (la lista), pero no son un duplicado de él; sí lo son los mismos subtítulos pedidos otra vez.
        public DownloadItem FindDuplicate(AddOptions o)
        {
            if (o == null || string.IsNullOrEmpty(o.Url)) return null;
            bool subs = o.MediaKind == DownloadEngine.SubtitleKind;
            foreach (DownloadItem it in Snapshot())
            {
                if (it.Pending || it.Removed || !string.Equals(it.Url, o.Url.Trim(), StringComparison.Ordinal)) continue;
                bool itSubs = it.MediaKind == DownloadEngine.SubtitleKind;
                if (subs != itSubs) continue;
                if (subs && (it.MediaVideo ?? "") != (o.MediaVideo ?? "")) continue;
                return it;
            }
            return null;
        }

        // "Redescargar": vuelve a bajar el archivo desde cero; al terminar sustituye al anterior.
        public void Redownload(DownloadItem it)
        {
            ResetForRedownload(it);
            StartItem(it);
        }

        void ResetForRedownload(DownloadItem it)
        {
            DownloadEngine eng = it.Engine;
            if (eng != null) { eng.Stop(); eng.WaitForExit(10000); }
            lock (it.Sync)
            {
                it.Segments.Clear();
                it.Size = -1;
                it.Resumable = false;
                it.ETag = null; it.LastModified = null;
                it.Error = null;
                it.Status = DownloadStatus.Queued;
            }
            if (!string.IsNullOrEmpty(it.TempPath))
            {
                try { if (File.Exists(it.TempPath)) File.Delete(it.TempPath); } catch { }
                it.TempPath = null;
            }
            it.DeleteState();
            it.Overwrite = Config.UseTempFolder && File.Exists(it.FilePath);
            SaveList();
        }

        // "Mover/Renombrar". Completa: mueve el archivo. Sin terminar: cambia el destino (con carpeta temporal
        // el archivo aún no está allí) o mueve el parcial. Si estaba descargando, sigue al acabar.
        public void MoveRename(DownloadItem it, string newPath)
        {
            newPath = Util.FitPath(Path.GetFullPath(newPath));
            if (string.Equals(newPath, it.FilePath, StringComparison.OrdinalIgnoreCase)) return;
            lock (_sync)
                if (IsPathTakenLocked(newPath, it)) throw new IOException("Otra descarga de la lista ya usa ese nombre.");
            if (File.Exists(newPath)) throw new IOException("Ya existe un archivo con ese nombre.");
            DownloadEngine eng = it.Engine;
            bool wasRunning = eng != null && eng.IsRunning && !eng.StopRequested;
            if (eng != null && eng.IsRunning) { eng.Stop(); eng.WaitForExit(10000); }
            Directory.CreateDirectory(Path.GetDirectoryName(newPath));
            bool fileThere = File.Exists(it.FilePath);
            if (it.Status == DownloadStatus.Complete || (string.IsNullOrEmpty(it.TempPath) && fileThere))
            {
                if (fileThere) File.Move(it.FilePath, newPath);
            }
            it.FilePath = newPath;
            it.NameFromUser = true;
            it.FolderFromUser = true;
            it.SaveState();
            SaveList();
            RaiseChanged();
            if (wasRunning) StartItem(it);
        }

        // "Actualizar dirección de descarga": la sesión caducó y el navegador dio una dirección nueva.
        public void ApplyNewAddress(DownloadItem it, string url, string cookies, string referrer, string userAgent)
        {
            DownloadEngine eng = it.Engine;
            if (eng != null && eng.IsRunning) { eng.Stop(); eng.WaitForExit(10000); }
            it.Url = url;
            if (!string.IsNullOrEmpty(cookies)) it.Cookies = cookies;
            if (!string.IsNullOrEmpty(referrer)) it.Referrer = referrer;
            if (!string.IsNullOrEmpty(userAgent)) it.UserAgentOverride = userAgent;
            it.Error = null;
            it.SaveState();
            SaveList();
            StartItem(it);
        }

        // "Eliminar todas las completadas" (solo de la lista; los archivos se quedan).
        public int RemoveCompleted()
        {
            int n = 0;
            BeginBatch();
            try
            {
                foreach (DownloadItem it in Snapshot())
                    if (it.Status == DownloadStatus.Complete && !it.Pending) { Remove(it, false); n++; }
            }
            finally { EndBatch(); }
            return n;
        }

        // Cambia la categoría de las descargas de la lista (los archivos no se mueven del disco, como en IDM).
        // pick devuelve la categoría nueva, o null para dejarla como está.
        public int Recategorize(Func<DownloadItem, string> pick)
        {
            int n = 0;
            foreach (DownloadItem it in Snapshot())
            {
                string c = pick(it);
                if (c != null && c != it.Category) { it.Category = c; n++; }
            }
            if (n > 0) { SaveList(); RaiseChanged(); }
            return n;
        }

        public int RenameCategory(string oldName, string newName)
        {
            return Recategorize(delegate(DownloadItem it) { return it.Category == oldName ? newName : null; });
        }

        // Tras eliminar una categoría, sus descargas vuelven a la que les toque por tipo (o General).
        public int ReassignDeletedCategory(string name)
        {
            return Recategorize(delegate(DownloadItem it)
            {
                return it.Category == name ? Config.CategoryNameFor(it.FileName, it.Url, it.Referrer) : null;
            });
        }

        // "¿Te gustaría colocar todas las descargas antiguas con tipo de archivo ... en esta categoría?"
        public int AssignOldDownloads(Category c)
        {
            return Recategorize(delegate(DownloadItem it)
            {
                return c.Matches(it.FileName, it.Url, it.Referrer) ? c.Name : null;
            });
        }

        public void SetQueue(DownloadItem it, int queue)
        {
            it.Queue = queue;
            if (queue != DownloadItem.NoQueue) lock (_sync) it.QueueOrder = ++_queueOrderSeq;
            SaveList();
            if (AnyQueueRunning) PumpQueue();
            RaiseChanged();
        }

        // Cambia el orden dentro de su cola ("Subir"/"Bajar" del Planificador).
        public void MoveInQueue(DownloadItem it, int direction)
        {
            lock (_sync)
            {
                List<DownloadItem> q = Items.FindAll(delegate(DownloadItem x) { return x.Queue == it.Queue && !x.Pending; });
                q.Sort(delegate(DownloadItem a, DownloadItem b) { return a.QueueOrder.CompareTo(b.QueueOrder); });
                int i = q.IndexOf(it), j = i + direction;
                if (i < 0 || j < 0 || j >= q.Count) return;
                long t = q[i].QueueOrder; q[i].QueueOrder = q[j].QueueOrder; q[j].QueueOrder = t;
            }
            SaveList();
            RaiseChanged();
        }

        public List<DownloadItem> ItemsInQueue(int queue)
        {
            List<DownloadItem> r;
            lock (_sync) r = Items.FindAll(delegate(DownloadItem x) { return x.Queue == queue && !x.Pending; });
            r.Sort(delegate(DownloadItem a, DownloadItem b) { return a.QueueOrder.CompareTo(b.QueueOrder); });
            return r;
        }

        // "Eliminar" una cola del usuario: sus archivos se quedan en la lista, fuera de toda cola.
        public bool DeleteQueue(QueueDef q)
        {
            if (q == null || q.IsMain || q.IsSync) return false;
            StopQueue(q.Id);
            foreach (DownloadItem it in Snapshot()) if (it.Queue == q.Id) it.Queue = DownloadItem.NoQueue;
            Queues.Queues.Remove(q);
            SaveQueues();
            SaveList();
            RaiseChanged();
            return true;
        }

        // --- Colas ---
        public void StartQueue() { StartQueue(QueueDef.MainId); }
        public void StopQueue() { StopQueue(QueueDef.MainId); }

        public void StartQueue(int id)
        {
            QueueDef q = Queues.Find(id);
            if (q == null) return;
            // Al iniciar la cola a mano, también se reanudan las que el usuario había detenido.
            foreach (DownloadItem it in Snapshot())
                if (it.Queue == id) { it.UserStopped = false; it.QueueFailures = 0; it.QueueRetryAfter = DateTime.MinValue; }
            lock (_sync) _syncChecked[id] = new HashSet<string>(); // nueva pasada de comprobación
            q.ProcessedThisRun = 0;
            lock (_running) _running.Add(id);
            StartQueueTimer();
            Action<QueueDef> qs = QueueStarted; if (qs != null) qs(q);
            PumpQueue();
            RaiseChanged();
        }

        // Para la cola y solo las descargas que lanzó ella (no las iniciadas a mano).
        public void StopQueue(int id)
        {
            bool was;
            lock (_running) was = _running.Remove(id);
            if (!AnyQueueRunning) StopQueueTimer();
            QueueDef q = Queues.Find(id);
            Action<QueueDef> qs = QueueStopped; if (was && qs != null && q != null) qs(q);
            foreach (DownloadItem it in Snapshot())
                if (it.StartedByQueue && it.StartedByQueueId == id && it.Engine != null) it.Engine.Stop();
            RaiseChanged();
        }

        void StartQueueTimer()
        {
            lock (_sync)
                if (_queueTimer == null)
                    _queueTimer = new Timer(delegate { try { if (AnyQueueRunning) PumpQueue(); } catch { } }, null, 2000, 2000);
        }

        void StopQueueTimer()
        {
            lock (_sync)
                if (_queueTimer != null) { _queueTimer.Dispose(); _queueTimer = null; }
        }

        bool IsRunning(DownloadItem it) { return it.Engine != null && it.Engine.IsRunning; }

        readonly object _pumpLock = new object();

        public void PumpQueue()
        {
            // Serializado: el temporizador y el final de una descarga pueden llamarlo a la vez.
            lock (_pumpLock)
            {
                int[] ids;
                lock (_running) { ids = new int[_running.Count]; _running.CopyTo(ids); }
                foreach (int id in ids) PumpQueueCore(id);
            }
        }

        void MarkSyncChecked(DownloadItem it)
        {
            HashSet<string> set;
            if (!_syncChecked.TryGetValue(it.Queue, out set)) _syncChecked[it.Queue] = set = new HashSet<string>();
            set.Add(it.Id);
        }

        bool SyncChecked(DownloadItem it)
        {
            HashSet<string> set;
            return _syncChecked.TryGetValue(it.Queue, out set) && set.Contains(it.Id);
        }

        void PumpQueueCore(int id)
        {
            QueueDef q = Queues.Find(id);
            if (q == null) { lock (_running) _running.Remove(id); return; }
            Schedule sc = q.Schedule;
            List<DownloadItem> toStart = new List<DownloadItem>(), toCheck = new List<DownloadItem>();
            bool finished = false;
            lock (_sync)
            {
                int active = 0;
                List<DownloadItem> pending = new List<DownloadItem>();
                bool waitingRetry = false;
                DateTime now = DateTime.UtcNow;
                foreach (DownloadItem it in Items)
                {
                    if (it.Queue != id) continue;
                    if ((it.StartedByQueue && it.StartedByQueueId == id && IsRunning(it)) || _syncChecking.Contains(it.Id)) { active++; continue; }
                    if (IsRunning(it) || it.UserStopped || it.Pending || it.Removed) continue;
                    if (it.Status == DownloadStatus.Complete)
                    {
                        // Cola de sincronización: cada pasada comprueba si el archivo cambió en el servidor.
                        if (q.IsSync && !SyncChecked(it)) pending.Add(it);
                        continue;
                    }
                    if (it.Status == DownloadStatus.Error || it.QueueFailures > 0)
                    {
                        if (sc.RetryLimitEnabled && it.QueueFailures >= sc.RetryLimit) continue; // se rinde
                        if (it.QueueRetryAfter > now) { waitingRetry = true; continue; }
                    }
                    pending.Add(it);
                }
                pending.Sort(delegate(DownloadItem a, DownloadItem b) { return a.QueueOrder.CompareTo(b.QueueOrder); });
                int slots = Math.Max(1, sc.MaxActive) - active;
                foreach (DownloadItem it in pending)
                {
                    if (slots <= 0) break;
                    if (it.Status == DownloadStatus.Complete) { toCheck.Add(it); _syncChecking.Add(it.Id); }
                    else toStart.Add(it);
                    slots--;
                }
                if (active == 0 && toStart.Count == 0 && toCheck.Count == 0 && !waitingRetry)
                {
                    finished = true;
                    lock (_running) _running.Remove(id);
                }
            }
            q.ProcessedThisRun += toStart.Count + toCheck.Count;
            foreach (DownloadItem it in toStart) StartItem(it, true);
            foreach (DownloadItem it in toCheck) StartSyncCheck(it);
            if (finished)
            {
                if (!AnyQueueRunning) StopQueueTimer();
                q.LastFinished = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                SaveQueues();
                RaiseChanged();
                Action<QueueDef> h = QueueFinished; if (h != null) h(q);
            }
        }

        // Comprueba en segundo plano si un archivo de la cola de sincronización cambió en el servidor
        // (fecha, tamaño o ETag). Si cambió, se vuelve a descargar sustituyendo al anterior.
        void StartSyncCheck(DownloadItem it)
        {
            Thread t = new Thread(delegate()
            {
                bool changed = false;
                try { changed = !File.Exists(it.FilePath) || DownloadEngine.RemoteChanged(it, Config); }
                catch { }
                try
                {
                    lock (_sync) { _syncChecking.Remove(it.Id); MarkSyncChecked(it); }
                    if (changed && !it.Removed)
                    {
                        it.SyncUpdated = true;
                        ResetForRedownload(it);
                        lock (_sync) _syncChecked[it.Queue].Remove(it.Id); // la descarga nueva la cuenta al terminar
                        StartItem(it, true);
                    }
                    RaiseChanged();
                    if (AnyQueueRunning) PumpQueue();
                }
                catch { } // hilo propio: un error aquí cerraría IDMClone entero
            });
            t.IsBackground = true;
            t.Name = "idm-sync-" + it.Id;
            t.Start();
        }

        // --- Medición de velocidad (llamar ~2 veces por segundo desde la GUI) ---
        // Media móvil exponencial: la velocidad y el tiempo restante no dan saltos.
        public void Tick()
        {
            long now = Stopwatch.GetTimestamp();
            foreach (DownloadItem it in Snapshot())
            {
                if (it.Status != DownloadStatus.Downloading) { it.SpeedBps = 0; it._lastTick = 0; continue; }
                long done = it.Downloaded;
                if (it._lastTick == 0) { it._lastTick = now; it._lastDone = done; continue; } // primera muestra (sin el tiempo de conexión)
                double dt = (now - it._lastTick) / (double)Stopwatch.Frequency;
                if (dt < 0.3) continue;
                double inst = Math.Max(0, done - it._lastDone) / dt;
                it.SpeedBps = it.SpeedBps <= 0 ? (long)inst : (long)(it.SpeedBps * 0.7 + inst * 0.3);
                it._lastDone = done;
                it._lastTick = now;
            }
        }

        // --- Persistencia de la lista ---
        public void SaveList()
        {
            if (!Persist) return;
            lock (_saveLock) { if (_batchDepth > 0) { _batchDirty = true; return; } }
            try
            {
                List<object> arr = new List<object>();
                foreach (DownloadItem it in Snapshot())
                {
                    if (it.Transient || it.Pending) continue;
                    Dictionary<string, object> o = new Dictionary<string, object>();
                    o["Id"] = it.Id; o["Url"] = it.Url; o["FilePath"] = it.FilePath;
                    o["Category"] = it.Category; o["Size"] = it.Size; o["Resumable"] = it.Resumable;
                    o["Status"] = (int)(it.Status == DownloadStatus.Downloading || it.Status == DownloadStatus.Connecting ? DownloadStatus.Paused : it.Status);
                    o["Added"] = it.Added;
                    o["Queue"] = it.Queue;
                    o["QueueOrder"] = it.QueueOrder;
                    if (it.NameFromUser) o["NameFromUser"] = true;
                    if (it.FolderFromUser) o["FolderFromUser"] = true;
                    if (!string.IsNullOrEmpty(it.Error)) o["Error"] = it.Error;
                    if (!string.IsNullOrEmpty(it.Referrer)) o["Referrer"] = it.Referrer;
                    if (!string.IsNullOrEmpty(it.UserAgentOverride)) o["UserAgent"] = it.UserAgentOverride;
                    if (!string.IsNullOrEmpty(it.ETag)) o["ETag"] = it.ETag;
                    if (!string.IsNullOrEmpty(it.LastModified)) o["LastModified"] = it.LastModified;
                    if (!string.IsNullOrEmpty(it.TempPath)) o["TempPath"] = it.TempPath;
                    // Las cookies son credenciales de sesión: se guardan cifradas para el usuario actual.
                    if (!string.IsNullOrEmpty(it.Cookies)) o["CookiesEnc"] = Util.Protect(it.Cookies);
                    if (!string.IsNullOrEmpty(it.Description)) o["Description"] = it.Description;
                    if (!string.IsNullOrEmpty(it.LastTry)) o["LastTry"] = it.LastTry;
                    if (!string.IsNullOrEmpty(it.LoginUser)) { o["LoginUser"] = it.LoginUser; o["LoginPasswordEnc"] = it.LoginPasswordEnc; }
                    if (it.RememberSpeedLimit) { o["RememberSpeedLimit"] = true; o["SpeedLimitOn"] = it.SpeedLimitOn; o["SpeedLimitKBps"] = it.SpeedLimitKBps; }
                    if (it.Overwrite) o["Overwrite"] = true;
                    // El cuerpo del formulario puede llevar datos de inicio de sesión: cifrado como las cookies.
                    if (!string.IsNullOrEmpty(it.PostData)) o["PostDataEnc"] = Util.Protect(it.PostData);
                    if (it.IsMedia) { o["MediaKind"] = it.MediaKind; o["MediaVideo"] = it.MediaVideo; o["MediaAudio"] = it.MediaAudio; }
                    arr.Add(o);
                }
                string json = AppConfig.NewSerializer().Serialize(arr);
                lock (_saveLock) Util.WriteAllTextAtomic(AppConfig.DownloadsPath, json);
            }
            catch { }
        }

        public void LoadList()
        {
            if (!Persist) return;
            object[] arr;
            string warning;
            Util.TryLoadJson<object[]>(AppConfig.DownloadsPath,
                delegate(string json) { return AppConfig.NewSerializer().Deserialize<object[]>(json); },
                out arr, out warning);
            LoadWarning = warning;
            if (arr == null) return;

            HashSet<string> ids = new HashSet<string>();
            foreach (object o in arr)
            {
                try
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    DownloadItem it = new DownloadItem();
                    it.Id = GetStr(d, "Id"); it.Url = GetStr(d, "Url"); it.FilePath = GetStr(d, "FilePath");
                    it.Category = GetStr(d, "Category"); it.Size = GetLong(d, "Size", -1);
                    it.Resumable = GetBool(d, "Resumable");
                    long st = GetLong(d, "Status", 0);
                    it.Status = Enum.IsDefined(typeof(DownloadStatus), (int)st) ? (DownloadStatus)(int)st : DownloadStatus.Paused;
                    it.Added = GetStr(d, "Added");
                    it.Queue = (int)GetLong(d, "Queue", it.Status == DownloadStatus.Complete ? DownloadItem.NoQueue : DownloadItem.MainQueue);
                    it.QueueOrder = GetLong(d, "QueueOrder", 0);
                    it.NameFromUser = GetBool(d, "NameFromUser");
                    it.FolderFromUser = GetBool(d, "FolderFromUser");
                    it.Error = NullIfEmpty(GetStr(d, "Error"));
                    it.Referrer = NullIfEmpty(GetStr(d, "Referrer"));
                    it.UserAgentOverride = NullIfEmpty(GetStr(d, "UserAgent"));
                    it.ETag = NullIfEmpty(GetStr(d, "ETag"));
                    it.LastModified = NullIfEmpty(GetStr(d, "LastModified"));
                    it.TempPath = NullIfEmpty(GetStr(d, "TempPath"));
                    it.Cookies = Util.Unprotect(GetStr(d, "CookiesEnc"));
                    it.Description = NullIfEmpty(GetStr(d, "Description"));
                    it.LastTry = NullIfEmpty(GetStr(d, "LastTry"));
                    it.LoginUser = NullIfEmpty(GetStr(d, "LoginUser"));
                    it.LoginPasswordEnc = NullIfEmpty(GetStr(d, "LoginPasswordEnc"));
                    it.RememberSpeedLimit = GetBool(d, "RememberSpeedLimit");
                    it.SpeedLimitOn = it.RememberSpeedLimit && GetBool(d, "SpeedLimitOn");
                    it.SpeedLimitKBps = (int)GetLong(d, "SpeedLimitKBps", 0);
                    it.Overwrite = GetBool(d, "Overwrite");
                    it.PostData = NullIfEmpty(Util.Unprotect(GetStr(d, "PostDataEnc")));
                    it.MediaKind = NullIfEmpty(GetStr(d, "MediaKind"));
                    it.MediaVideo = NullIfEmpty(GetStr(d, "MediaVideo"));
                    it.MediaAudio = NullIfEmpty(GetStr(d, "MediaAudio"));
                    if (string.IsNullOrEmpty(it.Id) || ids.Contains(it.Id)) it.Id = Util.NewId();
                    if (!Util.LooksLikeUrl(it.Url) || string.IsNullOrEmpty(it.FilePath)) continue;
                    ids.Add(it.Id);
                    LoadSegments(it);
                    lock (_sync)
                    {
                        if (it.QueueOrder <= 0) it.QueueOrder = _queueOrderSeq + 1;
                        _queueOrderSeq = Math.Max(_queueOrderSeq, it.QueueOrder);
                        Items.Add(it);
                    }
                }
                catch { /* una entrada mala no tumba la lista entera */ }
            }
        }

        void LoadSegments(DownloadItem it)
        {
            Dictionary<string, object> d;
            string warning;
            Util.TryLoadJson<Dictionary<string, object>>(it.StatePath,
                delegate(string json) { return AppConfig.NewSerializer().Deserialize<Dictionary<string, object>>(json); },
                out d, out warning);
            if (d == null) return;
            try
            {
                if (d.ContainsKey("Size")) it.Size = GetLong(d, "Size", it.Size);
                if (d.ContainsKey("Resumable")) it.Resumable = GetBool(d, "Resumable");
                if (d.ContainsKey("ETag")) it.ETag = NullIfEmpty(GetStr(d, "ETag"));
                if (d.ContainsKey("LastModified")) it.LastModified = NullIfEmpty(GetStr(d, "LastModified"));
                // El estado se guarda más a menudo que la lista: su ruta es la buena si el
                // servidor renombró el archivo y la app se cerró de golpe.
                string fp = GetStr(d, "FilePath");
                if (!string.IsNullOrEmpty(fp)) it.FilePath = fp;
                if (d.ContainsKey("TempPath")) it.TempPath = NullIfEmpty(GetStr(d, "TempPath"));
                object segs;
                if (d.TryGetValue("Segments", out segs))
                {
                    // JavaScriptSerializer devuelve las listas anidadas como ArrayList (no object[]).
                    System.Collections.IEnumerable a = segs as System.Collections.IEnumerable;
                    if (a != null && !(segs is string))
                        foreach (object so in a)
                        {
                            Dictionary<string, object> sd = so as Dictionary<string, object>;
                            if (sd == null) continue;
                            long s = GetLong(sd, "Start", 0), c = GetLong(sd, "Current", 0), e = GetLong(sd, "End", 0);
                            if (s < 0 || c < s || e < s) { it.Segments.Clear(); return; } // estado incoherente: empezar de cero
                            if (c > e) c = e;
                            it.Segments.Add(new Segment(s, c, e));
                        }
                }
            }
            catch { it.Segments.Clear(); }
        }

        static string NullIfEmpty(string s) { return string.IsNullOrEmpty(s) ? null : s; }
        static string GetStr(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : ""; }
        static long GetLong(Dictionary<string, object> d, string k, long def) { object v; if (d.TryGetValue(k, out v) && v != null) { long r; if (long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) return r; } return def; }
        static bool GetBool(Dictionary<string, object> d, string k) { object v; if (d.TryGetValue(k, out v) && v != null) { bool r; if (bool.TryParse(v.ToString(), out r)) return r; } return false; }

        void RaiseChanged() { Action h = Changed; if (h != null) h(); }
    }
}
