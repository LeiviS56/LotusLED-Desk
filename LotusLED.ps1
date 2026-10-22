<#
  LotusLED Desk - lanzador y servidor de fondo
  Sirve la interfaz en http://localhost:8787 (Web Bluetooth necesita un origen seguro),
  gestiona el icono en la bandeja del sistema (System Tray), el arranque automático con Windows
  y el apagado de luces cuando se apaga el PC.
#>
param(
    [int]$Port = 8787,
    [switch]$NoWindow,   # solo servidor (no abre ventana)
    [switch]$Startup     # invocado al iniciar sesión en Windows
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = Join-Path $PSScriptRoot 'web'
$url = "http://localhost:$Port/"
$profileDir = Join-Path $env:LOCALAPPDATA 'LotusLED\BrowserProfile'
$icoFile = Join-Path $PSScriptRoot 'LotusLED.ico'
$startupLnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'LotusLED.lnk'

function Test-Autostart {
    return (Test-Path $startupLnk)
}

function Set-Autostart([bool]$enable) {
    if ($enable) {
        $ws = New-Object -ComObject WScript.Shell
        $s = $ws.CreateShortcut($startupLnk)
        $s.TargetPath = "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"
        $s.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$PSScriptRoot\LotusLED.ps1`" -Startup"
        $s.WorkingDirectory = $PSScriptRoot
        if (Test-Path $icoFile) { $s.IconLocation = "$icoFile,0" }
        $s.WindowStyle = 7 # Minimizada
        $s.Description = "Controlador de tiras LED (LotusLED Desk)"
        $s.Save()
    } else {
        if (Test-Path $startupLnk) { Remove-Item -Force $startupLnk -ErrorAction SilentlyContinue }
    }
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
$script:shutdownAck = $false
$script:keepInBackground = $true
$script:eventQueue = New-Object System.Collections.Concurrent.ConcurrentQueue[psobject]
$script:lastDeviceState = @{
    connected = $false
    power = $false
    name = ''
}

# Suscripción a eventos de apagado del sistema Windows
$onSessionEnding = {
    param($sender, $e)
    $script:isShuttingDown = $true
    $script:eventQueue.Enqueue([pscustomobject]@{ event = 'shutdown' })
    # Esperar hasta 1.2 segundos a que el navegador confirme que mandó el apagado BLE
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 1400 -and -not $script:shutdownAck) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 40
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
            Set-Autostart $enabled
            Send-Json $res @{ ok = $true; autostart = (Test-Autostart) }
        } else {
            Send-Json $res @{ autostart = (Test-Autostart) }
        }
        return
    }

    if ($path -eq '/api/poll') {
        # Comprobar si hay apagado inminente
        if ($script:isShuttingDown) {
            Send-Json $res @{ event = 'shutdown' }
            return
        }

        # Comprobar eventos en cola
        $ev = $null
        if ($script:eventQueue.TryDequeue([ref]$ev)) {
            Send-Json $res $ev
            return
        }

        # Long poll: esperar hasta 15s o hasta que haya evento
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        while ($sw.ElapsedMilliseconds -lt 15000 -and $script:running) {
            [System.Windows.Forms.Application]::DoEvents()
            if ($script:isShuttingDown) {
                Send-Json $res @{ event = 'shutdown' }
                return
            }
            if ($script:eventQueue.TryDequeue([ref]$ev)) {
                Send-Json $res $ev
                return
            }
            Start-Sleep -Milliseconds 80
        }
        Send-Json $res @{ event = 'ping' }
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
$task = $listener.GetContextAsync()

try {
    while ($script:running -and $listener.IsListening) {
        [System.Windows.Forms.Application]::DoEvents()

        if ($task.Wait(120)) {
            Send-Static $task.Result
            $task = $listener.GetContextAsync()
        }

        # Cuando se cierra la ventana, salir para no consumir memoria RAM
        if (((Get-Date) - $lastCheck).TotalSeconds -ge 3 -and ((Get-Date) - $started).TotalSeconds -ge 10) {
            $lastCheck = Get-Date
            if (-not (Test-WindowOpen)) { break }
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
