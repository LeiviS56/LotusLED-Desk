<#
  Compila LotusLED.exe (app de escritorio) y LotusShutdown.exe (el ayudante sin ventana que enciende las luces
  al iniciar Windows y las apaga al apagar el PC).
  Usa el compilador de C# que ya trae Windows (.NET Framework 4.x); no hace falta instalar nada.
  Funciona desde cualquier carpeta: todas las rutas son relativas a este script.
#>
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
# Si no se puede (sin Explorador), se abre directamente.
function Start-HelperDetached([string]$Exe, [string]$Arguments, [string]$Dir) {
    $data = Join-Path $env:LOCALAPPDATA 'LotusLED'
    $name = if ($Arguments -match '-on') { 'Ayudante (encender).lnk' } else { 'Ayudante.lnk' }
    $lnk = Join-Path $data $name
    try {
        New-Item -ItemType Directory -Force -Path $data | Out-Null
        $s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
        $s.TargetPath = $Exe
        $s.Arguments = $Arguments
        $s.WorkingDirectory = $Dir
        $s.Save()
        if (Get-Process -Name 'explorer' -ErrorAction SilentlyContinue) {
            Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList "`"$lnk`""
            # Que de verdad haya un ayudante (el nuevo, o el que ya estaba): si no aparece, se abre directamente
            for ($i = 0; $i -lt 30; $i++) {
                $m = $null
                if ([System.Threading.Mutex]::TryOpenExisting('Local\LotusLED_Ayudante_Luces', [ref]$m)) { $m.Dispose(); return }
                Start-Sleep -Milliseconds 100
            }
        }
    } catch {}
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
    if ($wasRunning.Count -gt 0 -and (Test-Path $helper)) {
        Start-HelperDetached $helper '-watch' $here
        Write-Host "El ayudante de las luces se ha vuelto a abrir." -ForegroundColor DarkGray
    }
}

# La app nativa alternativa: todos los .cs menos el del ayudante (cada uno tiene su propio Main)
$appFiles = Get-ChildItem "$srcDir\*.cs" | Where-Object { $_.Name -ne 'LotusShutdown.cs' } | ForEach-Object { $_.FullName }
Build 'LotusLED.exe' $appFiles

Write-Host "`nCompilación completada." -ForegroundColor Cyan
