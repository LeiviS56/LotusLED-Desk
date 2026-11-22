<#
  LotusLED Desk - lanzador y servidor de fondo
  Sirve la interfaz en http://localhost:8787 (Web Bluetooth necesita un origen seguro),
  gestiona el icono en la bandeja del sistema (System Tray) y el arranque automático con Windows.
  Encender las luces al iniciar Windows y apagarlas al apagar el PC lo hace LotusShutdown.exe, un ayudante
  sin ventana que este script pone en marcha; así funciona aunque la ventana esté cerrada.
#>
param(
    [int]$Port = 8787,
    [switch]$NoWindow,   # solo servidor (no abre ventana)
    [switch]$Startup     # accesos de inicio antiguos: ahora solo arranca el ayudante, sin ventana
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = Join-Path $PSScriptRoot 'web'
$url = "http://localhost:$Port/"
$profileDir = Join-Path $env:LOCALAPPDATA 'LotusLED\BrowserProfile'
$icoFile = Join-Path $PSScriptRoot 'LotusLED.ico'
$helperExe = Join-Path $PSScriptRoot 'LotusShutdown.exe'
$startupDir = [Environment]::GetFolderPath('Startup')
$startupLnk = Join-Path $startupDir 'LotusLED Luces.lnk'
$oldStartupLnk = Join-Path $startupDir 'LotusLED.lnk'   # el de antes, que abría la ventana al iniciar Windows
$autoCfg = Join-Path $env:LOCALAPPDATA 'LotusLED\auto.cfg'

function Test-Autostart {
    return ((Test-Path $startupLnk) -or (Test-Path $oldStartupLnk))
}

# El ayudante tiene que ser la versión 1.1 o más: la 1.0 tomaba cualquier argumento por una MAC y apagaba las luces
function Test-HelperOk {
    if (-not (Test-Path $helperExe)) { return $false }
    try {
        $v = [Version](Get-Item $helperExe).VersionInfo.FileVersion
        return ($v -ge [Version]'1.1.0.0')
    } catch { return $false }
}

function Test-HelperRunning {
    $m = $null
    try {
        if ([System.Threading.Mutex]::TryOpenExisting('Local\LotusLED_Ayudante_Luces', [ref]$m)) { $m.Dispose(); return $true }
    } catch {}
    return $false
}

# Devuelve si quedó como se pidió. Sin el ayudante no se crea nada: abrir la ventana al iniciar Windows no es una opción.
function Set-Autostart([bool]$enable) {
    if (Test-Path $oldStartupLnk) { Remove-Item -Force $oldStartupLnk -ErrorAction SilentlyContinue }
    if (-not $enable) {
        if (Test-Path $startupLnk) { Remove-Item -Force $startupLnk -ErrorAction SilentlyContinue }
        return $true
    }
    if (-not (Test-HelperOk)) {
        if (Test-Path $startupLnk) { Remove-Item -Force $startupLnk -ErrorAction SilentlyContinue }
        return $false
    }
    $ws = New-Object -ComObject WScript.Shell
    $s = $ws.CreateShortcut($startupLnk)
    # Sin ventana: el ayudante enciende las luces al iniciar sesión y las apaga al apagar
    $s.TargetPath = $helperExe
    $s.Arguments = '-watch -on'
    $s.Description = 'LotusLED Desk: enciende las luces al iniciar Windows y las apaga al apagar (sin ventana)'
    $s.WindowStyle = 1
    $s.WorkingDirectory = $PSScriptRoot
    if (Test-Path $icoFile) { $s.IconLocation = "$icoFile,0" }
    $s.Save()
    return $true
}

# ¿El acceso de Inicio ya apunta al ayudante? (uno de una versión anterior podía abrir PowerShell)
function Test-AutostartIsHelper {
    if (-not (Test-Path $startupLnk)) { return $false }
    try {
        $s = (New-Object -ComObject WScript.Shell).CreateShortcut($startupLnk)
        return ($s.TargetPath -eq $helperExe -and $s.Arguments -eq '-watch -on')
    } catch { return $false }
}

function Start-Helper([switch]$PowerOn) {
    if (-not (Test-HelperOk)) { return $false }
    $helperArgs = @('-watch')
    if ($PowerOn) { $helperArgs += '-on' }
    try {
        # Si ya hay uno en marcha, el nuevo se cierra solo (con -on, antes le pide que encienda)
        Start-Process -FilePath $helperExe -ArgumentList $helperArgs -WorkingDirectory $PSScriptRoot | Out-Null
        return $true
    } catch { return $false }
}

# Opciones que lee el ayudante (encender al iniciar, apagar al apagar y si las luces estaban encendidas)
$script:autoCfgText = ''
$script:cfgLightsOn = $true
try {
    if (Test-Path $autoCfg) {
        $script:autoCfgText = [IO.File]::ReadAllText($autoCfg)
        if ($script:autoCfgText -match '(?m)^LightsOn=False') { $script:cfgLightsOn = $false }
    }
} catch {}

function Save-AutoCfg([bool]$powerOnStart, [bool]$powerOffShutdown, [bool]$lightsOn) {
    $text = "# LotusLED Desk: lo escribe la interfaz y lo lee LotusShutdown.exe (el ayudante sin ventana)`r`n" +
            "PowerOnStart=$powerOnStart`r`nPowerOffShutdown=$powerOffShutdown`r`nLightsOn=$lightsOn`r`n"
    if ($text -eq $script:autoCfgText) { return }
    try {
        New-Item -ItemType Directory -Force -Path (Split-Path $autoCfg) | Out-Null
        [IO.File]::WriteAllText($autoCfg, $text)
        $script:autoCfgText = $text
    } catch {}
}

# Arranque con Windows desde un acceso antiguo: ya no se abre la ventana (ni minimizada). Se pone en marcha el
# ayudante (que enciende las luces) y el acceso se cambia por el nuevo. Sin ayudante no se hace nada.
if ($Startup) {
    if (Test-HelperOk) {
        Start-Helper -PowerOn | Out-Null
        if ((Test-Path $oldStartupLnk) -or -not (Test-AutostartIsHelper)) { try { Set-Autostart $true | Out-Null } catch {} }
    }
    exit 0
}

function Find-Browser {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
        "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
        "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe"
    )
    foreach ($p in $candidates) { if ($p -and (Test-Path $p)) { return $p } }
    return $null
}

