Bibliotecas del motor de YouTube de IDMClone (src\YouTube*.cs)
================================================================

build.ps1 las mete comprimidas DENTRO de IDMClone.exe (recursos IDMClone.lib.*.dll.gz e IDMClone.ejs.*.gz);
src\YouTubeLibs.cs las carga desde ahí con AppDomain.AssemblyResolve. IDMClone sigue siendo un solo exe: estos
archivos no se instalan ni hace falta copiarlos junto a él.

DLL (.NET Framework, de nuget.org; se usa la carpeta lib/net46x de cada paquete)
  Jint 4.17.0                                   lib/net462   BSD-2-Clause   https://github.com/sebastienros/jint
  Acornima 1.9.1                                lib/net462   BSD-3-Clause   https://github.com/adams85/acornima
  System.Memory 4.5.5                           lib/net461   MIT            (LICENSE-dotnet.txt)
  System.Buffers 4.5.1                          lib/net461   MIT
  System.Numerics.Vectors 4.5.0                 lib/net46    MIT
  System.Runtime.CompilerServices.Unsafe 6.0.0  lib/net461   MIT
  Jint 4.17 se compiló contra Acornima 1.7.0; el cargador da la 1.9.1 (y una sola copia de cada DLL aunque se
  pidan con otra versión).

Descifrador de firmas de YouTube: yt-dlp/ejs 0.8.0 (Unlicense; lleva dentro meriyah 6.1.4, ISC, y astring, MIT)
  https://github.com/yt-dlp/ejs/releases/tag/0.8.0
  ejs\yt.solver.lib.js   ejs\yt.solver.core.js
  Para ponerlo al día sin recompilar: copiar los dos archivos nuevos a %APPDATA%\IDMClone\youtube\ejs\.

SHA-256
  b720af5f4b047db31574f78f92c0793dcd1610813c26c6814ee6c1d1794e8b03  Acornima.dll
  a29683efd3e1fa77bd5b8dfa200e6a53e8a578e8e15be168bf3ccaf88dc21ea4  Jint.dll
  accccfbe45d9f08ffeed9916e37b33e98c65be012cfff6e7fa7b67210ce1fefb  System.Buffers.dll
  bf3fb84664f4097f1a8a9bc71a51dcf8cf1a905d4080a4d290da1730866e856f  System.Memory.dll
  1d3ef8698281e7cf7371d1554afef5872b39f96c26da772210a33da041ba1183  System.Numerics.Vectors.dll
  37768488e8ef45729bc7d9a2677633c6450042975bb96516e186da6cb9cd0dcf  System.Runtime.CompilerServices.Unsafe.dll
  ca259e4e3cdd37d92fc266d9af08d4fd66da8479e240f4d984f29da402c22ead  ejs\yt.solver.core.js
  770831df5c46474fbff06732315b28f4fb090e427ca669a51da61e2457d41c82  ejs\yt.solver.lib.js
