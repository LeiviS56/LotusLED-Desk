<#
  Instalador de LotusLED Desk.
  - Accesos directos a la interfaz (Escritorio, Menú Inicio y junto a la carpeta del proyecto).
  - Al iniciar Windows NO se abre ninguna ventana: arranca LotusShutdown.exe, un ayudante sin ventana que
    enciende las luces al iniciar sesión y las apaga al apagar, reiniciar o cerrar sesión.
  - Tarea programada de respaldo por si el ayudante no estuviera en marcha.
  Funciona desde cualquier carpeta donde copies el proyecto.
#>
$dir     = $PSScriptRoot
$ps      = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$ico     = Join-Path $dir 'LotusLED.ico'
$script  = Join-Path $dir 'LotusLED.ps1'
$helper  = Join-Path $dir 'LotusShutdown.exe'
$dataDir = Join-Path $env:LOCALAPPDATA 'LotusLED'
$startup = [Environment]::GetFolderPath('Startup')
$ws      = New-Object -ComObject WScript.Shell

function New-Shortcut($path, $target, [string]$arguments, [string]$desc, [int]$style = 7) {
    $s = $ws.CreateShortcut($path)
    $s.TargetPath = $target
    $s.Arguments = $arguments
    $s.WorkingDirectory = $dir
    if (Test-Path $ico) { $s.IconLocation = "$ico,0" }
    $s.WindowStyle = $style
    $s.Description = $desc
    $s.Save()
    Write-Output "  - $path"
}

Write-Output "Instalando LotusLED Desk desde: $dir`n"

# El ayudante tiene que ser la versión 1.1.1 o más: la 1.0 tomaba cualquier argumento por una MAC y apagaba las
# luces (con ella en Inicio se apagarían al iniciar Windows), y la 1.1.0 no atendía el aviso para encender.
function Test-HelperOk {
    if (-not (Test-Path $helper)) { return $false }
    try {
        $v = [Version](Get-Item $helper).VersionInfo.FileVersion
        return ($v -ge [Version]'1.1.1.0')
    } catch { return $false }
}

# 1. El ayudante: se compila si falta, si es de una versión antigua o si el código es más nuevo que el .exe
$src = Join-Path $dir 'src\LotusShutdown.cs'
$needBuild = -not (Test-HelperOk)
if (-not $needBuild -and (Test-Path $src)) {
    $needBuild = (Get-Item $src).LastWriteTime -gt (Get-Item $helper).LastWriteTime
}
if ($needBuild) {
    Write-Output "Compilando los .exe (build.ps1)..."
    $buildError = $null
    try {
        & (Join-Path $dir 'build.ps1')
    } catch {
        $buildError = $_.Exception.Message
        Write-Output "  No se pudo compilar: $buildError"
    }
    Write-Output ""
    # Si no compiló, el .exe que haya es el de antes (puede ser el antiguo): mejor no instalar nada
    if ($buildError -and (Test-Path $src) -and (Test-Path $helper) -and
        (Get-Item $src).LastWriteTime -gt (Get-Item $helper).LastWriteTime) {
        Write-Output "No se instaló nada: LotusShutdown.exe no se pudo compilar y el que hay es de una versión anterior."
        Write-Output "Mira el error de arriba, arréglalo y vuelve a ejecutar Instalar.bat."
        exit 1
    }
}
if (-not (Test-HelperOk)) {
    Write-Output "Falta LotusShutdown.exe (o es de una versión antigua), así que las luces no se encenderán ni apagarán solas."
    Write-Output "No se instaló nada. Ejecuta build.ps1, mira si da algún error y vuelve a ejecutar Instalar.bat."
    exit 1
}

# 2. ¿Qué tira es la tuya? (la interfaz web no puede saber su MAC, el ayudante la necesita)
$deviceCfg = Join-Path $dir 'device.cfg'
function Get-KnownMacs {
    $macs = @()
    foreach ($f in @($deviceCfg, (Join-Path $dataDir 'device.cfg'))) {
        if (Test-Path $f) {
            foreach ($l in Get-Content $f) {
                if ($l -match '^\s*([0-9A-Fa-f]{2}([:-][0-9A-Fa-f]{2}){5}|[0-9A-Fa-f]{12})\b') { $macs += $Matches[1] }
            }
        }
    }
    # La última tira de LotusLED.exe (settings.cfg) no cuenta: puede ser la de un vecino (ya pasó con una L7179)
    return $macs
}

