using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using NetMonitor.Models;
using NetMonitor.Services;

namespace NetMonitor.ViewModels
{
    /// <summary>Uma linha da lista de aplicativos (aba Atividade ou Ocultos).</summary>
    public sealed class AppRowViewModel : ObservableObject
    {
        private AppIdentity? _identity;
        private long _download, _upload, _downRate, _upRate;
        private double _share;
        private DateTime _lastSeenUtc;
        private string _lastSeenText = "—";
        private int _hostCount;
        private bool _isBlocked;
        private string _pidsText = "";

        public AppRowViewModel(string id, string displayName, string? exePath)
        {
            Id = id;
            DisplayName = displayName;
            ExePath = exePath;
            Icon = IconCache.Get(exePath);
        }

        public string Id { get; }
        public string DisplayName { get; private set; }
        public string? ExePath { get; }
        public ImageSource? Icon { get; }
        public AppIdentity? Identity => _identity;

        public string Subtitle =>
            _identity?.Company is { } company ? $"{company} — {ExePath}" : ExePath ?? "";

        public bool IsWindowsComponent => _identity?.IsWindowsComponent ?? false;
        public bool CanBlock => !string.IsNullOrEmpty(ExePath);

        public long Download { get => _download; private set { if (Set(ref _download, value)) { Raise(nameof(DownloadText)); Raise(nameof(Total)); Raise(nameof(TotalText)); } } }
        public long Upload { get => _upload; private set { if (Set(ref _upload, value)) { Raise(nameof(UploadText)); Raise(nameof(Total)); Raise(nameof(TotalText)); } } }
        public long Total => _download + _upload;
        public string DownloadText => Format.Bytes(_download);
        public string UploadText => Format.Bytes(_upload);
        public string TotalText => Format.Bytes(Total);

        public long CurrentRate => _downRate + _upRate;
        public string RateText => CurrentRate <= 0 ? "—" : $"↓ {Format.Rate(_downRate)}   ↑ {Format.Rate(_upRate)}";

        /// <summary>Participação no tráfego total do intervalo (0..1), usada na barrinha.</summary>
        public double Share { get => _share; set => Set(ref _share, value); }

        public DateTime LastSeenUtc { get => _lastSeenUtc; private set => Set(ref _lastSeenUtc, value); }
        public string LastSeenText { get => _lastSeenText; private set => Set(ref _lastSeenText, value); }
        public int HostCount { get => _hostCount; private set => Set(ref _hostCount, value); }
        public string PidsText { get => _pidsText; private set => Set(ref _pidsText, value); }

        public bool IsBlocked
        {
            get => _isBlocked;
            set { if (Set(ref _isBlocked, value)) Raise(nameof(BlockButtonText)); }
        }

        public string BlockButtonText => IsBlocked ? "Desbloquear" : "Bloquear";

        public ObservableCollection<HostRowViewModel> Hosts { get; } = new();

        public void Update(AppSnapshot? s, DateTime nowUtc, DnsNameCache dns, bool includeHosts)
        {
            if (s == null)
            {
                Download = Upload = 0;
                _downRate = _upRate = 0;
                Raise(nameof(CurrentRate));
                Raise(nameof(RateText));
                HostCount = 0;
                Hosts.Clear();
                return;
            }

            if (_identity == null || _identity.DisplayName != s.App.DisplayName)
            {
                _identity = s.App;
                DisplayName = s.App.DisplayName;
                Raise(nameof(DisplayName));
                Raise(nameof(Subtitle));
                Raise(nameof(IsWindowsComponent));
            }

            Download = s.Received;
            Upload = s.Sent;
            _downRate = s.ReceiveRate;
            _upRate = s.SendRate;
            Raise(nameof(CurrentRate));
            Raise(nameof(RateText));
            LastSeenUtc = s.LastSeen;
            LastSeenText = Format.Ago(s.LastSeen, nowUtc);
            HostCount = s.Hosts.Count;
            PidsText = string.Join(", ", s.Pids);

            // Lista de hosts: reconstrói (é pequena e só aparece para o item selecionado).
            if (!includeHosts)
                return;
            Hosts.Clear();
            foreach (var h in s.Hosts.Take(200))
                Hosts.Add(new HostRowViewModel(h, dns.TryGetName(h.Address), nowUtc));
        }
    }

    public sealed class HostRowViewModel
    {
        public HostRowViewModel(HostSnapshot h, string? name, DateTime nowUtc)
        {
            Address = h.Address.IsIPv4MappedToIPv6 ? h.Address.MapToIPv4().ToString() : h.Address.ToString();
            HostName = name ?? (DnsNameCache.IsPrivate(h.Address) ? "(rede local)" : "");
            Port = h.LastPort;
            DownloadText = Format.Bytes(h.Received);
            UploadText = Format.Bytes(h.Sent);
            LastSeenText = Format.Ago(h.LastSeen, nowUtc);
        }

        public string Address { get; }
        public string HostName { get; }
        public int Port { get; }
        public string DownloadText { get; }
        public string UploadText { get; }
        public string LastSeenText { get; }
    }

    public sealed class BlockedRowViewModel
    {
        public BlockedRowViewModel(BlockedApp b)
        {
            AppId = b.AppId;
            DisplayName = b.DisplayName;
            ExePath = b.ExePath;
            Service = b.Service ?? "";
            BlockedAtText = b.BlockedAt?.ToString("dd/MM/yyyy HH:mm") ?? "";
            Icon = IconCache.Get(b.ExePath);
        }

        public string AppId { get; }
        public string DisplayName { get; }
        public string ExePath { get; }
        public string Service { get; }
        public string BlockedAtText { get; }
        public ImageSource? Icon { get; }
    }

    public sealed record IntervalOption(string Label, int Minutes);
}
