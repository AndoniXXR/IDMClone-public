using System;
using System.IO;
using Microsoft.Win32;

namespace IDMClone.Tests
{
    // Arreglos de la integración (subtítulos + red + actualizaciones + menú del Explorador): lo que solo falla al juntarlos.
    public static partial class TestMain
    {
        static void RunIntegracion()
        {
            Run("integración: subtítulos repetidos se numeran antes del idioma (Vídeo_1.es.vtt, como Vídeo_1.mp4)", TI_SubtitleUniquePath);
            Run("integración: una configuración de antes conserva Proxy DNS, FTP directo y SOCKS para todo", TI_ConfigUpgradeProxy);
            Run("integración: /update respeta el menú del Explorador quitado por el usuario y lo da a quien no lo tenía", TI_ExplorerMenuKeep);
            Run("integración: SOCKS de las opciones caído -> se baja por el SOCKS del navegador (como con un proxy HTTP caído)", TI_SocksDownBrowserSocks);
            Run("integración: la lista de enlaces (ventana no modal) se cierra con Cancelar y con Esc", TI_LinksCancelCloses);
        }

        static void TI_LinksCancelCloses()
        {
            AppConfig c = Cfg();
            DownloadManager m = new DownloadManager(c);
            for (int round = 0; round < 2; round++)
            {
                System.Collections.Generic.List<LinkEntry> links = new System.Collections.Generic.List<LinkEntry>();
                LinkEntry e = new LinkEntry(); e.Url = "http://127.0.0.1:1/a.zip"; links.Add(e);
                LinkListForm f = new LinkListForm(c, m, links, "prueba");
                try
                {
                    f.Show();
                    System.Windows.Forms.Application.DoEvents();
                    if (round == 0) FindButton(f, "Cancelar").PerformClick();
                    else f.CancelButton.PerformClick(); // lo mismo que pulsar Esc
                    for (int i = 0; i < 20 && !f.IsDisposed && f.Visible; i++) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                    Check(f.IsDisposed || !f.Visible, (round == 0 ? "Cancelar" : "Esc") + " no cerró la lista de enlaces");
                }
                finally { if (!f.IsDisposed) f.Dispose(); }
            }
            CheckEq(0, m.Snapshot().Count, "Cancelar no debe añadir nada");
        }

        static void TI_SocksDownBrowserSocks()
        {
            TestSocksServer x = Socks();
            int v5 = x.Count("v5");
            System.Net.Sockets.TcpListener l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start(); int dead = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); // puerto cerrado
            AppConfig c = Cfg();
            c.ProxyMode = "manual"; c.ProxyHost = ""; c.ProxyHttp = false; c.ProxyHttps = false;
            c.SocksEnabled = true; c.SocksType = "5"; c.SocksHost = "127.0.0.1"; c.SocksPort = dead; c.SocksProxyDns = true;
            c.UseBrowserProxyOnError = true; c.RetryCount = 2; c.RetryDelaySec = 1;
            DownloadManager m = new DownloadManager(c);
            AddOptions o = new AddOptions();
            string id = NewId("tisocksdown");
            o.Url = SocksUrl("caido.invalid", "range", "ti.bin", 200 * 1024, id);
            o.Folder = Dl; o.StartNow = true;
            o.BrowserProxy = "socks5://127.0.0.1:" + x.Port;
            DownloadItem it = m.Add(o);
            WaitEnd(it, 60);
            AssertComplete(it, 200 * 1024);
            Check(x.Count("v5") > v5, "No pasó al SOCKS del navegador");
            CheckBridgeIdle();
        }

