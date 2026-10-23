using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace IDMClone
{
    // Representa una descarga: su estado persistente + contadores en vivo.
    public class DownloadItem
    {
        public string Id { get; set; }
        public string Url { get; set; }
        public string FilePath { get; set; }
        public string Category { get; set; }
        public long Size { get; set; }          // -1 si se desconoce
        public bool Resumable { get; set; }
        public DownloadStatus Status { get; set; }
        public string Error { get; set; }
        public string Added { get; set; }        // fecha en texto ISO
        public int Queue { get; set; }           // 0 = cola principal, -1 = en ninguna cola
        public string Cookies { get; set; }      // cabecera Cookie (de la extensión)
        public string Referrer { get; set; }     // Referer
        public string UserAgentOverride { get; set; } // UA del navegador, si viene
        public string ETag { get; set; }         // validadores del servidor: detectan si el archivo
        public string LastModified { get; set; } // cambió entre sesiones (se envían en If-Range)
        public bool NameFromUser { get; set; }   // el usuario/navegador fijó el nombre: no renombrar
        public bool FolderFromUser { get; set; } // el usuario eligió la carpeta: no moverla por categoría
        public long QueueOrder { get; set; }     // posición en la cola (los fallidos pasan al final)
        public string TempPath { get; set; }     // archivo de trabajo en la carpeta temporal (null = se escribe en FilePath)
        public string Description { get; set; }  // columna "Descripción" (la escribe el usuario)
        public string LastTry { get; set; }      // "Última fecha de intento" (ISO)
        public string LoginUser { get; set; }    // "usar autorización" de esta descarga (Agregar URL / Propiedades)
        public string LoginPasswordEnc { get; set; }
        public bool SpeedLimitOn { get; set; }   // limitador de velocidad de esta descarga (ventana de progreso)
        public int SpeedLimitKBps { get; set; }
        public bool RememberSpeedLimit { get; set; }
        public bool Overwrite { get; set; }      // "Redescargar": al terminar sustituye al archivo existente
        [ScriptIgnore] public string PostData;
        public string BrowserProxy { get; set; } // proxy del navegador para esta descarga ("host:puerto" HTTP o "socks5://host:puerto"...), o null
        // Vídeo por streaming (HLS/DASH): la Url es la lista de reproducción; se baja por trozos y se une al final.
        public string MediaKind { get; set; }    // "hls", "dash", "pair", "subs" (subtítulos) o null
        public string MediaVideo { get; set; }   // calidad elegida (URL de su lista o id de la Representation); en "subs", la pista de subtítulos
        public string MediaAudio { get; set; }   // pista de audio aparte ("-" = sin audio)
        [ScriptIgnore] public bool IsMedia { get { return !string.IsNullOrEmpty(MediaKind); } }   // cuerpo de un formulario (descargas que el sitio da tras un POST)

        [ScriptIgnore] public string LoginPassword
        {
            get { return Util.Unprotect(LoginPasswordEnc) ?? ""; }
            set { LoginPasswordEnc = string.IsNullOrEmpty(value) ? null : Util.Protect(value); }
        }

        // Opciones de finalización de la ventana de progreso (solo para esta sesión, como en IDM).
        [ScriptIgnore] public bool? ShowCompleteDialog;
        [ScriptIgnore] public bool ExitWhenDone, ShutdownWhenDone;
        // Mientras está abierto el diálogo "Información del archivo a descargar": aún no está en la lista.
        [ScriptIgnore] public volatile bool Pending;
        [ScriptIgnore] public bool LastRunByQueue; // la última ejecución la lanzó la cola
        [ScriptIgnore] public int StartedByQueueId = -1; // qué cola la lanzó
        [ScriptIgnore] public bool SyncUpdated;      // la cola de sincronización vio que cambió y la volvió a bajar

        // Archivo donde se está escribiendo ahora: el temporal mientras descarga, o el final.
        [ScriptIgnore] public string WorkingPath { get { return string.IsNullOrEmpty(TempPath) ? FilePath : TempPath; } }
        public List<Segment> Segments { get; set; }

        public const int NoQueue = -1;
        public const int MainQueue = 0;

        // --- Runtime (no se serializa) ---
        [ScriptIgnore] public long SpeedBps;
        [ScriptIgnore] public long _lastDone;
        [ScriptIgnore] public long _lastTick;          // Stopwatch.GetTimestamp() de la última muestra
        [ScriptIgnore] public volatile bool StartedByQueue; // la lanzó la cola (la cola solo para las suyas)
        [ScriptIgnore] public volatile bool UserStopped;    // el usuario la detuvo: la cola no la relanza
        [ScriptIgnore] public DateTime QueueRetryAfter;     // fallida en cola: no reintentar antes de esta hora
        [ScriptIgnore] public int QueueFailures;
        [ScriptIgnore] public DownloadEngine Engine;
        [ScriptIgnore] public readonly object Sync = new object();
        [ScriptIgnore] public volatile bool Transient; // modo consola /n: no deja nada en disco salvo el archivo
        [ScriptIgnore] public volatile bool Removed;   // quitada de la lista: no volver a guardar su estado

        public DownloadItem()
        {
            Segments = new List<Segment>();
            Size = -1;
            Status = DownloadStatus.Queued;
        }

        public string FileName
        {
            get { try { return Path.GetFileName(FilePath); } catch { return FilePath; } }
        }

        // Bytes descargados = suma de lo hecho por cada segmento.
        public long Downloaded
        {
            get
            {
                long sum = 0;
                lock (Sync)
                    foreach (Segment s in Segments) sum += s.Done;
                return sum;
            }
        }

        public long Remaining
        {
            get { if (Size < 0) return -1; long r = Size - Downloaded; return r < 0 ? 0 : r; }
        }

        public int ActiveConnections
        {
            get { int n = 0; lock (Sync) foreach (Segment s in Segments) if (s.Active) n++; return n; }
        }

        public string StatePath
        {
            get { return Path.Combine(AppConfig.StateDir, Id + ".json"); }
        }

        // Guarda el progreso de los segmentos (varias veces por minuto, como IDM).
        public void SaveState()
        {
            if (Transient || Removed || Pending) return;
            try
            {
                Dictionary<string, object> o = new Dictionary<string, object>();
                o["Url"] = Url; o["FilePath"] = FilePath; o["Size"] = Size; o["Resumable"] = Resumable;
                if (!string.IsNullOrEmpty(TempPath)) o["TempPath"] = TempPath;
                if (!string.IsNullOrEmpty(ETag)) o["ETag"] = ETag;
                if (!string.IsNullOrEmpty(LastModified)) o["LastModified"] = LastModified;
                List<object> segs = new List<object>();
                lock (Sync)
                    foreach (Segment s in Segments)
                    {
                        Dictionary<string, object> sd = new Dictionary<string, object>();
                        sd["Start"] = s.Start; sd["Current"] = s.Current; sd["End"] = s.End;
                        segs.Add(sd);
                    }
                o["Segments"] = segs;
                // Lo que dicen estas posiciones ya se escribió en el archivo: volcarlo a disco antes de
                // guardarlas, para que tras un corte de luz no se reanude dejando huecos sin datos.
                if (segs.Count > 0) FlushToDisk(WorkingPath);
                string json = AppConfig.NewSerializer().Serialize(o);
                lock (_stateWriteLock)
                {
                    if (Removed) return;
                    Util.WriteAllTextAtomic(StatePath, json);
                }
            }
            catch { }
        }

        [ScriptIgnore] readonly object _stateWriteLock = new object();

        // FlushFileBuffers vuelca todo lo pendiente del archivo, lo escribiera el handle que lo escribiera.
        static void FlushToDisk(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    fs.Flush(true);
            }
            catch { }
        }

        public void DeleteState()
        {
            lock (_stateWriteLock) Util.DeleteWithBackups(StatePath);
        }
    }
}
