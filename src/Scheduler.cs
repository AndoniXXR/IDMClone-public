using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace IDMClone
{
    // Programador de la cola principal, como el de IDM (Scheduler -> Main download queue):
    //   - "Iniciar la descarga al arrancar IDMClone"
    //   - "Iniciar la descarga a las HH:MM", una vez (fecha) o diariamente (días de la semana)
    //   - "Detener la descarga a las HH:MM" (funciona aunque no esté marcado el inicio)
    //   - "Descargar N archivos a la vez" y "Número de reintentos por archivo" (pestaña Archivos en la cola)
    public class Schedule
    {
        public bool Enabled { get; set; }          // "Iniciar la descarga a las"
        public bool StartAtStartup { get; set; }
        public bool Once { get; set; }             // true = una vez en OnceDate; false = diariamente
        public string OnceDate { get; set; }       // yyyy-MM-dd
        public int StartHour { get; set; }
        public int StartMinute { get; set; }
        public bool[] Days { get; set; }           // 0=Domingo ... 6=Sábado
        public bool StopEnabled { get; set; }
        public int StopHour { get; set; }
        public int StopMinute { get; set; }
        public int MaxActive { get; set; }         // archivos a la vez (IDM: 1 por defecto)
        public bool RetryLimitEnabled { get; set; } // sin marcar = reintentos infinitos cada 30 s (IDM)
        public int RetryLimit { get; set; }        // IDM: 10

        // Último disparo, para no repetirlo y poder recuperar uno perdido (PC suspendido, app cerrada).
        public string LastStartFired { get; set; } // yyyy-MM-ddTHH:mm de la ocurrencia de inicio disparada
        public string LastStopHandled { get; set; } // yyyy-MM-ddTHH:mm de la última parada atendida

        public Schedule()
        {
            Days = new bool[7] { true, true, true, true, true, true, true };
            StartHour = 2;
            MaxActive = 1;
            RetryLimit = 10;
        }

        public static string Path2 { get { return System.IO.Path.Combine(AppConfig.Dir, "scheduler.json"); } }

        public static Schedule Load()
        {
            Schedule s;
            string warning;
            Util.TryLoadJson<Schedule>(Path2,
                delegate(string json) { return AppConfig.NewSerializer().Deserialize<Schedule>(json); },
                out s, out warning);
            if (s == null) return new Schedule();
            s.Normalize();
            return s;
        }

        public void Normalize()
        {
            if (Days == null || Days.Length != 7) Days = new bool[7] { true, true, true, true, true, true, true };
            StartHour = Clamp(StartHour, 0, 23); StartMinute = Clamp(StartMinute, 0, 59);
            StopHour = Clamp(StopHour, 0, 23); StopMinute = Clamp(StopMinute, 0, 59);
            if (MaxActive < 1) MaxActive = 1;
            if (MaxActive > 32) MaxActive = 32;
            if (RetryLimit < 1) RetryLimit = 10;
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        public void Save()
        {
            try { Util.WriteAllTextAtomic(Path2, AppConfig.NewSerializer().Serialize(this)); }
            catch { }
        }

        const string Fmt = "yyyy-MM-dd'T'HH:mm";
        static string Key(DateTime t) { return t.ToString(Fmt, CultureInfo.InvariantCulture); }
        static DateTime ParseKey(string s)
        {
            DateTime t;
            return !string.IsNullOrEmpty(s) && DateTime.TryParseExact(s, Fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out t) ? t : DateTime.MinValue;
        }

        bool StartsOn(DateTime day)
        {
            if (Once)
            {
                DateTime d;
                return DateTime.TryParseExact(OnceDate ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) && d.Date == day.Date;
            }
            int dow = (int)day.DayOfWeek;
            return Days != null && dow < Days.Length && Days[dow];
        }

        // Hasta cuándo vale arrancar una ocurrencia que se perdió: hasta la hora de parada
        // (si la parada es antes o igual que el inicio, es al día siguiente) o, si no hay parada,
        // hasta el final del día.
        DateTime WindowEnd(DateTime startAt)
        {
            if (!StopEnabled) return startAt.Date.AddDays(1);
            DateTime stop = startAt.Date.AddHours(StopHour).AddMinutes(StopMinute);
            if (stop <= startAt) stop = stop.AddDays(1);
            return stop;
        }

        // Devuelve "start", "stop" o null según si toca una acción ahora. Llamar cada pocos segundos.
        // Guarda el disparo para no repetirlo (también entre reinicios de la app).
        public string CheckNow(DateTime now)
        {
            DateTime lastStart = ParseKey(LastStartFired);

            // Inicio: ocurrencias de hoy y de ayer (por si la ventana cruza la medianoche).
            if (Enabled)
            {
                for (int back = 1; back >= 0; back--)
                {
                    DateTime day = now.Date.AddDays(-back);
                    if (!StartsOn(day)) continue;
                    DateTime at = day.AddHours(StartHour).AddMinutes(StartMinute);
                    if (now >= at && now < WindowEnd(at) && at > lastStart)
                    {
                        LastStartFired = Key(at);
                        return "start";
                    }
                }
            }

            // Parada: la más reciente ya pasada, si no se atendió y es posterior al último inicio.
            if (StopEnabled)
            {
                DateTime stop = now.Date.AddHours(StopHour).AddMinutes(StopMinute);
                if (stop > now) stop = stop.AddDays(-1);
                if (stop > ParseKey(LastStopHandled) && stop > lastStart && now - stop < TimeSpan.FromHours(12))
                {
                    LastStopHandled = Key(stop);
                    return "stop";
                }
            }
            return null;
        }
    }

    // Una cola de descarga de IDM: la principal, la de sincronización o una propia ("Nueva cola").
    // Cada una tiene su programación (Schedule), sus archivos a la vez y qué hacer al terminar.
    public class QueueDef
    {
        public const int MainId = 0, SyncId = 1;
        public int Id { get; set; }
        public string Name { get; set; }
        public Schedule Schedule { get; set; }
        // "Repetir la descarga cada H horas M minutos" (la cola de sincronización lo usa para buscar cambios).
        public bool RepeatEnabled { get; set; }
        public int RepeatHours { get; set; }
        public int RepeatMinutes { get; set; }
        public string LastFinished { get; set; }   // ISO: cuándo terminó por última vez (para repetir)
        // Al terminar la cola.
        public bool RunFileWhenDone { get; set; }
        public string RunFile { get; set; }
        public bool ExitWhenDone { get; set; }
        public bool ShutdownWhenDone { get; set; }
        // "Apagar equipo al finalizar": apagar, suspender o hibernar (FinishOptions.Modes; null = apagar) y
        // "Forzar el fin de procesos". Las colas guardadas antes de existir cargan con null/false.
        public string ShutdownMode { get; set; }
        public bool ForceTerminate { get; set; }
        [System.Web.Script.Serialization.ScriptIgnore]
        public string EffectiveShutdownMode
        {
            get { return Array.IndexOf(FinishOptions.Modes, ShutdownMode) >= 0 ? ShutdownMode : FinishOptions.Shutdown; }
        }

        // Archivos que la cola ha lanzado o comprobado en esta pasada (si es 0, al terminar no se hace nada).
        [System.Web.Script.Serialization.ScriptIgnore] public int ProcessedThisRun;

        public bool IsMain { get { return Id == MainId; } }
        public bool IsSync { get { return Id == SyncId; } }

        public QueueDef() { Schedule = new Schedule(); RepeatHours = 2; }

        public static QueueDef Create(int id, string name)
        {
            QueueDef q = new QueueDef(); q.Id = id; q.Name = name; return q;
        }

        // ¿Toca repetirla ya? (terminó hace más del intervalo y no está en marcha)
        public bool RepeatDue(DateTime now)
        {
            if (!RepeatEnabled || string.IsNullOrEmpty(LastFinished)) return false;
            DateTime last;
            if (!DateTime.TryParse(LastFinished, CultureInfo.InvariantCulture, DateTimeStyles.None, out last)) return false;
            TimeSpan every = TimeSpan.FromMinutes(Math.Max(1, RepeatHours * 60 + RepeatMinutes));
            return now - last >= every;
        }
    }

    // Todas las colas (queues.json). La primera vez se crea con la principal (con el programador antiguo,
    // scheduler.json, si lo había) y la de sincronización, como IDM.
    public class QueueSet
    {
        public List<QueueDef> Queues { get; set; }

        public QueueSet() { Queues = new List<QueueDef>(); }

        public static string FilePath { get { return System.IO.Path.Combine(AppConfig.Dir, "queues.json"); } }

        public QueueDef Main { get { return Find(QueueDef.MainId); } }
        public QueueDef Sync { get { return Find(QueueDef.SyncId); } }

        public QueueDef Find(int id)
        {
            foreach (QueueDef q in Queues) if (q.Id == id) return q;
            return null;
        }

        public static QueueSet CreateDefault(Schedule main)
        {
            QueueSet s = new QueueSet();
            QueueDef m = QueueDef.Create(QueueDef.MainId, "Cola principal");
            m.Schedule = main ?? new Schedule();
            s.Queues.Add(m);
            QueueDef sync = QueueDef.Create(QueueDef.SyncId, "Cola de sincronización");
            sync.RepeatEnabled = true; sync.RepeatHours = 2;
            s.Queues.Add(sync);
            return s;
        }

        public static QueueSet Load()
        {
            QueueSet s; string warning;
            Util.TryLoadJson<QueueSet>(FilePath,
                delegate(string json) { return AppConfig.NewSerializer().Deserialize<QueueSet>(json); },
                out s, out warning);
            if (s == null) s = CreateDefault(File.Exists(Schedule.Path2) ? Schedule.Load() : null);
            s.Normalize();
            return s;
        }

        public void Normalize()
        {
            if (Queues == null) Queues = new List<QueueDef>();
            Queues.RemoveAll(delegate(QueueDef q) { return q == null; });
            HashSet<int> ids = new HashSet<int>();
            Queues.RemoveAll(delegate(QueueDef q) { return !ids.Add(q.Id); });
            QueueSet def = CreateDefault(null);
            if (Main == null) Queues.Insert(0, def.Main);
            if (Sync == null) Queues.Insert(1, def.Sync);
            foreach (QueueDef q in Queues)
            {
                if (q.Schedule == null) q.Schedule = new Schedule();
                q.Schedule.Normalize();
                if (string.IsNullOrEmpty(q.Name)) q.Name = "Cola " + (q.Id + 1);
                if (q.RepeatHours < 0) q.RepeatHours = 0;
                if (q.RepeatMinutes < 0 || q.RepeatMinutes > 59) q.RepeatMinutes = 0;
            }
        }

        public QueueDef Add(string name)
        {
            int id = 2;
            while (Find(id) != null) id++;
            QueueDef q = QueueDef.Create(id, string.IsNullOrEmpty(name) ? "Cola " + (id + 1) : name);
            Queues.Add(q);
            return q;
        }

        public void Save()
        {
            try { Util.WriteAllTextAtomic(FilePath, AppConfig.NewSerializer().Serialize(this)); }
            catch { }
        }
    }
}
