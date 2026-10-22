@echo off
title Desinstalador LotusLED Desk
echo Desinstalando LotusLED Desk...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Desinstalar.ps1"
echo.
echo Presiona cualquier tecla para salir...
pause > nul
