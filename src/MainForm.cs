using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LotusLED
{
    public class MainForm : Form
    {
        private BleManager _ble;
        private AppSettings _settings;
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _trayMenu;
        private bool _isSystemShutdown = false;
        private bool _realExit = false;

        // UI Controls
        private Panel _topBar;
        private Label _lblTitle;
        private Label _lblStatus;
        private Panel _statusDot;
        private Button _btnClose;
        private Button _btnMin;

        private Button _btnPower;
        private TrackBar _tbBrightness;
        private Label _lblBrightnessVal;
        private TrackBar _tbSpeed;
        private Label _lblSpeedVal;
        private ComboBox _cbEffects;
        private Panel _colorPreview;
        private Panel _palettePanel;

        private CheckBox _chkAutostart;
        private CheckBox _chkPowerOnStart;
        private CheckBox _chkPowerOffShutdown;
        private CheckBox _chkMinimizeToTray;

        private Point _dragStart;
        private bool _isDragging = false;

        public MainForm(bool startMinimized = false)
        {
            _settings = AppSettings.Load();
            _ble = new BleManager();

            InitializeComponents();
            SetupTrayIcon();
            HookEvents();

            ApplyThemeColor(Color.FromArgb(_settings.ColorR, _settings.ColorG, _settings.ColorB));
            UpdatePowerButtonVisual();

            // Iniciar conexión BLE
            _ble.StartDiscovery(_settings.LastAddress);

            SystemEvents.SessionEnding += OnSessionEnding;

            if (startMinimized)
            {
                WindowState = FormWindowState.Minimized;
                ShowInTaskbar = false;
            }
        }

        private void InitializeComponents()
        {
            SuspendLayout();

            Text = "LotusLED Desk";
            Size = new Size(540, 720);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(14, 16, 23);
            ForeColor = Color.FromArgb(238, 240, 247);
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            DoubleBuffered = true;

            string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LotusLED.ico");
            if (File.Exists(icoPath))
            {
                Icon = new Icon(icoPath);
            }

            // TopBar
            _topBar = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Color.FromArgb(18, 20, 30) };
            _topBar.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { _isDragging = true; _dragStart = e.Location; } };
            _topBar.MouseMove += (s, e) => { if (_isDragging) { Location = new Point(Location.X + e.X - _dragStart.X, Location.Y + e.Y - _dragStart.Y); } };
            _topBar.MouseUp += (s, e) => { _isDragging = false; };

            _statusDot = new Panel { Size = new Size(10, 10), Location = new Point(16, 19), BackColor = Color.FromArgb(100, 107, 128) };
            _statusDot.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(_statusDot.BackColor))
                {
                    e.Graphics.FillEllipse(b, 0, 0, 9, 9);
                }
            };

            _lblTitle = new Label
            {
                Text = "LotusLED Desk",
                Font = new Font("Segoe UI", 11.5f, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(34, 14),
                AutoSize = true
            };

            _lblStatus = new Label
            {
                Text = "Buscando tira...",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(160, 167, 187),
                Location = new Point(175, 17),
                AutoSize = true
            };

            _btnMin = new Button
            {
                Text = "—",
                Size = new Size(36, 32),
                Location = new Point(452, 8),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(180, 185, 200),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            _btnMin.FlatAppearance.BorderSize = 0;
            _btnMin.Click += (s, e) => WindowState = FormWindowState.Minimized;

            _btnClose = new Button
            {
                Text = "✕",
                Size = new Size(36, 32),
                Location = new Point(492, 8),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(180, 185, 200),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9.5f)
            };
            _btnClose.FlatAppearance.BorderSize = 0;
            _btnClose.MouseEnter += (s, e) => { _btnClose.BackColor = Color.FromArgb(239, 68, 68); _btnClose.ForeColor = Color.White; };
            _btnClose.MouseLeave += (s, e) => { _btnClose.BackColor = Color.Transparent; _btnClose.ForeColor = Color.FromArgb(180, 185, 200); };
            _btnClose.Click += (s, e) => Close();

            _topBar.Controls.AddRange(new Control[] { _statusDot, _lblTitle, _lblStatus, _btnMin, _btnClose });

            // Main Container Panel
            var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 14, 24, 20), AutoScroll = true };

            // Botón de Encendido
            _btnPower = new Button
            {
                Size = new Size(84, 84),
                Location = new Point(220, 12),
                FlatStyle = FlatStyle.Flat,
                Text = "⏻",
                Font = new Font("Segoe UI", 26f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnPower.FlatAppearance.BorderSize = 2;
            _btnPower.Click += OnPowerToggle;

            // Slider Brillo
            var pnlBright = new Panel { Location = new Point(20, 112), Size = new Size(480, 52) };
            var lblBright = new Label { Text = "Brillo de la tira", Location = new Point(0, 0), AutoSize = true, ForeColor = Color.FromArgb(180, 185, 200) };
            _lblBrightnessVal = new Label { Text = _settings.Brightness + "%", Location = new Point(430, 0), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
            _tbBrightness = new TrackBar { Location = new Point(0, 20), Size = new Size(480, 30), Minimum = 1, Maximum = 100, Value = _settings.Brightness, TickStyle = TickStyle.None };
            _tbBrightness.Scroll += (s, e) =>
            {
                _settings.Brightness = (byte)_tbBrightness.Value;
                _lblBrightnessVal.Text = _settings.Brightness + "%";
                _ble.SetBrightness(_settings.Brightness);
                _settings.Save();
            };
            pnlBright.Controls.AddRange(new Control[] { lblBright, _lblBrightnessVal, _tbBrightness });

            // Previsualización y paleta
            var lblPalette = new Label { Text = "Colores rápidos", Location = new Point(20, 174), AutoSize = true, ForeColor = Color.FromArgb(180, 185, 200) };
            _colorPreview = new Panel
            {
                Size = new Size(32, 32),
                Location = new Point(468, 170),
                BackColor = Color.FromArgb(_settings.ColorR, _settings.ColorG, _settings.ColorB)
            };
            _colorPreview.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(Color.FromArgb(255, 255, 255), 2))
                {
                    e.Graphics.DrawEllipse(pen, 1, 1, 29, 29);
                }
            };

            _palettePanel = new FlowLayoutPanel
            {
                Location = new Point(20, 206),
                Size = new Size(480, 96),
                BackColor = Color.FromArgb(20, 23, 34),
                Padding = new Padding(8)
            };

            CreateColorSwatches();

            // Efectos
            var pnlEffects = new Panel { Location = new Point(20, 314), Size = new Size(480, 68), BackColor = Color.FromArgb(20, 23, 34), Padding = new Padding(12) };
            var lblEff = new Label { Text = "Modo de Efecto", Location = new Point(12, 10), AutoSize = true, ForeColor = Color.FromArgb(180, 185, 200) };
            _cbEffects = new ComboBox
            {
                Location = new Point(12, 32),
                Size = new Size(240, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(28, 32, 46),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            PopulateEffects();
            _cbEffects.SelectedIndexChanged += (s, e) =>
            {
                if (_cbEffects.SelectedIndex > 0)
                {
                    var item = (EffectItem)_cbEffects.SelectedItem;
                    _settings.Effect = item.Id;
                    _ble.SetEffect(item.Id);
                    _settings.Save();
                }
            };

            var lblSpeed = new Label { Text = "Velocidad", Location = new Point(275, 10), AutoSize = true, ForeColor = Color.FromArgb(180, 185, 200) };
            _lblSpeedVal = new Label { Text = _settings.Speed + "%", Location = new Point(430, 10), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
            _tbSpeed = new TrackBar { Location = new Point(275, 30), Size = new Size(185, 28), Minimum = 1, Maximum = 100, Value = _settings.Speed, TickStyle = TickStyle.None };
            _tbSpeed.Scroll += (s, e) =>
            {
                _settings.Speed = (byte)_tbSpeed.Value;
                _lblSpeedVal.Text = _settings.Speed + "%";
                _ble.SetSpeed(_settings.Speed);
                _settings.Save();
            };
            pnlEffects.Controls.AddRange(new Control[] { lblEff, _cbEffects, lblSpeed, _lblSpeedVal, _tbSpeed });

            // Opciones de inicio y comportamiento
            var pnlOptions = new Panel { Location = new Point(20, 396), Size = new Size(480, 190), BackColor = Color.FromArgb(20, 23, 34), Padding = new Padding(14) };
            var lblOptTitle = new Label { Text = "Automatización y opciones", Font = new Font("Segoe UI", 10f, FontStyle.Bold), Location = new Point(14, 10), AutoSize = true };

            _chkAutostart = new CheckBox { Text = "Iniciar con Windows (arranque automático)", Location = new Point(16, 40), AutoSize = true, Checked = _settings.Autostart };
            _chkPowerOnStart = new CheckBox { Text = "Encender luces automáticamente al encender el PC", Location = new Point(16, 72), AutoSize = true, Checked = _settings.PowerOnStart };
            _chkPowerOffShutdown = new CheckBox { Text = "Apagar luces automáticamente al apagar el PC", Location = new Point(16, 104), AutoSize = true, Checked = _settings.PowerOffShutdown };
            _chkMinimizeToTray = new CheckBox { Text = "Al cerrar la ventana (X), mantener en bandeja sin apagar luces", Location = new Point(16, 136), AutoSize = true, Checked = _settings.MinimizeToTray };

            _chkAutostart.CheckedChanged += (s, e) =>
            {
                _settings.Autostart = _chkAutostart.Checked;
                SetWindowsStartup(_settings.Autostart);
                _settings.Save();
            };
            _chkPowerOnStart.CheckedChanged += (s, e) => { _settings.PowerOnStart = _chkPowerOnStart.Checked; _settings.Save(); };
            _chkPowerOffShutdown.CheckedChanged += (s, e) => { _settings.PowerOffShutdown = _chkPowerOffShutdown.Checked; _settings.Save(); };
            _chkMinimizeToTray.CheckedChanged += (s, e) => { _settings.MinimizeToTray = _chkMinimizeToTray.Checked; _settings.Save(); };

            pnlOptions.Controls.AddRange(new Control[] { lblOptTitle, _chkAutostart, _chkPowerOnStart, _chkPowerOffShutdown, _chkMinimizeToTray });

            // Botón cerrar completamente
            var btnFullExit = new Button
            {
                Text = "Cerrar programa por completo",
                Location = new Point(20, 600),
                Size = new Size(480, 36),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(239, 68, 68),
                BackColor = Color.FromArgb(30, 20, 25),
                Cursor = Cursors.Hand
            };
            btnFullExit.FlatAppearance.BorderColor = Color.FromArgb(120, 30, 40);
            btnFullExit.Click += (s, e) =>
            {
                _realExit = true;
                Application.Exit();
            };

            content.Controls.AddRange(new Control[] {
                _btnPower, pnlBright, lblPalette, _colorPreview, _palettePanel,
                pnlEffects, pnlOptions, btnFullExit
            });

            Controls.Add(content);
            Controls.Add(_topBar);

            ResumeLayout(false);
        }

        private void CreateColorSwatches()
        {
            Color[] colors = new Color[]
            {
                Color.FromArgb(255, 0, 0),       // Rojo
                Color.FromArgb(255, 60, 0),      // Naranja fuerte
                Color.FromArgb(255, 140, 0),     // Naranja
                Color.FromArgb(255, 210, 0),     // Ámbar
                Color.FromArgb(160, 255, 0),     // Lima
                Color.FromArgb(0, 255, 60),      // Verde
                Color.FromArgb(0, 255, 200),     // Turquesa
                Color.FromArgb(0, 180, 255),     // Cian
                Color.FromArgb(0, 70, 255),      // Azul
                Color.FromArgb(110, 0, 255),     // Índigo
                Color.FromArgb(168, 85, 247),    // Morado Lotus
                Color.FromArgb(236, 72, 153),    // Rosa Neón
                Color.FromArgb(255, 200, 140),   // Blanco cálido
                Color.FromArgb(255, 255, 255)    // Blanco puro
            };

            foreach (var col in colors)
            {
                var btn = new Button
                {
                    Size = new Size(30, 30),
                    Margin = new Padding(4),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = col,
                    Cursor = Cursors.Hand
                };
                btn.FlatAppearance.BorderSize = 0;
                btn.Click += (s, e) =>
                {
                    _settings.ColorR = col.R;
                    _settings.ColorG = col.G;
                    _settings.ColorB = col.B;
                    ApplyThemeColor(col);

                    if (!_settings.Power)
                    {
                        _settings.Power = true;
                        _ble.SetPower(true);
                        UpdatePowerButtonVisual();
                    }

                    _ble.SetColor(col.R, col.G, col.B);
                    _settings.Save();
                };
                _palettePanel.Controls.Add(btn);
            }
        }

        private void PopulateEffects()
        {
            _cbEffects.Items.Add(new EffectItem(0, "Color Fijo"));
            _cbEffects.Items.Add(new EffectItem(0x80, "Salto RGB"));
            _cbEffects.Items.Add(new EffectItem(0x81, "Salto 7 Colores"));
            _cbEffects.Items.Add(new EffectItem(0x82, "Respiración Roja"));
            _cbEffects.Items.Add(new EffectItem(0x83, "Respiración Verde"));
            _cbEffects.Items.Add(new EffectItem(0x84, "Respiración Azul"));
            _cbEffects.Items.Add(new EffectItem(0x87, "Respiración Magenta"));
            _cbEffects.Items.Add(new EffectItem(0x88, "Respiración Blanca"));
            _cbEffects.Items.Add(new EffectItem(0x8C, "Fundido RGB"));
            _cbEffects.Items.Add(new EffectItem(0x8D, "Fundido 7 Colores"));
            _cbEffects.Items.Add(new EffectItem(0x8E, "Destellos Rojos"));
            _cbEffects.Items.Add(new EffectItem(0x95, "Destellos 7 Colores"));
            _cbEffects.SelectedIndex = 0;
        }

        private void HookEvents()
        {
            _ble.StatusChanged += msg =>
            {
                if (InvokeRequired) { BeginInvoke(new Action(() => _lblStatus.Text = msg)); }
                else { _lblStatus.Text = msg; }
            };

            _ble.DeviceConnected += (name, addr) =>
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(() => OnDeviceConnected(name, addr)));
                }
                else
                {
                    OnDeviceConnected(name, addr);
                }
            };

            _ble.DeviceDisconnected += () =>
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(() =>
                    {
                        _statusDot.BackColor = Color.FromArgb(239, 68, 68);
                        _statusDot.Invalidate();
                        _trayIcon.Text = "LotusLED: Desconectado";
                    }));
                }
            };
        }

        private void OnDeviceConnected(string name, ulong addr)
        {
            _statusDot.BackColor = Color.FromArgb(62, 230, 168); // Verde
            _statusDot.Invalidate();
            string names = _ble.ConnectedName;
            if (string.IsNullOrEmpty(names)) names = name;
            _lblStatus.Text = "Conectado a " + names;
            string tip = "LotusLED: " + names;
            _trayIcon.Text = tip.Length > 63 ? tip.Substring(0, 60) + "..." : tip; // Windows no admite más de 63 caracteres

            _settings.LastName = name;
            _settings.LastAddress = addr;

            if (_settings.PowerOnStart)
            {
                _settings.Power = true;
                UpdatePowerButtonVisual();
                _ble.SetPower(true);
                _ble.SetBrightness(_settings.Brightness);
                _ble.SetColor(_settings.ColorR, _settings.ColorG, _settings.ColorB);
            }
            _settings.Save();
        }

        private void OnPowerToggle(object sender, EventArgs e)
        {
            _settings.Power = !_settings.Power;
            _ble.SetPower(_settings.Power);
            if (_settings.Power)
            {
                _ble.SetBrightness(_settings.Brightness);
                _ble.SetColor(_settings.ColorR, _settings.ColorG, _settings.ColorB);
            }
            UpdatePowerButtonVisual();
            _settings.Save();
        }

        private void ApplyThemeColor(Color col)
        {
            _colorPreview.BackColor = col;
            _colorPreview.Invalidate();
            if (_settings.Power)
            {
                _btnPower.ForeColor = col;
                _btnPower.FlatAppearance.BorderColor = col;
            }
        }

        private void UpdatePowerButtonVisual()
        {
            if (_settings.Power)
            {
                Color c = Color.FromArgb(_settings.ColorR, _settings.ColorG, _settings.ColorB);
                _btnPower.BackColor = Color.FromArgb(40, c.R / 4, c.G / 4, c.B / 4);
                _btnPower.ForeColor = c;
                _btnPower.FlatAppearance.BorderColor = c;
            }
            else
            {
                _btnPower.BackColor = Color.FromArgb(24, 27, 38);
                _btnPower.ForeColor = Color.FromArgb(100, 107, 128);
                _btnPower.FlatAppearance.BorderColor = Color.FromArgb(50, 55, 70);
            }
        }

        private void SetupTrayIcon()
        {
            _trayMenu = new ContextMenuStrip();
            var itemOpen = _trayMenu.Items.Add("Abrir LotusLED Desk");
            itemOpen.Font = new Font(_trayMenu.Font, FontStyle.Bold);
            itemOpen.Click += (s, e) => RestoreWindow();

            _trayMenu.Items.Add("-");

            var itemOn = _trayMenu.Items.Add("Encender luces");
            itemOn.Click += (s, e) =>
            {
                _settings.Power = true;
                _ble.SetPower(true);
                _ble.SetColor(_settings.ColorR, _settings.ColorG, _settings.ColorB);
                UpdatePowerButtonVisual();
                _settings.Save();
            };

            var itemOff = _trayMenu.Items.Add("Apagar luces");
            itemOff.Click += (s, e) =>
            {
                _settings.Power = false;
                _ble.SetPower(false);
                UpdatePowerButtonVisual();
                _settings.Save();
            };

            _trayMenu.Items.Add("-");

            var itemExit = _trayMenu.Items.Add("Cerrar completamente");
            itemExit.Click += (s, e) =>
            {
                _realExit = true;
                Application.Exit();
            };

            _trayIcon = new NotifyIcon
            {
                Icon = Icon,
                Text = "LotusLED Desk",
                ContextMenuStrip = _trayMenu,
                Visible = true
            };
            _trayIcon.DoubleClick += (s, e) => RestoreWindow();
        }

        private void RestoreWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            ShowInTaskbar = true;
            BringToFront();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Si el PC se está apagando
            if (_isSystemShutdown || e.CloseReason == CloseReason.WindowsShutDown)
            {
                if (_settings.PowerOffShutdown)
                {
                    _ble.SetPower(false);
                    System.Threading.Thread.Sleep(250);
                }
                base.OnFormClosing(e);
                return;
            }

            // Si el usuario hizo clic en la 'X' de la ventana
            if (!_realExit && _settings.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                ShowInTaskbar = false;
                _trayIcon.ShowBalloonTip(2000, "LotusLED Desk", "El programa sigue en segundo plano. Las luces se apagarán cuando apagues el PC.", ToolTipIcon.Info);
                return;
            }

            base.OnFormClosing(e);
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            _isSystemShutdown = true;
            if (_settings.PowerOffShutdown)
            {
                _ble.SetPower(false);
                System.Threading.Thread.Sleep(300);
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_QUERYENDSESSION = 0x0011;
            const int WM_ENDSESSION = 0x0016;

            if (m.Msg == WM_QUERYENDSESSION || m.Msg == WM_ENDSESSION)
            {
                _isSystemShutdown = true;
                if (_settings.PowerOffShutdown)
                {
                    _ble.SetPower(false);
                    System.Threading.Thread.Sleep(250);
                }
            }
            base.WndProc(ref m);
        }

        private void SetWindowsStartup(bool enable)
        {
            try
            {
                string runKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(runKey, true))
                {
                    if (enable)
                    {
                        string exePath = Application.ExecutablePath;
                        key.SetValue("LotusLED", "\"" + exePath + "\" -startup");
                    }
                    else
                    {
                        key.DeleteValue("LotusLED", false);
                    }
                }
            }
            catch { }
        }

        private class EffectItem
        {
            public byte Id { get; private set; }
            public string Name { get; private set; }
            public EffectItem(byte id, string name) { Id = id; Name = name; }
            public override string ToString() { return Name; }
        }
    }
}
