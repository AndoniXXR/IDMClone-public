@echo off
rem Arranca el servidor de pruebas (descargas reales en http://127.0.0.1:18800) y abre todos los enlaces de
rem index.html en la lista de enlaces de IDMClone. Sirve en el repo y en la copia del Sandbox (misma estructura).
set "AQUI=%~dp0"
start "Servidor de descargas (cierra esta ventana para pararlo)" /min "%AQUI%..\bin\IDMCloneTests.exe" --serve 18800
timeout /t 2 /nobreak >nul
start "" "%AQUI%..\..\IDMClone.exe" /linksfile "%AQUI%index.html"
