using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using NetMonitor.Services;

namespace NetMonitor.ViewModels
{
    public sealed class MainViewModel : ObservableObject
    {
        private readonly TrafficStore _store;
        private readonly ProcessResolver _resolver;
        private readonly DnsNameCache _dns;
        private readonly FirewallManager _firewall;
        private readonly AppSettings _settings;
        private readonly DispatcherTimer _timer;
        private readonly Dictionary<string, AppRowViewModel> _activityById = new();
        private readonly Dictionary<string, AppRowViewModel> _hiddenById = new();
        private HashSet<string> _blockedIds = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastBlockedRefresh = DateTime.MinValue;
        private DateTime _lastPrune = DateTime.UtcNow;

        private IntervalOption _selectedInterval;
        private string _searchText = "";
        private AppRowViewModel? _selectedRow;
        private string _statusText = "Iniciando…";
        private string? _errorBanner;
        private bool _firewallDisabled;
        private string _totalDownText = "0 B", _totalUpText = "0 B", _rateText = "";

        public MainViewModel(TrafficStore store, ProcessResolver resolver, DnsNameCache dns, FirewallManager firewall,
            AppSettings settings)
        {
            _store = store;
            _resolver = resolver;
            _dns = dns;
            _firewall = firewall;
            _settings = settings;

            Intervals = new List<IntervalOption>
            {
                new("Último 1 minuto", 1),
                new("Últimos 5 minutos", 5),
                new("Últimos 10 minutos", 10),
                new("Últimos 15 minutos", 15),
                new("Últimos 30 minutos", 30),
                new("Última 1 hora", 60),
                new("Últimas 3 horas", 180),
                new("Últimas 24 horas", 1440),
            };
            _selectedInterval = Intervals.FirstOrDefault(i => i.Minutes == settings.IntervalMinutes) ?? Intervals[1];

            ConfigureLiveSorting(CollectionViewSource.GetDefaultView(ActivityRows));
            ConfigureLiveSorting(CollectionViewSource.GetDefaultView(HiddenRows));

            ToggleBlockCommand = new RelayCommand(p => ToggleBlock(p as AppRowViewModel),
                p => p is AppRowViewModel r && r.CanBlock);
            UnblockCommand = new RelayCommand(p => Unblock(p as BlockedRowViewModel));
            UnblockAllCommand = new RelayCommand(_ => UnblockAll(), _ => BlockedRows.Count > 0);
            HideCommand = new RelayCommand(p => Hide(p as AppRowViewModel), p => p is AppRowViewModel);
            UnhideCommand = new RelayCommand(p => Unhide(p as AppRowViewModel), p => p is AppRowViewModel);
            OpenLocationCommand = new RelayCommand(p => OpenLocation((p as AppRowViewModel)?.ExePath),
                p => p is AppRowViewModel { ExePath: not null });
            CopyPathCommand = new RelayCommand(p => { if (p is AppRowViewModel { ExePath: { } path }) Clipboard.SetText(path); },
                p => p is AppRowViewModel { ExePath: not null });
            OpenFirewallCommand = new RelayCommand(_ => Shell("wf.msc"));

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Refresh();
        }

        public ObservableCollection<AppRowViewModel> ActivityRows { get; } = new();
        public ObservableCollection<AppRowViewModel> HiddenRows { get; } = new();
        public ObservableCollection<BlockedRowViewModel> BlockedRows { get; } = new();
        public IReadOnlyList<IntervalOption> Intervals { get; }

        public ICommand ToggleBlockCommand { get; }
        public ICommand UnblockCommand { get; }
        public ICommand UnblockAllCommand { get; }
        public ICommand HideCommand { get; }
        public ICommand UnhideCommand { get; }
        public ICommand OpenLocationCommand { get; }
        public ICommand CopyPathCommand { get; }
        public ICommand OpenFirewallCommand { get; }

