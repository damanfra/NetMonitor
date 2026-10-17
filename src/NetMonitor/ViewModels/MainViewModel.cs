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
using NetMonitor.Localization;
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
        private DateTime _lastAlertCheck = DateTime.MinValue;
        private readonly Dictionary<string, DateTime> _lastAlertByApp = new(StringComparer.OrdinalIgnoreCase);
        private bool _startWithWindows;
        private int _selectedTabIndex;
        private UpdateInfo? _availableUpdate;
        private bool _isCheckingUpdate, _isInstallingUpdate;
        private double _updateProgress;
        private string _updateStatusText = "";

        private IntervalOption _selectedInterval;
        private string _searchText = "";
        private AppRowViewModel? _selectedRow;
        private string _statusText = Loc.T("Status_Starting");
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
                new(1),
                new(5),
                new(10),
                new(15),
                new(30),
                new(60),
                new(180),
                new(1440),
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
            OpenSettingsFolderCommand = new RelayCommand(_ => Shell(SettingsStore.Folder));
            TestNotificationCommand = new RelayCommand(_ => NotificationRequested?.Invoke(
                Loc.T("Notification_TestTitle"), Loc.T("Notification_TestBody")));

            CheckUpdatesCommand = new RelayCommand(_ => _ = CheckForUpdatesAsync(userInitiated: true),
                _ => !_isCheckingUpdate && !_isInstallingUpdate);
            InstallUpdateCommand = new RelayCommand(_ => _ = InstallUpdateAsync(),
                _ => _availableUpdate != null && !_isInstallingUpdate);
            OpenReleasePageCommand = new RelayCommand(_ => Shell(_availableUpdate?.PageUrl
                ?? $"https://github.com/{UpdateService.Repository}/releases"));
            ShowUpdateCommand = new RelayCommand(_ => SelectedTabIndex = SettingsTabIndex);

            try { _startWithWindows = StartupManager.IsEnabled(); }
            catch { _startWithWindows = false; }

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
        public ICommand OpenSettingsFolderCommand { get; }
        public ICommand TestNotificationCommand { get; }
        public ICommand CheckUpdatesCommand { get; }
        public ICommand InstallUpdateCommand { get; }
        public ICommand OpenReleasePageCommand { get; }
        public ICommand ShowUpdateCommand { get; }

        private const int SettingsTabIndex = 3;

        public int SelectedTabIndex { get => _selectedTabIndex; set => Set(ref _selectedTabIndex, value); }

        public string WindowTitle => Loc.T("Window_Title", UpdateService.CurrentVersion.ToString(3));

        // ---------- Atualizações ----------

        public bool CheckUpdatesOnStartup
        {
            get => _settings.CheckUpdatesOnStartup;
            set { _settings.CheckUpdatesOnStartup = value; SettingsStore.Save(_settings); Raise(nameof(CheckUpdatesOnStartup)); }
        }

        public bool UpdateAvailable => _availableUpdate != null;
        public string UpdateBadgeText => _availableUpdate == null ? "" : Loc.T("Update_Badge", _availableUpdate.Version.ToString(3));
        public bool IsInstallingUpdate { get => _isInstallingUpdate; private set => Set(ref _isInstallingUpdate, value); }
        public double UpdateProgress { get => _updateProgress; private set => Set(ref _updateProgress, value); }
        public string UpdateStatusText { get => _updateStatusText; private set => Set(ref _updateStatusText, value); }

        public void CheckForUpdatesInBackground() => _ = CheckForUpdatesAsync(userInitiated: false);

        private async System.Threading.Tasks.Task CheckForUpdatesAsync(bool userInitiated)
        {
            if (_isCheckingUpdate)
                return;
            _isCheckingUpdate = true;
            UpdateStatusText = Loc.T("Update_Checking");
            try
            {
                if (!userInitiated)
                    await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(5)); // não atrasar a abertura
                _availableUpdate = await UpdateService.CheckAsync();
                UpdateStatusText = _availableUpdate != null
                    ? Loc.T("Update_Available", _availableUpdate.Version.ToString(3))
                    : Loc.T("Update_UpToDate", DateTime.Now);
            }
            catch (Exception ex)
            {
                // Silencioso na verificação automática; só mostra o motivo na aba Configurações.
                UpdateStatusText = Loc.T("Update_CheckFailed", ex.Message);
            }
            finally
            {
                _isCheckingUpdate = false;
                Raise(nameof(UpdateAvailable));
                Raise(nameof(UpdateBadgeText));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private async System.Threading.Tasks.Task InstallUpdateAsync()
        {
            var update = _availableUpdate;
            if (update == null)
                return;
            if (MessageBox.Show(Loc.T("Update_Confirm", update.Version.ToString(3)), Loc.T("Update_ConfirmTitle"),
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            IsInstallingUpdate = true;
            UpdateStatusText = Loc.T("Update_Downloading");
            try
            {
                var progress = new Progress<double>(p =>
                {
                    UpdateProgress = p;
                    UpdateStatusText = Loc.T("Update_DownloadingPercent", p);
                });
                await UpdateService.ApplyAsync(update, progress);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                UpdateStatusText = Loc.T("Update_InstallFailed", ex.Message);
                MessageBox.Show(Loc.T("Update_InstallFailed", ex.Message), Loc.T("Common_Error"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsInstallingUpdate = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        /// <summary>Pedido de notificação na bandeja (título, texto). A janela é quem exibe.</summary>
        public event Action<string, string>? NotificationRequested;

        public string VersionText => Loc.T("Settings_Version", typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "?");
        public string SettingsFilePath => SettingsStore.FilePath;

        // ---------- Configurações ----------

        public bool StartWithWindows
        {
            get => _startWithWindows;
            set
            {
                if (value == _startWithWindows)
                    return;
                try
                {
                    if (value) StartupManager.Enable();
                    else StartupManager.Disable();
                    _startWithWindows = value;
                    StatusText = value
                        ? Loc.T("Status_StartupEnabled")
                        : Loc.T("Status_StartupDisabled");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Loc.T("Error_Startup", ex.Message), Loc.T("Common_Error"),
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                Raise(nameof(StartWithWindows));
                Raise(nameof(NotStartWithWindows));
            }
        }

        public bool NotStartWithWindows
        {
            get => !StartWithWindows;
            set { if (value) StartWithWindows = false; }
        }

        public IReadOnlyList<LanguageOption> Languages => Loc.Supported;

        public string LanguageCode
        {
            get => Loc.Instance.Culture.Name;
            set
            {
                if (string.Equals(value, _settings.Language, StringComparison.OrdinalIgnoreCase))
                    return;
                _settings.Language = value;
                SettingsStore.Save(_settings);
                Loc.Instance.SetLanguage(value);
                Raise(nameof(LanguageCode));
                Raise(nameof(VersionText));
                Raise(nameof(WindowTitle));
                Raise(nameof(UpdateBadgeText));
                Raise(nameof(Intervals));
                Refresh();
            }
        }

        public bool MinimizeToTray
        {
            get => _settings.MinimizeToTray;
            set { _settings.MinimizeToTray = value; SettingsStore.Save(_settings); Raise(nameof(MinimizeToTray)); }
        }

        public bool AlertsEnabled
        {
            get => _settings.AlertsEnabled;
            set { _settings.AlertsEnabled = value; SettingsStore.Save(_settings); Raise(nameof(AlertsEnabled)); }
        }

        public int AlertThresholdMb
        {
            get => _settings.AlertThresholdMb;
            set
            {
                _settings.AlertThresholdMb = Math.Max(1, value);
                SettingsStore.Save(_settings);
                _lastAlertByApp.Clear();
                Raise(nameof(AlertThresholdMb));
            }
        }

        public IntervalOption SelectedAlertWindow
        {
            get => Intervals.FirstOrDefault(i => i.Minutes == _settings.AlertWindowMinutes) ?? Intervals[1];
            set
            {
                _settings.AlertWindowMinutes = value.Minutes;
                SettingsStore.Save(_settings);
                _lastAlertByApp.Clear();
                Raise(nameof(SelectedAlertWindow));
            }
        }

        public bool AlertIncludeHidden
        {
            get => _settings.AlertIncludeHidden;
            set { _settings.AlertIncludeHidden = value; SettingsStore.Save(_settings); Raise(nameof(AlertIncludeHidden)); }
        }

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

        public string ActivityTabHeader => Loc.T("Tab_Activity", ActivityRows.Count);
        public string BlockedTabHeader => Loc.T("Tab_Blocked", BlockedRows.Count);
        public string HiddenTabHeader => Loc.T("Tab_Hidden", _settings.HiddenApps.Count);

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

            if (now - _lastAlertCheck >= TimeSpan.FromSeconds(5))
            {
                _lastAlertCheck = now;
                CheckAlerts(now, hidden);
            }

            Raise(nameof(ActivityTabHeader));
            Raise(nameof(HiddenTabHeader));
            StatusText = Loc.T("Status_Summary", ActivityRows.Count, SelectedInterval.Label.ToLower(Loc.Instance.Culture), DateTime.Now);
        }

        /// <summary>
        /// Avisa (uma vez por janela de tempo, por app) quando um app passa do limite configurado.
        /// Apps bloqueados e, por padrão, ocultos são ignorados.
        /// </summary>
        private void CheckAlerts(DateTime now, HashSet<string> hidden)
        {
            if (!_settings.AlertsEnabled || NotificationRequested == null)
                return;

            var window = TimeSpan.FromMinutes(_settings.AlertWindowMinutes);
            var threshold = (long)_settings.AlertThresholdMb * 1024 * 1024;
            foreach (var s in _store.Query(window, now))
            {
                var total = s.Sent + s.Received;
                if (total < threshold
                    || _blockedIds.Contains(s.App.Id)
                    || (!_settings.AlertIncludeHidden && hidden.Contains(s.App.Id))
                    || (_lastAlertByApp.TryGetValue(s.App.Id, out var last) && now - last < window))
                    continue;

                _lastAlertByApp[s.App.Id] = now;
                var label = Intervals.FirstOrDefault(i => i.Minutes == _settings.AlertWindowMinutes)?.Label.ToLower(Loc.Instance.Culture)
                            ?? Loc.T("Alert_LastMinutes", _settings.AlertWindowMinutes);
                NotificationRequested(
                    Loc.T("Alert_Title", s.App.DisplayName),
                    Loc.T("Alert_Body", Format.Bytes(total), label, Format.Bytes(s.Received), Format.Bytes(s.Sent)));
            }
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
                StatusText = Loc.T("Error_ReadRules", ex.Message);
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
                ? "\n\n" + Loc.T("Block_WindowsWarning")
                : "";
            var target = identity.Services.Count > 0
                ? Loc.T("Block_ServiceTarget", string.Join(", ", identity.Services), identity.ExePath)
                : identity.ExePath;
            if (MessageBox.Show(
                    Loc.T("Block_Confirm", identity.DisplayName, target) + warning,
                    Loc.T("Block_ConfirmTitle"), MessageBoxButton.OKCancel,
                    identity.IsWindowsComponent ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            try
            {
                _firewall.Block(identity);
                var closed = 0;
                if (CloseConnectionsOnBlock && identity.Services.Count == 0)
                    closed = ConnectionKiller.CloseTcpConnections(_resolver.PidsFor(identity.Id));
                StatusText = Loc.T("Status_Blocked", identity.DisplayName) + (closed > 0 ? " " + Loc.T("Status_ConnectionsClosed", closed) : "");
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Error_CreateRule", ex.Message), Loc.T("Common_Error"), MessageBoxButton.OK, MessageBoxImage.Error);
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
                StatusText = Loc.T("Status_Unblocked", name);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Error_RemoveRule", ex.Message), Loc.T("Common_Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            RefreshBlocked();
            Refresh();
        }

        private void UnblockAll()
        {
            if (MessageBox.Show(Loc.T("UnblockAll_Confirm", BlockedRows.Count), Loc.T("Blocked_UnblockAll"),
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            foreach (var row in BlockedRows.ToList())
            {
                try { _firewall.Unblock(row.AppId); }
                catch { /* segue com os demais */ }
            }
            StatusText = Loc.T("Status_AllUnblocked");
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
            StatusText = Loc.T("Status_Hidden", row.DisplayName);
            Refresh();
        }

        private void Unhide(AppRowViewModel? row)
        {
            if (row == null)
                return;
            _settings.HiddenApps.RemoveAll(h => string.Equals(h.Id, row.Id, StringComparison.OrdinalIgnoreCase));
            SettingsStore.Save(_settings);
            StatusText = Loc.T("Status_Unhidden", row.DisplayName);
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
