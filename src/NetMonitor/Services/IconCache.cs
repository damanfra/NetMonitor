using System;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetMonitor.Interop;

namespace NetMonitor.Services
{
    /// <summary>Ícone do executável como ImageSource WPF (com cache por caminho).</summary>
    public static class IconCache
    {
        private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

        public static ImageSource? Get(string? exePath)
        {
            if (string.IsNullOrEmpty(exePath))
                return null;
            return Cache.GetOrAdd(exePath, Load);
        }

        private static ImageSource? Load(string path)
        {
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon == null)
                    return null;
                var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(32, 32));
                source.Freeze();
                return source;
            }
            catch
            {
                return null;
            }
        }
    }
}
