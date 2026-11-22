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
  `AppSettings.cs` → %LOCALAPPDATA%\LotusLED\settings.cfg) y `LotusShutdown.cs`: el ayudante sin ventana
  (`LotusShutdown.exe -watch -on`, arranca con Windows) que enciende las tiras al iniciar sesión y las apaga al apagar.
- `build.ps1`: compila los dos .exe con csc.exe de .NET Framework 4 (cada uno tiene su propio Main).
- `Instalar.ps1/.bat` y `Desinstalar.ps1/.bat`: accesos directos, el ayudante en Inicio (`LotusLED Luces.lnk`)
  y la tarea programada de respaldo `LotusLED_Shutdown` (evento 1074 de USER32, `-task`).

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

## Encender y apagar con el PC sin ventana (22-nov-2026)
- El usuario tiene la ventana SIEMPRE cerrada. Quiere: al iniciar Windows, luces encendidas sin que aparezca nada
  (ni minimizado); al apagar el PC, luces apagadas.
- Por qué fallaba el apagado: (1) con la ventana abierta, el aviso SessionEnding de `LotusLED.ps1` esperaba
  /api/shutdown-ack, pero el mismo hilo atendía las peticiones (bloqueo hasta 1,4 s) y el aviso salía tarde;
  (2) con la ventana cerrada `LotusLED.ps1` termina y nadie vigila; (3) la tarea de respaldo arranca en paralelo
  sin que Windows la espere, y por defecto no corría con batería.
- Arreglo: `LotusShutdown.exe -watch` es un ayudante residente con una ventana oculta de nivel superior
  (título "LotusLED Desk", sin WS_VISIBLE; HWND_MESSAGE no serviría). Windows apaga proceso por proceso
  (QUERY → END → lo termina → el siguiente), de mayor a menor nivel. El ayudante usa nivel 0x100 (de los últimos):
  si otro programa cancela antes, ni se entera y las luces siguen encendidas, y para entonces el navegador ya se
  cerró, así que su apagado es la última orden. En WM_QUERYENDSESSION pone el motivo ("Apagando las luces LED…";
  sin motivo, a un programa sin ventana visible Windows lo mata a los 5 s), lanza el apagado en otro hilo y
  responde al momento (en un apagado forzado la pregunta solo tiene 1 s); en WM_ENDSESSION espera (máx. ~6,5 s).
  WM_ENDSESSION(FALSE) = cancelado mientras esperábamos → vuelve a encender si las luces estaban encendidas.
  Con `-on`, al arrancar enciende las tiras (insiste hasta 3 min, la tira puede tardar en arrancar).
- Sin emparejar, `FromBluetoothAddressAsync` da null si Windows no ha visto la tira hace poco (tras reiniciar):
  se escucha su anuncio unos segundos y se reintenta. Con la tira ausente, cada intento tarda 7 s y no se cancela.
- Opciones del ayudante en %LOCALAPPDATA%\LotusLED\auto.cfg (PowerOnStart, PowerOffShutdown, LightsOn), que
  escribe `LotusLED.ps1` con lo que manda la web (/api/state) y también `AppSettings.Save` del exe nativo.
- Registro: %LOCALAPPDATA%\LotusLED\LotusShutdown.log (cada arranque, encendido y apagado, con tiempos).
- Qué tiras: argumentos + `device.cfg` (junto al exe o en %LOCALAPPDATA%\LotusLED). settings.cfg (LastAddress) solo
  si no hay device.cfg, porque puede ser la tira del vecino. `Instalar` busca tiras (`-find`) si no hay ninguna.
- Windows comparte la conexión BLE entre programas: el ayudante puede escribir aunque la web tenga la tira conectada.
  No guardar conexiones abiertas entre apagados (la tira dejaría de anunciarse).
- `build.ps1` cierra el ayudante antes de compilar (si no, el .exe está bloqueado) y lo vuelve a abrir.
  Usa /codepage:65001 porque MainForm.cs, BleManager.cs y Program.cs no llevan BOM y tienen acentos.

## Pendiente
1. En el PC del usuario: `git pull`, ejecutar `Instalar.bat` (compila y deja el ayudante en marcha).
2. Probar de verdad: reiniciar y apagar, y mirar LotusShutdown.log.
3. Subir los .exe nuevos a una Release de GitHub (v1.1).

## Notas
- Sin dependencias externas: ni NuGet ni Node; solo lo que trae Windows.
- No subir nunca `device.cfg`, `settings.cfg`, `*.exe` ni `*.lnk`.
