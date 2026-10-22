<#
  Desinstala LotusLED Desk: quita los accesos directos, el inicio automático y la tarea de apagado.
  No borra la carpeta del proyecto ni tu configuración.
#>
$dir = $PSScriptRoot
$links = @(
    (Join-Path (Split-Path $dir -Parent) 'LotusLED Desk.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'LotusLED Desk.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'LotusLED Desk.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Startup')) 'LotusLED.lnk')
)
foreach ($l in $links) {
    if (Test-Path $l) { Remove-Item -Force $l; Write-Output "  - Eliminado: $l" }
}

# Entrada de registro que crea LotusLED.exe -install
Remove-ItemProperty -Path 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'LotusLED' -ErrorAction SilentlyContinue

Unregister-ScheduledTask -TaskName 'LotusLED_Shutdown' -Confirm:$false -ErrorAction SilentlyContinue
Write-Output "  - Tarea LotusLED_Shutdown eliminada (si existía)"
Write-Output "`nDesinstalación completada."
