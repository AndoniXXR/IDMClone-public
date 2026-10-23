using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace IDMClone
{
    // IPC por tubería con nombre: una segunda instancia (CLI) o el host nativo envían
    // una orden a la GUI ya abierta y esperan su respuesta. Es el equivalente legítimo y
    // propio de la app a lo que en IDM hace \\.\pipe\IDMNetworkMonitor.
    // Protocolo: una línea de petición (texto o JSON) -> una línea de respuesta JSON.
    public static class Ipc
    {
        const string BasePipeName = "IDMClone_Pipe";
        const string BaseMutexName = "IDMClone_SingleInstance_Mutex";

        // Con una carpeta de datos propia (IDMCLONE_HOME, tests) se usan nombres propios,
        // para no mezclarse con la instancia normal del usuario.
        static string Suffix
        {
            get
            {
                if (!AppConfig.IsCustomHome) return "";
                using (SHA1 sha = SHA1.Create())
                {
                    byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(AppConfig.Dir.ToLowerInvariant()));
                    return "_" + BitConverter.ToString(h, 0, 6).Replace("-", "");
                }
            }
        }

        // Tubería por usuario y sesión de Windows (como \\.\pipe\IDMNetworkMonitor.<sesión> de IDM):
        // dos usuarios con cambio rápido de usuario no se pisan, y otro usuario no puede hacerse pasar
        // por la ventana de IDMClone para recibir las URL y cookies del navegador.
        static string UserTag
        {
            get
            {
                string sid = WindowsIdentity.GetCurrent().User.Value;
                using (SHA1 sha = SHA1.Create())
                    return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sid)), 0, 4).Replace("-", "");
            }
        }

        public static string PipeName { get { return BasePipeName + "_" + Process.GetCurrentProcess().SessionId + "_" + UserTag + Suffix; } }
        public static string MutexName { get { return @"Local\" + BaseMutexName + "_" + UserTag + Suffix; } }

        // Solo el usuario actual puede abrir la tubería.
        static PipeSecurity CurrentUserOnly()
        {
            PipeSecurity ps = new PipeSecurity();
            ps.SetAccessRuleProtection(true, false);
            ps.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            return ps;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        // ¿El proceso que atiende la tubería es del usuario actual y de esta sesión?
        public static bool ServerIsTrusted(NamedPipeClientStream client)
        {
            try
            {
                uint pid;
                if (!GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out pid)) return false;
                if (pid == (uint)Process.GetCurrentProcess().Id) return true;
                if (Process.GetProcessById((int)pid).SessionId != Process.GetCurrentProcess().SessionId) return false;
                const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_QUERY = 0x0008;
                IntPtr hp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (hp == IntPtr.Zero) return false;
                try
                {
                    IntPtr tok;
                    if (!OpenProcessToken(hp, TOKEN_QUERY, out tok)) return false;
                    try
                    {
                        using (WindowsIdentity id = new WindowsIdentity(tok))
                            return id.User != null && id.User.Equals(WindowsIdentity.GetCurrent().User);
                    }
                    finally { CloseHandle(tok); }
                }
                finally { CloseHandle(hp); }
            }
            catch { return false; }
        }

        public class Server
        {
            volatile bool _stopped;
            internal string Name;
            public void Stop()
            {
                _stopped = true;
                // Desbloquear el WaitForConnection pendiente.
                try { using (NamedPipeClientStream c = new NamedPipeClientStream(".", Name, PipeDirection.InOut)) c.Connect(200); } catch { }
            }
            internal bool Stopped { get { return _stopped; } }
        }

        // Atiende peticiones; cada conexión en su propio hilo para que una lenta no bloquee al resto.
        public static Server StartServer(Func<string, string> onRequest)
        {
            return StartServer(onRequest, PipeName);
        }

        public static Server StartServer(Func<string, string> onRequest, string pipeName)
        {
            Server srv = new Server();
            srv.Name = pipeName;
            Thread t = new Thread(delegate()
            {
                while (!srv.Stopped)
                {
                    NamedPipeServerStream server = null;
                    try
                    {
                        server = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.None,
                            0, 0, CurrentUserOnly());
                        server.WaitForConnection();
                        if (srv.Stopped) { server.Dispose(); break; }
                        NamedPipeServerStream conn = server;
                        server = null;
                        Thread h = new Thread(delegate() { Serve(conn, onRequest); });
                        h.IsBackground = true;
                        h.Name = "ipc-conn";
                        h.Start();
                    }
                    catch
                    {
                        if (server != null) { try { server.Dispose(); } catch { } }
                        Thread.Sleep(200);
                    }
                }
            });
            t.IsBackground = true;
            t.Name = "ipc-server";
            t.Start();
            return srv;
        }

        static void Serve(NamedPipeServerStream conn, Func<string, string> onRequest)
        {
            try
            {
                using (conn)
                {
                    StreamReader sr = new StreamReader(conn, new UTF8Encoding(false));
                    StreamWriter sw = new StreamWriter(conn, new UTF8Encoding(false));
                    string line = sr.ReadLine();
                    if (line == null) return;
                    string reply;
                    try { reply = onRequest != null ? onRequest(line) : null; }
                    catch (Exception ex) { reply = ErrorJson(ex.Message); }
                    if (string.IsNullOrEmpty(reply)) reply = "{\"ok\":true}";
                    sw.WriteLine(reply.Replace("\r", "").Replace("\n", " "));
                    sw.Flush();
                    try { conn.WaitForPipeDrain(); } catch { }
                }
            }
            catch { }
        }

        // Envía una orden y devuelve la respuesta de la GUI, o null si no hay ninguna abierta
        // (o no contestó a tiempo).
        public static string Request(string command, int connectTimeoutMs, int replyTimeoutMs)
        {
            return Request(command, connectTimeoutMs, replyTimeoutMs, PipeName);
        }

        public static string Request(string command, int connectTimeoutMs, int replyTimeoutMs, string pipeName)
        {
            bool connected;
            return Request(command, connectTimeoutMs, replyTimeoutMs, pipeName, out connected);
        }

        // 'connected' distingue "no hay GUI abierta" de "la GUI no contestó a tiempo".
        public static string Request(string command, int connectTimeoutMs, int replyTimeoutMs, string pipeName, out bool connected)
        {
            connected = false;
            try
            {
                using (NamedPipeClientStream client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
                {
                    client.Connect(connectTimeoutMs);
                    // No mandar nada (URL, cookies...) a un servidor que no sea de este usuario y sesión.
                    if (!ServerIsTrusted(client)) return null;
                    connected = true;
                    StreamWriter sw = new StreamWriter(client, new UTF8Encoding(false));
                    sw.WriteLine(command.Replace("\r", "").Replace("\n", " "));
                    sw.Flush();
                    string reply = null;
                    StreamReader sr = new StreamReader(client, new UTF8Encoding(false));
                    Thread reader = new Thread(delegate() { try { reply = sr.ReadLine(); } catch { } });
                    reader.IsBackground = true;
                    reader.Start();
                    if (!reader.Join(replyTimeoutMs)) return null;
                    return reply;
                }
            }
            catch { return null; }
        }

        // Compatibilidad: true si una instancia abierta recibió la orden y la aceptó.
        public static bool SendCommand(string command)
        {
            string reply = Request(command, 1500, 15000);
            return reply != null && reply.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0;
        }

        public static string OkJson() { return "{\"ok\":true}"; }

        public static string ErrorJson(string message)
        {
            return "{\"ok\":false,\"error\":" + JsonQuote(message) + "}";
        }

        public static string JsonQuote(string s)
        {
            if (s == null) return "\"\"";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
