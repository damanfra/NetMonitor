using System;
using System.ComponentModel;
using System.Windows;
using NetMonitor.Services;
using NetMonitor.ViewModels;
using Forms = System.Windows.Forms;

namespace NetMonitor
{
    public partial class MainWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly Forms.NotifyIcon _tray;

        public MainWindow(MainViewModel viewModel, AppSettings settings)
        {
            InitializeComponent();
            DataContext = viewModel;
            _settings = settings;

            // Ícone na bandeja: minimizar não interrompe o monitoramento.
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Abrir NetMonitor", null, (_, _) => RestoreFromTray());
            menu.Items.Add("Sair", null, (_, _) => Close());
            _tray = new Forms.NotifyIcon
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Shield,
                Text = "NetMonitor",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _tray.DoubleClick += (_, _) => RestoreFromTray();
            _tray.BalloonTipClicked += (_, _) => RestoreFromTray();

            viewModel.NotificationRequested += (title, text) =>
                _tray.ShowBalloonTip(10000, title, text, Forms.ToolTipIcon.Warning);
        }

        /// <summary>Usado quando o app é iniciado pelo Windows (--minimized): fica só na bandeja.</summary>
        public void StartInTray()
        {
            ShowInTaskbar = false;
            WindowState = WindowState.Minimized;
            Show();
            Hide();
            ShowInTaskbar = true;
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized && _settings.MinimizeToTray)
                Hide();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            _tray.Visible = false;
            _tray.Dispose();
        }

        public void RestoreFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            Topmost = true;  // traz para frente mesmo se outra janela tiver o foco
            Topmost = false;
        }
    }
}
