using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace IDMClone.Tests
{
    // Servidor HTTP con autenticación NTLM y Digest de verdad: comprueba criptográficamente lo que manda
    // el cliente (la parte NTLM la genera Windows, SSPI), con conexiones persistentes como exige NTLM.
    //   /ntlm/<nombre>?size=N&id=X                 NTLMv2, usuario "usuario" / "clave123"
    //   /digest/<MD5|SHA-256>/<nombre>?size=N&id=X Digest qop=auth, mismo usuario
    // Sirve el mismo contenido determinista que TestServer, con Range, a ~530 KB/s por conexión.
    public class AuthServer
    {
        public const string User = "usuario", Password = "clave123";
        TcpListener _listener;
        volatile bool _stopped;
        public int Port;
        readonly Dictionary<string, int> _counters = new Dictionary<string, int>();
        readonly string _nonce = Guid.NewGuid().ToString("N");

        public string Url(string path, string name, long size, string id)
        {
            return string.Format(CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/{1}/{2}?size={3}&id={4}", Port, path, name, size, id);
        }

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
                    Thread w = new Thread(delegate() { try { Handle(c); } catch { } finally { try { c.Close(); } catch { } } });
                    w.IsBackground = true;
                    w.Start();
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        public void Stop() { _stopped = true; try { _listener.Stop(); } catch { } }

        public int Count(string key) { lock (_counters) { int v; return _counters.TryGetValue(key, out v) ? v : 0; } }
        void Bump(string key) { lock (_counters) { int v; _counters.TryGetValue(key, out v); _counters[key] = v + 1; } }

        void Handle(TcpClient client)
        {
            client.NoDelay = true;
            NetworkStream ns = client.GetStream();
            byte[] serverChallenge = null; // estado NTLM de esta conexión
            while (true)
            {
                string head = ReadHead(ns);
                if (head == null) return;
                string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                string[] rl = lines[0].Split(' ');
                if (rl.Length < 2) return;
                string method = rl[0], target = rl[1];
                Dictionary<string, string> h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    int c = lines[i].IndexOf(':');
                    if (c > 0) h[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
                }
                string path = target, query = "";
                int q = target.IndexOf('?');
                if (q >= 0) { path = target.Substring(0, q); query = target.Substring(q + 1); }
                long size = 0; string id = "x";
                foreach (string kv in query.Split('&'))
                {
                    int e = kv.IndexOf('=');
                    if (e < 0) continue;
                    if (kv.Substring(0, e) == "size") size = long.Parse(kv.Substring(e + 1), CultureInfo.InvariantCulture);
                    else if (kv.Substring(0, e) == "id") id = kv.Substring(e + 1);
                }
                string[] parts = path.Split('/');
                string auth; h.TryGetValue("Authorization", out auth);
                Bump(id + ":req");

                if (parts.Length >= 3 && parts[1] == "ntlm")
                {
                    if (auth == null || !auth.StartsWith("NTLM ", StringComparison.OrdinalIgnoreCase))
                    {
                        Unauthorized(ns, "NTLM", true);
                        continue;
                    }
                    byte[] msg = Convert.FromBase64String(auth.Substring(5).Trim());
                    int type = msg.Length >= 12 ? BitConverter.ToInt32(msg, 8) : 0;
                    if (type == 1)
                    {
                        serverChallenge = new byte[8];
                        using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(serverChallenge);
                        Unauthorized(ns, "NTLM " + Convert.ToBase64String(Ntlm.Challenge(msg, serverChallenge)), true);
                        continue;
                    }
                    string who;
                    if (type == 3 && serverChallenge != null && Ntlm.Verify(msg, serverChallenge, Password, out who) && who.Equals(User, StringComparison.OrdinalIgnoreCase))
                    {
                        Bump(id + ":ntlm-ok");
                        Serve(ns, h, size, id);
                        return;
                    }
                    Bump(id + ":ntlm-fail");
                    Unauthorized(ns, "NTLM", false);
                    return;
                }
                if (parts.Length >= 4 && parts[1] == "digest")
                {
                    string algo = parts[2];
                    if (auth != null && auth.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase) && DigestOk(auth, method, algo))
                    {
                        Bump(id + ":digest-ok");
                        Serve(ns, h, size, id);
                        return;
                    }
                    if (auth != null) Bump(id + ":digest-fail");
                    Unauthorized(ns, "Digest realm=\"pruebas\", qop=\"auth\", algorithm=" + algo + ", nonce=\"" + _nonce + "\", opaque=\"op1\"", true);
                    continue;
                }
                WriteHead(ns, 404, "Not Found", new string[] { "Content-Length: 0" }, false);
                return;
            }
        }

        bool DigestOk(string auth, string method, string algo)
        {
            List<Dictionary<string, string>> l = DigestAuth.ParseDigestChallenges(auth);
            if (l.Count != 1) return false;
            Dictionary<string, string> p = l[0];
            if (Get(p, "username") != User || Get(p, "nonce") != _nonce || Get(p, "opaque") != "op1" || Get(p, "qop") != "auth") return false;
            string a = algo == "SHA-256" ? "SHA-256" : "MD5";
            string ha1 = DigestAuth.H(a, User + ":" + Get(p, "realm") + ":" + Password);
            string ha2 = DigestAuth.H(a, method + ":" + Get(p, "uri"));
            string expected = DigestAuth.H(a, ha1 + ":" + _nonce + ":" + Get(p, "nc") + ":" + Get(p, "cnonce") + ":auth:" + ha2);
            return string.Equals(expected, Get(p, "response"), StringComparison.OrdinalIgnoreCase);
        }

        static string Get(Dictionary<string, string> p, string k) { string v; return p.TryGetValue(k, out v) ? v : null; }

        void Unauthorized(NetworkStream ns, string challenge, bool keepAlive)
        {
            WriteHead(ns, 401, "Unauthorized", new string[] { "WWW-Authenticate: " + challenge, "Content-Length: 0" }, keepAlive);
        }

        void Serve(NetworkStream ns, Dictionary<string, string> h, long size, string id)
        {
            long a = 0, b = size - 1;
            bool ranged = false;
            string range;
            if (h.TryGetValue("Range", out range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                string r = range.Substring(6);
                int dash = r.IndexOf('-');
                a = long.Parse(r.Substring(0, dash), CultureInfo.InvariantCulture);
                string bs = r.Substring(dash + 1);
                if (bs.Length > 0) b = Math.Min(size - 1, long.Parse(bs, CultureInfo.InvariantCulture));
                ranged = true;
                if (a > 0) Bump(id + ":ranged");
            }
            List<string> hdr = new List<string>();
            hdr.Add("Content-Type: application/octet-stream");
            hdr.Add("Accept-Ranges: bytes");
            hdr.Add("Content-Length: " + (b - a + 1).ToString(CultureInfo.InvariantCulture));
            if (ranged) hdr.Add(string.Format(CultureInfo.InvariantCulture, "Content-Range: bytes {0}-{1}/{2}", a, b, size));
            WriteHead(ns, ranged ? 206 : 200, ranged ? "Partial Content" : "OK", hdr.ToArray(), false);
            byte[] buf = new byte[16 * 1024];
            long pos = a;
            try
            {
                while (pos <= b)
                {
                    int n = (int)Math.Min(buf.Length, b - pos + 1);
                    TestServer.Fill(buf, pos, n);
                    ns.Write(buf, 0, n);
                    pos += n;
                    Thread.Sleep(30);
                }
            }
            catch (IOException) { }
        }

        static string ReadHead(NetworkStream ns)
        {
            StringBuilder sb = new StringBuilder();
            int c, state = 0;
            while ((c = ns.ReadByte()) >= 0)
            {
                sb.Append((char)c);
                if (c == '\r' && (state == 0 || state == 2)) state++;
                else if (c == '\n' && (state == 1 || state == 3)) { state++; if (state == 4) break; }
                else state = 0;
                if (sb.Length > 65536) return null;
            }
            return state == 4 ? sb.ToString() : null;
        }

        static void WriteHead(NetworkStream ns, int status, string reason, string[] headers, bool keepAlive)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
            foreach (string x in headers) sb.Append(x).Append("\r\n");
            sb.Append(keepAlive ? "Connection: keep-alive\r\n\r\n" : "Connection: close\r\n\r\n");
            byte[] data = Encoding.ASCII.GetBytes(sb.ToString());
            ns.Write(data, 0, data.Length);
        }
    }

    // Lado servidor de NTLM (MS-NLMP): mensaje de desafío y verificación de la respuesta NTLMv2.
    public static class Ntlm
    {
        const uint UNICODE = 0x1, REQUEST_TARGET = 0x4, NTLM = 0x200, ALWAYS_SIGN = 0x8000, TARGET_TYPE_DOMAIN = 0x10000,
                   TARGET_INFO = 0x800000, VERSION = 0x2000000;
        const uint KEEP = 0x1 | 0x10 | 0x20 | 0x200 | 0x8000 | 0x80000 | 0x2000000 | 0x20000000 | 0x40000000 | 0x80000000;

        public static byte[] Challenge(byte[] negotiate, byte[] serverChallenge)
        {
            uint clientFlags = negotiate.Length >= 16 ? BitConverter.ToUInt32(negotiate, 12) : 0;
            uint flags = (clientFlags & KEEP) | UNICODE | REQUEST_TARGET | NTLM | ALWAYS_SIGN | TARGET_TYPE_DOMAIN | TARGET_INFO | VERSION;
            byte[] targetName = Encoding.Unicode.GetBytes("PRUEBAS");
            MemoryStream ti = new MemoryStream();
            AvPair(ti, 2, Encoding.Unicode.GetBytes("PRUEBAS"));
            AvPair(ti, 1, Encoding.Unicode.GetBytes("SERVIDOR"));
            AvPair(ti, 4, Encoding.Unicode.GetBytes("pruebas.local"));
            AvPair(ti, 3, Encoding.Unicode.GetBytes("servidor.pruebas.local"));
            AvPair(ti, 0, new byte[0]);
            byte[] targetInfo = ti.ToArray();

            const int headerLen = 56;
            MemoryStream m = new MemoryStream();
            BinaryWriter w = new BinaryWriter(m);
            w.Write(Encoding.ASCII.GetBytes("NTLMSSP\0"));
            w.Write(2);
            w.Write((ushort)targetName.Length); w.Write((ushort)targetName.Length); w.Write(headerLen);
            w.Write(flags);
            w.Write(serverChallenge);
            w.Write(new byte[8]);
            w.Write((ushort)targetInfo.Length); w.Write((ushort)targetInfo.Length); w.Write(headerLen + targetName.Length);
            w.Write(new byte[] { 10, 0, 0x61, 0x4A, 0, 0, 0, 15 }); // versión 10.0.19041, NTLMSSP 15
            w.Write(targetName);
            w.Write(targetInfo);
            return m.ToArray();
        }

        static void AvPair(MemoryStream s, ushort id, byte[] value)
        {
            s.Write(BitConverter.GetBytes(id), 0, 2);
            s.Write(BitConverter.GetBytes((ushort)value.Length), 0, 2);
            s.Write(value, 0, value.Length);
        }

        static byte[] Field(byte[] msg, int at)
        {
            int len = BitConverter.ToUInt16(msg, at), off = BitConverter.ToInt32(msg, at + 4);
            byte[] r = new byte[len];
            Array.Copy(msg, off, r, 0, len);
            return r;
        }

        public static bool Verify(byte[] auth, byte[] serverChallenge, string password, out string user)
        {
            user = "";
            byte[] nt = Field(auth, 20);
            byte[] domain = Field(auth, 28), userB = Field(auth, 36);
            uint flags = BitConverter.ToUInt32(auth, 60);
            Encoding enc = (flags & UNICODE) != 0 ? Encoding.Unicode : Encoding.ASCII;
            user = enc.GetString(userB);
            string dom = enc.GetString(domain);
            if (nt.Length <= 24) return false; // NTLMv1: no se acepta
            byte[] ntHash = Md4(Encoding.Unicode.GetBytes(password));
            byte[] ntowf2;
            using (HMACMD5 hm = new HMACMD5(ntHash)) ntowf2 = hm.ComputeHash(Encoding.Unicode.GetBytes(user.ToUpperInvariant() + dom));
            byte[] proof = new byte[16], blob = new byte[nt.Length - 16];
            Array.Copy(nt, 0, proof, 0, 16);
            Array.Copy(nt, 16, blob, 0, blob.Length);
            byte[] data = new byte[8 + blob.Length];
            Array.Copy(serverChallenge, data, 8);
            Array.Copy(blob, 0, data, 8, blob.Length);
            byte[] expected;
            using (HMACMD5 hm = new HMACMD5(ntowf2)) expected = hm.ComputeHash(data);
            for (int i = 0; i < 16; i++) if (expected[i] != proof[i]) return false;
            return true;
        }

        // MD4 (RFC 1320): .NET no lo trae y NTLM lo usa para el hash de la contraseña.
        public static byte[] Md4(byte[] input)
        {
            long bitLen = (long)input.Length * 8;
            int padLen = ((input.Length + 8) / 64 + 1) * 64;
            byte[] msg = new byte[padLen];
            Array.Copy(input, msg, input.Length);
            msg[input.Length] = 0x80;
            Array.Copy(BitConverter.GetBytes(bitLen), 0, msg, padLen - 8, 8);
            uint a = 0x67452301, b = 0xefcdab89, c = 0x98badcfe, d = 0x10325476;
            uint[] x = new uint[16];
            for (int blk = 0; blk < padLen; blk += 64)
            {
                for (int i = 0; i < 16; i++) x[i] = BitConverter.ToUInt32(msg, blk + i * 4);
                uint aa = a, bb = b, cc = c, dd = d;
                int[] s1 = { 3, 7, 11, 19 };
                for (int i = 0; i < 16; i++)
                {
                    uint t = a + ((b & c) | (~b & d)) + x[i];
                    a = d; d = c; c = b; b = Rol(t, s1[i % 4]);
                }
                int[] s2 = { 3, 5, 9, 13 };
                int[] o2 = { 0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15 };
                for (int i = 0; i < 16; i++)
                {
                    uint t = a + ((b & c) | (b & d) | (c & d)) + x[o2[i]] + 0x5a827999;
                    a = d; d = c; c = b; b = Rol(t, s2[i % 4]);
                }
                int[] s3 = { 3, 9, 11, 15 };
                int[] o3 = { 0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15 };
                for (int i = 0; i < 16; i++)
                {
                    uint t = a + (b ^ c ^ d) + x[o3[i]] + 0x6ed9eba1;
                    a = d; d = c; c = b; b = Rol(t, s3[i % 4]);
                }
                a += aa; b += bb; c += cc; d += dd;
            }
            byte[] r = new byte[16];
            Array.Copy(BitConverter.GetBytes(a), 0, r, 0, 4);
            Array.Copy(BitConverter.GetBytes(b), 0, r, 4, 4);
            Array.Copy(BitConverter.GetBytes(c), 0, r, 8, 4);
            Array.Copy(BitConverter.GetBytes(d), 0, r, 12, 4);
            return r;
        }

        static uint Rol(uint v, int s) { return (v << s) | (v >> (32 - s)); }
    }
}
