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
  cerró (Chrome está en 0x280 y se mata a sí mismo en su WM_ENDSESSION), así que su apagado es la última orden.
- En WM_QUERYENDSESSION no se toca el disco: se pone el motivo ("Apagando las luces LED…"; sin motivo, a un
  programa sin ventana visible Windows lo mata a los 5 s), se lanza el apagado en otro hilo y se responde al
  momento (en un apagado forzado la pregunta solo tiene 1 s; `shutdown /s` sin `/t 0` ya es forzado). En
  WM_ENDSESSION se espera, como mucho 22,5 s contados desde la pregunta (presupuesto del apagado: 20 s; con la
  1.1.3 eran 10 s: en el PC del usuario conectar en frío tarda de 6 a 16 s). Si dura más de 5 s, Windows enseña su
  pantalla de espera (con "Apagar de todos modos") hasta que acabamos; solo pasa si la tira tarda en conectar.
- Conexión con la tira (`Keeper`, desde la 1.1.4): una por tira para todo el proceso, con cuenta de quién la usa
  (`Lights.Acquire`/`Release`, con un candado por tira para abrirla y cerrarla); dura todos los intentos de una
  orden, así que un intento que se pasa de tiempo no tira lo que Windows llevaba conectado. Lo que hace conectar a
  Windows (según Microsoft): MaintainConnection, pedir los servicios sin caché, o leer/escribir; pedir lo guardado
  (Cached) no. 1.1.5: la conexión la provocan las peticiones sin caché (Cached solo si ya está conectada) y la
  sesión con MaintainConnection solo se pide ya conectada (`Lights.Maintain`, para la conexión adelantada). En la
  1.1.4 la sesión iba desde el principio y la única petición era Cached: conectar tardó 0,08 / 1,5 / 4,0 / 8,6 /
  13,3 / 29,1 s y dos veces nada en 30 s (la 1.1.3, con peticiones sin caché, conectaba en ~5-10 s). Medido el 22-nov con la 1.1.3: conectar
  tardaba 8-10 s casi siempre (una vez más de 16 s) y la petición de servicios se cortaba a los 3 s (Cached) y a los
  6 s (Uncached); el intento siguiente conectaba en menos de 1 s. Ahora la petición de servicios espera todo lo que
  quede menos 1 s, sin cortar, salvo que Windows ya diga que está conectada (ConnectionStatus, o la sesión Active,
  que es lo que mira Chrome) y no conteste en 4 s (atascada: se pide otra). Según Microsoft, cancelar una petición
  no cancela la conexión, y cada petición sin respuesta caduca a los ~7 s (Unreachable): se vuelve a pedir. Lo mismo
  con las escrituras. Lo guardado (Cached, ya conectada) espera como mucho 4 s. Con el último Release se cierra todo
  (la tira vuelve a anunciarse); si la suelta una orden de encender cortada por el aviso de Windows, se deja 3 s
  abierta por si la recoge el apagado (no tira lo que llevaba conectado); al acabar el apagado se cierra ya. Desde la 1.1.6 los servicios que ya no hacen
  falta no se cierran al momento: los guarda el `Keeper` (`Hold`) y se cierran con la conexión, porque dos hilos
  pueden usar la tira a la vez (conexión adelantada y apagado) y Windows podría darles los mismos objetos.
- Conexión adelantada: la tarea de respaldo (evento 1074, que salta en cuanto se pide apagar) ve que el ayudante
  está en marcha y le avisa con `Local\LotusLED_Ayudante_Prepara`. Con el botón de encendido (el usuario apaga así)
  y al cerrar sesión no hay 1074: el ayudante mira `GetSystemMetrics(SM_SHUTTINGDOWN)` cada 200 ms
  (`WatchShuttingDown`) y empieza igual en cuanto Windows empieza a cerrar la sesión (el 22-nov, con el botón,
  Windows tardó 9 s en llegar a nosotros). El ayudante va conectando con las tiras mientras Windows cierra los demás
  programas (`Lights.Poke`: pide los servicios sin caché una y otra vez; para cuando empieza el apagado, que ya pide
  él), sin cambiar las luces, y ya conectadas pide MaintainConnection (se reintenta si no se pudo; la sesión se abre
  sin el candado de la tira, para no hacer esperar al apagado); el apagado usa la misma conexión. Si Windows aún no
  encuentra la tira, se insiste. Cada hilo suelta su conexión él mismo, y sus esperas se cortan (`Lights.UseAbort`)
  al soltarla o en cuanto empieza el apagado: desde ahí no pide nada a la tira (lo hace el apagado) y solo guarda la
  conexión. La suelta al acabar el apagado, si el apagado no sigue, al minuto si no empezó y Windows no está cerrando
  la sesión (otro aviso alarga el minuto), o si Windows deja de cerrar la sesión 3 s (otro programa lo canceló), y
  apunta cuándo conectó y cuántas peticiones hicieron falta. Con la 1.1.3 no conectó en 9,1 s; con la 1.1.4, a los 13,3 s.
