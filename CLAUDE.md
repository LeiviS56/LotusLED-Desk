# LotusLED Desk: contexto para Claude Code

Controlador para Windows de tiras LED Bluetooth LE que normalmente se usan con la app móvil
LotusLamp X (controladores L7179 / ELK-BLEDOM / MELK). Proyecto en C#, JavaScript y PowerShell (no Python).

## Arquitectura
- `LotusLED.ps1`: lanzador principal. Servidor HTTP local en http://localhost:8787 que sirve `web/`,
  abre Edge/Chrome en modo `--app` (con perfil propio en %LOCALAPPDATA%\LotusLED\BrowserProfile),
  icono en la bandeja y aviso de apagado (SessionEnding → /api/poll → la web apaga la tira → /api/shutdown-ack).
- `web/`: interfaz. `ble.js` (protocolo Web Bluetooth, tramas de 9 bytes 7E…EF, servicio FFF0 / característica FFF3),
  `sync.js` (modo música y ambilight), `app.js` (lógica e interfaz; guarda el estado en localStorage).
- `src/`: versión nativa alternativa en WinForms + WinRT Bluetooth (`Program.cs`, `MainForm.cs`, `BleManager.cs`,
  `AppSettings.cs` → %LOCALAPPDATA%\LotusLED\settings.cfg) y `LotusShutdown.cs` (exe aparte que apaga la tira).
- `build.ps1`: compila los dos .exe con csc.exe de .NET Framework 4 (cada uno tiene su propio Main).
- `Instalar.ps1/.bat` y `Desinstalar.ps1/.bat`: accesos directos, inicio con Windows y la tarea programada
  `LotusLED_Shutdown` (evento 1074 de USER32).

## Cambios hechos (22-oct-2026, sesión de Cowork)
- Rutas fijas `Desktop\LeiviS\LotusLED` → relativas ($PSScriptRoot). El proyecto se movió a `LeiviS ia`,
  así que los accesos directos y la tarea antiguos apuntan a una ruta que ya no existe.
- `build.ps1` arreglado: antes compilaba todos los .cs en un solo exe (dos Main → error).
- `LotusLED.ps1`: corregido el error de sintaxis en el texto del icono de la bandeja (`if` dentro de `$()`).
- Quitada la MAC fija del código. `LotusShutdown.exe` busca la MAC en: argumento → `device.cfg` junto al exe
  → settings.cfg. `device.cfg` existe en local y está en .gitignore.
- Añadidos README (para GitHub), LICENSE (MIT), .gitignore y Desinstalar. Los .ps1 se guardan en UTF-8 con BOM
  (Windows PowerShell 5.1 los necesita así para los acentos).
- Sin probar todavía: no se ha compilado ni ejecutado nada tras los cambios.

## Arreglo "no detecta las tiras" (22-oct-2026)
- La tira del usuario es una `ELK-BLEDDM` (servicio FFF0/char FFF3; su MAC está solo en `device.cfg`, que no se sube).
  Cerca del PC hay otro aparato `L7179` (FF00/FF01) que NO es suyo: antes estaba por error en `device.cfg`.
- Si hay tira guardada (LastAddress), `BleManager` solo conecta esa; sin tira guardada conecta todas las que vea.
- Causa: `BleManager.cs` no liberaba (`Dispose`) el `BluetoothLEDevice` cuando un intento fallaba. Windows dejaba la
  conexión abierta, la tira dejaba de anunciarse y nadie la veía (ni el exe, ni la web, ni el móvil). Además solo
  manejaba una tira.
- `BleManager.cs` reescrito: conecta con todas las tiras, manda cada comando a todas, libera siempre los intentos
  fallidos (también los que llegan tarde tras el tiempo de espera), el escáner sigue activo para reconectar solo.
- Compilado con `build.ps1` y probado: el exe conecta con las dos. Copia de los .exe antiguos en
  %LOCALAPPDATA%\LotusLED\backup.
- El usuario usa la versión WEB (LotusLED.ps1), no el exe ("la interfaz del exe es fea"). No tener los dos abiertos:
  el que conecta primero se queda la tira y el otro no la ve.
- Web (`ble.js`/`app.js`): ya no se rinde al reconectar; tras 3 reintentos espera al anuncio de la tira
  (`watchAdvertisements`) y reconecta sola. Si la conexión automática al abrir falla, se queda esperando la tira.
- `LotusShutdown.exe` apaga todas las MAC de `device.cfg` + settings.cfg a la vez (en paralelo, ~1 s).

## Pendiente
1. Ejecutar `Instalar.bat` para rehacer accesos directos y la tarea de apagado con la ruta nueva.
2. Ejecutar `build.ps1` y comprobar que compilan los dos .exe.
3. Probar: conectar la tira, el icono de la bandeja, el inicio automático y el apagado al apagar el PC.
4. Publicar en GitHub: `git init`, primer commit, crear el repositorio y subir. Los .exe van en Releases, no en el repo.

## Notas
- Sin dependencias externas: ni NuGet ni Node; solo lo que trae Windows.
- No subir nunca `device.cfg`, `settings.cfg`, `*.exe` ni `*.lnk`.
