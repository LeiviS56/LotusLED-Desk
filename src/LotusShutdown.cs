using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Windows.Foundation;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

// LotusShutdown.exe: el ayudante sin ventana de LotusLED Desk.
//
//   LotusShutdown.exe -watch -on   Se queda en segundo plano, sin ventana ni icono (lo arranca Windows al iniciar
//                                  sesión). Enciende las tiras al empezar y las apaga al apagar, reiniciar o cerrar
//                                  sesión: retiene el apagado de Windows unos segundos hasta que la orden llega.
//                                  Si ya hay uno en marcha, le pide que encienda y termina.
//   LotusShutdown.exe -watch       Igual, pero sin encender las luces al empezar (lo usan LotusLED.ps1 e Instalar).
//   LotusShutdown.exe              Apaga las tiras ahora y termina.
//   LotusShutdown.exe -on          Las enciende ahora y termina.
//   LotusShutdown.exe -task        Lo usa la tarea programada de respaldo al pedirse un apagado: si el ayudante ya
//                                  vigila no hace nada; si no, apaga las tiras reteniendo el apagado como él.
//   LotusShutdown.exe -probe       Comprueba que llega a las tiras (conecta, busca dónde escribir y espera respuesta
//                                  de la tira) sin cambiar las luces, y apunta en el registro cuánto tardó cada fase.
//   LotusShutdown.exe -probe -scan Igual, pero escuchando anuncios a la vez (para ver si así Windows conecta antes).
//   LotusShutdown.exe -probe -session  Igual, pero pidiendo MaintainConnection desde el principio (como la 1.1.4).
//   LotusShutdown.exe -find <archivo> [segundos]
//                                  Busca tiras cercanas y guarda una por línea "MAC|nombre|señal" (lo usa Instalar).
//
// Qué tiras: las MAC que se pasen como argumento y las de device.cfg (junto al .exe o en %LOCALAPPDATA%\LotusLED);
// solo si no hay ningún device.cfg, la última que usó LotusLED.exe (settings.cfg).
// Lo que hace queda apuntado en %LOCALAPPDATA%\LotusLED\LotusShutdown.log.
// Ojo: el compilador que trae Windows (csc de .NET Framework 4) solo admite C# 5.

// El nombre que enseña Windows (Administrador de tareas y la pantalla de "programas que impiden apagar").
// LotusLED.ps1 e Instalar.ps1 exigen la 1.1.1 o más: la 1.0 apagaba las luces con cualquier argumento, y la 1.1.0 no
// atendía el aviso para encender de un segundo "-watch -on".
[assembly: AssemblyTitle("LotusLED Desk - luces")]
[assembly: AssemblyDescription("Enciende las tiras LED al iniciar Windows y las apaga al apagar el PC")]
[assembly: AssemblyProduct("LotusLED Desk")]
[assembly: AssemblyVersion("1.1.7.0")]
[assembly: AssemblyFileVersion("1.1.7.0")]

namespace LotusLED.Shutdown
{
    static class Helper
    {
        const string MutexName = "Local\\LotusLED_Ayudante_Luces";
        const string PowerOnEventName = "Local\\LotusLED_Ayudante_Encender";
        // Existe solo mientras el ayudante va a apagar alguna tira al apagar el PC (hay MAC y la opción está
        // activada). LotusLED.ps1 lo mira: si no existe, apaga la ventana web, como antes de haber ayudante.
        const string ArmedName = "Local\\LotusLED_Ayudante_Apaga";
        // La tarea de respaldo (evento 1074: se acaba de pedir apagar o reiniciar) se lo dice al ayudante, que se
        // adelanta y va conectando con las tiras mientras Windows cierra los demás programas.
        const string PrepareEventName = "Local\\LotusLED_Ayudante_Prepara";
        // La tarea de respaldo, si un apagado se canceló y a ella ya no le da tiempo, le pide al ayudante que deja en
        // marcha que vuelva a encender las tiras (si estaban encendidas: LightsOn, no la opción de encender al iniciar).
        const string RestoreEventName = "Local\\LotusLED_Ayudante_Restaura";

