using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace IDMClone.Tests
{
    // Servidor FTP mínimo (modo pasivo PASV y activo PORT/EPRT) para probar el motor con FTP.
    // Ruta: /f/<modos>/<tamaño>/<id>/<nombre>   modos: normal | slow | missing
    // MDTM da 2026-01-01 10:00:00 UTC. Contadores: "pasv", "port", "mdtm", "<id>:retr", "<id>:rest", "<id>:tls",
    // "<id>:active" (RETR por modo activo), "<id>:passive".
    public class TestFtpServer
    {
        TcpListener _listener;
        volatile bool _stopped;
        public int Port;
        readonly Dictionary<string, int> _counters = new Dictionary<string, int>();
        // FTPS: con certificado admite AUTH TLS (control y datos cifrados con PROT P).
        public X509Certificate2 Cert;
        public bool RequireTls;   // rechaza RETR/SIZE sin cifrar (como los servidores que exigen FTPS)
        // Simula un modo que no funciona en la red:
        //   RejectActive  acepta PORT/EPRT pero no logra conectar con el cliente: RETR -> "425" (cliente detrás de NAT)
        //   RejectPassive PASV anuncia un puerto en el que nadie escucha (cortafuegos del servidor)
        public volatile bool RejectActive, RejectPassive;
        public static readonly DateTime MdtmUtc = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        // Certificado autofirmado para 127.0.0.1 (solo para las pruebas).
        public static X509Certificate2 MakeCert()
        {
            using (RSA rsa = RSA.Create(2048))
            {
                CertificateRequest req = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                SubjectAlternativeNameBuilder san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback); san.AddDnsName("localhost");
                req.CertificateExtensions.Add(san.Build());
                using (X509Certificate2 c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30)))
                    return new X509Certificate2(c.Export(X509ContentType.Pfx), (string)null, X509KeyStorageFlags.Exportable);
            }
        }

        public string FtpsUrl(string modes, string name, long size, string id) { return "ftps" + Url(modes, name, size, id).Substring(3); }

        public string Url(string modes, string name, long size, string id)
        {
            return string.Format(CultureInfo.InvariantCulture, "ftp://127.0.0.1:{0}/f/{1}/{2}/{3}/{4}", Port, modes, size, id, name);
        }

        public int Count(string key) { lock (_counters) { int v; return _counters.TryGetValue(key, out v) ? v : 0; } }
        void Bump(string key) { lock (_counters) { int v; _counters.TryGetValue(key, out v); _counters[key] = v + 1; } }

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
                    try { c = _listener.AcceptTcpClient(); } catch { if (_stopped) return; continue; }
                    Thread s = new Thread(delegate() { try { Session(c); } catch { } finally { try { c.Close(); } catch { } } });
                    s.IsBackground = true; s.Start();
                }
            });
            t.IsBackground = true; t.Start();
        }

        public void Stop() { _stopped = true; try { _listener.Stop(); } catch { } }

        void Session(TcpClient client)
        {
            Stream ns = client.GetStream();
            StreamReader r = new StreamReader(ns, Encoding.ASCII);
            StreamWriter w = new StreamWriter(ns, Encoding.ASCII); w.NewLine = "\r\n"; w.AutoFlush = true;
            w.WriteLine("220 Servidor FTP de pruebas");
            string cwd = "/";
            bool tls = false, protData = false;
            long rest = 0;
            TcpListener pasv = null;
            IPEndPoint active = null; // destino del modo activo (PORT/EPRT)
            try
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    int sp = line.IndexOf(' ');
                    string cmd = (sp < 0 ? line : line.Substring(0, sp)).ToUpperInvariant();
                    string arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();
                    switch (cmd)
                    {
                        case "AUTH":
                            if (Cert == null || !arg.StartsWith("TLS", StringComparison.OrdinalIgnoreCase)) { w.WriteLine("502 AUTH no disponible"); break; }
                            w.WriteLine("234 AUTH TLS OK");
                            SslStream ssl = new SslStream(ns, false);
                            ssl.AuthenticateAsServer(Cert, false, SslProtocols.Tls12, false);
                            ns = ssl; tls = true; Bump("auth-tls");
                            r = new StreamReader(ns, Encoding.ASCII);
                            w = new StreamWriter(ns, Encoding.ASCII); w.NewLine = "\r\n"; w.AutoFlush = true;
                            break;
                        case "PBSZ": w.WriteLine(tls ? "200 PBSZ=0" : "503 Primero AUTH"); break;
                        case "PROT":
                            if (!tls) { w.WriteLine("503 Primero AUTH"); break; }
                            protData = arg.ToUpperInvariant() == "P"; if (protData) Bump("prot-p");
                            w.WriteLine("200 Protección " + arg); break;
                        case "USER":
                            if (RequireTls && !tls) { w.WriteLine("530 Este servidor exige FTPS (AUTH TLS)"); break; }
                            w.WriteLine("331 Contraseña, por favor"); break;
                        case "PASS": w.WriteLine("230 Dentro"); break;
                        case "OPTS": w.WriteLine("200 OK"); break;
                        case "FEAT":
                            w.WriteLine("211-Features:"); w.WriteLine(" SIZE"); w.WriteLine(" REST STREAM");
                            if (Cert != null) { w.WriteLine(" AUTH TLS"); w.WriteLine(" PBSZ"); w.WriteLine(" PROT"); }
                            w.WriteLine("211 End"); break;
                        case "SYST": w.WriteLine("215 UNIX Type: L8"); break;
                        case "PWD": w.WriteLine("257 \"" + cwd + "\" es el directorio actual"); break;
                        case "CWD": cwd = Combine(cwd, arg); w.WriteLine("250 OK"); break;
                        case "TYPE": w.WriteLine("200 Tipo cambiado"); break;
                        case "NOOP": w.WriteLine("200 OK"); break;
                        case "REST": rest = long.Parse(arg, CultureInfo.InvariantCulture); w.WriteLine("350 Reanudando en " + rest); break;
                        case "PASV":
                        {
                            Bump("pasv");
                            if (pasv != null) { try { pasv.Stop(); } catch { } }
                            active = null;
                            pasv = new TcpListener(IPAddress.Loopback, 0); pasv.Start();
                            int p = ((IPEndPoint)pasv.LocalEndpoint).Port;
                            if (RejectPassive) { try { pasv.Stop(); } catch { } pasv = null; } // nadie escuchará ahí
                            w.WriteLine(string.Format("227 Entering Passive Mode (127,0,0,1,{0},{1})", p / 256, p % 256));
                            break;
                        }
                        case "EPSV": w.WriteLine("500 No disponible"); break;
                        case "PORT":
                        case "EPRT":
                        {
                            Bump("port");
                            IPEndPoint ep = cmd == "PORT" ? ParsePort(arg) : ParseEprt(arg);
                            // Solo hacia la propia IP del cliente (como los servidores reales: sin "FTP bounce").
                            if (ep == null || !ep.Address.Equals(((IPEndPoint)client.Client.RemoteEndPoint).Address)) { w.WriteLine("501 Dirección no válida"); break; }
                            if (pasv != null) { try { pasv.Stop(); } catch { } pasv = null; }
                            active = ep;
                            w.WriteLine("200 " + cmd + " OK");
                            break;
                        }
                        case "MDTM":
                        {
                            Bump("mdtm");
                            FtpFile f = Resolve(Combine(cwd, arg));
                            if (f == null || f.Missing) { w.WriteLine("550 No existe"); break; }
                            w.WriteLine("213 " + MdtmUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
                            break;
                        }
                        case "SIZE":
                        {
                            FtpFile f = Resolve(Combine(cwd, arg));
                            if (f == null || f.Missing) { w.WriteLine("550 No existe"); break; }
                            w.WriteLine("213 " + f.Size.ToString(CultureInfo.InvariantCulture));
                            break;
                        }
                        case "RETR":
                        {
                            FtpFile f = Resolve(Combine(cwd, arg));
                            if (f == null || f.Missing) { w.WriteLine("550 No existe"); break; }
                            if (pasv == null && active == null) { w.WriteLine("425 Usa PASV o PORT antes"); break; }
                            if (active != null && RejectActive) { active = null; rest = 0; w.WriteLine("425 No se puede abrir la conexión de datos"); break; }
                            Bump(f.Id + ":retr");
                            Bump(f.Id + (active != null ? ":active" : ":passive"));
                            if (rest > 0) Bump(f.Id + ":rest");
                            w.WriteLine("150 Abriendo conexión de datos");
                            if (protData) Bump(f.Id + ":tls");
                            TcpClient data;
                            if (active != null)
                            {
                                data = new TcpClient(AddressFamily.InterNetwork);
                                try { data.Connect(active); }
                                catch (SocketException) { data.Close(); active = null; rest = 0; w.WriteLine("425 No se puede abrir la conexión de datos"); break; }
                            }
                            else data = pasv.AcceptTcpClient();
                            bool complete = SendData(data, f, rest, protData ? Cert : null);
                            if (pasv != null) { try { pasv.Stop(); } catch { } }
                            pasv = null; active = null; rest = 0;
                            w.WriteLine(complete ? "226 Transferencia completa" : "426 Transferencia cortada");
                            break;
                        }
                        case "ABOR": w.WriteLine("226 Abortado"); break;
                        case "QUIT": w.WriteLine("221 Adiós"); return;
                        default: w.WriteLine("502 No implementado"); break;
                    }
                }
            }
            catch (IOException) { }
            finally { if (pasv != null) { try { pasv.Stop(); } catch { } } }
        }

        // "h1,h2,h3,h4,p1,p2"
        static IPEndPoint ParsePort(string arg)
        {
            string[] p = arg.Split(',');
            if (p.Length != 6) return null;
            try
            {
                IPAddress ip = IPAddress.Parse(p[0] + "." + p[1] + "." + p[2] + "." + p[3]);
                int port = int.Parse(p[4], CultureInfo.InvariantCulture) * 256 + int.Parse(p[5], CultureInfo.InvariantCulture);
                return port > 0 && port < 65536 ? new IPEndPoint(ip, port) : null;
            }
            catch { return null; }
        }

        // "|1|127.0.0.1|5282|" o "|2|::1|5282|"
        static IPEndPoint ParseEprt(string arg)
        {
            if (arg.Length < 7) return null;
            string[] p = arg.Split(arg[0]);
            if (p.Length < 5) return null;
            try
            {
                int port = int.Parse(p[3], CultureInfo.InvariantCulture);
                return port > 0 && port < 65536 ? new IPEndPoint(IPAddress.Parse(p[2]), port) : null;
            }
            catch { return null; }
        }

        bool SendData(TcpClient dataClient, FtpFile f, long from, X509Certificate2 cert)
        {
            using (TcpClient data = dataClient)
            using (Stream ds = cert != null ? (Stream)Secure(data.GetStream(), cert) : data.GetStream())
            {
                int chunk = f.Slow ? 16 * 1024 : 64 * 1024;
                byte[] buf = new byte[chunk];
                long pos = from;
                try
                {
                    while (pos < f.Size)
                    {
                        int n = (int)Math.Min(chunk, f.Size - pos);
                        TestServer.Fill(buf, pos, n);
                        ds.Write(buf, 0, n);
                        pos += n;
                        if (f.Slow) Thread.Sleep(30);
                    }
                    return true;
                }
                catch (IOException) { return false; } // el cliente cortó (fin de segmento o pausa)
            }
        }

        static SslStream Secure(Stream s, X509Certificate2 cert)
        {
            SslStream ssl = new SslStream(s, false);
            ssl.AuthenticateAsServer(cert, false, SslProtocols.Tls12, false);
            return ssl;
        }

        static string Combine(string cwd, string arg)
        {
            if (arg.StartsWith("/")) return arg;
            return cwd.TrimEnd('/') + "/" + arg;
        }

        class FtpFile { public long Size; public string Id; public bool Slow, Missing; }

        static FtpFile Resolve(string path)
        {
            // /f/<modos>/<tamaño>/<id>/<nombre>
            string[] p = path.Trim('/').Split('/');
            if (p.Length < 5 || p[0] != "f") return null;
            FtpFile f = new FtpFile();
            string modes = p[1];
            f.Slow = modes.Contains("slow");
            f.Missing = modes.Contains("missing");
            f.Size = long.Parse(p[2], CultureInfo.InvariantCulture);
            f.Id = p[3];
            return f;
        }
    }
}