- Opciones y MAC se leen antes (cada 5 s si cambian los archivos, o siempre si la última lectura de device.cfg
  falló) en `Snapshot`; device.cfg está en OneDrive, así
  que se guarda una copia en %LOCALAPPDATA%\LotusLED\ultimas-tiras.cfg que solo se usa si no se puede leer.
- Encender nunca llega después de apagar: el aviso de Windows activa `Lights.Abort` (corta al momento las esperas
  de las órdenes de encender) y el apagado se queda `Lights.Gate` hasta el final (lo espera hasta 4 s; si no lo
  consiguió, en cuanto la orden de encender lo suelta vuelve a apagar, una vez y aunque quede poco tiempo, las que ya
  había apagado; ya apagadas todas, lo espera hasta 0,5 s antes del final, aunque el tiempo crezca, y deja de
  esperarlo si el apagado se cancela; si nunca lo consigue, no las da por apagadas: "sin confirmar"). Lo suelta antes de apuntar el
  resultado. Si el apagado no sigue, sus esperas se cortan (`OffRound.CancelEvent`), para que no apague segundos después. El apagado
  apunta en el registro desde otro hilo, para no esperar al disco antes de la primera orden.
- ENDSESSION_CLOSEAPP con LOGOFF es un cierre de sesión, no un instalador.
- WM_ENDSESSION(FALSE), o seguir vivo 30 s después de WM_ENDSESSION(TRUE) (o 52,5 s después de la pregunta sin
  WM_ENDSESSION; ese aviso no vale si llegó WM_ENDSESSION) y que Windows ya no esté cerrando la sesión
  (SM_SHUTTINGDOWN; si aún lo está, se vuelve a mirar cada 10 s), es que el apagado no siguió: corta el apagado si
  aún iba y vuelve a encender todas las tiras que mandó apagar si LightsOn. Cada apagado lleva un número y esos avisos solo valen para el suyo; si Windows vuelve a
  preguntar antes, empieza un apagado nuevo. LightsOn lo actualizan la web (en cada encendido/apagado) y el ayudante
  (al encender al iniciar sesión). En la práctica casi nunca pasa.
- Con `-on`, al arrancar enciende las tiras (insiste hasta 3 min, con intentos de 45 s, la tira puede tardar en
  arrancar; en cada intento mira las tiras de ese momento). Volver a encender tras un apagado cancelado, también 45 s. Si ya hay un ayudante en marcha, el nuevo con `-on` se lo pide con el evento
  `Local\LotusLED_Ayudante_Encender` y termina.
- Sin `RegisterApplicationRestart`: con él, al reiniciar con "reiniciar aplicaciones" Windows manda
  ENDSESSION_CLOSEAPP y el ayudante creería que es un instalador. CLOSEAPP solo se toma por un instalador si
  `GetSystemMetrics(SM_SHUTTINGDOWN)` es 0; entonces se cierra y vigila la tarea de respaldo.
