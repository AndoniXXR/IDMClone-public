@echo off
rem Registra el host de IDMClone en los navegadores (doble clic). Equivale a install.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
echo.
pause
