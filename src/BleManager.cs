using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Windows.Foundation;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace LotusLED
{
    /// <summary>
    /// Si hay una tira guardada (LastAddress) solo conecta con esa; si no, con todas las que encuentre
    /// y manda cada comando a todas. El escáner sigue activo mientras la app está abierta, así que
    /// una tira que se apaga o se desconecta se vuelve a conectar sola en cuanto vuelve a anunciarse.
    /// </summary>
    public class BleManager
    {
        private class Strip
        {
            public ulong Address;
            public string Name;
            public BluetoothLEDevice Device;
            public GattCharacteristic WriteChar;
        }

        private BluetoothLEAdvertisementWatcher _watcher;
        private readonly Dictionary<ulong, Strip> _strips = new Dictionary<ulong, Strip>();
        private readonly HashSet<ulong> _connecting = new HashSet<ulong>();
        private readonly Dictionary<ulong, DateTime> _retryAfter = new Dictionary<ulong, DateTime>();
        private readonly object _lock = new object();
        private bool _running = false;
        private ulong _onlyAddress = 0; // si hay una tira guardada, solo se conecta esa (no las de los vecinos)

        public event Action<string, ulong> DeviceConnected;
        public event Action DeviceDisconnected;
        public event Action<string> StatusChanged;

        private void FireStatus(string msg)
        {
            var h = StatusChanged;
            if (h != null) h(msg);
        }

        private void FireConnected(string name, ulong addr)
        {
            var h = DeviceConnected;
            if (h != null) h(name, addr);
        }

        private void FireDisconnected()
        {
            var h = DeviceDisconnected;
            if (h != null) h();
        }

        public bool IsConnected
        {
            get { lock (_lock) { return _strips.Count > 0; } }
        }

        public int ConnectedCount
        {
            get { lock (_lock) { return _strips.Count; } }
        }

        /// <summary>Nombres de todas las tiras conectadas, separados por comas.</summary>
        public string ConnectedName
        {
            get { lock (_lock) { return string.Join(", ", _strips.Values.Select(s => s.Name)); } }
        }

        public ulong ConnectedAddress { get; private set; }

        public void StartDiscovery(ulong targetAddress = 0)
        {
            _running = true;
            _onlyAddress = targetAddress;
            FireStatus("Buscando tiras LED Bluetooth...");

            // La última tira conocida se intenta directamente (es más rápido que esperar a su anuncio)
            if (targetAddress != 0)
            {
                ThreadPool.QueueUserWorkItem(_ => TryConnectToAddress(targetAddress));
            }
            StartWatcher();
        }

        private void StartWatcher()
        {
            StopWatcher();
            if (!_running) return;

            var w = new BluetoothLEAdvertisementWatcher();
            w.ScanningMode = BluetoothLEScanningMode.Active;
            w.Received += OnAdvertisementReceived;
            w.Stopped += OnWatcherStopped;
            _watcher = w;
            try
            {
                w.Start();
            }
            catch (Exception ex)
            {
                FireStatus("Error al iniciar Bluetooth: " + ex.Message);
                ScheduleWatcherRestart();
            }
        }

        private void StopWatcher()
        {
            var w = _watcher;
            _watcher = null;
            if (w == null) return;
            try
            {
                w.Received -= OnAdvertisementReceived;
                w.Stopped -= OnWatcherStopped;
                w.Stop();
            }
            catch { }
        }

        public void StopDiscovery()
        {
            _running = false;
            StopWatcher();
        }

        private void OnWatcherStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        {
            // Windows detiene el escáner si se apaga el Bluetooth o el adaptador se reinicia: lo relanzamos.
            if (sender != _watcher || !_running) return;
            if (args.Error != BluetoothError.Success)
                FireStatus("Bluetooth no disponible (" + args.Error + "). Reintentando...");
            ScheduleWatcherRestart();
        }

        private void ScheduleWatcherRestart()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(5000);
                if (_running) StartWatcher();
            });
        }

        private static bool LooksLikeLedStrip(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            name = name.Trim();
            string[] prefixes = { "L7", "ELK", "MELK", "BLEDOM", "LED", "BLE", "Lotus", "QHM", "BJ_LED", "Triones" };
            foreach (var p in prefixes)
            {
                if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            string name = args.Advertisement.LocalName;
            if (!LooksLikeLedStrip(name)) return;

            ulong addr = args.BluetoothAddress;
            if (_onlyAddress != 0 && addr != _onlyAddress) return;
            lock (_lock)
            {
                if (_strips.ContainsKey(addr) || _connecting.Contains(addr)) return;
                DateTime after;
                if (_retryAfter.TryGetValue(addr, out after) && DateTime.Now < after) return;
            }
            ThreadPool.QueueUserWorkItem(_ => TryConnectToAddress(addr, name.Trim()));
        }

        public bool TryConnectToAddress(ulong address, string hintName = "")
        {
            lock (_lock)
            {
                if (_strips.ContainsKey(address) || _connecting.Contains(address)) return false;
                _connecting.Add(address);
            }

            BluetoothLEDevice dev = null;
            bool ok = false;
            try
            {
                FireStatus("Conectando con " + (string.IsNullOrEmpty(hintName) ? "tira LED" : hintName) + "...");
                dev = AwaitOp(BluetoothLEDevice.FromBluetoothAddressAsync(address), 15000);
                if (dev == null)
                {
                    FireStatus("No se pudo obtener el dispositivo.");
                    return false;
                }

                string name = (dev.Name ?? "").Trim();
                if (name.Length == 0) name = string.IsNullOrEmpty(hintName) ? "Tira LED" : hintName;

                var gatt = AwaitOp(dev.GetGattServicesAsync(BluetoothCacheMode.Uncached), 15000);
                if (gatt == null || gatt.Status != GattCommunicationStatus.Success || gatt.Services == null || gatt.Services.Count == 0)
                {
                    FireStatus("No se encontraron servicios GATT en " + name + ".");
                    return false;
                }

                var foundChar = FindWriteCharacteristic(gatt.Services);
                if (foundChar == null)
                {
                    FireStatus("No se encontró canal de escritura LED en " + name + ".");
                    return false;
                }

                var strip = new Strip { Address = address, Name = name, Device = dev, WriteChar = foundChar };
                int count;
                lock (_lock)
                {
                    _strips[address] = strip;
                    _retryAfter.Remove(address);
                    count = _strips.Count;
                }
                dev.ConnectionStatusChanged += OnConnectionStatusChanged;
                ConnectedAddress = address;
                ok = true;

                FireStatus(count == 1 ? "Conectado a " + name : "Conectado a " + count + " tiras: " + ConnectedName);
                FireConnected(name, address);
                return true;
            }
            catch (Exception ex)
            {
                FireStatus("Error al conectar: " + ex.Message);
                return false;
            }
            finally
            {
                if (!ok)
                {
                    // Muy importante: si no se libera, Windows mantiene la conexión abierta, la tira deja de
                    // anunciarse y ya no la ve nadie (ni esta app, ni la versión web, ni el móvil).
                    if (dev != null) { try { dev.Dispose(); } catch { } }
                    lock (_lock) { _retryAfter[address] = DateTime.Now.AddSeconds(10); }
                }
                lock (_lock) { _connecting.Remove(address); }
            }
        }

        /// <summary>Elige la característica de escritura: primero las conocidas, si no, cualquiera que admita escritura.</summary>
        private static GattCharacteristic FindWriteCharacteristic(IReadOnlyList<GattDeviceService> services)
        {
            string[] preferred = { "0000fff3", "0000ff01", "0000ffe1", "0000ffe9", "0000ffd9", "6e400002" };
            GattCharacteristic best = null;
            int bestRank = int.MaxValue;

            foreach (var svc in services)
            {
                var charRes = AwaitOp(svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached), 8000);
                if (charRes == null || charRes.Characteristics == null) continue;

                foreach (var ch in charRes.Characteristics)
                {
                    bool canWrite = ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse) ||
                                    ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write);
                    if (!canWrite) continue;

                    string uuid = ch.Uuid.ToString().ToLower();
                    int rank = Array.FindIndex(preferred, p => uuid.StartsWith(p));
                    if (rank < 0) rank = preferred.Length;
                    if (rank < bestRank)
                    {
                        best = ch;
                        bestRank = rank;
                    }
                }
            }
            return best;
        }

        private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected) return;

            Strip strip;
            int remaining;
            lock (_lock)
            {
                if (!_strips.TryGetValue(sender.BluetoothAddress, out strip) || strip.Device != sender) return;
                _strips.Remove(sender.BluetoothAddress);
                remaining = _strips.Count;
            }

            sender.ConnectionStatusChanged -= OnConnectionStatusChanged;
            try { sender.Dispose(); } catch { }

            // No hace falta un bucle de reconexión: el escáner sigue activo y la reconecta cuando vuelva a anunciarse.
            FireStatus(strip.Name + " desconectada. Esperando a que vuelva...");
            if (remaining == 0) FireDisconnected();
        }

        public bool SendCommand(byte[] data)
        {
            List<Strip> targets;
            lock (_lock) { targets = _strips.Values.ToList(); }
            if (targets.Count == 0) return false;

            bool any = false;
            foreach (var s in targets)
            {
                try
                {
                    var dw = new DataWriter();
                    dw.WriteBytes(data);
                    var buf = dw.DetachBuffer();
                    var opt = s.WriteChar.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
                        ? GattWriteOption.WriteWithoutResponse
                        : GattWriteOption.WriteWithResponse;

                    s.WriteChar.WriteValueWithResultAsync(buf, opt);
                    any = true;
                }
                catch { }
            }
            return any;
        }

        public void SetPower(bool on)
        {
            byte[] cmd = on
                ? new byte[] { 0x7E, 0x00, 0x04, 0xF0, 0x00, 0x01, 0xFF, 0x00, 0xEF }
                : new byte[] { 0x7E, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0x00, 0xEF };
            SendCommand(cmd);
        }

        public void SetColor(byte r, byte g, byte b)
        {
            byte[] cmd = new byte[] { 0x7E, 0x00, 0x05, 0x03, r, g, b, 0x00, 0xEF };
            SendCommand(cmd);
        }

        public void SetBrightness(byte pct)
        {
            pct = Math.Min((byte)100, Math.Max((byte)0, pct));
            byte[] cmd = new byte[] { 0x7E, 0x00, 0x01, pct, 0x00, 0x00, 0x00, 0x00, 0xEF };
            SendCommand(cmd);
        }

        public void SetEffect(byte effectId)
        {
            byte[] cmd = new byte[] { 0x7E, 0x00, 0x03, effectId, 0x03, 0x00, 0x00, 0x00, 0xEF };
            SendCommand(cmd);
        }

        public void SetSpeed(byte speedPct)
        {
            speedPct = Math.Min((byte)100, Math.Max((byte)0, speedPct));
            byte[] cmd = new byte[] { 0x7E, 0x00, 0x02, speedPct, 0x00, 0x00, 0x00, 0x00, 0xEF };
            SendCommand(cmd);
        }

        private static T AwaitOp<T>(IAsyncOperation<T> op, int timeoutMs)
        {
            if (op == null) return default(T);
            var evt = new ManualResetEvent(false);
            var gate = new object();
            bool abandoned = false;
            T result = default(T);
            op.Completed = (info, status) =>
            {
                T r = default(T);
                if (status == AsyncStatus.Completed)
                {
                    try { r = info.GetResults(); } catch { }
                }
                lock (gate)
                {
                    if (abandoned)
                    {
                        // Llegó tarde: nadie lo va a usar, así que se libera (si no, deja la tira "secuestrada")
                        var d = r as IDisposable;
                        if (d != null) { try { d.Dispose(); } catch { } }
                        return;
                    }
                    result = r;
                    evt.Set();
                }
            };
            if (!evt.WaitOne(timeoutMs))
            {
                lock (gate)
                {
                    if (!evt.WaitOne(0))
                    {
                        abandoned = true;
                        try { op.Cancel(); } catch { }
                        return default(T);
                    }
                }
            }
            return result;
        }
    }
}
