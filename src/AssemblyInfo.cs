using System.Reflection;
using System.Runtime.Versioning;

// csc.exe no añade este atributo por sí solo. Sin él, .NET ejecuta el programa en modo de
// compatibilidad antiguo (anterior a 4.6): entre otras cosas solo ofrece SSL3/TLS 1.0 y
// cualquier descarga HTTPS moderna falla con "No se puede crear un canal seguro SSL/TLS".
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

[assembly: AssemblyTitle("IDMClone")]
[assembly: AssemblyProduct("IDMClone")]
[assembly: AssemblyDescription("Gestor de descargas al estilo IDM")]
[assembly: AssemblyVersion("1.3.2.0")]
[assembly: AssemblyFileVersion("1.3.2.0")]