        public IntervalOption SelectedInterval
        {
            get => _selectedInterval;
            set
            {
                if (Set(ref _selectedInterval, value))
                {
                    _settings.IntervalMinutes = value.Minutes;
                    SettingsStore.Save(_settings);
                    Refresh();
                }
            }
        }

        public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) Refresh(); } }

        public bool HideWindowsComponents
        {
            get => _settings.HideWindowsComponents;
            set { _settings.HideWindowsComponents = value; SettingsStore.Save(_settings); Raise(nameof(HideWindowsComponents)); Refresh(); }
        }

        public bool HideMicrosoftApps
        {
            get => _settings.HideMicrosoftApps;
            set { _settings.HideMicrosoftApps = value; SettingsStore.Save(_settings); Raise(nameof(HideMicrosoftApps)); Refresh(); }
        }

        public bool CloseConnectionsOnBlock
        {
            get => _settings.CloseConnectionsOnBlock;
            set { _settings.CloseConnectionsOnBlock = value; SettingsStore.Save(_settings); Raise(nameof(CloseConnectionsOnBlock)); }
        }

        public AppRowViewModel? SelectedRow
        {
            get => _selectedRow;
            set { if (Set(ref _selectedRow, value)) Refresh(); }
        }

        public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
        public string? ErrorBanner { get => _errorBanner; set { Set(ref _errorBanner, value); Raise(nameof(HasError)); } }
        public bool HasError => !string.IsNullOrEmpty(_errorBanner);
        public bool FirewallDisabled { get => _firewallDisabled; private set => Set(ref _firewallDisabled, value); }
        public string TotalDownText { get => _totalDownText; private set => Set(ref _totalDownText, value); }
        public string TotalUpText { get => _totalUpText; private set => Set(ref _totalUpText, value); }
        public string RateText { get => _rateText; private set => Set(ref _rateText, value); }

        public string ActivityTabHeader => $"Atividade ({ActivityRows.Count})";
        public string BlockedTabHeader => $"Bloqueados ({BlockedRows.Count})";
        public string HiddenTabHeader => $"Ocultos ({_settings.HiddenApps.Count})";

        public void Start()
        {
            RefreshBlocked();
            Refresh();
            _timer.Start();
        }

        private static void ConfigureLiveSorting(ICollectionView view)
        {
            view.SortDescriptions.Add(new SortDescription(nameof(AppRowViewModel.Total), ListSortDirection.Descending));
            if (view is ICollectionViewLiveShaping live && live.CanChangeLiveSorting)
            {
                foreach (var p in new[]
                         {
                             nameof(AppRowViewModel.Total), nameof(AppRowViewModel.Download), nameof(AppRowViewModel.Upload),
                             nameof(AppRowViewModel.CurrentRate), nameof(AppRowViewModel.LastSeenUtc),
                             nameof(AppRowViewModel.DisplayName), nameof(AppRowViewModel.HostCount),
                         })
                    live.LiveSortingProperties.Add(p);
                live.IsLiveSorting = true;
            }
        }

        private void Refresh()
        {
            var now = DateTime.UtcNow;
            if (now - _lastPrune > TimeSpan.FromMinutes(1))
            {
                _store.Prune(now);
                _lastPrune = now;
            }
            if (now - _lastBlockedRefresh > TimeSpan.FromSeconds(15))
                RefreshBlocked();

            var snapshots = _store.Query(TimeSpan.FromMinutes(SelectedInterval.Minutes), now);
            var hidden = new HashSet<string>(_settings.HiddenApps.Select(h => h.Id), StringComparer.OrdinalIgnoreCase);

            long totalDown = snapshots.Sum(s => s.Received);
            long totalUp = snapshots.Sum(s => s.Sent);
            TotalDownText = Format.Bytes(totalDown);
            TotalUpText = Format.Bytes(totalUp);
            RateText = $"↓ {Format.Rate(snapshots.Sum(s => s.ReceiveRate))}   ↑ {Format.Rate(snapshots.Sum(s => s.SendRate))}";
            var grandTotal = Math.Max(1, totalDown + totalUp);

            // --- Aba Atividade ---
            var search = SearchText.Trim();
            var visible = snapshots.Where(s =>
                    !hidden.Contains(s.App.Id)
                    && !(HideWindowsComponents && s.App.IsWindowsComponent)
                    && !(HideMicrosoftApps && s.App.IsMicrosoft)
                    && (search.Length == 0
                        || s.App.DisplayName.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                        || (s.App.ExePath?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)))
                .ToDictionary(s => s.App.Id);

            Sync(ActivityRows, _activityById, visible, grandTotal, now,
                id => new AppRowViewModel(id, visible[id].App.DisplayName, visible[id].App.ExePath), removeMissing: true);

            // --- Aba Ocultos: todos os ocultos, com o tráfego do intervalo quando houver ---
            var bySnapshotId = snapshots.ToDictionary(s => s.App.Id);
            var hiddenSnapshots = _settings.HiddenApps.ToDictionary(h => h.Id,
                h => bySnapshotId.GetValueOrDefault(h.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var h in _settings.HiddenApps)
            {
                if (!_hiddenById.ContainsKey(h.Id))
                {
                    var row = new AppRowViewModel(h.Id, h.DisplayName, h.ExePath);
                    _hiddenById[h.Id] = row;
                    HiddenRows.Add(row);
                }
            }
            foreach (var id in _hiddenById.Keys.Where(id => !hiddenSnapshots.ContainsKey(id)).ToList())
            {
                HiddenRows.Remove(_hiddenById[id]);
                _hiddenById.Remove(id);
            }
            foreach (var (id, row) in _hiddenById)
            {
                var s = hiddenSnapshots[id];
                row.Update(s, now, _dns, row == SelectedRow);
                row.Share = s == null ? 0 : (double)(s.Sent + s.Received) / grandTotal;
                row.IsBlocked = _blockedIds.Contains(id);
            }

            Raise(nameof(ActivityTabHeader));
            Raise(nameof(HiddenTabHeader));
            StatusText = $"{ActivityRows.Count} app(s) com atividade em: {SelectedInterval.Label.ToLowerInvariant()} — atualizado às {DateTime.Now:HH:mm:ss}";
        }

        private void Sync(ObservableCollection<AppRowViewModel> rows, Dictionary<string, AppRowViewModel> byId,
            Dictionary<string, AppSnapshot> snapshots, long grandTotal, DateTime now,
            Func<string, AppRowViewModel> create, bool removeMissing)
        {
            if (removeMissing)
            {
                foreach (var id in byId.Keys.Where(id => !snapshots.ContainsKey(id)).ToList())
                {
                    rows.Remove(byId[id]);
                    byId.Remove(id);
                }
            }

            foreach (var (id, s) in snapshots)
            {
                if (!byId.TryGetValue(id, out var row))
                {
                    row = create(id);
                    byId[id] = row;
                    rows.Add(row);
                }
                row.Update(s, now, _dns, row == SelectedRow);
                row.Share = (double)(s.Sent + s.Received) / grandTotal;
                row.IsBlocked = _blockedIds.Contains(id);
            }
        }

        private void RefreshBlocked()
        {
            _lastBlockedRefresh = DateTime.UtcNow;
            try
            {
                var blocked = _firewall.GetBlockedApps();
                _blockedIds = new HashSet<string>(blocked.Select(b => b.AppId), StringComparer.OrdinalIgnoreCase);
                BlockedRows.Clear();
                foreach (var b in blocked)
                    BlockedRows.Add(new BlockedRowViewModel(b));
                FirewallDisabled = !_firewall.IsFirewallEnabledForCurrentProfiles();
            }
            catch (Exception ex)
            {
                StatusText = "Não foi possível ler as regras do Firewall: " + ex.Message;
            }
            Raise(nameof(BlockedTabHeader));
        }

        private void ToggleBlock(AppRowViewModel? row)
        {
            if (row == null)
                return;
            if (row.IsBlocked)
            {
                UnblockById(row.Id, row.DisplayName);
                return;
            }

            var identity = row.Identity;
            if (identity == null || identity.ExePath == null)
                return;

            var warning = identity.IsWindowsComponent
                ? "\n\n⚠ ATENÇÃO: este é um componente do Windows. Bloqueá-lo pode afetar atualizações, " +
                  "resolução de nomes (DNS), horário, rede local etc. Se algo parar de funcionar, " +
                  "desbloqueie na aba \"Bloqueados\"."
                : "";
            var target = identity.Services.Count > 0
                ? $"Serviço(s): {string.Join(", ", identity.Services)}\nExecutável: {identity.ExePath}"
                : identity.ExePath;
            if (MessageBox.Show(
                    $"Bloquear todo o acesso à rede (entrada e saída) de:\n\n{identity.DisplayName}\n{target}" +
                    $"\n\nSerá criada uma regra no Firewall do Windows. Você pode desfazer a qualquer momento.{warning}",
                    "Bloquear aplicativo", MessageBoxButton.OKCancel,
                    identity.IsWindowsComponent ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            try
            {
                _firewall.Block(identity);
                var closed = 0;
                if (CloseConnectionsOnBlock && identity.Services.Count == 0)
                    closed = ConnectionKiller.CloseTcpConnections(_resolver.PidsFor(identity.Id));
                StatusText = $"\"{identity.DisplayName}\" bloqueado." + (closed > 0 ? $" {closed} conexão(ões) ativa(s) encerrada(s)." : "");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Falha ao criar a regra de firewall:\n" + ex.Message, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            RefreshBlocked();
            Refresh();
        }

        private void Unblock(BlockedRowViewModel? row)
        {
            if (row != null)
                UnblockById(row.AppId, row.DisplayName);
        }

        private void UnblockById(string id, string name)
        {
            try
            {
                _firewall.Unblock(id);
                StatusText = $"\"{name}\" desbloqueado.";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Falha ao remover a regra de firewall:\n" + ex.Message, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            RefreshBlocked();
            Refresh();
        }

        private void UnblockAll()
        {
            if (MessageBox.Show($"Remover o bloqueio de todos os {BlockedRows.Count} aplicativo(s)?", "Desbloquear todos",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            foreach (var row in BlockedRows.ToList())
            {
                try { _firewall.Unblock(row.AppId); }
                catch { /* segue com os demais */ }
            }
            StatusText = "Todos os bloqueios foram removidos.";
            RefreshBlocked();
            Refresh();
        }

        private void Hide(AppRowViewModel? row)
        {
            if (row == null || _settings.HiddenApps.Any(h => h.Id == row.Id))
                return;
            _settings.HiddenApps.Add(new HiddenApp
            {
                Id = row.Id, DisplayName = row.DisplayName, ExePath = row.ExePath, HiddenAt = DateTime.Now,
            });
            SettingsStore.Save(_settings);
            StatusText = $"\"{row.DisplayName}\" foi para a aba Ocultos.";
            Refresh();
        }

        private void Unhide(AppRowViewModel? row)
        {
            if (row == null)
                return;
            _settings.HiddenApps.RemoveAll(h => string.Equals(h.Id, row.Id, StringComparison.OrdinalIgnoreCase));
            SettingsStore.Save(_settings);
            StatusText = $"\"{row.DisplayName}\" voltou a ser exibido.";
            Refresh();
        }

        private static void OpenLocation(string? path)
        {
            if (path != null)
                Process.Start("explorer.exe", $"/select,\"{path}\"");
        }

        private static void Shell(string file)
        {
            try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
            catch { /* ignorado */ }
        }
    }
}
