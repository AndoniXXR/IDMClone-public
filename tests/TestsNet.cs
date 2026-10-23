using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace IDMClone.Tests
{
    // Happy Eyeballs: un servidor con una IPv6 que no responde no debe hacer esperar ~21 s (lo que tarda
    // .NET Framework en darla por perdida) antes de usar su IPv4, como pasaba con algunos CDN de vídeo.
    public static partial class TestMain
    {
        static void RunNet()
        {
            Run("red: todas las conexiones de .NET pasan por Happy Eyeballs (también las de redirecciones)", TN_Hook);
            Run("red: se saltan las direcciones anteriores a la ganadora que no conectaron", TN_SlowRule);
            Run("red: localhost con ::1 sin servidor -> se salta ::1 y la descarga va por 127.0.0.1", TN_Localhost);
        }

        static void TN_Hook()
        {
            DownloadEngine.ConfigureNetwork();
            Check(HappyEyeballs.Global, "No se pudo enganchar la tabla de ServicePoint de .NET");
            ServicePoint sp = ServicePointManager.FindServicePoint(new Uri("http://he-hook-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".invalid:81/"));
            Check(sp.BindIPEndPointDelegate != null, "Un ServicePoint nuevo no lleva el delegado");
        }

        static void TN_SlowRule()
        {
            Func<int[], string> f = delegate(int[] st) { return string.Join(",", HappyEyeballs.SlowBeforeWinner(st).ConvertAll(i => i.ToString()).ToArray()); };
            Check(f(new int[] { 0, 1 }) == "0", "IPv6 sin respuesta e IPv4 gana: " + f(new int[] { 0, 1 }));
            Check(f(new int[] { -1, 0, 1, 0 }) == "0,1", "Dos lentas antes de la ganadora: " + f(new int[] { -1, 0, 1, 0 }));
            Check(f(new int[] { 1, 0 }) == "", "La primera gana: no se salta nada");
            Check(f(new int[] { 0, 0 }) == "", "Sin ganadora no se salta nada (mejor esperar que fallar)");
            Check(f(new int[] { -1, -1 }) == "", "Todas fallan: no se salta nada");
        }

        static void TN_Localhost()
        {
            DownloadEngine.ConfigureNetwork();
            IPAddress[] a = Dns.GetHostAddresses("localhost");
            bool v6 = false, v4 = false;
            foreach (IPAddress x in a) { if (x.AddressFamily == AddressFamily.InterNetworkV6) v6 = true; else v4 = true; }
            if (!v6 || !v4 || a[0].AddressFamily != AddressFamily.InterNetworkV6)
                throw new SkipException("localhost no resuelve primero a ::1 y luego a 127.0.0.1 en este equipo");
            HappyEyeballs.Forget(); // otra prueba pudo ir ya a localhost (se recuerda 10 minutos)
            int before = HappyEyeballs.Races;
            // Redirección 302 de 127.0.0.1 a localhost: el ServicePoint de localhost lo crea .NET al seguirla.
            string id = NewId("he");
            DownloadItem it = NewMgr().Add(string.Format("{0}/r/range/he.bin?size={1}&id={2}", S.Base, 700 * 1024, id), Dl, null, true);
            WaitEnd(it, 60);
            AssertComplete(it, 700 * 1024);
            Check(HappyEyeballs.Races > before, "No se hizo la carrera IPv6/IPv4 con localhost");
            Check(HappyEyeballs.ShouldSkip("localhost", new IPEndPoint(IPAddress.IPv6Loopback, S.Port)), "::1 (sin servidor) no quedó marcada para saltarla");
            Check(!HappyEyeballs.ShouldSkip("localhost", new IPEndPoint(IPAddress.Loopback, S.Port)), "127.0.0.1 no debe saltarse");
        }
    }
}
