using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Windows.Foundation;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace LotusLED
{
    class ShutdownOff
    {
        static void Main(string[] args)
        {
            // Apaga TODAS las tiras que encuentre en:
            //   1) los argumentos:  LotusShutdown.exe AA:BB:CC:DD:EE:FF 11:22:33:44:55:66
            //   2) device.cfg junto al .exe (una MAC por línea; no se sube a GitHub)
            //   3) %LOCALAPPDATA%\LotusLED\settings.cfg (la última tira que conectó LotusLED.exe)
            var addrs = new List<ulong>();
            try
            {
                foreach (var a in args) AddAddr(addrs, ParseMac(a));

                string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "device.cfg");
                if (File.Exists(local))
                {
                    foreach (var line in File.ReadAllLines(local))
                    {
                        string l = line.Trim();
                        if (l.Length == 0 || l.StartsWith("#")) continue;
                        AddAddr(addrs, ParseMac(l));
                    }
                }

                string cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LotusLED", "settings.cfg");
                if (File.Exists(cfg))
                {
                    foreach (var line in File.ReadAllLines(cfg))
                    {
                        ulong v;
                        if (line.StartsWith("LastAddress=") && ulong.TryParse(line.Substring("LastAddress=".Length).Trim(), out v))
                            AddAddr(addrs, v);
                    }
                }
            }
            catch { }

            if (addrs.Count == 0) return; // no sabemos qué tiras apagar

            // Todas a la vez: al apagar el PC Windows da poco tiempo, no podemos ir una detrás de otra
            var threads = new List<Thread>();
            foreach (var addr in addrs)
            {
                ulong a = addr;
                var t = new Thread(() => TurnOff(a)) { IsBackground = true };
                t.Start();
                threads.Add(t);
            }
            foreach (var t in threads) t.Join(15000);
        }

        static void AddAddr(List<ulong> list, ulong addr)
        {
            if (addr != 0 && !list.Contains(addr)) list.Add(addr);
        }

        static void TurnOff(ulong addr)
        {
            BluetoothLEDevice dev = null;
            try
            {
                dev = AwaitOp(BluetoothLEDevice.FromBluetoothAddressAsync(addr), 5000);
                if (dev == null) return;

                var gatt = AwaitOp(dev.GetGattServicesAsync(BluetoothCacheMode.Uncached), 5000);
                if (gatt == null || gatt.Services == null) return;

                GattCharacteristic writeChar = null;
                foreach (var svc in gatt.Services)
                {
                    var chars = AwaitOp(svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached), 4000);
                    if (chars == null || chars.Characteristics == null) continue;

                    foreach (var ch in chars.Characteristics)
                    {
                        string u = ch.Uuid.ToString().ToLower();
                        if (u.StartsWith("0000ff01") || u.StartsWith("0000fff3") || u.StartsWith("6e400002"))
                        {
                            writeChar = ch;
                            break;
                        }
                    }
                    if (writeChar != null) break;
                }

                if (writeChar == null) return;

                // Comando OFF: 7E 00 04 00 00 00 FF 00 EF
                byte[] cmdOff = new byte[] { 0x7E, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0x00, 0xEF };
                var dw = new DataWriter();
                dw.WriteBytes(cmdOff);
                var buf = dw.DetachBuffer();
                var opt = writeChar.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
                    ? GattWriteOption.WriteWithoutResponse
                    : GattWriteOption.WriteWithResponse;

                AwaitOp(writeChar.WriteValueWithResultAsync(buf, opt), 3000);
                Thread.Sleep(200);
            }
            catch { }
            finally
            {
                if (dev != null) { try { dev.Dispose(); } catch { } }
            }
        }

        /// <summary>Acepta "AA:BB:CC:DD:EE:FF", "AABBCCDDEEFF" o el número decimal de settings.cfg.</summary>
        static ulong ParseMac(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            string t = text.Trim();
            string hex = t.Replace(":", "").Replace("-", "");
            bool looksHex = t.Contains(":") || t.Contains("-") ||
                            (hex.Length == 12 && System.Text.RegularExpressions.Regex.IsMatch(hex, "[A-Fa-f]"));
            ulong v;
            if (looksHex)
                return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out v) ? v : 0;
            return ulong.TryParse(t, out v) ? v : 0;
        }

        static T AwaitOp<T>(IAsyncOperation<T> op, int timeoutMs)
        {
            if (op == null) return default(T);
            var evt = new ManualResetEvent(false);
            T result = default(T);
            op.Completed = (info, status) =>
            {
                if (status == AsyncStatus.Completed)
                {
                    try { result = info.GetResults(); } catch { }
                }
                evt.Set();
            };
            evt.WaitOne(timeoutMs);
            return result;
        }
    }
}
