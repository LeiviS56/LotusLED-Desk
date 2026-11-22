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
    '/r:System.Management.dll',
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

# El ayudante se abre fuera del grupo de procesos (job) del programa que lo abre. El 22-nov lo abrió Claude Code
# durante unas pruebas, quedó en el job de Claude y, al apagar el PC, se cerró con Claude antes de que Windows le
# avisara: no apagó las luces. Primero lo crea el servicio WMI (Win32_Process.Create: en la sesión y el escritorio del
# usuario y fuera de todo job, medido el 22-nov; no si se ejecuta como administrador). Si no, el Explorador, con un
# acceso directo junto al .exe, como lo abre Windows desde Inicio (no en AppData: lo que crea allí un programa
# empaquetado, MSIX, Windows puede guardarlo en otro sitio y el Explorador no lo vería). Solo al final se abre
# directamente. Si ya ha aparecido uno no se abre otro: uno lento de arrancar no tiene que perder contra el siguiente.
# Si ya hay uno en marcha no se abre otro. Con -on se le pide que encienda, también si el que queda en marcha es otro
# que se abrió a la vez.
function Start-HelperDetached([string]$Exe, [string]$Arguments, [string]$Dir) {
    $isUp = {
        $mm = $null
        if ([System.Threading.Mutex]::TryOpenExisting('Local\LotusLED_Ayudante_Luces', [ref]$mm)) { $mm.Dispose(); $true } else { $false }
    }
    $askOn = {
        if ($Arguments -match '-on') {
            $e = $null
            if ([System.Threading.EventWaitHandle]::TryOpenExisting('Local\LotusLED_Ayudante_Encender', [ref]$e)) { [void]$e.Set(); $e.Dispose() }
        }
    }
    if (& $isUp) { & $askOn; return }
    $session = (Get-Process -Id $PID).SessionId
    # Si hay un ayudante (-watch) arrancando en esta sesión (uno con -probe o -find no cuenta)
    $starting = {
        @(Get-CimInstance Win32_Process -Filter "Name='LotusShutdown.exe' AND SessionId=$session" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -match '-watch' }).Count -gt 0
    }
    # Espera a que aparezca su mutex: con el proceso abierto, mientras siga vivo, hasta 20 s; sin él (Explorador), 10 s,
    # y 10 s más si ya hay un ayudante arrancando
    $waitUp = {
        param([int]$procId)
        for ($i = 0; $i -lt 200; $i++) {
            if (& $isUp) { return $true }
            if ($procId -gt 0) {
                if (-not (Get-Process -Id $procId -ErrorAction SilentlyContinue)) { return (& $isUp) }
            } elseif ($i -ge 100 -and -not (& $starting)) { break }
            Start-Sleep -Milliseconds 100
        }
        return ((& $isUp) -or (& $starting))
    }
    $up = $false
    # Como administrador, WMI lo abriría también como administrador (y los demás programas no podrían abrir su mutex
    # ni sus avisos): entonces solo el Explorador, que lo abre como usuario normal
    $elevated = $false
    try {
        $elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch {}
    if (-not $elevated) {
        try {
            $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -ErrorAction Stop `
                -Arguments @{ CommandLine = "`"$Exe`" $Arguments"; CurrentDirectory = $Dir }
            if ($r.ReturnValue -eq 0) { $up = & $waitUp ([int]$r.ProcessId) }
        } catch {}
    }
    if (-not $up) {
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
                    $up = & $waitUp 0
                }
            }
        } catch {}
    }
    if (-not $up -and -not (& $starting)) {
        Start-Process -FilePath $Exe -ArgumentList $Arguments -WorkingDirectory $Dir | Out-Null
    }
    if ($up) { & $askOn }
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
$helperBuilt = $false
try {
    Build 'LotusShutdown.exe' @((Join-Path $srcDir 'LotusShutdown.cs'))
    $helperBuilt = $true
} finally {
    # Si no compiló, sigue el .exe de antes: se vuelve a abrir aunque Instalar pidiera no hacerlo (Instalar puede
    # acabar sin instalar nada, y lo que ya funcionaba no debe quedarse sin ayudante)
    if ($wasRunning.Count -gt 0 -and (Test-Path $helper) -and (-not $NoHelperRestart -or -not $helperBuilt)) {
        Start-HelperDetached $helper '-watch' $here
        Write-Host "El ayudante de las luces se ha vuelto a abrir." -ForegroundColor DarkGray
    }
}

# La app nativa alternativa: todos los .cs menos el del ayudante (cada uno tiene su propio Main)
$appFiles = Get-ChildItem "$srcDir\*.cs" | Where-Object { $_.Name -ne 'LotusShutdown.cs' } | ForEach-Object { $_.FullName }
Build 'LotusLED.exe' $appFiles

Write-Host "`nCompilación completada." -ForegroundColor Cyan
