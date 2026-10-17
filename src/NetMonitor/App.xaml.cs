using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using NetMonitor.Localization;
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
            if (!isFirst && e.Args.Contains(UpdateService.UpdatedArgument, StringComparer.OrdinalIgnoreCase))
            {
                // Reiniciado por uma auto-atualização: espera a versão antiga terminar de fechar.
                try { isFirst = _instanceMutex.WaitOne(TimeSpan.FromSeconds(30)); }
                catch (AbandonedMutexException) { isFirst = true; }
            }
            if (!isFirst)
            {
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); }
                catch { /* a outra instância ainda está iniciando */ }
                Shutdown();
                return;
            }

            var settings = SettingsStore.Load();
            Loc.Instance.SetLanguage(settings.Language);
            var store = new TrafficStore();
            var resolver = new ProcessResolver();
            var dns = new DnsNameCache();
            var firewall = new FirewallManager();
            var vm = new MainViewModel(store, resolver, dns, firewall, settings);

            if (!EtwTrafficMonitor.IsElevated)
            {
                vm.ErrorBanner = Loc.T("Error_NotElevated");
            }
            else
            {
                try
                {
                    _monitor = new EtwTrafficMonitor(store, resolver, dns);
                    _monitor.Failed += ex => Dispatcher.BeginInvoke(() => vm.ErrorBanner = Loc.T("Error_MonitorStopped", ex.Message));
                    _monitor.Start();
                }
                catch (Exception ex)
                {
                    vm.ErrorBanner = Loc.T("Error_MonitorStart", ex.Message);
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
            Task.Run(UpdateService.CleanupOldVersion);
            if (settings.CheckUpdatesOnStartup)
                vm.CheckForUpdatesInBackground();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _monitor?.Dispose();
            _instanceMutex?.Dispose();
            base.OnExit(e);
        }
    }
}
