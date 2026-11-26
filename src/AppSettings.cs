using System;
using System.IO;

namespace LotusLED
{
    public class AppSettings
    {
        public ulong LastAddress = 0; // 0 = buscar la tira automáticamente (se guarda al conectar)
        public string LastName = "";
        public bool Power = true;
        public byte ColorR = 168;
        public byte ColorG = 85;
        public byte ColorB = 247;
        public byte Brightness = 80;
        public byte Effect = 0;
        public byte Speed = 50;
        public bool Autostart = true;
        public bool PowerOnStart = true;
        public bool PowerOffShutdown = true;
        public bool MinimizeToTray = true;

        private static string ConfigPath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LotusLED");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, "settings.cfg");
            }
        }

        public static AppSettings Load()
        {
            var s = new AppSettings();
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string[] lines = File.ReadAllLines(ConfigPath);
                    foreach (string line in lines)
                    {
                        int eq = line.IndexOf('=');
                        if (eq < 0) continue;
                        string key = line.Substring(0, eq).Trim();
                        string val = line.Substring(eq + 1).Trim();

                        if (key == "LastAddress") ulong.TryParse(val, out s.LastAddress);
                        else if (key == "LastName") s.LastName = val;
                        else if (key == "Power") bool.TryParse(val, out s.Power);
                        else if (key == "ColorR") byte.TryParse(val, out s.ColorR);
                        else if (key == "ColorG") byte.TryParse(val, out s.ColorG);
                        else if (key == "ColorB") byte.TryParse(val, out s.ColorB);
                        else if (key == "Brightness") byte.TryParse(val, out s.Brightness);
                        else if (key == "Effect") byte.TryParse(val, out s.Effect);
                        else if (key == "Speed") byte.TryParse(val, out s.Speed);
                        else if (key == "Autostart") bool.TryParse(val, out s.Autostart);
                        else if (key == "PowerOnStart") bool.TryParse(val, out s.PowerOnStart);
                        else if (key == "PowerOffShutdown") bool.TryParse(val, out s.PowerOffShutdown);
                        else if (key == "MinimizeToTray") bool.TryParse(val, out s.MinimizeToTray);
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                using (var sw = new StreamWriter(ConfigPath, false))
                {
                    sw.WriteLine("LastAddress=" + LastAddress);
                    sw.WriteLine("LastName=" + LastName);
                    sw.WriteLine("Power=" + Power);
                    sw.WriteLine("ColorR=" + ColorR);
                    sw.WriteLine("ColorG=" + ColorG);
                    sw.WriteLine("ColorB=" + ColorB);
                    sw.WriteLine("Brightness=" + Brightness);
                    sw.WriteLine("Effect=" + Effect);
                    sw.WriteLine("Speed=" + Speed);
                    sw.WriteLine("Autostart=" + Autostart);
                    sw.WriteLine("PowerOnStart=" + PowerOnStart);
                    sw.WriteLine("PowerOffShutdown=" + PowerOffShutdown);
                    sw.WriteLine("MinimizeToTray=" + MinimizeToTray);
                }
            }
            catch { }

            // Las mismas opciones para el ayudante sin ventana (LotusShutdown.exe), que también las escribe la interfaz web
            try
            {
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(ConfigPath), "auto.cfg"),
                    "# LotusLED Desk: lo escribe la interfaz y lo lee LotusShutdown.exe (el ayudante sin ventana)\r\n" +
                    "PowerOnStart=" + PowerOnStart + "\r\n" +
                    "PowerOffShutdown=" + PowerOffShutdown + "\r\n" +
                    "LightsOn=" + Power + "\r\n");
            }
            catch { }
        }
    }
}
