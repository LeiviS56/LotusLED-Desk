using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LotusLED
{
    static class Program
    {
        private static Mutex _mutex;

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool createdNew;
            _mutex = new Mutex(true, "LotusLED_Desk_SingleInstance_Mutex", out createdNew);

            bool isStartup = false;
            foreach (var arg in args)
            {
                if (arg.Equals("-startup", StringComparison.OrdinalIgnoreCase)) isStartup = true;
                if (arg.Equals("-install", StringComparison.OrdinalIgnoreCase))
                {
                    InstallApp();
                    MessageBox.Show("LotusLED Desk se ha instalado correctamente.", "Instalación completada", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (arg.Equals("-uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    UninstallApp();
                    MessageBox.Show("Accesos directos eliminados.", "Desinstalación", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }

            if (!createdNew)
            {
                // Ya hay una instancia corriendo
                return;
            }

            Application.Run(new MainForm(isStartup));
        }

        public static void InstallApp()
        {
            string exe = Application.ExecutablePath;
            string dir = Path.GetDirectoryName(exe);

            // Registro Run para arranque con Windows
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    k.SetValue("LotusLED", "\"" + exe + "\" -startup");
                }
            }
            catch { }

            // Acceso directo en Escritorio y Menú Inicio
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(shellType);

                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);

                string deskLnk = Path.Combine(desktop, "LotusLED Desk.lnk");
                dynamic s1 = shell.CreateShortcut(deskLnk);
                s1.TargetPath = exe;
                s1.WorkingDirectory = dir;
                s1.Description = "Controlador de tiras LED (LotusLED Desk)";
                s1.Save();

                string menuLnk = Path.Combine(startMenu, "LotusLED Desk.lnk");
                dynamic s2 = shell.CreateShortcut(menuLnk);
                s2.TargetPath = exe;
                s2.WorkingDirectory = dir;
                s2.Description = "Controlador de tiras LED (LotusLED Desk)";
                s2.Save();
            }
            catch { }
        }

        public static void UninstallApp()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    k.DeleteValue("LotusLED", false);
                }
            }
            catch { }

            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                string deskLnk = Path.Combine(desktop, "LotusLED Desk.lnk");
                string menuLnk = Path.Combine(startMenu, "LotusLED Desk.lnk");

                if (File.Exists(deskLnk)) File.Delete(deskLnk);
                if (File.Exists(menuLnk)) File.Delete(menuLnk);
            }
            catch { }
        }
    }
}