if (@(Get-KnownMacs).Count -eq 0) {
    Write-Output "Todavía no sé cuál es tu tira LED."
    Write-Output "Cierra la ventana de LotusLED Desk y la app LotusLamp X del móvil (la tira tiene que estar libre),"
    Read-Host "deja la tira enchufada y pulsa Enter para buscarla"
    $found = Join-Path $env:TEMP 'lotusled-tiras.txt'
    Remove-Item $found -ErrorAction SilentlyContinue
    Write-Output "Buscando tiras durante 10 segundos..."
    Start-Process -FilePath $helper -ArgumentList @('-find', "`"$found`"", '10') -Wait
    $list = @()
    if (Test-Path $found) { $list = @(Get-Content $found -Encoding UTF8 | Where-Object { $_ -and -not $_.StartsWith('#') }) }
    if ($list.Count -eq 0) {
        Write-Output "  No encontré ninguna. Puedes escribir la MAC de tu tira en device.cfg (una por línea) y volver a instalar."
    } else {
        for ($i = 0; $i -lt $list.Count; $i++) {
            $p = $list[$i].Split('|')
            Write-Output ("  {0}) {1}  {2}  (señal {3} dBm)" -f ($i + 1), $p[1], $p[0], $p[2])
        }
        Write-Output "  La primera es la que está más cerca. Ojo con las de los vecinos."
        $ans = Read-Host "Números de tus tiras separados por comas (Enter = la 1; 0 = ninguna)"
        if ([string]::IsNullOrWhiteSpace($ans)) { $ans = '1' }
        $chosen = @()
        foreach ($n in $ans.Split(',')) {
            $k = 0
            if ([int]::TryParse($n.Trim(), [ref]$k) -and $k -ge 1 -and $k -le $list.Count) { $chosen += $list[$k - 1] }
        }
        $lines = @('# MAC de tus tiras LED (una por línea). Las usa LotusShutdown.exe para encenderlas y apagarlas.',
                   '# Este archivo es personal y NO se sube a GitHub (está en .gitignore).')
        if ($chosen.Count -gt 0) {
            foreach ($c in $chosen) { $p = $c.Split('|'); $lines += "# $($p[1])"; $lines += $p[0] }
            Set-Content -Path $deviceCfg -Value $lines -Encoding UTF8
            Write-Output "  Guardado en device.cfg."
        } elseif ($ans.Trim() -eq '0') {
            # Elegiste ninguna: device.cfg sin MAC, para que el ayudante no use la última tira de LotusLED.exe
            # (settings.cfg), que puede ser la de un vecino
            $lines += '# Elegiste "ninguna" al instalar: el ayudante no enciende ni apaga ninguna tira.'
            Set-Content -Path $deviceCfg -Value $lines -Encoding UTF8
            Write-Output "  Ninguna: las luces no se encenderán ni apagarán solas hasta que pongas una MAC en device.cfg."
        }
    }
    Write-Output ""
}

Write-Output "Accesos directos:"
# 3. Interfaz (para cuando quieras cambiar el color a mano)
$uiArgs = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`""
New-Shortcut (Join-Path (Split-Path $dir -Parent) 'LotusLED Desk.lnk') $ps $uiArgs 'Controlador de tiras LED (LotusLED Desk)'
New-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'LotusLED Desk.lnk') $ps $uiArgs 'Controlador de tiras LED (LotusLED Desk)'
New-Shortcut (Join-Path ([Environment]::GetFolderPath('Programs')) 'LotusLED Desk.lnk') $ps $uiArgs 'Controlador de tiras LED (LotusLED Desk)'

# 4. Inicio con Windows: solo el ayudante, sin ventana. Se quita el acceso antiguo que abría la interfaz
#    (y la entrada de registro que crea LotusLED.exe -install), que además se quedaría con la tira.
$oldLnk = Join-Path $startup 'LotusLED.lnk'
if (Test-Path $oldLnk) { Remove-Item -Force $oldLnk; Write-Output "  - Quitado el inicio antiguo que abría la ventana" }
Remove-ItemProperty -Path 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'LotusLED' -ErrorAction SilentlyContinue
New-Shortcut (Join-Path $startup 'LotusLED Luces.lnk') $helper '-watch -on' 'LotusLED Desk: enciende las luces al iniciar Windows y las apaga al apagar (sin ventana)' 1

# 5. Tarea programada de respaldo: evento 1074 de USER32 (alguien pide apagar o reiniciar). Si el ayudante
#    está en marcha no hace nada; si no, intenta apagar las tiras. Sin límite de batería.
try {
    $action   = New-ScheduledTaskAction -Execute $helper -Argument '-task' -WorkingDirectory $dir
    $trigger  = Get-CimClass -ClassName MSFT_TaskEventTrigger -Namespace Root/Microsoft/Windows/TaskScheduler | New-CimInstance -ClientOnly
    $trigger.Enabled = $true
    $trigger.Subscription = '<QueryList><Query Id="0" Path="System"><Select Path="System">*[System[Provider[@Name="USER32"] and (EventID=1074)]]</Select></Query></QueryList>'
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 2) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName 'LotusLED_Shutdown' -Action $action -Trigger $trigger -Settings $settings `
        -Description 'LotusLED Desk: respaldo para apagar las tiras LED al apagar el PC' -Force -ErrorAction Stop | Out-Null
    Write-Output "  - Tarea programada de respaldo: LotusLED_Shutdown"
} catch {
    Write-Output "  (No se pudo crear la tarea de respaldo: $($_.Exception.Message))"
}

# 6. Arrancar el ayudante ya (cerrando uno anterior, por si era de otra versión o de otra carpeta).
#    Sin -on: instalar no cambia las luces; se encenderán solas la próxima vez que inicies Windows.
$unknown = 0
Get-Process -Name 'LotusShutdown' -ErrorAction SilentlyContinue | Where-Object {
    # Uno que esté buscando tiras o comprobándolas (-find, -probe) se deja terminar, y uno del que no se pueda
    # leer qué hace (de otro usuario o como administrador) también
    $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId=$($_.Id)" -ErrorAction SilentlyContinue).CommandLine
    if (-not $cmd) { $script:unknown++ }
    $cmd -and $cmd -match '-watch|-task'
} | Stop-Process -Force -ErrorAction SilentlyContinue
if ($unknown -gt 0) {
    Write-Output "  (Hay $unknown LotusShutdown.exe que no se pudo identificar; si las luces no van bien, reinicia el PC.)"
}
Start-Sleep -Milliseconds 500
Start-Process -FilePath $helper -ArgumentList '-watch' -WorkingDirectory $dir | Out-Null

Write-Output "`nInstalación completada."
Write-Output "Las luces se encenderán solas al iniciar Windows (sin abrir nada) y se apagarán al apagar el PC."
Write-Output "Si algo falla, mira lo que hizo el ayudante en: $(Join-Path $dataDir 'LotusShutdown.log')"