        static void TI_SubtitleUniquePath()
        {
            string dir = Path.Combine(Home, "ti-subs-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Mi vídeo.es.vtt"), "x");
            File.WriteAllText(Path.Combine(dir, "Mi vídeo.pt-BR.srt"), "x");
            File.WriteAllText(Path.Combine(dir, "a.zip"), "x");
            File.WriteAllText(Path.Combine(dir, "pelicula.final.mkv"), "x");
            CheckEq("Mi vídeo_1.es.vtt", Path.GetFileName(Util.UniquePath(Path.Combine(dir, "Mi vídeo.es.vtt"))), "vtt con idioma");
            CheckEq("Mi vídeo_1.pt-BR.srt", Path.GetFileName(Util.UniquePath(Path.Combine(dir, "Mi vídeo.pt-BR.srt"))), "srt con región");
            CheckEq("a_1.zip", Path.GetFileName(Util.UniquePath(Path.Combine(dir, "a.zip"))), "lo demás, como siempre");
            CheckEq("pelicula.final_1.mkv", Path.GetFileName(Util.UniquePath(Path.Combine(dir, "pelicula.final.mkv"))), "un punto que no es idioma");
        }

        static void TI_ConfigUpgradeProxy()
        {
            AppConfig old = AppConfig.CreateDefault();
            old.SocksEnabled = true; old.ProxyHttp = true; old.ProxyHttps = true; old.ProxyFtp = true; old.SocksProxyDns = false;
            AppConfig.CreateDefaultForUpgrade(old, "{\"SocksEnabled\":true,\"ProxyHost\":\"p\"}");
            Check(old.SocksProxyDns, "Antes el SOCKS mandaba el nombre: Proxy DNS debe quedar marcado");
            Check(!old.ProxyFtp, "Antes el FTP iba directo");
            Check(!old.ProxyHttp && !old.ProxyHttps, "Antes, con SOCKS activo, todo iba por el SOCKS");
            AppConfig sinSocks = AppConfig.CreateDefault();
            sinSocks.ProxyHttp = true;
            AppConfig.CreateDefaultForUpgrade(sinSocks, "{\"SocksEnabled\":false}");
            Check(sinSocks.ProxyHttp && !sinSocks.ProxyFtp, "Sin SOCKS: http sigue por el proxy y el FTP directo");
            AppConfig nueva = AppConfig.CreateDefault();
            nueva.SocksEnabled = true; nueva.ProxyFtp = true; nueva.SocksProxyDns = false; nueva.ProxyHttp = true;
            AppConfig.CreateDefaultForUpgrade(nueva, "{\"SocksEnabled\":true,\"ProxyFtp\":true,\"SocksProxyDns\":false}");
            Check(nueva.ProxyFtp && !nueva.SocksProxyDns && nueva.ProxyHttp, "Una configuración que ya tiene las claves no se toca");
        }

        static void TI_ExplorerMenuKeep()
        {
            string root = @"Software\IDMCloneTests\ti-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string savedRoot = IDMClone.Setup.Installer.RegRoot;
            try
            {
                IDMClone.Setup.Installer.RegRoot = root;
                string dir = Path.Combine(Home, "ti-inst-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, IDMClone.Setup.Installer.ExeName), "x");
                string uninstall = root + "\\" + IDMClone.Setup.Installer.UninstallKey;
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(uninstall)) k.SetValue("DisplayName", "IDMClone");

                IDMClone.Setup.InstallOptions o = new IDMClone.Setup.InstallOptions(); o.Dir = dir;
                IDMClone.Setup.Installer.KeepChoices(o);
                Check(o.ExplorerMenu, "Sin la marca (versión anterior sin menú): la actualización lo pone");

                ShellMenu.MarkOff(uninstall, true);
                Check(ShellMenu.IsMarkedOff(uninstall), "La marca no se guardó");
                o = new IDMClone.Setup.InstallOptions(); o.Dir = dir;
                IDMClone.Setup.Installer.KeepChoices(o);
                Check(!o.ExplorerMenu, "Quitado por el usuario: la actualización no lo vuelve a poner");

                ShellMenu.MarkOff(uninstall, false);
                Check(!ShellMenu.IsMarkedOff(uninstall), "La marca no se quitó");
                ShellMenu.MarkOff(root + @"\no\existe", true);
                Check(!ShellMenu.IsMarkedOff(root + @"\no\existe"), "Sin instalar no se crea la clave");
            }
            finally
            {
                IDMClone.Setup.Installer.RegRoot = savedRoot;
                try { Registry.CurrentUser.DeleteSubKeyTree(root, false); } catch { }
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\IDMCloneTests"))
                        if (k != null && k.SubKeyCount == 0 && k.ValueCount == 0) { k.Close(); Registry.CurrentUser.DeleteSubKey(@"Software\IDMCloneTests", false); }
                }
                catch { }
            }
        }
    }
}
