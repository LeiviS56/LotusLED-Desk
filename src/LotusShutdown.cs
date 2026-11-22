using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
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
//                                  de la tira) sin cambiar las luces, y lo apunta en el registro.
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
[assembly: AssemblyVersion("1.1.2.0")]
[assembly: AssemblyFileVersion("1.1.2.0")]

namespace LotusLED.Shutdown
{
    static class Helper
    {
        const string MutexName = "Local\\LotusLED_Ayudante_Luces";
        const string PowerOnEventName = "Local\\LotusLED_Ayudante_Encender";
        // Existe solo mientras el ayudante va a apagar alguna tira al apagar el PC (hay MAC y la opción está
        // activada). LotusLED.ps1 lo mira: si no existe, apaga la ventana web, como antes de haber ayudante.
        const string ArmedName = "Local\\LotusLED_Ayudante_Apaga";

        [STAThread]
        static int Main(string[] args)
        {
            bool watch = false, on = false, task = false, probe = false;
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
                if (probe) return Lights.Probe(Config.Addresses(macs));
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
        /// encarga. Si no, este proceso hace lo mismo que él: ventana oculta, nivel de apagado bajo y motivo de
        /// bloqueo, para que Windows no lo cierre a medias (antes moría con 0xC000013A sin llegar a apagar).
        /// </summary>
        static int RunTask(List<string> macs)
        {
            Mutex m;
            if (Mutex.TryOpenExisting(MutexName, out m))
            {
                m.Dispose();
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
            var res = Lights.SetAll(addrs, on, 20000, null, false);
            Log.Results(on ? "Encender" : "Apagar", res);
            return Lights.CountOk(res) > 0 ? 0 : 3;
        }

        internal static string MutexNameForGuard { get { return MutexName; } }
        internal static string PowerOnEventNameForGuard { get { return PowerOnEventName; } }
        internal static string ArmedNameForGuard { get { return ArmedName; } }
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
        const int OffBudgetMs = 6000;       // lo normal es 0,3-3 s; si Windows tiene que volver a buscar la tira, más
        const int EndWaitMs = OffBudgetMs + 1300; // lo máximo que retenemos el apagado, desde la pregunta
        // La tarea de respaldo empieza antes (al pedirse el apagado, mientras Windows cierra los demás programas),
        // así que puede insistir más; en WM_ENDSESSION retiene lo mismo que el ayudante (EndWaitMs como mucho).
        const int TaskBudgetMs = 15000;
        const int StartWindowMs = 180000;   // al iniciar sesión, cuánto tiempo seguimos intentando encender
        const int ResumeCheckMs = 30000;    // si seguimos vivos tanto después de WM_ENDSESSION, el apagado se canceló

        public const int WM_APP_OFFDONE = 0x8001;
        public const int WM_APP_RESUMECHECK = 0x8002;

        static Mutex _mutex;
        static EventWaitHandle _powerOnEvent;
        static GuardWindow _window;
        static GuardMode _mode;
        static List<string> _macs;
        static volatile Snapshot _cfg;          // opciones y tiras, leídas antes: en el apagado no se toca el disco
        static volatile bool _ending;           // Windows ha avisado de que se termina la sesión
        static OffRound _round;                 // el apagado en curso (solo se toca desde el hilo de la ventana)
        static int _roundCount;                 // para saber de qué apagado habla un aviso que llega tarde
        static bool _reason;                    // hay motivo de bloqueo puesto
        static int _powerOnRunning;             // 1 mientras se intenta encender
        static string _stamp;                   // huella de los archivos de configuración de la última lectura
        static EventWaitHandle _armed;          // abierto mientras hay tiras que apagar (lo mira LotusLED.ps1)
        static volatile int _exitCode;          // de la tarea de respaldo: 0 si apagó todas (o no tenía que apagar)

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
        }

        public static int Run(List<string> macs, GuardMode mode)
        {
            // La tarea de respaldo no se queda con el mutex del ayudante: si a la vez arranca un "-watch -on" (se
            // apaga justo al iniciar sesión y luego se cancela), ese tiene que poder quedarse vigilando.
            if (mode != GuardMode.OffNow)
            {
                // El aviso para encender existe antes que el mutex: quien vea el mutex ya puede usarlo
                _powerOnEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Helper.PowerOnEventNameForGuard);
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
                StartOff(_window.Handle, "Se pide apagar o reiniciar");
            }
            else
            {
                UpdateArmed(_cfg);
                Log.Write("Ayudante en marcha (" + Config.ExeDir + "). Tiras: " + Log.List(_cfg.Addrs) +
                          (_cfg.Addrs.Count == 0 ? "  <- falta la MAC en device.cfg" : ""));
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
            }

            Application.Run();
            GC.KeepAlive(_mutex);
            GC.KeepAlive(_powerOnEvent);
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
                if (now == stamp) continue;
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
        static void PowerOnAtStart()
        {
            if (Interlocked.Exchange(ref _powerOnRunning, 1) == 1) return; // ya se está intentando (con lo de ahora)
            try
            {
                var clock = Stopwatch.StartNew();
                var done = new List<ulong>();
                var pending = new List<ulong>();
                Func<bool> stop = () => _ending;
                bool loggedNoMac = false;
                for (int round = 1; !_ending && clock.ElapsedMilliseconds < StartWindowMs; round++)
                {
                    var cfg = _cfg;
                    if (cfg == null || !cfg.PowerOnStart)
                    {
                        Log.Write("Encender al iniciar está desactivado en la interfaz.");
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
                    var res = Lights.SetAll(pending, true, 20000, stop, true);
                    if (_ending) break; // Windows se apaga: lo que pase con estas órdenes ya no cuenta
                    foreach (var r in res.Values)
                        if (r.Ok) { done.Add(r.Address); pending.Remove(r.Address); }
                    if (round == 1 || pending.Count == 0) Log.Results("Encender al iniciar (intento " + round + ")", res);
                    if (pending.Count == 0) continue; // por si entretanto apareció otra tira; si no, termina arriba
                    for (int i = 0; i < 50 && !_ending; i++) Thread.Sleep(100);
                }
                if (done.Count > 0 && !_ending) NoteLightsOn();
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
            var old = _round;
            if (old != null)
            {
                if (old.EndSeen || Native.Wait(old.Done, 0))
                {
                    // Nos vuelven a preguntar y seguimos vivos: aquel apagado no siguió (y su aviso de "seguimos en
                    // marcha" aún no ha llegado). Empieza uno nuevo; no se vuelve a encender, nos estamos apagando.
                    old.Cancelled = true;
                    _round = null;
                }
                else
                {
                    // El de antes sigue apagando: vale para este también, pero sus avisos atrasados ya no
                    old.Id = ++_roundCount;
                    if (_mode != GuardMode.OffNow) ScheduleResumeCheck(hwnd, old.Id, EndWaitMs + ResumeCheckMs);
                    return;
                }
            }
            StartOff(hwnd, "Windows avisa de " + kind);
        }

        static void StartOff(IntPtr hwnd, string why)
        {
            if (_round != null) return; // ya empezó en esta ronda
            var round = new OffRound { Id = ++_roundCount };
            _round = round;
            // Después de la pregunta siempre llega WM_ENDSESSION; si por lo que sea no llegara y seguimos vivos,
            // que este apagado no se quede a medias para siempre (los siguientes no apagarían).
            if (_mode != GuardMode.OffNow) ScheduleResumeCheck(hwnd, round.Id, EndWaitMs + ResumeCheckMs);
            var cfg = _cfg;
            if (cfg == null || !cfg.PowerOff || cfg.Addrs.Count == 0)
            {
                bool disabled = cfg != null && !cfg.PowerOff;
                string note = disabled ? ". Apagar las luces está desactivado en la interfaz." : ", pero no hay ninguna MAC en device.cfg.";
                _exitCode = disabled ? 0 : 2;
                round.Done.Set();
                StartThread(() => Log.Write(why + note));
                if (_mode == GuardMode.OffNow) Native.PostMessage(hwnd, WM_APP_OFFDONE, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            if (!_reason)
            {
                try { _reason = Native.ShutdownBlockReasonCreate(hwnd, "Apagando las luces LED…"); } catch { }
            }

            var addrs = new List<ulong>(cfg.Addrs);
            round.Addrs = addrs;
            var mode = _mode;
            int fullBudget = mode == GuardMode.OffNow ? TaskBudgetMs : OffBudgetMs;
            var t = new Thread(() =>
            {
                try
                {
                    Log.Write(why + ". Apagando " + Log.List(addrs) + "…");
                    // Una orden de encender a medias se corta al momento (Lights.Abort); se espera a que suelte el
                    // turno y se guarda hasta el final, para que ninguna salga después del apagado.
                    bool gate = Monitor.TryEnter(Lights.Gate, 1500);
                    try
                    {
                        int budget = (int)Math.Max(1500, fullBudget - round.Clock.ElapsedMilliseconds);
                        var res = Lights.SetAll(addrs, false, budget, () => round.Cancelled, false);
                        round.Result = res;
                        _exitCode = Lights.CountOk(res) == res.Count ? 0 : 3;
                        Log.Results(round.Cancelled ? "Apagar (cortado: el apagado no siguió)" : "Apagar", res);
                    }
                    finally { if (gate) Monitor.Exit(Lights.Gate); }
                }
                catch (Exception ex) { _exitCode = 1; Log.Write("Error al apagar: " + ex.Message); }
                finally
                {
                    try { round.Done.Set(); } catch { }
                    if (mode == GuardMode.OffNow) Native.PostMessage(hwnd, WM_APP_OFFDONE, IntPtr.Zero, IntPtr.Zero);
                }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        public static bool InRound { get { return _round != null; } }

        /// <summary>WM_ENDSESSION: si la sesión termina, esperamos a que el apagado acabe; si se canceló, vuelve todo.</summary>
        public static void OnEndSession(IntPtr hwnd, bool ending, long lParam)
        {
            if (!ending)
            {
                Resume(hwnd, "Windows canceló el apagado.");
                return;
            }

            // Apagado forzado sin pregunta previa: empezamos ahora
            BeginEnding();
            if (_round == null) StartOff(hwnd, "Windows termina la sesión sin preguntar");
            var round = _round;
            round.EndSeen = true;
            long elapsed = round.Clock.ElapsedMilliseconds;
            // El ayudante cuenta desde la pregunta; la tarea empezó antes y puede ir más allá, pero sin retener a
            // Windows más que el ayudante.
            int left = _mode == GuardMode.OffNow
                ? (int)Math.Max(0, Math.Min(EndWaitMs, TaskBudgetMs + 1300 - elapsed))
                : (int)Math.Max(0, EndWaitMs - elapsed);
            bool finished = Native.Wait(round.Done, left);
            ClearReason(hwnd);
            // Sin escribir en el hilo de la ventana: si el disco se atasca, Windows no puede esperar por eso
            if (!finished) LogBriefly("Se acabó el tiempo antes de confirmar el apagado.", 300);

            // Después de esto Windows nos cierra enseguida. Si dentro de un rato seguimos vivos, es que no llegó a
            // terminar la sesión (por ejemplo, alguien pulsó Cancelar en la pantalla de espera).
            if (_mode != GuardMode.OffNow) ScheduleResumeCheck(hwnd, round.Id, ResumeCheckMs);
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

        static void ScheduleResumeCheck(IntPtr hwnd, int roundId, int delayMs)
        {
            StartThread(() =>
            {
                Thread.Sleep(delayMs);
                Native.PostMessage(hwnd, WM_APP_RESUMECHECK, (IntPtr)roundId, IntPtr.Zero);
            });
        }

        public static void OnResumeCheck(IntPtr hwnd, int roundId)
        {
            if (_round != null && _round.Id == roundId) Resume(hwnd, "Seguimos en marcha: Windows no llegó a cerrar la sesión.");
        }

        /// <summary>
        /// El apagado no siguió adelante: todo vuelve a la normalidad y, si estaban encendidas, se encienden (todas las
        /// que se mandaron apagar: una puede haberse apagado aunque no llegara a confirmarlo).
        /// </summary>
        static void Resume(IntPtr hwnd, string msg)
        {
            var round = _round;
            _round = null;
            if (round != null) round.Cancelled = true; // si aún estaba apagando, que lo deje
            ClearReason(hwnd);
            _ending = false;
            try { Lights.Abort.Reset(); } catch { }
            StartThread(() => Log.Write(msg));
            if (round == null || _mode == GuardMode.OffNow) return;
            StartThread(() =>
            {
                round.Done.WaitOne(TaskBudgetMs + 3000);
                var cfg = _cfg;
                if (cfg == null || !cfg.LightsOn || _ending || round.Addrs.Count == 0) return;
                Log.Results("Volver a encender", Lights.SetAll(round.Addrs, true, 20000, () => _ending, true));
            });
        }

        /// <summary>La tarea de respaldo terminó de apagar: se va (si Windows nos está esperando, después).</summary>
        public static void OnOffDone(IntPtr hwnd)
        {
            if (_mode != GuardMode.OffNow) return;
            ClearReason(hwnd);
            Quit();
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
            if ((lParam & Native.ENDSESSION_CLOSEAPP) == 0) return false;
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
                Guard.OnOffDone(Handle);
                return;
            }
            if (m.Msg == Guard.WM_APP_RESUMECHECK)
            {
                Guard.OnResumeCheck(Handle, m.WParam.ToInt32());
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
    class StripLink : IDisposable
    {
        public ulong Address;
        public string Name = "";
        public BluetoothLEDevice Device;
        public List<GattDeviceService> Services = new List<GattDeviceService>();
        public GattCharacteristic Char;
        public bool Melk;
        public bool Scanned;                // Windows no la conocía y hubo que escuchar su anuncio
        public string OpenStatus = "";      // lo que dijo OpenAsync al abrir el servicio (si no fue bien)

        public void Dispose()
        {
            foreach (var s in Services) { try { s.Dispose(); } catch { } }
            Services.Clear();
            Char = null;
            if (Device != null) { try { Device.Dispose(); } catch { } Device = null; }
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
            var results = new Dictionary<ulong, StripResult>();
            var clock = Stopwatch.StartNew();
            var threads = new List<Thread>();
            foreach (var addr in addrs)
            {
                var r = new StripResult { Address = addr };
                results[addr] = r;
                var t = new Thread(() =>
                {
                    _abort = abortable ? Abort : null;
                    Worker(r, on, clock, budgetMs, cancel);
                });
                t.IsBackground = true;
                t.SetApartmentState(ApartmentState.MTA);
                t.Start();
                threads.Add(t);
            }
            foreach (var t in threads)
            {
                int left = (int)(budgetMs + 1000 - clock.ElapsedMilliseconds);
                if (left > 0) t.Join(left);
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

        static void Worker(StripResult r, bool on, Stopwatch clock, int budgetMs, Func<bool> cancel)
        {
            string last = "";
            bool uncachedOnly = false;
            for (int attempt = 1; ; attempt++)
            {
                long left = budgetMs - clock.ElapsedMilliseconds;
                if (left < 600 || (cancel != null && cancel())) break;

                StripLink link = null;
                bool ok = false;
                try
                {
                    string err;
                    link = Open(r.Address, clock, budgetMs, uncachedOnly, out err);
                    if (link == null) last = err;
                    else
                    {
                        int t = (int)Math.Max(400, Math.Min(4500, budgetMs - clock.ElapsedMilliseconds - 100));
                        string why;
                        if (Send(link, on, t, cancel, out why)) ok = true;
                        else
                        {
                            last = (cancel != null && cancel()) ? "cancelado" : why;
                            // Lo que Windows tenía guardado de la tira puede estar viejo: la próxima vez, preguntándole a ella
                            uncachedOnly = true;
                        }
                    }
                }
                catch (Exception ex) { last = ex.Message; }

                if (link != null) link.Dispose(); // siempre: nada de conexiones abiertas
                lock (r)
                {
                    if (r.Closed) return; // SetAll ya devolvió sin esperarnos
                    r.Attempts = attempt;
                    if (link != null && link.Name.Length > 0) r.Name = link.Name;
                    if (ok)
                    {
                        r.Ok = true;
                        r.Ms = clock.ElapsedMilliseconds;
                        r.Detail = (link.Melk ? "MELK" : "ELK") + (link.Scanned ? ", hubo que buscarla" : "");
                    }
                    else r.Detail = last;
                }
                if (ok) return;
                Thread.Sleep((int)Math.Max(0, Math.Min(500, budgetMs - clock.ElapsedMilliseconds - 600)));
            }
        }

        /// <summary>
        /// Conecta con cada tira y busca dónde escribir, pero no manda ninguna orden: las luces no cambian. Para saber
        /// que la tira de verdad contesta (y no es solo lo que Windows tenía guardado), lee algo de ella.
        /// </summary>
        public static int Probe(List<ulong> addrs)
        {
            if (addrs.Count == 0)
            {
                Log.Write("Prueba: no hay ninguna MAC en device.cfg.");
                return 2;
            }
            int ok = 0;
            foreach (var addr in addrs)
            {
                var clock = Stopwatch.StartNew();
                string err = "";
                StripLink link = null;
                string readStatus = "";
                for (int attempt = 1; attempt <= 3 && link == null && clock.ElapsedMilliseconds < 15000; attempt++)
                {
                    link = Open(addr, clock, 20000, attempt > 1, out err);
                    if (link == null) Thread.Sleep(500);
                }
                if (link == null)
                {
                    Log.Write("Prueba " + Log.Short(addr) + ": no se llega (" + err + ").");
                    continue;
                }
                try
                {
                    var p = link.Char.CharacteristicProperties;
                    string answer = null;
                    if ((p & GattCharacteristicProperties.Read) != 0)
                    {
                        // Success o ProtocolError: contestó la tira. AccessDenied lo dice Windows: se prueba de la otra forma.
                        var rd = AwaitOp(link.Char.ReadValueAsync(BluetoothCacheMode.Uncached), 4000);
                        if (rd == null) answer = "no contestó";
                        else if (rd.Status == GattCommunicationStatus.Success || rd.Status == GattCommunicationStatus.ProtocolError)
                            answer = "contestó";
                        else if (rd.Status != GattCommunicationStatus.AccessDenied) answer = "no contestó (" + rd.Status + ")";
                        if (rd != null) readStatus = ", leer dio " + rd.Status;
                    }
                    if (answer == null)
                    {
                        var sv = AwaitOp(link.Device.GetGattServicesAsync(BluetoothCacheMode.Uncached), 6000);
                        if (sv != null && sv.Services != null) foreach (var s in sv.Services) { try { s.Dispose(); } catch { } }
                        answer = sv == null ? "no contestó" : (sv.Status == GattCommunicationStatus.Success ? "contestó" : "no contestó (" + sv.Status + ")");
                    }
                    bool good = answer == "contestó";
                    if (good) ok++;
                    Log.Write("Prueba " + link.Name + " " + Log.Short(addr) + ": " + (good ? "se llega" : "NO se llega") +
                        " en " + clock.ElapsedMilliseconds + " ms, la tira " + answer + " (" + (link.Melk ? "MELK" : "ELK") +
                        ", servicio " + Uuid16(link.Char.Service.Uuid) + ", característica " + Uuid16(link.Char.Uuid) +
                        ((p & GattCharacteristicProperties.Write) != 0 ? ", escritura con respuesta" : ", escritura sin respuesta") +
                        ((p & GattCharacteristicProperties.Read) != 0 ? ", se puede leer" : "") + readStatus +
                        (link.Scanned ? ", hubo que buscarla" : "") +
                        (link.OpenStatus.Length > 0 ? ", abrir el servicio dio " + link.OpenStatus : "") + ").");
                }
                finally { link.Dispose(); }
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
        /// Conecta con la tira y busca dónde escribir. Primero con lo que Windows ya tiene guardado (rápido); con
        /// uncachedOnly, preguntando a la tira (si un intento falló, lo guardado puede estar viejo).
        /// </summary>
        static StripLink Open(ulong addr, Stopwatch clock, int budgetMs, bool uncachedOnly, out string error)
        {
            error = "";
            var link = new StripLink { Address = addr };
            try
            {
                link.Device = AwaitOp(BluetoothLEDevice.FromBluetoothAddressAsync(addr), Left(clock, budgetMs, 5000));
                if (link.Device == null && !Aborted())
                {
                    // Sin emparejar, Windows solo la conoce si la ha visto hace poco (tras reiniciar, no):
                    // la buscamos un momento para que entre en su lista y volvemos a probar.
                    link.Scanned = true;
                    if (Finder.WaitForAdvert(addr, Left(clock, budgetMs, 4000), _abort))
                        link.Device = AwaitOp(BluetoothLEDevice.FromBluetoothAddressAsync(addr), Left(clock, budgetMs, 4000));
                }
                if (link.Device == null)
                {
                    error = Aborted() ? "cancelado" : "Windows no la encuentra (¿apagada, lejos o conectada al móvil?)";
                    link.Dispose();
                    return null;
                }
                link.Name = (link.Device.Name ?? "").Trim();
                link.Melk = link.Name.StartsWith("MELK", StringComparison.OrdinalIgnoreCase);

                if (!uncachedOnly && FindWritable(link, BluetoothCacheMode.Cached, clock, budgetMs, out error)) return link;
                if (!Aborted() && FindWritable(link, BluetoothCacheMode.Uncached, clock, budgetMs, out error)) return link;
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
            foreach (var s in link.Services) { try { s.Dispose(); } catch { } }
            link.Services.Clear();
            link.Char = null;

            var res = AwaitOp(link.Device.GetGattServicesAsync(mode), Left(clock, budgetMs, mode == BluetoothCacheMode.Cached ? 3000 : 6000));
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

            // Antes de cada trama se mira si hay que parar: al encender, una que saliera después del aviso de
            // Windows podría llegar a la tira detrás del "apagar".
            if (link.Melk)
            {
                foreach (var init in MelkInit)
                {
                    if (stop()) { why = "cancelado"; return false; }
                    WriteOnce(ch, init, canFast ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse, Math.Max(200, Math.Min(2500, left() - 300)));
                }
            }

            if (canAck)
            {
                if (stop()) { why = "cancelado"; return false; }
                // Si también admite sin respuesta, se le guarda un tercio del tiempo por si esta no contesta
                int ackTime = canFast ? Math.Max(200, left() * 2 / 3) : Math.Max(200, left());
                if (WriteOnce(ch, frame, GattWriteOption.WriteWithResponse, ackTime))
                    return true;
                if (!canFast || stop()) { why = stop() ? "cancelado" : "la tira no confirmó la orden"; return false; }
            }
            if (!canFast) { why = "la característica no admite escritura"; return false; }

            // Sin respuesta no hay confirmación (las ELK-BLEDOM suelen ser así): se manda dos veces y luego se lee
            // la característica. Como todo viaja en orden, si la tira contesta a la lectura es que las órdenes llegaron.
            if (stop()) { why = "cancelado"; return false; }
            bool ok = WriteOnce(ch, frame, GattWriteOption.WriteWithoutResponse, Math.Max(200, left() - 300));
            if (stop()) { why = "cancelado"; return false; }
            Thread.Sleep(60);
            if (stop()) { why = "cancelado"; return false; }
            ok = WriteOnce(ch, frame, GattWriteOption.WriteWithoutResponse, Math.Max(150, Math.Min(600, left() - 200))) || ok;
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

        static bool WriteOnce(GattCharacteristic ch, byte[] frame, GattWriteOption opt, int timeoutMs)
        {
            try
            {
                var dw = new DataWriter();
                dw.WriteBytes(frame);
                var res = AwaitOp(ch.WriteValueWithResultAsync(dw.DetachBuffer(), opt), timeoutMs);
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
            if (op == null) return default(T);
            var done = new ManualResetEvent(false);
            var gate = new object();
            bool abandoned = false, finished = false;
            T result = default(T);
            op.Completed = (info, status) =>
            {
                T r = default(T);
                if (status == AsyncStatus.Completed) { try { r = info.GetResults(); } catch { } }
                bool late;
                lock (gate)
                {
                    late = abandoned;
                    if (!late) { result = r; finished = true; try { done.Set(); } catch { } }
                }
                if (late)
                {
                    DisposeLate(r);
                    try { done.Close(); } catch { }
                }
            };
            var abort = _abort;
            try
            {
                if (abort == null) done.WaitOne(Math.Max(1, timeoutMs));
                else WaitHandle.WaitAny(new WaitHandle[] { done, abort }, Math.Max(1, timeoutMs));
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
                int fromArgs = list.Count;
                bool exists = ReadDeviceCfg(Path.Combine(ExeDir, "device.cfg"), list, ref readError);
                exists = ReadDeviceCfg(Path.Combine(DataDir, "device.cfg"), list, ref readError) || exists;
                if (exists && !readError) SaveCache(list.GetRange(fromArgs, list.Count - fromArgs));
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