- `-task` (tarea de respaldo, evento 1074): si no hay ayudante, apaga en cuanto se pide el apagado (no se fía de
  que a un proceso nacido a mitad del apagado le llegue la pregunta) con ventana oculta, nivel 0x100 y motivo.
  No se queda el mutex del ayudante. Como empieza antes, puede insistir hasta 25 s (pero en WM_ENDSESSION retiene
  como el ayudante). Cuando llega WM_QUERYENDSESSION (o WM_ENDSESSION sin pregunta, `TaskAsked`): si aquel apagado
  acabó (o ya dejó de insistir, `OffRound.Finishing`; dejar de insistir y alargar el tiempo se deciden con
  `lock (round)`, así que no se pierde ninguno de los dos) sin llegar a todas, se corta el anterior (`Superseded`, para
  que un hilo suyo rezagado no apague después) y lo intenta otra vez con 20 s, como el ayudante (Windows ya nos espera); si aún está
  apagando, se le alarga el tiempo a 20 s desde ese momento (`OffRound.Deadline`, que SetAll mira en cada intento;
  si SetAll ya se había rendido, el apagado vuelve a intentarlo con las que faltan mientras quede tiempo).
  El aviso de que acabó un apagado anterior no quita el motivo del que está en marcha. La tarea tiene un límite de
  4 min; volver a encender se recorta para acabar 30 s antes (si no queda tiempo, o no llegó a todas, se lo pide al
  ayudante que deja con el evento `Local\LotusLED_Ayudante_Restaura`, que enciende si LightsOn aunque encender al
  iniciar esté desactivado; la tarea espera hasta 20 s a que exista el evento, que el ayudante crea antes que su
  mutex). Mientras Windows sigue cerrando la sesión, si el apagado adelantado no llegó a todas, lo vuelve a intentar
  en cada vigilancia (cada 10 s). Mientras Windows cierra la sesión no se va nunca (antes se iba en mitad de un
  apagado que Windows había retrasado ~200 s): si el apagado se cancela antes del límite de 4 min, vuelve a encender
  o se lo deja al ayudante; y poco antes del límite (a los ~200 s, `HandOff`, una vez) deja ya un ayudante con el
  aviso Restaura, que espera mientras Windows cierra la sesión, enciende si se cancela y apaga si le llega la
  pregunta. También si está esperando tras volver a la normalidad (otro apagado, sin pregunta). Si ya se lo pidió, al
  cancelarse la tarea no enciende (lo hace el ayudante) ni se lo pide otra vez. Tras WM_ENDSESSION(TRUE) la
  vigilancia siguiente llega como tarde a los ~200 s; si la pregunta llega tras volver a la normalidad, la vigilancia
  vuelve a empezar. Luego se queda: a los 75 s, si Windows no se está
  apagando (SM_SHUTTINGDOWN; si sí, vuelve a mirar cada 10 s hasta 30 s antes del límite y se va), si llega
  WM_ENDSESSION(FALSE), o si sigue viva 30 s después de WM_ENDSESSION(TRUE) (también en un segundo apagado; cada
  vigilancia lleva un número y la de los 75 s deja de valer), el apagado se canceló o es con
  espera (`/t 600`): vuelve a encender si LightsOn, deja un ayudante `-watch` en marcha (por el Explorador, ver
  abajo) y termina (`WM_APP_TASKDONE`, que no se va si entretanto empezó otro apagado, ni si es de una vuelta
  anterior: lleva número; una vuelta anterior tampoco deja ayudante). Si mientras tanto empieza
  otro apagado, vuelve a apagar. Sin nada que apagar (sin MAC) termina al momento. Antes la tarea moría a medias
  (0xC000013A el 1-nov) o ni arrancaba (15 y 21-nov).
- `LotusLED.ps1` solo deja de apagar desde la web si existe `Local\LotusLED_Ayudante_Apaga`, que el ayudante tiene
  abierto mientras haya MAC y la opción de apagar esté activada (sin MAC, la web apaga como antes).
- Escritura: la primera puede esperar a que Windows conecte (si los servicios salieron de lo guardado), así que
  tiene todo el tiempo que quede. Sin respuesta (FFF3 de la ELK-BLEDDM): dos tramas y una lectura sin caché de FFF3;
  si la lectura no vuelve o da Unreachable, se reintenta (AccessDenied lo dice Windows sin preguntar a la tira: se
  trata como si no se pudiera leer). Antes de cada trama de encender se mira Abort. Si la característica admite las dos escrituras, la
  de con respuesta tiene 2/3 del tiempo. Si un intento falla, el siguiente busca sin caché. OpenAsync
  (SharedReadAndWrite) se pide antes de GetCharacteristicsAsync, como Chrome. Las MELK reciben su saludo antes.
- Sin emparejar, `FromBluetoothAddressAsync` da null si Windows no ha visto la tira hace poco (tras reiniciar):
  se escucha su anuncio unos segundos y se reintenta ("hubo que buscarla" en el registro).
