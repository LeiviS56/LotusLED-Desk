<#
  Compila LotusLED.exe (app de escritorio) y LotusShutdown.exe (el ayudante sin ventana que enciende las luces
  al iniciar Windows y las apaga al apagar el PC).
  Usa el compilador de C# que ya trae Windows (.NET Framework 4.x); no hace falta instalar nada.
  Funciona desde cualquier carpeta: todas las rutas son relativas a este script.
  -NoHelperRestart: no vuelve a abrir el ayudante al acabar (Instalar.ps1 lo abre él después).
#>
param([switch]$NoHelperRestart)
$ErrorActionPreference = 'Stop'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "No se encontró el compilador de C# en $csc" }

$here   = $PSScriptRoot
$srcDir = Join-Path $here 'src'
$ico    = Join-Path $here 'LotusLED.ico'
$fw     = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$winmd  = Join-Path $env:WINDIR 'System32\WinMetadata'

$refs = @(
    '/r:System.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll',
    "/r:$fw\System.Runtime.dll",
    "/r:$fw\System.Runtime.InteropServices.WindowsRuntime.dll",
    "/r:$fw\System.Runtime.WindowsRuntime.dll",
    "/r:$winmd\Windows.Foundation.winmd",
    "/r:$winmd\Windows.Devices.winmd",
    "/r:$winmd\Windows.Storage.winmd"
)

function Build($outName, [string[]]$files) {
    $out = Join-Path $here $outName
    # /codepage:65001: los .cs están en UTF-8 (sin esto, los acentos de los que no llevan BOM salen mal)
    $cscArgs = @('/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', '/codepage:65001', "/out:$out", "/win32icon:$ico") + $refs + $files
    & $csc $cscArgs
    if ($LASTEXITCODE -ne 0) { throw "Falló la compilación de $outName" }
    Write-Host "OK -> $outName" -ForegroundColor Green
}

# El ayudante se abre a través del Explorador, con un acceso directo, igual que lo abre Windows desde Inicio: así
# no queda dentro del grupo de procesos (job) del programa que lo abre. El 22-nov lo abrió así Claude Code durante
# unas pruebas, y al apagar el PC se cerró con Claude, antes de que Windows le avisara: no apagó las luces.
# El acceso va junto al .exe y no en AppData: lo que crea en AppData un programa empaquetado (MSIX, como Claude)
# Windows puede guardarlo en otro sitio, y el Explorador no lo encontraría. Si el Explorador no lo abre, lo abre el
# servicio WMI (también queda fuera del job) y, si tampoco, se abre directamente. Si ya ha aparecido uno, no se abre
# otro: uno lento de arrancar no tiene que perder contra el siguiente. Si ya hay uno en marcha no se abre otro (con
# -on se le pide que encienda).
function Start-HelperDetached([string]$Exe, [string]$Arguments, [string]$Dir) {
    $m = $null
    if ([System.Threading.Mutex]::TryOpenExisting('Local\LotusLED_Ayudante_Luces', [ref]$m)) {
        $m.Dispose()
        if ($Arguments -match '-on') {
            $e = $null
            if ([System.Threading.EventWaitHandle]::TryOpenExisting('Local\LotusLED_Ayudante_Encender', [ref]$e)) { [void]$e.Set(); $e.Dispose() }
        }
        return
    }
    $session = (Get-Process -Id $PID).SessionId
    # Si hay un ayudante (-watch) arrancando en esta sesión
    $starting = {
        @(Get-CimInstance Win32_Process -Filter "Name='LotusShutdown.exe' AND SessionId=$session" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -match '-watch' }).Count -gt 0
    }
    # Hasta 10 s a que aparezca su mutex, y 10 s más si ya está arrancando
    $waitUp = {
        for ($i = 0; $i -lt 200; $i++) {
            $mm = $null
            if ([System.Threading.Mutex]::TryOpenExisting('Local\LotusLED_Ayudante_Luces', [ref]$mm)) { $mm.Dispose(); return $true }
            if ($i -ge 100 -and -not (& $starting)) { break }
            Start-Sleep -Milliseconds 100
        }
        return (& $starting)
    }
    $name = if ($Arguments -match '-on') { 'Ayudante (encender).lnk' } else { 'Ayudante.lnk' }
    try {
        $shell = @(Get-Process -Name 'explorer' -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session })
        if ($shell.Count -gt 0) {
            $lnk = $null
            foreach ($folder in @($Dir, (Join-Path $env:LOCALAPPDATA 'LotusLED'))) {
                try {
                    New-Item -ItemType Directory -Force -Path $folder | Out-Null
                    $path = Join-Path $folder $name
                    $s = (New-Object -ComObject WScript.Shell).CreateShortcut($path)
                    $s.TargetPath = $Exe
                    $s.Arguments = $Arguments
                    $s.WorkingDirectory = $Dir
                    $s.Save()
                    if (Test-Path -LiteralPath $path) { $lnk = $path; break }
                } catch {}
            }
            if ($lnk) {
                Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList "`"$lnk`""
                if (& $waitUp) { return }
            }
        }
    } catch {}
    try {
        $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -ErrorAction Stop `
            -Arguments @{ CommandLine = "`"$Exe`" $Arguments"; CurrentDirectory = $Dir }
        if ($r.ReturnValue -eq 0 -and (& $waitUp)) { return }
    } catch {}
    if (& $starting) { return }
    Start-Process -FilePath $Exe -ArgumentList $Arguments -WorkingDirectory $Dir | Out-Null
}

# El ayudante, por separado. Si está en marcha, Windows no deja sobrescribir el .exe: se cierra y luego se vuelve a abrir.
$helper = Join-Path $here 'LotusShutdown.exe'
# Solo el de esta carpeta (el que bloquea el .exe); si estaba vigilando, luego se vuelve a abrir
$wasRunning = @(Get-Process -Name 'LotusShutdown' -ErrorAction SilentlyContinue | Where-Object {
    $p = $null
    try { $p = $_.Path } catch {}
    if (-not $p) {
        try { $p = (Get-CimInstance Win32_Process -Filter "ProcessId=$($_.Id)" -ErrorAction Stop).ExecutablePath } catch {}
    }
    $p -and $p -eq $helper
})
if ($wasRunning.Count -gt 0) {
    $wasRunning | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}
try {
    Build 'LotusShutdown.exe' @((Join-Path $srcDir 'LotusShutdown.cs'))
} finally {
    if ($wasRunning.Count -gt 0 -and -not $NoHelperRestart -and (Test-Path $helper)) {
        Start-HelperDetached $helper '-watch' $here
        Write-Host "El ayudante de las luces se ha vuelto a abrir." -ForegroundColor DarkGray
    }
}

# La app nativa alternativa: todos los .cs menos el del ayudante (cada uno tiene su propio Main)
$appFiles = Get-ChildItem "$srcDir\*.cs" | Where-Object { $_.Name -ne 'LotusShutdown.cs' } | ForEach-Object { $_.FullName }
Build 'LotusLED.exe' $appFiles

Write-Host "`nCompilación completada." -ForegroundColor Cyan
