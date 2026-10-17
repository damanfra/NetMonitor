using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using NetMonitor.Services;
using NetMonitor.ViewModels;

namespace NetMonitor
{
    public partial class App : Application
    {
        private const string InstanceMutexName = "Global\\NetMonitor.SingleInstance";
        private const string ShowEventName = "Global\\NetMonitor.ShowWindow";

        private EtwTrafficMonitor? _monitor;
        private Mutex? _instanceMutex;
        private EventWaitHandle? _showEvent;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Uma única instância: duas sessões ETW com o mesmo nome se derrubariam.
            // Se já houver uma aberta, pede para ela aparecer e sai.
            _instanceMutex = new Mutex(true, InstanceMutexName, out var isFirst);
            if (!isFirst)
            {
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); }
                catch { /* a outra instância ainda está iniciando */ }
                Shutdown();
                return;
            }

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
            if (e.Args.Contains(StartupManager.MinimizedArgument, StringComparer.OrdinalIgnoreCase))
                window.StartInTray();
            else
                window.Show();
            vm.Start();

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var showEvent = _showEvent;
            new Thread(() =>
            {
                while (true)
                {
                    try { showEvent.WaitOne(); }
                    catch { return; }
                    Dispatcher.BeginInvoke(window.RestoreFromTray);
                }
            })
            { IsBackground = true, Name = "SingleInstance" }.Start();

            // Se o .exe foi movido/atualizado, mantém a tarefa de inicialização apontando para ele.
            Task.Run(StartupManager.RefreshIfEnabled);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _monitor?.Dispose();
            _instanceMutex?.Dispose();
            base.OnExit(e);
        }
    }
}
