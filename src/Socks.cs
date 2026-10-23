using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace IDMClone
{
    // Servidor SOCKS de Opciones > Proxy/Socks > "Avanzado... / Socks..." (SOCKS4, SOCKS4a y SOCKS5 con
    // usuario y contraseña, RFC 1928 y RFC 1929).
    //
    // HttpWebRequest no sabe hablar SOCKS: las peticiones http y https van a un puente local (SocksBridge) que
    // hace de proxy HTTP en 127.0.0.1 y sale por el servidor SOCKS. El FTP no pasa por aquí (FtpWebRequest no
    // tiene forma de usar SOCKS): sigue yendo directo.
    //
    // "Proxy DNS" (IDM: Avanzado... / Socks...): con él, el nombre del servidor lo resuelve el servidor SOCKS
    // (SOCKS5 con ATYP 3, SOCKS4a); sin él, este equipo resuelve el nombre y manda la IP (SOCKS5 con ATYP 1/4,
    // SOCKS4). En SOCKS4 lo decide el tipo: "4" resuelve aquí y "4a" en el servidor.
    public class SocksSettings
    {
        public string Type = "5";     // "4", "4a" o "5"
        public string Host;
        public int Port = 1080;
        public string User;
        public string Password;
        public bool RemoteDns = true; // solo SOCKS5: true = manda el nombre (Proxy DNS); false = lo resuelve este equipo

        // Los ajustes SOCKS de la configuración, o null si no hay que usarlo.
        public static SocksSettings From(AppConfig cfg)
        {
            if (cfg == null || !cfg.SocksEnabled || string.IsNullOrEmpty((cfg.SocksHost ?? "").Trim())) return null;
            SocksSettings s = new SocksSettings();
            // Opciones: "Socks 5" / "Socks 4" y "Proxy DNS". Socks 4 con Proxy DNS es SOCKS4a.
            s.Type = cfg.SocksType == "4" && cfg.SocksProxyDns ? "4a" : cfg.SocksType;
            s.RemoteDns = cfg.SocksProxyDns || s.Type == "4a";
            s.Host = cfg.SocksHost.Trim();
            s.Port = cfg.SocksPort;
            s.User = string.IsNullOrEmpty(cfg.SocksUser) ? null : cfg.SocksUser;
            s.Password = Util.Unprotect(cfg.SocksPasswordEnc) ?? "";
            return s;
        }

        // Proxy SOCKS que usó el navegador (#17, DownloadItem.BrowserProxy): "socks5://host:puerto" ("socks5h://" y
        // "socks://" son lo mismo: el SOCKS5 de aquí siempre manda el nombre), "socks4://" o "socks4a://"; "[ipv6]" vale.
        // Sin usuario: el navegador no da la contraseña. null si no es eso.
        public static SocksSettings FromBrowserProxy(string proxy)
        {
            if (string.IsNullOrEmpty(proxy) || proxy.Length > 300) return null;
            string[] schemes = { "socks5h://", "socks5://", "socks://", "socks4a://", "socks4://" };
            string[] types = { "5", "5", "5", "4a", "4" };
            for (int i = 0; i < schemes.Length; i++)
            {
                if (!proxy.StartsWith(schemes[i], StringComparison.OrdinalIgnoreCase)) continue;
                string hp = proxy.Substring(schemes[i].Length).TrimEnd('/');
                int colon = hp.LastIndexOf(':');
                if (colon <= 0 || colon < hp.LastIndexOf(']') || hp.IndexOfAny(new char[] { '/', '@', ' ', '?', '#', '\\' }) >= 0) return null;
                int port;
                if (!int.TryParse(hp.Substring(colon + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port) ||
                    port < 1 || port > 65535) return null;
                string host = hp.Substring(0, colon);
                if (host.StartsWith("[") && host.EndsWith("]")) host = host.Substring(1, host.Length - 2);
                else if (host.IndexOf(':') >= 0) return null;
                if (host.Length == 0 || Uri.CheckHostName(host) == UriHostNameType.Unknown) return null;
                SocksSettings s = new SocksSettings();
                s.Type = types[i]; s.Host = host; s.Port = port;
                return s;
            }
            return null;
        }

        internal string Key { get { return Type + (RemoteDns ? "" : "-ip") + "\n" + Host + "\n" + Port + "\n" + (User ?? "") + "\n" + (Password ?? ""); } }

        public override string ToString() { return "SOCKS" + Type + " " + Host + ":" + Port; }
    }

    // Fallo del servidor SOCKS. Code: auth, rule, proto (no se arreglan reintentando), connect, dns, refused,
    // unreachable, timeout, fail.
    public class SocksException : IOException
    {
        public readonly string Code;
        public SocksException(string message, string code) : base(message) { Code = code; }
        public bool Permanent { get { return IsPermanentCode(Code); } }
        public static bool IsPermanentCode(string code) { return code == "auth" || code == "rule" || code == "proto"; }
    }

    public static class SocksClient
    {
        // Abre una conexión con host:port a través del servidor SOCKS. Devuelve el socket ya negociado.
        public static Socket Connect(string host, int port, SocksSettings s, int timeoutMs)
        {
            Socket sock = ConnectServer(s, timeoutMs);
            try
            {
                sock.ReceiveTimeout = timeoutMs; sock.SendTimeout = timeoutMs;
                using (NetworkStream ns = new NetworkStream(sock, false))
                {
                    if (s.Type == "5") Socks5(ns, host, port, s);
                    else Socks4(ns, host, port, s);
                }
                sock.ReceiveTimeout = 0; sock.SendTimeout = 0;
                return sock;
            }
            catch (IOException ex)
            {
                Close(sock);
                if (ex is SocksException) throw;
                SocketException se = ex.InnerException as SocketException;
                if (se != null && se.SocketErrorCode == SocketError.TimedOut)
                    throw new SocksException("El servidor SOCKS " + s.Host + ":" + s.Port + " no responde.", "timeout");
                throw new SocksException("El servidor SOCKS " + s.Host + ":" + s.Port + " cortó la conexión: " + ex.Message, "fail");
            }
            catch (SocketException ex)
            {
                Close(sock);
                throw new SocksException("Error con el servidor SOCKS " + s.Host + ":" + s.Port + ": " + ex.Message, "fail");
            }
            catch { Close(sock); throw; }
        }

        static Socket ConnectServer(SocksSettings s, int timeoutMs)
        {
            IPAddress[] addrs;
            IPAddress one;
            if (IPAddress.TryParse(s.Host, out one)) addrs = new IPAddress[] { one };
            else
            {
                try { addrs = Dns.GetHostAddresses(s.Host); }
                catch (Exception ex) { throw new SocksException("No se encuentra el servidor SOCKS " + s.Host + ": " + ex.Message, "connect"); }
            }
            DateTime limit = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            string last = null;
            for (int i = 0; i < addrs.Length; i++)
            {
                IPAddress a = addrs[i];
                // Cada dirección se lleva su parte del tiempo: una IPv6 que no responde no deja sin probar la IPv4.
                int left = (int)(limit - DateTime.UtcNow).TotalMilliseconds / (addrs.Length - i);
                if (left <= 0) break;
                Socket sock = new Socket(a.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    IAsyncResult ar = sock.BeginConnect(a, s.Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(left)) { Close(sock); last = "tiempo de espera agotado"; continue; }
                    sock.EndConnect(ar);
                    sock.NoDelay = true;
                    return sock;
                }
                catch (Exception ex) { Close(sock); last = ex.Message; }
            }
            throw new SocksException("No se puede conectar con el servidor SOCKS " + s.Host + ":" + s.Port + (last != null ? " (" + last + ")" : "") + ". Revisa el servidor SOCKS (Opciones > Proxy/Socks o el proxy del navegador).", "connect");
        }

        static void Close(Socket s) { try { s.Close(); } catch { } }

        static void Socks5(NetworkStream ns, string host, int port, SocksSettings s)
        {
            bool auth = !string.IsNullOrEmpty(s.User);
            Write(ns, auth ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 });
            byte[] r = Read(ns, 2);
            if (r[0] != 5) throw new SocksException("El servidor " + s.Host + ":" + s.Port + " no es un servidor SOCKS5.", "proto");
            if (r[1] == 0xFF || (r[1] == 2 && !auth))
                throw new SocksException(auth ? "El servidor SOCKS no acepta autenticación con usuario y contraseña."
                                              : "El servidor SOCKS pide usuario y contraseña: escríbelos en Opciones > Proxy/Socks > Avanzado... / Socks...", "auth");
            if (r[1] == 2)
            {
                // RFC 1929: versión 1, usuario y contraseña (hasta 255 bytes cada uno).
                byte[] u = Encoding.UTF8.GetBytes(s.User), p = Encoding.UTF8.GetBytes(s.Password ?? "");
                if (u.Length > 255 || p.Length > 255) throw new SocksException("El usuario o la contraseña SOCKS son demasiado largos (máximo 255 bytes).", "auth");
                MemoryStream m = new MemoryStream();
                m.WriteByte(1); m.WriteByte((byte)u.Length); m.Write(u, 0, u.Length); m.WriteByte((byte)p.Length); m.Write(p, 0, p.Length);
                Write(ns, m.ToArray());
                byte[] a = Read(ns, 2);
                if (a[1] != 0) throw new SocksException("El servidor SOCKS rechazó el usuario o la contraseña. Revísalos en Opciones > Proxy/Socks > Avanzado... / Socks...", "auth");
            }
            else if (r[1] != 0) throw new SocksException("El servidor SOCKS pide un método de autenticación que IDMClone no admite (" + r[1] + ").", "proto");

            // CONNECT. Con Proxy DNS, un nombre (ATYP 3) lo resuelve el servidor SOCKS: así no hay consultas DNS desde
            // este equipo. Sin él, se resuelve aquí y se manda la IP (ATYP 1 o 4).
            MemoryStream q = new MemoryStream();
            q.WriteByte(5); q.WriteByte(1); q.WriteByte(0);
            IPAddress ip;
            string h = host.Trim('[', ']');
            if (!IPAddress.TryParse(h, out ip) && !s.RemoteDns) ip = ResolveLocal(h, host, false);
            if (ip != null)
            {
                q.WriteByte(ip.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)4 : (byte)1);
                byte[] b = ip.GetAddressBytes(); q.Write(b, 0, b.Length);
            }
            else
            {
                byte[] b = AsciiName(h);
                if (b.Length > 255) throw new SocksException("Nombre de servidor demasiado largo para SOCKS.", "proto");
                q.WriteByte(3); q.WriteByte((byte)b.Length); q.Write(b, 0, b.Length);
            }
            q.WriteByte((byte)(port >> 8)); q.WriteByte((byte)port);
            Write(ns, q.ToArray());
            byte[] rep = Read(ns, 4);
            if (rep[0] != 5) throw new SocksException("Respuesta SOCKS5 no válida.", "proto");
            if (rep[1] != 0) throw Socks5Error(rep[1], host, port);
            int skip = rep[3] == 1 ? 4 : rep[3] == 4 ? 16 : rep[3] == 3 ? Read(ns, 1)[0] : -1;
            if (skip < 0) throw new SocksException("Respuesta SOCKS5 no válida (tipo de dirección " + rep[3] + ").", "proto");
            Read(ns, skip + 2);
        }

        static SocksException Socks5Error(int code, string host, int port)
        {
            string why, kind = "fail";
            switch (code)
            {
                case 1: why = "fallo general del servidor SOCKS"; break;
                case 2: why = "las reglas del servidor SOCKS no permiten esta conexión"; kind = "rule"; break;
                case 3: why = "red inalcanzable"; kind = "unreachable"; break;
                case 4: why = "no se puede llegar al servidor"; kind = "unreachable"; break;
                case 5: why = "el servidor rechazó la conexión"; kind = "refused"; break;
                case 6: why = "tiempo de espera agotado"; kind = "timeout"; break;
                case 7: why = "orden no admitida por el servidor SOCKS"; kind = "proto"; break;
                case 8: why = "tipo de dirección no admitido por el servidor SOCKS"; kind = "proto"; break;
                default: why = "error desconocido"; break;
            }
            return new SocksException("El servidor SOCKS no pudo conectar con " + host + ":" + port + ": " + why + " (código " + code + ").", kind);
        }

        static void Socks4(NetworkStream ns, string host, int port, SocksSettings s)
        {
            string h = host.Trim('[', ']');
            IPAddress ip;
            bool literal = IPAddress.TryParse(h, out ip);
            if (literal && ip.AddressFamily != AddressFamily.InterNetwork)
                throw new SocksException("SOCKS4 solo admite direcciones IPv4 (" + host + " es IPv6): usa SOCKS5.", "proto");
            bool remoteName = !literal && s.Type == "4a";
            if (!literal && !remoteName) ip = ResolveLocal(h, host, true);   // SOCKS4 puro: el nombre se resuelve en este equipo
            MemoryStream q = new MemoryStream();
            q.WriteByte(4); q.WriteByte(1); q.WriteByte((byte)(port >> 8)); q.WriteByte((byte)port);
            byte[] addr = remoteName ? new byte[] { 0, 0, 0, 1 } : ip.GetAddressBytes();
            q.Write(addr, 0, 4);
            byte[] user = Encoding.UTF8.GetBytes(s.User ?? "");
            q.Write(user, 0, user.Length); q.WriteByte(0);
            if (remoteName) { byte[] n = AsciiName(h); q.Write(n, 0, n.Length); q.WriteByte(0); }
            Write(ns, q.ToArray());
            byte[] r = Read(ns, 8);
            if (r[1] == 0x5A) return;
            if (r[1] == 0x5D) throw new SocksException("El servidor SOCKS4 no acepta el usuario \"" + (s.User ?? "") + "\" (identd).", "auth");
            if (r[1] == 0x5C) throw new SocksException("El servidor SOCKS4 no pudo comprobar el usuario (identd).", "auth");
            if (r[1] == 0x5B) throw new SocksException("El servidor SOCKS4 rechazó la conexión con " + host + ":" + port + " o no pudo establecerla.", "refused");
            throw new SocksException("Respuesta SOCKS4 no válida (" + r[1] + ").", "proto");
        }

        // Sin Proxy DNS: el nombre se resuelve en este equipo. Primero IPv4 (SOCKS4 solo admite IPv4; en SOCKS5 es
        // lo que suele llegar a todas partes) y, en SOCKS5, si no hay, IPv6.
        static IPAddress ResolveLocal(string h, string host, bool v4Only)
        {
            IPAddress[] all;
            try { all = Dns.GetHostAddresses(h); }
            catch (Exception ex)
            {
                throw new SocksException("No se encuentra el servidor " + host + " (" + (v4Only ? "con SOCKS4" : "sin \"Proxy DNS\"") +
                                         " los nombres se resuelven en este equipo): " + ex.Message, "dns");
            }
            foreach (IPAddress a in all) if (a.AddressFamily == AddressFamily.InterNetwork) return a;
            if (!v4Only) foreach (IPAddress a in all) if (a.AddressFamily == AddressFamily.InterNetworkV6) return a;
            if (v4Only) throw new SocksException(host + " no tiene dirección IPv4: SOCKS4 no puede llegar a él. Usa SOCKS4a o SOCKS5.", "dns");
            throw new SocksException("No se encuentra el servidor " + host + ".", "dns");
        }

        // Nombre en ASCII (IDN -> punycode); un nombre mal formado es un error claro, no una excepción suelta.
        static byte[] AsciiName(string h)
        {
            try { return Encoding.ASCII.GetBytes(new System.Globalization.IdnMapping().GetAscii(h)); }
            catch (ArgumentException) { throw new SocksException("Nombre de servidor no válido: " + h, "proto"); }
        }

        static void Write(Stream s, byte[] b) { s.Write(b, 0, b.Length); s.Flush(); }

        static byte[] Read(Stream s, int n)
        {
            byte[] b = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = s.Read(b, got, n - got);
                if (r <= 0) throw new SocksException("El servidor SOCKS cerró la conexión durante la negociación.", "fail");
                got += r;
            }
            return b;
        }
    }

    // Proxy HTTP local (solo 127.0.0.1, puerto aleatorio) que reenvía por SOCKS lo que le pide HttpWebRequest:
    //  - "CONNECT host:443" (https): túnel; el TLS va de extremo a extremo, el puente solo copia bytes.
    //  - "GET http://host/ruta" (http): reescribe la petición a la forma de origen y fuerza "Connection: close"
    //    (en las dos direcciones) para que .NET no reutilice la conexión con el puente para otro servidor.
    // Cada configuración SOCKS es una "ruta" con un token aleatorio de la sesión que .NET manda en
    // Proxy-Authorization (Basic); sin él, el puente responde 407: otros procesos del equipo no pueden usarlo
    // como proxy abierto.
    public static class SocksBridge
    {
        class Route { public string Id, Token; public SocksSettings S; }

        static readonly object _lock = new object();
        static readonly Dictionary<string, Route> _byKey = new Dictionary<string, Route>();
        static readonly Dictionary<string, Route> _byId = new Dictionary<string, Route>();
        static TcpListener _listener;
        static int _port, _nextId, _active;
        public const int MaxConnections = 512;      // conexiones simultáneas con el puente (2 hilos pequeños cada una)
        const int MaxPreAuth = 64;                  // de ellas, las que aún no han mandado el token
        const int MaxRequestHead = 65536;           // cabecera de la petición (las cookies pueden ser grandes)
        const int StackSize = 256 * 1024;
        public static int ConnectTimeoutMs = 30000;
        // Fallo SOCKS: respuesta 502 con el código (auth, rule, proto, connect...) y el mensaje (codificado %XX).
        public const string ErrorHeader = "X-IDMClone-Socks";
        public const string MessageHeader = "X-IDMClone-Socks-Message";
        // Marca secreta de esta sesión que solo lleva el 502 del propio puente: cualquier servidor puede mandar
        // cabeceras X-IDMClone-Socks (en http el puente las quita; dentro de un túnel https no puede), pero sin
        // esta marca no se creen. No es el token de las rutas: ese lo manda .NET y esta solo la manda el puente.
        public const string MarkerHeader = "X-IDMClone-Socks-Auth";
        static readonly string _marker = NewToken();
        const int PreAuthSeconds = 3;               // tiempo para mandar la cabecera antes de autorizar
        static readonly object _pendingLock = new object();
        static readonly LinkedList<Socket> _pending = new LinkedList<Socket>();   // sin autorizar, de la más antigua a la más nueva

        public static int ActiveConnections { get { return Thread.VolatileRead(ref _active); } }

        public static Uri Address
        {
            get { EnsureStarted(); return new Uri("http://127.0.0.1:" + _port + "/"); }
        }

        // Usuario (id de la ruta) y token con los que .NET se identifica ante el puente para esta configuración.
        public static NetworkCredential CredentialFor(SocksSettings s)
        {
            lock (_lock)
            {
                Route r;
                if (!_byKey.TryGetValue(s.Key, out r))
                {
                    r = new Route();
                    r.Id = "s" + (++_nextId);
                    r.Token = NewToken();
                    r.S = s;
                    _byKey[s.Key] = r; _byId[r.Id] = r;
                }
                return new NetworkCredential(r.Id, r.Token);
            }
        }

        static string NewToken()
        {
            byte[] b = new byte[24];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        static void EnsureStarted()
        {
            lock (_lock)
            {
                if (_listener != null) return;
                TcpListener l = new TcpListener(IPAddress.Loopback, 0);
                l.Start(64);
                _port = ((IPEndPoint)l.LocalEndpoint).Port;
                _listener = l;
                Thread t = new Thread(AcceptLoop);
                t.IsBackground = true; t.Name = "SocksBridge";
                t.Start();
            }
        }

        static void AcceptLoop()
        {
            while (true)
            {
                Socket c;
                try { c = _listener.AcceptSocket(); }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { Thread.Sleep(50); continue; }
                if (Interlocked.Increment(ref _active) > MaxConnections)
                {
                    Interlocked.Decrement(ref _active);
                    Busy(c);
                    continue;
                }
                try
                {
                    Thread t = new Thread(delegate() { try { Serve(c); } catch { } finally { Interlocked.Decrement(ref _active); } }, StackSize);
                    t.IsBackground = true; t.Name = "SocksBridge conn";
                    t.Start();
                }
                catch
                {
                    // Sin memoria para otro hilo: se rechaza esta conexión, el puente sigue vivo.
                    Interlocked.Decrement(ref _active);
                    Busy(c);
                }
            }
        }

        // Demasiadas conexiones: 503 y cierre (en vez de cortar sin decir nada).
        static void Busy(Socket c)
        {
            try
            {
                c.SendTimeout = 2000;
                byte[] b = Encoding.ASCII.GetBytes("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\nProxy-Connection: close\r\n\r\n");
                c.Send(b);
            }
            catch { }
            try { c.Close(); } catch { }
        }

        class Request
        {
            public string Method, Target, Version;
            public List<KeyValuePair<string, string>> Headers = new List<KeyValuePair<string, string>>();
            public string Get(string name)
            {
                foreach (KeyValuePair<string, string> h in Headers) if (string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)) return h.Value;
                return null;
            }
        }

        static void Serve(Socket client)
        {
            Socket up = null;
            NetworkStream cs = new NetworkStream(client, true);
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 10000; client.SendTimeout = 30000;
                Request req;
                Route route;
                // Antes de autorizar: pocas conexiones a la vez y poco tiempo para mandar la cabecera, así otro
                // proceso del equipo no puede ocupar el puente sin conocer el token. Si ya hay demasiadas sin
                // autorizar, se cierra la más antigua (no la nueva): unas conexiones lentas no bloquean las de .NET,
                // que mandan la cabecera entera nada más conectar.
                LinkedListNode<Socket> node;
                Socket evict = null;
                lock (_pendingLock)
                {
                    node = _pending.AddLast(client);
                    if (_pending.Count > MaxPreAuth) { evict = _pending.First.Value; _pending.RemoveFirst(); }
                }
                if (evict != null) { try { evict.Close(); } catch { } }
                try
                {
                    bool tooBig;
                    client.ReceiveTimeout = PreAuthSeconds * 1000;
                    string head = ReadHead(cs, MaxRequestHead, DateTime.UtcNow.AddSeconds(PreAuthSeconds), out tooBig);
                    if (tooBig)
                    {
                        Reply(cs, 431, "Request Header Fields Too Large", null, null, "Cabecera demasiado grande.");
                        // Se lee (y tira) un poco lo que quede para que el cierre no se convierta en un RST que se coma el 431.
                        try { client.Shutdown(SocketShutdown.Send); client.ReceiveTimeout = 1000; byte[] junk = new byte[8192]; for (int i = 0; i < 16 && cs.Read(junk, 0, junk.Length) > 0; i++) { } } catch { }
                        return;
                    }
                    if (head == null) return;
                    req = Parse(head);
                    if (req == null) { Reply(cs, 400, "Bad Request", null, null, "Petición no válida."); return; }
                    route = Authorize(req.Get("Proxy-Authorization"));
                }
                finally { lock (_pendingLock) { if (node.List != null) _pending.Remove(node); } }
                client.ReceiveTimeout = 10000;
                if (route == null)
                {
                    // Sin el token: 407 y cierre. .NET repite la petición con el token en una conexión nueva
                    // (con "keep-alive" podría encadenar otra petición detrás, y el puente atiende una por conexión).
                    Send(cs, "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"IDMClone\"\r\nContent-Length: 0\r\n" +
                             "Proxy-Connection: close\r\nConnection: close\r\n\r\n");
                    return;
                }

                bool connect = req.Method == "CONNECT";
                string host; int port;
                Uri uri = null;
                if (connect)
                {
                    if (!SplitHostPort(req.Target, out host, out port)) { Reply(cs, 400, "Bad Request", null, null, "Destino no válido."); return; }
                }
                else
                {
                    if (!Uri.TryCreate(req.Target, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttp)
                    { Reply(cs, 400, "Bad Request", null, null, "El puente SOCKS solo admite http:// y CONNECT."); return; }
                    host = uri.DnsSafeHost; port = uri.Port;
                }

                try { up = SocksClient.Connect(host, port, route.S, ConnectTimeoutMs); }
                catch (SocksException ex)
                {
                    Reply(cs, 502, "Bad Gateway", ex.Code, ex.Message, ex.Message);
                    return;
                }
                catch (Exception ex)
                {
                    string m = "Error con el servidor SOCKS " + route.S.Host + ":" + route.S.Port + ": " + ex.Message;
                    Reply(cs, 502, "Bad Gateway", "fail", m, m);
                    return;
                }
                client.ReceiveTimeout = 0; client.SendTimeout = 0;
                NetworkStream us = new NetworkStream(up, false);

                if (connect)
                {
                    Send(cs, "HTTP/1.1 200 Connection established\r\n\r\n");
                    Socket upRef = up;
                    Thread t = StartCopy(us, cs, client, upRef);
                    Copy(cs, us);
                    CloseBoth(client, upRef);
                    t.Join(5000);
                    return;
                }

                // http: petición en forma de origen, sin cabeceras del proxy y con "Connection: close".
                StringBuilder sb = new StringBuilder();
                sb.Append(req.Method).Append(' ').Append(uri.PathAndQuery).Append(' ').Append(req.Version).Append("\r\n");
                foreach (KeyValuePair<string, string> h in req.Headers)
                {
                    if (IsHopHeader(h.Key)) continue;
                    sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
                }
                sb.Append("Connection: close\r\n\r\n");
                Send(us, sb.ToString());

                // Cuerpo de la petición (POST): solo lo que anuncia; después, cualquier otra cosa que mande .NET
                // (peticiones en cadena) se descarta y el cierre del cliente corta también la conexión SOCKS.
                long len;
                bool chunked = (req.Get("Transfer-Encoding") ?? "").IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!chunked && long.TryParse((req.Get("Content-Length") ?? "0").Trim(), out len) && len > 0) CopyExactly(cs, us, len);
                Socket upSock = up;
                Thread w = chunked ? StartCopy(cs, us, client, upSock) : StartDrain(cs, client, upSock);

                // Respuesta: se reescribe la cabecera (y las 1xx que haya antes) con "Connection: close".
                bool sent = false;
                while (true)
                {
                    bool big;
                    string rh = ReadHead(us, 65536, DateTime.MaxValue, out big);
                    if (rh == null)
                    {
                        // Respuesta sin cabecera válida (cortada, enorme o con saltos de línea raros): 502 claro en vez
                        // de pasar a .NET unos bytes sin cabecera.
                        if (!sent) Reply(cs, 502, "Bad Gateway", null, null, "El servidor " + host + " no envió una respuesta HTTP válida a través de SOCKS.");
                        return;
                    }
                    sent = true;
                    int status;
                    Send(cs, RewriteResponseHead(rh, out status));
                    if (status < 100 || status >= 200 || status == 101) break;
                }
                Copy(us, cs);
                CloseBoth(client, upSock);
                w.Join(5000);
            }
            finally
            {
                try { cs.Close(); } catch { }
                if (up != null) { try { up.Close(); } catch { } }
            }
        }

        static Route Authorize(string header)
        {
            if (string.IsNullOrEmpty(header)) return null;
            header = header.Trim();
            if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return null;
            string dec;
            try { dec = Encoding.UTF8.GetString(Convert.FromBase64String(header.Substring(6).Trim())); }
            catch { return null; }
            int c = dec.IndexOf(':');
            if (c <= 0) return null;
            string id = dec.Substring(0, c), token = dec.Substring(c + 1);
            Route r;
            lock (_lock) { if (!_byId.TryGetValue(id, out r)) return null; }
            return SameToken(r.Token, token) ? r : null;
        }

        // Comparación en tiempo constante (no deja adivinar el token por lo que tarda en responder).
        static bool SameToken(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        static bool IsHopHeader(string name)
        {
            return name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Connection", StringComparison.OrdinalIgnoreCase) || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase);
        }

        static string RewriteResponseHead(string head, out int status)
        {
            string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            status = 0;
            string[] sl = lines[0].Split(' ');
            if (sl.Length >= 2) int.TryParse(sl[1], out status);
            StringBuilder sb = new StringBuilder(lines[0]).Append("\r\n");
            for (int i = 1; i < lines.Length; i++)
            {
                string l = lines[i];
                if (l.Length == 0) continue;
                int c = l.IndexOf(':');
                if (c > 0 && IsHopHeader(l.Substring(0, c).Trim())) continue;
                // Las cabeceras de error del puente solo las pone el puente: las que mande el servidor se quitan.
                if (c > 0 && l.Substring(0, c).Trim().StartsWith(ErrorHeader, StringComparison.OrdinalIgnoreCase)) continue;
                sb.Append(l).Append("\r\n");
            }
            if (status >= 200 || status < 100) sb.Append("Connection: close\r\nProxy-Connection: close\r\n");
            return sb.Append("\r\n").ToString();
        }

        static Request Parse(string head)
        {
            string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            string[] rl = lines[0].Split(' ');
            if (rl.Length != 3 || !rl[2].StartsWith("HTTP/1.")) return null;
            Request r = new Request();
            r.Method = rl[0]; r.Target = rl[1]; r.Version = rl[2];
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c > 0) r.Headers.Add(new KeyValuePair<string, string>(lines[i].Substring(0, c).Trim(), lines[i].Substring(c + 1).Trim()));
            }
            return r;
        }

        static bool SplitHostPort(string target, out string host, out int port)
        {
            host = null; port = 0;
            int c = target.LastIndexOf(':');
            if (c <= 0 || c == target.Length - 1) return false;
            if (!int.TryParse(target.Substring(c + 1), out port) || port < 1 || port > 65535) return false;
            host = target.Substring(0, c).Trim('[', ']');
            return host.Length > 0;
        }

        // Cabecera HTTP hasta la línea en blanco, byte a byte (no se lee nada del cuerpo). null si se corta, pasa
        // de max (tooBig) o no termina antes de deadline. Acepta también saltos de línea "\n" sueltos, como .NET.
        static string ReadHead(Stream s, int max, DateTime deadline, out bool tooBig)
        {
            tooBig = false;
            StringBuilder sb = new StringBuilder();
            int nl = 0;   // saltos de línea seguidos (ignorando los \r)
            try
            {
                int b;
                while ((b = s.ReadByte()) >= 0)
                {
                    sb.Append((char)b);
                    if (b == '\n') { if (++nl == 2) return Normalize(sb.ToString()); }
                    else if (b != '\r') nl = 0;
                    if (sb.Length > max) { tooBig = true; return null; }
                    if (deadline != DateTime.MaxValue && DateTime.UtcNow > deadline) return null;
                }
            }
            catch (IOException) { return null; }
            catch (ObjectDisposedException) { return null; }
            return null;
        }

        // Cabecera con saltos "\r\n" y sin la línea en blanco final.
        static string Normalize(string head)
        {
            string[] lines = head.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            List<string> keep = new List<string>();
            foreach (string l in lines) if (l.Length > 0) keep.Add(l);
            return string.Join("\r\n", keep.ToArray());
        }

        static void Send(Stream s, string text)
        {
            byte[] b = Encoding.GetEncoding(28591).GetBytes(text);   // Latin-1: los bytes de la cabecera tal cual
            s.Write(b, 0, b.Length);
            s.Flush();
        }

        static void Reply(Stream s, int code, string reason, string socksCode, string message, string body)
        {
            byte[] b = Encoding.UTF8.GetBytes(body ?? "");
            StringBuilder sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
            if (socksCode != null) sb.Append(ErrorHeader).Append(": ").Append(socksCode).Append("\r\n");
            if (message != null) sb.Append(MessageHeader).Append(": ").Append(Uri.EscapeDataString(message)).Append("\r\n");
            if (socksCode != null || message != null) sb.Append(MarkerHeader).Append(": ").Append(_marker).Append("\r\n");
            sb.Append("Content-Type: text/plain; charset=utf-8\r\nContent-Length: ").Append(b.Length).Append("\r\nConnection: close\r\nProxy-Connection: close\r\n\r\n");
            try { Send(s, sb.ToString()); s.Write(b, 0, b.Length); s.Flush(); } catch { }
        }

        static Thread StartCopy(Stream from, Stream to, Socket a, Socket b)
        {
            Thread t = new Thread(delegate() { Copy(from, to); CloseBoth(a, b); }, StackSize);
            t.IsBackground = true; t.Name = "SocksBridge copy";
            t.Start();
            return t;
        }

        // Lee y descarta lo que mande el cliente tras la petición; cuando cierra (o aborta), cierra las dos conexiones.
        static Thread StartDrain(Stream from, Socket a, Socket b)
        {
            Thread t = new Thread(delegate()
            {
                byte[] buf = new byte[4096];
                try { while (from.Read(buf, 0, buf.Length) > 0) { } } catch { }
                CloseBoth(a, b);
            }, StackSize);
            t.IsBackground = true; t.Name = "SocksBridge watch";
            t.Start();
            return t;
        }

        static void Copy(Stream from, Stream to)
        {
            byte[] buf = new byte[65536];
            try
            {
                int n;
                while ((n = from.Read(buf, 0, buf.Length)) > 0) to.Write(buf, 0, n);
                to.Flush();
            }
            catch { }
        }

        static void CopyExactly(Stream from, Stream to, long len)
        {
            byte[] buf = new byte[65536];
            while (len > 0)
            {
                int n = from.Read(buf, 0, (int)Math.Min(buf.Length, len));
                if (n <= 0) throw new IOException("El cliente cortó el cuerpo de la petición.");
                to.Write(buf, 0, n);
                len -= n;
            }
        }

        static void CloseBoth(Socket a, Socket b)
        {
            foreach (Socket s in new Socket[] { a, b })
            {
                if (s == null) continue;
                try { s.Shutdown(SocketShutdown.Send); } catch { }
                try { s.Close(); } catch { }
            }
        }

        // Mensaje para el usuario si el fallo de una petición vino del puente SOCKS; null si no.
        public static string Explain(WebException wex)
        {
            HttpWebResponse h = FromBridge(wex);
            if (h == null) return null;
            string code = h.Headers[ErrorHeader];
            if (code == null) return null;
            string msg = h.Headers[MessageHeader];
            try { if (msg != null) return Uri.UnescapeDataString(msg); } catch { }
            return "Falló la conexión a través del servidor SOCKS (" + code + ").";
        }

        // Código del fallo SOCKS (connect, timeout, fail, proto, auth, dns, refused, rule) si vino del puente; null si no.
        public static string FailureCode(WebException wex)
        {
            HttpWebResponse h = FromBridge(wex);
            return h != null ? h.Headers[ErrorHeader] : null;
        }

        // ¿Es un fallo SOCKS que no se arregla reintentando (usuario o contraseña, reglas del servidor)?
        public static bool IsPermanent(WebException wex)
        {
            HttpWebResponse h = FromBridge(wex);
            string code = h != null ? h.Headers[ErrorHeader] : null;
            return code != null && SocksException.IsPermanentCode(code);
        }

        // La respuesta del error, solo si es un 502 del propio puente (lleva la marca secreta de esta sesión).
        static HttpWebResponse FromBridge(WebException wex)
        {
            HttpWebResponse h = wex != null ? wex.Response as HttpWebResponse : null;
            if (h == null) return null;
            try
            {
                if ((int)h.StatusCode != 502) return null;
                string mark = h.Headers[MarkerHeader];
                return mark != null && SameToken(_marker, mark.Trim()) ? h : null;
            }
            catch (ObjectDisposedException) { return null; }
        }
    }
}