        [STAThread]
        static int Main(string[] args)
        {
            bool watch = false, on = false, task = false, probe = false, scan = false, session = false;
            string findFile = null;
            int findSeconds = 8;
            var macs = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].Trim();
                string k = a.TrimStart('-', '/').ToLowerInvariant();
                if (k == "watch" || k == "vigilar") watch = true;
                else if (k == "on" || k == "encender") on = true;
                else if (k == "off" || k == "apagar") on = false;
                else if (k == "task" || k == "tarea") task = true;
                else if (k == "probe" || k == "probar") probe = true;
                else if (k == "scan" || k == "escanear") scan = true;
                else if (k == "session" || k == "sesion" || k == "sesión") session = true;
                else if (k == "find" || k == "buscar")
                {
                    findFile = (i + 1 < args.Length) ? args[++i] : Path.Combine(Config.DataDir, "tiras-encontradas.txt");
                    int s;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out s)) { findSeconds = s; i++; }
                }
                else if (a.Length > 0) macs.Add(a);
            }

            try
            {
                if (findFile != null) return Finder.Run(findFile, findSeconds);
                if (probe) return Lights.Probe(Config.Addresses(macs), scan, session);
                if (watch) return Guard.Run(macs, on ? GuardMode.PowerOn : GuardMode.Watch);
                if (task) return RunTask(macs);
                return RunOnce(macs, on);
            }
            catch (Exception ex)
            {
                Log.Write("Error inesperado: " + ex);
                return 1;
            }
        }

        /// <summary>
        /// Tarea de respaldo (evento 1074: alguien pidió apagar o reiniciar). Si el ayudante está en marcha, él se
        /// encarga: solo se le avisa para que vaya conectando. Si no, este proceso hace lo mismo que él: ventana
        /// oculta, nivel de apagado bajo y motivo de bloqueo, para que Windows no lo cierre a medias (antes moría con
        /// 0xC000013A sin llegar a apagar).
        /// </summary>
        static int RunTask(List<string> macs)
        {
            Mutex m;
            if (Mutex.TryOpenExisting(MutexName, out m))
            {
                m.Dispose();
                try
                {
                    EventWaitHandle prepare;
                    if (EventWaitHandle.TryOpenExisting(PrepareEventName, out prepare)) { prepare.Set(); prepare.Dispose(); }
                }
                catch { }
                return 0;
            }
            if (!Config.Flag("PowerOffShutdown", true)) return 0;
            return Guard.Run(macs, GuardMode.OffNow);
        }

        /// <summary>Encender o apagar ahora, una vez, y terminar.</summary>
        static int RunOnce(List<string> macs, bool on)
        {
            var addrs = Config.Addresses(macs);
            if (addrs.Count == 0)
            {
                Log.Write("No sé qué tiras " + (on ? "encender" : "apagar") + ": no hay ninguna MAC en device.cfg.");
                return 2;
            }
            var res = Lights.SetAll(addrs, on, 30000, null, false);
            Log.Results(on ? "Encender" : "Apagar", res);
            return Lights.CountOk(res) > 0 ? 0 : 3;
        }

        internal static string MutexNameForGuard { get { return MutexName; } }
        internal static string PowerOnEventNameForGuard { get { return PowerOnEventName; } }
        internal static string ArmedNameForGuard { get { return ArmedName; } }
        internal static string PrepareEventNameForGuard { get { return PrepareEventName; } }
        internal static string RestoreEventNameForGuard { get { return RestoreEventName; } }
    }

    enum GuardMode { Watch, PowerOn, OffNow }

    // ========================================================================================================
    // Ayudante residente: ventana oculta que recibe los avisos de apagado de Windows
    // ========================================================================================================
    //
    // Cómo apaga Windows (Microsoft, "Application Shutdown Changes in Windows Vista" y "Logging Off"): va proceso
    // por proceso, de mayor a menor nivel de apagado. A cada uno le manda WM_QUERYENDSESSION, luego WM_ENDSESSION
    // y lo termina; después pasa al siguiente. Un programa sin ventanas visibles que tarde más de 5 s lo mata,
    // salvo que haya puesto un motivo con ShutdownBlockReasonCreate: entonces espera (y a los 5 s enseña la
    // pantalla "Apagando las luces LED…"). En un apagado forzado la pregunta solo tiene 1 s.
    //
    // Por eso: nivel bajo (nos preguntan los últimos, cuando el resto de programas, el navegador incluido, ya
    // aceptaron y se cerraron; si alguno cancela antes, ni nos enteramos y las luces siguen encendidas). En la
    // pregunta no se toca el disco: se pone el motivo, se lanza el apagado en otro hilo y se responde al momento.
    // En WM_ENDSESSION se espera a que termine, con un tope contado desde la pregunta.
    static class Guard
    {
        const uint ShutdownLevel = 0x100;   // "aplicaciones, al final" (por defecto todos tienen 0x280)
        // Con la tira ya conectada es menos de 1 s, pero en el PC del usuario Windows tarda de 6 a 16 s en conectar en
        // frío (medido el 22-nov). La conexión se empieza antes (al pedirse el apagado, ver Prepare), pero si no hubo
        // aviso empieza aquí. Con el motivo puesto, Windows espera (a partir de 5 s enseña su pantalla "Apagando las
        // luces LED…", con un botón para apagar sin esperar); solo dura tanto si la tira tarda.
        const int OffBudgetMs = 20000;
        // Lo máximo que retenemos el apagado, desde la pregunta: el apagado más lo que tarda en apuntarlo
        const int EndWaitMs = OffBudgetMs + 2500;
        // La tarea de respaldo empieza antes (al pedirse el apagado, mientras Windows cierra los demás programas),
        // así que puede insistir más; en WM_ENDSESSION retiene lo mismo que el ayudante (EndWaitMs como mucho).
        const int TaskBudgetMs = 25000;
        // La tarea apaga en cuanto se pide el apagado, pero se puede cancelar después (o ser un apagado con espera,
        // "shutdown /t 600"). Si pasado este rato Windows no nos ha cerrado ni se está apagando, vuelve a encender y
        // deja el ayudante en marcha. Por debajo del límite de 4 min de la tarea, contando lo que tarda en encender.
        const int TaskStayMs = 75000;
        const int TaskRecheckMs = 10000;    // si a los 75 s Windows aún se está apagando, se vuelve a mirar (2 veces)
        const int StartWindowMs = 180000;   // al iniciar sesión, cuánto tiempo seguimos intentando encender
        const int PowerOnTryMs = 45000;     // cada intento de encender al iniciar sesión
        const int ResumeCheckMs = 30000;    // si seguimos vivos tanto después de WM_ENDSESSION, el apagado se canceló
        // La conexión adelantada se suelta si en este rato no ha empezado el apagado (alguien lo canceló antes de llegar
        // a nosotros, o es con espera)
        const int PrepareHoldMs = 60000;
        // Límite de la tarea programada (Instalar.ps1: ExecutionTimeLimit de 4 min); volver a encender acaba antes
        const int TaskLimitMs = 240000;
        const int TaskMarginMs = 30000;    // volver a encender, dejar el ayudante (hasta 20 s) y salir

        public const int WM_APP_OFFDONE = 0x8001;
        public const int WM_APP_RESUMECHECK = 0x8002;
        public const int WM_APP_TASKCHECK = 0x8003;
        public const int WM_APP_QUIT = 0x8004;
        public const int WM_APP_TASKDONE = 0x8005;

        static Mutex _mutex;
        static EventWaitHandle _powerOnEvent;
        static EventWaitHandle _restoreEvent;
        static GuardWindow _window;
        static GuardMode _mode;
        static List<string> _macs;
        static volatile Snapshot _cfg;          // opciones y tiras, leídas antes: en el apagado no se toca el disco
        static volatile bool _ending;           // Windows ha avisado de que se termina la sesión
        static volatile OffRound _round;        // el apagado en curso (solo se cambia desde el hilo de la ventana)
        static int _roundCount;                 // para saber de qué apagado habla un aviso que llega tarde
        static bool _reason;                    // hay motivo de bloqueo puesto
        static int _powerOnRunning;             // 1 mientras se intenta encender
        static string _stamp;                   // huella de los archivos de configuración de la última lectura
        static EventWaitHandle _armed;          // abierto mientras hay tiras que apagar (lo mira LotusLED.ps1)
        static volatile int _exitCode;          // de la tarea de respaldo: 0 si apagó todas (o no tenía que apagar)
        static bool _taskResumed;               // tarea de respaldo: el apagado no siguió y ya se volvió a encender
        static int _taskCheckGen;               // tarea de respaldo: qué vigilancia vale (los avisos de otra se ignoran)
        static int _resumeGen;                  // tarea de respaldo: qué vuelta a la normalidad vale para irse
        static readonly Stopwatch _age = Stopwatch.StartNew();  // desde que arrancó el proceso (la tarea tiene 4 min)
        static EventWaitHandle _prepareEvent;   // la tarea de respaldo avisa de que se pidió apagar (ir conectando)
        static volatile bool _prepareBySystem;  // el aviso para ir conectando es porque Windows empezó a cerrar la sesión

        /// <summary>Un apagado: cuándo empezó (la pregunta de Windows), cuándo acabó y cómo fue.</summary>
        class OffRound
        {
            public int Id;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            public readonly Stopwatch Clock = Stopwatch.StartNew();
            public List<ulong> Addrs = new List<ulong>();   // las tiras que se mandaron apagar
            public bool EndSeen;                            // ya llegó WM_ENDSESSION(TRUE) de este apagado
            public volatile bool Cancelled;                 // el apagado no siguió: se deja de insistir
            public volatile Dictionary<ulong, StripResult> Result;
            public long Deadline;                           // hasta cuándo insiste, en ms desde que empezó (puede crecer)
            public bool Early;                              // lo empezó la tarea al pedirse el apagado, antes de la pregunta
            public bool QuerySeen;                          // tarea: ya se atendió la pregunta (o el fin de sesión) de Windows
            public bool SlowNoted;                          // ya se apuntó que Windows tarda en cerrar la sesión
            public volatile bool Finishing;                 // ya no insiste más (Result puesto), aunque Done aún no esté
            public volatile bool Superseded;                // la tarea empezó otro apagado en su lugar
            public readonly ManualResetEvent CancelEvent = new ManualResetEvent(false);  // se activa con Cancelled
            public void Cancel() { Cancelled = true; try { CancelEvent.Set(); } catch { } }
        }

        public static int Run(List<string> macs, GuardMode mode)
        {
            // La tarea de respaldo no se queda con el mutex del ayudante: si a la vez arranca un "-watch -on" (se
            // apaga justo al iniciar sesión y luego se cancela), ese tiene que poder quedarse vigilando.
            if (mode != GuardMode.OffNow)
            {
                // Los avisos para encender y para ir conectando existen antes que el mutex: quien vea el mutex ya
                // puede usarlos
                _powerOnEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Helper.PowerOnEventNameForGuard);
                _prepareEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Helper.PrepareEventNameForGuard);
                _restoreEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Helper.RestoreEventNameForGuard);
                bool created;
                _mutex = new Mutex(true, Helper.MutexNameForGuard, out created);
                if (!created)
                {
                    // Ya hay un ayudante vigilando en esta sesión (por ejemplo, lo abrió la interfaz o Instalar).
                    // Si a este lo arrancó Windows para encender, se lo pedimos a él.
                    if (mode == GuardMode.PowerOn) { try { _powerOnEvent.Set(); } catch { } }
                    return 0;
                }
            }

            _mode = mode;
            _macs = macs;
            try { Native.SetProcessShutdownParameters(ShutdownLevel, 0); } catch { }
            // Sin RegisterApplicationRestart: con él, al reiniciar con "reiniciar aplicaciones" Windows nos manda el
            // aviso como si fuera un instalador (ENDSESSION_CLOSEAPP) y no apagaríamos. Si un instalador nos cierra,
            // vigila la tarea de respaldo hasta el próximo inicio de sesión.
            _window = new GuardWindow();
            _stamp = Config.Stamp(); // antes de leer: un cambio a mitad de la lectura se vuelve a leer luego
            _cfg = Config.Load(macs, null);

            if (mode == GuardMode.OffNow)
            {
                Log.Write("Tarea de respaldo: el ayudante no estaba en marcha, apago desde aquí.");
                StartOff(_window.Handle, "Se pide apagar o reiniciar", true);
                ScheduleTaskCheck(_window.Handle, ++_taskCheckGen, TaskStayMs);
            }
            else
            {
                UpdateArmed(_cfg);
                Log.Write("Ayudante en marcha (" + Config.ExeDir + "). Tiras: " + Log.List(_cfg.Addrs) +
                          (_cfg.Addrs.Count == 0 ? "  <- falta la MAC en device.cfg" : "") +
                          JobWarning());
                StartThread(() => { if (mode == GuardMode.PowerOn) PowerOnAtStart(); else Lights.Warmup(); });
                StartThread(Refresher);
                StartThread(() =>
                {
                    while (true)
                    {
                        _powerOnEvent.WaitOne();
                        if (!_ending) PowerOnAtStart();
                    }
                });
                StartThread(() =>
                {
                    while (true)
                    {
                        _restoreEvent.WaitOne();
                        if (!_ending) PowerOnAtStart(true);
                    }
                });
                StartThread(() =>
                {
                    while (true)
                    {
                        _prepareEvent.WaitOne();
                        Prepare();
                    }
                });
                StartThread(WatchShuttingDown);
            }

            Application.Run();
            GC.KeepAlive(_mutex);
            GC.KeepAlive(_powerOnEvent);
            GC.KeepAlive(_prepareEvent);
            GC.KeepAlive(_restoreEvent);
            return mode == GuardMode.OffNow ? _exitCode : 0;
        }

        static void StartThread(ThreadStart body)
        {
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { Log.Write("Error en segundo plano: " + ex.Message); }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        /// <summary>Vuelve a leer las opciones y las tiras cuando cambian (para tenerlas listas al apagar).</summary>
        static void Refresher()
        {
            string stamp = _stamp;
            while (true)
            {
                Thread.Sleep(5000);
                if (_ending) continue;
                string now = Config.Stamp();
                var cur = _cfg;
                // Si la última vez no se pudo leer device.cfg (OneDrive aún sin arrancar), se insiste aunque no cambie
                if (now == stamp && (cur == null || !cur.ReadError)) continue;
                stamp = now;
                var old = _cfg;
                var cfg = Config.Load(_macs, old);
                _cfg = cfg;
                UpdateArmed(cfg);
                if (old == null || Log.List(old.Addrs) != Log.List(cfg.Addrs))
                    Log.Write("Tiras: " + Log.List(cfg.Addrs) + (cfg.Addrs.Count == 0 ? "  <- falta la MAC en device.cfg" : ""));
            }
        }

        /// <summary>Que LotusLED.ps1 sepa si de verdad vamos a apagar algo (si no, que apague la ventana web).</summary>
        static void UpdateArmed(Snapshot cfg)
        {
            bool want = cfg != null && cfg.PowerOff && cfg.Addrs.Count > 0;
            try
            {
                if (want && _armed == null)
                    _armed = new EventWaitHandle(false, EventResetMode.ManualReset, Helper.ArmedNameForGuard);
                else if (!want && _armed != null)
                {
                    _armed.Dispose();
                    _armed = null;
                }
            }
            catch { }
        }

        /// <summary>
        /// Al iniciar sesión: encender las tiras. La tira puede tardar en arrancar, así que se insiste un rato. En cada
        /// intento se miran las tiras y las opciones de ese momento (pueden cambiar mientras tanto, o faltar la MAC
        /// al principio); un aviso para encender que llegue a mitad se suma a este intento.
        /// </summary>
        /// <param name="restore">Lo pide la tarea tras un apagado cancelado: se encienden si estaban encendidas (LightsOn).</param>
        static void PowerOnAtStart(bool restore = false)
        {
            if (Interlocked.Exchange(ref _powerOnRunning, 1) == 1) return; // ya se está intentando (con lo de ahora)
            try
            {
                var clock = Stopwatch.StartNew();
                var done = new List<ulong>();
                var pending = new List<ulong>();
                // Mientras Windows cierra la sesión no sale ninguna orden de encender (un ayudante abierto a mitad de un
                // apagado puede no recibir la pregunta, y encendería detrás del apagado): se espera, sin contar ese rato.
                // Si el apagado sigue, termina (la pregunta, _ending, o Windows nos cierra); si se cancela, sigue.
                Func<bool> stop = () => _ending || ShuttingDown();
                bool loggedNoMac = false, loggedPause = false;
                int tries = 0;
                long paused = 0;
                while (!_ending && clock.ElapsedMilliseconds - paused < StartWindowMs)
                {
                    if (ShuttingDown())
                    {
                        if (!loggedPause) { Log.Write("Encender: espero, Windows está cerrando la sesión."); loggedPause = true; }
                        var wait = Stopwatch.StartNew();
                        while (ShuttingDown() && !_ending) Thread.Sleep(500);
                        paused += wait.ElapsedMilliseconds;
                        continue;
                    }
                    var cfg = _cfg;
                    if (cfg == null || !(restore ? cfg.LightsOn : cfg.PowerOnStart))
                    {
                        if (done.Count == 0)
                            Log.Write(restore ? "Volver a encender: las luces estaban apagadas." : "Encender al iniciar está desactivado en la interfaz.");
                        return;
                    }
                    pending.Clear();
                    foreach (var a in cfg.Addrs) if (!done.Contains(a)) pending.Add(a);
                    if (pending.Count == 0)
                    {
                        if (done.Count > 0) break; // todas encendidas
                        // Todavía no hay ninguna MAC (device.cfg sin leer o vacío): se espera a que aparezca
                        if (!loggedNoMac) { Log.Write("Encender al iniciar: espero a que haya alguna MAC en device.cfg."); loggedNoMac = true; }
                        for (int i = 0; i < 50 && !_ending; i++) Thread.Sleep(100);
                        continue;
                    }
                    tries++;
                    // Cada intento con tiempo de sobra: conectar puede tardar, y cortar a medias obliga a empezar de cero
                    var res = Lights.SetAll(pending, true, PowerOnTryMs, stop, true);
                    if (_ending) break; // Windows se apaga: lo que pase con estas órdenes ya no cuenta
                    bool first = done.Count == 0;
                    foreach (var r in res.Values)
                        if (r.Ok) { done.Add(r.Address); pending.Remove(r.Address); }
                    // En cuanto se enciende la primera (no al final: la web puede apagarlas mientras se insiste con otra)
                    if (first && done.Count > 0) NoteLightsOn();
                    if (tries == 1 || pending.Count == 0)
                        Log.Results((restore ? "Volver a encender" : "Encender al iniciar") + " (intento " + tries + ")", res);
                    if (pending.Count == 0) continue; // por si entretanto apareció otra tira; si no, termina arriba
                    for (int i = 0; i < 50 && !_ending; i++) Thread.Sleep(100);
                }
                if (pending.Count > 0 && !_ending)
                    Log.Write("No se pudo encender " + Log.List(pending) + " en " + (StartWindowMs / 1000) +
                              " s. ¿Está enchufada, o conectada al móvil?");
            }
            finally { Interlocked.Exchange(ref _powerOnRunning, 0); }
        }

        /// <summary>
        /// Las luces quedaron encendidas: se apunta (en memoria y en auto.cfg) para que, si luego se cancela un
        /// apagado, se vuelvan a encender aunque la última vez que se usó la ventana estuvieran apagadas.
        /// </summary>
        static void NoteLightsOn()
        {
            var cfg = _cfg;
            if (cfg != null) cfg.LightsOn = true;
            Config.SetFlag("LightsOn", true);
        }

        /// <summary>
        /// Con el botón de encendido del PC (y al cerrar sesión) no hay evento 1074, así que la tarea de respaldo no
        /// avisa: el 22-nov, con el botón, Windows tardó 9 s en cerrar los demás programas antes de llegar a nosotros, y
        /// la tira tarda unos 8 s en conectar. Windows sí marca la sesión como "cerrándose" (SM_SHUTTINGDOWN) en cuanto
        /// empieza: al verlo, se empieza a conectar como con el aviso de la tarea (sin cambiar las luces).
        /// </summary>
        static void WatchShuttingDown()
        {
            bool was = false;
            while (true)
            {
                bool now = ShuttingDown();
                if (now && !was && _round == null && !_ending)
                {
                    var cfg = _cfg;
                    if (cfg != null && cfg.PowerOff && cfg.Addrs.Count > 0)
                    {
                        _prepareBySystem = true;
                        try { _prepareEvent.Set(); } catch { }
                    }
                }
                was = now;
                Thread.Sleep(now ? 1000 : 200);
            }
        }

        static bool ShuttingDown()
        {
            try { return Native.GetSystemMetrics(Native.SM_SHUTTINGDOWN) != 0; } catch { return false; }
        }

        /// <summary>
        /// Se acaba de pedir apagar o reiniciar (aviso de la tarea de respaldo, evento 1074, o Windows empieza a cerrar
        /// la sesión, ver WatchShuttingDown): mientras Windows cierra
        /// los demás programas, se va conectando con las tiras (pidiéndoles los servicios, una y otra vez, hasta que
        /// conecten) y, ya conectadas, se pide a Windows que no suelte la conexión, para que cuando nos toque a
        /// nosotros (los últimos) la orden salga al momento; el apagado usa la misma conexión. No cambia las luces. Se
        /// suelta al acabar el apagado, si el apagado no sigue, o al minuto si no ha empezado (se canceló antes de
        /// llegar a nosotros, o es con espera; otro aviso de la tarea alarga el minuto): con la conexión abierta, la
        /// tira no se anuncia.
        /// </summary>
        static void Prepare()
        {
            var cfg = _cfg;
            if (_ending || cfg == null || !cfg.PowerOff || cfg.Addrs.Count == 0) return;
            var addrs = new List<ulong>(cfg.Addrs);
            bool bySystem = _prepareBySystem;
            _prepareBySystem = false;
            string start = (bySystem ? "Windows empieza a cerrar la sesión (apagar, reiniciar o cerrar sesión)" : "Se pide apagar o reiniciar") +
                           ": voy conectando con " + Log.List(addrs) + " para que el apagado sea rápido.";
            StartThread(() => Log.Write(start));
            var clock = Stopwatch.StartNew();
            long holdUntil = PrepareHoldMs;     // se alarga si la tarea vuelve a avisar
            var released = new ManualResetEvent(false);
            // Se activa al soltarla o en cuanto empieza el apagado: desde ahí este hilo no pide nada a la tira (el
            // apagado lo hace él mismo, y no tiene que esperar a que acabe una petición nuestra)
            var stop = new ManualResetEvent(false);
            var threads = new List<Thread>();
            foreach (var addr in addrs)
            {
                ulong a = addr;
                // Cada hilo abre la conexión y la suelta él mismo: así nunca se cierra con una petición suya en marcha
                var t = new Thread(() =>
                {
                    Keeper k = null;
                    try
                    {
                        // Al soltar la conexión, o al empezar el apagado, se corta lo que esté esperando este hilo
                        // (buscarla, pedir servicios, la sesión): ninguna petición nuestra sigue en marcha
                        Lights.UseAbort(stop);
                        // Si Windows aún no la encuentra (sin emparejar, tras reiniciar), se insiste: puede aparecer
                        bool noted = false;
                        while (k == null && !stop.WaitOne(0))
                        {
                            string err;
                            k = Lights.Acquire(a, "adelantada", Stopwatch.StartNew(), 15000, out err);
                            if (k != null || stop.WaitOne(0)) break;
                            if (!noted) { noted = true; Log.Write("Conexión adelantada con " + Log.Short(a) + ": " + err + " (sigo intentándolo)."); }
                            WaitHandle.WaitAny(new WaitHandle[] { stop }, 1000);
                        }
                        if (k == null) return;
                        long connectedAt = -1;
                        int pokes = 0;
                        bool sessionNoted = false;
                        while (!released.WaitOne(0))
                        {
                            if (stop.WaitOne(0))
                            {
                                // Ya apaga el apagado: se guarda la conexión (que no la suelte Windows) hasta que acabe
                                if (connectedAt < 0 && k.IsConnected) connectedAt = clock.ElapsedMilliseconds;
                                WaitHandle.WaitAny(new WaitHandle[] { released }, 250);
                            }
                            else if (k.IsConnected)
                            {
                                if (connectedAt < 0) connectedAt = clock.ElapsedMilliseconds;
                                // Ya conectada: que Windows no la suelte hasta el apagado. Si no se pudo abrir la
                                // sesión, se vuelve a probar (Maintain no hace nada si ya la hay).
                                if (k.Session == null) Lights.Maintain(k, 3000);
                                if (!sessionNoted)
                                {
                                    sessionNoted = true;
                                    Log.Write("Conexión adelantada con " + Log.Short(a) + ": conectada en " + connectedAt + " ms (" +
                                              pokes + (pokes == 1 ? " petición" : " peticiones") + (k.Scanned ? ", hubo que buscarla" : "") +
                                              (k.Session == null ? ", sin sesión todavía" : "") + ").");
                                }
                                WaitHandle.WaitAny(new WaitHandle[] { stop }, k.Session == null ? 1000 : 250);
                            }
                            else
                            {
                                pokes++;
                                if (!Lights.Poke(k, 10000)) WaitHandle.WaitAny(new WaitHandle[] { stop }, 200);
                            }
                        }
                        if (connectedAt < 0 && !k.IsConnected)
                            Log.Write("Conexión adelantada con " + Log.Short(a) + ": sin conectar en " + clock.ElapsedMilliseconds + " ms (" +
                                      pokes + (pokes == 1 ? " petición" : " peticiones") + ").");
                    }
                    catch (Exception ex) { Log.Write("Conexión adelantada con " + Log.Short(a) + ": " + ex.Message); }
                    finally { Lights.Release(k); }
                });
                t.IsBackground = true;
                t.SetApartmentState(ApartmentState.MTA);
                t.Start();
                threads.Add(t);
            }
            OffRound seen = null;
            string why;
            long shuttingAt = -1;   // la última vez que Windows decía estar cerrando la sesión
            while (true)
            {
                var round = _round;
                bool shutting = ShuttingDown();
                if (shutting) shuttingAt = clock.ElapsedMilliseconds;
                if (round != null)
                {
                    if (seen == null) { try { stop.Set(); } catch { } }
                    seen = round;
                    if (Native.Wait(round.Done, 0)) { why = "acabó el apagado"; break; }
                }
                else if (seen != null) { why = "el apagado no siguió"; break; }
                // Al minuto, salvo que Windows siga cerrando la sesión (otro programa le hace esperar): llegará a nosotros
                else if (clock.ElapsedMilliseconds > holdUntil && !shutting) { why = "no empezó el apagado"; break; }
                // Windows empezó a cerrar la sesión y lo dejó (otro programa lo canceló) antes de llegar a nosotros
                else if (shuttingAt >= 0 && clock.ElapsedMilliseconds - shuttingAt > 3000) { why = "Windows dejó de cerrar la sesión"; break; }
                // Otro aviso (se volvió a pedir apagar): un minuto más desde ahora
                if (_prepareEvent != null && _prepareEvent.WaitOne(0))
                {
                    holdUntil = clock.ElapsedMilliseconds + PrepareHoldMs;
                    _prepareBySystem = false;
                }
                Thread.Sleep(200);
            }
            try { stop.Set(); } catch { }
            try { released.Set(); } catch { }
            // Los hilos sueltan su conexión enseguida (sus esperas se cortan con released)
            foreach (var t in threads) t.Join(3000);
            Log.Write("Conexión adelantada: la suelto a los " + clock.ElapsedMilliseconds + " ms (" + why + ").");
        }

        /// <summary>A partir de aquí no sale ninguna orden de encender, y las que estén a medias se cortan.</summary>
        static void BeginEnding()
        {
            _ending = true;
            try { Lights.Abort.Set(); } catch { }
        }

        /// <summary>WM_QUERYENDSESSION: se pone el motivo, se empieza a apagar en otro hilo y se responde ya.</summary>
        public static void OnQueryEndSession(IntPtr hwnd, long lParam)
        {
            BeginEnding();
            string kind = (lParam & Native.ENDSESSION_LOGOFF) != 0 ? "cierre de sesión" : "apagado o reinicio";
            if ((lParam & Native.ENDSESSION_CRITICAL) != 0) kind += " forzado";
            if (_mode == GuardMode.OffNow)
            {
                TaskAsked(hwnd, "Windows avisa de " + kind);
                return;
            }
            var old = _round;
            if (old != null)
            {
                if (old.EndSeen || Native.Wait(old.Done, 0))
                {
                    // Nos vuelven a preguntar y seguimos vivos: aquel apagado no siguió (y su aviso de "seguimos en
                    // marcha" aún no ha llegado). Empieza uno nuevo; no se vuelve a encender, nos estamos apagando.
                    old.Cancel();
                    _round = null;
                }
                else
                {
                    // El de antes sigue apagando: vale para este también, pero sus avisos atrasados ya no
                    old.Id = ++_roundCount;
                    if (_mode != GuardMode.OffNow) ScheduleResumeCheck(hwnd, old.Id, EndWaitMs + ResumeCheckMs, true);
                    return;
                }
            }
            StartOff(hwnd, "Windows avisa de " + kind);
        }

        /// <summary>
        /// Tarea de respaldo: Windows nos pregunta (o termina la sesión sin preguntar). La tarea ya apagó, o está
        /// apagando, desde que se pidió el apagado. Se apaga otra vez si entretanto volvió a encender (el primero no
        /// siguió) o si aquel apagado acabó sin llegar a todas las tiras; si aún está apagando, ahora que Windows nos
        /// espera se le da el tiempo de un apagado entero desde ya (antes le quedaba lo que sobrara de sus 25 s).
        /// </summary>
        static void TaskAsked(IntPtr hwnd, string what)
        {
            var cur = _round;
            if (cur == null)
            {
                _taskResumed = false;
                StartOff(hwnd, what);
            }
            else
            {
                // Con el candado del apagado: o ya dejó de insistir (Finishing; alargarle el tiempo no serviría y, si no
                // llegó a todas, se empieza otro), o se le alarga y lo verá antes de decidir si sigue
                bool finished, extended = false;
                lock (cur)
                {
                    finished = cur.Finishing || Native.Wait(cur.Done, 0);
                    long want = cur.Clock.ElapsedMilliseconds + OffBudgetMs;
                    if (!finished && want > Interlocked.Read(ref cur.Deadline))
                    {
                        Interlocked.Exchange(ref cur.Deadline, want);
                        extended = true;
                    }
                }
                if (finished)
                {
                    if (!cur.Cancelled && !AllOk(cur))
                    {
                        // El anterior se corta: un hilo suyo que siguiera esperando no debe apagar más tarde (por
                        // ejemplo, después de volver a encender si este apagado se cancela)
                        cur.Superseded = true;
                        cur.Cancel();
                        _round = null;
                        StartOff(hwnd, what + ": vuelvo a intentar apagar");
                    }
                }
                else if (extended)
                    StartThread(() => Log.Write(what + ": sigo apagando, con " + (OffBudgetMs / 1000) + " s desde ahora."));
            }
            var r = _round;
            if (r != null) r.QuerySeen = true;
        }

        /// <param name="early">La tarea de respaldo al pedirse el apagado: Windows aún no nos espera, hay más tiempo.</param>
        static void StartOff(IntPtr hwnd, string why, bool early = false)
        {
            if (_round != null) return; // ya empezó en esta ronda
            var round = new OffRound { Id = ++_roundCount, Deadline = early ? TaskBudgetMs : OffBudgetMs, Early = early };
            _round = round;
            // Después de la pregunta siempre llega WM_ENDSESSION; si por lo que sea no llegara y seguimos vivos,
            // que este apagado no se quede a medias para siempre (los siguientes no apagarían).
            if (_mode != GuardMode.OffNow) ScheduleResumeCheck(hwnd, round.Id, EndWaitMs + ResumeCheckMs, true);
            var cfg = _cfg;
            if (cfg == null || !cfg.PowerOff || cfg.Addrs.Count == 0)
            {
                bool disabled = cfg != null && !cfg.PowerOff;
                string note = disabled ? ". Apagar las luces está desactivado en la interfaz." : ", pero no hay ninguna MAC en device.cfg.";
                _exitCode = disabled ? 0 : 2;
                round.Done.Set();
                StartThread(() => Log.Write(why + note));
                // La tarea de respaldo no apagó nada, así que no hay nada que deshacer si se cancela: se va ya
                if (_mode == GuardMode.OffNow) Native.PostMessage(hwnd, WM_APP_QUIT, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            if (!_reason)
            {
                try { _reason = Native.ShutdownBlockReasonCreate(hwnd, "Apagando las luces LED…"); } catch { }
            }

            var addrs = new List<ulong>(cfg.Addrs);
            round.Addrs = addrs;
            var mode = _mode;
            var t = new Thread(() =>
            {
                try
                {
                    // Apuntarlo en otro hilo: si el disco va lento en pleno apagado, que no retrase la orden
                    StartThread(() => Log.Write(why + ". Apagando " + Log.List(addrs) + "…"));
                    // Una orden de encender a medias se corta al momento (Lights.Abort: sus esperas terminan enseguida y
                    // sueltan el turno); se espera a que lo suelte y se guarda hasta el final, para que ninguna salga
                    // después del apagado. Solo un hilo parado justo entre mirar Abort y escribir lo retendría.
                    bool gate = Monitor.TryEnter(Lights.Gate, 4000);
                    Dictionary<ulong, StripResult> res;
                    try
                    {
                        // El tiempo se cuenta desde que empezó el apagado, y puede crecer (la tarea, cuando Windows
                        // pregunta). Si crece justo cuando SetAll ya se rendía, se vuelve a intentar con las que faltan.
                        // Si el apagado no sigue, sus esperas se cortan (CancelEvent): que no apague más tarde.
                        res = new Dictionary<ulong, StripResult>();
                        var pending = new List<ulong>(addrs);
                        bool resent = false;
                        while (true)
                        {
                            if (pending.Count > 0)
                            {
                                long start = round.Clock.ElapsedMilliseconds;
                                Func<int> budget = () => (int)Math.Max(1500, Interlocked.Read(ref round.Deadline) - start);
                                var part = Lights.SetAll(pending, false, budget, () => round.Cancelled, round.CancelEvent);
                                foreach (var kv in part)
                                {
                                    StripResult before;
                                    if (res.TryGetValue(kv.Key, out before)) kv.Value.Attempts += before.Attempts;
                                    res[kv.Key] = kv.Value;
                                }
                            }
                            pending.Clear();
                            foreach (var kv in res) if (!kv.Value.Ok) pending.Add(kv.Key);
                            // Una orden de encender que no soltó el turno en 4 s pudo salir después de este apagado: en
                            // cuanto lo suelta, se vuelven a apagar (una vez) las que ya se habían apagado. Si aún quedan
                            // tiras por apagar no se espera: se mira otra vez tras el siguiente intento.
                            bool resendNow = false;
                            if (!gate && !round.Cancelled)
                            {
                                long left = Interlocked.Read(ref round.Deadline) - round.Clock.ElapsedMilliseconds;
                                int wait = pending.Count == 0 ? (int)Math.Max(0, left - 2000) : 0;
                                if (Monitor.TryEnter(Lights.Gate, wait))
                                {
                                    gate = true;
                                    if (!resent)
                                    {
                                        resent = true;
                                        var again = new List<ulong>();
                                        foreach (var kv in res) if (kv.Value.Ok) again.Add(kv.Key);
                                        pending.AddRange(again);
                                        resendNow = again.Count > 0;
                                        if (resendNow)
                                            StartThread(() => Log.Write("Una orden de encender tardó en soltar el turno: vuelvo a apagar " + Log.List(again) + "."));
                                    }
                                }
                            }
                            // Dejar de insistir se decide con el candado del apagado: si a la vez la tarea le alarga el
                            // tiempo (TaskAsked), o lo ve aquí y sigue, o ve Finishing y empieza otro apagado. El
                            // reenvío tiene una pasada aunque quede poco (SetAll da al menos 1,5 s).
                            lock (round)
                            {
                                if (pending.Count == 0 || round.Cancelled ||
                                    (!resendNow && Interlocked.Read(ref round.Deadline) - round.Clock.ElapsedMilliseconds < 2000))
                                {
                                    // Sin el turno, una orden de encender pudo llegar después: no se da por apagada
                                    if (!gate)
                                        foreach (var r in res.Values)
                                            if (r.Ok) { r.Ok = false; r.Detail = "sin confirmar: una orden de encender no soltó el turno"; }
                                    round.Result = res;
                                    round.Finishing = true;
                                    break;
                                }
                            }
                        }
                        _exitCode = Lights.CountOk(res) == res.Count ? 0 : 3;
                    }
                    finally { if (gate) Monitor.Exit(Lights.Gate); }
                    // Después de soltar el turno: si la tarea vuelve a intentarlo, que no espere al disco
                    Log.Results(round.Superseded ? "Apagar (primer intento)" :
                                round.Cancelled ? "Apagar (cortado: el apagado no siguió)" : "Apagar", res);
                }
                catch (Exception ex) { _exitCode = 1; Log.Write("Error al apagar: " + ex.Message); }
                finally
                {
                    try { round.Done.Set(); } catch { }
                    if (mode == GuardMode.OffNow) Native.PostMessage(hwnd, WM_APP_OFFDONE, (IntPtr)round.Id, IntPtr.Zero);
                }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        public static bool InRound { get { return _round != null; } }

        static bool AllOk(OffRound round)
        {
            var res = round.Result;
            return res != null && Lights.CountOk(res) == res.Count;
        }

        /// <summary>WM_ENDSESSION: si la sesión termina, esperamos a que el apagado acabe; si se canceló, vuelve todo.</summary>
        public static void OnEndSession(IntPtr hwnd, bool ending, long lParam)
        {
            if (!ending)
            {
                Resume(hwnd, "Windows canceló el apagado.");
                return;
            }

            // Apagado forzado sin pregunta previa: empezamos ahora (la tarea, como si le hubieran preguntado)
            BeginEnding();
            if (_mode == GuardMode.OffNow)
            {
                var cur = _round;
                if (cur == null || !cur.QuerySeen) TaskAsked(hwnd, "Windows termina la sesión sin preguntar");
            }
            else if (_round == null) StartOff(hwnd, "Windows termina la sesión sin preguntar");
            var round = _round;
            round.EndSeen = true;
            long elapsed = round.Clock.ElapsedMilliseconds;
            // El ayudante cuenta desde la pregunta; la tarea empezó antes y puede ir más allá, pero sin retener a
            // Windows más que el ayudante.
            int left = _mode == GuardMode.OffNow
                ? (int)Math.Max(0, Math.Min(EndWaitMs, Interlocked.Read(ref round.Deadline) + 2500 - elapsed))
                : (int)Math.Max(0, EndWaitMs - elapsed);
            bool finished = Native.Wait(round.Done, left);
            ClearReason(hwnd);
            // Sin escribir en el hilo de la ventana: si el disco se atasca, Windows no puede esperar por eso
            if (!finished) LogBriefly("Se acabó el tiempo antes de confirmar el apagado.", 300);

            // Después de esto Windows nos cierra enseguida. Si dentro de un rato seguimos vivos, es que no llegó a
            // terminar la sesión (por ejemplo, alguien pulsó Cancelar en la pantalla de espera). La tarea lo mira con
            // su propio aviso (también si es su segundo apagado: el primero ya gastó los suyos).
            if (_mode != GuardMode.OffNow) ScheduleResumeCheck(hwnd, round.Id, ResumeCheckMs, false);
            else ScheduleTaskCheck(hwnd, ++_taskCheckGen, ResumeCheckMs); // la de los 75 s ya no vale
        }

        /// <summary>Apunta en el registro desde otro hilo, esperando como mucho maxWaitMs (sin procesar mensajes).</summary>
        static void LogBriefly(string msg, int maxWaitMs)
        {
            var written = new ManualResetEvent(false);
            StartThread(() =>
            {
                try { Log.Write(msg); }
                finally { try { written.Set(); } catch { } }
            });
            Native.Wait(written, maxWaitMs);
        }

        /// <param name="noEnd">El que se programa al preguntar (por si no llega WM_ENDSESSION): si llegó, no vale.</param>
        static void ScheduleResumeCheck(IntPtr hwnd, int roundId, int delayMs, bool noEnd)
        {
            StartThread(() =>
            {
                Thread.Sleep(delayMs);
                Native.PostMessage(hwnd, WM_APP_RESUMECHECK, (IntPtr)roundId, (IntPtr)(noEnd ? 1 : 0));
            });
        }

        public static void OnResumeCheck(IntPtr hwnd, int roundId, bool noEnd)
        {
            var round = _round;
            if (round == null || round.Id != roundId) return;
            if (noEnd && round.EndSeen) return; // de eso se encarga el aviso programado en WM_ENDSESSION
            if (ShuttingDown())
            {
                // Windows sigue cerrando la sesión (va lento, o espera a otro programa): encender ahora sería encender
                // en pleno apagado. Se vuelve a mirar dentro de poco.
                if (!round.SlowNoted)
                {
                    round.SlowNoted = true;
                    StartThread(() => Log.Write("Seguimos en marcha, pero Windows sigue cerrando la sesión: no enciendo."));
                }
                StartThread(() =>
                {
                    Thread.Sleep(TaskRecheckMs);
                    Native.PostMessage(hwnd, WM_APP_RESUMECHECK, (IntPtr)roundId, (IntPtr)(noEnd ? 1 : 0));
                });
                return;
            }
            Resume(hwnd, "Seguimos en marcha: Windows no llegó a cerrar la sesión.");
        }

        /// <summary>
        /// El apagado no siguió adelante: todo vuelve a la normalidad y, si estaban encendidas, se encienden (todas las
        /// que se mandaron apagar: una puede haberse apagado aunque no llegara a confirmarlo).
        /// </summary>
        static void Resume(IntPtr hwnd, string msg)
        {
            var mode = _mode;
            if (mode == GuardMode.OffNow)
            {
                if (_taskResumed) return;
                _taskResumed = true;
            }
            var round = _round;
            _round = null;
            if (round != null) round.Cancel(); // si aún estaba apagando, que lo deje (y que no apague más tarde)
            ClearReason(hwnd);
            _ending = false;
            try { Lights.Abort.Reset(); } catch { }
            // Si entretanto empieza otro apagado y también se cancela, solo vale el aviso de irse de la última vez
            // (el de esta no debe cerrar la tarea mientras la otra aún vuelve a encender)
            int gen = ++_resumeGen;
            StartThread(() =>
            {
                Log.Write(msg);
                bool helperOn = false;
                if (round != null)
                {
                    round.Done.WaitOne((int)Math.Max(3000, Interlocked.Read(ref round.Deadline) + 3000 - round.Clock.ElapsedMilliseconds));
                    var cfg = _cfg;
                    // La tarea tiene que acabar antes de su límite de 4 min (si no, Windows la corta a medias)
                    int onBudget = mode == GuardMode.OffNow
                        ? (int)Math.Min(PowerOnTryMs, TaskLimitMs - TaskMarginMs - _age.ElapsedMilliseconds)
                        : PowerOnTryMs;
                    if (cfg != null && cfg.LightsOn && !_ending && round.Addrs.Count > 0)
                    {
                        // Tarea: si Windows vuelve a estar cerrando la sesión (otro apagado), no se enciende (nos llegará
                        // la pregunta). Unos segundos de margen, por si aún no ha bajado el aviso del que se canceló.
                        bool closing = false;
                        if (mode == GuardMode.OffNow)
                        {
                            for (int i = 0; i < 50 && ShuttingDown() && !_ending; i++) Thread.Sleep(100);
                            closing = ShuttingDown();
                        }
                        if (closing)
                        {
                            helperOn = true; // si este otro apagado también se cancela, que las encienda el ayudante
                            Log.Write("Tarea de respaldo: no vuelvo a encender, Windows está cerrando la sesión otra vez.");
                        }
                        else if (onBudget >= 3000)
                        {
                            Func<bool> cancel = () => _ending || (mode == GuardMode.OffNow && ShuttingDown());
                            var res = Lights.SetAll(round.Addrs, true, onBudget, cancel, true);
                            Log.Results("Volver a encender", res);
                            // Tarea: si no llegó a todas (tiempo recortado cerca del límite, o un apagado nuevo), sigue el
                            // ayudante, que insiste hasta 3 min
                            if (mode == GuardMode.OffNow && !_ending && Lights.CountOk(res) < res.Count)
                            {
                                helperOn = true;
                                Log.Write("Tarea de respaldo: lo que falta por encender lo encenderá el ayudante.");
                            }
                        }
                        else
                        {
                            // Que las encienda el ayudante que se deja en marcha (lo mismo que al iniciar sesión)
                            helperOn = true;
                            Log.Write("Tarea de respaldo: no queda tiempo para volver a encender antes de su límite de 4 min; las encenderá el ayudante.");
                        }
                    }
                }
                // No había ayudante: que se quede uno vigilando para el próximo apagado, y esta tarea se va. Salvo que
                // entretanto haya empezado otro apagado: entonces esta tarea vuelve a apagar y Windows la cierra.
                if (mode == GuardMode.OffNow && !_ending && gen == Thread.VolatileRead(ref _resumeGen))
                {
                    StartResidentHelper(helperOn);
                    // Se va desde el hilo de la ventana, mirando otra vez: si mientras tanto Windows volvió a preguntar,
                    // esta tarea se queda apagando
                    Native.PostMessage(hwnd, WM_APP_TASKDONE, (IntPtr)gen, IntPtr.Zero);
                }
            });
        }

        /// <summary>
        /// La tarea deja un ayudante vigilando. Se abre fuera del job de la tarea: primero lo crea el servicio WMI
        /// (Win32_Process.Create: sesión 1, WinSta0\\Default y fuera de todo job, medido el 22-nov), y si no, el
        /// Explorador con un acceso directo (como lo abre Windows desde Inicio); solo al final, directamente.
        /// </summary>
        /// <param name="restore">Pedirle que vuelva a encender las tiras, porque a la tarea no le dio tiempo.</param>
        static void StartResidentHelper(bool restore)
        {
            try
            {
                string how = "";
                if (MutexExists()) how = " (ya había uno)";
                else
                {
                    string exe = Application.ExecutablePath, dir = Config.ExeDir;
                    // Como administrador, WMI lo abriría también como administrador (y los demás no podrían abrir su mutex
                    // ni sus avisos): entonces el Explorador, que lo abre como usuario normal
                    if (!Elevated() && StartViaWmi(exe, "-watch", dir)) how = "";
                    else if (StartViaExplorer(exe, "-watch", dir)) how = " (con el Explorador)";
                    else if (HelperStarting()) how = " (aún arrancando)";
                    else
                    {
                        var psi = new ProcessStartInfo(exe, "-watch");
                        psi.UseShellExecute = false;
                        psi.WorkingDirectory = dir;
                        Process.Start(psi);
                        how = " (abierto directamente: puede cerrarse con esta tarea)";
                    }
                }
                Log.Write("Tarea de respaldo: dejo el ayudante en marcha para el próximo apagado" + how + ".");
                if (restore)
                {
                    // Sus avisos existen antes que su mutex: se espera a que exista (hasta 20 s, por si aún arranca); sirve
                    // también si el que queda es otro que se abrió a la vez
                    bool sent = false;
                    for (int i = 0; i < 200; i++)
                    {
                        EventWaitHandle e;
                        if (EventWaitHandle.TryOpenExisting(Helper.RestoreEventNameForGuard, out e)) { sent = SetAndClose(e); break; }
                        Thread.Sleep(100);
                    }
                    Log.Write(sent ? "Tarea de respaldo: le pido que vuelva a encender las luces."
                                   : "Tarea de respaldo: no pude pedirle que vuelva a encender las luces.");
                }
            }
            catch (Exception ex) { Log.Write("Tarea de respaldo: no se pudo poner en marcha el ayudante (" + ex.Message + ")."); }
        }

        static bool Elevated()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        static bool SetAndClose(EventWaitHandle e)
        {
            try { return e.Set(); }
            finally { e.Dispose(); }
        }

        static bool MutexExists()
        {
            Mutex m;
            if (!Mutex.TryOpenExisting(Helper.MutexNameForGuard, out m)) return false;
            m.Dispose();
            return true;
        }

        /// <summary>
        /// Lo crea el servicio WMI (Win32_Process.Create), que lo deja fuera de nuestro job, en esta sesión y en el
        /// escritorio del usuario. true si el ayudante quedó en marcha.
        /// </summary>
        static bool StartViaWmi(string exe, string args, string dir)
        {
            try
            {
                using (var cls = new ManagementClass("root\\cimv2", "Win32_Process", null))
                {
                    var a = new object[] { "\"" + exe + "\" " + args, dir, null, null };
                    object r = cls.InvokeMethod("Create", a);
                    if (Convert.ToInt64(r) != 0) return false;
                    return WaitHelperUp(a[3] == null ? 0 : Convert.ToInt32(a[3]));
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// Abre el ayudante a través del Explorador, con un acceso directo, como lo abre Windows desde Inicio: así no
        /// queda dentro del grupo de procesos (job) de esta tarea programada (queda en uno solo suyo, sin peligro). El
        /// acceso va junto al .exe y no en AppData: una app empaquetada (MSIX) puede tener AppData virtualizado, y el
        /// Explorador no vería el archivo. true si el ayudante quedó en marcha.
        /// </summary>
        static bool StartViaExplorer(string exe, string args, string dir)
        {
            try
            {
                if (OthersInSession("explorer") == 0) return false;
                string name = "Ayudante.lnk";
                string lnk = SaveShortcut(Path.Combine(dir, name), exe, args, dir);
                if (lnk == null)
                {
                    Directory.CreateDirectory(Config.DataDir);
                    lnk = SaveShortcut(Path.Combine(Config.DataDir, name), exe, args, dir);
                }
                if (lnk == null) return false;
                string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                using (Process.Start(explorer, "\"" + lnk + "\"")) { }
                return WaitHelperUp(0);
            }
            catch { return false; }
        }

        /// <summary>Guarda el acceso directo. Devuelve su ruta, o null si no se pudo.</summary>
        static string SaveShortcut(string lnk, string exe, string args, string dir)
        {
            try
            {
                var shType = Type.GetTypeFromProgID("WScript.Shell");
                if (shType == null) return null;
                object sh = Activator.CreateInstance(shType);
                try
                {
                    object sc = shType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
                    try
                    {
                        var t = sc.GetType();
                        t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { exe });
                        t.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { args });
                        t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { dir });
                        t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                    }
                    finally { Marshal.ReleaseComObject(sc); }
                }
                finally { Marshal.ReleaseComObject(sh); }
                return File.Exists(lnk) ? lnk : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Espera a que aparezca su mutex. Con el proceso que se abrió (pid), mientras siga vivo, hasta 20 s; sin él
        /// (Explorador), 10 s, y 10 más si hay un "-watch" arrancando en la sesión. Si ha aparecido alguno no se abre
        /// otro: ganaría el mutex al que ya arranca y podría quedar en nuestro job.
        /// </summary>
        static bool WaitHelperUp(int pid)
        {
            for (int i = 0; i < 200; i++)
            {
                if (MutexExists()) return true;
                if (pid > 0) { if (Exited(pid)) return MutexExists(); }
                else if (i >= 100 && !HelperStarting()) break;
                Thread.Sleep(100);
            }
            return MutexExists() || HelperStarting();
        }

        static bool Exited(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return p.HasExited; }
            catch { return true; }
        }

        /// <summary>Si hay otro LotusShutdown con -watch en esta sesión (uno con -probe o -find no cuenta).</summary>
        static bool HelperStarting()
        {
            try
            {
                int me, session;
                using (var p = Process.GetCurrentProcess()) { me = p.Id; session = p.SessionId; }
                using (var s = new ManagementObjectSearcher("root\\cimv2",
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='LotusShutdown.exe' AND SessionId=" + session))
                using (var list = s.Get())
                {
                    foreach (ManagementObject o in list)
                    {
                        using (o)
                        {
                            if (Convert.ToInt32(o["ProcessId"]) == me) continue;
                            var cmd = o["CommandLine"] as string;
                            if (cmd != null && cmd.IndexOf("-watch", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Aviso para el registro si este proceso está en un job (grupo de procesos) que puede cerrarlo junto con otro
        /// programa: uno con más procesos (como el de Claude el 22-nov) o que se cierra de golpe (KILL_ON_JOB_CLOSE).
        /// Lo que abre el Explorador va a un job solo para él y sin eso (el del Asistente de compatibilidad, medido el
        /// 22-nov: 1 proceso, LimitFlags 0x800): no pasa nada. "" si no hay peligro o no se puede saber.
        /// </summary>
        static string JobWarning()
        {
            try
            {
                bool inJob;
                // (IntPtr)(-1): este proceso, sin abrir ningún handle. Solo se mira el job más cercano.
                if (!Native.IsProcessInJob(new IntPtr(-1), IntPtr.Zero, out inJob) || !inJob) return "";
                int procs = -1, flags = -1, ret;
                IntPtr buf = Marshal.AllocHGlobal(256);
                try
                {
                    // Sin handle: el job de este proceso (el más cercano, si hay varios)
                    // JobObjectBasicAccountingInformation (1, 48 bytes): ActiveProcesses en el byte 40
                    if (Native.QueryInformationJobObject(IntPtr.Zero, 1, buf, 48, out ret)) procs = Marshal.ReadInt32(buf, 40);
                    // JobObjectExtendedLimitInformation (9, 144 bytes en 64 bits, 112 en 32): LimitFlags en el byte 16
                    if (Native.QueryInformationJobObject(IntPtr.Zero, 9, buf, IntPtr.Size == 8 ? 144 : 112, out ret))
                        flags = Marshal.ReadInt32(buf, 16);
                }
                finally { Marshal.FreeHGlobal(buf); }
                bool kill = flags != -1 && (flags & 0x2000) != 0;
                if (procs == 1 && flags != -1 && !kill) return "";
                return ". Ojo: está dentro de un job (" + (procs > 0 ? procs + " procesos" : "procesos: ?") +
                       (kill ? ", se cierra de golpe" : "") + "); si es el de quien lo abrió, puede cerrarse con él.";
            }
            catch { return ""; }
        }

        /// <summary>Cuántos procesos con ese nombre hay en esta sesión, sin contar este.</summary>
        static int OthersInSession(string name)
        {
            int n = 0;
            try
            {
                var me = Process.GetCurrentProcess();
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { if (p.Id != me.Id && p.SessionId == me.SessionId) n++; } catch { }
                    p.Dispose();
                }
            }
            catch { }
            return n;
        }

        /// <summary>Tarea de respaldo: ya volvió a encender y dejó el ayudante; se va, salvo que haya empezado otro apagado.</summary>
        public static void OnTaskDone(IntPtr hwnd, int gen)
        {
            if (_mode != GuardMode.OffNow || _ending || _round != null || gen != _resumeGen) return;
            // Otro apagado en marcha que aún no nos ha llegado: la tarea se queda para recibir la pregunta y apagar (el
            // ayudante recién abierto puede no recibirla). Lo cortará el límite de 4 min si hiciera falta.
            if (ShuttingDown())
            {
                StartThread(() =>
                {
                    Thread.Sleep(2000);
                    Native.PostMessage(hwnd, WM_APP_TASKDONE, (IntPtr)gen, IntPtr.Zero);
                });
                return;
            }
            Quit();
        }

        /// <summary>La tarea de respaldo terminó de apagar: quita el motivo y espera a ver si el apagado sigue.</summary>
        public static void OnOffDone(IntPtr hwnd, int roundId)
        {
            if (_mode != GuardMode.OffNow) return;
            // El aviso de un apagado anterior no quita el motivo del que está en marcha
            var round = _round;
            if (round == null || round.Id != roundId) return;
            ClearReason(hwnd);
        }

        /// <summary>
        /// Tarea de respaldo, un rato después: seguimos vivos, así que Windows no nos ha cerrado. Si se está apagando
        /// (por ejemplo, con la pantalla de "programas que impiden apagar"), se vuelve a mirar dentro de poco; si no,
        /// el apagado se canceló o es con espera: se vuelven a encender y se deja el ayudante vigilando. Todo antes del
        /// límite de 4 min de la tarea.
        /// </summary>
        public static void OnTaskCheck(IntPtr hwnd, int gen)
        {
            if (_mode != GuardMode.OffNow || _taskResumed || gen != _taskCheckGen) return;
            if (ShuttingDown())
            {
                // Windows sigue cerrando la sesión (otro programa le hace esperar): nos llegará la pregunta, y con ella
                // el reintento si el apagado no llegó a todas. Se mira cada 10 s hasta poco antes del límite de 4 min.
                // Cerca del límite ya no da tiempo a volver a encender: se va, salvo que Windows ya haya preguntado o esté
                // apagando las luces (entonces sigue hasta el final, o hasta que el límite la corte).
                var r = _round;
                if (r != null && !_ending && !r.Cancelled && Native.Wait(r.Done, 0) && !AllOk(r))
                {
                    // El apagado adelantado no llegó a todas: mientras Windows cierra los demás programas, otra vez
                    r.Superseded = true;
                    r.Cancel();
                    _round = null;
                    StartOff(hwnd, "Windows sigue cerrando la sesión: vuelvo a intentar apagar", true);
                    r = _round;
                }
                bool busy = _ending || (r != null && (!Native.Wait(r.Done, 0) || (!r.Cancelled && !AllOk(r))));
                if (busy || _age.ElapsedMilliseconds + TaskRecheckMs < TaskLimitMs - TaskMarginMs) ScheduleTaskCheck(hwnd, gen, TaskRecheckMs);
                else Quit();
                return;
            }
            Resume(hwnd, "Tarea de respaldo: Windows no llegó a apagarse (se canceló o es un apagado con espera).");
        }

        static void ScheduleTaskCheck(IntPtr hwnd, int gen, int delayMs)
        {
            StartThread(() =>
            {
                Thread.Sleep(delayMs);
                Native.PostMessage(hwnd, WM_APP_TASKCHECK, (IntPtr)gen, IntPtr.Zero);
            });
        }

        static void ClearReason(IntPtr hwnd)
        {
            if (!_reason) return;
            _reason = false;
            try { Native.ShutdownBlockReasonDestroy(hwnd); } catch { }
        }

        public static void Quit()
        {
            if (_mode != GuardMode.OffNow) Log.Write("Ayudante cerrado.");
            Application.ExitThread();
        }
    }

    /// <summary>Ventana de nivel superior oculta: Windows solo manda los avisos de apagado a ventanas.</summary>
    class GuardWindow : NativeWindow
    {
        static System.Drawing.Icon _icon;

        public GuardWindow()
        {
            var cp = new CreateParams();
            // Su título y su icono son los que enseña Windows en la pantalla de "programas que impiden apagar"
            cp.Caption = "LotusLED Desk";
            // Sin padre (de nivel superior: una HWND_MESSAGE no recibe los avisos) y sin WS_VISIBLE
            CreateHandle(cp);
            try
            {
                _icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (_icon != null)
                {
                    Native.SendMessage(Handle, Native.WM_SETICON, (IntPtr)1, _icon.Handle);
                    Native.SendMessage(Handle, Native.WM_SETICON, (IntPtr)0, _icon.Handle);
                }
            }
            catch { }
        }

        static bool IsInstallerClose(long lParam)
        {
            // Con LOGOFF es un cierre de sesión aunque también lleve CLOSEAPP
            if ((lParam & Native.ENDSESSION_CLOSEAPP) == 0 || (lParam & Native.ENDSESSION_LOGOFF) != 0) return false;
            try { return Native.GetSystemMetrics(Native.SM_SHUTTINGDOWN) == 0; } catch { return true; }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_QUERYENDSESSION)
            {
                long lp = m.LParam.ToInt64();
                // ENDSESSION_CLOSEAPP sin que Windows se esté apagando: un instalador (Restart Manager) pide cerrar el
                // programa. Si Windows sí se apaga (por ejemplo, al reiniciar con "reiniciar aplicaciones"), se apaga.
                if (!IsInstallerClose(lp))
                {
                    try { Guard.OnQueryEndSession(Handle, lp); } catch (Exception ex) { Log.Write("Error al apagar: " + ex.Message); }
                }
                m.Result = (IntPtr)1; // sí, Windows puede seguir (el apagado sigue en otro hilo)
                return;
            }
            if (m.Msg == Native.WM_ENDSESSION)
            {
                long lp = m.LParam.ToInt64();
                bool ending = m.WParam != IntPtr.Zero;
                if (IsInstallerClose(lp) && !Guard.InRound)
                {
                    // Nos cierra un instalador: la tarea de respaldo vigila hasta el próximo inicio de sesión
                    if (ending) Guard.Quit();
                }
                else
                {
                    try { Guard.OnEndSession(Handle, ending, lp); } catch (Exception ex) { Log.Write("Error al terminar la sesión: " + ex.Message); }
                }
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == Guard.WM_APP_OFFDONE)
            {
                Guard.OnOffDone(Handle, m.WParam.ToInt32());
                return;
            }
            if (m.Msg == Guard.WM_APP_RESUMECHECK)
            {
                Guard.OnResumeCheck(Handle, m.WParam.ToInt32(), m.LParam != IntPtr.Zero);
                return;
            }
            if (m.Msg == Guard.WM_APP_TASKCHECK)
            {
                Guard.OnTaskCheck(Handle, m.WParam.ToInt32());
                return;
            }
            if (m.Msg == Guard.WM_APP_TASKDONE)
            {
                Guard.OnTaskDone(Handle, m.WParam.ToInt32());
                return;
            }
            if (m.Msg == Guard.WM_APP_QUIT)
            {
                Guard.Quit();
                return;
            }
            if (m.Msg == Native.WM_CLOSE)
            {
                Guard.Quit();
                return;
            }
            base.WndProc(ref m);
        }
    }

    // ========================================================================================================
    // Bluetooth: conectar, encontrar la característica de escritura y mandar encender/apagar
    // ========================================================================================================
    /// <summary>Lo que hace falta para escribir en una tira: sus servicios y la característica (la conexión es del Keeper).</summary>
    class StripLink : IDisposable
    {
        public ulong Address;
        public string Name = "";
        public Keeper Keeper;
        public BluetoothLEDevice Device;    // el del Keeper: no se cierra aquí
        public List<GattDeviceService> Services = new List<GattDeviceService>();
        public GattCharacteristic Char;
        public bool Melk;
        public string OpenStatus = "";      // lo que dijo OpenAsync al abrir el servicio (si no fue bien)
        public long MsFind;                 // cuánto tardó Windows en darnos la tira en este intento (0 si ya la teníamos)
        public long MsGatt;                 // cuánto tardaron sus servicios y características (aquí se espera a conectar)

        /// <summary>
        /// Sus servicios no se cierran aquí sino al cerrar la conexión (Keeper.Close): mientras otro hilo use la
        /// misma tira, Windows podría haberle dado los mismos objetos, y cerrarlos le dejaría sin ellos.
        /// </summary>
        public void Dispose()
        {
            var k = Keeper;
            if (k != null) k.Hold(Services);
            else foreach (var s in Services) { try { s.Dispose(); } catch { } }
            Services.Clear();
            Char = null;
            Device = null;
        }
    }

    /// <summary>
    /// La conexión con una tira, una sola para todo el proceso (apagar, encender, la conexión adelantada, la prueba).
    /// Mientras alguien la use, se le pide a Windows que conecte y no suelte la conexión (GattSession.MaintainConnection),
    /// así que un intento que se pase de tiempo no tira lo que ya llevaba hecho: en el PC del usuario conectar en frío
    /// tarda de 6 a 16 s, y antes cada intento cortado volvía a empezar de cero. Nadie cierra el dispositivo que usa
    /// otro hilo. Cuando la suelta el último se cierra todo: con la conexión abierta la tira no se anuncia y nadie más
    /// (el móvil, la ventana web) la vería.
    /// </summary>
    class Keeper
    {
        public ulong Address;
        public BluetoothLEDevice Device;
        public GattSession Session;
        public string Owner = "";               // quién la abrió: "apagar", "encender", "adelantada" o "prueba"
        public bool Scanned;                    // Windows no la conocía y hubo que escuchar su anuncio
        public long MsFind;                     // cuánto tardó Windows en darnos la tira
        public readonly Stopwatch Clock = Stopwatch.StartNew();     // desde que se abrió
        public readonly ManualResetEvent Connected = new ManualResetEvent(false);  // ahora mismo conectada
        public int Refs;                        // cuántos la están usando (lo lleva Lights, con su candado)
        long _msConnected = -1;                 // a los cuántos ms de abrirla conectó por primera vez
        TypedEventHandler<BluetoothLEDevice, object> _onStatus;
        GattSession _watchedSession;
        TypedEventHandler<GattSession, GattSessionStatusChangedEventArgs> _onSession;
        readonly List<GattDeviceService> _held = new List<GattDeviceService>();
        bool _closed;
        public volatile bool Closed;            // ya se cerró: nadie debe ponerle sesión
        public bool Lingered;                   // ya se dejó abierta un momento al soltarla en el apagado (lo lleva Lights)
        public readonly object SessionGate = new object();   // de uno en uno al abrir la sesión

        public long MsConnected { get { return Interlocked.Read(ref _msConnected); } }

        /// <summary>Conectada según el dispositivo, o según la sesión (Chrome mira la sesión: Active).</summary>
        public bool IsConnected
        {
            get
            {
                try { var d = Device; if (d != null && d.ConnectionStatus == BluetoothConnectionStatus.Connected) return true; }
                catch { }
                try { var s = Session; return s != null && s.SessionStatus == GattSessionStatus.Active; }
                catch { return false; }
            }
        }

        /// <summary>Apunta cuándo conecta (solo para saber cuánto tarda; Windows conecta igual aunque no miremos).</summary>
        public void Watch()
        {
            try
            {
                _onStatus = (d, o) => Check();
                Device.ConnectionStatusChanged += _onStatus;
            }
            catch { _onStatus = null; }
            Check();
        }

        /// <summary>Lo mismo con la sesión, cuando la hay.</summary>
        public void WatchSession()
        {
            var s = Session;
            if (s == null || _watchedSession != null) return;
            try
            {
                _onSession = (x, e) => Check();
                s.SessionStatusChanged += _onSession;
                _watchedSession = s;
            }
            catch { _onSession = null; }
            Check();
        }

        readonly object _checkLock = new object();

        void Check()
        {
            // De uno en uno, mirando el estado de ahora: un aviso atrasado no deja Connected activo estando desconectada
            lock (_checkLock)
            {
                try
                {
                    if (IsConnected)
                    {
                        Interlocked.CompareExchange(ref _msConnected, Clock.ElapsedMilliseconds, -1);
                        Connected.Set();
                    }
                    else Connected.Reset();
                }
                catch { }
            }
        }

        /// <summary>
        /// Guarda servicios que ya no hacen falta para cerrarlos con la conexión, y no antes: si dos hilos usan la
        /// tira a la vez (la conexión adelantada y el apagado), cerrarlos ahora podría quitárselos al otro. Ya
        /// cerrada la conexión, se cierran al momento.
        /// </summary>
        public void Hold(IEnumerable<GattDeviceService> services)
        {
            if (services == null) return;
            var late = new List<GattDeviceService>();
            lock (_held)
            {
                foreach (var s in services)
                {
                    if (s == null) continue;
                    if (_closed) late.Add(s);
                    else if (!_held.Contains(s)) _held.Add(s);
                }
            }
            foreach (var s in late) { try { s.Dispose(); } catch { } }
        }

        /// <summary>Lo mismo con lo que devuelve una petición de servicios (también la que llega tarde).</summary>
        public void Hold(GattDeviceServicesResult res)
        {
            if (res != null) { try { Hold(res.Services); } catch { } }
        }

        /// <summary>Deja de pedir que se mantenga la conexión y lo cierra todo (lo llama Lights.Release).</summary>
        public void Close()
        {
            Closed = true;
            List<GattDeviceService> held;
            lock (_held)
            {
                _closed = true;
                held = new List<GattDeviceService>(_held);
                _held.Clear();
            }
            foreach (var x in held) { try { x.Dispose(); } catch { } }
            var d = Device;
            if (d != null && _onStatus != null) { try { d.ConnectionStatusChanged -= _onStatus; } catch { } }
            var s = Session;
            Session = null;
            var ws = _watchedSession;
            _watchedSession = null;
            if (ws != null && _onSession != null) { try { ws.SessionStatusChanged -= _onSession; } catch { } }
            if (s != null)
            {
                try { s.MaintainConnection = false; } catch { }
                try { s.Dispose(); } catch { }
            }
            Device = null;
            if (d != null) { try { d.Dispose(); } catch { } }
            // Connected no se cierra: puede haber alguien esperándolo todavía
        }
    }

    class StripResult
    {
        public ulong Address;
        public string Name = "";
        public bool Ok;
        public long Ms;
        public int Attempts;
        public string Detail = "";
        public bool Closed;     // SetAll ya devolvió: lo que pase después no cuenta
    }

    static class Lights
    {
        /// <summary>Se toma al mandar "encender", para que nunca llegue después de un "apagar" (que lo toma todo el rato).</summary>
        public static readonly object Gate = new object();

        /// <summary>Se activa cuando Windows avisa del apagado: corta al momento las esperas de las órdenes de encender.</summary>
        public static readonly ManualResetEvent Abort = new ManualResetEvent(false);

        /// <summary>En los hilos que encienden, Abort; en los que apagan, null (el apagado no se corta).</summary>
        [ThreadStatic] static WaitHandle _abort;

        static readonly byte[] ElkOn = { 0x7E, 0x00, 0x04, 0xF0, 0x00, 0x01, 0xFF, 0x00, 0xEF };
        static readonly byte[] ElkOff = { 0x7E, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0x00, 0xEF };
        static readonly byte[] MelkOn = { 0x7E, 0x07, 0x04, 0xFF, 0x00, 0x01, 0x02, 0x01, 0xEF };
        static readonly byte[] MelkOff = { 0x7E, 0x07, 0x04, 0x00, 0x00, 0x00, 0x02, 0x01, 0xEF };
        // Las MELK quieren este saludo antes de la primera orden (lo mismo que manda la interfaz web al conectar)
        static readonly byte[][] MelkInit = { new byte[] { 0x7E, 0x07, 0x83 }, new byte[] { 0x7E, 0x04, 0x04 } };

        // Ya conectada, lo que se le deja a una petición antes de darla por atascada (los servicios tardan 0,3-0,8 s)
        const int ConnectedGraceMs = 4000;

        // Servicios y características de escritura conocidos (los mismos que usa la interfaz web, en el mismo orden)
        static readonly string[] Services = { "fff0", "ffe0", "ffe5", "ffd0", "ffd5", "ff00", "ae00", "fee7" };
        static readonly string[] WriteChars = { "fff3", "ffe1", "ffe9", "ffd9", "ff01", "ae01", "fff2", "fff1" };
        const string NordicUartRx = "6e400002";

        /// <summary>Carga la parte de Bluetooth de Windows con antelación para que el apagado vaya más rápido.</summary>
        public static void Warmup()
        {
            try
            {
                var ad = AwaitOp(BluetoothAdapter.GetDefaultAsync(), 10000);
                if (ad == null) Log.Write("Aviso: no se encontró ningún adaptador Bluetooth.");
            }
            catch { }
        }

        // Las conexiones abiertas, una por tira (ver Keeper), y un candado por tira para abrirlas y cerrarlas: así dos
        // hilos nunca tienen a la vez dos objetos de la misma tira (Windows puede darles el mismo, y cerrar uno
        // cerraría el del otro).
        static readonly Dictionary<ulong, Keeper> _keepers = new Dictionary<ulong, Keeper>();
        static readonly Dictionary<ulong, object> _keeperLocks = new Dictionary<ulong, object>();

        static object KeeperLock(ulong addr)
        {
            lock (_keepers)
            {
                object o;
                if (!_keeperLocks.TryGetValue(addr, out o)) { o = new object(); _keeperLocks[addr] = o; }
                return o;
            }
        }

        /// <summary>
        /// La conexión con la tira: la que ya hay abierta o una nueva, en la que se pide a Windows que conecte y la
        /// mantenga. No espera a que conecte. null (y por qué) si Windows no la encuentra a tiempo. Cada Acquire que
        /// no devuelva null lleva su Release.
        /// </summary>
        public static Keeper Acquire(ulong addr, string owner, Stopwatch clock, int budgetMs, out string error)
        {
            error = "";
            var gate = KeeperLock(addr);
            // Si otro hilo la está abriendo, se espera a que acabe: luego vale para los dos
            if (!Monitor.TryEnter(gate, Left(clock, budgetMs, 30000)))
            {
                error = "sin tiempo para buscarla";
                return null;
            }
            BluetoothLEDevice dev = null;
            bool kept = false;
            try
            {
                Keeper k;
                lock (_keepers)
                {
                    if (_keepers.TryGetValue(addr, out k)) k.Refs++;
                }
                if (k != null) return k;
                var phase = Stopwatch.StartNew();
                bool scanned = false;
                dev = AwaitOp(BluetoothLEDevice.FromBluetoothAddressAsync(addr), Left(clock, budgetMs, 5000));
                if (dev == null && !Aborted())
                {
                    // Sin emparejar, Windows solo la conoce si la ha visto hace poco (tras reiniciar, no):
                    // la buscamos un momento para que entre en su lista y volvemos a probar.
                    scanned = true;
                    if (Finder.WaitForAdvert(addr, Left(clock, budgetMs, 4000), _abort))
                        dev = AwaitOp(BluetoothLEDevice.FromBluetoothAddressAsync(addr), Left(clock, budgetMs, 4000));
                }
                if (dev == null)
                {
                    error = Aborted() ? "cancelado" : "Windows no la encuentra (¿apagada, lejos o conectada al móvil?)" +
                            " (" + phase.ElapsedMilliseconds + " ms)";
                    return null;
                }
                var keeper = new Keeper { Address = addr, Device = dev, Owner = owner, Scanned = scanned, MsFind = phase.ElapsedMilliseconds, Refs = 1 };
                keeper.Watch();
                // Sin sesión todavía: la conexión la provocan las peticiones a la tira (ver Open). Con la 1.1.4 se pedía
                // MaintainConnection desde el principio y conectar pasó a tardar de 0 a más de 30 s; pidiendo los
                // servicios sin caché tardaba 5-7 s. La sesión se pide después, para no soltarla (Maintain).
                lock (_keepers) _keepers[addr] = keeper;
                kept = true;
                return keeper;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
            finally
            {
                if (!kept)
                {
                    // Lo que se abrió a medias se cierra aquí mismo (nunca una conexión abierta sin dueño)
                    if (dev != null) { try { dev.Dispose(); } catch { } }
                }
                Monitor.Exit(gate);
            }
        }

        /// <summary>
        /// Pide a Windows que no suelte la conexión mientras la tengamos (GattSession.MaintainConnection). Para la
        /// conexión adelantada, una vez conectada, o desde el principio con "-probe -session" (para comparar).
        /// </summary>
        public static void Maintain(Keeper k, int timeoutMs)
        {
            if (k == null) return;
            lock (k.SessionGate)
            {
                var dev = k.Device;
                if (k.Session != null || dev == null || k.Closed) return;
                // Abrir la sesión puede tardar (hasta timeoutMs): sin el candado de la tira, que el apagado no tenga
                // que esperar por esto para coger la conexión o soltarla
                var session = OpenSession(dev, timeoutMs);
                if (session == null) return;
                lock (KeeperLock(k.Address))
                {
                    if (!k.Closed && k.Session == null)
                    {
                        k.Session = session;
                        session = null;
                        k.WatchSession();
                    }
                }
                if (session != null)
                {
                    // Se cerró mientras tanto: esta sesión no es de nadie
                    try { session.MaintainConnection = false; } catch { }
                    try { session.Dispose(); } catch { }
                }
            }
        }

        /// <summary>
        /// Provoca la conexión: pide los servicios a la tira (sin caché, que es lo que hace conectar a Windows) y espera
        /// a que contesten, a que Windows se rinda (~7 s), a que pase timeoutMs o a que se suelte la conexión
        /// adelantada. Los servicios que devuelva (también si llegan tarde) los guarda el Keeper hasta que se cierre:
        /// cerrarlos ahora podría quitárselos al apagado, que pide los mismos a la vez, y además así Windows no suelta
        /// la conexión antes de que Maintain pida mantenerla.
        /// </summary>
        public static bool Poke(Keeper k, int timeoutMs)
        {
            var d = k != null ? k.Device : null;
            if (d == null) return false;
            try
            {
                var res = AwaitOp(d.GetGattServicesAsync(BluetoothCacheMode.Uncached), timeoutMs, k.Connected, ConnectedGraceMs, k.Hold);
                if (res == null) return false;
                k.Hold(res);
                return res.Status == GattCommunicationStatus.Success;
            }
            catch { return false; }
        }

        /// <summary>
        /// Las esperas de este hilo se cortan cuando se active h (además de con el aviso de Windows, si ya lo usaba).
        /// Para los hilos de la conexión adelantada: al soltarla no queda ninguna petición a la tira en marcha.
        /// </summary>
        public static void UseAbort(WaitHandle h)
        {
            _abort = h;
        }

        /// <summary>La sesión que pide a Windows conectar y mantener la conexión. null si no se pudo.</summary>
        static GattSession OpenSession(BluetoothLEDevice dev, int timeoutMs)
        {
            if (dev == null) return null;
            GattSession session = null;
            try
            {
                session = AwaitOp(GattSession.FromDeviceIdAsync(dev.BluetoothDeviceId), timeoutMs);
                if (session != null) session.MaintainConnection = true;
                return session;
            }
            catch
            {
                if (session != null) { try { session.Dispose(); } catch { } }
                return null;
            }
        }

        // Al soltar la última en pleno apagado, lo que se deja abierta por si la recoge el apagado
        const int LingerMs = 3000;

        /// <summary>
        /// Se deja de usar la conexión; con el último, se cierra (y la tira vuelve a anunciarse). Si Windows ya avisó
        /// del apagado (una orden de encender cortada a medias), se deja abierta unos segundos: el apagado llega
        /// enseguida y así no empieza de cero lo que Windows llevaba conectado.
        /// </summary>
        public static void Release(Keeper k)
        {
            if (k == null) return;
            // Con el candado de la tira: que nadie abra otra mientras esta se cierra. Solo se espera de verdad si otro
            // hilo está mirando si ya hay una (eso es un momento): mientras exista esta, nadie abre otra.
            lock (KeeperLock(k.Address))
            {
                lock (_keepers)
                {
                    if (--k.Refs > 0) return;
                    // Solo una orden de encender cortada por el aviso (sus hilos esperan con Abort); al acabar el
                    // apagado, o la conexión adelantada, se cierra ya
                    if (!k.Lingered && _abort == Abort && Abort.WaitOne(0))
                    {
                        k.Lingered = true;
                        var t = new Thread(() => { Thread.Sleep(LingerMs); CloseIfUnused(k); });
                        t.IsBackground = true;
                        t.SetApartmentState(ApartmentState.MTA);
                        t.Start();
                        return;
                    }
                    Keeper cur;
                    if (_keepers.TryGetValue(k.Address, out cur) && cur == k) _keepers.Remove(k.Address);
                }
                k.Close();
            }
        }

        /// <summary>La que se dejó abierta al soltarla en el apagado: si nadie la recogió, se cierra.</summary>
        static void CloseIfUnused(Keeper k)
        {
            try
            {
                lock (KeeperLock(k.Address))
                {
                    lock (_keepers)
                    {
                        if (k.Refs > 0 || k.Closed) return; // la recogió alguien (la cierra su Release), o ya se cerró
                        Keeper cur;
                        if (_keepers.TryGetValue(k.Address, out cur) && cur == k) _keepers.Remove(k.Address);
                    }
                    k.Close();
                }
            }
            catch { }
        }

        public static int CountOk(Dictionary<ulong, StripResult> res)
        {
            int n = 0;
            foreach (var r in res.Values) if (r.Ok) n++;
            return n;
        }

        /// <summary>
        /// Enciende o apaga todas las tiras a la vez (cada una en su hilo) y reintenta hasta que se acaba el tiempo.
        /// Nunca deja conexiones abiertas: si no, la tira dejaría de anunciarse y nadie más la vería.
        /// Con abortable, las esperas se cortan en cuanto Windows avisa del apagado (Abort).
        /// </summary>
        public static Dictionary<ulong, StripResult> SetAll(List<ulong> addrs, bool on, int budgetMs, Func<bool> cancel, bool abortable)
        {
            return SetAll(addrs, on, () => budgetMs, cancel, abortable ? Abort : null);
        }

        /// <summary>
        /// Igual, con un tiempo que puede crecer mientras tanto (se mira en cada intento) y lo que corta las esperas de
        /// estos hilos (null: nada).
        /// </summary>
        public static Dictionary<ulong, StripResult> SetAll(List<ulong> addrs, bool on, Func<int> budget, Func<bool> cancel, WaitHandle abort)
        {
            var results = new Dictionary<ulong, StripResult>();
            var clock = Stopwatch.StartNew();
            var threads = new List<Thread>();
            foreach (var addr in addrs)
            {
                var r = new StripResult { Address = addr };
                results[addr] = r;
                var t = new Thread(() =>
                {
                    _abort = abort;
                    Worker(r, on, clock, budget, cancel);
                });
                t.IsBackground = true;
                t.SetApartmentState(ApartmentState.MTA);
                t.Start();
                threads.Add(t);
            }
            foreach (var t in threads)
            {
                // A ratos, por si el tiempo crece mientras tanto
                while (true)
                {
                    long left = budget() + 1000 - clock.ElapsedMilliseconds;
                    if (left <= 0 || t.Join((int)Math.Min(left, 500))) break;
                }
            }
            // Copia: un hilo que se pasó de tiempo no debe cambiar lo que ya devolvimos
            var copy = new Dictionary<ulong, StripResult>();
            foreach (var kv in results)
            {
                var r = kv.Value;
                lock (r)
                {
                    copy[kv.Key] = new StripResult { Address = r.Address, Name = r.Name, Ok = r.Ok, Ms = r.Ms, Attempts = r.Attempts, Detail = r.Detail };
                    r.Closed = true;
                }
            }
            return copy;
        }

        static void Worker(StripResult r, bool on, Stopwatch clock, Func<int> budget, Func<bool> cancel)
        {
            string last = "";
            bool uncachedOnly = false;
            Keeper keeper = null;
            bool wasConnected = false;  // ya estaba conectada al empezar (la conexión adelantada, o la ventana web)
            try
            {
                for (int attempt = 1; ; attempt++)
                {
                    int budgetMs = budget();
                    long left = budgetMs - clock.ElapsedMilliseconds;
                    if (left < 600 || (cancel != null && cancel())) break;

                    StripLink link = null;
                    bool ok = false;
                    long msSend = 0;
                    try
                    {
                        string err;
                        long msFind = 0;
                        if (keeper == null)
                        {
                            // La conexión se abre una vez y dura todos los intentos: lo que Windows lleve conectado
                            // no se pierde aunque un intento se pase de tiempo
                            var findClock = Stopwatch.StartNew();
                            keeper = Acquire(r.Address, on ? "encender" : "apagar", clock, budgetMs, out err);
                            msFind = findClock.ElapsedMilliseconds;
                            if (keeper != null) wasConnected = keeper.IsConnected;
                        }
                        else err = "";
                        if (keeper == null) last = err;
                        else
                        {
                            link = Open(keeper, clock, budgetMs, uncachedOnly, out err);
                            if (link == null) last = err + ConnNote(keeper, wasConnected);
                            else
                            {
                                link.MsFind = msFind;
                                // La primera escritura puede tener que esperar a que Windows conecte: tiene todo el
                                // tiempo que quede (otro intento no iría más rápido, la conexión sigue la misma)
                                int t = (int)Math.Max(400, budgetMs - clock.ElapsedMilliseconds - 100);
                                string why;
                                var sendClock = Stopwatch.StartNew();
                                ok = Send(link, on, t, cancel, out why);
                                msSend = sendClock.ElapsedMilliseconds;
                                if (!ok)
                                {
                                    last = (cancel != null && cancel()) ? "cancelado"
                                         : why + " (buscar " + link.MsFind + " ms, servicios " + link.MsGatt + " ms, orden " + msSend + " ms" +
                                           ConnNote(keeper, wasConnected) + ")";
                                    // Lo que Windows tenía guardado de la tira puede estar viejo: la próxima vez, preguntándole a ella
                                    uncachedOnly = true;
                                }
                            }
                        }
                    }
                    catch (Exception ex) { last = ex.Message; }

                    if (link != null) link.Dispose(); // sus servicios; la conexión la cierra el Release del final
                    lock (r)
                    {
                        if (r.Closed) return; // SetAll ya devolvió sin esperarnos
                        r.Attempts = attempt;
                        if (link != null && link.Name.Length > 0) r.Name = link.Name;
                        if (ok)
                        {
                            r.Ok = true;
                            r.Ms = clock.ElapsedMilliseconds;
                            r.Detail = (link.Melk ? "MELK" : "ELK") + (keeper.Scanned ? ", hubo que buscarla" : "") +
                                       ConnNote(keeper, wasConnected) +
                                       ": buscar " + link.MsFind + " ms, servicios " + link.MsGatt + " ms, orden " + msSend + " ms";
                        }
                        else r.Detail = last;
                    }
                    if (ok) return;
                    Thread.Sleep((int)Math.Max(0, Math.Min(keeper != null ? 200 : 500, budget() - clock.ElapsedMilliseconds - 600)));
                }
            }
            finally { Release(keeper); }
        }

        /// <summary>Para el registro: si la tira ya estaba conectada, o cuánto tardó Windows en conectar con ella.</summary>
        static string ConnNote(Keeper k, bool wasConnected)
        {
            if (k == null) return "";
            if (wasConnected) return ", ya estaba conectada";
            long ms = k.MsConnected;
            string ahead = k.Owner == "adelantada" ? " desde la conexión adelantada" : "";
            return ms >= 0 ? ", conectó a los " + ms + " ms" + ahead : ", sin conectar tras " + k.Clock.ElapsedMilliseconds + " ms" + ahead;
        }

        /// <summary>
        /// Conecta con cada tira y busca dónde escribir, pero no manda ninguna orden: las luces no cambian. Para saber
        /// que la tira de verdad contesta (y no es solo lo que Windows tenía guardado), lee algo de ella. Apunta cuánto
        /// tardó cada fase y, sobre todo, a los cuántos ms conectó Windows. Para comparar formas de conectar: con scan,
        /// escucha anuncios mientras tanto; con session, pide MaintainConnection desde el principio, como la 1.1.4, pero
        /// sigue pidiendo los servicios sin caché hasta que conecta (la 1.1.4 pedía lo guardado, que no hace conectar).
        /// </summary>
        public static int Probe(List<ulong> addrs, bool scan, bool session)
        {
            if (addrs.Count == 0)
            {
                Log.Write("Prueba: no hay ninguna MAC en device.cfg.");
                return 2;
            }
            const int ProbeBudgetMs = 30000;
            int ok = 0;
            foreach (var addr in addrs)
            {
                var clock = Stopwatch.StartNew();
                string err = "";
                StripLink link = null;
                Keeper keeper = null;
                string readStatus = "";
                var failed = new List<string>();   // cómo fue cada intento que no sirvió, con sus tiempos
                int attempt = 0;
                BluetoothLEAdvertisementWatcher watcher = scan ? Finder.StartScan() : null;
                try
                {
                    keeper = Acquire(addr, "prueba", clock, ProbeBudgetMs, out err);
                    if (keeper == null)
                    {
                        Log.Write("Prueba " + Log.Short(addr) + ": no se llega en " + clock.ElapsedMilliseconds + " ms (" + err + ")." +
                                  (scan ? " Escuchando anuncios a la vez." : ""));
                        continue;
                    }
                    bool wasConnected = keeper.IsConnected;
                    if (session) Maintain(keeper, 3000);
                    while (attempt < 10 && link == null && clock.ElapsedMilliseconds < ProbeBudgetMs - 1000)
                    {
                        attempt++;
                        long t0 = clock.ElapsedMilliseconds;
                        link = Open(keeper, clock, ProbeBudgetMs, attempt > 1, out err);
                        if (link == null)
                        {
                            failed.Add("intento " + attempt + " (" + (clock.ElapsedMilliseconds - t0) + " ms): " + err);
                            Thread.Sleep(200);
                        }
                    }
                    if (watcher != null) { Finder.StopScan(watcher); watcher = null; }
                    string before = failed.Count > 0 ? " Antes: " + string.Join("; ", failed.ToArray()) + "." : "";
                    string conn = ConnNote(keeper, wasConnected);
                    if (link == null)
                    {
                        Log.Write("Prueba " + Log.Short(addr) + ": no se llega en " + clock.ElapsedMilliseconds + " ms (" +
                                  conn.TrimStart(',', ' ') + (scan ? ", escuchando anuncios a la vez" : "") +
                                  (session ? ", con MaintainConnection desde el principio" : "") + ")." + before);
                        continue;
                    }
                    var p = link.Char.CharacteristicProperties;
                    // Antes de la segunda petición de servicios (que se cierra luego, y podría ser el mismo objeto)
                    string svcId = Uuid16(link.Char.Service.Uuid), charId = Uuid16(link.Char.Uuid);
                    string answer = null;
                    var answerClock = Stopwatch.StartNew();
                    int answerWait = (int)Math.Max(4000, ProbeBudgetMs - clock.ElapsedMilliseconds);
                    if ((p & GattCharacteristicProperties.Read) != 0)
                    {
                        // Success o ProtocolError: contestó la tira. AccessDenied lo dice Windows: se prueba de la otra forma.
                        var rd = AwaitOp(link.Char.ReadValueAsync(BluetoothCacheMode.Uncached), answerWait, keeper.Connected, ConnectedGraceMs);
                        if (rd == null) answer = "no contestó";
                        else if (rd.Status == GattCommunicationStatus.Success || rd.Status == GattCommunicationStatus.ProtocolError)
                            answer = "contestó";
                        else if (rd.Status != GattCommunicationStatus.AccessDenied) answer = "no contestó (" + rd.Status + ")";
                        if (rd != null) readStatus = ", leer dio " + rd.Status;
                    }
                    if (answer == null)
                    {
                        var sv = AwaitOp(link.Device.GetGattServicesAsync(BluetoothCacheMode.Uncached),
                                         (int)Math.Max(4000, ProbeBudgetMs - clock.ElapsedMilliseconds), keeper.Connected, ConnectedGraceMs, keeper.Hold);
                        keeper.Hold(sv);
                        answer = sv == null ? "no contestó" : (sv.Status == GattCommunicationStatus.Success ? "contestó" : "no contestó (" + sv.Status + ")");
                    }
                    bool good = answer == "contestó";
                    if (good) ok++;
                    Log.Write("Prueba " + link.Name + " " + Log.Short(addr) + ": " + (good ? "se llega" : "NO se llega") +
                        " en " + clock.ElapsedMilliseconds + " ms, la tira " + answer + " (intento " + attempt + conn +
                        ": buscar " + keeper.MsFind + " ms, servicios " + link.MsGatt + " ms, respuesta " + answerClock.ElapsedMilliseconds + " ms; " +
                        (link.Melk ? "MELK" : "ELK") +
                        ", servicio " + svcId + ", característica " + charId +
                        ((p & GattCharacteristicProperties.Write) != 0 ? ", escritura con respuesta" : ", escritura sin respuesta") +
                        ((p & GattCharacteristicProperties.Read) != 0 ? ", se puede leer" : "") + readStatus +
                        (keeper.Scanned ? ", hubo que buscarla" : "") +
                        (session && keeper.Session == null ? ", no se pudo abrir la sesión" : "") +
                        (scan ? ", escuchando anuncios a la vez" : "") +
                        (session ? ", con MaintainConnection desde el principio" : "") +
                        (link.OpenStatus.Length > 0 ? ", abrir el servicio dio " + link.OpenStatus : "") + ")." + before);
                }
                finally
                {
                    if (watcher != null) Finder.StopScan(watcher);
                    if (link != null) link.Dispose();
                    Release(keeper);
                }
            }
            return ok == addrs.Count ? 0 : 3;
        }

        static string Uuid16(Guid uuid)
        {
            string u = uuid.ToString();
            return u.EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase) && u.StartsWith("0000")
                ? u.Substring(4, 4) : u;
        }

        /// <summary>
        /// Busca dónde escribir en la tira (la conexión es del Keeper). Primero con lo que Windows ya tiene guardado; con
        /// uncachedOnly, preguntando a la tira (si un intento falló, lo guardado puede estar viejo). Aquí es donde se
        /// espera a que Windows conecte, y se le deja casi todo el tiempo que quede, sin cortar.
        /// </summary>
        static StripLink Open(Keeper keeper, Stopwatch clock, int budgetMs, bool uncachedOnly, out string error)
        {
            error = "";
            var link = new StripLink { Address = keeper.Address, Keeper = keeper, Device = keeper.Device };
            var phase = Stopwatch.StartNew();
            try
            {
                if (link.Device == null) { error = "conexión cerrada"; return null; }
                link.Name = (link.Device.Name ?? "").Trim();
                link.Melk = link.Name.StartsWith("MELK", StringComparison.OrdinalIgnoreCase);

                // Lo guardado solo si ya está conectada: pedir lo guardado no hace que Windows conecte (según Microsoft
                // conectan MaintainConnection, pedir los servicios sin caché, o leer o escribir), y en la 1.1.4 una
                // petición así se quedó esperando 29 s sin que nadie conectara
                bool cachedFirst = !uncachedOnly && keeper.IsConnected;
                bool found = (cachedFirst && FindWritable(link, BluetoothCacheMode.Cached, clock, budgetMs, out error)) ||
                             (!Aborted() && FindWritable(link, BluetoothCacheMode.Uncached, clock, budgetMs, out error));
                link.MsGatt = phase.ElapsedMilliseconds;
                if (found) return link;
                error += " (servicios " + link.MsGatt + " ms" + (cachedFirst ? ", con lo guardado y sin caché" : ", sin caché") + ")";
                link.Dispose();
                return null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                link.Dispose();
                return null;
            }
        }

        static bool Aborted()
        {
            var a = _abort;
            return a != null && a.WaitOne(0);
        }

        static bool FindWritable(StripLink link, BluetoothCacheMode mode, Stopwatch clock, int budgetMs, out string error)
        {
            error = "";
            // Los de la vez anterior (lo guardado) no se cierran todavía: los guarda el Keeper (ver StripLink.Dispose)
            if (link.Keeper != null) link.Keeper.Hold(link.Services);
            else foreach (var s in link.Services) { try { s.Dispose(); } catch { } }
            link.Services.Clear();
            link.Char = null;

            // Si aún no hay conexión, Windows conecta ahora: se le deja todo el tiempo menos lo justo para escribir
            // después (antes se cortaba a los 3 s y luego a los 6). Si Windows se rinde antes (~7 s, Unreachable), el
            // siguiente intento lo vuelve a pedir enseguida.
            long left = budgetMs - clock.ElapsedMilliseconds;
            int wait = (int)Math.Max(1, Math.Max(Math.Min(left, 500), left - 1000));
            // Lo guardado solo se pide ya conectada, y no hace conectar a Windows: si no llega enseguida (0,3-0,8 s),
            // por ejemplo porque la tira se acaba de desconectar, no va a llegar (en la 1.1.4 esperó 29 s). Que quede
            // tiempo para pedirlo sin caché.
            if (mode == BluetoothCacheMode.Cached) wait = Math.Min(wait, ConnectedGraceMs);
            // En cuanto Windows dice que está conectada, la respuesta debería llegar enseguida: si se atasca, se corta y
            // el siguiente intento la pide otra vez (la conexión sigue abierta, así que va rápido)
            var res = AwaitOp(link.Device.GetGattServicesAsync(mode), wait,
                              link.Keeper != null ? link.Keeper.Connected : null, ConnectedGraceMs,
                              link.Keeper != null ? (Action<GattDeviceServicesResult>)link.Keeper.Hold : null);
            if (res == null) { error = "sin respuesta al pedir sus servicios"; return false; }
            if (res.Services != null) link.Services.AddRange(res.Services);
            if (res.Status != GattCommunicationStatus.Success)
            {
                error = "servicios: " + res.Status;
                return false;
            }

            // Primero los servicios de tiras LED conocidos; los genéricos (1800, 1801, 180A…) no hace falta mirarlos
            var ordered = new List<GattDeviceService>();
            foreach (var s in link.Services) if (Rank(Services, s.Uuid) < 99) ordered.Add(s);
            ordered.Sort((a, b) => Rank(Services, a.Uuid).CompareTo(Rank(Services, b.Uuid)));
            foreach (var s in link.Services) if (Rank(Services, s.Uuid) == 99 && !IsStandard(s.Uuid)) ordered.Add(s);

            GattCharacteristic best = null;
            int bestRank = int.MaxValue;
            string openStatus = "";
            foreach (var svc in ordered)
            {
                if (Left(clock, budgetMs, 3000) < 200 || Aborted()) break;
                // Abrir el servicio en modo compartido antes de mirar sus características, como hace Chrome: así
                // podemos escribir aunque la ventana de LotusLED tenga la tira (Windows comparte la conexión).
                string st = OpenShared(svc, Left(clock, budgetMs, 1500));
                var chars = AwaitOp(svc.GetCharacteristicsAsync(mode), Left(clock, budgetMs, 3000));
                if (chars == null || chars.Status != GattCommunicationStatus.Success || chars.Characteristics == null) continue;
                foreach (var ch in chars.Characteristics)
                {
                    var p = ch.CharacteristicProperties;
                    if ((p & (GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse)) == 0) continue;
                    int rank = Rank(WriteChars, ch.Uuid);
                    if (rank == 99 && ch.Uuid.ToString().StartsWith(NordicUartRx, StringComparison.OrdinalIgnoreCase)) rank = 50;
                    if (rank < bestRank) { best = ch; bestRank = rank; openStatus = st; }
                }
                if (bestRank == 0) break; // FFF3: la de las ELK-BLEDOM
            }
            if (best == null || bestRank == 99)
            {
                error = "no tiene ninguna característica de tira LED conocida";
                return false;
            }
            link.Char = best;
            link.OpenStatus = openStatus;
            return true;
        }

        /// <summary>Abre el servicio compartido. Devuelve "" si fue bien, o lo que dijo Windows.</summary>
        static string OpenShared(GattDeviceService svc, int timeoutMs)
        {
            try
            {
                var open = AwaitOp(svc.OpenAsync(GattSharingMode.SharedReadAndWrite), timeoutMs);
                if (open == GattOpenStatus.Success || open == GattOpenStatus.AlreadyOpened) return "";
                return open == GattOpenStatus.Unspecified ? "sin respuesta" : open.ToString();
            }
            catch { return ""; /* Windows 10 anterior a 1803: no existe OpenAsync */ }
        }

        /// <summary>Manda encender o apagar. Devuelve false (y por qué) si no hay forma de saber que llegó.</summary>
        static bool Send(StripLink link, bool on, int timeoutMs, Func<bool> cancel, out string why)
        {
            why = "";
            if (link == null || link.Char == null) { why = "sin característica"; return false; }
            if (on)
            {
                lock (Gate)
                {
                    if ((cancel != null && cancel()) || Aborted()) { why = "cancelado"; return false; }
                    return Write(link, ElkOrMelk(link, true), timeoutMs, cancel, out why);
                }
            }
            return Write(link, ElkOrMelk(link, false), timeoutMs, cancel, out why);
        }

        static byte[] ElkOrMelk(StripLink link, bool on)
        {
            if (link.Melk) return on ? MelkOn : MelkOff;
            return on ? ElkOn : ElkOff;
        }

        /// <summary>
        /// Escribe la orden, sin pasarse de timeoutMs en total. La primera escritura puede tener que esperar a que
        /// Windows conecte con la tira (lo guardado no conecta), así que se le deja casi todo el tiempo.
        /// </summary>
        static bool Write(StripLink link, byte[] frame, int timeoutMs, Func<bool> cancel, out string why)
        {
            why = "";
            var ch = link.Char;
            var p = ch.CharacteristicProperties;
            bool canAck = (p & GattCharacteristicProperties.Write) != 0;
            bool canFast = (p & GattCharacteristicProperties.WriteWithoutResponse) != 0;
            var clock = Stopwatch.StartNew();
            Func<int> left = () => (int)(timeoutMs - clock.ElapsedMilliseconds);
            Func<bool> stop = () => (cancel != null && cancel()) || Aborted();
            WaitHandle connected = link.Keeper != null ? link.Keeper.Connected : null;

            // Antes de cada trama se mira si hay que parar: al encender, una que saliera después del aviso de
            // Windows podría llegar a la tira detrás del "apagar".
            if (link.Melk)
            {
                foreach (var init in MelkInit)
                {
                    if (stop()) { why = "cancelado"; return false; }
                    WriteOnce(ch, init, canFast ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse, Math.Max(200, Math.Min(2500, left() - 300)), connected);
                }
            }

            if (canAck)
            {
                if (stop()) { why = "cancelado"; return false; }
                // Si también admite sin respuesta, se le guarda un tercio del tiempo por si esta no contesta
                int ackTime = canFast ? Math.Max(200, left() * 2 / 3) : Math.Max(200, left());
                if (WriteOnce(ch, frame, GattWriteOption.WriteWithResponse, ackTime, connected))
                    return true;
                if (!canFast || stop()) { why = stop() ? "cancelado" : "la tira no confirmó la orden"; return false; }
            }
            if (!canFast) { why = "la característica no admite escritura"; return false; }

            // Sin respuesta no hay confirmación (las ELK-BLEDOM suelen ser así): se manda dos veces y luego se lee
            // la característica. Como todo viaja en orden, si la tira contesta a la lectura es que las órdenes llegaron.
            if (stop()) { why = "cancelado"; return false; }
            bool ok = WriteOnce(ch, frame, GattWriteOption.WriteWithoutResponse, Math.Max(200, left() - 300), connected);
            if (stop()) { why = "cancelado"; return false; }
            Thread.Sleep(60);
            if (stop()) { why = "cancelado"; return false; }
            ok = WriteOnce(ch, frame, GattWriteOption.WriteWithoutResponse, Math.Max(150, Math.Min(600, left() - 200)), connected) || ok;
            if (!ok) { why = stop() ? "cancelado" : "no se pudo escribir"; return false; }

            if ((p & GattCharacteristicProperties.Read) != 0)
            {
                GattReadResult rd = null;
                try { rd = AwaitOp(ch.ReadValueAsync(BluetoothCacheMode.Uncached), Math.Max(150, Math.Min(1500, left()))); }
                catch { }
                if (rd == null) { why = stop() ? "cancelado" : "sin confirmar: la tira no contestó"; return false; }
                if (rd.Status == GattCommunicationStatus.Unreachable) { why = "sin confirmar: se perdió la conexión"; return false; }
                // Success, o un error de la propia tira (ProtocolError): contestó, así que lo anterior llegó.
                // AccessDenied lo dice Windows sin preguntar a la tira: no confirma nada, se hace como si no se pudiera leer.
                if (rd.Status != GattCommunicationStatus.AccessDenied) return true;
            }
            // No se puede leer: unos cuantos intervalos de conexión para que salga por la antena
            Thread.Sleep(Math.Max(0, Math.Min(350, left())));
            return true;
        }

        static bool WriteOnce(GattCharacteristic ch, byte[] frame, GattWriteOption opt, int timeoutMs, WaitHandle connected)
        {
            try
            {
                var dw = new DataWriter();
                dw.WriteBytes(frame);
                var res = AwaitOp(ch.WriteValueWithResultAsync(dw.DetachBuffer(), opt), timeoutMs, connected, ConnectedGraceMs);
                return res != null && res.Status == GattCommunicationStatus.Success;
            }
            catch { return false; }
        }

        static int Rank(string[] list, Guid uuid)
        {
            string u = uuid.ToString().ToLowerInvariant();
            if (!u.EndsWith("-0000-1000-8000-00805f9b34fb") || !u.StartsWith("0000")) return 99;
            string shortId = u.Substring(4, 4);
            int i = Array.IndexOf(list, shortId);
            return i < 0 ? 99 : i;
        }

        static bool IsStandard(Guid uuid)
        {
            string u = uuid.ToString().ToLowerInvariant();
            return u.StartsWith("00001800") || u.StartsWith("00001801") || u.StartsWith("0000180a") || u.StartsWith("0000180f");
        }

        static int Left(Stopwatch clock, int budgetMs, int cap)
        {
            long left = budgetMs - clock.ElapsedMilliseconds;
            return (int)Math.Max(1, Math.Min(cap, left));
        }

        /// <summary>
        /// Espera una operación de WinRT con tiempo límite (y, en los hilos que encienden, hasta que Windows avise
        /// del apagado). Si no termina, la cancela, y si aun así termina más tarde, cierra lo que devuelva: si no,
        /// Windows dejaría la conexión con la tira abierta y la tira dejaría de anunciarse (ya pasó con LotusLED.exe).
        /// </summary>
        public static T AwaitOp<T>(IAsyncOperation<T> op, int timeoutMs)
        {
            return AwaitOp(op, timeoutMs, null, 0);
        }

        /// <summary>
        /// Igual, pero cuando se activa hint (la tira ya está conectada) solo se espera graceMs más: conectada, la
        /// operación debería acabar enseguida, y si no, más vale empezar otra que seguir esperando a una atascada.
        /// Con late, lo que llegue tarde se le pasa a late en vez de cerrarlo (los servicios, al Keeper).
        /// </summary>
        public static T AwaitOp<T>(IAsyncOperation<T> op, int timeoutMs, WaitHandle hint, int graceMs, Action<T> late = null)
        {
            if (op == null) return default(T);
            var done = new ManualResetEvent(false);
            var gate = new object();
            bool abandoned = false, finished = false;
            T result = default(T);
            op.Completed = (info, status) =>
            {
                T r = default(T);
                if (status == AsyncStatus.Completed) { try { r = info.GetResults(); } catch { } }
                bool isLate;
                lock (gate)
                {
                    isLate = abandoned;
                    if (!isLate) { result = r; finished = true; try { done.Set(); } catch { } }
                }
                if (isLate)
                {
                    if (late != null) { try { late(r); } catch { } }
                    else DisposeLate(r);
                    try { done.Close(); } catch { }
                }
            };
            var abort = _abort;
            try
            {
                var waitClock = Stopwatch.StartNew();
                int total = Math.Max(1, timeoutMs);
                var handles = new List<WaitHandle> { done };
                if (abort != null) handles.Add(abort);
                int hintIndex = -1;
                if (hint != null) { hintIndex = handles.Count; handles.Add(hint); }
                int got = WaitHandle.WaitAny(handles.ToArray(), total);
                if (hintIndex >= 0 && got == hintIndex)
                {
                    handles.RemoveAt(hintIndex);
                    int rest = (int)Math.Max(1, Math.Min(graceMs, total - waitClock.ElapsedMilliseconds));
                    WaitHandle.WaitAny(handles.ToArray(), rest);
                }
            }
            catch { }
            lock (gate)
            {
                if (finished)
                {
                    try { done.Close(); } catch { }
                    return result;
                }
                abandoned = true; // a partir de aquí, el que cierre "done" (y lo que llegue) es la llamada de vuelta
            }
            try { op.Cancel(); } catch { }
            return default(T);
        }

        static void DisposeLate(object o)
        {
            if (o == null) return;
            try
            {
                var svcs = o as GattDeviceServicesResult;
                if (svcs != null)
                {
                    if (svcs.Services != null) foreach (var s in svcs.Services) { try { s.Dispose(); } catch { } }
                    return;
                }
                var d = o as IDisposable;
                if (d != null) d.Dispose();
            }
            catch { }
        }
    }

    // ========================================================================================================
    // Buscar tiras cercanas (para que Instalar pueda preguntar cuál es la tuya)
    // ========================================================================================================
    static class Finder
    {
        static readonly string[] Prefixes = { "L7", "ELK", "MELK", "BLEDOM", "LEDBLE", "BLE-LED", "LED", "Lotus", "QHM", "BJ_LED", "Triones", "LEDnet" };
        static readonly string[] LedServices = { "0000fff0", "0000ffe0", "0000ffd5", "0000ffd0", "0000ffe5" };

        /// <summary>Escucha anuncios hasta ver esa dirección (o hasta que pase el tiempo o se active abort). Así Windows la vuelve a conocer.</summary>
        public static bool WaitForAdvert(ulong addr, int timeoutMs, WaitHandle abort)
        {
            if (timeoutMs < 300) return false;
            using (var seen = new ManualResetEvent(false))
            {
                bool stopped = false;
                var stopLock = new object();
                var watcher = new BluetoothLEAdvertisementWatcher();
                watcher.ScanningMode = BluetoothLEScanningMode.Active;
                watcher.Received += (s, e) =>
                {
                    if (e.BluetoothAddress != addr) return;
                    lock (stopLock) { if (!stopped) { try { seen.Set(); } catch { } } }
                };
                try
                {
                    watcher.Start();
                    bool ok;
                    if (abort == null) ok = seen.WaitOne(timeoutMs);
                    else ok = WaitHandle.WaitAny(new WaitHandle[] { seen, abort }, timeoutMs) == 0;
                    if (ok) Thread.Sleep(150); // que llegue también la respuesta al escaneo (con el nombre)
                    return ok;
                }
                catch { return false; }
                finally
                {
                    try { watcher.Stop(); } catch { }
                    lock (stopLock) { stopped = true; } // un anuncio que llegue tarde ya no toca "seen"
                }
            }
        }

        /// <summary>Empieza a escuchar anuncios (activo) y lo deja así hasta StopScan. null si no se pudo.</summary>
        public static BluetoothLEAdvertisementWatcher StartScan()
        {
            try
            {
                var w = new BluetoothLEAdvertisementWatcher();
                w.ScanningMode = BluetoothLEScanningMode.Active;
                w.Start();
                return w;
            }
            catch { return null; }
        }

        public static void StopScan(BluetoothLEAdvertisementWatcher w)
        {
            if (w == null) return;
            try { w.Stop(); } catch { }
        }

        public static int Run(string outFile, int seconds)
        {
            var found = new Dictionary<ulong, KeyValuePair<string, short>>();
            var watcher = new BluetoothLEAdvertisementWatcher();
            watcher.ScanningMode = BluetoothLEScanningMode.Active;
            watcher.Received += (s, e) =>
            {
                string name = (e.Advertisement.LocalName ?? "").Trim();
                bool led = false;
                foreach (var p in Prefixes) if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) led = true;
                if (!led && e.Advertisement.ServiceUuids != null)
                    foreach (var u in e.Advertisement.ServiceUuids)
                        foreach (var ls in LedServices) if (u.ToString().StartsWith(ls, StringComparison.OrdinalIgnoreCase)) led = true;
                if (!led) return;
                lock (found)
                {
                    KeyValuePair<string, short> prev;
                    if (found.TryGetValue(e.BluetoothAddress, out prev))
                    {
                        if (name.Length == 0) name = prev.Key;
                        if (prev.Value > e.RawSignalStrengthInDBm) found[e.BluetoothAddress] = new KeyValuePair<string, short>(name, prev.Value);
                        else found[e.BluetoothAddress] = new KeyValuePair<string, short>(name, e.RawSignalStrengthInDBm);
                    }
                    else found[e.BluetoothAddress] = new KeyValuePair<string, short>(name, e.RawSignalStrengthInDBm);
                }
            };
            try
            {
                watcher.Start();
                Thread.Sleep(Math.Max(2, Math.Min(60, seconds)) * 1000);
                watcher.Stop();
            }
            catch (Exception ex)
            {
                Log.Write("No se pudo buscar tiras: " + ex.Message);
                try { File.WriteAllText(outFile, "#error|" + ex.Message.Replace("\r", " ").Replace("\n", " "), Encoding.UTF8); } catch { }
                return 1;
            }

            var lines = new List<string>();
            lock (found)
            {
                var list = new List<KeyValuePair<ulong, KeyValuePair<string, short>>>(found);
                list.Sort((a, b) => b.Value.Value.CompareTo(a.Value.Value)); // la más cercana primero
                foreach (var kv in list)
                    lines.Add(Config.FormatMac(kv.Key) + "|" + (kv.Value.Key.Length > 0 ? kv.Value.Key : "(sin nombre)") + "|" + kv.Value.Value);
            }
            File.WriteAllLines(outFile, lines.ToArray(), Encoding.UTF8);
            return 0;
        }
    }

    // ========================================================================================================
    // Configuración y registro
    // ========================================================================================================

    /// <summary>Lo que el ayudante necesita saber al apagar, leído de antemano.</summary>
    class Snapshot
    {
        public List<ulong> Addrs = new List<ulong>();
        public bool PowerOnStart = true;
        public bool PowerOff = true;
        public bool LightsOn = true;
        public bool ReadError;      // no se pudo leer device.cfg (se usaron las tiras de antes o la copia local)
    }

    static class Config
    {
        public static string ExeDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }

        public static string DataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LotusLED"); }
        }

        // Copia local de las MAC: device.cfg está en la carpeta del proyecto (OneDrive). Si un día no se puede
        // leer (OneDrive sin arrancar o el archivo solo en la nube), el ayudante sigue sabiendo qué tiras son.
        static string CachePath { get { return Path.Combine(DataDir, "ultimas-tiras.cfg"); } }

        static string[] WatchedFiles
        {
            get
            {
                return new[] { Path.Combine(ExeDir, "device.cfg"), Path.Combine(DataDir, "device.cfg"),
                               Path.Combine(DataDir, "auto.cfg"), Path.Combine(DataDir, "settings.cfg") };
            }
        }

        /// <summary>Huella de los archivos de configuración (fecha y tamaño), para releerlos solo si cambian.</summary>
        public static string Stamp()
        {
            var sb = new StringBuilder();
            foreach (var f in WatchedFiles)
            {
                try
                {
                    var fi = new FileInfo(f);
                    sb.Append(fi.Exists ? fi.LastWriteTimeUtc.Ticks + ":" + fi.Length : "-").Append('|');
                }
                catch { sb.Append("?|"); }
            }
            return sb.ToString();
        }

        public static Snapshot Load(IEnumerable<string> extra, Snapshot previous)
        {
            var s = new Snapshot();
            bool readError;
            s.Addrs = ReadAddresses(extra, out readError);
            s.ReadError = readError;
            // Si no se pudo leer device.cfg, mejor las tiras de antes que ninguna
            if (readError && s.Addrs.Count == 0 && previous != null) s.Addrs = new List<ulong>(previous.Addrs);
            s.PowerOnStart = Flag("PowerOnStart", true);
            s.PowerOff = Flag("PowerOffShutdown", true);
            s.LightsOn = Flag("LightsOn", true);
            return s;
        }

        /// <summary>MAC de los argumentos y de device.cfg (junto al .exe y en %LOCALAPPDATA%\LotusLED); si no hay, la de settings.cfg.</summary>
        public static List<ulong> Addresses(IEnumerable<string> extra)
        {
            bool readError;
            return ReadAddresses(extra, out readError);
        }

        static List<ulong> ReadAddresses(IEnumerable<string> extra, out bool readError)
        {
            readError = false;
            var list = new List<ulong>();
            try
            {
                if (extra != null) foreach (var a in extra) Add(list, ParseMac(a));
                var fromFiles = new List<ulong>();
                bool exists = ReadDeviceCfg(Path.Combine(ExeDir, "device.cfg"), fromFiles, ref readError);
                exists = ReadDeviceCfg(Path.Combine(DataDir, "device.cfg"), fromFiles, ref readError) || exists;
                // La copia local guarda lo de device.cfg (aunque alguna MAC también venga por argumento)
                if (exists && !readError) SaveCache(fromFiles);
                foreach (var a in fromFiles) Add(list, a);
                if (list.Count > 0) return list;

                if (readError)
                {
                    bool ignore = false;
                    ReadDeviceCfg(CachePath, list, ref ignore);
                    return list;
                }
                // Un device.cfg sin ninguna MAC (en Instalar se eligió "ninguna") quiere decir eso: ninguna
                if (exists) return list;

                // Solo si no hay device.cfg: la última tira de LotusLED.exe. Puede ser la de un vecino (ya pasó con
                // una L7179), y una tira ausente retrasa a las demás (Windows tarda 7 s en rendirse con cada una).
                string cfg = Path.Combine(DataDir, "settings.cfg");
                if (File.Exists(cfg))
                {
                    foreach (var line in File.ReadAllLines(cfg))
                    {
                        ulong v;
                        if (line.StartsWith("LastAddress=") && ulong.TryParse(line.Substring("LastAddress=".Length).Trim(), out v))
                            Add(list, v);
                    }
                }
            }
            catch { }
            return list;
        }

        /// <summary>Añade las MAC del archivo. Devuelve si existe; si existe pero no se pudo leer, readError.</summary>
        static bool ReadDeviceCfg(string path, List<ulong> list, ref bool readError)
        {
            string[] lines;
            try
            {
                // Sin File.Exists antes: da false también si no se puede acceder, y eso es un error de lectura
                lines = File.ReadAllLines(path);
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch { readError = true; return true; }
            foreach (var line in lines)
            {
                string l = line.Trim();
                if (l.Length == 0 || l.StartsWith("#")) continue;
                // Se admite "AA:BB:CC:DD:EE:FF  # comentario" o "AA:BB:CC:DD:EE:FF|nombre"
                int cut = l.IndexOfAny(new[] { '#', '|', ' ', '\t', ';' });
                if (cut > 0) l = l.Substring(0, cut);
                Add(list, ParseMac(l));
            }
            return true;
        }

        static void SaveCache(List<ulong> addrs)
        {
            try
            {
                var sb = new StringBuilder("# Copia de device.cfg que guarda LotusShutdown.exe por si un día no se puede leer el original\r\n");
                foreach (var a in addrs) sb.Append(FormatMac(a)).Append("\r\n");
                string text = sb.ToString();
                if (File.Exists(CachePath) && File.ReadAllText(CachePath) == text) return;
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(CachePath, text);
            }
            catch { }
        }

        static void Add(List<ulong> list, ulong addr)
        {
            if (addr != 0 && !list.Contains(addr)) list.Add(addr);
        }

        /// <summary>Opciones que guarda la interfaz en %LOCALAPPDATA%\LotusLED\auto.cfg (por defecto, todo activado).</summary>
        public static bool Flag(string key, bool def)
        {
            try
            {
                string path = Path.Combine(DataDir, "auto.cfg");
                if (!File.Exists(path)) return def;
                foreach (var line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (!string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;
                    bool v;
                    if (bool.TryParse(line.Substring(eq + 1).Trim(), out v)) return v;
                }
            }
            catch { }
            return def;
        }

        /// <summary>Cambia una opción de auto.cfg dejando las demás como estaban (si ya tiene ese valor, no escribe).</summary>
        public static void SetFlag(string key, bool value)
        {
            try
            {
                string path = Path.Combine(DataDir, "auto.cfg");
                var lines = new List<string>();
                if (File.Exists(path)) lines.AddRange(File.ReadAllLines(path));
                string wanted = key + "=" + (value ? "True" : "False");
                bool found = false, changed = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    int eq = lines[i].IndexOf('=');
                    if (eq <= 0 || !string.Equals(lines[i].Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;
                    found = true;
                    if (lines[i].Trim() != wanted) { lines[i] = wanted; changed = true; }
                }
                if (!found) { lines.Add(wanted); changed = true; }
                if (!changed) return;
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(path, string.Join("\r\n", lines.ToArray()) + "\r\n");
            }
            catch { }
        }

        /// <summary>Acepta "AA:BB:CC:DD:EE:FF", "AA-BB-…", "AABBCCDDEEFF" o el número decimal de settings.cfg.</summary>
        public static ulong ParseMac(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            string t = text.Trim();
            string hex = t.Replace(":", "").Replace("-", "");
            bool looksHex = t.Contains(":") || t.Contains("-") ||
                            (hex.Length == 12 && System.Text.RegularExpressions.Regex.IsMatch(hex, "[A-Fa-f]"));
            ulong v;
            if (looksHex)
            {
                if (hex.Length != 12) return 0;
                return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out v) ? v : 0;
            }
            return ulong.TryParse(t, out v) && v <= 0xFFFFFFFFFFFFUL ? v : 0;
        }

        public static string FormatMac(ulong a)
        {
            var sb = new StringBuilder();
            for (int i = 5; i >= 0; i--)
            {
                sb.Append(((a >> (8 * i)) & 0xFF).ToString("X2"));
                if (i > 0) sb.Append(':');
            }
            return sb.ToString();
        }
    }

    static class Log
    {
        static readonly object Lock = new object();

        public static void Write(string msg)
        {
            lock (Lock)
            {
                try
                {
                    Directory.CreateDirectory(Config.DataDir);
                    string path = Path.Combine(Config.DataDir, "LotusShutdown.log");
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 256 * 1024)
                    {
                        string old = path + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + "\r\n", Encoding.UTF8);
                }
                catch { }
            }
        }

        /// <summary>La MAC sin los tres primeros bytes, por si alguien comparte el registro.</summary>
        public static string Short(ulong a)
        {
            string m = Config.FormatMac(a);
            return "…" + m.Substring(9);
        }

        public static string List(List<ulong> addrs)
        {
            if (addrs == null || addrs.Count == 0) return "ninguna";
            var parts = new List<string>();
            foreach (var a in addrs) parts.Add(Short(a));
            return string.Join(", ", parts.ToArray());
        }

        public static void Results(string what, Dictionary<ulong, StripResult> res)
        {
            var parts = new List<string>();
            foreach (var r in res.Values)
            {
                string who = (r.Name.Length > 0 ? r.Name + " " : "") + Short(r.Address);
                parts.Add(who + (r.Ok ? ": OK en " + r.Ms + " ms (" + r.Detail + ")" : ": FALLÓ (" + r.Detail + ")") +
                          (r.Attempts > 1 ? ", " + r.Attempts + " intentos" : ""));
            }
            Write(what + " -> " + string.Join("; ", parts.ToArray()));
        }
    }

    static class Native
    {
        public const int WM_CLOSE = 0x0010;
        public const int WM_SETICON = 0x0080;
        public const int WM_QUERYENDSESSION = 0x0011;
        public const int WM_ENDSESSION = 0x0016;
        public const long ENDSESSION_CLOSEAPP = 0x00000001;
        public const long ENDSESSION_CRITICAL = 0x40000000L;
        public const long ENDSESSION_LOGOFF = 0x80000000L;
        public const int SM_SHUTTINGDOWN = 0x2000;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, int length, out int returnLength);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessShutdownParameters(uint dwLevel, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string pwszReason);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        /// <summary>
        /// Espera sin procesar mensajes de ventana (las esperas normales de .NET en un hilo STA sí los procesan y
        /// podrían volver a entrar en la ventana a mitad del apagado). Devuelve true si se activó.
        /// </summary>
        public static bool Wait(ManualResetEvent ev, int timeoutMs)
        {
            uint r = WaitForSingleObject(ev.SafeWaitHandle.DangerousGetHandle(), (uint)Math.Max(0, timeoutMs));
            GC.KeepAlive(ev);
            return r == 0;
        }
    }
}
