<#
  Compila LotusLED.exe (app de escritorio) y LotusShutdown.exe (apaga las luces al apagar el PC).
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
    $cscArgs = @('/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', "/out:$out", "/win32icon:$ico") + $refs + $files
    & $csc $cscArgs
    if ($LASTEXITCODE -ne 0) { throw "Falló la compilación de $outName" }
    Write-Host "OK -> $outName" -ForegroundColor Green
}

# La app principal: todos los .cs menos el de apagado (cada uno tiene su propio Main)
$appFiles = Get-ChildItem "$srcDir\*.cs" | Where-Object { $_.Name -ne 'LotusShutdown.cs' } | ForEach-Object { $_.FullName }
Build 'LotusLED.exe' $appFiles

# El ayudante de apagado, por separado
Build 'LotusShutdown.exe' @((Join-Path $srcDir 'LotusShutdown.cs'))

Write-Host "`nCompilación completada." -ForegroundColor Cyan
