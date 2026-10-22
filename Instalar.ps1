<#
  Instalador de LotusLED Desk.
  Crea accesos directos (Escritorio, Menú Inicio, inicio con Windows) y la tarea que apaga
  las luces al apagar el PC. Funciona desde cualquier carpeta donde copies el proyecto.
#>
$dir    = $PSScriptRoot
$ps     = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$ico    = Join-Path $dir 'LotusLED.ico'
$script = Join-Path $dir 'LotusLED.ps1'
$ws     = New-Object -ComObject WScript.Shell

function New-Shortcut($path, [string]$extraArgs = '', [string]$desc = 'Controlador de tiras LED (LotusLED Desk)') {
    $s = $ws.CreateShortcut($path)
    $s.TargetPath = $ps
    $s.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`" $extraArgs".Trim()
    $s.WorkingDirectory = $dir
    if (Test-Path $ico) { $s.IconLocation = "$ico,0" }
    $s.WindowStyle = 7
    $s.Description = $desc
    $s.Save()
    Write-Output "  - $path"
}

Write-Output "Instalando LotusLED Desk desde: $dir"

# 1. Acceso directo en la carpeta padre del proyecto
New-Shortcut (Join-Path (Split-Path $dir -Parent) 'LotusLED Desk.lnk')
# 2. Escritorio
New-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'LotusLED Desk.lnk')
# 3. Menú Inicio
New-Shortcut (Join-Path ([Environment]::GetFolderPath('Programs')) 'LotusLED Desk.lnk')
# 4. Inicio automático con Windows
New-Shortcut (Join-Path ([Environment]::GetFolderPath('Startup')) 'LotusLED.lnk') '-Startup' 'LotusLED Desk (inicio automático)'

# 5. Tarea programada: apaga las tiras al apagar o reiniciar el PC (evento 1074 de USER32)
$shutdownExe = Join-Path $dir 'LotusShutdown.exe'
if (Test-Path $shutdownExe) {
    $action  = New-ScheduledTaskAction -Execute $shutdownExe -WorkingDirectory $dir
    $trigger = Get-CimClass -ClassName MSFT_TaskEventTrigger -Namespace Root/Microsoft/Windows/TaskScheduler | New-CimInstance -ClientOnly
    $trigger.Enabled = $true
    $trigger.Subscription = '<QueryList><Query Id="0" Path="System"><Select Path="System">*[System[Provider[@Name="USER32"] and (EventID=1074)]]</Select></Query></QueryList>'
    Register-ScheduledTask -TaskName 'LotusLED_Shutdown' -Action $action -Trigger $trigger -Description 'Apaga tiras LED al apagar el PC' -Force -ErrorAction SilentlyContinue | Out-Null
    Write-Output "  - Tarea programada: LotusLED_Shutdown"
} else {
    Write-Output "  (No se encontró LotusShutdown.exe: ejecuta build.ps1 para crearlo)"
}

Write-Output "`nInstalación completada."
