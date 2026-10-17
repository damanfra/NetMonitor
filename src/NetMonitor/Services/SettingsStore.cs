using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NetMonitor.Services
{
    public sealed class HiddenApp
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? ExePath { get; set; }
        public DateTime HiddenAt { get; set; }
    }

    public sealed class AppSettings
    {
        public int IntervalMinutes { get; set; } = 5;
        public bool HideWindowsComponents { get; set; }
        public bool HideMicrosoftApps { get; set; }
        public bool CloseConnectionsOnBlock { get; set; } = true;
        public bool MinimizeToTray { get; set; } = true;
        public List<HiddenApp> HiddenApps { get; set; } = new();
    }

    /// <summary>Configurações em %APPDATA%\NetMonitor\settings.json.</summary>
    public static class SettingsStore
    {
        private static readonly string Folder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetMonitor");

        private static readonly string FilePath = Path.Combine(Folder, "settings.json");

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
            }
            catch
            {
                // arquivo corrompido: começa do zero
            }
            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
                File.Move(tmp, FilePath, true);
            }
            catch
            {
                // sem permissão de escrita: segue com as configurações em memória
            }
        }
    }
}
