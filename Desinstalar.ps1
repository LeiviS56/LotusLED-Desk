<#
  Desinstala LotusLED Desk: quita los accesos directos, el inicio con Windows, el ayudante de las luces
  y las tareas programadas. No borra la carpeta del proyecto ni tu configuración.
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

# Las tareas programadas. Si se crearon como administrador, sin serlo no se pueden quitar: se avisa
foreach ($task in @('LotusLED_Shutdown', 'LotusLED_Logon')) {
    Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
    if (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) {
        Write-Output "  ! No se pudo quitar la tarea $task (se creó como administrador)."
        Write-Output "    Ejecuta Desinstalar.bat con clic derecho, 'Ejecutar como administrador'."
    } else {
        Write-Output "  - Tarea $task eliminada (si existía)"
    }
}
Write-Output "`nDesinstalación completada."
