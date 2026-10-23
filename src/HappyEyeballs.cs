using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;

namespace IDMClone
{
    // "Happy Eyeballs" (RFC 8305), como los navegadores e IDM: si un servidor anuncia una dirección IPv6
    // que no responde, .NET Framework la prueba primero y espera ~21 s a que caduque antes de pasar a la
    // IPv4. Aquí, la primera vez que se conecta a un servidor con direcciones de las dos familias, se
    // hace una carrera (la siguiente dirección arranca si la anterior no conecta en 250 ms) y las
    // direcciones que .NET probaría antes de la ganadora y que no contestaron se saltan durante un rato.
    // Se salta lanzando una excepción en BindIPEndPointDelegate: .NET la trata como un fallo de conexión
    // con esa dirección y pasa enseguida a la siguiente.
    public static class HappyEyeballs
    {
        public const int StaggerMs = 250;        // RFC 8305: "Connection Attempt Delay" recomendado
        const int RaceTimeoutMs = 10000;
        static readonly TimeSpan Remember = TimeSpan.FromMinutes(10);

        static readonly Dictionary<string, DateTime> _checked = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, DateTime> _slow = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, object> _hostLocks = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        public static bool Global { get; private set; }
        public static int Races;                  // carreras hechas (para los tests)

        // Engancha todos los ServicePoint que cree .NET (incluidos los de las redirecciones automáticas).
        // Si el runtime no lo permite, cada petición lo pone en el suyo con Use().
        public static void Install()
        {
            try
            {
                FieldInfo f = typeof(ServicePointManager).GetField("s_ServicePointTable", BindingFlags.NonPublic | BindingFlags.Static);
                Hashtable old = f != null ? f.GetValue(null) as Hashtable : null;
                if (old == null) return;
                if (old is HookTable) { Global = true; return; }
                lock (old)
                {
                    HookTable t = new HookTable();
                    foreach (DictionaryEntry e in old) t.Add(e.Key, e.Value);
                    f.SetValue(null, t);
                }
                Global = true;
            }
            catch { }
        }

        class HookTable : Hashtable
        {
            public override object this[object key]
            {
                get { return base[key]; }
                set { Attach(value); base[key] = value; }
            }
            public override void Add(object key, object value) { Attach(value); base.Add(key, value); }
            static void Attach(object v)
            {
                WeakReference w = v as WeakReference;
                ServicePoint sp = (w != null ? w.Target : v) as ServicePoint;
                if (sp != null) Use(sp);
            }
        }

        // Olvida lo aprendido de cada servidor (para los tests).
        public static void Forget()
        {
            lock (_checked) _checked.Clear();
            lock (_slow) _slow.Clear();
        }

        public static void Use(ServicePoint sp)
        {
            try { if (sp != null && sp.BindIPEndPointDelegate == null) sp.BindIPEndPointDelegate = Bind; } catch { }
        }

        static IPEndPoint Bind(ServicePoint sp, IPEndPoint remote, int retry)
        {
            if (retry > 0 || remote == null) return null;
            string host;
            try { host = sp.Address.DnsSafeHost; } catch { return null; }
            if (ShouldSkip(host, remote))
                throw new SocketException((int)SocketError.TimedOut);
            return null; // sin enlazar a una dirección local concreta: lo normal
        }

        public static bool ShouldSkip(string host, IPEndPoint remote)
        {
            if (string.IsNullOrEmpty(host)) return false;
            IPAddress literal;
            if (IPAddress.TryParse(host, out literal)) return false;
            IPAddress[] all;
            try { all = Dns.GetHostAddresses(host); } catch { return false; }
            if (!Mixed(all)) return false;
            Check(host, all, remote.Port);
            lock (_slow)
            {
                DateTime until;
                return _slow.TryGetValue(Key(host, remote.Address), out until) && until > DateTime.UtcNow;
            }
        }