$browser = Find-Browser
$appUrl = if ($Startup) { "$url#startup" } else { $url }
$browserArgs = @(
    "--app=$appUrl",
    "--user-data-dir=`"$profileDir`"",
    '--window-size=1320,880',
    '--enable-features=WebBluetoothNewPermissionsBackend',
    '--enable-experimental-web-platform-features',
    '--disable-background-timer-throttling',
    '--disable-renderer-backgrounding',
    '--disable-backgrounding-occluded-windows',
    '--no-first-run',
    '--no-default-browser-check'
)

function Open-Window {
    if ($browser) {
        Start-Process -FilePath $browser -ArgumentList $browserArgs | Out-Null
    } else {
        Start-Process $url | Out-Null
    }
}

function Test-WindowOpen {
    if ($NoWindow -or -not $browser) { return $true }
    $exe = [System.IO.Path]::GetFileName($browser)
    $procs = Get-CimInstance Win32_Process -Filter "Name='$exe'" -ErrorAction SilentlyContinue
    return [bool]($procs | Where-Object { $_.CommandLine -like '*LotusLED\BrowserProfile*' })
}

# El ayudante vigila el apagado aunque luego se cierre la ventana
Start-Helper | Out-Null

# Inicializar servidor HTTP
$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add($url)
try {
    $listener.Start()
} catch {
    # Ya hay una instancia corriendo: solo abrimos la ventana y salimos
    if (-not $Startup) { Open-Window }
    exit 0
}

# Variables de estado del backend
$script:running = $true
$script:isShuttingDown = $false
$script:shutdownWatch = $null       # desde cuándo: si a los 25 s seguimos vivos, Windows canceló el apagado
$script:shutdownDelivered = $false  # ya se avisó a la ventana
$script:shutdownAck = $false
$script:pollRes = $null             # respuesta de la petición /api/poll que está esperando
$script:draining = $false           # la ventana se cerró: se atiende lo pendiente y se sale
$script:keepInBackground = $true
$script:eventQueue = New-Object System.Collections.Concurrent.ConcurrentQueue[psobject]
$script:lastDeviceState = @{
    connected = $false
    power = $false
    name = ''
}

# Aviso de apagado de Windows (llega en este mismo hilo, durante DoEvents).
# Antes se esperaba aquí a /api/shutdown-ack, pero este hilo es el único que atiende peticiones, así que la
# confirmación no podía llegar y el aviso a la ventana salía tarde. Ahora se contesta al momento la petición
# /api/poll que la ventana tiene abierta y se atienden aquí mismo las que lleguen hasta que confirma (máx. 2 s).
# Con el ayudante (LotusShutdown.exe) en marcha no se hace nada: él apaga la tira el último, cuando el navegador ya
# se cerró, y solo si nadie canceló el apagado. Apagarla desde aquí la dejaría apagada aunque luego se cancelara.
# Esto queda solo para cuando no hay ayudante.
$onSessionEnding = {
    param($sender, $e)
    if (Test-HelperRunning) { return }
    $script:isShuttingDown = $true
    $script:shutdownAck = $false
    $script:shutdownWatch = [System.Diagnostics.Stopwatch]::StartNew()
    $r = $script:pollRes
    if ($r) {
        $script:pollRes = $null
        $script:shutdownDelivered = $true
        try { Send-Json $r @{ event = 'shutdown' } } catch {}
    }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 2000 -and -not $script:shutdownAck -and $script:acceptTask) {
        try {
            $t = $script:acceptTask
            if ($t.Wait(40) -and [object]::ReferenceEquals($t, $script:acceptTask)) {
                $script:acceptTask = $listener.GetContextAsync()
                Send-Static $t.Result
            }
        } catch { break }
    }
}
[Microsoft.Win32.SystemEvents]::add_SessionEnding($onSessionEnding)

# Bandeja del sistema (NotifyIcon)
$notifyIcon = $null
if (-not $NoWindow) {
    try {
        $notifyIcon = New-Object System.Windows.Forms.NotifyIcon
        if (Test-Path $icoFile) {
            $notifyIcon.Icon = New-Object System.Drawing.Icon($icoFile)
        }
        $notifyIcon.Text = "LotusLED Desk"
        $notifyIcon.Visible = $true

        $menu = New-Object System.Windows.Forms.ContextMenuStrip
        $itemOpen = $menu.Items.Add("Abrir LotusLED Desk")
        $itemOpen.add_Click({ Open-Window })
        $itemOpen.Font = New-Object System.Drawing.Font($itemOpen.Font, [System.Drawing.FontStyle]::Bold)

        $menu.Items.Add("-") | Out-Null

        $itemOn = $menu.Items.Add("Encender luces")
        $itemOn.add_Click({
            $script:eventQueue.Enqueue([pscustomobject]@{ event = 'set-power'; power = $true })
        })

        $itemOff = $menu.Items.Add("Apagar luces")
        $itemOff.add_Click({
            $script:eventQueue.Enqueue([pscustomobject]@{ event = 'set-power'; power = $false })
        })

        $menu.Items.Add("-") | Out-Null

        $itemExit = $menu.Items.Add("Cerrar LotusLED")
        $itemExit.add_Click({
            $script:running = $false
        })

        $notifyIcon.ContextMenuStrip = $menu
        $notifyIcon.add_DoubleClick({ Open-Window })
    } catch {
        # Si falla NotifyIcon no impedimos el funcionamiento del servidor
    }
}

$mime = @{
    '.html' = 'text/html; charset=utf-8'
    '.css'  = 'text/css; charset=utf-8'
    '.js'   = 'text/javascript; charset=utf-8'
    '.json' = 'application/json; charset=utf-8'
    '.png'  = 'image/png'
    '.jpg'  = 'image/jpeg'
    '.svg'  = 'image/svg+xml'
    '.ico'  = 'image/x-icon'
}
$rootFull = [System.IO.Path]::GetFullPath($root)

function Send-Json($res, $obj, [int]$status = 200) {
    try {
        $res.StatusCode = $status
        $res.ContentType = 'application/json; charset=utf-8'
        $res.Headers['Cache-Control'] = 'no-cache, no-store'
        $json = $obj | ConvertTo-Json -Compress
        $bytes = [Text.Encoding]::UTF8.GetBytes($json)
        $res.ContentLength64 = $bytes.Length
        $res.OutputStream.Write($bytes, 0, $bytes.Length)
    } finally {
        try { $res.OutputStream.Close() } catch {}
    }
}

function Handle-Api($ctx, $path) {
    $req = $ctx.Request
    $res = $ctx.Response

    if ($path -eq '/api/autostart') {
        if ($req.HttpMethod -eq 'POST') {
            $body = ''
            if ($req.HasEntityBody) {
                $sr = New-Object System.IO.StreamReader($req.InputStream, [System.Text.Encoding]::UTF8)
                $body = $sr.ReadToEnd()
            }
            $enabled = $true
            try {
                $data = $body | ConvertFrom-Json
                if ($null -ne $data.enabled) { $enabled = [bool]$data.enabled }
            } catch {}
            $done = $false
            try { $done = [bool](Set-Autostart $enabled) } catch {}
            if ($done -and $enabled) { Start-Helper | Out-Null }
            $answer = @{ ok = $done; autostart = (Test-Autostart) }
            if (-not $done) { $answer.error = 'Falta LotusShutdown.exe (o es una versión antigua). Ejecuta Instalar.bat.' }
            Send-Json $res $answer
        } else {
            Send-Json $res @{ autostart = (Test-Autostart) }
        }
        return
    }

    if ($path -eq '/api/poll') {
        # Si a los 25 s del aviso seguimos aquí, Windows canceló el apagado: todo vuelve a la normalidad
        if ($script:isShuttingDown -and $script:shutdownWatch -and $script:shutdownWatch.Elapsed.TotalSeconds -gt 25) {
            $script:isShuttingDown = $false
            $script:shutdownDelivered = $false
        }

        # Apagado inminente: avisar una vez y no quedarse esperando
        if ($script:isShuttingDown) {
            if ($script:shutdownDelivered) {
                Send-Json $res @{ event = 'ping' }
            } else {
                $script:shutdownDelivered = $true
                Send-Json $res @{ event = 'shutdown' }
            }
            return
        }

        # La ventana ya se cerró y estamos atendiendo lo último: no esperar
        if ($script:draining) {
            Send-Json $res @{ event = 'ping' }
            return
        }

        # Comprobar eventos en cola
        $ev = $null
        if ($script:eventQueue.TryDequeue([ref]$ev)) {
            Send-Json $res $ev
            return
        }

        # Long poll: esperar hasta 15 s o hasta que haya evento. Si llega el aviso de apagado mientras tanto,
        # $onSessionEnding contesta esta misma petición ($script:pollRes) y aquí solo hay que salir.
        # Este hilo es el único que atiende peticiones: si llega otra (por ejemplo, las opciones que se acaban de
        # cambiar), se corta la espera para que no se pierda si luego se cierra la ventana.
        $script:pollRes = $res
        try {
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            while ($sw.ElapsedMilliseconds -lt 15000 -and $script:running) {
                [System.Windows.Forms.Application]::DoEvents()
                if (-not [object]::ReferenceEquals($script:pollRes, $res)) { return }
                if ($script:eventQueue.TryDequeue([ref]$ev)) {
                    $script:pollRes = $null
                    Send-Json $res $ev
                    return
                }
                if ($script:acceptTask -and $script:acceptTask.IsCompleted) { break }
                Start-Sleep -Milliseconds 80
            }
            if ([object]::ReferenceEquals($script:pollRes, $res)) {
                $script:pollRes = $null
                Send-Json $res @{ event = 'ping' }
            }
        } finally {
            if ([object]::ReferenceEquals($script:pollRes, $res)) { $script:pollRes = $null }
        }
        return
    }

    if ($path -eq '/api/shutdown-ack') {
        $script:shutdownAck = $true
        Send-Json $res @{ ok = $true }
        return
    }

    if ($path -eq '/api/state' -and $req.HttpMethod -eq 'POST') {
        try {
            $sr = New-Object System.IO.StreamReader($req.InputStream, [System.Text.Encoding]::UTF8)
            $data = $sr.ReadToEnd() | ConvertFrom-Json
            if ($data) {
                $script:lastDeviceState.connected = [bool]$data.connected
                $script:lastDeviceState.power = [bool]$data.power
                $script:lastDeviceState.name = [string]$data.name
                if ($null -ne $data.keepInBackground) {
                    $script:keepInBackground = [bool]$data.keepInBackground
                }
                # Para el ayudante sin ventana (LotusShutdown.exe)
                if ($data.connected) { $script:cfgLightsOn = [bool]$data.power }
                $pos = if ($null -ne $data.powerOnStart) { [bool]$data.powerOnStart } else { $true }
                $pof = if ($null -ne $data.powerOffShutdown) { [bool]$data.powerOffShutdown } else { $true }
                Save-AutoCfg $pos $pof $script:cfgLightsOn
                if ($notifyIcon) {
                    $estado = if ($data.power) { 'Encendido' } else { 'Apagado' }
                    $tip = if ($data.connected) {
                        "LotusLED Desk: $($data.name) ($estado)"
                    } else {
                        "LotusLED Desk: Desconectado"
                    }
                    if ($tip.Length -gt 63) { $tip = $tip.Substring(0, 60) + '…' }
                    $notifyIcon.Text = $tip
                }
            }
        } catch {}
        Send-Json $res @{ ok = $true }
        return
    }

    if ($path -eq '/api/quit') {
        $script:running = $false
        Send-Json $res @{ ok = $true }
        return
    }

    Send-Json $res @{ error = 'not_found' } 404
}

function Send-Static($ctx) {
    $res = $ctx.Response
    try {
        $path = [Uri]::UnescapeDataString($ctx.Request.Url.AbsolutePath)
        if ($path.StartsWith('/api/')) {
            Handle-Api $ctx $path
            return
        }
        if ($path -eq '/') { $path = '/index.html' }
        $file = [System.IO.Path]::GetFullPath((Join-Path $rootFull ($path.TrimStart('/') -replace '/', '\')))
        if (-not $file.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path $file -PathType Leaf)) {
            $res.StatusCode = 404
            $bytes = [Text.Encoding]::UTF8.GetBytes('404')
        } else {
            $ext = [System.IO.Path]::GetExtension($file).ToLower()
            $res.ContentType = if ($mime.ContainsKey($ext)) { $mime[$ext] } else { 'application/octet-stream' }
            $bytes = [System.IO.File]::ReadAllBytes($file)
        }
        $res.Headers['Cache-Control'] = 'no-cache'
        $res.ContentLength64 = $bytes.Length
        $res.OutputStream.Write($bytes, 0, $bytes.Length)
    } catch {
        try { $res.StatusCode = 500 } catch {}
    } finally {
        try { $res.OutputStream.Close() } catch {}
    }
}

# Abrir ventana
if ($NoWindow) {
    Write-Host "LotusLED sirviendo en $url (Ctrl+C para salir)"
} else {
    Open-Window
}

$started = Get-Date
$lastCheck = Get-Date
$script:acceptTask = $listener.GetContextAsync()

try {
    while ($script:running -and $listener.IsListening) {
        [System.Windows.Forms.Application]::DoEvents()

        # La espera puede procesar el aviso de apagado, que atiende peticiones por su cuenta y cambia
        # $script:acceptTask: solo se usa el resultado si la tarea sigue siendo la misma.
        $t = $script:acceptTask
        if ($t.Wait(100) -and [object]::ReferenceEquals($t, $script:acceptTask)) {
            # Primero se pide la siguiente (el aviso de apagado puede necesitarla) y luego se atiende esta
            $script:acceptTask = $listener.GetContextAsync()
            Send-Static $t.Result
        }

        # Cuando se cierra la ventana, salir para no consumir memoria RAM (después de atender lo que quede pendiente)
        if (((Get-Date) - $lastCheck).TotalSeconds -ge 3 -and ((Get-Date) - $started).TotalSeconds -ge 10) {
            $lastCheck = Get-Date
            if (-not (Test-WindowOpen)) {
                $script:draining = $true
                $drain = [System.Diagnostics.Stopwatch]::StartNew()
                while ($drain.ElapsedMilliseconds -lt 1500) {
                    $t = $script:acceptTask
                    if (-not $t.Wait(150)) { break }
                    if ([object]::ReferenceEquals($t, $script:acceptTask)) {
                        $script:acceptTask = $listener.GetContextAsync()
                        Send-Static $t.Result
                    }
                }
                break
            }
        }
    }
} finally {
    if ($notifyIcon) {
        $notifyIcon.Visible = $false
        $notifyIcon.Dispose()
    }
    try { [Microsoft.Win32.SystemEvents]::remove_SessionEnding($onSessionEnding) } catch {}
    $listener.Stop()
    $listener.Close()
}
