using System;
using System.Windows;
using NetMonitor.Services;
using NetMonitor.ViewModels;

namespace NetMonitor
{
    public partial class App : Application
    {
        private EtwTrafficMonitor? _monitor;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var settings = SettingsStore.Load();
            var store = new TrafficStore();
            var resolver = new ProcessResolver();
            var dns = new DnsNameCache();
            var firewall = new FirewallManager();
            var vm = new MainViewModel(store, resolver, dns, firewall, settings);

            if (!EtwTrafficMonitor.IsElevated)
            {
                vm.ErrorBanner = "O NetMonitor precisa ser executado como Administrador para monitorar o tráfego e criar regras de firewall.";
            }
            else
            {
                try
                {
                    _monitor = new EtwTrafficMonitor(store, resolver, dns);
                    _monitor.Failed += ex => Dispatcher.BeginInvoke(() => vm.ErrorBanner = "O monitoramento parou: " + ex.Message);
                    _monitor.Start();
                }
                catch (Exception ex)
                {
                    vm.ErrorBanner = "Não foi possível iniciar o monitoramento (ETW): " + ex.Message;
                }
            }

            var window = new MainWindow(vm, settings);
            MainWindow = window;
            window.Show();
            vm.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _monitor?.Dispose();
            base.OnExit(e);
        }
    }
}
