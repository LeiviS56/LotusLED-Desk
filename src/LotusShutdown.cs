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
//   LotusShutdown.exe -watch       Igual, pero sin encender las luces al empezar (lo usa LotusLED.ps1).
//   LotusShutdown.exe              Apaga las tiras ahora y termina.
//   LotusShutdown.exe -on          Las enciende ahora y termina.
//   LotusShutdown.exe -task        Lo usa la tarea programada de respaldo: no hace nada si el ayudante ya vigila.
//   LotusShutdown.exe -find <archivo> [segundos]
//                                  Busca tiras cercanas y guarda una por línea "MAC|nombre|señal" (lo usa Instalar).
//   LotusShutdown.exe -probe       Comprueba que llega a las tiras (conecta y busca dónde escribir) sin cambiar
//                                  las luces, y lo apunta en el registro.
//
// Qué tiras: las MAC que se pasen como argumento y las de device.cfg (junto al .exe o en %LOCALAPPDATA%\LotusLED);
// solo si no hay ninguna, la última que usó LotusLED.exe (settings.cfg).
// Lo que hace queda apuntado en %LOCALAPPDATA%\LotusLED\LotusShutdown.log.
// Ojo: el compilador que trae Windows (csc de .NET Framework 4) solo admite C# 5.

// El nombre que enseña Windows (Administrador de tareas y la pantalla de "programas que impiden apagar")
[assembly: AssemblyTitle("LotusLED Desk - luces")]
[assembly: AssemblyDescription("Enciende las tiras LED al iniciar Windows y las apaga al apagar el PC")]
[assembly: AssemblyProduct("LotusLED Desk")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace LotusLED.Shutdown
{
    static class Helper
    {
        const string MutexName = "Local\\LotusLED_Ayudante_Luces";

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
                if (watch) return Guard.Run(macs, on);
                return RunOnce(macs, on, task);
            }
            catch (Exception ex)
            {
                Log.Write("Error inesperado: " + ex);
                return 1;
            }
        }

        /// <summary>Encender o apagar ahora, una vez, y terminar.</summary>
        static int RunOnce(List<string> macs, bool on, bool task)
        {
            if (task)
            {
                // Tarea de respaldo (evento 1074 al pedir el apagado): si el ayudante está en marcha, él se encarga
                Mutex m;
                if (Mutex.TryOpenExisting(MutexName, out m))
                {
                    m.Dispose();
                    return 0;
                }
                if (!Config.Flag("PowerOffShutdown", true)) return 0;
                Log.Write("Tarea de respaldo: el ayudante no estaba en marcha, apago desde aquí.");
            }

            var addrs = Config.Addresses(macs);
            if (addrs.Count == 0)
            {
                Log.Write("No sé qué tiras " + (on ? "encender" : "apagar") + ": no hay ninguna MAC en device.cfg.");
                return 2;
            }
            var res = Lights.SetAll(addrs, on, task ? 15000 : 20000, null);
            Log.Results(on ? "Encender" : "Apagar", res);
            return Lights.CountOk(res) > 0 ? 0 : 3;
        }

        internal static string MutexNameForGuard { get { return MutexName; } }
    }

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
    // aceptaron y se cerraron; si alguno cancela antes, ni nos enteramos y las luces siguen encendidas), en la
    // pregunta se pone el motivo, se lanza el apagado en otro hilo y se responde al momento, y en WM_ENDSESSION
    // se espera a que termine.
    static class Guard
    {
        const uint ShutdownLevel = 0x100;   // "aplicaciones, al final" (por defecto todos tienen 0x280)
        const int OffBudgetMs = 5000;       // lo normal es 0,3-3 s; si no aparece, Windows tarda 7 s en rendirse
        const int StartWindowMs = 180000;   // al iniciar sesión, cuánto tiempo seguimos intentando encender

        static Mutex _mutex;
        static GuardWindow _window;
        static List<string> _macs;
        static volatile bool _ending;           // Windows ha avisado de que se termina la sesión
        static Thread _offThread;               // el apagado de esta ronda (null = aún no empezó)
        static ManualResetEvent _offDone;
        static Stopwatch _offClock;
        static Dictionary<ulong, StripResult> _offResult;
        static bool _reason;                    // hay motivo de bloqueo puesto

        public static int Run(List<string> macs, bool powerOnNow)
        {
            bool created;
            _mutex = new Mutex(true, Helper.MutexNameForGuard, out created);
            if (!created) return 0; // ya hay un ayudante vigilando en esta sesión

            _macs = macs;
            try { Native.SetProcessShutdownParameters(ShutdownLevel, 0); } catch { }
            _window = new GuardWindow();

            var addrs = Config.Addresses(macs);
            Log.Write("Ayudante en marcha (" + Config.ExeDir + "). Tiras: " + Log.List(addrs) +
                      (addrs.Count == 0 ? "  <- falta la MAC en device.cfg" : ""));

            var t = new Thread(() => { if (powerOnNow) PowerOnAtStart(); else Lights.Warmup(); });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();

            Application.Run();
            GC.KeepAlive(_mutex);
            return 0;
        }

        /// <summary>Al iniciar sesión: encender las tiras. La tira puede tardar en arrancar, así que se insiste un rato.</summary>
        static void PowerOnAtStart()
        {
            if (!Config.Flag("PowerOnStart", true))
            {
                Log.Write("Encender al iniciar está desactivado en la interfaz.");
                return;
            }
            var pending = Config.Addresses(_macs);
            if (pending.Count == 0) return;

            var clock = Stopwatch.StartNew();
            Func<bool> stop = () => _ending;
            for (int round = 1; pending.Count > 0 && !_ending && clock.ElapsedMilliseconds < StartWindowMs; round++)
            {
                var res = Lights.SetAll(pending, true, 20000, stop);
                foreach (var r in res.Values)
                    if (r.Ok) pending.Remove(r.Address);
                if (round == 1 || pending.Count == 0) Log.Results("Encender al iniciar (intento " + round + ")", res);
                if (pending.Count == 0 || _ending) break;
                for (int i = 0; i < 50 && !_ending; i++) Thread.Sleep(100);
            }
            if (pending.Count > 0 && !_ending)
                Log.Write("No se pudo encender " + Log.List(pending) + " en " + (StartWindowMs / 1000) +
                          " s. ¿Está enchufada, o conectada al móvil?");
        }

        /// <summary>WM_QUERYENDSESSION: se pone el motivo, se empieza a apagar en otro hilo y se responde ya.</summary>
        public static void OnQueryEndSession(IntPtr hwnd, long lParam)
        {
            _ending = true; // el encendido del arranque, si sigue intentándolo, ya no manda nada más
            string kind = (lParam & Native.ENDSESSION_LOGOFF) != 0 ? "cierre de sesión" : "apagado o reinicio";
            if ((lParam & Native.ENDSESSION_CRITICAL) != 0) kind += " forzado";
            StartOff(hwnd, "Windows avisa de " + kind);
        }

        static void StartOff(IntPtr hwnd, string why)
        {
            if (_offThread != null) return; // ya empezó en esta ronda
            if (!Config.Flag("PowerOffShutdown", true))
            {
                Log.Write(why + ". Apagar las luces está desactivado en la interfaz.");
                return;
            }
            var addrs = Config.Addresses(_macs);
            if (addrs.Count == 0)
            {
                Log.Write(why + ", pero no hay ninguna MAC en device.cfg.");
                return;
            }
            Log.Write(why + ". Apagando " + Log.List(addrs) + "…");
            if (!_reason)
            {
                try { _reason = Native.ShutdownBlockReasonCreate(hwnd, "Apagando las luces LED…"); } catch { }
            }

            var done = new ManualResetEvent(false);
            _offDone = done;
            _offClock = Stopwatch.StartNew();
            _offResult = null;
            _offThread = new Thread(() =>
            {
                try
                {
                    // Si una orden de encender está a medio enviar, que termine antes (y ya no saldrá ninguna más)
                    if (Monitor.TryEnter(Lights.Gate, 2500)) Monitor.Exit(Lights.Gate);
                    var res = Lights.SetAll(addrs, false, OffBudgetMs, null);
                    _offResult = res;
                    Log.Results("Apagar", res);
                }
                catch (Exception ex) { Log.Write("Error al apagar: " + ex.Message); }
                finally { try { done.Set(); } catch { } }
            });
            _offThread.IsBackground = true;
            _offThread.SetApartmentState(ApartmentState.MTA);
            _offThread.Start();
        }

        /// <summary>WM_ENDSESSION: si la sesión termina, esperamos a que el apagado acabe; si se canceló, vuelve todo.</summary>
        public static void OnEndSession(IntPtr hwnd, bool ending, long lParam)
        {
            if (ending)
            {
                // Apagado forzado sin pregunta previa: empezamos ahora
                if (_offThread == null) StartOff(hwnd, "Windows termina la sesión sin preguntar");
                if (_offDone != null)
                {
                    int left = (int)Math.Max(0, OffBudgetMs + 1500 - _offClock.ElapsedMilliseconds);
                    if (!Native.Wait(_offDone, left)) Log.Write("Se acabó el tiempo antes de confirmar el apagado.");
                }
                ClearReason(hwnd);
                return; // a partir de aquí Windows puede cerrarnos en cualquier momento
            }

            // Cancelado (alguien pulsó Cancelar mientras esperábamos): se dejan las luces como estaban
            Log.Write("Windows canceló el apagado.");
            var done = _offDone;
            var thread = _offThread;
            _offThread = null;
            _offDone = null;
            ClearReason(hwnd);
            _ending = false;
            if (thread == null) return;
            var t = new Thread(() =>
            {
                if (done != null) done.WaitOne(OffBudgetMs + 3000);
                var res = _offResult;
                if (res == null || !Config.Flag("LightsOn", true)) return;
                var restore = new List<ulong>();
                foreach (var r in res.Values) if (r.Ok) restore.Add(r.Address);
                if (restore.Count > 0)
                    Log.Results("Volver a encender", Lights.SetAll(restore, true, 20000, () => _ending));
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        static void ClearReason(IntPtr hwnd)
        {
            if (!_reason) return;
            _reason = false;
            try { Native.ShutdownBlockReasonDestroy(hwnd); } catch { }
        }

        public static void Quit()
        {
            Log.Write("Ayudante cerrado.");
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

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_QUERYENDSESSION)
            {
                long lp = m.LParam.ToInt64();
                // ENDSESSION_CLOSEAPP: un instalador pide cerrar el programa (no es un apagado)
                if ((lp & Native.ENDSESSION_CLOSEAPP) == 0)
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
                if ((lp & Native.ENDSESSION_CLOSEAPP) != 0)
                {
                    if (ending) Guard.Quit();
                }
                else
                {
                    try { Guard.OnEndSession(Handle, ending, lp); } catch (Exception ex) { Log.Write("Error al terminar la sesión: " + ex.Message); }
                }
                m.Result = IntPtr.Zero;
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
        /// <summary>Se toma al mandar "encender", para que nunca llegue después de un "apagar".</summary>
        public static readonly object Gate = new object();

        static readonly byte[] ElkOn = { 0x7E, 0x00, 0x04, 0xF0, 0x00, 0x01, 0xFF, 0x00, 0xEF };
        static readonly byte[] ElkOff = { 0x7E, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0x00, 0xEF };
        static readonly byte[] MelkOn = { 0x7E, 0x07, 0x04, 0xFF, 0x00, 0x01, 0x02, 0x01, 0xEF };
        static readonly byte[] MelkOff = { 0x7E, 0x07, 0x04, 0x00, 0x00, 0x00, 0x02, 0x01, 0xEF };

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
        /// </summary>
        public static Dictionary<ulong, StripResult> SetAll(List<ulong> addrs, bool on, int budgetMs, Func<bool> cancel)
        {
            var results = new Dictionary<ulong, StripResult>();
            var clock = Stopwatch.StartNew();
            var threads = new List<Thread>();
            foreach (var addr in addrs)
            {
                var r = new StripResult { Address = addr };
                results[addr] = r;
                var t = new Thread(() => Worker(r, on, clock, budgetMs, cancel));
                t.IsBackground = true;
                t.SetApartmentState(ApartmentState.MTA);
                t.Start();
                threads.Add(t);
            }
            foreach (var t in threads)
            {
                int left = (int)(budgetMs + 800 - clock.ElapsedMilliseconds);
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
            for (int attempt = 1; ; attempt++)
            {
                long left = budgetMs - clock.ElapsedMilliseconds;
                if (left < 400 || (cancel != null && cancel())) break;

                StripLink link = null;
                bool ok = false;
                try
                {
                    string err;
                    link = Open(r.Address, clock, budgetMs, out err);
                    if (link == null) last = err;
                    else if (Send(link, on, (int)Math.Min(2500, Math.Max(500, budgetMs - clock.ElapsedMilliseconds)), cancel))
                        ok = true;
                    else last = (cancel != null && cancel()) ? "cancelado" : "la orden no llegó";
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
                        r.Detail = link.Melk ? "MELK" : "ELK";
                    }
                    else r.Detail = last;
                }
                if (ok) return;
                Thread.Sleep((int)Math.Max(0, Math.Min(500, budgetMs - clock.ElapsedMilliseconds - 400)));
            }
        }

        /// <summary>Conecta con cada tira y busca dónde escribir, pero no manda nada: las luces no cambian.</summary>
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
                for (int attempt = 1; attempt <= 3 && link == null && clock.ElapsedMilliseconds < 15000; attempt++)
                {
                    link = Open(addr, clock, 20000, out err);
                    if (link == null) Thread.Sleep(500);
                }
                if (link != null)
                {
                    ok++;
                    var p = link.Char.CharacteristicProperties;
                    Log.Write("Prueba " + link.Name + " " + Log.Short(addr) + ": se llega en " + clock.ElapsedMilliseconds +
                        " ms (" + (link.Melk ? "MELK" : "ELK") + ", servicio " + Uuid16(link.Char.Service.Uuid) +
                        ", característica " + Uuid16(link.Char.Uuid) +
                        ((p & GattCharacteristicProperties.Write) != 0 ? ", escritura con respuesta" : ", escritura sin respuesta") +
                        ((p & GattCharacteristicProperties.Read) != 0 ? ", se puede leer" : "") + ").");
                    link.Dispose();
                }
                else Log.Write("Prueba " + Log.Short(addr) + ": no se llega (" + err + ").");
            }
            return ok == addrs.Count ? 0 : 3;
        }

        static string Uuid16(Guid uuid)
        {
            string u = uuid.ToString();
            return u.EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase) && u.StartsWith("0000")
                ? u.Substring(4, 4) : u;
        }

        /// <summary>Conecta con la tira y busca dónde escribir. Primero con lo que Windows ya tiene guardado (rápido).</summary>
        static StripLink Open(ulong addr, Stopwatch clock, int budgetMs, out string error)
        {
            error = "";
            var link = new StripLink { Address = addr };
            try
            {
                link.Device = AwaitOp(BluetoothLEDevice.FromBluetoothAddressAsync(addr), Left(clock, budgetMs, 5000));
                if (link.Device == null)
                {
                    // Sin emparejar, Windows solo la conoce si la ha visto hace poco (tras reiniciar, no):
                    // la buscamos un momento para que entre en su lista y volvemos a probar.
                    if (Finder.WaitForAdvert(addr, Left(clock, budgetMs, 4000)))
                        link.Device = AwaitOp(BluetoothLEDevice.FromBluetoothAddressAsync(addr), Left(clock, budgetMs, 4000));
                }
                if (link.Device == null)
                {
                    error = "Windows no la encuentra (¿apagada, lejos o conectada al móvil?)";
                    return null;
                }
                link.Name = (link.Device.Name ?? "").Trim();
                link.Melk = link.Name.StartsWith("MELK", StringComparison.OrdinalIgnoreCase);

                if (FindWritable(link, BluetoothCacheMode.Cached, clock, budgetMs, out error)) return link;
                if (FindWritable(link, BluetoothCacheMode.Uncached, clock, budgetMs, out error)) return link;
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

        static bool FindWritable(StripLink link, BluetoothCacheMode mode, Stopwatch clock, int budgetMs, out string error)
        {
            error = "";
            foreach (var s in link.Services) { try { s.Dispose(); } catch { } }
            link.Services.Clear();

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
            foreach (var svc in ordered)
            {
                int t = Left(clock, budgetMs, 3000);
                if (t < 200) break;
                var chars = AwaitOp(svc.GetCharacteristicsAsync(mode), t);
                if (chars == null || chars.Status != GattCommunicationStatus.Success || chars.Characteristics == null) continue;
                foreach (var ch in chars.Characteristics)
                {
                    var p = ch.CharacteristicProperties;
                    if ((p & (GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse)) == 0) continue;
                    int rank = Rank(WriteChars, ch.Uuid);
                    if (rank == 99 && ch.Uuid.ToString().StartsWith(NordicUartRx, StringComparison.OrdinalIgnoreCase)) rank = 50;
                    if (rank < bestRank) { best = ch; bestRank = rank; }
                }
                if (bestRank == 0) break; // FFF3: la de las ELK-BLEDOM
            }
            if (best == null || bestRank == 99)
            {
                error = "no tiene ninguna característica de tira LED conocida";
                return false;
            }
            link.Char = best;
            // Abrir el servicio en modo compartido, como hace Chrome: así podemos escribir aunque la ventana
            // de LotusLED tenga la tira conectada (Windows comparte la conexión entre programas).
            try
            {
                var open = AwaitOp(best.Service.OpenAsync(GattSharingMode.SharedReadAndWrite), Left(clock, budgetMs, 1500));
                if (open != GattOpenStatus.Success && open != GattOpenStatus.AlreadyOpened && open != GattOpenStatus.Unspecified)
                    Log.Write("Aviso: " + link.Name + " " + Log.Short(link.Address) + ": abrir el servicio dio " + open + "; pruebo igualmente.");
            }
            catch { /* Windows 10 anterior a 1803: no existe OpenAsync */ }
            return true;
        }

        /// <summary>Manda encender o apagar. Con respuesta si la tira la admite (así sabemos que llegó).</summary>
        public static bool Send(StripLink link, bool on, int timeoutMs, Func<bool> cancel)
        {
            if (link == null || link.Char == null) return false;
            if (on)
            {
                lock (Gate)
                {
                    if (cancel != null && cancel()) return false;
                    return Write(link, ElkOrMelk(link, true), timeoutMs);
                }
            }
            return Write(link, ElkOrMelk(link, false), timeoutMs);
        }

        static byte[] ElkOrMelk(StripLink link, bool on)
        {
            if (link.Melk) return on ? MelkOn : MelkOff;
            return on ? ElkOn : ElkOff;
        }

        static bool Write(StripLink link, byte[] frame, int timeoutMs)
        {
            var p = link.Char.CharacteristicProperties;
            bool canAck = (p & GattCharacteristicProperties.Write) != 0;
            bool canFast = (p & GattCharacteristicProperties.WriteWithoutResponse) != 0;
            var clock = Stopwatch.StartNew();

            if (canAck && WriteOnce(link.Char, frame, GattWriteOption.WriteWithResponse, Math.Min(timeoutMs, 1500)))
                return true;
            if (!canFast) return false;

            // Sin respuesta no hay confirmación (las ELK-BLEDOM suelen ser así): se manda dos veces y luego se lee
            // la característica. Como todo viaja en orden, si la lectura vuelve es que las órdenes llegaron.
            int t = (int)Math.Max(300, Math.Min(1500, timeoutMs - clock.ElapsedMilliseconds));
            bool ok = WriteOnce(link.Char, frame, GattWriteOption.WriteWithoutResponse, t);
            Thread.Sleep(60);
            ok = WriteOnce(link.Char, frame, GattWriteOption.WriteWithoutResponse, 600) || ok;
            if (!ok) return false;
            bool confirmed = false;
            if ((p & GattCharacteristicProperties.Read) != 0)
            {
                try
                {
                    var rd = AwaitOp(link.Char.ReadValueAsync(BluetoothCacheMode.Uncached), 900);
                    confirmed = rd != null && rd.Status == GattCommunicationStatus.Success;
                }
                catch { }
            }
            if (!confirmed) Thread.Sleep(350); // unos cuantos intervalos de conexión para que salga por la antena
            return true;
        }

        static bool WriteOnce(GattCharacteristic ch, byte[] frame, GattWriteOption opt, int timeoutMs)
        {
            var dw = new DataWriter();
            dw.WriteBytes(frame);
            var res = AwaitOp(ch.WriteValueWithResultAsync(dw.DetachBuffer(), opt), timeoutMs);
            return res != null && res.Status == GattCommunicationStatus.Success;
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
        /// Espera una operación de WinRT con tiempo límite. Si se pasa, la cancela, y si aun así termina más
        /// tarde, cierra lo que devuelva: si no, Windows dejaría la conexión con la tira abierta y la tira
        /// dejaría de anunciarse (lo que ya pasó con LotusLED.exe).
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
                lock (gate) { late = abandoned; if (!late) { result = r; finished = true; } }
                if (late) DisposeLate(r);
                try { done.Set(); } catch { }
            };
            if (done.WaitOne(Math.Max(1, timeoutMs))) { lock (gate) { return result; } }
            T got;
            bool fin;
            lock (gate) { abandoned = true; got = result; fin = finished; }
            if (!fin) { try { op.Cancel(); } catch { } }
            return got;
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

        /// <summary>Escucha anuncios hasta ver esa dirección (o hasta que pase el tiempo). Así Windows la vuelve a conocer.</summary>
        public static bool WaitForAdvert(ulong addr, int timeoutMs)
        {
            if (timeoutMs < 300) return false;
            var seen = new ManualResetEvent(false);
            var watcher = new BluetoothLEAdvertisementWatcher();
            watcher.ScanningMode = BluetoothLEScanningMode.Active;
            watcher.Received += (s, e) => { if (e.BluetoothAddress == addr) { try { seen.Set(); } catch { } } };
            try
            {
                watcher.Start();
                bool ok = seen.WaitOne(timeoutMs);
                if (ok) Thread.Sleep(150); // que llegue también la respuesta al escaneo (con el nombre)
                return ok;
            }
            catch { return false; }
            finally { try { watcher.Stop(); } catch { } }
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
    static class Config
    {
        public static string ExeDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }

        public static string DataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LotusLED"); }
        }

        /// <summary>MAC de los argumentos y de device.cfg (junto al .exe y en %LOCALAPPDATA%\LotusLED); si no hay, la de settings.cfg.</summary>
        public static List<ulong> Addresses(IEnumerable<string> extra)
        {
            var list = new List<ulong>();
            try
            {
                if (extra != null) foreach (var a in extra) Add(list, ParseMac(a));
                ReadDeviceCfg(Path.Combine(ExeDir, "device.cfg"), list);
                ReadDeviceCfg(Path.Combine(DataDir, "device.cfg"), list);
                if (list.Count > 0) return list;

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

        static void ReadDeviceCfg(string path, List<ulong> list)
        {
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                string l = line.Trim();
                if (l.Length == 0 || l.StartsWith("#")) continue;
                // Se admite "AA:BB:CC:DD:EE:FF  # comentario" o "AA:BB:CC:DD:EE:FF|nombre"
                int cut = l.IndexOfAny(new[] { '#', '|', ' ', '\t', ';' });
                if (cut > 0) l = l.Substring(0, cut);
                Add(list, ParseMac(l));
            }
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
            if (addrs.Count == 0) return "ninguna";
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
                parts.Add(who + (r.Ok ? ": OK en " + r.Ms + " ms" : ": FALLÓ (" + r.Detail + ")") +
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

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessShutdownParameters(uint dwLevel, uint dwFlags);

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
