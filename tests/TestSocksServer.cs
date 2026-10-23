using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace IDMClone.Tests
{
    // Servidor SOCKS4/4a/5 mínimo para las pruebas (con usuario y contraseña RFC 1929 opcional).
    // Solo lleva a destinos de prueba: los nombres "*.invalid", 127.0.0.1 y 192.0.2.x (red de documentación)
    // se convierten en 127.0.0.1 con el puerto pedido (el de TestServer). Cualquier otro destino se rechaza.
    // Contadores: conn, v4, v4a, v5, atyp1, atyp3, atyp4, auth-ok, auth-bad, refused, host:<nombre>, user:<usuario>.
    public class TestSocksServer
    {
        TcpListener _listener;
        volatile bool _stopped;
        public int Port;
        public string User, Password;    // si User no es null, SOCKS5 exige usuario y contraseña
        readonly Dictionary<string, int> _counters = new Dictionary<string, int>();

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Thread t = new Thread(delegate()
            {
                while (!_stopped)
                {
                    TcpClient c;
                    try { c = _listener.AcceptTcpClient(); }
                    catch { if (_stopped) return; continue; }
                    Thread h = new Thread(delegate() { try { Handle(c); } catch { } finally { try { c.Close(); } catch { } } });
                    h.IsBackground = true;
                    h.Start();
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        public void Stop() { _stopped = true; try { _listener.Stop(); } catch { } }

        public int Count(string key) { lock (_counters) { int v; return _counters.TryGetValue(key, out v) ? v : 0; } }
        void Bump(string key) { lock (_counters) { int v; _counters.TryGetValue(key, out v); _counters[key] = v + 1; } }

        static IPAddress Map(string host)
        {
            if (host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host.StartsWith("192.0.2.")) return IPAddress.Loopback;
            return null;
        }

        static byte[] Read(NetworkStream ns, int n)
        {
            byte[] b = new byte[n];
            int got = 0;
            while (got < n) { int r = ns.Read(b, got, n - got); if (r <= 0) throw new Exception("cortado"); got += r; }
            return b;
        }

        static string ReadZ(NetworkStream ns)
        {
            StringBuilder sb = new StringBuilder();
            int b;
            while ((b = ns.ReadByte()) > 0) sb.Append((char)b);
            return sb.ToString();
        }

        void Handle(TcpClient c)
        {
            c.NoDelay = true;
            NetworkStream ns = c.GetStream();
            Bump("conn");
            int ver = ns.ReadByte();
            string host; int port;
            if (ver == 4)
            {
                byte[] h = Read(ns, 7);   // orden, puerto, IPv4
                port = (h[1] << 8) | h[2];
                string user = ReadZ(ns);
                Bump("user:" + user);
                if (h[3] == 0 && h[4] == 0 && h[5] == 0 && h[6] != 0) { host = ReadZ(ns); Bump("v4a"); }
                else { host = h[3] + "." + h[4] + "." + h[5] + "." + h[6]; Bump("v4"); }
                Bump("host:" + host);
                TcpClient up = Open(host, port);
                ns.Write(new byte[] { 0, (byte)(up != null ? 0x5A : 0x5B), 0, 0, 0, 0, 0, 0 }, 0, 8);
                if (up == null) { Bump("refused"); return; }
                Pump(c, up);
                return;
            }
            if (ver != 5) return;
            Bump("v5");
            int nm = ns.ReadByte();
            byte[] methods = Read(ns, nm);
            if (User != null)
            {
                if (Array.IndexOf(methods, (byte)2) < 0) { ns.Write(new byte[] { 5, 0xFF }, 0, 2); return; }
                ns.Write(new byte[] { 5, 2 }, 0, 2);
                Read(ns, 1);
                string u = Encoding.UTF8.GetString(Read(ns, ns.ReadByte()));
                string p = Encoding.UTF8.GetString(Read(ns, ns.ReadByte()));
                Bump("user:" + u);
                bool ok = u == User && p == Password;
                ns.Write(new byte[] { 1, (byte)(ok ? 0 : 1) }, 0, 2);
                Bump(ok ? "auth-ok" : "auth-bad");
                if (!ok) return;
            }
            else
            {
                if (Array.IndexOf(methods, (byte)0) < 0) { ns.Write(new byte[] { 5, 0xFF }, 0, 2); return; }
                ns.Write(new byte[] { 5, 0 }, 0, 2);
            }
            byte[] req = Read(ns, 4);
            Bump("atyp" + req[3]);
            if (req[3] == 1) { byte[] a = Read(ns, 4); host = a[0] + "." + a[1] + "." + a[2] + "." + a[3]; }
            else if (req[3] == 3) host = Encoding.ASCII.GetString(Read(ns, ns.ReadByte()));
            else if (req[3] == 4) host = new IPAddress(Read(ns, 16)).ToString();
            else return;
            byte[] pb = Read(ns, 2);
            port = (pb[0] << 8) | pb[1];
            Bump("host:" + host);
            TcpClient t = req[1] == 1 ? Open(host, port) : null;
            ns.Write(new byte[] { 5, (byte)(t != null ? 0 : 5), 0, 1, 127, 0, 0, 1, 0, 0 }, 0, 10);
            if (t == null) { Bump("refused"); return; }
            Pump(c, t);
        }

        static TcpClient Open(string host, int port)
        {
            IPAddress ip = Map(host);
            if (ip == null) return null;
            try { TcpClient t = new TcpClient(); t.Connect(ip, port); t.NoDelay = true; return t; }
            catch { return null; }
        }

        static void Pump(TcpClient a, TcpClient b)
        {
            NetworkStream sa = a.GetStream(), sb = b.GetStream();
            Thread t = new Thread(delegate() { Copy(sb, sa); Close(a, b); });
            t.IsBackground = true;
            t.Start();
            Copy(sa, sb);
            Close(a, b);
            t.Join(5000);
        }

        static void Copy(NetworkStream from, NetworkStream to)
        {
            byte[] buf = new byte[65536];
            try { int n; while ((n = from.Read(buf, 0, buf.Length)) > 0) to.Write(buf, 0, n); } catch { }
        }

        static void Close(TcpClient a, TcpClient b)
        {
            try { a.Client.Shutdown(SocketShutdown.Send); } catch { }
            try { b.Client.Shutdown(SocketShutdown.Send); } catch { }
            try { a.Close(); } catch { }
            try { b.Close(); } catch { }
        }
    }
}
