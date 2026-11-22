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

# El ayudante, por separado. Si está en marcha, Windows no deja sobrescribir el .exe: se cierra y luego se vuelve a abrir.
$helper = Join-Path $here 'LotusShutdown.exe'
# Solo el de esta carpeta (el que bloquea el .exe); si estaba vigilando, luego se vuelve a abrir
$wasRunning = @(Get-Process -Name 'LotusShutdown' -ErrorAction SilentlyContinue | Where-Object {
    $p = $null
    try { $p = $_.Path } catch {}
    -not $p -or $p -eq $helper
})
if ($wasRunning.Count -gt 0) {
    $wasRunning | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}
try {
    Build 'LotusShutdown.exe' @((Join-Path $srcDir 'LotusShutdown.cs'))
} finally {
    if ($wasRunning.Count -gt 0 -and (Test-Path $helper)) {
        Start-Process -FilePath $helper -ArgumentList '-watch' -WorkingDirectory $here | Out-Null
        Write-Host "El ayudante de las luces se ha vuelto a abrir." -ForegroundColor DarkGray
    }
}

# La app nativa alternativa: todos los .cs menos el del ayudante (cada uno tiene su propio Main)
$appFiles = Get-ChildItem "$srcDir\*.cs" | Where-Object { $_.Name -ne 'LotusShutdown.cs' } | ForEach-Object { $_.FullName }
Build 'LotusLED.exe' $appFiles

Write-Host "`nCompilación completada." -ForegroundColor Cyan
