@echo off
title Instalador LotusLED Desk
echo Instalando LotusLED Desk...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Instalar.ps1"
echo.
echo Presiona cualquier tecla para salir...
pause > nul