- `-probe` conecta y lee de la tira sin cambiar las luces (para comprobar la instalación), con hasta 30 s. Apunta a
  los cuántos ms conectó Windows, cada intento y lo que tardó cada fase (buscar la tira, servicios, respuesta);
  encender y apagar también apuntan sus fases y cuándo conectó ("ya estaba conectada", "desde la conexión
  adelantada"). Para comparar: `-probe -scan` escucha anuncios a la vez (con la 1.1.4 no se notó) y
  `-probe -session` pide MaintainConnection desde el principio, como la 1.1.4, pero sigue pidiendo los servicios sin
  caché hasta conectar (la 1.1.4 pedía lo guardado). Con la 1.1.5, 3 y 3 pruebas el 22-nov, todas a la primera:
  `-probe` 8,5 / 8,5 / 0,6 s, `-probe -session` 7,9 / 8,0 / 8,2 s (con la 1.1.4 fallaron 3 de 8).
- Opciones del ayudante en %LOCALAPPDATA%\LotusLED\auto.cfg (PowerOnStart, PowerOffShutdown, LightsOn), que
  escribe `LotusLED.ps1` con lo que manda la web (/api/state) y también `AppSettings.Save` del exe nativo.
- Registro: %LOCALAPPDATA%\LotusLED\LotusShutdown.log (cada arranque, encendido y apagado, con tiempos).
- Qué tiras: argumentos + `device.cfg` (junto al exe o en %LOCALAPPDATA%\LotusLED). settings.cfg (LastAddress) solo
  si no hay ningún device.cfg, porque puede ser la tira del vecino; un device.cfg sin MAC ("0 = ninguna" en
  Instalar) es ninguna. `Instalar` busca tiras (`-find`) si no hay MAC en device.cfg.
- Windows comparte la conexión BLE entre programas: el ayudante puede escribir aunque la web tenga la tira conectada.
  No guardar conexiones abiertas entre apagados (la tira dejaría de anunciarse y el móvil no la vería).
- `LotusLED.ps1`: con el ayudante en marcha y con tiras, su aviso SessionEnding no hace nada (apagar desde la web
  dejaría la tira apagada aunque luego se cancelara el apagado). Nunca abre la ventana al iniciar Windows: sin
  ayudante válido, el inicio automático no se activa. La espera larga de /api/poll se corta si llega otra petición
  (que no sea otro /api/poll).
- Versión: `LotusLED.ps1` e `Instalar.ps1` exigen LotusShutdown.exe 1.1.1 o más (la 1.0 tomaba cualquier argumento
  por una MAC y apagaba las luces; la 1.1.0 no atendía el aviso para encender). La actual es la 1.1.9. Si no
  compila, `Instalar` no instala nada.
- El ayudante se abre siempre fuera del job de quien lo abre (`Start-HelperDetached` en build.ps1, Instalar.ps1 y
  LotusLED.ps1, y `StartResidentHelper` en la tarea). Primero lo crea el servicio WMI (`Win32_Process.Create`; medido
  el 22-nov: sesión 1, WinSta0\Default y fuera de todo job), esperando su mutex mientras ese proceso siga vivo (hasta
  20 s); como administrador no se usa WMI (lo abriría como administrador y los demás no podrían abrir su mutex ni sus
  avisos). Si no, el Explorador con un acceso directo junto al .exe (`Ayudante.lnk`, o `Ayudante (encender).lnk` con
  `-on`; si no se puede guardar ahí, en %LOCALAPPDATA%\LotusLED), como lo abre Windows desde Inicio: queda en un job
  de un solo proceso, LimitFlags 0x800 (BREAKAWAY_OK, sin KILL_ON_JOB_CLOSE; probablemente el del Asistente de
  compatibilidad), que no lo cierra nadie. No en AppData: lo que crea allí una app empaquetada (MSIX) Windows puede
  guardarlo en otro sitio y el Explorador no lo vería (el PowerShell de Claude Code no tiene paquete, pero por si
  acaso). Espera 10 s al mutex, 20 s si hay un `-watch` arrancando en la sesión (por la línea de órdenes, con WMI:
  un `-probe` no cuenta). Solo al final, y si no hay ninguno arrancando, se abre directamente. Si ya hay uno no se abre
  otro; con `-on` se le manda el aviso de encender (también si el que queda es otro que se abrió a la vez). Al
  arrancar, el ayudante apunta en el registro si está en un job con más procesos o con KILL_ON_JOB_CLOSE
  (`JobWarning`; el job de un solo proceso no avisa; solo se mira el job más cercano). Si el mutex o los avisos son
  de un ayudante abierto como administrador, abrirlos da acceso denegado: se toma como que existe (antes, excepción); también en `Test-HelperArmed`, y un `-watch` nuevo lo apunta y se cierra
  (antes, "Error inesperado"). A ese ayudante no se le pueden mandar avisos (encender, Restaura, Prepara). `Instalar` llama a
  `build.ps1 -NoHelperRestart`; si no compila, `build.ps1` vuelve a abrir el de antes igualmente. Motivo: el 22-nov
  a las 05:05 el usuario apagó con el botón y el ayudante no dejó ni una línea; lo había abierto Instalar.ps1 desde
  Claude Code (Remote Control), así que estaba en el job de Claude y desapareció con Claude antes de que le llegara el
  aviso. El que abre Windows desde Inicio no está en el job de nadie.
- Mientras Windows cierra la sesión (SM_SHUTTINGDOWN) no sale ninguna orden de encender: un ayudante abierto a mitad
  de un apagado puede no recibir la pregunta y encendería detrás. Encender al iniciar (y volver a encender por el
  evento Restaura) se queda esperando, sin contar ese rato, y sigue si el apagado se cancela; solo lo termina la
  pregunta de Windows (`_ending`); un intento cortado así no cuenta. Las peticiones de encender se juntan
  (`_askLogon`/`_askRestore`): la que está en marcha suma la que llega (en la 1.1.8 volvía a empezar solo con la
  nueva: se perdía la otra). Con Restaura vuelve a encender todas (un apagado cancelado pudo apagarlas); otra de
  iniciar sesión (el aviso que manda `Start-HelperDetached` con -on al ayudante que acaba de abrir) no repite las ya
  encendidas. La tarea tampoco vuelve a encender si Windows está cerrando la sesión otra vez
  (espera 5 s por si aún no bajó el aviso del apagado cancelado): se lo deja al ayudante (Restaura) y se queda a
  esperar la pregunta: no deja el ayudante ni se va (`OnTaskDone`) mientras SM_SHUTTINGDOWN.
- `build.ps1` cierra solo el ayudante de esta carpeta (misma ruta del .exe) antes de compilar (si no, el .exe está
  bloqueado) y lo vuelve a abrir. Usa /codepage:65001 porque MainForm.cs, BleManager.cs y Program.cs no llevan BOM
  y tienen acentos.
- PC del usuario (22-nov): Inicio rápido activado (apagar = cerrar sesiones e hibernar el núcleo: Kernel-Power 187
  con SystemAction=3 y 42 con TargetState=6; al encender, Power-Troubleshooter 1), el botón de encendido apaga, y
  nunca se suspende ni hiberna solo.
- Instalado en el PC del usuario el 22-nov (rama local suya con el rediseño estilo Apple, sin subir). Con la 1.1.2,
  5 pruebas `-probe`: 4,5 s (hubo que buscarla), 10,0 s, 0,3 s, 2,3 s; siempre "leer dio Success". Con la 1.1.3:
  8,1 s, 10,4 s, 9,9 s, no llegó en 16 s, y 6,2 s (casi todo en la fase de servicios, es decir, conectando).

## Pendiente
1. Probar de verdad: apagar y encender el PC, y mirar LotusShutdown.log (tiempos del apagado, "hubo que buscarla").
   Encender al iniciar sesión ya funcionó de verdad el 22-nov (2,7 s). El primer apagado de verdad (05:05) no valió:
   el ayudante estaba dentro del job de Claude (ver arriba).
2. Subir los .exe nuevos a una Release de GitHub (v1.1). La Release v1.0 tiene el LotusShutdown.exe antiguo.
3. No cubierto: suspender (Windows avisa con PBT_APMSUSPEND pero no espera más de ~2 s, y conectar tarda ~8 s),
   hibernar, apagar con el botón si está puesto en "suspender", cortes de luz.

## Notas
- Sin dependencias externas: ni NuGet ni Node; solo lo que trae Windows.
- No subir nunca `device.cfg`, `settings.cfg`, `*.exe` ni `*.lnk`.