        static bool Mixed(IPAddress[] a)
        {
            bool v4 = false, v6 = false;
            foreach (IPAddress x in a) { if (x.AddressFamily == AddressFamily.InterNetwork) v4 = true; else if (x.AddressFamily == AddressFamily.InterNetworkV6) v6 = true; }
            return v4 && v6;
        }

        static string Key(string host, IPAddress a) { return host + "|" + a; }

        // Una carrera por servidor cada 10 minutos (las demás conexiones esperan a la primera).
        static void Check(string host, IPAddress[] all, int port)
        {
            object hl;
            lock (_hostLocks) { if (!_hostLocks.TryGetValue(host, out hl)) _hostLocks[host] = hl = new object(); }
            lock (hl)
            {
                lock (_checked) { DateTime t; if (_checked.TryGetValue(host, out t) && DateTime.UtcNow - t < Remember) return; }
                int[] state = Race(all, port);
                Interlocked.Increment(ref Races);
                DateTime until = DateTime.UtcNow + Remember;
                lock (_slow) foreach (int i in SlowBeforeWinner(state)) _slow[Key(host, all[i])] = until;
                lock (_checked) _checked[host] = DateTime.UtcNow;
            }
        }

        // Direcciones que .NET probaría antes de la ganadora (va en orden) y que no conectaron.
        // state: 1 conectó, -1 falló, 0 sin respuesta. Sin ganadora no se salta ninguna.
        public static List<int> SlowBeforeWinner(int[] state)
        {
            List<int> r = new List<int>();
            int win = Array.IndexOf(state, 1);
            if (win < 0) return r;
            for (int i = 0; i < win; i++) if (state[i] != 1) r.Add(i);
            return r;
        }

        // Carrera: arranca la primera dirección; la siguiente, si en 250 ms nadie conectó (o en cuanto la
        // anterior falle). Termina con la primera que conecte. Devuelve el estado de cada dirección.
        static int[] Race(IPAddress[] all, int port)
        {
            int n = all.Length;
            int[] state = new int[n];
            Socket[] socks = new Socket[n];
            object sync = new object();
            using (AutoResetEvent changed = new AutoResetEvent(false))
            {
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(RaceTimeoutMs);
                try
                {
                    int next = 0;
                    while (true)
                    {
                        int win, pending = 0;
                        lock (sync) { win = Array.IndexOf(state, 1); for (int i = 0; i < next; i++) if (state[i] == 0) pending++; }
                        if (win >= 0) break;
                        if (next < n && pending == 0) { Start(all, socks, state, next++, port, sync, changed); continue; }
                        if (pending == 0) break;                    // todas fallaron
                        double left = (deadline - DateTime.UtcNow).TotalMilliseconds;
                        if (left <= 0) break;
                        if (!changed.WaitOne(next < n ? StaggerMs : (int)left) && next < n)
                            Start(all, socks, state, next++, port, sync, changed);
                    }
                    lock (sync) return (int[])state.Clone();
                }
                finally
                {
                    lock (sync) foreach (Socket s in socks) if (s != null) { try { s.Close(); } catch { } }
                }
            }
        }

        static void Start(IPAddress[] all, Socket[] socks, int[] state, int i, int port, object sync, AutoResetEvent changed)
        {
            Socket s;
            try { s = new Socket(all[i].AddressFamily, SocketType.Stream, ProtocolType.Tcp); }
            catch { lock (sync) state[i] = -1; Signal(changed); return; }
            lock (sync) socks[i] = s;
            try
            {
                s.BeginConnect(new IPEndPoint(all[i], port), delegate(IAsyncResult ar)
                {
                    bool ok;
                    try { s.EndConnect(ar); ok = true; } catch { ok = false; }
                    lock (sync) if (state[i] == 0) state[i] = ok ? 1 : -1;
                    Signal(changed);
                }, null);
            }
            catch { lock (sync) state[i] = -1; Signal(changed); }
        }

        // El intento puede terminar después de la carrera, con el evento ya cerrado.
        static void Signal(AutoResetEvent e) { try { e.Set(); } catch (ObjectDisposedException) { } }
    }
}
