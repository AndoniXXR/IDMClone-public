using System;
using System.IO;

namespace IDMClone.Tests
{
    // Nombres de archivo muy largos, como IDM 6.43 (medido en un Sandbox): el nombre se acorta conservando la extensión
    // (como mucho 211 caracteres y la ruta completa hasta 254), y en la carpeta temporal se trabaja con un nombre corto.
    // Antes, DwnlData\<id>\<nombre entero> pasaba del límite de ruta de Windows y la descarga acababa en "Error".
    public static partial class TestMain
    {
        static void RunNombreLargo()
        {
            Run("nombre largo: se acorta como IDM (211 de nombre, 254 de ruta, con la extensión)", TL_Fit);
            Run("nombre largo: se descarga con la carpeta temporal y queda acortado en el destino", TL_Download);
            Run("nombre largo: archivo de trabajo corto en la carpeta temporal", TL_WorkName);
        }

        static void TL_Fit()
        {
            // Los mismos casos que se midieron con el IDM original.
            string compressed = @"C:\Users\WDAGUtilityAccount\Downloads\Compressed";
            string n1 = "un nombre de archivo muy largo para comprobar que Abrir carpeta tambien funciona con rutas largas en el Explorador de Windows y que selecciona el archivo correcto aunque ocupe casi doscientos caracteres.zip";
            string f1 = Util.FitFileName(compressed, n1);
            Check(f1.Length == 205 && f1.EndsWith("caractere.zip"), "206 en Compressed: " + f1.Length + " " + f1);
            string b = new string('b', 240) + ".zip";
            Check(Util.FitFileName(compressed, b) == new string('b', 201) + ".zip", "244 en Compressed -> 205");
            Check(Util.FitFileName(@"C:\t", b) == new string('b', 207) + ".zip", "244 en C:\\t -> 211");
            Check(Util.FitFileName(@"C:\t2\", new string('c', 300) + ".zip") == new string('c', 207) + ".zip", "304 en C:\\t2 -> 211");
            Check((compressed + "\\" + f1).Length == 254, "La ruta completa debería quedar en 254");
            // Lo que no hace falta acortar no se toca.
            Check(Util.FitFileName(compressed, "vídeo #1, final.mp4") == "vídeo #1, final.mp4", "Nombre corto cambiado");
            Check(Util.FitPath(Path.Combine(compressed, b)) == Path.Combine(compressed, new string('b', 201) + ".zip"), "FitPath");
            // Sin extensión, con un emoji justo en el corte (no se parte) y sin acabar en punto o espacio.
            string noExt = Util.FitFileName(@"C:\t", new string('d', 300));
            Check(noExt.Length == 211, "Sin extensión: " + noExt.Length);
            string emoji = new string('e', 206) + "\U0001F600" + new string('e', 50) + ".mp4";
            string fe = Util.FitFileName(@"C:\t", emoji);
            Check(fe.Length <= 211 && !char.IsHighSurrogate(fe[fe.Length - 5]) && fe.EndsWith(".mp4"), "Emoji partido: " + fe.Length);
            Check(Util.FitFileName(@"C:\t", new string('f', 205) + "   ....x" + new string('g', 50) + ".zip").IndexOf(" .zip", StringComparison.Ordinal) < 0, "Acaba en espacio");
            Check(Util.FitFileName(@"C:\t", null) == null && Util.FitFileName(@"C:\t", "") == "", "Vacío");
        }

        static void TL_Download()
        {
            AppConfig c = Cfg(); c.UseTempFolder = true; c.TempFolder = Path.Combine(Home, "temporal");
            DownloadManager m = new DownloadManager(c);
            long size = 1024 * 1024;
            string name = "nombre-largo-" + new string('x', 230) + ".bin";
            DownloadItem it = m.Add(S.Url("range", name, size, NewId("largo")), Dl, null, true);
            Check(Path.GetFileName(it.FilePath).Length <= Util.MaxFileNameLength && it.FilePath.Length <= Util.MaxPathLength && it.FilePath.EndsWith(".bin"),
                  "Nombre no acortado: " + it.FilePath.Length + " " + it.FilePath);
            WaitEnd(it, 60);
            AssertComplete(it, size);
            Check(Path.GetFileName(it.FilePath).StartsWith("nombre-largo-xxx"), "Nombre final: " + it.FilePath);
            Check(Directory.GetFiles(c.TempFolder, "*", SearchOption.AllDirectories).Length == 0, "Quedó algo en la carpeta temporal");
        }

        static void TL_WorkName()
        {
            string w = Util.WorkFileName(new string('w', 300) + ".mp4");
            Check(w == new string('w', 40) + ".mp4", "Archivo de trabajo: " + w);
            Check(Util.WorkFileName("informe.pdf") == "informe.pdf", "Uno corto queda igual");
            Check(Util.WorkFileName("") == "descarga" && Util.WorkFileName(null) == "descarga", "Sin nombre");
            Check(Util.WorkFileName("a:b?.zip") == "a_b_.zip", "Caracteres no válidos");
        }
    }
}
