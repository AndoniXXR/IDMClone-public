using System;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace IDMClone.Tests
{
    // FTPS (FTP con cifrado TLS explícito: AUTH TLS + PROT P) contra el servidor FTP de pruebas con certificado.
    public static partial class TestMain
    {
        static TestFtpServer Ftps;
        static X509Certificate2 FtpsCert;

        static void RunFtps()
        {
            FtpsCert = TestFtpServer.MakeCert();
            Ftps = new TestFtpServer(); Ftps.Cert = FtpsCert; Ftps.Start();
            try
            {
                Run("ftps: descarga cifrada con varias conexiones (AUTH TLS, PROT P)", TF_Download);
                Run("ftps: pausar y reanudar por la conexión cifrada (REST)", TF_Resume);
                Run("ftps: certificado no válido -> error claro y sin reintentos", TF_BadCert);
                Run("ftps: servidor sin cifrado -> error claro", TF_NoTls);
            }
            finally { Ftps.Stop(); }
        }

        // Confiar solo en el certificado de pruebas mientras dura la prueba.
        static IDisposable TrustTestCert()
        {
            RemoteCertificateValidationCallback saved = ServicePointManager.ServerCertificateValidationCallback;
            ServicePointManager.ServerCertificateValidationCallback = delegate(object s, X509Certificate c, X509Chain ch, SslPolicyErrors e)
            {
                return e == SslPolicyErrors.None || c != null && c.GetCertHashString() == FtpsCert.Thumbprint;
            };
            return new Restore(delegate { ServicePointManager.ServerCertificateValidationCallback = saved; });
        }

        class Restore : IDisposable { readonly Action _a; public Restore(Action a) { _a = a; } public void Dispose() { _a(); } }

        static void TF_Download()
        {
            using (TrustTestCert())
            {
                DownloadManager m = NewMgr();
                long size = 3L * 1024 * 1024;
                string id = NewId("ftps");
                DownloadItem it = m.Add(Ftps.FtpsUrl("normal", "cifrado.bin", size, id), Dl, null, true);
                WaitEnd(it, 40);
                Check(it.Status == DownloadStatus.Complete, "Estado: " + it.Status + " " + it.Error);
                Check(HashFile(it.FilePath, "SHA256").Equals(TestServer.Sha256Of(size), StringComparison.OrdinalIgnoreCase), "Contenido distinto");
                Check(Ftps.Count(id + ":retr") >= 2 && Ftps.Count(id + ":tls") == Ftps.Count(id + ":retr"), "Conexiones: " + Ftps.Count(id + ":retr") + ", cifradas: " + Ftps.Count(id + ":tls"));
                Check(Ftps.Count("auth-tls") >= 2 && Ftps.Count("prot-p") >= 2, "AUTH TLS / PROT P: " + Ftps.Count("auth-tls") + "/" + Ftps.Count("prot-p"));
                Check(it.FileName == "cifrado.bin", "Nombre: " + it.FileName);
            }
        }

        static void TF_Resume()
        {
            using (TrustTestCert())
            {
                DownloadManager m = NewMgr();
                long size = 4L * 1024 * 1024;
                string id = NewId("ftpsr");
                DownloadItem it = m.Add(Ftps.FtpsUrl("slow", "reanudar.bin", size, id), Dl, null, true);
                WaitProgress(it, 600 * 1024, 30);
                m.StopItem(it); WaitEnd(it, 15);
                Check(it.Status == DownloadStatus.Paused, "Tras pausar: " + it.Status);
                long had = it.Downloaded;
                m.StartItem(it); WaitEnd(it, 90);
                Check(it.Status == DownloadStatus.Complete, "Estado: " + it.Status + " " + it.Error);
                Check(Ftps.Count(id + ":rest") >= 1, "No continuó con REST");
                Check(HashFile(it.FilePath, "SHA256").Equals(TestServer.Sha256Of(size), StringComparison.OrdinalIgnoreCase), "Contenido distinto tras reanudar (tenía " + had + ")");
            }
        }

        static void TF_BadCert()
        {
            DownloadManager m = NewMgr();
            Stopwatch sw = Stopwatch.StartNew();
            DownloadItem it = m.Add(Ftps.FtpsUrl("normal", "desconfiado.bin", 1024, NewId("ftpsc")), Dl, null, true);
            WaitEnd(it, 30);
            Check(it.Status == DownloadStatus.Error && it.Error != null && (it.Error.Contains("certificado") || it.Error.Contains("cifrada")), "Error: " + it.Status + " " + it.Error);
            Check(sw.Elapsed.TotalSeconds < 10, "Reintentó un certificado no válido (" + sw.Elapsed.TotalSeconds.ToString("0.0") + " s)");
        }

        static void TF_NoTls()
        {
            DownloadManager m = NewMgr();
            Stopwatch sw = Stopwatch.StartNew();
            TestFtpServer plain = new TestFtpServer(); plain.Start(); // sin certificado: no admite AUTH TLS
            DownloadItem it;
            try
            {
                it = m.Add(plain.FtpsUrl("normal", "sincifrar.bin", 1024, NewId("ftpsn")), Dl, null, true);
                WaitEnd(it, 30);
            }
            finally { plain.Stop(); }
            Check(it.Status == DownloadStatus.Error && it.Error != null && it.Error.Contains("no admite conexiones cifradas"), "Error: " + it.Status + " " + it.Error);
            Check(sw.Elapsed.TotalSeconds < 10, "Reintentó un servidor sin FTPS");
        }
    }
}
