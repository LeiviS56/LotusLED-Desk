<#
  Desinstala LotusLED Desk: quita los accesos directos, el inicio con Windows, el ayudante de las luces
  y la tarea de respaldo. No borra la carpeta del proyecto ni tu configuración.
#>
$dir = $PSScriptRoot
$startup = [Environment]::GetFolderPath('Startup')
$links = @(
    (Join-Path (Split-Path $dir -Parent) 'LotusLED Desk.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'LotusLED Desk.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'LotusLED Desk.lnk'),
    (Join-Path $startup 'LotusLED Luces.lnk'),
    (Join-Path $startup 'LotusLED.lnk'),
    (Join-Path $dir 'Ayudante.lnk'),
    (Join-Path $dir 'Ayudante (encender).lnk'),
    (Join-Path $env:LOCALAPPDATA 'LotusLED\Ayudante.lnk'),
    (Join-Path $env:LOCALAPPDATA 'LotusLED\Ayudante (encender).lnk')
)
foreach ($l in $links) {
    if (Test-Path $l) { Remove-Item -Force $l; Write-Output "  - Eliminado: $l" }
}

# Entrada de registro que crea LotusLED.exe -install
Remove-ItemProperty -Path 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'LotusLED' -ErrorAction SilentlyContinue

# El ayudante que vigila el apagado
Get-Process -Name 'LotusShutdown' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Write-Output "  - Ayudante de las luces cerrado (si estaba en marcha)"

Unregister-ScheduledTask -TaskName 'LotusLED_Shutdown' -Confirm:$false -ErrorAction SilentlyContinue
Write-Output "  - Tarea LotusLED_Shutdown eliminada (si existía)"
Write-Output "`nDesinstalación completada."
